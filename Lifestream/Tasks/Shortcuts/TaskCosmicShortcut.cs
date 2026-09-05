using ECommons.Automation;
using Callback = ECommons.Automation.Callback;
using ECommons.Automation.NeoTaskManager;
using ECommons.Automation.NeoTaskManager.Tasks;
using ECommons.ChatMethods;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lifestream.Data;
using Lifestream.Schedulers;
using Lifestream.Tasks.SameWorld;

namespace Lifestream.Tasks.Shortcuts;

/// <summary>
/// <c>/li cosmic</c>(別名 <c>ardorum</c>／<c>moon</c>)前往渴望灣(宇宙探索,區域 1237)。
///
/// <para>兩條路線都從「傳送到嘆息海『最佳威兔洞』乙太之光(175)」開始,差別只在後半段:</para>
/// <list type="bullet">
/// <item><b>快版(這裡)</b>:互動乙太之光 → 選「前往渴望灣」→(多副本區時)選副本區。</item>
/// <item><b>舊版</b>(<see cref="StaticAlias.CosmicExploration"/>):騎馬 → 繞路走到入口物件
///   (EObj 1052581)→ 兩層選單。騎馬跑那一段就是舊版大部分的耗時。</item>
/// </list>
///
/// <para>⚠️ 快版**不是** <see cref="TaskSinusArdorumTeleport.Enqueue"/>。那一個是浮動視窗按鈕與
/// <c>/li 渴望灣</c> 用的,它從 <see cref="WorldChange.TargetValidAetheryte"/> 起手、**完全沒有傳送步驟**,
/// 前提是「人已經站在乙太之光 175 旁邊」(按鈕本身也只在 <c>P.ActiveAetheryte.ID == 175</c> 時才畫出來)。
/// 指令可以在任何地方下,所以這裡走的是 <see cref="TaskAetheryteAethernetTeleport"/> 那一套
/// 「條件式傳送 → 鎖定走位 → 互動 → 選單項」(<c>/li goto</c> 進渴望灣用的也是它),
/// 只是每一步都改成**逾時不中止**,好讓失敗時佇列還能走到退路
/// (預設的 <c>AbortOnTimeout=true</c> 會把整條佇列清掉,退路就永遠不會執行)。</para>
///
/// <para>兩條路線的**前提完全相同**:都要已解鎖乙太之光 175(否則第一步的傳送就過不了)、
/// 都要在原伺服器(指令會先把人送回原伺服器)、都要已解鎖宇宙探索(否則乙太之光選單與入口物件對話
/// 都不會有那一項)。離線查不出任何「快版會失敗但舊版會成功」的情境,所以退路是防未知用的;
/// 代價是失敗時多等一段逾時,換到的是「不會從『慢但會到』變成『快但有時候到不了』」。</para>
///
/// <para>退路有兩個觸發點,對應兩種失敗:</para>
/// <list type="number">
/// <item>連玄關乙太之光都沒站到(傳送沒成功)——立刻退,不必把後面每一步都空等到逾時。</item>
/// <item>站到了、但最後人不在渴望灣——走完快版才退。</item>
/// </list>
/// </summary>
public static unsafe class TaskCosmicShortcut
{
    /// <summary>
    /// 逾時不中止。快版的每一步都必須用它:任何一步失敗都要讓佇列繼續走到
    /// <see cref="VerifyArrivalOrFallBack"/>,不能整條 Abort 掉——那會連退路一起清掉。
    /// </summary>
    private static TaskManagerConfiguration Lenient(int ms) => new(timeLimitMS: ms, abortOnTimeout: false);

    /// <summary>
    /// 「前往渴望灣」那一項到底有沒有被選到。用來讓後面的等待步驟短路:沒選到就不會有轉場,
    /// 空等 60 秒只是讓使用者多站著發呆。單一佇列、指令入口有 <see cref="Utils.IsBusy"/> 擋重入,
    /// 所以用靜態欄位是安全的;每次 <see cref="Enqueue"/> 都會重設。
    /// </summary>
    private static bool SelectedSinusArdorumEntry;

    public static void Enqueue()
    {
        SelectedSinusArdorumEntry = false;
        PluginLog.Information($"[Cosmic] Taking the aetheryte-menu route: teleport to aetheryte {TaskAetheryteAethernetTeleport.SinusArdorumRootAetheryteId} -> interact -> select [{Lang.TravelToSinusArdorum.Print(" | ")}].");
        TaskRemoveAfkStatus.Enqueue();

        // 1) 需要時才傳送到玄關乙太之光(人已經站在它旁邊就跳過)。判定條件與 TaskAetheryteAethernetTeleport 相同。
        P.TaskManager.Enqueue(() =>
        {
            if(!IsAtGatewayAetheryte())
            {
                PluginLog.Information($"[Cosmic] Not standing next to aetheryte {TaskAetheryteAethernetTeleport.SinusArdorumRootAetheryteId} (currently in territory {P.Territory}), teleporting there first.");
                P.TaskManager.InsertMulti(
                    new(() => S.TeleportService.TeleportToAetheryte(TaskAetheryteAethernetTeleport.SinusArdorumRootAetheryteId), "CosmicTeleportToGatewayAetheryte"),
                    new(Utils.WaitForScreenFalse, "CosmicWaitTeleportStart", Lenient(30000)),
                    new(Utils.WaitForScreen, "CosmicWaitTeleportEnd", Lenient(60000))
                    );
            }
            else
            {
                PluginLog.Information($"[Cosmic] Already next to aetheryte {TaskAetheryteAethernetTeleport.SinusArdorumRootAetheryteId}, skipping the teleport.");
            }
        }, "CosmicConditionalTeleportToGatewayAetheryte");

        // 2) 等真的站到玄關乙太之光旁。⚠️ 不能只看一幀就判定:讀取畫面結束(IsScreenReady)與
        //    乙太之光物件變成可鎖定之間有幾幀的落差,只查一次會在「其實傳送成功了」的情況下誤判成失敗,
        //    白白退回慢版。逾時不中止——逾時就是下一步要處理的失敗訊號。
        P.TaskManager.Enqueue(() => Player.Interactable && IsAtGatewayAetheryte(), "CosmicWaitAtGatewayAetheryte", Lenient(10000));

        // 3) 傳送成功才排後面的步驟。沒成功就直接退路——後面每一步都會失敗,一步一步空等到逾時
        //    要花好幾分鐘,而使用者只會看到角色站著不動。
        P.TaskManager.Enqueue(RouteDecision, "CosmicRouteDecision");
    }

    /// <summary>玄關乙太之光(175)就在身邊、摸得到嗎?判定與 <see cref="TaskAetheryteAethernetTeleport"/> 相同。</summary>
    private static bool IsAtGatewayAetheryte()
        => Svc.ClientState.TerritoryType == TaskAetheryteAethernetTeleport.SinusArdorumRootAetheryteTerritoryId
        && Utils.GetReachableAetheryte(x => Utils.TryGetTinyAetheryteFromIGameObject(x, out var ae) && ae.HasValue && ae.Value.ID == TaskAetheryteAethernetTeleport.SinusArdorumRootAetheryteId) != null;

    private static bool? RouteDecision()
    {
        if(!IsAtGatewayAetheryte())
        {
            PluginLog.Information($"[Cosmic] Could not get to the gateway aetheryte {TaskAetheryteAethernetTeleport.SinusArdorumRootAetheryteId} (currently in territory {P.Territory}) - the aetheryte-menu route cannot even start.");
            FallBackToEntranceObjectRoute();
            return true;
        }

        // ⚠️ 用 InsertStack 而不是在 Enqueue() 裡就排好:這些步驟是「執行途中」才確定要走的,
        // 而 Enqueue 會排到佇列尾端。這一刻後面剛好沒有別的任務,但只要將來有人在
        // TaskCosmicShortcut.Enqueue 之後再加步驟,用 Enqueue 就會靜默錯序。
        // (同樣的坑 TaskGotoDestination.InsertMasterAetheryteFallback 已經踩過一次。)
        P.TaskManager.InsertStack(() =>
        {
            // 鎖定並確保站進互動距離(與 TaskAetheryteAethernetTeleport 同一套)。
            P.TaskManager.EnqueueDelay(10, true);
            P.TaskManager.Enqueue(WorldChange.TargetReachableMasterAetheryte, "CosmicTargetAetheryte", Lenient(15000));
            P.TaskManager.Enqueue(ConditionalWalkToAetheryte, "CosmicConditionalLockon");
            P.TaskManager.Enqueue(WorldChange.InteractWithTargetedAetheryte, "CosmicInteractAetheryte", Lenient(15000));

            // 選「前往渴望灣」。找不到那一項時會把當下的選單內容寫進 log。
            P.TaskManager.Enqueue(SelectTravelToSinusArdorum, "CosmicSelectTravelToSinusArdorum", Lenient(20000));

            // 多副本區的區域會再跳一個「切換副本區」選單,單副本區則直接轉場。沿用既有處理(逾時不中止)。
            TaskSinusArdorumTeleport.EnqueueSelectAnyInstance();

            // 等真的抵達渴望灣。逾時不中止——逾時本身就是「快版沒成功」的訊號。
            P.TaskManager.Enqueue(WaitArrival, "CosmicWaitArrival", Lenient(60000));

            // 到了就收工;沒到就整包退回舊的別名流程。
            P.TaskManager.Enqueue(VerifyArrivalOrFallBack, "CosmicVerifyArrivalOrFallBack");
        });
        return true;
    }

    /// <summary>摸得到但還沒站進互動距離時,鎖定 + 自動移動走過去。</summary>
    private static bool? ConditionalWalkToAetheryte()
    {
        if(P.ActiveAetheryte == null)
        {
            PluginLog.Information("[Cosmic] Aetheryte is targetable but out of interaction range, walking to it.");
            P.TaskManager.InsertMulti(
                new(WorldChange.LockOn, "CosmicLockOn"),
                new(WorldChange.EnableAutomove, "CosmicEnableAutomove"),
                new(WorldChange.WaitUntilMasterAetheryteExists, "CosmicWalkToAetheryte", Lenient(20000)),
                new(WorldChange.DisableAutomove, "CosmicDisableAutomove"),
                new FrameDelayTask(10)
                );
        }
        return true;
    }

    /// <summary>
    /// 選「前往渴望灣」,順便在等不到那一項時定期寫診斷。
    /// 顯示文字是執行期從表解析的(見 <see cref="Lang.TravelToSinusArdorum"/>),不是寫死的字串。
    /// ⚠️ 選單剛開的那一兩幀 EntryCount 會是 0,那時不能當成「沒有這一項」——只是還沒填好。
    /// </summary>
    private static bool? SelectTravelToSinusArdorum()
    {
        if(!Player.Available) return false;
        if(TryGetAddonByName<AddonSelectString>("SelectString", out var addon) && IsAddonReady(&addon->AtkUnitBase))
        {
            var entries = Utils.GetEntries(addon);
            if(entries.Count > 0 && !entries.Any(x => x.EqualsAny(Lang.TravelToSinusArdorum)) && EzThrottler.Throttle("CosmicMenuMismatchLog", 5000))
            {
                PluginLog.Information($"[Cosmic] The aetheryte menu is open but has no Sinus Ardorum entry. Looked for [{Lang.TravelToSinusArdorum.Print(" | ")}], menu shows [{entries.Print(" | ")}].");
            }
        }
        else if(EzThrottler.Throttle("CosmicMenuWaitLog", 5000))
        {
            PluginLog.Information("[Cosmic] Waiting for the aetheryte menu to open.");
        }

        if(Utils.TrySelectSpecificEntry(Lang.TravelToSinusArdorum, () => EzThrottler.Throttle("SelectString")))
        {
            SelectedSinusArdorumEntry = true;
            return true;
        }
        return false;
    }

    private static bool? WaitArrival()
    {
        // 選單項根本沒選到就不會有轉場,別空等滿 60 秒才去走退路。
        if(!SelectedSinusArdorumEntry) return true;
        return P.Territory == TaskAetheryteAethernetTeleport.SinusArdorumTerritoryId
            && Player.Interactable
            && !Svc.Condition[ConditionFlag.BetweenAreas];
    }

    /// <summary>快版的收尾:到了就結束,沒到就走退路並寫明為什麼。</summary>
    private static bool? VerifyArrivalOrFallBack()
    {
        if(P.Territory == TaskAetheryteAethernetTeleport.SinusArdorumTerritoryId)
        {
            PluginLog.Information("[Cosmic] Arrived at Sinus Ardorum through the aetheryte menu.");
            return true;
        }

        PluginLog.Information($"[Cosmic] The aetheryte-menu route did not get us to Sinus Ardorum ({DescribeFailure()}).");
        FallBackToEntranceObjectRoute();
        return true;
    }

    private static void FallBackToEntranceObjectRoute()
    {
        PluginLog.Information("[Cosmic] Falling back to the entrance-object route: teleport to the gateway aetheryte, mount up, ride to the entrance object and use its menu.");
        ChatPrinter.Green($"[Lifestream] {"The quick route to Sinus Ardorum did not work, falling back to the long one.".Loc()}");
        CloseLeftoverSelectString();
        // 同 RouteDecision 的理由:退路是執行途中才決定要走的,一律用 InsertStack 插到佇列最前面。
        P.TaskManager.InsertStack(() => StaticAlias.CosmicExploration.Enqueue(true));
    }

    private static string DescribeFailure()
    {
        if(TryGetAddonByName<AddonSelectString>("SelectString", out var addon) && IsAddonReady(&addon->AtkUnitBase))
        {
            return $"still in territory {P.Territory}, selectedMenuEntry={SelectedSinusArdorumEntry}, a SelectString is open showing [{Utils.GetEntries(addon).Print(" | ")}]";
        }
        return $"still in territory {P.Territory}, selectedMenuEntry={SelectedSinusArdorumEntry}, no aetheryte menu open, ActiveAetheryte={(P.ActiveAetheryte == null ? "null" : $"{P.ActiveAetheryte.Value.Name}({P.ActiveAetheryte.Value.ID})")}";
    }

    /// <summary>
    /// 快版失敗最可能的形狀就是「選單開著但沒有那一項」——這時選單還開著,
    /// 退路的騎馬/走位會被占用狀態擋住。先取消掉它再走退路。
    /// </summary>
    private static void CloseLeftoverSelectString()
    {
        if(TryGetAddonByName<AddonSelectString>("SelectString", out var addon) && IsAddonReady(&addon->AtkUnitBase))
        {
            // 這扇窗是本外掛沒按過的(按到了就不會走退路),守衛在這裡幾乎必放行;接上是為了「同一扇窗的所有按法都過同一個守衛」。
            if(!AddonPressGuard.TryPressOnce("SelectString", &addon->AtkUnitBase, nameof(CloseLeftoverSelectString))) return;
            PluginLog.Information("[Cosmic] Cancelling the leftover aetheryte menu before falling back.");
            Callback.Fire(&addon->AtkUnitBase, true, -1);
        }
    }
}
