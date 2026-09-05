using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation.NeoTaskManager.Tasks;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lifestream.Data;
using Lifestream.Tasks.Utility;

namespace Lifestream.Tasks.Shortcuts;
public static unsafe class TaskISShortcut
{
    public enum IslandNPC : uint
    {
        Baldin = 1043621,
        TactfulTaskmaster = 1043078,
        ExcitableExplorer = 1043081,
        EnterprisingExporter = 1043464,
        HorrendousHoarder = 1043463,
        FelicitousFurball = 1043473,
        CreatureComforter = 1043466,
        ProduceProducer = 1043465,
    }

    public static class IslandTerritories
    {
        public const uint Moraby = 135;
        public const uint Island = 1055;
    }

    public static readonly Dictionary<IslandNPC, Vector3> NPCPoints = new()
    {
        [IslandNPC.Baldin] = new(173.05f, 14.10f, 668.42f),
        [IslandNPC.TactfulTaskmaster] = new(-278.37f, 40.00f, 229.82f),
        [IslandNPC.ExcitableExplorer] = new(-265.44f, 40.00f, 234.52f),
        [IslandNPC.EnterprisingExporter] = new(-265.72f, 41.01f, 210.44f),
        [IslandNPC.HorrendousHoarder] = new(-265.72f, 41.01f, 210.44f),
        [IslandNPC.FelicitousFurball] = new(-272.06f, 41.00f, 212.32f),
        [IslandNPC.CreatureComforter] = new(-268.60f, 55.20f, 134.44f),
        [IslandNPC.ProduceProducer] = new(-258.16f, 55.20f, 135.01f),
    };

    public static void Enqueue(IslandNPC? npcNullable = null, bool returnHome = true)
    {
        if(P.TaskManager.IsBusy)
        {
            DuoLog.Error($"Lifestream is busy, could not process request");
            return;
        }
        if(!Player.Available)
        {
            DuoLog.Error("Player not available");
            return;
        }
        if(returnHome)
        {
            if(!Player.IsInHomeWorld)
            {
                P.TPAndChangeWorld(Player.HomeWorld, !Player.IsInHomeDC, null, true, null, false, false);
            }
            P.TaskManager.Enqueue(() => Player.Interactable && Player.IsInHomeWorld && IsScreenReady());
        }
        var point = npcNullable == null ? NPCPoints[IslandNPC.TactfulTaskmaster] : NPCPoints[npcNullable.Value];
        P.TaskManager.Enqueue(() =>
        {
            if(P.Territory == IslandTerritories.Island)
            {
                EnqueueNavToNPC(point);
            }
            else
            {
                TravelToIsland();
            }
        });

        IGameObject baldin() => Svc.Objects.FirstOrDefault(x => x.BaseId == (uint)IslandNPC.Baldin);

        void TravelToIsland()
        {
            StaticAlias.IslandSanctuary.Enqueue(true);
            P.TaskManager.EnqueueTask(NeoTasks.ApproachObjectViaAutomove(baldin, 6.5f));
            P.TaskManager.EnqueueTask(NeoTasks.InteractWithObject(baldin));
            P.TaskManager.Enqueue(TalkWithBaldin);
            P.TaskManager.Enqueue(ConfirmIslandTravel);
            P.TaskManager.Enqueue(() => Player.Interactable && IsScreenReady() && P.Territory == IslandTerritories.Island, "WaitUntilPlayerInteractableOnIsland", TaskSettings.Timeout2M);
            P.TaskManager.Enqueue(() => EnqueueNavToNPC(point));
        }

        bool TalkWithBaldin()
        {
            // Talk 走艦隊 15 幀政策(AddonPressGuard 與 Framework_Update 那一站共用同一把 key,同幀不會點兩次);
            // 原本連 IsAddonReady 都沒檢查,對未就緒/關閉中的 Talk 送 ReceiveEvent 是攔不到的 AccessViolation。
            if(TryGetAddonMaster<AddonMaster.Talk>(out var talk) && talk.IsAddonReady
                && AddonPressGuard.TryPressOnce("Talk", talk.Base, nameof(TalkWithBaldin), escapeIsRoutine: true))
                talk.Click();
            return Utils.TrySelectSpecificEntry(Lang.TravelToMyIsland, () => EzThrottler.Throttle(nameof(TalkWithBaldin)));
        }

        bool ConfirmIslandTravel()
        {
            var addon = (AddonSelectYesno*)Utils.GetSpecificYesno(true, Lang.TravelToYourIsland);
            if(addon != null && IsButtonEnabled(addon->YesButton))
            {
                if(EzThrottler.Throttle(nameof(ConfirmIslandTravel), 5000) && AddonPressGuard.TryPressOnce("SelectYesno", addon, nameof(ConfirmIslandTravel)))
                {
                    new AddonMaster.SelectYesno(addon).Yes();
                    return true;
                }
            }
            return false;
        }

        void EnqueueNavToNPC(Vector3 point)
        {
            P.TaskManager.Enqueue(Utils.WaitForScreen);
            P.TaskManager.Enqueue(() =>
            {
                if(!(Svc.PluginInterface.InstalledPlugins.Any(x => x.InternalName == "vnavmesh" && x.IsLoaded)))
                {
                    PluginLog.Warning($"Navmesh not found, will not continue navigation");
                    return null;
                }
                else
                {
                    return true;
                }
            });
            P.TaskManager.Enqueue(S.Ipc.VnavmeshIPC.IsReady);
            P.TaskManager.Enqueue(() =>
            {
                var task = S.Ipc.VnavmeshIPC.Pathfind(Player.Position, point, false);
                P.TaskManager.Enqueue(() =>
                {
                    if(!task.IsCompleted) return false;
                    var path = task.Result;
                    P.TaskManager.Enqueue(() => TaskMoveToHouse.UseSprint(false));
                    P.TaskManager.Enqueue(() => P.FollowPath.Move([.. path], true));
                    return true;
                }, "Build path");
            }, "Master navmesh task");
        }
    }
}
