
using ECommons.Automation;
using ECommons.Automation.UIInput;
using ECommons.GameHelpers;
using Callback = ECommons.Automation.Callback;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lifestream.Schedulers;

namespace Lifestream.Tasks.SameWorld;
public static unsafe class TaskGoToResidentialDistrict
{
    public static void Enqueue(int ward)
    {
        if(ward < 1 || ward > 30) throw new ArgumentOutOfRangeException(nameof(ward));
        if(C.WaitForScreenReady) P.TaskManager.Enqueue(Utils.WaitForScreen);
        P.TaskManager.Enqueue(WorldChange.TargetValidAetheryte);
        P.TaskManager.Enqueue(WorldChange.InteractWithTargetedAetheryte);
        P.TaskManager.Enqueue(() => Utils.TrySelectSpecificEntry(Lang.ResidentialDistrict, () => EzThrottler.Throttle("SelectResidentialDistrict")), $"TaskGoToResidentialDistrictSelect {Lang.ResidentialDistrict}");
        P.TaskManager.Enqueue(() => Utils.TrySelectSpecificEntry(Lang.GoToWard, () => EzThrottler.Throttle("SelectGoToWard")), $"TaskGoToResidentialDistrictSelect {Lang.GoToWard}");
        if(ward > 1) P.TaskManager.Enqueue(() => SelectWard(ward));
        P.TaskManager.Enqueue(GoToWard);
        P.TaskManager.Enqueue(ConfirmYesNoGoToWard);
        P.TaskManager.EnqueueTask(new(() => Player.Interactable && S.Data.ResidentialAethernet.IsInResidentialZone(), "Wait until player arrives"));
    }

    public static bool ConfirmYesNoGoToWard()
    {
        if(Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51]) return true;
        var x = (AddonSelectYesno*)Utils.GetSpecificYesno(true, Lang.TravelTo);
        if(x != null)
        {
            if(IsButtonEnabled(x->YesButton) && EzThrottler.Throttle("ConfirmTravelTo") && AddonPressGuard.TryPressOnce("SelectYesno", x, nameof(ConfirmYesNoGoToWard)))
            {
                new AddonMaster.SelectYesno(x).Yes();
                return true;
            }
        }
        return false;
    }

    public static bool? SelectWard(int ward)
    {
        if(TryGetAddonByName<AtkUnitBase>("HousingSelectBlock", out var addon) && IsAddonReady(addon))
        {
            if(ward == 1)
            {
                return true;
            }
            else
            {
                // 換頁不關窗 ⇒ 粒度含頁碼、走多次互動窗的逃生口;之後 GoToWard 對同一扇窗按的確認鈕是「回答」(不帶參數組)。
                if(EzThrottler.Throttle("HousingSelectBlockSelectWard") && AddonPressGuard.TryPressOnce("HousingSelectBlock", addon, nameof(SelectWard), paramKey: $"1|{ward - 1}", escapeIsRoutine: true))
                {
                    Callback.Fire(addon, true, 1, ward - 1);
                    return true;
                }
            }
        }
        return false;
    }

    public static bool? GoToWard()
    {
        if(TryGetAddonByName<AtkUnitBase>("HousingSelectBlock", out var addon) && IsAddonReady(addon))
        {
            var button = addon->GetComponentButtonById(34);
            if(IsButtonEnabled(button))
            {
                if(EzThrottler.Throttle("HousingSelectBlockConfirm") && AddonPressGuard.TryPressOnce("HousingSelectBlock", addon, nameof(GoToWard)))
                {
                    button->ClickAddonButton(addon);
                    return true;
                }
            }
        }
        return false;
    }
}
