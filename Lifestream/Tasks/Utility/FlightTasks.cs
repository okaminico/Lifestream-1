using ECommons.Automation;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lifestream.Tasks.Utility;
public static unsafe class FlightTasks
{
    public static bool? FlyIfCan()
    {
        if(Svc.Condition[ConditionFlag.InFlight])
        {
            return true;
        }
        if(Utils.CanFly())
        {
            if(ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 2) == 0 && EzThrottler.Throttle("Jump", 100))
            {
                Chat.ExecuteGeneralAction(2);
            }
        }
        else
        {
            return null;
        }
        return false;
    }

    /// <summary>
    /// <see cref="FlyIfCan"/> 的「放棄飛行也不中止」包裝。
    ///
    /// 🔴 <see cref="FlyIfCan"/> 在**不可飛的區域回 null**,而 NeoTaskManager 對 null 的語意是
    ///    「中止整條佇列」(<c>TaskManager.Tick</c>:<c>result == null</c> ⇒ <c>Abort()</c>)。
    ///    所以裸排 <c>FlyIfCan</c> 等於「只要人在不能飛的地方,整條任務鏈就靜默斷掉」——
    ///    使用者看到的是「勾了『使用飛行』的自訂別名一到某些區域就完全沒反應」,而且沒有任何訊息。
    ///    這裡把那個 null 轉成 true(＝這一步做完了,只是沒飛起來),讓後面的步驟照樣用走的。
    ///
    /// ⚠️ 其餘語意逐字不變:已在空中回 true,還在起飛回 false(讓呼叫端的逾時去管)。
    /// </summary>
    public static bool FlyIfCanOrGiveUp()
    {
        var result = FlyIfCan();
        if(result == null)
        {
            // 使用者跑 LogLevel 1,「為什麼沒飛起來」正是他會來問的事,寫 Debug 會被單檔數十萬行淹沒。
            PluginLog.Information("[Flight] 這個區域現在不允許飛行,放棄起飛、改用地面移動。");
            return true;
        }
        return result.Value;
    }
}
