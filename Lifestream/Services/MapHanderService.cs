using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Memory;
using ECommons.MathHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lifestream.Enums;
using Lifestream.Systems.Residential;
using Lifestream.Tasks.SameWorld;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using FXWindows = TerraFX.Interop.Windows.Windows;

namespace Lifestream.Services;
public unsafe class MapHanderService : IDisposable
{
    private MapHanderService()
    {
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "AreaMap", OnMapReceivedEvent);
    }

    public void Dispose()
    {
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostReceiveEvent, "AreaMap", OnMapReceivedEvent);
    }

    private void OnMapReceivedEvent(AddonEvent type, AddonArgs args)
    {
        if(args is AddonReceiveEventArgs evt && TryGetAddonByName<AddonAreaMap>("AreaMap", out var addon) && addon->AtkUnitBase.IsReady() && !Utils.IsBusy())
        {
            /*var atkEvent = (AtkEvent*)evt.AtkEvent;
            var data = MemoryHelper.ReadRaw(evt.Data, 40);
            PluginLog.Information($"""
                EventParam: {evt.EventParam}
                AtkEventType: {evt.AtkEventType}
                atkEvent->Param: {atkEvent->Param}
                atkEvent->Node->NodeId: {(atkEvent->Node == null?"-":atkEvent->Node->NodeId)}
                atkEvent->State: {atkEvent->State.StateFlags}
                data: {data.ToHexString()}
                CursorTarget: {(addon->CursorTarget == null?"-": addon->CursorTarget->NodeId)}
                """);*/
            var isLeftClicked = *(byte*)(evt.Data + 6) == 0;
            if(evt.AtkEventType == (int)AtkEventType.MouseUp)
            {
                if(isLeftClicked)
                {
                    if(!Bitmask.IsBitSet(FXWindows.GetKeyState((int)Keys.ControlKey), 15) && !Bitmask.IsBitSet(FXWindows.GetKeyState((int)Keys.LControlKey), 15) && !Bitmask.IsBitSet(FXWindows.GetKeyState((int)Keys.RControlKey), 15))
                    {
                        if(TryGetAddonByName<AtkUnitBase>("Tooltip", out var addonTooltip) && IsAddonReady(addonTooltip) && addonTooltip->IsVisible)
                        {
                            // 🔴 NodeList[2] 既沒驗 NodeListCount 上界也沒判元素,而 &node->NodeText
                            // 對 null 節點不會當場崩:NodeText 在 AtkTextNode 偏移 0xC0,算出的毒指標
                            // 0xC0 連 ReadSeString 內部的判空都騙得過去,直到真的去讀才炸。
                            //
                            // 🔴🔴 這裡**絕對不能**用「取不到就當空字串」帶過:下面自訂乙太網那段是
                            // x.Name.StartsWith(text),空字串會對**每一個**地點成立 —— 讀取失敗會變成
                            // 隨便挑一個地點傳送過去。讀不到就什麼都不做。
                            if(!TryGetNodeText(addonTooltip, 2, out var text))
                            {
                                if(EzThrottler.Throttle("MapTooltipUnreadableLog", 5000))
                                {
                                    PluginLog.Information("[Map] Tooltip text node is unavailable; ignoring this map click.");
                                }
                                return;
                            }

                            // 🔴 設定「點擊地圖上的乙太之光標記以快速傳送」原本是死的:全 repo 只有
                            // Config 的宣告與設定頁那個核取方塊,這裡從來沒讀過它 —— 使用者取消勾選
                            // 完全沒有效果,而且是靜默的。這裡把它接上。
                            // (同一節的 DisableMapClickOtherTerritory 一直是活的,見下面;
                            //  它是這個總開關底下的細部篩選。)
                            //
                            // ⚠️ 接上之後,設定檔裡已經是 false 的使用者會發現地圖點擊「突然壞了」——
                            // 其實是他當初關掉的設定終於生效。所以這裡寫 Information 級診斷
                            // (使用者跑 LogLevel 1),讓 log 直接回答「為什麼沒反應」,不要再靜默一次。
                            // 判斷點放在這裡而不是事件入口,是為了只在「真的點到了某個標記」時才記錄。
                            if(!C.UseMapTeleport)
                            {
                                if(EzThrottler.Throttle("MapTeleportDisabledLog", 5000))
                                {
                                    PluginLog.Information($"[Map] Ignoring click on \"{text}\": \"Click Aethernet Shard on map for quick teleport\" is turned off in Lifestream settings (Map Integration).");
                                }
                                return;
                            }

                            if(P.ActiveAetheryte != null)
                            {
                                var master = Utils.GetMaster();
                                foreach(var x in S.Data.DataStore.Aetherytes[master])
                                {
                                    if(x.Name == text)
                                    {
                                        if(P.ActiveAetheryte.Value.ID == x.ID)
                                        {
                                            Notify.Error("You are already here!");
                                        }
                                        else
                                        {
                                            TaskAethernetTeleport.Enqueue(x);
                                        }
                                        return;
                                    }
                                }
                            }
                            if(S.Data.ResidentialAethernet.ActiveAetheryte != null)
                            {
                                var zone = S.Data.ResidentialAethernet.ZoneInfo.SafeSelect(P.Territory);
                                if(zone != null)
                                {
                                    foreach(var x in zone.Aetherytes)
                                    {
                                        if(x.Name == text)
                                        {
                                            if(S.Data.ResidentialAethernet.ActiveAetheryte.Value.ID == x.ID)
                                            {
                                                Notify.Error("You are already here!");
                                            }
                                            else
                                            {
                                                TaskAethernetTeleport.Enqueue(x.Name);
                                            }
                                            return;
                                        }
                                    }
                                }
                            }
                            if(S.Data.CustomAethernet.ActiveAetheryte != null)
                            {
                                var zone = S.Data.CustomAethernet.ZoneInfo.SafeSelect(P.Territory);
                                if(zone != null)
                                {
                                    foreach(var x in zone.Aetherytes)
                                    {
                                        if(x.Name.StartsWith(text))
                                        {
                                            if(S.Data.CustomAethernet.ActiveAetheryte.Value.ID == x.ID)
                                            {
                                                Notify.Error("You are already here!");
                                            }
                                            else
                                            {
                                                TaskAethernetTeleport.Enqueue(x.Name);
                                            }
                                            return;
                                        }
                                    }
                                    if(zone.GenericAetheryteNames.Contains(text))
                                    {
                                        var target = zone.Aetherytes.MinBy(x => Vector2.Distance(x.MapPosition.Value, addon->HoveredCoords));
                                        TaskAethernetTeleport.Enqueue(target.Name);
                                    }
                                }
                            }
                            if(!C.DisableMapClickOtherTerritory)
                            {
                                foreach(var x in S.Data.DataStore.Aetherytes)
                                {
                                    foreach(var a in x.Value)
                                    {
                                        if(a.Name == text)
                                        {
                                            TaskAetheryteAethernetTeleport.Enqueue(x.Key.ID, a.ID);
                                            return;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
