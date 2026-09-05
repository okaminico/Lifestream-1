using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using ECommons.GameFunctions;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Lumina.Excel.Sheets;

namespace Lifestream.Tasks.SameWorld;
public static unsafe class TaskChangeInstance
{
    public static readonly char[] InstanceNumbers = "\0".ToCharArray();

    public static void Enqueue(int number)
    {
        // 切線後重新上坐騎(參考 DR FastInstanceZoneChange 的 MountAfterChange;預設關)
        var remount = C.InstanceRemount && Svc.Condition[ConditionFlag.Mounted];
        var remountId = remount ? Svc.Objects.LocalPlayer?.CurrentMount?.RowId ?? 0 : 0;

        var tasks = new TaskManagerTask[]
        {
            new(InteractWithAetheryte),
            new(SelectTravel),
            new(() => SelectInstance(number), $"SelectInstance({number})"),
            new(() => !IsOccupied()),
            new(() =>
            {
                if(C.InstanceSwitcherRepeat && number != S.InstanceHandler.GetInstance())
                {
                    Enqueue(number);
                }
            })
        };
        if(C.EnableFlydownInstance)
        {
            P.TaskManager.Enqueue(() =>
            {
                if(!Svc.Condition[ConditionFlag.InFlight])
                {
                    return true;
                }
                if(EzThrottler.Throttle("DropFlight", 1000))
                {
                    Chat.ExecuteCommand($"/generalaction {Svc.Data.GetExcelSheet<GeneralAction>().GetRow(23).Name}");
                }
                return false;
            });
        }
        // 附近沒有以太之光時先傳送到本區以太之光再切線(參考 DR 的 TeleportIfNotNearAetheryte;預設關)
        if(C.InstanceTpToAetheryte)
        {
            P.TaskManager.Enqueue(TeleportToZoneAetheryte, "TeleportToZoneAetheryte", new(timeLimitMS: 60000));
        }
        P.TaskManager.EnqueueMulti(tasks);
        if(remount)
        {
            P.TaskManager.Enqueue(() => RemountAfterChange(remountId, number), "RemountAfterChange", new(timeLimitMS: 30000, abortOnTimeout: false));
        }
    }

    public static bool TeleportToZoneAetheryte()
    {
        if(GetAetheryte() != null) return true;
        if(Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51] || Svc.Condition[ConditionFlag.Casting]) return false;
        if(!Player.Interactable) return false;
        var target = GetZoneAetheryteId();
        if(target == 0) throw new InvalidOperationException("No unlocked aetheryte found in this zone");
        if(EzThrottler.Throttle("InstanceTpToAetheryte", 5000))
        {
            S.TeleportService.TeleportToAetheryte(target);
        }
        return false;
    }

    public static uint GetZoneAetheryteId()
    {
        foreach(var x in Svc.AetheryteList)
        {
            if(x.AetheryteData.ValueNullable?.IsAetheryte == true && x.AetheryteData.Value.Territory.RowId == P.Territory)
            {
                return x.AetheryteId;
            }
        }
        return 0;
    }

    public static bool RemountAfterChange(uint mountId, int number)
    {
        if(S.InstanceHandler.GetInstance() != number) return true;
        if(Svc.Condition[ConditionFlag.Mounted]) return true;
        if(!IsScreenReady() || !Player.Interactable) return false;
        if(FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance()->GetActionStatus(FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction, 9) != 0) return true;
        if(!Player.IsAnimationLocked && EzThrottler.Throttle("InstanceRemount", 1000))
        {
            if(mountId != 0)
            {
                FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance()->UseAction(FFXIVClientStructs.FFXIV.Client.Game.ActionType.Mount, mountId);
            }
            else
            {
                Chat.ExecuteGeneralAction(9);
            }
        }
        return false;
    }

    public static bool SelectInstance(int num)
    {
        if(TryGetAddonMaster<AddonMaster.SelectString>(out var m) && m.IsAddonReady)
        {
            foreach(var x in m.Entries)
            {
                var text = x.Text;
                // 讀到 U+FFFD ＝ 選單記憶體變動中(多半是上一層選單關閉中),這一幀不碰。
                if(AddonPressGuard.IsTextUnstable("SelectString", text)) return false;
                if(text.Contains(InstanceNumbers[num]))
                {
                    if(EzThrottler.Throttle("SelectTravelToInstance") && AddonPressGuard.TryPressOnce("SelectString", m.Base, nameof(SelectInstance), paramKey: x.Index.ToString()))
                    {
                        x.Select();
                        return true;
                    }
                    return false;
                }
            }
        }
        return false;
    }

    public static bool SelectTravel()
    {
        if(TryGetAddonMaster<AddonMaster.SelectString>(out var m) && m.IsAddonReady)
        {
            foreach(var x in m.Entries)
            {
                var text = x.Text;
                if(AddonPressGuard.IsTextUnstable("SelectString", text)) return false;
                if(text.ContainsAny(Lang.TravelToInstancedArea))
                {
                    // 按後下一 tick 的 SelectInstance 又掃 SelectString:粒度含索引,第一層選單關閉中若被誤讀出分線字形也不會撞同索引。
                    if(EzThrottler.Throttle("SelectTravelToInstancedArea") && AddonPressGuard.TryPressOnce("SelectString", m.Base, nameof(SelectTravel), paramKey: x.Index.ToString()))
                    {
                        x.Select();
                        return true;
                    }
                }
            }
        }
        return false;
    }

    public static bool InteractWithAetheryte()
    {
        if(Svc.Condition[ConditionFlag.OccupiedInQuestEvent]) return true;
        if(!Utils.DismountIfNeeded()) return false;
        var aetheryte = GetAetheryte() ?? throw new NullReferenceException();
        if(aetheryte.IsTarget())
        {
            if(EzThrottler.Throttle("InteractWithAetheryte"))
            {
                TargetSystem.Instance()->InteractWithObject(aetheryte.Struct(), false);
                return false;
            }
        }
        else
        {
            if(EzThrottler.Throttle("AetheryteSetTarget"))
            {
                Svc.Targets.Target = aetheryte;
                return false;
            }
        }
        return false;
    }

    public static IGameObject GetAetheryte()
    {
        foreach(var x in Svc.Objects)
        {
            if(x.ObjectKind == ObjectKind.Aetheryte && x.IsTargetable)
            {
                if(Vector3.Distance(x.Position, Player.Position) < 11f)
                {
                    return x;
                }
            }
        }
        return null;
    }
}
