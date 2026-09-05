using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation;
using ECommons.GameFunctions;
using Callback = ECommons.Automation.Callback;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lifestream.AtkReaders;
using Lifestream.Systems.Legacy;

namespace Lifestream.Schedulers;

internal static unsafe class WorldChange
{
    internal static bool? TargetValidAetheryte()
    {
        if(!Player.Available) return false;
        if(IsOccupied()) return false;
        var a = Utils.GetValidAetheryte();
        if(a != null)
        {
            if(a.Address != Svc.Targets.Target?.Address)
            {
                if(EzThrottler.Throttle("TargetValidAetheryte", 500))
                {
                    Svc.Targets.SetTarget(a);
                    return true;
                }
            }
            else
            {
                return true;
            }
        }
        return false;
    }

    internal static bool? InteractWithTargetedAetheryte()
    {
        if(!Player.Available) return false;
        if(Player.IsAnimationLocked) return false;
        if(!Utils.DismountIfNeeded()) return false;
        if(IsOccupied()) return false;
        var a = Utils.GetValidAetheryte();
        if(a != null && Svc.Targets.Target?.Address == a.Address)
        {
            if(EzThrottler.Throttle("InteractWithTargetedAetheryte", 500))
            {
                TargetSystem.Instance()->InteractWithObject(a.Struct(), false);
                return true;
            }
        }
        return false;
    }

    internal static bool? SelectAethernet()
    {
        if(!Player.Available) return false;
        return Utils.TrySelectSpecificEntry(Lang.Aethernet, () => EzThrottler.Throttle("SelectString"));
    }

    /// <summary>
    /// 「需要的話才選以太之光網路」。
    ///
    /// 主水晶(Aetheryte 表的 IsAetheryte=true,也就是 DataStore.Aetherytes 字典的「鍵」)互動後會先跳一層
    /// SelectString(以太之光網路／跨界傳送／切換副本區…),要先選「以太之光網路」才會開目的地清單;
    /// 但**城內以太之光(子節點,字典的「值」)互動後是直接開 TelepotTown 目的地清單,根本沒有這一層選單**。
    /// 對子節點排 <see cref="SelectAethernet"/> 會永遠找不到那一項而空轉到逾時,而且過程中一行訊息都沒有。
    /// (上游的 <c>TaskAethernetTeleport.Enqueue(TinyAetheryte)</c> 也是用「ActiveAetheryte 是不是
    /// 字典的鍵」來決定要不要排這一步 —— 但它在「排入佇列的當下」就求值,對「要先走過去才會站到節點旁」的
    /// 流程用不了,那時 ActiveAetheryte 還是 null。)
    ///
    /// 所以這裡不預先判斷節點種類,而是看**實際開出來的是哪個視窗**,主水晶跟子節點都適用:
    /// <list type="bullet">
    /// <item>TelepotTown(目的地清單)已開 → 這一層選單不存在,直接放行。</item>
    /// <item>SelectString 開著且有「以太之光網路」那一項 → 照舊選它(行為與 <see cref="SelectAethernet"/> 相同)。</item>
    /// <item>SelectString 開著但沒有那一項 → 把看到的選項全部印進 log 後放行,交給下一步的目的地選擇處理
    ///   (特例區域會直接把目的地列在 SelectString 裡,見 <see cref="TeleportToAethernetDestination(string)"/>)。</item>
    /// <item>兩個都還沒開 → 回 false 繼續等(互動到開窗有幾百毫秒延遲),並定期印出「在等什麼」。</item>
    /// </list>
    /// 每一種狀態都會定期寫 Information 等級的診斷(使用者的記錄等級只會濾掉 Verbose、Debug 收得到但單檔數十萬行會淹沒),所以就算真的卡住,
    /// log 也看得出來是卡在哪一步、當下的選單長什麼樣,不會像修正前那樣靜默逾時。
    /// </summary>
    internal static bool? SelectAethernetIfNeeded()
    {
        if(!Player.Available) return false;

        if(TryGetAddonByName<AtkUnitBase>("TelepotTown", out var telep) && IsAddonReady(telep))
        {
            if(EzThrottler.Throttle("AethernetMenuSkipLog", 5000))
            {
                PluginLog.Information($"[Aethernet] Destination list is already open - the node we interacted with has no aethernet submenu, skipping menu selection. ({DescribeActiveAetheryte()})");
            }
            return true;
        }

        if(TryGetAddonByName<AddonSelectString>("SelectString", out var addon) && IsAddonReady(&addon->AtkUnitBase))
        {
            var entries = Utils.GetEntries(addon);
            if(entries.Any(x => x.EqualsAny(Lang.Aethernet))) return SelectAethernet();
            // 選單已開但選項還沒填進去(開窗的那一兩幀)——當作還沒開,繼續等,不要誤判成「沒有這一項」。
            if(entries.Count == 0) return false;
            if(EzThrottler.Throttle("AethernetMenuMismatchLog", 5000))
            {
                PluginLog.Information($"[Aethernet] SelectString is open but none of its entries is the aethernet option. Looked for [{Lang.Aethernet.Print(" | ")}], menu shows [{entries.Print(" | ")}]. Passing through to destination selection. ({DescribeActiveAetheryte()})");
            }
            return true;
        }

        if(EzThrottler.Throttle("AethernetMenuWaitLog", 5000))
        {
            PluginLog.Information($"[Aethernet] Waiting for the aetheryte window: neither SelectString nor TelepotTown is open yet. ({DescribeActiveAetheryte()})");
        }
        return false;
    }

    private static string DescribeActiveAetheryte()
    {
        var a = P.ActiveAetheryte;
        if(a == null) return "ActiveAetheryte=null";
        return $"ActiveAetheryte={a.Value.Name}({a.Value.ID}), isMasterAetheryte={a.Value.IsAetheryte}";
    }

    internal static bool? SelectVisitAnotherWorld()
    {
        if(!Player.Available) return false;
        return Utils.TrySelectSpecificEntry(Lang.VisitAnotherWorld, () => EzThrottler.Throttle("SelectString"));
    }

    internal static bool? ConfirmWorldVisit(string s)
    {
        if(!Player.Available) return false;
        var x = (AddonSelectYesno*)Utils.GetSpecificYesno(true, Lang.ConfirmWorldVisit);
        if(x != null)
        {
            if(IsButtonEnabled(x->YesButton) && EzThrottler.Throttle("ConfirmWorldVisit") && AddonPressGuard.TryPressOnce("SelectYesno", x, nameof(ConfirmWorldVisit)))
            {
                new AddonMaster.SelectYesno(x).Yes();
                return true;
            }
        }
        return false;
    }

    internal static bool? SelectWorldToVisit(string world)
    {
        if(!Player.Available) return false;
        var worlds = Utils.GetAvailableWorldDestinations();
        // 讀到 U+FFFD ＝ 窗記憶體變動中,這一幀不碰。
        if(AddonPressGuard.AnyTextUnstable("WorldTravelSelect", worlds)) return false;
        var index = Array.IndexOf(worlds, world);
        if(index != -1)
        {
            if(TryGetAddonByName<AtkUnitBase>("WorldTravelSelect", out var addon) && IsAddonReady(addon))
            {
                if(EzThrottler.Throttle("SelectWorldToVisit", 1000) && AddonPressGuard.TryPressOnce("WorldTravelSelect", addon, nameof(SelectWorldToVisit)))
                {
                    Callback.Fire(addon, true, index + 2);
                    return true;
                }
            }
        }
        return false;
    }

    /*internal static bool? TeleportToAethernetDestination(TinyAetheryte t)
    {
        if (!Player.Available) return false;
        if (TryGetAddonByName<AtkUnitBase>("TelepotTown", out var telep) && IsAddonReady(telep))
        {
            if (S.Data.DataStore.StaticData.Callback.TryGetValue(t.ID, out var callback))
            {
                if (Utils.GetAvailableAethernetDestinations().Any(x => x.Equals(t.Name)))
                {
                    if (EzThrottler.Throttle("TeleportToAethernetDestination", 2000))
                    {
                        P.TaskManager.InsertMulti(
                            new(() => Callback.Fire(telep, true, 11, callback)),
                            new(() => Callback.Fire(telep, true, 11, callback))
                            );
                        return true;
                    }
                }
                else
                {
                    PluginLog.Debug($"Could not find destination {t.Name}, attempting partial search...");
                    foreach (var destText in Utils.GetAvailableAethernetDestinations())
                    {
                        if (destText.Length > 20)
                        {
                            var text = destText[..^3];
                            if (t.Name.StartsWith(text))
                            {
                                if (EzThrottler.Throttle("TeleportToAethernetDestination", 2000))
                                {
                                    PluginLog.Debug($"Destination {t.Name} starts with {text}, assuming successful search");
                                    P.TaskManager.InsertMulti(
                                        new(() => Callback.Fire(telep, true, 11, callback)),
                                        new(() => Callback.Fire(telep, true, 11, callback))
                                        );
                                    return true;
                                }
                            }
                        }
                    }
                    if (EzThrottler.Throttle("TeleportToAethernetDestinationLog", 5000))
                    {
                        PluginLog.Warning($"GetAvailableAethernetDestinations does not contains {t.Name}, contains {Utils.GetAvailableAethernetDestinations().Print()}");
                    }
                }
            }
            else
            {
                DuoLog.Error($"Callback data absent for {t.Name}");
                return null;
            }
        }
        return false;
    }*/

    internal static bool? TeleportToAethernetDestination(string name)
    {
        if(!Player.Available) return false;
        if(TryGetAddonByName<AtkUnitBase>("TelepotTown", out var telep) && IsAddonReady(telep))
        {
            var reader = new ReaderTelepotTown(telep);
            for(var i = 0; i < reader.DestinationName.Count; i++)
            {
                var destName = reader.DestinationName[i].Name;
                // 讀到 U+FFFD ＝ 窗記憶體變動中,這一幀不碰。
                if(AddonPressGuard.IsTextUnstable("TelepotTown", destName)) return false;
                if(destName == name)
                {
                    var data = reader.DestinationData.SafeSelect(i);
                    if(data != null)
                    {
                        if(EzThrottler.Throttle("TeleportToAethernetDestination", 2000))
                        {
                            var callback = data.CallbackData;
                            // 上游自 2024-04 起就是刻意對 TelepotTown 連送兩次同參數的 Fire(11, callback)(第一次在下一 tick、
                            // 第二次再下一 tick),repo 內沒有註解說明第一次是「選取」還是「直接啟動傳送」。
                            // 兩種語意對「第二次該不該送」的答案相反,所以不猜,兩種都安全:
                            //  ・每次都重新從 addon 清單取 TelepotTown,位址不同或已不在(＝第一次已啟動傳送、窗走完了)就回 true 不送
                            //    —— 原本是把 tick N 取到的指標捕獲進 lambda、N+2 直接解參,那正是「對關閉中的窗送 callback」的形狀;
                            //  ・窗還在就經 AddonPressGuard(粒度含參數組、多次互動窗的 15 幀逃生口):第一次照常送,
                            //    第二次要等同位址同參數過了 15 幀(關閉中的危險窗口 <10 幀)才送,是「選取＋確認」語意時只多 0.25 秒。
                            var telepAddress = (nint)telep;
                            P.TaskManager.InsertMulti(
                                new(() => FireTelepotTownOnce(telepAddress, callback), "TelepotTownFire#1"),
                                new(() => FireTelepotTownOnce(telepAddress, callback), "TelepotTownFire#2")
                                );
                            return true;
                        }
                    }
                }
            }
        }
        else if(S.Data.CustomAethernet.QuasiAethernetZones.Contains(P.Territory) && TryGetAddonMaster<AddonMaster.SelectString>(out var m) && m.IsAddonReady)
        {
            if(AddonPressGuard.AnyTextUnstable("SelectString", m.Entries.Select(e => e.Text))) return false;
            if(Utils.TryFindEqualsOrContains(m.Entries, e => e.Text, name, out var entry))
            {
                if(EzThrottler.Throttle("TeleportToAethernetDestination", 2000) && AddonPressGuard.TryPressOnce("SelectString", m.Base, "TeleportToAethernetDestination.Quasi", paramKey: entry.Index.ToString()))
                {
                    entry.Select();
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// <see cref="TeleportToAethernetDestination(string)"/> 排入的兩個 Fire 任務共用的本體:每 tick 重新取窗、經守衛、送一次。
    /// 回 <see langword="true"/> ＝ 這一步結束(送出了,或那扇窗已經不在/不再就緒、沒東西可送);
    /// 回 <see langword="false"/> ＝ 這一幀被守衛擋下,下一 tick 再來。
    /// 🔴 <paramref name="expected"/> 只做位址等值比較,永遠不解參。
    /// </summary>
    private static bool FireTelepotTownOnce(nint expected, uint callback)
    {
        if(!TryGetAddonByName<AtkUnitBase>("TelepotTown", out var telep) || (nint)telep != expected || !IsAddonReady(telep))
        {
            PluginLog.Debug($"[Aethernet] TelepotTown 0x{expected:X} 已不在 addon 清單或不再就緒(傳送多半已啟動),略過這一發 Fire(11, {callback})");
            return true;
        }
        if(!AddonPressGuard.TryPressOnce("TelepotTown", telep, nameof(TeleportToAethernetDestination), paramKey: $"11|{callback}", escapeIsRoutine: true)) return false;
        Callback.Fire(telep, true, 11, callback);
        return true;
    }

    internal static bool? ExecuteTPToAethernetDestination(uint destination, uint subIndex = 0)
    {
        if(!Player.Available) return false;
        // AgentMap 取得器合法回 null。拿不到就回 false 讓工作重試,不要對 null 讀 IsPlayerMoving。
        var map = AgentMap.Instance();
        if(map == null) return false;
        if(map->IsPlayerMoving == false && !IsOccupied() && !Player.Object.IsCasting && EzThrottler.Throttle("ExecTP", 1000))
        {
            return S.TeleportService.TeleportToAetheryte(destination, subIndex);
            //return Svc.PluginInterface.GetIpcSubscriber<uint, byte, bool>("Teleport").InvokeFunc(destination, (byte)subIndex);
        }
        return false;
    }

    internal static bool? WaitUntilNotBusy()
    {
        if(!Player.Available) return false;
        return S.Data.DataStore.Territories.Contains(P.Territory) && Player.Object.CastActionId == 0 && !IsOccupied() && !Utils.IsDisallowedToUseAethernet() && Player.Object.IsTargetable;
    }


    internal static bool? TargetReachableWorldChangeAetheryte()
    {
        return TargetReachableAetheryte(Utils.GetReachableWorldChangeAetheryte);
    }

    internal static bool? TargetReachableMasterAetheryte()
    {
        return TargetReachableAetheryte(Utils.GetReachableMasterAetheryte);
    }

    /// <summary>
    /// 跟 <see cref="TargetReachableMasterAetheryte"/> 同一套鎖定機制,但目標放寬成「同一個以太之光
    /// 網路裡摸得到的任一節點」(主水晶或城內以太之光都算),對應 <see cref="Utils.GetReachableAethernetNetworkNode"/>。
    /// </summary>
    internal static bool? TargetReachableAethernetNetworkNode(TinyAetheryte root, uint? excludeId = null)
    {
        return TargetReachableAetheryte(_ => Utils.GetReachableAethernetNetworkNode(root, excludeId));
    }

    internal static bool? TargetReachableAetheryte(Func<bool, IGameObject> aetheryteFunc)
    {
        if(!Player.Available) return false;
        var a = aetheryteFunc(false);
        if(a.IsTarget()) return true;
        if(a != null)
        {
            if(!a.IsTarget() && EzThrottler.Throttle("TargetReachableAetheryte", 200))
            {
                Svc.Targets.SetTarget(a);
                return true;
            }
        }
        return false;
    }

    internal static bool? LockOn()
    {
        if(!Player.Available) return false;
        if(Svc.Targets.Target != null && EzThrottler.Throttle("LockOn", 200))
        {
            Chat.SendMessage("/lockon");
            return true;
        }
        return false;
    }

    internal static bool? EnableAutomove()
    {
        if(!Player.Available) return false;
        if(EzThrottler.Throttle("EnableAutomove", 200))
        {
            Chat.SendMessage("/automove on");
            return true;
        }
        return false;
    }

    internal static bool? WaitUntilMasterAetheryteExists()
    {
        if(!Player.Available) return false;
        return P.ActiveAetheryte != null && P.ActiveAetheryte.Value.IsAetheryte;
    }

    internal static bool? DisableAutomove()
    {
        if(!Player.Available) return false;
        if(EzThrottler.Throttle("DisableAutomove", 200))
        {
            Chat.SendMessage("/automove off");
            return true;
        }
        return false;
    }

    internal static bool? LeaveParty()
    {
        if(!Player.Available) return false;
        if(Svc.Party.Length < 2) return true;
        if(EzThrottler.Throttle("LeaveParty", 200))
        {
            Chat.SendMessage("/leave");
            return true;
        }
        return false;
    }

    internal static bool ClosePF()
    {
        if(TryGetAddonMaster<AddonMaster.LookingForGroupDetail>(out var m))
        {
            // 「送 -1 直到窗消失」的重試迴圈:第一次 -1 後窗關閉中仍過 IsAddonReady,第 11 幀再送就落在危險窗口 ⇒ 同位址只送一次。
            if(m.IsAddonReady && Utils.GenericThrottle && AddonPressGuard.TryPressOnce("LookingForGroupDetail", m.Base, nameof(ClosePF))) Callback.Fire(m.Base, true, -1);
        }
        else
        {
            return true;
        }
        return false;
    }

    internal static bool OpenSelfPF()
    {
        if(Player.Available)
        {
            if(Utils.GenericThrottle)
            {
                // 🔴 這個指標會被原封不動交給原生的 OpenPartyFinderInfo(hook 的 Original),
                //    傳 null 進去是崩在遊戲程式碼裡、try/catch 攔不到。
                //    AgentLookingForGroup 取得器合法回 null,所以先判空;拿不到就這次不呼叫、
                //    回 false 讓工作下一幀重試。
                var lfg = AgentLookingForGroup.Instance();
                if(lfg == null) return false;
                S.Memory.OpenPartyFinderInfoDetour(lfg, Player.CID);
                return true;
            }
        }
        return false;
    }

    internal static bool EndPF()
    {
        if(TryGetAddonMaster<AddonMaster.LookingForGroupDetail>(out var m) && m.IsAddonReady)
        {
            if(Utils.GenericThrottle && AddonPressGuard.TryPressOnce("LookingForGroupDetail", m.Base, nameof(EndPF)))
            {
                m.TellEnd();
                return true;
            }
        }
        return false;
    }

    internal static bool WaitUntilNotRecruiting()
    {
        return !Svc.Condition[ConditionFlag.RecruitingWorldOnly];
    }

    internal static bool? LeaveAnyParty()
    {
        if(!Player.Available) return false;
        if(Svc.Party.Length < 2 && !Svc.Condition[ConditionFlag.ParticipatingInCrossWorldPartyOrAlliance]) return true;
        if(EzThrottler.Throttle("LeaveParty", 200))
        {
            Chat.SendMessage("/leave");
            return true;
        }
        return false;
    }

    internal static bool? ConfirmLeaveParty()
    {
        if(!Player.Available) return false;
        if(Svc.Party.Length < 2) return true;
        var x = (AddonSelectYesno*)Utils.GetSpecificYesno();
        if(x != null)
        {
            if(IsButtonEnabled(x->YesButton) && EzThrottler.Throttle("ConfirmLeaveParty") && AddonPressGuard.TryPressOnce("SelectYesno", x, nameof(ConfirmLeaveParty)))
            {
                new SelectYesnoMaster(x).Yes();
                return true;
            }
        }
        return false;
    }
}
