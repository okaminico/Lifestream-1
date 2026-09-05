using ECommons.Automation;
using ECommons.Automation.UIInput;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace Lifestream.Tasks.Login;
public static unsafe class TaskChangeCharacter
{
    public static void Enqueue(string currentLoginWorld, string charaName, string charaWorld, int account)
    {
        if(Svc.ClientState.IsLoggedIn)
        {
            EnqueueLogout();
        }
        EnqueueLogin(currentLoginWorld, charaName, charaWorld, account);
    }

    public static void EnqueueLogout()
    {
        P.TaskManager.Enqueue(Logout);
        P.TaskManager.Enqueue(SelectYesLogout, new(timeLimitMS: 100000));
    }

    public static void EnqueueLogin(string currentLoginWorld, string charaName, string homeWorld, int account)
    {
        ConnectToDc(currentLoginWorld, account);
        P.TaskManager.Enqueue(() => SelectCharacter(charaName, homeWorld, currentLoginWorld), $"Select chara {charaName}@{homeWorld}", new(timeLimitMS: 1000000));
        P.TaskManager.Enqueue(ConfirmLogin);
    }

    public static void ConnectToDc(string currentWorld, int account)
    {
        var dc = (int)ExcelWorldHelper.Get(currentWorld)?.DataCenter.RowId;
        if((int)Svc.Data.Language < 4)
        {
            P.TaskManager.Enqueue(ClickSelectDataCenter, new(timeLimitMS: 1000000));
            P.TaskManager.Enqueue(() => SelectDataCenter(dc), $"Connect to DC {dc}");
            P.TaskManager.Enqueue(() => SelectServiceAccount(account), $"SelectServiceAccount {account}");
        }
        else
        {
            P.TaskManager.Enqueue(ClickStart);
        }
    }

    public static bool? SelectYesLogout()
    {
        if(!Svc.ClientState.IsLoggedIn) return true;
        var addon = Utils.GetLogOutYesno();
        if(addon == null || !IsAddonReady(addon)) return false;
        // 按 Yes 後不終結任務、只靠節流擋重按;關閉中的 SelectYesno 三關 ready 仍全過,再按就是 AVE ⇒ 同位址只按一次。
        if(Utils.GenericThrottle && EzThrottler.Throttle("ConfirmLogout") && AddonPressGuard.TryPressOnce("SelectYesno", addon, nameof(SelectYesLogout)))
        {
            new AddonMaster.SelectYesno((nint)addon).Yes();
            return false;
        }
        return false;
    }

    public static bool? Logout()
    {
        var addon = Utils.GetLogOutYesno();
        if(addon != null) return true;
        var isLoggedIn = Svc.Condition.Any();
        if(!isLoggedIn) return true;

        if(Player.Interactable && !Player.IsAnimationLocked && Utils.GenericThrottle && EzThrottler.Throttle("InitiateLogout"))
        {
            Chat.ExecuteCommand("/logout");
            return false;
        }
        return false;
    }

    public static bool? SelectServiceAccount(int account)
    {
        if(TryGetAddonByName<AtkUnitBase>("_CharaSelectWorldServer", out _))
        {
            return true;
        }
        if(TryGetAddonMaster<AddonMaster.SelectString>(out var m) && m.IsAddonReady)
        {
            var text = m.Text;
            // 讀到 U+FFFD ＝ 窗記憶體正在變動(多半是關閉中),這一幀不碰(對照孿生站 DCChange.SelectServiceAccount)。
            if(AddonPressGuard.IsTextUnstable("SelectString", text)) return false;
            var compareTo = Svc.Data.GetExcelSheet<Lobby>()?.GetRow(11).Text.GetText();
            if(text == compareTo && AddonPressGuard.TryPressOnce("SelectString", m.Base, nameof(SelectServiceAccount), paramKey: account.ToString()))
            {
                m.Entries[account].Select();
                return true;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    public static bool? ClickSelectDataCenter()
    {
        if(TryGetAddonByName<AtkUnitBase>("TitleDCWorldMap", out var addon) && addon->IsVisible)
        {
            PluginLog.Information($"Visible");
            Utils.RethrottleGeneric();
            return true;
        }
        if(TryGetAddonMaster<AddonMaster._TitleMenu>(out var m) && m.IsReady)
        {
            if(Utils.GenericThrottle && EzThrottler.Throttle("ClickTitleMenuStart") && AddonPressGuard.TryPressOnce("_TitleMenu", m.Base, nameof(ClickSelectDataCenter)))
            {
                m.DataCenter();
                return false;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    public static bool? ClickStart()
    {
        if(TryGetAddonByName<AtkUnitBase>("_CharaSelectListMenu", out var addon) && addon->IsVisible)
        {
            PluginLog.Information($"Visible");
            Utils.RethrottleGeneric();
            return true;
        }
        if(TryGetAddonMaster<AddonMaster._TitleMenu>(out var m) && m.IsReady)
        {
            // 按 Start 後 _TitleMenu 關閉、角色清單要等連線才出現;關閉中的 _TitleMenu 仍過 IsReady ⇒ 同位址只按一次。
            if(Utils.GenericThrottle && EzThrottler.Throttle("ClickTitleMenuStart") && AddonPressGuard.TryPressOnce("_TitleMenu", m.Base, nameof(ClickStart)))
            {
                m.Start();
                return false;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    public static bool? SelectDataCenter(int dc)
    {
        if(TryGetAddonMaster<AddonMaster.TitleDCWorldMap>(out var m) && m.IsAddonReady)
        {
            if(Utils.GenericThrottle && EzThrottler.Throttle("ClickDCSelect") && AddonPressGuard.TryPressOnce("TitleDCWorldMap", m.Base, nameof(SelectDataCenter)))
            {
                m.Select(dc);
                return true;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    public static bool? SelectCharacter(string name, string homeWorld, string currentLoginWorld, bool callContextMenu = false, bool onlyChangeWorld = false)
    {
        currentLoginWorld ??= homeWorld;
        if(TryGetAddonByName<AtkUnitBase>("SelectYesno", out _))
        {
            Utils.RethrottleGeneric();
            return true;
        }
        if(TryGetAddonByName<AtkUnitBase>("SelectOk", out _))
        {
            Utils.RethrottleGeneric();
            return true;
        }
        if(callContextMenu && TryGetAddonByName<AtkUnitBase>("ContextMenu", out var cmenu) && IsAddonReady(cmenu))
        {
            Utils.RethrottleGeneric();
            return true;
        }
        if(TryGetAddonMaster<AddonMaster._CharaSelectListMenu>(out var m) && m.IsAddonReady && TryGetAddonMaster<AddonMaster._CharaSelectWorldServer>(out var mw))
        {
            if(m.TemporarilyLocked) return false;
            if(mw.Worlds.Length == 0) return false;
            foreach(var c in m.Characters)
            {
                if(c.Name == name && ExcelWorldHelper.GetName(c.HomeWorld) == homeWorld)
                {
                    if(Utils.GenericThrottle && EzThrottler.Throttle("SelectChara"))
                    {
                        if(onlyChangeWorld)
                        {
                            return true;
                        }
                        else
                        {
                            // 角色清單窗按了不關(等確認框/右鍵選單出現),重按是設計上的重試 ⇒ 粒度含角色索引與按法,
                            // 走多次互動窗的 15 幀逃生口:只擋同位址同角色同按法在 15 幀內的重送(關閉中的危險窗口 <10 幀)。
                            if(!callContextMenu)
                            {
                                if(AddonPressGuard.TryPressOnce("_CharaSelectListMenu", m.Base, "SelectCharacter.Login", paramKey: $"login|{c.Index}", escapeIsRoutine: true))
                                    c.Login();
                            }
                            else
                            {
                                if(AddonPressGuard.TryPressOnce("_CharaSelectListMenu", m.Base, "SelectCharacter.OpenContextMenu", paramKey: $"ctx|{c.Index}", escapeIsRoutine: true))
                                    c.OpenContextMenu();
                            }
                        }
                    }
                    return false;
                }
            }
            foreach(var w in mw.Worlds)
            {
                if(w.Name == currentLoginWorld)
                {
                    if(Utils.GenericThrottle && EzThrottler.Throttle("SelectWorld") && AddonPressGuard.TryPressOnce("_CharaSelectWorldServer", mw.Base, "SelectCharacter.SelectWorld", paramKey: w.Index.ToString(), escapeIsRoutine: true))
                    {
                        w.Select();
                    }
                    return false;
                }
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    public static bool? ConfirmLogin()
    {
        if(TryGetAddonByName<AtkUnitBase>("SelectOk", out _))
        {
            return true;
        }
        if(Svc.ClientState.IsLoggedIn)
        {
            return true;
        }
        if(TryGetAddonMaster<AddonMaster.SelectYesno>(out var m) && m.IsAddonReady)
        {
            var text = m.Text;
            // 讀到 U+FFFD ＝ 窗記憶體變動中(多半是關閉中),這一幀不碰。
            if(AddonPressGuard.IsTextUnstable("SelectYesno", text)) return false;
            if(text.ContainsAny(StringComparison.OrdinalIgnoreCase, Lang.LogInPartialText))
            {
                // 按 Yes 後窗關、登入開始,任務不終結;關閉中的 SelectYesno 仍過 IsAddonReady、文字仍比得中 ⇒ 同位址只按一次。
                if(Utils.GenericThrottle && EzThrottler.Throttle("ConfirmLogin") && AddonPressGuard.TryPressOnce("SelectYesno", m.Base, nameof(ConfirmLogin)))
                {
                    m.Yes();
                    return false;
                }
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    public static bool? CloseCharaSelect()
    {
        var lobby = AgentLobby.Instance();
        if(Utils.CanAutoLogin()) return true;
        // AgentLobby 取得器合法回 null。下面那行會讀 lobby->AgentInterface / TemporaryLocked,
        // 拿不到就回 false 讓工作重試,不要裸解參考。
        if(lobby == null) return false;
        if(!TryGetAddonByName<AtkUnitBase>("SelectOk", out _) && TryGetAddonByName<AtkUnitBase>("_CharaSelectReturn", out var addon) && IsAddonReady(addon) && (!lobby->AgentInterface.IsAgentActive() || !lobby->TemporaryLocked))
        {
            if(Utils.GenericThrottle)
            {
                // 🔴 GetComponentButtonById 找不到時合法回 null,對 null 呼叫 ClickAddonButton 是攔不到的 AVE(對照 DCChange.CancelDcVisit 的判空)。
                //    按下「返回標題」後窗會關而任務不終結、只有 10 幀節流 ⇒ 同位址只按一次,窗消失前不再送 ReceiveEvent。
                var button = addon->GetComponentButtonById(4);
                if(button != null && AddonPressGuard.TryPressOnce("_CharaSelectReturn", addon, nameof(CloseCharaSelect)))
                {
                    button->ClickAddonButton(addon);
                }
            }
        }
        return false;
    }
}
