using ECommons.Automation.NeoTaskManager;
using Lifestream.IPC;

namespace Lifestream.Tasks.Utility;

/// <summary>
/// 「整條 /li 鏈真的抵達目的地了」的收尾哨兵：排在佇列最尾端，等到佇列真的空掉、
/// 自動移動也真的走完，才呼叫 <see cref="TataruPraiseIPC.TryPraiseArrival"/>。
/// </summary>
/// <remarks>
/// 🔑 <b>為什麼是「排一個任務」而不是「監看 IsBusy 由忙轉閒」。</b>
/// NeoTaskManager 沒有「整條鏈完成」事件，而且 <c>Abort()</c> 之後的狀態與正常跑完
/// <b>完全一樣</b>(佇列空、CurrentTask 為 null)——從外面監看分不出成功與中止。
/// 排一個哨兵任務就自動分得出來：逾時中止、任務擲例外中止、任務回 null 中止、
/// <c>/li stop</c>、進度條右鍵停止、IPC <c>Abort</c>——這些全都會呼叫 <c>Abort()</c> 把佇列清掉，
/// 哨兵跟著一起被丟掉，於是<b>不會出聲</b>。這正是我們要的語意。
///
/// <para>
/// 📌 <b>自我後推(re-defer)。</b>Lifestream 的任務會在執行途中往佇列尾端再排新任務
/// (例如 <see cref="TaskGotoDestination"/> 的導航、<c>/li A, B</c> 的第二段指令)，
/// 所以「排在當下的最尾端」不等於「最後執行」。哨兵每次輪到自己時，只要後面還有東西
/// 就把自己重排到隊尾並讓路；直到自己是唯一剩下的那個，才進入等待。
/// 這也順帶解決了鏈式指令重複呼叫的問題：同一時間佇列裡只會有一個哨兵
/// (<see cref="IsAlreadyQueued"/>)。
/// </para>
///
/// <para>
/// ⚠️ <b>會讓 <c>TaskManager.IsBusy</c> 在最後一段自動移動期間維持 true。</b>
/// Lifestream 多數流程本來就用 <c>Enqueue(() =&gt; P.FollowPath.Waypoints.Count == 0)</c>
/// 把移動包在佇列裡(CustomAliasCommand、TaskPropertyShortcut、TaskMoveToHouse)，
/// 只有 <c>/li goto</c> 的導航尾段沒有；哨兵讓那一段也變成「忙」，與 <c>Utils.IsBusy()</c>
/// 一直以來的判定一致。這個選項關掉時哨兵根本不會排進去，行為與修改前逐位元相同。
/// </para>
///
/// <para>
/// 🔴 <b>移動失敗不算抵達。</b><see cref="Movement.FollowPath"/> 逾時或偵測到 vnavmesh
/// 自己在動時，會把航點<b>清空</b>並印一行錯誤——從外面看跟「走到了」一模一樣。
/// 所以那兩條路徑會立起 <see cref="Movement.FollowPath.LastMovementFailed"/>，哨兵據此閉嘴。
/// </para>
/// </remarks>
internal static class TaskAnnounceArrival
{
    internal const string TaskName = "TataruPraiseAnnounceArrival";

    /// <summary>
    /// 最後一段路可能很長(跨半張地圖的導航)，所以時限放到 15 分鐘，而且<b>逾時不中止整條佇列</b>——
    /// 這個哨兵只是通知，不能因為它把使用者的流程弄斷。逾時就只是不出聲。
    /// </summary>
    private static readonly TaskManagerConfiguration Settings = new(timeLimitMS: 60000 * 15, abortOnTimeout: false, timeoutSilently: true);

    /// <summary>
    /// 「一切都空了」要連續成立這麼多幀才算數。
    /// 🔴 這不是保險，是修一個真的會發生的競態：<c>/li A, B</c> 的第二段指令是用
    /// <c>Svc.Framework.RunOnTick(..., delayTicks: 1)</c> 送出去的，而 RunOnTick 與
    /// TaskManager.Tick 同樣掛在 Framework.Update 上，<b>同一幀內誰先跑沒有保證</b>。
    /// 只看一幀的話會在第二段指令排進佇列前就宣布抵達。
    /// </summary>
    private const int RequiredIdleTicks = 3;

    private static int IdleTicks;

    /// <summary>
    /// 這一次 <c>/li</c> 指令真的排出了任務時，在尾端補上哨兵。
    /// </summary>
    /// <param name="queuedBefore">執行指令<b>之前</b>的 <c>NumQueuedTasks</c>。</param>
    internal static void EnqueueIfChainStarted(int queuedBefore)
    {
        if(!C.TataruPraiseOnArrival) return;
        // 純開關視窗類的指令(/li panel、/li fav…)不會排任何任務,不該通知。
        // /li stop 會把佇列清空,數字只會變小,同樣不會走到下面。
        if(P.TaskManager.NumQueuedTasks <= queuedBefore) return;
        if(IsAlreadyQueued()) return;
        // 上一趟的移動失敗旗標不可以殘留到這一趟(這一趟可能根本沒有移動)。
        if(P.followPath != null) P.followPath.LastMovementFailed = false;
        IdleTicks = 0;
        Enqueue();
    }

    private static bool IsAlreadyQueued()
        => P.TaskManager.CurrentTask?.Name == TaskName || P.TaskManager.Tasks.Any(x => x.Name == TaskName);

    private static void Enqueue() => P.TaskManager.Enqueue(Tick, TaskName, Settings);

    private static bool Tick()
    {
        // 後面還有任務 -> 讓路,把自己重排到隊尾。
        if(P.TaskManager.Tasks.Count > 0)
        {
            IdleTicks = 0;
            Enqueue();
            return true;
        }
        // 佇列空了,但自動移動可能還在走(/li goto 的導航尾段就不在佇列裡)。
        if(P.followPath != null && P.followPath.Waypoints.Count > 0)
        {
            IdleTicks = 0;
            return false;
        }
        if(++IdleTicks < RequiredIdleTicks) return false;

        if(P.followPath != null && P.followPath.LastMovementFailed)
        {
            // 使用者跑 LogLevel 1 —— 「為什麼這次沒出聲」正是他會來問的事。
            PluginLog.Information("[TataruPraise] 自動移動未能走完(逾時或被 vnavmesh 接手),不宣布抵達。");
            return true;
        }
        TataruPraiseIPC.TryPraiseArrival("/li 任務鏈完成");
        return true;
    }
}
