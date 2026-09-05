using ECommons.Automation.NeoTaskManager.Tasks;
using ECommons.ChatMethods;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lifestream.Schedulers;
using Lifestream.Systems;
using Lifestream.Systems.Custom;
using Lifestream.Systems.Legacy;
using Lifestream.Systems.TeleportPanel;
using Lifestream.Tasks.SameWorld;
using Lumina.Excel.Sheets;

namespace Lifestream.Tasks.Utility;

/// <summary>
/// 傳送面板按下目的地之後的整條流程。
///
/// 傳送本身一律走既有的安全路徑：
///   - 主水晶  → <see cref="Services.TeleportService.TeleportToAetheryte"/>(純 <c>Telepo::Teleport</c>)
///   - 城內乙太之光 → <see cref="TaskAetheryteAethernetTeleport"/>(傳送到主水晶 → 實際互動 → 選單)
/// 🔴 DailyRoutines 原版在這一段會自己組 <c>EventStartPackt</c> 送出封包來開乙太之光選單 ——
/// **封包偽造是紅線，沒有移植**。Lifestream 本來就有「真的走過去互動」的合法流程，直接用它。
///
/// 抵達後的「傳送到座標」是可選的，而且分成兩層開關(見 <see cref="Data.Config.EnableAetheryteLanding"/>
/// 與 <see cref="Data.Config.AetheryteLandingDirectWrite"/>)：
///   第一層(預設關)：走 vnavmesh 走過去 —— 跟 <c>/li goto</c> 同一套機制，沒有任何記憶體寫入。
///   第二層(預設關、需先開第一層)：直接寫座標瞬移。這是使用者自行承擔風險的選項。
///
/// 第一層底下還有一個純最佳化的旁支(<see cref="TryEnqueueLandingAethernetRelay"/>)：落點離同城某座
/// 城內乙太之光比離剛傳送到的主水晶近很多時，先搭一段都市傳送網再走完最後一段，
/// 免得在九號解決方案這種大城裡橫跨整張地圖用走的。判斷不成立就原封不動走上面那條路。
/// </summary>
public static unsafe class TaskTeleportPanelGo
{
    /// <summary>
    /// 入口包裝：真的排了任務就在尾端補「抵達提醒」哨兵（2026-08-31 使用者要求：
    /// 快捷傳送也要塔塔露通知）。三個入口（最愛視窗/傳送面板/IPC TeleportToFavorite）
    /// 都收斂到這裡，一處包三處生效。哨兵自帶設定開關、去重與
    /// 「沒排任務就不響」判斷，早退路徑（忙碌/無玩家）自然靜默。
    /// 用 try/finally 而不是在每個 return 前呼叫：未來新增早退路徑也自動正確。
    /// </summary>
    public static void Enqueue(TeleportPanelEntry entry)
    {
        var queuedBefore = P.TaskManager.NumQueuedTasks;
        try
        {
            EnqueueCore(entry);
        }
        finally
        {
            TaskAnnounceArrival.EnqueueIfChainStarted(queuedBefore);
        }
    }

    private static void EnqueueCore(TeleportPanelEntry entry)
    {
        if(entry == null) return;
        if(P.TaskManager.IsBusy)
        {
            DuoLog.Error("Lifestream is busy");
            return;
        }
        if(!Player.Interactable)
        {
            DuoLog.Error("Can't teleport - no player");
            return;
        }

        // DailyRoutines 把 BetterTeleport 宣告成依賴 SameAethernetTeleport。我們的版本**不是硬依賴**
        // (面板不開修補也完全能用)，但確實有一個情境會撞到：站在目標乙太之光旁邊時，遊戲會用
        // LogMessage 1478「此處為目前所在地。」拒絕傳送 —— 而那正是搭配自訂落點時最常做的動作
        // (重新傳送一次讓自己回到儲存的位置)。沒開修補就先講清楚，不要排一條註定沉默失敗的佇列。
        if(!entry.IsAetheryte && !SameAethernetTeleportPatch.IsApplied && IsStandingAt(entry))
        {
            DuoLog.Warning("You are already at this aethernet shard - the game refuses this teleport. See the Teleport Panel settings if you want to allow it.".Loc());
            return;
        }

        TaskRemoveAfkStatus.Enqueue();

        if(entry.IsAetheryte)
        {
            P.TaskManager.Enqueue(() => S.TeleportService.TeleportToAetheryte(entry.Id, entry.SubIndex),
                "TeleportPanelTeleport", new(timeLimitMS: 30000));
            P.TaskManager.Enqueue(Utils.WaitForScreenFalse);
            P.TaskManager.Enqueue(Utils.WaitForScreen);
        }
        else if(IsGatewayZoneShard(entry, out var gateway))
        {
            EnqueueGatewayZoneShardRoute(entry, gateway);
        }
        else
        {
            // 城內乙太之光。TaskAetheryteAethernetTeleport.Enqueue 是排到佇列尾端的，
            // 而落點步驟是在這行之後才排進去的,所以順序正確(這正是 TaskGotoDestination
            // 註解裡提醒過的 Enqueue/InsertMulti 陷阱,這裡是「可以用 Enqueue」的那一側)。
            //
            // 它在找不到主水晶/子節點時會丟例外。我們的 MasterId 直接來自 DataStore 的鍵，
            // 照理不會發生;但這是從 ImGui 的繪製回呼呼叫進來的 —— 這裡丟例外會打斷整個視窗的繪製,
            // 所以降級成聊天欄錯誤,並中止已經排進去的步驟,不要留下半條佇列。
            try
            {
                TaskAetheryteAethernetTeleport.Enqueue(entry.MasterId, entry.Id);
            }
            catch(Exception e)
            {
                PluginLog.Error($"[TeleportPanel] Could not enqueue aethernet teleport to {entry.Id} via {entry.MasterId}: {e.Message}");
                DuoLog.Error($"Could not reach {entry.DisplayName}");
                P.TaskManager.Abort();
                return;
            }
            // ⚠️ 路線若決定「用走的」(TaskAethernetRoute.RouteUsesAethernet=false)就根本不會有讀取畫面,
            // 直接放行,否則這裡會空轉滿 15 秒才往下走。
            P.TaskManager.Enqueue(
                () => !TaskAethernetRoute.RouteUsesAethernet || Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51],
                "TeleportPanelWaitTransition", new(timeLimitMS: 15000, abortOnTimeout: false));
            P.TaskManager.Enqueue(Utils.WaitForScreen);
        }

        EnqueueLanding(entry);
    }

    /// <summary>
    /// 這一列是不是「玄關區域**內部**的城內乙太之光」——目前只有蒼天街的那八座。
    ///
    /// 它們與一般城內乙太之光的差別在於 <see cref="TeleportPanelEntry.Id"/> 是 Lifestream 的偽 id
    /// (69420000 起)，<c>Aetheryte</c> 表裡查無此列，所以
    /// <see cref="TaskAetheryteAethernetTeleport.Enqueue"/> 那條一般路線走不通 ——
    /// 🔴 而且它的失敗方式是**靜默走錯**:找不到子節點時它會退化成「只傳送到母城主水晶」，
    /// 人被丟在伊修加爾德下層，蒼天街那一段完全沒發生，畫面上不會有任何錯誤。
    ///
    /// ⚠️ 用 <see cref="TeleportPanelEntry.Id"/> 而不是只看區域來判斷:玄關區域本身那一列
    /// (蒼天街/渴望灣，id 是 <c>uint.MaxValue</c> 往下數)的 Territory 也是玄關區域，
    /// 但它走的是一般路線(<c>TaskAetheryteAethernetTeleport</c> 認得那兩個偽 id)，不能混進來。
    /// </summary>
    private static bool IsGatewayZoneShard(TeleportPanelEntry entry,
        out TaskAetheryteAethernetTeleport.GatewayRoute gateway)
    {
        gateway = null;
        if(!TaskAetheryteAethernetTeleport.TryGetGatewayRouteByTerritory(entry.Territory, out var route)) return false;
        if(S.Data.CustomAethernet?.CustomAetheryteNames.ContainsKey(entry.Id) != true) return false;
        gateway = route;
        return true;
    }

    /// <summary>
    /// 前往蒼天街的某一座城內乙太之光。分三段：先用玄關路線進到該區域，落地後**需要的話走到最近的
    /// 那一座乙太之光**，再用區域內的乙太之光網路跳過去。
    ///
    /// 每一段都是既有的、已經上線過的流程，沒有新的原生層操作：
    ///   第一段 = <see cref="TaskAetheryteAethernetTeleport.Enqueue"/>(浮動視窗的「蒼天街」按鈕走的就是它)
    ///   第二段 = vnavmesh IPC 尋路 + <see cref="Movement.FollowPath"/>，與 <c>/li goto</c>、
    ///            自訂落點用的是同一套(<see cref="TaskGotoDestination.EnqueueNavTo"/>)
    ///   第三段 = 與 <see cref="SameWorld.TaskAethernetTeleport"/> 相同的四步(鎖定→互動→選單→選目的地)，
    ///            只是改用 <c>InsertMulti</c> 排在當下這一步的後面，因為它必須在**抵達之後**才決定要不要做。
    ///
    /// 🔴 第二段是 2026-08-09 使用者實測後補的：蒼天街的玄關落點離最近的「無名眾人廣場」超出互動範圍
    /// (該區域的 <see cref="Data.ZoneDetail.MaxInteractionDistance"/> 只有 4.56 碼)，
    /// 修改前這裡直接排下互動步驟，<see cref="WorldChange.TargetValidAetheryte"/> 永遠找不到東西可鎖定，
    /// 整條佇列就在原地空轉到逾時 —— **畫面上一行訊息都沒有**。
    /// </summary>
    private static void EnqueueGatewayZoneShardRoute(TeleportPanelEntry entry,
        TaskAetheryteAethernetTeleport.GatewayRoute gateway)
    {
        if(P.Territory != entry.Territory)
        {
            TaskAetheryteAethernetTeleport.Enqueue(gateway.RootAetheryteId, gateway.AethernetId);
            // 進去是一整段區域轉場。⚠️ 這一步刻意讓逾時中止整條佇列：沒到對的區域就開始找乙太之光，
            // 會在錯的地圖上鎖定錯的東西然後對它開選單。
            P.TaskManager.Enqueue(
                () => P.Territory == entry.Territory && Player.Interactable && !Svc.Condition[ConditionFlag.BetweenAreas],
                "TeleportPanelWaitGatewayArrival", new(timeLimitMS: 120000));
            P.TaskManager.Enqueue(Utils.WaitForScreen);
        }

        // 🔑 「還要不要再跳一次」**必須在執行期判斷**，不能在排佇列的當下決定：
        // 玄關傳送的落點就是該區域的入口乙太之光，使用者要去的如果正好是它，這時再開選單選自己
        // 會被遊戲以 LogMessage 1478「此處為目前所在地。」拒絕 —— 與本檔開頭那個前置檢查同一個理由，
        // 只是那個檢查是在出發前做的，管不到「傳送過去之後才站到目的地上」這種情形。
        P.TaskManager.Enqueue(() =>
        {
            if(IsStandingAt(entry))
            {
                PluginLog.Information($"[TeleportPanel] Arrived directly at {entry.Name} - no aethernet hop needed.");
                return;
            }

            // 落地就在某座乙太之光的互動範圍內 —— 直接跳，這就是修改前的行為。
            // 判準刻意用 GetValidAetheryte 而不是自己算距離：那正是下一步 TargetValidAetheryte /
            // InteractWithTargetedAetheryte 用的同一個函式(含該區域專屬的 MaxInteractionDistance)，
            // 所以它非 null 就代表互動那幾步一定摸得到東西。
            if(IsAetheryteInReach())
            {
                InsertAethernetHop(entry);
                return;
            }

            InsertWalkToNearestShard(entry);
        }, "TeleportPanelGatewayZoneHop");
        P.TaskManager.Enqueue(Utils.WaitForScreen);
    }

    /// <summary>
    /// 「互動 → 選單 → 選目的地」。抽成獨立方法是因為現在有兩個進入點：落地就在乙太之光旁邊，
    /// 以及走過去之後。兩邊必須排出**完全相同**的四步，不要各留一份。
    /// </summary>
    private static void InsertAethernetHop(TeleportPanelEntry entry)
    {
        P.TaskManager.InsertMulti(
            new(WorldChange.TargetValidAetheryte),
            new(WorldChange.InteractWithTargetedAetheryte),
            new(WorldChange.SelectAethernetIfNeeded),
            // ⚠️ 用 Name 不是 DisplayName：這個字串要跟遊戲選單上的項目比對，
            // 使用者的備註在這裡會讓它一個都對不上。
            new(() => WorldChange.TeleportToAethernetDestination(entry.Name),
                nameof(WorldChange.TeleportToAethernetDestination))
            );
    }

    /// <summary>
    /// 走去乙太之光的時限。蒼天街玄關落點到最近的那一座只有幾十碼，正常步行遠低於這個數字，
    /// 逾時多半是卡地形或路徑本身有問題。
    /// ⚠️ 這一步刻意 <c>abortOnTimeout: false</c>：逾時即中止會讓後面的抵達檢查**跑不到**，
    /// 使用者就又回到「什麼都沒發生」。改成讓檢查那一步一定跑得到，由它報錯並中止。
    /// </summary>
    private const int GatewayZoneWalkTimeLimitMS = 90000;

    /// <summary>
    /// 落地點離所有乙太之光都太遠時，用 vnavmesh 走到最近的那一座。
    ///
    /// 🔴 移動只走 vnavmesh IPC，Lifestream 這邊不自己接管走位 —— 路徑點交給既有的
    /// <see cref="Movement.FollowPath"/>(它自己會偵測 vnavmesh 同時在動並讓路)。
    /// 🔴 vnavmesh 不可用(沒裝／網格沒建好／找不到路)時一律**明確報錯並中止整條佇列**，
    /// 絕不留使用者在原地罰站等逾時 —— 那正是這次要修的症狀。
    /// </summary>
    private static void InsertWalkToNearestShard(TeleportPanelEntry entry)
    {
        if(!TryGetNearestShard(entry.Territory, out var shard, out var shardPos))
        {
            PluginLog.Error($"[TeleportPanel] Landed in territory {entry.Territory} out of interaction range of every aethernet shard, and none of them has a resolvable world position - cannot walk anywhere.");
            DuoLog.Error("Landed too far from every aethernet shard, and Lifestream could not work out where the nearest one is.".Loc());
            P.TaskManager.Abort();
            return;
        }

        var distance = DistanceXZ(shardPos, Player.Position);

        if(!Svc.PluginInterface.InstalledPlugins.Any(x => x.InternalName == "vnavmesh" && x.IsLoaded))
        {
            // 標點是額外的協助，不是替代方案 —— 佇列照樣中止，聊天欄也照樣有錯誤訊息。
            FlagOnMap(entry.Territory, shardPos);
            PluginLog.Error($"[TeleportPanel] Landed {distance:F0}y from the nearest aethernet shard \"{shard.Name}\" but vnavmesh is not installed - aborting.");
            DuoLog.Error($"{"Landed too far from the aethernet shards and vnavmesh is not available - please walk to this one yourself:".Loc()} {shard.Name}");
            P.TaskManager.Abort();
            return;
        }

        PluginLog.Information($"[TeleportPanel] Landed {distance:F0}y from the nearest aethernet shard \"{shard.Name}\" (interaction range here is {GetMaxInteractionDistance(entry.Territory):F1}y) - walking there with vnavmesh before taking the aethernet to \"{entry.Name}\".");

        P.TaskManager.InsertMulti(
            // 網格還在建就等它。逾時不中止：下一步會再問一次 IsReady，由它統一報錯，
            // 免得這裡靜默清掉整條佇列。
            // ⚠️ 刻意直接傳 IsReady 這個方法群組，不要包成 `IsReady() == true`：
            // 它在 IPC 整個叫不動時是**回 null**（同時自己印一次錯誤），而 null 在 NeoTaskManager
            // 的語意是「中止」。包成 == true 會把那個 null 變成 false，於是這一步空轉滿 120 秒，
            // 而 IsReady 每一幀都再印一次聊天欄錯誤 —— 一次故障洗出幾千行。
            new(S.Ipc.VnavmeshIPC.IsReady, "TeleportPanelGatewayZoneWaitNav",
                new(timeLimitMS: 120000, abortOnTimeout: false)),
            new(() => StartWalkToShard(shard, shardPos), "TeleportPanelGatewayZonePathfind"),
            new(() => CheckWalkArrival(entry, shard), "TeleportPanelGatewayZoneWalkCheckArrival")
            );
    }

    /// <summary>
    /// 送出尋路要求並把「等路徑 → 衝刺 → 走 → 等走完」四步插到抵達檢查前面。
    /// 回 <c>null</c> = 中止整條佇列(NeoTaskManager 的語意)，只有在已經印出錯誤之後才用。
    /// </summary>
    private static bool? StartWalkToShard(CustomAetheryte shard, Vector3 shardPos)
    {
        if(S.Ipc.VnavmeshIPC.IsReady() != true)
        {
            PluginLog.Error($"[TeleportPanel] vnavmesh is not ready (build progress {S.Ipc.VnavmeshIPC.BuildProgress():F2}) - cannot walk to \"{shard.Name}\".");
            DuoLog.Error($"{"The vnavmesh navigation mesh is not ready - please walk to this aethernet shard yourself:".Loc()} {shard.Name}");
            return null;
        }

        // ⚠️ 這個 IPC 掛在 SafeWrapper.AnyException 底下：vnavmesh 那頭擲例外時它是**回 null**，
        // 不是把例外傳上來。不檢查就會在下一步吃 NullReferenceException。
        var task = S.Ipc.VnavmeshIPC.Pathfind(Player.Position, shardPos, false);
        if(task == null)
        {
            PluginLog.Error($"[TeleportPanel] vnavmesh Nav.Pathfind returned no task for \"{shard.Name}\".");
            DuoLog.Error($"{LocText.VnavmeshNoPathToShard.Loc()} {shard.Name}");
            return null;
        }

        P.TaskManager.InsertMulti(
            new(() => task.IsCompleted, "TeleportPanelGatewayZoneWaitPath",
                new(timeLimitMS: 60000, abortOnTimeout: false)),
            // 衝刺失敗(卡動畫、技能還在冷卻)只是走得慢一點，不能因此中止整條佇列。
            new(() => TaskMoveToHouse.UseSprint(false), "TeleportPanelGatewayZoneSprint",
                new(timeLimitMS: 20000, abortOnTimeout: false)),
            new(() => BeginFollowPath(task, shard), "TeleportPanelGatewayZoneStartWalk"),
            // 進得了互動範圍就不必走完剩下的路徑點。
            new(() => IsAetheryteInReach() || P.FollowPath.Waypoints.Count == 0,
                "TeleportPanelGatewayZoneWaitWalk",
                new(timeLimitMS: GatewayZoneWalkTimeLimitMS, abortOnTimeout: false))
            );
        return true;
    }

    private static bool? BeginFollowPath(Task<List<Vector3>> task, CustomAetheryte shard)
    {
        if(!task.IsCompleted || task.IsFaulted || task.IsCanceled)
        {
            PluginLog.Error($"[TeleportPanel] vnavmesh pathfinding to \"{shard.Name}\" did not produce a path: completed={task.IsCompleted}, faulted={task.IsFaulted}, cancelled={task.IsCanceled}, error={task.Exception?.InnerException?.Message ?? task.Exception?.Message}");
            DuoLog.Error($"{LocText.VnavmeshNoPathToShard.Loc()} {shard.Name}");
            return null;
        }

        var path = task.Result;
        if(path == null || path.Count == 0)
        {
            // 🔴 空路徑**不是**「距離 0」。vnavmesh 找不到路、或目標點根本不在網格上時就是回空的，
            // 不擲例外。照舊往下走的話，下一步的「路徑點數 == 0」會被誤讀成「已經走到了」，
            // 然後互動步驟又在原地空轉 —— 跟修正前一模一樣的症狀。
            PluginLog.Error($"[TeleportPanel] vnavmesh returned an empty path to \"{shard.Name}\" - the destination is probably off the navmesh.");
            DuoLog.Error($"{LocText.VnavmeshNoPathToShard.Loc()} {shard.Name}");
            return null;
        }

        P.FollowPath.Stop();
        P.FollowPath.Move([.. path], true);
        return true;
    }

    /// <summary>
    /// 走完(或走不動了)之後的收尾。三種結果各自明確，沒有「什麼都不做」這一格。
    /// </summary>
    private static void CheckWalkArrival(TeleportPanelEntry entry, CustomAetheryte shard)
    {
        // 提早抵達時佇列裡還留著沒走完的路徑點，收乾淨再往下。
        P.FollowPath.Stop();

        var reached = Player.Available ? Utils.GetValidAetheryte() : null;
        if(reached == null)
        {
            PluginLog.Error($"[TeleportPanel] Walked toward \"{shard.Name}\" but there is still no aetheryte within interaction range.");
            DuoLog.Error($"{"Could not reach this aethernet shard in time:".Loc()} {shard.Name}");
            P.TaskManager.Abort();
            return;
        }

        // 走到的如果就是目的地本身，拿它開選單再選自己會被遊戲以 LogMessage 1478
        // 「此處為目前所在地。」拒絕 —— 與本檔開頭那個前置檢查同一個理由。
        // ⚠️ 用實際站到的那一座來判斷，不是用「原本打算走去哪一座」：兩者在極端情形下會不同。
        var actual = S.Data.CustomAethernet?.GetFromIGameObject(reached);
        if(actual != null && actual.Value.ID == entry.Id)
        {
            PluginLog.Information($"[TeleportPanel] Walked to \"{entry.Name}\" itself - no aethernet hop needed.");
            return;
        }

        PluginLog.Information($"[TeleportPanel] Arrived at \"{actual?.Name ?? shard.Name}\" - taking the aethernet to \"{entry.Name}\".");
        InsertAethernetHop(entry);
    }

    /// <summary>
    /// 現在身邊有沒有摸得到的乙太之光。<see cref="Utils.GetValidAetheryte"/> 會解參考
    /// <c>Svc.ClientState.LocalPlayer</c>，所以一定要先過 <see cref="Player.Available"/>。
    /// </summary>
    private static bool IsAetheryteInReach() => Player.Available && Utils.GetValidAetheryte() != null;

    /// <summary>診斷用：這個區域的互動半徑。蒼天街只有 4.56 碼，log 裡看得到才解釋得了
    /// 「明明已經在蒼天街了為什麼還說摸不到」。</summary>
    private static float GetMaxInteractionDistance(uint territory)
    {
        if(S.Data.CustomAethernet?.ZoneInfo != null
            && S.Data.CustomAethernet.ZoneInfo.TryGetValue(territory, out var zone))
        {
            return zone.MaxInteractionDistance;
        }
        return Data.ZoneDetail.DefaultMaxInteractionDistance;
    }

    /// <summary>
    /// 這個區域裡離玩家最近、而且**座標解得出來**的那一座城內乙太之光。
    ///
    /// 🔑 「解得出來」是硬條件不是加分項：<see cref="CustomAetheryte.Position"/> 只有 XZ，
    /// 而蒼天街那八座的 Y 從 -50 到 +10 差了 60 碼。拿玩家的 Y 硬湊餵給 vnavmesh，
    /// 它會找不到對應的 navmesh 多邊形 —— 而**它的失敗形式是回空路徑不是報錯**。
    /// </summary>
    private static bool TryGetNearestShard(uint territory, out CustomAetheryte shard, out Vector3 position)
    {
        shard = default;
        position = default;
        if(!Player.Available) return false;
        if(S.Data.CustomAethernet?.ZoneInfo == null) return false;
        if(!S.Data.CustomAethernet.ZoneInfo.TryGetValue(territory, out var zone)) return false;

        var found = false;
        var best = float.MaxValue;
        foreach(var candidate in zone.Aetherytes)
        {
            if(candidate.TerritoryType != territory) continue;
            var pos = ResolveShardWorldPosition(candidate);
            if(pos == null)
            {
                PluginLog.Information($"[TeleportPanel] Could not resolve a world position for aethernet shard \"{candidate.Name}\" ({candidate.ID}) - skipping it as a walk target.");
                continue;
            }
            var d = DistanceXZ(pos.Value, Player.Position);
            if(d < best)
            {
                best = d;
                shard = candidate;
                position = pos.Value;
                found = true;
            }
        }
        return found;
    }

    /// <summary>
    /// 一座城內乙太之光的**完整**世界座標(含 Y)。兩個來源，都不是猜的：
    /// <list type="number">
    /// <item>場上的實體 —— 最準。只在**當下這一幀**讀它的 <c>Position</c>，
    ///   不保存 <c>IGameObject</c>、不保存任何原生指標。比對方式與
    ///   <see cref="CustomAethernet.GetFromIGameObject"/> 相同(2D、10 碼)。</item>
    /// <item><c>Level</c> 表 <c>Type=45</c> 的列 —— 物件還沒載入時的退路。
    ///   2026-08-09 對台服 7.20 的 EXD dump 逐一比對過：蒼天街(886)、南方博茲雅(920)、
    ///   扎杜諾爾(975)、優雷卡常風/恆冰(732/763) 底下每一座建模過的乙太之光，都能在同區域的
    ///   Type=45 列裡找到誤差 ≤0.1 碼的對應。
    ///   ⚠️ 新月島(1252) 整個區域一列 <c>Level</c> 都沒有 —— 所以「查不到」是**正常結果不是錯誤**，
    ///   呼叫端必須接受 null(它會跳過那一座)。</item>
    /// </list>
    /// </summary>
    private static Vector3? ResolveShardWorldPosition(CustomAetheryte shard)
    {
        if(P.Territory != shard.TerritoryType) return null;

        if(Player.Available)
        {
            foreach(var obj in Svc.Objects)
            {
                if(!obj.IsAetheryte()) continue;
                var d = new Vector2(obj.Position.X - shard.Position.X, obj.Position.Z - shard.Position.Y).Length();
                if(d < 10f) return obj.Position;
            }
        }

        var best = float.MaxValue;
        Vector3? result = null;
        foreach(var pos in GetLevelShardPositions(shard.TerritoryType))
        {
            var d = new Vector2(pos.X - shard.Position.X, pos.Z - shard.Position.Y).Length();
            if(d < best)
            {
                best = d;
                result = pos;
            }
        }
        // 對得上的話誤差是 0.1 碼等級的。差到 5 碼以上就代表比對的根本不是同一座，寧可回 null。
        return best <= 5f ? result : null;
    }

    /// <summary><c>Level</c> 表裡「城內乙太之光」的 <c>Type</c>。</summary>
    private const byte AethernetShardLevelType = 45;

    /// <summary>
    /// 每個區域掃一次就快取。<c>Level</c> 是大表，而 <see cref="TryGetNearestShard"/> 一次會問到
    /// 八座 —— 逐座各掃一次全表會在同一幀掉影格。**空清單也要快取**(新月島就是空的)。
    /// </summary>
    private static readonly Dictionary<uint, List<Vector3>> LevelShardPositions = [];

    private static List<Vector3> GetLevelShardPositions(uint territory)
    {
        if(LevelShardPositions.TryGetValue(territory, out var cached)) return cached;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<Vector3> list = [];
        var sheet = Svc.Data.GetExcelSheet<Level>();
        if(sheet != null)
        {
            foreach(var row in sheet)
            {
                if(row.Type != AethernetShardLevelType) continue;
                if(row.Territory.RowId != territory) continue;
                list.Add(new Vector3(row.X, row.Y, row.Z));
            }
        }
        LevelShardPositions[territory] = list;
        // 這是整段流程裡唯一會掃全表的地方(約 5.8 萬列)，而且一個區域只掃一次。
        // 把耗時一起寫進 log：真的造成掉影格時看得出來是這裡，不必用猜的。
        PluginLog.Information($"[TeleportPanel] Level sheet lists {list.Count} aethernet shard rows (type {AethernetShardLevelType}) in territory {territory}; scan took {sw.Elapsed.TotalMilliseconds:F1}ms (cached from now on).");
        return list;
    }

    private static void FlagOnMap(uint territory, Vector3 position)
    {
        var agent = AgentMap.Instance();
        if(agent == null) return;
        var mapId = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory)?.Map.RowId ?? 0;
        if(mapId == 0) return;
        agent->SetFlagMapMarker(territory, mapId, position);
        agent->OpenMap(mapId, territory);
    }

    /// <summary>
    /// 只比水平距離：乙太之光座標多半是從地圖標記換算來的，Y 恆為 0，
    /// 把 Y 算進去會讓不同來源的座標無法公平比較。
    /// </summary>
    private static float DistanceXZ(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    /// <summary>
    /// 人是不是就站在這座乙太之光旁邊。只比水平距離：乙太之光座標多半是從地圖標記換算來的，
    /// Y 恆為 0，把 Y 算進去會讓不同來源的座標無法公平比較。
    /// </summary>
    private static bool IsStandingAt(TeleportPanelEntry entry)
    {
        if(entry.Position == null || !Player.Available) return false;
        if(P.Territory != entry.Territory) return false;
        var pos = entry.Position.Value;
        return new Vector2(Player.Position.X - pos.X, Player.Position.Z - pos.Z).Length() < 12f;
    }

    /// <summary>
    /// 若該乙太之光設了自訂落點而且功能已啟用，抵達後再前往落點。
    /// 沒設、或功能關著，就什麼都不做 —— 傳送本身完全不受影響。
    /// </summary>
    private static void EnqueueLanding(TeleportPanelEntry entry)
    {
        if(!C.EnableAetheryteLanding) return;
        if(!C.AetheryteLandings.TryGetValue(entry.Id, out var landing)) return;

        // 到了對的區域、能動了，才開始處理落點。逾時中止整條佇列：
        // 沒到對的地圖就開始導航(或更糟，直接寫座標)會在錯的地方亂跑。
        P.TaskManager.Enqueue(
            () => P.Territory == entry.Territory && Player.Interactable && !Svc.Condition[ConditionFlag.BetweenAreas],
            "TeleportPanelWaitArrival", new(timeLimitMS: 120000));
        P.TaskManager.Enqueue(Utils.WaitForScreen);

        P.TaskManager.Enqueue(() =>
        {
            if(C.AetheryteLandingDirectWrite)
            {
                if(TryDirectWrite(landing)) return true;
                // 直接寫失敗(例如在戰鬥中/副本裡)就退回安全路徑，不要什麼都不做。
                ChatPrinter.Red($"[Lifestream] {"Direct position write refused - falling back to walking.".Loc()}");
            }
            // 落點離某座城內乙太之光比離剛落地的主水晶近很多時，先搭一段都市傳送網再走。
            // 不成立(或功能關著)就原封不動走既有那條路。
            if(TryEnqueueLandingAethernetRelay(entry, landing)) return true;
            EnqueueWalkTo(entry, landing);
            return true;
        }, "TeleportPanelLanding");
    }

    /// <summary>
    /// 自訂落點的「城內乙太之光中繼」。
    ///
    /// 修改前 <see cref="EnqueueLanding"/> 只有兩條路：直寫座標，或從落地點 vnavmesh 一路走過去。
    /// 傳送到主水晶時那個「一路」可能橫跨整座城(九號解決方案是實測案例)，
    /// 而該城同一個都市傳送網裡往往就有一座乙太之光近得多 —— 遊戲自己的乙太之光選單本來就到得了它。
    ///
    /// 🔑 <b>沒有新機制</b>：搭乘那一段直接沿用 <see cref="TaskAethernetRoute.Enqueue"/>
    /// (「身邊摸得到同網路節點就走過去用它，否則傳送回主水晶」的那一份，含它全部的逾時退路)，
    /// 走路那一段仍然是原本的 <see cref="EnqueueWalkTo"/>。這裡新增的只有「要不要中繼」的判斷。
    ///
    /// 🔴 判斷刻意保守，每一條不成立就直接回 false ＝ 行為與修改前逐字相同：
    /// <list type="bullet">
    /// <item>只處理「傳送到主水晶」(<see cref="TeleportPanelEntry.IsAetheryte"/>)。
    ///   目的地本身就是城內乙太之光時**本來就已經是對的** —— 那條路走
    ///   <see cref="TaskAetheryteAethernetTeleport.Enqueue"/> → <see cref="TaskAethernetRoute"/>，
    ///   人是落在該乙太之光旁邊，走路那段已經從最近處開始，再挑一座別的等於推翻使用者指名的目的地。</item>
    /// <item>房屋類(<see cref="TeleportPanelEntry.SubIndex"/> &gt; 0)不處理。</item>
    /// <item>沒裝 vnavmesh 不處理 —— 那條路只是在地圖上插旗，多吃一次讀取畫面沒有意義，
    ///   而且會改掉「沒有 vnavmesh 時的行為」。</item>
    /// <item>收益低於 <see cref="Data.Config.AetheryteLandingRelayGain"/> 不處理(0 = 整個功能關掉)。</item>
    /// </list>
    /// </summary>
    private static bool TryEnqueueLandingAethernetRelay(TeleportPanelEntry entry, Vector3 landing)
    {
        if(!entry.IsAetheryte) return false;
        if(entry.SubIndex != 0) return false;
        var gain = C.AetheryteLandingRelayGain;
        if(gain <= 0f) return false;
        // ⚠️ TaskAethernetRoute.Enqueue 開頭就有 `if(!Player.Available) return;` —— 它靜默什麼都不排，
        // 而我們後面那幾步照樣會排進去，結果是白等 15 秒轉場才走路。這裡先擋掉那條路。
        if(!Player.Available) return false;
        if(!Svc.PluginInterface.InstalledPlugins.Any(x => x.InternalName == "vnavmesh" && x.IsLoaded)) return false;
        if(S.Data.DataStore?.Aetherytes == null) return false;

        // 這一列對應的主水晶。DataStore 的鍵只有「有 AethernetGroup 的主水晶」，
        // 野外傳送點與房屋傳送點查不到 —— 查不到就代表它根本沒有都市傳送網可搭。
        TinyAetheryte root = default;
        var haveRoot = false;
        foreach(var master in S.Data.DataStore.Aetherytes.Keys)
        {
            if(master.ID != entry.Id) continue;
            root = master;
            haveRoot = true;
            break;
        }
        if(!haveRoot) return false;
        // 同一個都市傳送網可以橫跨區域(蒼天街/住宅區)，只在落點所在的那一區裡比。
        if(root.TerritoryType != entry.Territory) return false;
        if(!TryGetAetheryteGroundPosition(root, out var rootPos))
        {
            PluginLog.Information($"[TeleportPanel] Cannot work out where aetheryte \"{entry.Name}\" ({entry.Id}) actually is, so the custom landing cannot be compared against the aethernet - walking the whole way.");
            return false;
        }

        var arrivalDistance = DistanceXZ(rootPos, landing);

        TinyAetheryte best = default;
        var bestDistance = float.MaxValue;
        foreach(var child in S.Data.DataStore.Aetherytes[root])
        {
            // 選單上不會出現的隱藏節點(飛空艇著陸場之類)不能當中繼點。
            if(child.Invisible) continue;
            if(child.TerritoryType != entry.Territory) continue;
            if(!TaskGotoDestination.IsAetheryteUnlocked(child.ID)) continue;
            if(!TryGetAetheryteGroundPosition(child, out var childPos)) continue;
            var d = DistanceXZ(childPos, landing);
            if(d < bestDistance)
            {
                bestDistance = d;
                best = child;
            }
        }

        if(bestDistance == float.MaxValue)
        {
            PluginLog.Information($"[TeleportPanel] Custom landing for \"{entry.Name}\": {arrivalDistance:F0}y from the aetheryte, no unlocked aethernet shard with a usable position in territory {entry.Territory} - decision = walk the whole way.");
            return false;
        }

        var saved = arrivalDistance - bestDistance;
        if(saved < gain)
        {
            PluginLog.Information($"[TeleportPanel] Custom landing for \"{entry.Name}\": {arrivalDistance:F0}y from the aetheryte, nearest aethernet shard \"{best.Name}\" {bestDistance:F0}y - only {saved:F0}y saved, below the {gain:F0}y threshold, decision = walk the whole way.");
            return false;
        }

        PluginLog.Information($"[TeleportPanel] Custom landing for \"{entry.Name}\": {arrivalDistance:F0}y from the aetheryte, nearest aethernet shard \"{best.Name}\" {bestDistance:F0}y - {saved:F0}y saved (threshold {gain:F0}y), decision = take the aethernet there first, then walk.");

        // 🔴 這裡用 Enqueue 不是 InsertMulti，而且那是對的：本方法是從佇列**最後一步**
        // (TeleportPanelLanding)裡呼叫的，此刻佇列後面什麼都沒有，走路那段也是接在這些之後才排進去。
        // (TaskAethernetRoute 內部自己會用 InsertMulti 把實際步驟插到這幾步前面，那是它的職責。)
        TaskAethernetRoute.Enqueue(root, best);
        // ⚠️ 路線若決定「用走的」(RouteUsesAethernet=false)就根本不會有讀取畫面，直接放行，
        // 否則這裡會空轉滿 15 秒才往下走。與本檔上面那條城內乙太之光路線同一個判斷。
        P.TaskManager.Enqueue(
            () => !TaskAethernetRoute.RouteUsesAethernet || Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51],
            "TeleportPanelLandingRelayWaitTransition", new(timeLimitMS: 15000, abortOnTimeout: false));
        P.TaskManager.Enqueue(Utils.WaitForScreen);
        // 🔴 這一步刻意 abortOnTimeout:false。中繼是最佳化，不是必要條件：不管乙太之光那一段
        // 有沒有真的成功(選單沒開、被打斷、逾時)，人都還在同一個區域裡，接下來照樣走得到落點 ——
        // 逾時即中止會把走路那段一起清掉，那才是比修改前更糟的結果。
        P.TaskManager.Enqueue(
            () => P.Territory == entry.Territory && Player.Interactable && !Svc.Condition[ConditionFlag.BetweenAreas],
            "TeleportPanelLandingRelayWaitArrival", new(timeLimitMS: 120000, abortOnTimeout: false));
        P.TaskManager.Enqueue(() =>
        {
            PluginLog.Information($"[TeleportPanel] Aethernet relay finished at territory {P.Territory}, {(Player.Available ? $"{DistanceXZ(Player.Position, landing):F0}y" : "unknown distance")} from the custom landing - walking the rest.");
            EnqueueWalkTo(entry, landing);
            return true;
        }, "TeleportPanelLandingRelayWalk");
        return true;
    }

    /// <summary>
    /// 一座乙太之光在世界中的水平位置(Y 一律填 0，呼叫端只比 XZ)。
    ///
    /// 🔑 <b>優先用 <see cref="TinyAetheryte.Position"/>，不要用
    /// <see cref="ECommons.GameHelpers.Map.AetherytePosition(uint)"/></b>：後者查不到 <c>Level</c> 資料時
    /// 退回地圖標記換算，而那個換算**沒有把 <c>Map.OffsetX</c>/<c>Map.OffsetY</c> 減掉**。
    /// 2026-08-27 對台服 7.20 EXD 逐筆驗過：Lifestream 隨附的 <c>StaticData.json</c> 裡
    /// <c>CustomPositions</c> 那 20 筆(九號解決方案 217/230~236、圖萊尤拉 216/218~224、多瑪飛地 127/129/130/162)
    /// 與地圖標記換算值的差，**每一筆都恰好等於該地圖的 (OffsetX, OffsetY)**
    /// (九號解決方案 = (0, 90)、圖萊尤拉 = (50, -70)、多瑪飛地 = (23, 34))。
    /// 也就是說 <c>CustomPositions</c> 存在的唯一理由就是補這個缺項，而
    /// <see cref="Systems.Legacy.DataStore.GetTinyAetheryte"/> 是**先查它**再退回地圖標記的 ——
    /// 所以 <see cref="TinyAetheryte.Position"/> 才是這幾座城市裡唯一正確的來源。
    /// 拿差了 90 碼的座標來比「哪一座近」，在九號解決方案會直接選錯乙太之光。
    ///
    /// 退路仍留著 <see cref="Systems.TeleportPanel.TeleportPanelIndex.GetPosition"/>(它會先查
    /// <c>Level</c> 表，那條路徑是準的)，只在 DataStore 兩個來源都空的時候才用得到。
    ///
    /// ⚠️ <b>精度只有幾十碼</b>：同一次比對裡，九號解決方案七座乙太之光的 <c>CustomPositions</c>
    /// 與 <c>Level</c> 表 <c>Type=45</c> 實體物件座標的水平差是 8~108 碼(Y 幾乎完全吻合，差 ≤1.4 碼，
    /// 所以配對本身是對的，差的是地圖標記畫在圖示中心而不是物件上)。
    /// ⇒ 這些座標只夠用來回答「哪一座明顯比較近」，**不能**拿來當導航目標或判斷「是不是已經到了」。
    /// 呼叫端的門檻(<see cref="Data.Config.AetheryteLandingRelayGain"/>)本來就要蓋過這個誤差。
    /// 📌 順帶一提：<c>Level</c> 表 <c>Type=45</c> 在該區域有 19 列而該城只有 7 座乙太之光，
    /// 所以**不能**反過來把 Type=45 當乙太之光清單 —— 多出來的 11 列會冒充成更近的候選。
    /// </summary>
    private static bool TryGetAetheryteGroundPosition(TinyAetheryte a, out Vector3 position)
    {
        // (0,0) 是 DataStore「兩個來源都查不到」的哨兵值，不是真的世界原點。
        if(a.Position != Vector2.Zero)
        {
            position = new Vector3(a.Position.X, 0f, a.Position.Y);
            return true;
        }
        var fallback = TeleportPanelIndex.GetPosition(a.ID);
        if(fallback == null || fallback.Value.X == 0f && fallback.Value.Z == 0f)
        {
            position = default;
            return false;
        }
        position = fallback.Value;
        return true;
    }

    /// <summary>從目前所在位置走去落點。</summary>
    /// <remarks>
    /// 📌 這裡的診斷刻意寫 <c>Information</c>:「人被丟在奇怪的地方 / 卡住了」這類回報,
    /// 沒有<b>起點、終點、走不走得成</b>三件事就只能用猜的,而使用者跑 LogLevel 1,
    /// 盲區只有 <c>Verbose</c>,<c>Debug</c> 收得到但單檔數十萬行會淹沒。
    /// ⚠️ 起點要在**這一刻**取:落地後角色可能還在滑動,事後回推會對不上。
    /// </remarks>
    private static void EnqueueWalkTo(TeleportPanelEntry entry, Vector3 landing)
    {
        var hasVnavmesh = Svc.PluginInterface.InstalledPlugins.Any(x => x.InternalName == "vnavmesh" && x.IsLoaded);
        var from = Player.Available ? Player.Position : default;
        var straight = Player.Available ? Vector3.Distance(from, landing) : -1f;
        PluginLog.Information($"[TeleportPanel] 自訂落點 ⇒ 用走的:\"{entry.Name}\"(乙太之光 {entry.Id}、區域 {entry.Territory}),"
            + $"起點 {from:F2},落點 {landing:F2},直線 {straight:F1} 碼,vnavmesh={hasVnavmesh}。");
        if(hasVnavmesh)
        {
            TaskGotoDestination.EnqueueNavTo(landing);
        }
        else
        {
            var mapId = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(entry.Territory)?.Map.RowId ?? 0;
            // AgentMap 取得器合法回 null;拿不到就只印訊息、不插旗(同檔 FlagOnMap 的寫法)。
            var agent = AgentMap.Instance();
            if(mapId != 0 && agent != null)
            {
                agent->SetFlagMapMarker(entry.Territory, mapId, landing);
                agent->OpenMap(mapId, entry.Territory);
            }
            ChatPrinter.Green($"[Lifestream] {LocText.VnavmeshNotInstalledFlagged.Loc()} {entry.DisplayName}");
        }
    }

    /// <summary>
    /// 🔴 直接把玩家座標寫進遊戲記憶體(瞬移)。使用者自行承擔風險的功能，預設關閉。
    ///
    /// 安全性設計(針對「崩潰」而非「被偵測」)：
    ///   - 指標是**當下這一幀重新取得**的(<see cref="Player.GameObject"/> 每次都從
    ///     <c>Svc.ClientState.LocalPlayer.Address</c> 重解析)，絕不跨幀保存，也不存 IGameObject。
    ///   - 呼叫的是 FFXIVClientStructs 的 <c>GameObject.SetPosition</c>。該特徵碼
    ///     <c>E8 ?? ?? ?? ?? 83 4B 70 01</c> 已離線在台服 7.20 客戶端驗過：.text 唯一命中
    ///     0x1416F3C7C，跟隨 E8 後為 0x140853C50，函式本體就是把 X/Y/Z 寫進 [rcx+0xB0/B4/B8]
    ///     並更新 [rcx+0x9C] 的旗標 —— 與目前 CS 的 GameObject.Position 偏移一致。
    ///   - 沒有 hook、沒有每幀常駐、沒有封包。
    ///
    /// 前置條件擋掉最容易出事的情境(副本、戰鬥、轉場、坐騎飛行中)。
    /// </summary>
    private static bool TryDirectWrite(Vector3 target)
    {
        if(!Player.Interactable) return false;
        if(Player.IsInDuty) return false;
        if(Svc.Condition[ConditionFlag.InCombat]) return false;
        if(Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51]) return false;
        if(Svc.Condition[ConditionFlag.Casting] || Svc.Condition[ConditionFlag.Occupied]) return false;

        var go = Player.GameObject;
        if(go == null) return false;

        go->SetPosition(target.X, target.Y, target.Z);
        PluginLog.Information($"[TeleportPanel] Direct position write to {target.X:F1}, {target.Y:F1}, {target.Z:F1}");
        return true;
    }
}
