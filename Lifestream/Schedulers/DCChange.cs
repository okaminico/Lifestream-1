using Dalamud.Utility;
using ECommons.Automation;
using ECommons.Automation.UIInput;
using Callback = ECommons.Automation.Callback;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lifestream.AtkReaders;
using Lifestream.Tasks.CrossDC;
using Lifestream.Tasks.Login;
using Lumina.Excel.Sheets;

namespace Lifestream.Schedulers;

internal static unsafe class DCChange
{
    internal static bool DCThrottle => FrameThrottler.Throttle("DCOperation", 10);
    internal static bool DCRethrottle() => FrameThrottler.Throttle("DCOperation", 10, true);

    internal static bool? WaitUntilNotBusy()
    {
        if(!Player.Available) return false;
        return Player.Object.CastActionId == 0 && !IsOccupied() && Player.Object.IsTargetable;
    }

    internal static bool? Logout()
    {
        if(DCThrottle)
        {
            DCRethrottle();
            PluginLog.Debug($"[DCChange] Sending logout command");
            Chat.SendMessage("/logout");
            return true;
        }
        return false;
    }

    internal static bool? SelectYesLogin()
    {
        if(Svc.ClientState.IsLoggedIn)
        {
            return true;
        }
        {
            if(TryGetAddonByName<AtkUnitBase>("SelectOk", out var addon) && IsAddonReady(addon))
            {
                return true;
            }
        }
        {
            var addon = Utils.GetSpecificYesno(true, Lang.LogInPartialText);
            if(addon == null || !IsAddonReady(addon))
            {
                DCRethrottle();
                return false;
            }
            if(DCThrottle && AddonPressGuard.TryPressOnce("SelectYesno", addon, nameof(SelectYesLogin)))
            {
                PluginLog.Debug($"[DCChange] Confirming login");
                new AddonMaster.SelectYesno(addon).Yes();
                return false;
            }
            else
            {
                return false;
            }
        }
    }

    internal static bool? SelectYesLogout()
    {
        if(!Svc.ClientState.IsLoggedIn)
        {
            return true;
        }

        var addon = Utils.GetLogOutYesno();
        if(addon == null || !IsAddonReady(addon))
        {
            DCRethrottle();
            return false;
        }
        if(DCThrottle && AddonPressGuard.TryPressOnce("SelectYesno", addon, nameof(SelectYesLogout)))
        {
            PluginLog.Debug($"[DCChange] Confirming logout");
            Callback.Fire(addon, true, 0);
            return true;
        }
        else
        {
            return false;
        }
    }


    internal static bool? SelectCharacter(string name, uint world)
    {
        {
            // Select Character
            var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("_CharaSelectListMenu", 1).Address;
            PluginLog.Debug($"Select1");
            if(addon == null) return false;
            PluginLog.Debug($"Select1-1");
            //if (!AgentLobby.Instance()->AgentInterface.IsAgentActive()) return false;
            PluginLog.Debug($"Select2");
            // AgentLobby 取得器合法回 null。拿不到就當作「還沒準備好」回 false,
            // 讓這個工作下一幀重試 —— 比對 null 解參考 TemporaryLocked 安全。
            var lobby = AgentLobby.Instance();
            if(lobby == null) return false;
            if(lobby->TemporaryLocked) return false;
            PluginLog.Debug($"Select3");
            if(Utils.TryGetCharacterIndex(name, world, out var index))
            {
                PluginLog.Debug($"Select4/{index}");
                if(DCThrottle && EzThrottler.Check("CharaSelectListMenuError") && AddonPressGuard.TryPressOnce("_CharaSelectListMenu", addon, nameof(SelectCharacter), paramKey: $"29|0|{index}", escapeIsRoutine: true))
                {
                    PluginLog.Debug($"[DCChange] Selecting character index {index}");
                    Callback.Fire(addon, false, (int)29, (int)0, (int)index);
                }
                var nextAddon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("SelectYesno", 1).Address;
                return nextAddon != null;
            }
            else
            {
                DCRethrottle();
            }
        }
        return false;
    }

    internal static bool? WaitUntilCanAutoLogin()
    {
        return Utils.CanAutoLogin();
    }

    internal static bool? TitleScreenClickStart()
    {
        if(!Utils.CanAutoLogin())
        {
            DCRethrottle();
            return true;
        }
        // 按 Start 後 _TitleMenu 關閉、任務不終結;關閉中的 _TitleMenu 仍過 IsAddonReady ⇒ 同位址只按一次(被擋走既有的 else DCRethrottle 路)。
        if(Utils.CanAutoLogin() && TryGetAddonByName<AtkUnitBase>("_TitleMenu", out var title) && IsAddonReady(title) && DCThrottle && EzThrottler.Throttle("TitleScreenClickStart") && AddonPressGuard.TryPressOnce("_TitleMenu", title, nameof(TitleScreenClickStart)))
        {
            PluginLog.Debug($"[DCChange] Clicking start");
            Callback.Fire(title, true, (int)4);
            DCRethrottle();
            return false;
        }
        else
        {
            DCRethrottle();
        }
        return false;
    }

    internal static bool? OpenContextMenuForChara(string name, uint homeWorld, uint currentLoginWorld)
    {
        if(TryGetAddonByName<AddonContextMenu>("ContextMenu", out var m) && IsAddonReady(&m->AtkUnitBase))
        {
            DCRethrottle();
            return true;
        }
        if(TryGetAddonByName<AtkUnitBase>("_CharaSelectListMenu", out var addon) && IsAddonReady(addon))
        {
            TaskChangeCharacter.SelectCharacter(name, ExcelWorldHelper.GetName(homeWorld), ExcelWorldHelper.GetName(currentLoginWorld), true);
        }
        else
        {
            DCRethrottle();
        }
        return false;
    }

    internal static bool? SelectVisitAnotherDC()
    {
        if(TryGetAddonMaster<AddonMaster.ContextMenu>(out var m) && m.IsAddonReady)
        {
            if(AddonPressGuard.AnyTextUnstable("ContextMenu", m.Entries.Select(x => x.Text))) return false;
            if(m.Entries.TryGetFirst(x => x.Enabled && x.Text == Svc.Data.GetExcelSheet<Lobby>().GetRow(1150).Text.GetText(), out var entry) && DCThrottle && EzThrottler.Throttle("SelectVisitAnotherDC") && AddonPressGuard.TryPressOnce("ContextMenu", m.Base, nameof(SelectVisitAnotherDC)))
            {
                PluginLog.Debug($"[DCChange] Selecting visit another data center");
                entry.Select();
                return true;
            }
        }
        else
        {
            DCRethrottle();
        }
        return false;
    }

    internal static bool? SelectReturnToHomeWorld()
    {
        if(TryGetAddonMaster<AddonMaster.ContextMenu>(out var m) && m.IsAddonReady)
        {
            if(AddonPressGuard.AnyTextUnstable("ContextMenu", m.Entries.Select(x => x.Text))) return false;
            if(m.Entries.TryGetFirst(x => x.Enabled && x.Text == Svc.Data.GetExcelSheet<Lobby>().GetRow(1117).Text.GetText(), out var entry) && DCThrottle && EzThrottler.Throttle("SelectReturnToHomeWorld") && AddonPressGuard.TryPressOnce("ContextMenu", m.Base, nameof(SelectReturnToHomeWorld)))
            {
                PluginLog.Debug($"[DCChange] Selecting return to home world");
                entry.Select();
                return true;
            }
        }
        else
        {
            DCRethrottle();
        }
        return false;
    }

    internal static bool? ConfirmDcVisitIntention()
    {
        if(TryGetAddonByName<AtkUnitBase>("LobbyDKTCheck", out var addon) && IsAddonReady(addon) && IsButtonEnabled(GetNodeListButton(addon, 3)))
        {
            if(DCThrottle && AddonPressGuard.TryPressOnce("LobbyDKTCheck", addon, nameof(ConfirmDcVisitIntention)))
            {
                PluginLog.Debug($"[DCChange] Confirming DC visit intention");
                Callback.Fire(addon, true, 0);
                return true;
            }
        }
        else
        {
            DCRethrottle();
        }
        return false;
    }

    internal static bool? SelectTargetDataCenter(string name)
    {
        if(TryGetAddonByName<AtkUnitBase>("LobbyDKTWorldList", out var addon) && IsAddonReady(addon))
        {
            var reader = new ReaderLobbyDKTWorldList(addon);
            // ⚠️ cw 讀了之後在本方法裡從來沒被用過(上游就是這樣)。沒有明確指示不回退既有行為,
            // 所以保留這次讀取,只補上判空。
            TryGetNodeText(addon, 13, out var cw);
            if(reader.SelectedDataCenter == name)
            {
                PluginLog.Information($"SelectTargetDataCenter complete");
                return true;
            }
            // 🔴 SearchNodeById 找不到時回 null,而 GetAsAtkComponentNode() 是原生 member function
            // (this 走 RCX),對 null 呼叫＝當場 AVE。Component(偏移 0xB0)也是可為 null 的指標欄位。
            // 這裡刻意**不提早 return** —— 讓後面的 reader.Regions.Count == 0 → DCRethrottle 照跑,
            // 控制流完全不變;取不到時 listUld 留 null,迴圈裡的 GetNodeSafe 自然一路跳過。
            var listNode = addon->UldManager.SearchNodeById(21);
            var list = listNode == null ? null : listNode->GetAsAtkComponentNode();
            AtkUldManager* listUld = null;
            if(list != null && list->Component != null) listUld = &list->Component->UldManager;
            var addonItem = 0;
            var listIndex = 3;
            var category = 0;
            var categoryIndex = 0;
            foreach(var region in reader.Regions)
            {
                addonItem++;
                categoryIndex = 1;
                foreach(var dc in region.DataCenters)
                {
                    if(dc.Name == name)
                    {
                        // 🔴 五跳裸鏈。真正的炸點是下一行的 t->AtkResNode.Alpha_2 ——
                        // 取節點那一行算出毒指標時不會崩,要到讀 Alpha_2／NodeText 才炸。
                        var t = GetTextNodeSafe(GetComponentNodeSafe(GetNodeSafe(listUld, listIndex), 8));
                        if(t != null && t->AtkResNode.Alpha_2 == 255)
                        {
                            var text = GenericHelpers.ReadSeString(&t->NodeText).GetText();
                            // 讀到 U+FFFD ＝ 窗記憶體變動中,這一幀不碰(回 false 走既有的「下一輪再試」路徑)。
                            if(AddonPressGuard.IsTextUnstable("LobbyDKTWorldList", text)) return false;
                            if(text == name && DCThrottle && EzThrottler.Throttle("SelectTargetDataCenter"))
                            {
                                PluginLog.Debug($"[DCChange] Selecting Target DC {name} index {addonItem} list {listIndex}");
                                S.Memory.ConstructEvent(addon, category, 1, 7, categoryIndex, addonItem);
                                DCRethrottle();
                                return false;
                            }
                        }
                    }
                    addonItem++;
                    listIndex++;
                    categoryIndex++;
                }
                category++;
            }
            if(reader.Regions.Count == 0) DCRethrottle();
        }
        else
        {
            DCRethrottle();
        }
        return false;
    }

    internal static bool? SelectTargetWorld(string name, Func<bool> noAvailableWorldsAction)
    {
        if(TryGetAddonByName<AtkUnitBase>("LobbyDKTWorldList", out var addon) && IsAddonReady(addon))
        {
            // cw 讀不到時**刻意不提早 return**:回 false 會把下面整段清單掃描與
            // noAvailableWorldsAction 一起跳掉,而「讀不到目前世界名稱」不代表沒事可做 ——
            // 掃清單本來就能找到目標世界並選下去。讀不到就留空字串,
            // 讓 cw == name 單純比不中(＝「還沒確認到站」),控制流與原本一字不差。
            TryGetNodeText(addon, 10, out var cw);
            if(cw == name || (C.DcvUseAlternativeWorld && cw.EqualsAny(PublicWorlds.Get(Utils.GetDataCenter(name).RowId).Select(w => w.Name.ToString()))))
            {
                return true;
            }
            // 取不到清單時 listUld 留 null,兩個迴圈裡的 GetNodeSafe 會一路跳過 ——
            // num 維持 0 ⇒ 照樣走到既有的 DCRethrottle 與 noAvailableWorldsAction,控制流不變。
            var listNode = GetNodeSafe(&addon->UldManager, 6);
            var list = listNode == null ? null : listNode->GetAsAtkComponentNode();
            AtkUldManager* listUld = null;
            if(list != null && list->Component != null) listUld = &list->Component->UldManager;
            var num = 0;
            for(var i = 3; i < 3 + 8; i++)
            {
                // 🔴 五跳裸鏈;炸點在下一行讀 Alpha_2 的時候,不是在取節點那一行。
                var t = GetTextNodeSafe(GetComponentNodeSafe(GetNodeSafe(listUld, i), 8));
                if(t != null && t->AtkResNode.Alpha_2 == 255)
                {
                    var text = GenericHelpers.ReadSeString(&t->NodeText).GetText();
                    if(AddonPressGuard.IsTextUnstable("LobbyDKTWorldList", text)) return false;
                    if(text != "") num++;
                    if(text == name && DCThrottle && EzThrottler.Throttle("SelectTargetWorld"))
                    {
                        PluginLog.Debug($"[DCChange] Selecting target world {name} index {i}");
                        S.Memory.ConstructEvent(addon, 0, 2, 6, i - 2, i - 2);
                        DCRethrottle();
                        return false;
                    }
                }
            }
            if(C.DcvUseAlternativeWorld)
            {
                for(var i = 3; i < 3 + 8; i++)
                {
                    // 🔴 同上(替代世界那一輪)。
                    var t = GetTextNodeSafe(GetComponentNodeSafe(GetNodeSafe(listUld, i), 8));
                    if(t != null && t->AtkResNode.Alpha_2 == 255)
                    {
                        var text = GenericHelpers.ReadSeString(&t->NodeText).GetText();
                        if(AddonPressGuard.IsTextUnstable("LobbyDKTWorldList", text)) return false;
                        if(text != "") num++;
                        if(text.EqualsAny(PublicWorlds.Get(Utils.GetDataCenter(name).RowId).Select(w => w.Name.ToString())) && DCThrottle && EzThrottler.Throttle("SelectTargetWorld"))
                        {
                            PluginLog.Debug($"[DCChange] Selecting alternative target world {name} index {i}");
                            S.Memory.ConstructEvent(addon, 0, 2, 6, i - 2, i - 2);
                            DCRethrottle();
                            return false;
                        }
                    }
                }
            }
            if(num == 0)
            {
                DCRethrottle();
            }
            if(noAvailableWorldsAction != null && TryGetAddonByName<AtkUnitBase>("LobbyDKTWorldList", out var addon2) && IsAddonReady(addon2) && IsButtonEnabled(GetNodeListButton(addon2, 4)))
            {
                var result = noAvailableWorldsAction();
                if(result) return true;
            }
        }
        else
        {
            DCRethrottle();
        }
        return false;
    }

    internal static bool? CancelDcVisit()
    {
        if(TryGetAddonByName<AtkUnitBase>("LobbyDKTWorldList", out var addon) && IsAddonReady(addon))
        {
            if(IsButtonEnabled(GetNodeListButton(addon, 4)))
            {
                if(DCThrottle && EzThrottler.Throttle("CancelDcVisit", 5000))
                {
                    var button = GetNodeListButton(addon, 4);
                    // 不帶參數組的 key ＝「這扇窗已被我們關掉」:窗消失前對同位址的清單選取(帶參數組)也一律不准再送。
                    if(button != null && AddonPressGuard.TryPressOnce("LobbyDKTWorldList", addon, nameof(CancelDcVisit)))
                    {
                        PluginLog.Debug($"[DCChange] Cancelling DC visit");
                        button->ClickAddonButton(addon);
                        return true;
                    }
                }
            }
            else
            {
                DCRethrottle();
            }
        }
        else
        {
            DCRethrottle();
        }
        return false;
    }

    internal static bool? ConfirmDcVisit()
    {
        if(TryGetAddonByName<AtkUnitBase>("LobbyDKTWorldList", out var addon) && IsAddonReady(addon))
        {
            if(IsButtonEnabled(GetNodeListButton(addon, 5)))
            {
                if(DCThrottle && EzThrottler.Throttle("ConfirmDcVisit", 5000) && AddonPressGuard.TryPressOnce("LobbyDKTWorldList", addon, nameof(ConfirmDcVisit)))
                {
                    PluginLog.Debug($"[DCChange] Confirming DC visit");
                    Callback.Fire(addon, true, (int)4);
                    return true;
                }
            }
            else
            {
                DCRethrottle();
            }
        }
        else
        {
            DCRethrottle();
        }
        return false;
    }

    internal static bool? ConfirmDcVisit2(string destination, string charaName, uint charaWorld, uint currentLoginWorld)
    {
        if(TryGetAddonByName<AtkUnitBase>("LobbyDKTCheckExec", out var addon) && IsAddonReady(addon))
        {
            if(IsButtonEnabled(GetNodeListButton(addon, 3)))
            {
                if(DCThrottle && EzThrottler.Throttle("ConfirmDcVisit", 5000) && AddonPressGuard.TryPressOnce("LobbyDKTCheckExec", addon, nameof(ConfirmDcVisit2)))
                {
                    PluginLog.Debug($"[DCChange] Confirming DC visit 2");
                    Callback.Fire(addon, true, (int)0);
                    return true;
                }
            }
            else
            {
                DCRethrottle();
            }
        }
        else
        {
            DCRethrottle();
        }
        if(destination != null) TaskChangeDatacenter.ProcessUnableDialogue(destination, charaName, charaWorld, currentLoginWorld);
        return false;
    }

    internal static bool? SelectOk()
    {
        if(TryGetAddonByName<AtkUnitBase>("SelectOk", out var addon) && IsAddonReady(addon))
        {
            if(DCThrottle && EzThrottler.Throttle("SelectOk", 500) && AddonPressGuard.TryPressOnce("SelectOk", addon, nameof(SelectOk)))
            {
                PluginLog.Debug($"[DCChange] Selecting OK");
                Callback.Fire(addon, true, (int)0);
                return true;
            }
        }
        else
        {
            DCRethrottle();
        }
        return false;
    }

    internal static bool? SelectServiceAccount(int account)
    {
        var dcMenu = (AtkUnitBase*)Svc.GameGui.GetAddonByName("TitleDCWorldMap", 1).Address;
        // Close(true) 的 true 就是 fireCallback:第一次 Close 後窗進入關閉中,下一 tick 仍 non-null 再 Close 一次
        // 是同一類存取違規 ⇒ 同位址只關一次,窗消失前不再碰。
        if(dcMenu != null && AddonPressGuard.TryPressOnce("TitleDCWorldMap", dcMenu, "SelectServiceAccount.CloseDCMap")) dcMenu->Close(true);
        if(TryGetAddonByName<AtkUnitBase>("_CharaSelectWorldServer", out _))
        {
            return true;
        }
        if(TryGetAddonByName<AddonSelectString>("SelectString", out var addon) && IsAddonReady(&addon->AtkUnitBase)
            && addon->AtkUnitBase.UldManager.NodeListCount >= 4)
        {
            // 上界(NodeListCount >= 4)原本就有,缺的是**元素判空**與 GetAsAtkTextNode 的 null this
            // —— 兩者是不同的關卡,只做上界擋不住元素為 null。
            // 讀不到就回 false(等下一輪),不要拿空字串去和 Lobby 表比對後選錯服務帳號。
            if(!TryGetNodeText(&addon->AtkUnitBase, 3, out var text)) return false;
            if(AddonPressGuard.IsTextUnstable("SelectString", text)) return false;
            var compareTo = Svc.Data.GetExcelSheet<Lobby>()?.GetRow(11).Text.ToString();
            if(text == compareTo)
            {
                if(!AddonPressGuard.TryPressOnce("SelectString", addon, nameof(SelectServiceAccount), paramKey: account.ToString())) return false;
                PluginLog.Information($"Selecting service account");
                new AddonMaster.SelectString(addon).Entries[account].Select();
                return true;
            }
            else
            {
                PluginLog.Information($"Found different SelectString: {text}");
                return false;
            }
        }
        return false;
    }
}
