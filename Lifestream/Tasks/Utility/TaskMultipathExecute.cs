using ECommons.Automation;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lifestream.Data;
using Action = Lumina.Excel.Sheets.Action;

namespace Lifestream.Tasks.Utility;
public static unsafe class TaskMultipathExecute
{
    private static uint LastTerritory = 0;

    public static void Enqueue(MultiPath path)
    {
        LastTerritory = 0;
        P.TaskManager.Enqueue(() => Execute(path), $"ExecuteMultipath {path.Name}", TaskSettings.TimeoutInfinite);
    }

    private static bool Execute(MultiPath mpath)
    {
        if(!Player.Interactable || !IsScreenReady())
        {
            P.FollowPath.Stop();
            return false;
        }
        var path = mpath.Entries.FirstOrDefault(x => x.Territory == P.Territory);
        if(P.Territory != LastTerritory)
        {
            P.FollowPath.Stop();
            var points = (List<Vector3>)[.. path.Points];
            var distance = float.MaxValue;
            var index = 0;
            for(var i = 0; i < points.Count; i++)
            {
                if(Vector3.Distance(Player.Object.Position, points[i]) < distance)
                {
                    index = i;
                    distance = Vector3.Distance(Player.Object.Position, points[i]);
                }
            }
            points = points[index..];
            P.FollowPath.Move(points, true);
            LastTerritory = path.Territory;
        }
        else
        {
            if(Svc.Condition[ConditionFlag.InCombat] || path?.Sprint == true)
            {
                var status = ActionManager.Instance()->GetActionStatus(ActionType.Action, 3);
                if(status == 0)
                {
                    if(EzThrottler.Throttle("UseSprint", 250))
                    {
                        // 🔴 技能名不可寫死英文：台服 Action row 3 的 Name 是「衝刺」，
                        //    送 "/action Sprint" 在台服永遠不會發動，而外層還會每 250ms
                        //    重送一次失敗的指令。改讀 Excel 表，寫法照本 repo 既有的
                        //    TaskMoveToHouse.UseSprint。指令名 /action 本身台服保留英文，不要改。
                        //    用 GetRowOrDefault：本 pin 的 Lumina GetRow 對不存在的 row 會擲例外，
                        //    這裡是每幀跑的路徑，寧可不衝刺也不要每幀丟例外。
                        var sprintName = Svc.Data.GetExcelSheet<Action>().GetRowOrDefault(3)?.Name.GetText();
                        if(!sprintName.IsNullOrEmpty())
                        {
                            Chat.ExecuteCommand($"/action \"{sprintName}\"");
                        }
                    }
                }
            }
            P.FollowPath.UpdateTimeout(10);
            if(P.FollowPath.Waypoints.Count == 0)
            {
                P.NotificationMasterApi.DisplayTrayNotification("Multipath completed");
                return true;
            }
        }
        return false;
    }
}
