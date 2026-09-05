using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Lifestream.Movement;

public class FollowPath : IDisposable
{
    public bool MovementAllowed = true;
    public bool AlignCamera = false;
    public bool IgnoreDeltaY = true;
    public float Tolerance = 0.25f;
    public List<Vector3> waypointsInternal = [];
    public IReadOnlyList<Vector3> Waypoints => waypointsInternal;
    public int MaxWaypoints = 0;

    private OverrideCamera _camera = new();
    private OverrideMovement _movement = new();
    private long TimeoutAt = 0;

    /// <summary>
    /// 最後一次自動移動是不是「還沒走到就被收掉」（逾時、或偵測到 vnavmesh 自己在動）。
    /// </summary>
    /// <remarks>
    /// 🔴 那兩條失敗路徑都是把航點<b>清空</b>後就結束，從外面看跟「順利走完」<b>一模一樣</b>。
    /// 想在抵達時做事的人(<see cref="Tasks.Utility.TaskAnnounceArrival"/>)只看航點數會把失敗當成功。
    /// <para>每次 <see cref="Move"/> 都會清成 false —— 這個旗標只描述「最後一次」。</para>
    /// </remarks>
    public bool LastMovementFailed = false;

    // ── 卡住偵測與回復 ───────────────────────────────────────────────────────────
    // 🔴 這裡處理的是一個**只會靜默浪費 30 秒**的故障:vnavmesh 給的是折線路徑,而
    // 本類別是用 OverrideMovement 直線走向 waypointsInternal[0]。兩點之間若擦到欄杆、
    // 小石頭、地形接縫這種網格沒模到的小障礙物,角色就會**頂著障礙物原地推**,
    // 而且因為 Tolerance 只有 0.25 碼,永遠到不了那個航點。
    // 修改前唯一的出路是下面那個「每個航點 30 秒」的逾時 —— 也就是說使用者會看著角色
    // 原地卡半分鐘,然後收到一句 movement has timed out,整趟作廢。
    //
    // 📌 實機證據(台服 2026-08-26 12:12:58 紅玉海自訂落點):5 個航點的路徑,
    // 逾時在開走後 30.017 秒觸發。FollowPath 每消化掉一個航點就把 TimeoutAt 歸零,
    // 所以「整整 30 秒」代表**連第一個航點都沒到過** —— 從落地那一步就卡住了。
    // 同一個落點另外 4 次是成功的,所以這是間歇性的,不是座標算錯。
    //
    // ⚠️ 這裡刻意**不去修 vnavmesh 的網格** —— 障礙物有沒有被烘進網格是 vnavmesh 那邊的事。
    // 本類別能做的是「發現推不動就換個方式」,而且回復動作只在**已經卡住**時才會執行,
    // 也就是說在一個本來就已經要失敗的狀態下才動作:假設不成立最差也只是白跳一次/白算一次路徑。

    /// <summary>多久取樣一次位移。</summary>
    private const int StuckSampleIntervalMs = 500;

    /// <summary>兩次取樣之間水平位移小於這個值就算「沒有前進」。</summary>
    /// <remarks>
    /// 0.5 碼:正常走路一秒約 6 碼,即使被地形磨蹭著慢慢滑也遠超過這個值;
    /// 而真的頂在障礙物上時位移是 0。取這個值是為了與「轉身中」「上下坡減速」分開。
    /// </remarks>
    private const float StuckProgressThreshold = 0.5f;

    /// <summary>連續沒有前進超過這麼久才判定卡住。</summary>
    /// <remarks>
    /// ⚠️ 不能太短:上坐騎、衝刺、落地硬直、過場都會讓角色靜止一兩秒,
    /// 那些是正常的等待不是卡住。2.5 秒足以蓋過那些,又遠小於原本的 30 秒逾時。
    /// </remarks>
    private const int StuckTriggerMs = 2500;

    private Vector3 StuckRefPos;
    private long StuckSampleAt;
    private long StuckProgressAt;
    private int StuckRecoveries;
    private Vector3 FinalDestination;
    private bool HasFinalDestination;
    private Task<List<Vector3>>? RepathTask;

    public FollowPath()
    {
        Svc.ClientState.Login += OnLogin;
    }

    public void Dispose()
    {
        Svc.ClientState.Login -= OnLogin;
        _camera.Dispose();
        _movement.Dispose();
    }

    // Update() returns early while Player.Available is false (login/relog transition),
    // skipping the waypoint-clear branch below - so a path left over from an interrupted
    // task (e.g. a timed-out Walk_to_door) survives the transition and resumes immediately
    // toward a stale destination the instant the new character's Player becomes available,
    // fighting the player's own input right at login.
    private void OnLogin()
    {
        Stop();
        _movement.Enabled = _camera.Enabled = false;
    }

    public void UpdateTimeout(int seconds) => TimeoutAt = Environment.TickCount64 + seconds * 1000;


    public unsafe void Update()
    {
        if(!Player.Available)
            return;

        while(waypointsInternal.Count > 0)
        {
            if(waypointsInternal.Count > MaxWaypoints) MaxWaypoints = waypointsInternal.Count;
            if(TimeoutAt == 0) TimeoutAt = Environment.TickCount64 + 30000;
            if(S.Ipc.VnavmeshIPC.IsRunning())
            {
                waypointsInternal.Clear();
                LastMovementFailed = true;
                DuoLog.Error($"Detected vnavmesh movement, Lifestream will abort all tasks now.");
                break;
            }
            if(Environment.TickCount64 > TimeoutAt)
            {
                waypointsInternal.Clear();
                LastMovementFailed = true;
                DuoLog.Error($"Lifestream movement has timed out.");
                break;
            }
            var toNext = waypointsInternal[0] - Player.Object.Position;
            if(IgnoreDeltaY)
                toNext.Y = 0;
            if(toNext.LengthSquared() > Tolerance * Tolerance)
                break;
            waypointsInternal.RemoveAt(0);
            TimeoutAt = 0;
            MarkProgress();
        }

        if(waypointsInternal.Count > 0)
            UpdateStuckRecovery();

        if(waypointsInternal.Count == 0)
        {
            _movement.Enabled = _camera.Enabled = false;
            _camera.SpeedH = _camera.SpeedV = default;
            _movement.DesiredPosition = Player.Object.Position;
            MaxWaypoints = 0;
        }
        else
        {
            OverrideAFK.ResetTimers();
            _movement.Enabled = MovementAllowed;
            _movement.DesiredPosition = waypointsInternal[0];
            _camera.Enabled = AlignCamera;
            _camera.SpeedH = _camera.SpeedV = 360.Degrees();
            _camera.DesiredAzimuth = Angle.FromDirectionXZ(_movement.DesiredPosition - Player.Object.Position) + 180.Degrees();
            _camera.DesiredAltitude = -30.Degrees();
        }
    }

    /// <summary>
    /// 卡住偵測與回復。只在還有航點要走的時候呼叫。
    /// </summary>
    /// <remarks>
    /// 回復手段交替使用,理由是兩種卡法要的解法不同:
    /// <list type="bullet">
    /// <item><b>跳躍</b>(奇數次)—— 對小石頭、階梯邊緣、欄杆下緣這種「差一點就過得去」的
    /// 地形有效,而且成本極低(不中斷路徑、不重算)。</item>
    /// <item><b>從現在的位置重算路徑</b>(偶數次)—— 對「直線航段擦到牆角」有效:
    /// 重算的起點是**卡住的位置**,vnavmesh 會從這個多邊形重新拉線,得到的折線通常
    /// 會繞開原本擦到的那個角。</item>
    /// </list>
    /// ⚠️ 次數上限用完之後就**什麼都不做**,把場面交還給原本那個 30 秒逾時 ——
    /// 也就是說最壞情況與修改前完全相同,不會多卡、也不會無限重試。
    /// <br/><br/>
    /// 📌 診斷一律 <c>Information</c>:這是要請使用者回報的東西,而使用者跑 LogLevel 1,
    /// 盲區只有 <c>Verbose</c>,<c>Debug</c> 收得到但單檔數十萬行會淹沒。
    /// </remarks>
    private unsafe void UpdateStuckRecovery()
    {
        if(!C.MovementStuckRecovery) return;
        if(!MovementAllowed) return;

        // 上一次的重算還沒收尾就先收尾。沒完成之前不做任何判斷 —— 這段期間角色仍照舊路徑走,
        // 重算失敗也只是回到原本的狀態。
        if(RepathTask != null)
        {
            if(!RepathTask.IsCompleted) return;
            var finished = RepathTask;
            RepathTask = null;
            if(finished.IsCompletedSuccessfully && finished.Result != null && finished.Result.Count > 0)
            {
                waypointsInternal = [.. finished.Result];
                TimeoutAt = 0;
                PluginLog.Information($"[Lifestream] 卡住回復 #{StuckRecoveries}:重算完成,{finished.Result.Count} 個航點,續往 {FinalDestination:F2}。");
            }
            else
            {
                var why = finished.IsFaulted ? finished.Exception?.InnerException?.Message ?? "pathfind faulted" : "pathfind 回空路徑";
                PluginLog.Information($"[Lifestream] 卡住回復 #{StuckRecoveries}:重算沒有結果({why}),維持原路徑。");
            }
            MarkProgress();
            return;
        }

        var now = Environment.TickCount64;
        if(StuckSampleAt == 0)
        {
            MarkProgress();
            return;
        }
        if(now - StuckSampleAt < StuckSampleIntervalMs) return;
        StuckSampleAt = now;

        var pos = Player.Object.Position;
        if(Distance2D(pos, StuckRefPos) >= StuckProgressThreshold)
        {
            StuckRefPos = pos;
            StuckProgressAt = now;
            return;
        }

        var stuckMs = now - StuckProgressAt;
        if(stuckMs < StuckTriggerMs) return;

        // 次數用完:不再嘗試,交還給原本的 30 秒逾時(行為與修改前相同)。
        if(StuckRecoveries >= C.MovementStuckMaxRecoveries)
            return;

        StuckRecoveries++;
        var waypoint = waypointsInternal[0];

        if(StuckRecoveries % 2 == 1 && TryJump())
        {
            PluginLog.Information($"[Lifestream] 卡住回復 #{StuckRecoveries}:{stuckMs}ms 沒有前進。位置 {pos:F2},航點 {waypoint:F2}(剩 {waypointsInternal.Count} 個),終點 {FinalDestination:F2} ⇒ 跳躍。");
        }
        else
        {
            TryRepath(pos, waypoint, stuckMs);
        }

        MarkProgress();
    }

    /// <summary>
    /// 跳一下。<c>GeneralAction 2</c> 是跳躍 —— 同一份寫法已經在
    /// <see cref="Tasks.Utility.FlightTasks.FlyIfCan"/> 用來起飛,不是新猜的編號。
    /// </summary>
    /// <remarks>
    /// 🔴 用 <c>GetActionStatus</c> 問遊戲「現在能不能跳」而不是自己列條件:
    /// 戰鬥中、詠唱中、水裡、過場、騎乘中…全都由遊戲回答。回非 0 就不跳,
    /// 讓呼叫端改走重算路徑那條。
    /// <br/><br/>
    /// ⚠️ 指標每次重新取得,不跨幀保存。
    /// </remarks>
    private unsafe bool TryJump()
    {
        var am = ActionManager.Instance();
        if(am == null) return false;
        if(am->GetActionStatus(ActionType.GeneralAction, 2) != 0) return false;
        return am->UseAction(ActionType.GeneralAction, 2);
    }

    /// <summary>從目前(卡住的)位置重新算一次到終點的路徑。</summary>
    private void TryRepath(Vector3 from, Vector3 waypoint, long stuckMs)
    {
        if(!HasFinalDestination)
        {
            PluginLog.Information($"[Lifestream] 卡住回復 #{StuckRecoveries}:沒有記錄終點,略過重算。");
            return;
        }
        // ⚠️ IsReady() 回的是 bool?(vnavmesh 沒載入時是 null),要跟 true 比而不是直接當條件。
        if(S.Ipc.VnavmeshIPC.IsReady() != true)
        {
            PluginLog.Information($"[Lifestream] 卡住回復 #{StuckRecoveries}:vnavmesh 尚未就緒,略過重算。");
            return;
        }
        try
        {
            RepathTask = S.Ipc.VnavmeshIPC.Pathfind(from, FinalDestination, false);
            PluginLog.Information($"[Lifestream] 卡住回復 #{StuckRecoveries}:{stuckMs}ms 沒有前進。位置 {from:F2},航點 {waypoint:F2}(剩 {waypointsInternal.Count} 個),終點 {FinalDestination:F2} ⇒ 從現在位置重算路徑。");
        }
        catch(Exception e)
        {
            RepathTask = null;
            PluginLog.Information($"[Lifestream] 卡住回復 #{StuckRecoveries}:重算路徑呼叫失敗:{e.Message}");
        }
    }

    /// <summary>把「最後一次有進展」的時間與位置更新到現在。</summary>
    private void MarkProgress()
    {
        var now = Environment.TickCount64;
        StuckSampleAt = now;
        StuckProgressAt = now;
        StuckRefPos = Player.Available ? Player.Object.Position : default;
    }

    /// <summary>只比水平距離:高度變化(上下坡、跳躍)不算「往目的地前進」。</summary>
    private static float Distance2D(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    private void ResetStuckTracking()
    {
        RepathTask = null;
        StuckRecoveries = 0;
        StuckSampleAt = 0;
        StuckProgressAt = 0;
        StuckRefPos = default;
        HasFinalDestination = waypointsInternal.Count > 0;
        FinalDestination = HasFinalDestination ? waypointsInternal[^1] : default;
    }

    public void Stop()
    {
        waypointsInternal.Clear();
        ResetStuckTracking();
    }

    public void RemoveFirst() => waypointsInternal.RemoveAt(0);

    public void Move(List<Vector3> waypoints, bool ignoreDeltaY)
    {
        TimeoutAt = 0;
        LastMovementFailed = false;
        waypointsInternal = [.. waypoints];
        IgnoreDeltaY = ignoreDeltaY;
        ResetStuckTracking();
    }
}
