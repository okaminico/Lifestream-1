using ECommons.Automation;
using Callback = ECommons.Automation.Callback;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Lifestream.Tasks.SameWorld;

/// <summary>
/// 主動初始化分線資料:分線總數只在遊戲把「移動到副本區」選單列出來時才拿得到
/// (CS 的 PublicInstance 結構沒有數量欄位,也沒有任何可呼叫的請求函式;
/// 上游因此要求玩家「先手動點一次以太之光」)。
/// 這裡把同一套合法流程自動化一次:互動以太之光 → 選單出現(InstanceHandler 於此記錄數量) → 取消關閉。
/// 只在使用者明確觸發時執行(DTR 點擊/overlay 按鈕),不做無人值守的自動掃分線。
/// </summary>
public static unsafe class TaskInitInstanceData
{
    public static void Enqueue()
    {
        P.TaskManager.Enqueue(TaskChangeInstance.InteractWithAetheryte, "InitInstanceInteract", new(timeLimitMS: 30000));
        P.TaskManager.Enqueue(WaitForInstanceData, "InitInstanceWaitData", new(timeLimitMS: 15000, abortOnTimeout: false));
        P.TaskManager.Enqueue(CloseSelectString, "InitInstanceCloseMenu", new(timeLimitMS: 10000, abortOnTimeout: false));
    }

    /// <summary>
    /// 若附近沒有以太之光但本區有,先傳送過去(沿用切線的同一個選項)。
    /// </summary>
    public static void EnqueueWithTeleport()
    {
        if(TaskChangeInstance.GetAetheryte() == null && C.InstanceTpToAetheryte)
        {
            P.TaskManager.Enqueue(TaskChangeInstance.TeleportToZoneAetheryte, "InitInstanceTeleport", new(timeLimitMS: 60000));
            P.TaskManager.Enqueue(Utils.WaitForScreen);
        }
        Enqueue();
    }

    private static bool WaitForInstanceData()
    {
        if(S.InstanceHandler.IsInstanceCountConfirmed()) return true;
        // 選單還沒開就繼續等;InstanceHandler 的 PostUpdate 監聽會在開啟瞬間記錄數量
        return false;
    }

    private static bool CloseSelectString()
    {
        if(!TryGetAddonMaster<AddonMaster.SelectString>(out var m) || !m.IsAddonReady) return true;
        // 讀到 U+FFFD ＝ 選單記憶體變動中(多半是上一發 -1 之後正在關閉),這一幀不碰、不判定。
        if(AddonPressGuard.IsTextUnstable("SelectString", m.Text) || AddonPressGuard.AnyTextUnstable("SelectString", m.Entries.Select(x => x.Text))) return false;
        if(!m.Entries.Any(x => x.Text.ContainsAny(Lang.TravelToInstancedArea)) && m.Text != Lang.ToReduceCongestion) return true;
        // 「送 -1 直到窗消失」迴圈:第一次 -1 後窗關閉中仍過 IsAddonReady、Entries 仍讀得到 ⇒ 同位址只送一次(60 幀逃生口)。
        if(EzThrottler.Throttle("InitInstanceCloseMenu", 500) && AddonPressGuard.TryPressOnce("SelectString", m.Base, nameof(CloseSelectString)))
        {
            // Lifestream 既有的取消慣例(見 Schedulers/WorldChange.cs)
            Callback.Fire((AtkUnitBase*)m.Base, true, -1);
        }
        return false;
    }

    /// <summary>
    /// 現在能否執行初始化(需在分線區、附近有水晶或允許先傳送)。
    /// </summary>
    public static bool CanInitialize()
    {
        if(!Player.Interactable || P.TaskManager.IsBusy || IsOccupied()) return false;
        if(S.InstanceHandler.GetInstance() == 0) return false;
        return TaskChangeInstance.GetAetheryte() != null || (C.InstanceTpToAetheryte && TaskChangeInstance.GetZoneAetheryteId() != 0);
    }
}
