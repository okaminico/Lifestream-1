using ECommons.Configuration;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lifestream.Data;
using Lifestream.Systems;
using Lifestream.Systems.TeleportPanel;
using Lifestream.Tasks.Shortcuts;
using Lumina.Excel.Sheets;
using NightmareUI;
using NightmareUI.ImGuiElements;
using NightmareUI.PrimaryUI;
using System.Globalization;
using Action = System.Action;

namespace Lifestream.GUI;

internal static unsafe class UISettings
{
    private static string AddNew = "";

    /// <summary>「去不了的世界」區塊裡待加入的世界，0 = 還沒選。</summary>
    private static int AddUnavailableWorld = 0;

    /// <summary>
    /// 挑世界加進排除清單用。刻意自己開一個而不是用共用單例 <c>WorldSelector.Instance</c>：
    /// 這裡要列出<b>全部</b>世界(含已經被排除的)，而單例會被別的分頁改設定。
    /// </summary>
    private static readonly WorldSelector UnavailableWorldSelector = new("##addunavailableworld")
    {
        EmptyName = "Select a world".Loc(),
    };
    internal static void Draw()
    {
        NuiTools.ButtonTabs([[new("General".Loc(), () => Wrapper(DrawGeneral)), new("Overlay".Loc(), () => Wrapper(DrawOverlay)), new("Teleport Panel".Loc(), () => Wrapper(DrawTeleportPanel))], [new("Expert".Loc(), () => Wrapper(DrawExpert)), new("Service Accounts".Loc(), () => Wrapper(UIServiceAccount.Draw)), new("Travel Block".Loc(), TabTravelBan.Draw)]]);
    }

    private static void Wrapper(Action action)
    {
        ImGui.Dummy(new(5f));
        action();
    }

    /// <summary>
    /// 傳送面板的設定，以及兩個有帳號風險、預設關閉的進階選項。
    /// 這兩個選項的說明文字刻意寫得很直白 —— 使用者是在知情的前提下自行承擔風險，
    /// 所以說明不能含糊。
    /// </summary>
    private static void DrawTeleportPanel()
    {
        new NuiBuilder()
        .Section("Teleport Panel".Loc())
        .Widget(() =>
        {
            ImGuiEx.TextWrapped("A searchable teleport window with favorites, renames and a map preview. Open it with \"/li panel\".".Loc());
            if(ImGui.Button("Open teleport panel".Loc())) S.Gui.TeleportPanelWindow.IsOpen = true;
            ImGui.SameLine();
            if(ImGui.Button($"★ {"Open favorites window".Loc()}")) S.Gui.TeleportFavoritesWindow.IsOpen = true;
            ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, "The favorites window (\"/li fav\") is a separate list where you can put your favorites in your own order and sort them into your own categories. The teleport panel itself is unchanged.".Loc());
            ImGui.Checkbox("Show map preview".Loc(), ref C.TeleportPanelShowMap);
            ImGui.Checkbox("Hide city aethernet shards while in a party".Loc(), ref C.TeleportPanelHideAethernetInParty);
            ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, "Favorites and renames are shared with the Lifestream overlay - anything you star here also stars there.".Loc());
        })
        .Draw();

        new NuiBuilder()
        .Section("Import from DailyRoutines".Loc())
        .Widget(() =>
        {
            ImGuiEx.TextWrapped("Imports favorites, remarks and custom landing positions from DailyRoutines' BetterTeleport module. Both plugins key this data by aetheryte row id, so it transfers 1:1. Existing entries are never overwritten.".Loc());
            var exists = DailyRoutinesImport.Exists;
            if(ImGuiEx.Button("Import BetterTeleport.json".Loc(), exists))
            {
                DailyRoutinesImport.Import();
            }
            if(!exists)
            {
                ImGuiEx.Text(ImGuiColors.DalamudGrey, $"{"File not found:".Loc()} {DailyRoutinesImport.DefaultPath}");
            }
            if(DailyRoutinesImport.LastResult != null)
            {
                ImGuiEx.TextWrapped(ImGuiColors.DalamudYellow, DailyRoutinesImport.LastResult);
            }
        })
        .Draw();

        new NuiBuilder()
        .Section("Custom landing positions".Loc())
        .Widget(() =>
        {
            ImGuiEx.TextWrapped("You can save a custom landing spot for each aetheryte. After teleporting there, Lifestream will bring you to that spot instead of leaving you at the crystal. Set them from the right-click menu in the teleport panel.".Loc());
            ImGui.Checkbox("Enable custom landing positions".Loc(), ref C.EnableAetheryteLanding);
            ImGuiEx.HelpMarker("Off by default. When enabled, the safe route is used: normal teleport, then vnavmesh walks you to the spot. Nothing is written to game memory.".Loc());

            if(C.EnableAetheryteLanding)
            {
                ImGui.Indent();
                ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, "Safe mode: teleport + vnavmesh pathfinding. If vnavmesh is not installed the spot is flagged on your map instead.".Loc());
                ImGui.Dummy(new(5f));

                // 落點離某座城內乙太之光比離剛落地的主水晶近很多時，先搭一段都市傳送網再走完最後一段。
                // 0 = 關，等於修改前的行為(從主水晶一路走過去)。
                ImGui.SetNextItemWidth(200f.Scale());
                ImGui.SliderFloat("Take the aethernet first when it saves at least, yalms".Loc(), ref C.AetheryteLandingRelayGain, 0f, 200f, "%.0f");
                ImGuiEx.HelpMarker("When your landing spot is much closer to one of the city's aethernet shards than to the aetheryte you teleported to, Lifestream rides the aethernet there first and only walks the last stretch - that is the difference between crossing Solution Nine on foot and a short walk. 0 turns this off and always walks the whole way. Straight-line distance, not path length. Needs vnavmesh; without it the spot is just flagged on the map as before.".Loc());
                ImGui.Dummy(new(5f));

                ImGui.Checkbox("Use direct position write instead of walking".Loc(), ref C.AetheryteLandingDirectWrite);
                ImGui.Indent();
                ImGuiEx.TextWrapped(EColor.RedBright, LocText.MemoryTeleportWarning.Loc());
                ImGuiEx.TextWrapped(EColor.RedBright, "DailyRoutines, where this feature comes from, keeps its own list of zones with server-side movement-speed detection, and on the CN/TC clients it refuses to position-teleport inside those zones unless the account is premium - that is their own evidence that it is detectable.".Loc());
                ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, LocText.MemoryTeleportRefusalNote.Loc());
                ImGui.Unindent();
                ImGui.Unindent();
            }

            if(C.AetheryteLandings.Count > 0)
            {
                ImGui.Dummy(new(5f));
                ImGuiEx.Text($"{"Saved landing positions".Loc()}: {C.AetheryteLandings.Count}");
                uint toRemove = 0;
                foreach(var (id, pos) in C.AetheryteLandings)
                {
                    var row = Svc.Data.GetExcelSheet<Aetheryte>().GetRowOrDefault(id);
                    var name = C.Renames.TryGetValue(id, out var rn) && rn != "" ? rn
                        : row?.PlaceName.ValueNullable?.Name.ToString() is { Length: > 0 } pn ? pn
                        : row?.AethernetName.ValueNullable?.Name.ToString() is { Length: > 0 } an ? an
                        : $"#{id}";
                    ImGuiEx.Text($"{name}  ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})");
                    ImGui.SameLine();
                    if(ImGui.SmallButton($"{"Delete".Loc()}##landing{id}")) toRemove = id;
                }
                if(toRemove > 0)
                {
                    C.AetheryteLandings.Remove(toRemove);
                    EzConfig.Save();
                }
            }
        })
        .Draw();

        new NuiBuilder()
        .Section("Teleport to the aethernet shard you are standing on".Loc())
        .Widget(() =>
        {
            ImGuiEx.TextWrapped("The game refuses an aethernet teleport when the destination is the shard you are already standing at (\"This is your current location.\"). This option patches that check out, which is useful together with custom landing positions - you can re-teleport to snap back to your saved spot.".Loc());
            ImGuiEx.TextWrapped(EColor.RedBright, "WARNING - use at your own risk. This is a memory patch: it rewrites two bytes of the game's code so it stops refusing, and the client then sends a teleport request the unmodified client would never send. Off by default.".Loc());
            ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, "The \"you have not attuned to this aetheryte\" check is left untouched. The patch is verified before it is written: each signature must match exactly once and the byte found must be the expected conditional jump, otherwise nothing is patched at all. The original bytes are restored when you turn this off or unload the plugin.".Loc());

            var enabled = C.SameAethernetTeleport;
            if(ImGui.Checkbox("Allow teleporting to the aethernet shard you are standing on".Loc(), ref enabled))
            {
                C.SameAethernetTeleport = enabled;
                EzConfig.Save();
                if(enabled)
                {
                    if(!SameAethernetTeleportPatch.Enable())
                    {
                        // 解析或寫入失敗：把設定退回關閉，免得下次啟動又白試一次。
                        C.SameAethernetTeleport = false;
                        EzConfig.Save();
                    }
                }
                else
                {
                    SameAethernetTeleportPatch.Disable();
                }
            }

            if(SameAethernetTeleportPatch.IsApplied)
            {
                ImGuiEx.Text(ImGuiColors.HealerGreen, "Patch active.".Loc());
            }
            else if(SameAethernetTeleportPatch.ResolveError != null)
            {
                ImGuiEx.TextWrapped(EColor.RedBright, $"{"Patch not applied:".Loc()} {SameAethernetTeleportPatch.ResolveError}");
                ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, "This usually means the game updated and the signature moved. Nothing was written - the feature is simply off.".Loc());
            }
        })
        .Draw();
    }

    private static void DrawGeneral()
    {
        new NuiBuilder()
        .Section("Teleport Configuration".Loc())
        .Widget(() =>
        {
            ImGui.SetNextItemWidth(200f.Scale());
            ImGuiEx.EnumCombo("Teleport world change gateway".Loc(), ref C.WorldChangeAetheryte, Lang.WorldChangeAetherytes);
            ImGuiEx.HelpMarker("Where would you like to teleport for world changes".Loc());
            ImGui.Checkbox("Teleport to specific aethernet destination after world/dc visit".Loc(), ref C.WorldVisitTPToAethernet);
            if(C.WorldVisitTPToAethernet)
            {
                ImGui.Indent();
                ImGui.SetNextItemWidth(250f.Scale());
                ImGui.InputText("Aethernet destination, as if you'd use in \"/li\" command".Loc(), ref C.WorldVisitTPTarget, 50);
                ImGui.Checkbox("Only teleport from command but not from overlay".Loc(), ref C.WorldVisitTPOnlyCmd);
                ImGui.Unindent();
            }
            // 目的地就在同一區又很近時,乾脆用走的 —— 省下整整一次讀取畫面。
            // 🔴 預設 0(關):這會改變語意(點了傳送面板卻用走的),所以必須由使用者自己開,而且門檻可調。
            ImGui.SetNextItemWidth(200f.Scale());
            ImGui.SliderFloat("Walk instead of using the aethernet when closer than, yalms".Loc(), ref C.SkipAethernetIfCloserThan, 0f, 150f, "%.0f");
            ImGuiEx.HelpMarker("0 turns this off (default). When the destination aethernet shard is in the zone you are already in and closer than this, Lifestream simply walks there instead of taking the aethernet - that saves a whole loading screen. Straight-line distance, not path length. If it cannot walk there in time it falls back to the normal aethernet route.".Loc());
            ImGui.Checkbox("Add firmament location into Foundation aetheryte".Loc(), ref C.Firmament);
            ImGuiEx.HelpMarker("Also lists the Firmament and its eight city aethernet shards in the teleport panel, grouped under the Foundation aetheryte - that is what lets you add them to your favorites.".Loc());
            ImGui.Checkbox("Add Sinus Ardorum location into Bestways Burrow aetheryte".Loc(), ref C.SinusArdorum);
            ImGuiEx.HelpMarker("Also lists Sinus Ardorum in the teleport panel, grouped under the Bestways Burrow aetheryte - that is what lets you add it to your favorites.".Loc());
            ImGui.Checkbox("Automatically leave non cross-world party upon changing world".Loc(), ref C.LeavePartyBeforeWorldChange);
            ImGui.Checkbox("Show teleport destination in chat".Loc(), ref C.DisplayChatTeleport);
            ImGui.Checkbox("Show teleport destination in popup notifications".Loc(), ref C.DisplayPopupNotifications);
            ImGui.Checkbox("Retry same-world failed world visits".Loc(), ref C.RetryWorldVisit);
            ImGui.Indent();
            ImGui.SetNextItemWidth(100f.Scale());
            ImGui.InputInt(LocText.IntervalBetweenRetriesSeconds.Loc() + "##2", ref C.RetryWorldVisitInterval.ValidateRange(1, 120));
            ImGui.SameLine();
            ImGuiEx.Text("+ up to".Loc());
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100f.Scale());
            ImGui.InputInt("seconds".Loc() + "##2", ref C.RetryWorldVisitIntervalDelta.ValidateRange(0, 120));
            ImGuiEx.HelpMarker("To make it appear less bot-like".Loc());
            ImGui.Unindent();
            //ImGui.Checkbox("Use Return instead of Teleport when possible", ref C.UseReturn);
            //ImGuiEx.HelpMarker("This includes any IPC calls");
            ImGui.Checkbox("Enable tray notifications upon travel completion".Loc(), ref C.EnableNotifications);
            ImGuiEx.PluginAvailabilityIndicator([new("NotificationMaster")]);
        })

        .Section("Shortcuts".Loc())
        .Widget(() =>
        {
            ImGui.SetNextItemWidth(200f.Scale());
            ImGuiEx.EnumCombo("\"/li\" command behavior".Loc(), ref C.LiCommandBehavior);
            ImGui.Checkbox("When teleporting to your own apartment, enter inside".Loc(), ref C.EnterMyApartment);
            ImGui.SetNextItemWidth(150f.Scale());
            ImGuiEx.EnumCombo("When teleporting to your/fc house, perform this action".Loc(), ref C.HouseEnterMode);
            ImGui.SetNextItemWidth(150f.Scale());
            if(ImGui.BeginCombo("Preferred Inn".Loc(), Utils.GetInnNameFromTerritory(C.PreferredInn), ImGuiComboFlags.HeightLarge))
            {
                foreach(var x in (uint[])[0, .. TaskPropertyShortcut.InnData.Keys])
                {
                    if(ImGui.Selectable(Utils.GetInnNameFromTerritory(x), x == C.PreferredInn)) C.PreferredInn = x;
                }
                ImGui.EndCombo();
            }
            if(Player.CID != 0)
            {
                ImGui.SetNextItemWidth(150f.Scale());
                var pref = C.PreferredSharedEstates.SafeSelect(Player.CID);
                var name = pref switch
                {
                    (0, 0, 0) => "First available".Loc(),
                    (-1, 0, 0) => "Disable".Loc(),
                    _ => $"{ExcelTerritoryHelper.GetName((uint)pref.Territory)}, W{pref.Ward}, P{pref.Plot}"
                };
                if(ImGui.BeginCombo("Preferred shared estate for ??".Loc(Player.NameWithWorld), name))
                {
                    foreach(var x in Svc.AetheryteList.Where(x => x.IsSharedHouse))
                    {
                        if(ImGui.RadioButton("First available".Loc(), pref == default))
                        {
                            C.PreferredSharedEstates.Remove(Player.CID);
                        }
                        if(ImGui.RadioButton("Disable".Loc(), pref == (-1, 0, 0)))
                        {
                            C.PreferredSharedEstates[Player.CID] = (-1, 0, 0);
                        }
                        if(ImGui.RadioButton("??, Ward ??, Plot ??".Loc(ExcelTerritoryHelper.GetName(x.TerritoryId), x.Ward, x.Plot), pref == ((int)x.TerritoryId, x.Ward, x.Plot)))
                        {
                            C.PreferredSharedEstates[Player.CID] = ((int)x.TerritoryId, x.Ward, x.Plot);
                        }
                    }
                    ImGui.EndCombo();
                }
            }
            ImGui.Separator();
            ImGuiEx.Text("\"/li auto\" command priority:".Loc());
            ImGui.SameLine();
            if(ImGui.SmallButton("Reset".Loc())) C.PropertyPrio.Clear();
            var dragDrop = Ref<ImGuiEx.RealtimeDragDrop<AutoPropertyData>>.Get(() => new("apddd", x => x.Type.ToString()));
            C.PropertyPrio.AddRange(Enum.GetValues<TaskPropertyShortcut.PropertyType>().Where(x => x != TaskPropertyShortcut.PropertyType.Auto && !C.PropertyPrio.Any(s => s.Type == x)).Select(x => new AutoPropertyData(false, x)));
            dragDrop.Begin();
            for(var i = 0; i < C.PropertyPrio.Count; i++)
            {
                var d = C.PropertyPrio[i];
                ImGui.PushID($"c{i}");
                dragDrop.NextRow();
                dragDrop.DrawButtonDummy(d, C.PropertyPrio, i);
                ImGui.SameLine();
                ImGui.Checkbox($"{d.Type}", ref d.Enabled);
                ImGui.PopID();
            }
            dragDrop.End();
            ImGui.Separator();
        })

        .Section("Map Integration".Loc())
        .Widget(() =>
        {
            ImGui.Checkbox("Click Aethernet Shard on map for quick teleport".Loc(), ref C.UseMapTeleport);
            ImGui.Checkbox("Only process when next to aetheryte in the same map".Loc(), ref C.DisableMapClickOtherTerritory);
        })

        .Section("Command completion".Loc())
        .Widget(() =>
        {
            ImGuiEx.Text("Suggest autocompletion when typing Lifestream commands in chat".Loc());
            ImGui.Checkbox("Enable".Loc(), ref C.EnableAutoCompletion);
            ImGui.Checkbox("Display popup window at fixed position".Loc(), ref C.AutoCompletionFixedWindow);
            ImGui.Indent();
            ImGui.SetNextItemWidth(200f.Scale());
            ImGui.DragFloat2("Position".Loc(), ref C.AutoCompletionWindowOffset, 1f);
            ImGuiEx.RadioButtonBool("From bottom".Loc(), "From top".Loc(), ref C.AutoCompletionWindowBottom, sameLine: true, inverted: true);
            ImGuiEx.RadioButtonBool("From right".Loc(), "From left".Loc(), ref C.AutoCompletionWindowRight, sameLine: true, inverted: true);
            ImGui.Unindent();
        })

        .Section("Cross-Datacenter".Loc())
        .Widget(() =>
        {
            ImGui.Checkbox("Allow travelling to another data center".Loc(), ref C.AllowDcTransfer);
            ImGui.Checkbox("Leave party before switching data center".Loc(), ref C.LeavePartyBeforeLogout);
            ImGui.Checkbox("Teleport to gateway aetheryte before switching data center if not in sanctuary".Loc(), ref C.TeleportToGatewayBeforeLogout);
            ImGui.Checkbox("Teleport to gateway aetheryte after completing data center travel".Loc(), ref C.DCReturnToGateway);
            ImGui.Checkbox("Allow alternative world during DC transfer".Loc(), ref C.DcvUseAlternativeWorld);
            ImGuiEx.HelpMarker("If destination world isn't available but some other world on targeted data center is, it will be selected instead. Normal world visit will be enqueued after logging in.".Loc());
            ImGui.Checkbox("Retry data center transfer if destination world is not available".Loc(), ref C.EnableDvcRetry);
            ImGui.Indent();
            ImGui.SetNextItemWidth(150f.Scale());
            ImGui.InputInt("Max retries".Loc(), ref C.MaxDcvRetries.ValidateRange(1, int.MaxValue));
            ImGui.SetNextItemWidth(150f.Scale());
            ImGui.InputInt(LocText.IntervalBetweenRetriesSeconds.Loc(), ref C.DcvRetryInterval.ValidateRange(10, 1000));
            ImGui.Unindent();
        })

        .Section("Unavailable worlds".Loc())
        .Widget(() =>
        {
            ImGuiEx.TextWrapped("Worlds listed here are never offered as a travel destination.".Loc());
            ImGuiEx.HelpMarker("This only controls where you can go. World names are still recognised everywhere else, so existing address book entries, name matching and the lobby world list keep working. Taiwan's Ramuh (4034) is excluded out of the box because that world was shut down.".Loc());
            if(C.UnavailableWorlds.Count == 0)
            {
                ImGuiEx.Text(ImGuiColors.DalamudGrey, "Nothing is excluded - every world is offered as a destination.".Loc());
            }
            else
            {
                // ToArray:迴圈裡會 Remove，不可以直接走訪原集合。
                foreach(var worldId in C.UnavailableWorlds.ToArray())
                {
                    if(ImGuiEx.IconButton(FontAwesomeIcon.Trash, $"UnavailableWorld{worldId}"))
                    {
                        C.UnavailableWorlds.Remove(worldId);
                        EzConfig.Save();
                    }
                    ImGuiEx.Tooltip("Remove".Loc());
                    ImGui.SameLine();
                    var worldName = ExcelWorldHelper.GetName(worldId);
                    if(worldName.IsNullOrEmpty())
                    {
                        // 查不到名字要在列上看得見是「不知道」，不能默默只印 id。
                        ImGuiEx.Text(ImGuiColors.DalamudGrey, $"? ({worldId})");
                        ImGuiEx.Tooltip("This world id is not present in the game's world table.".Loc());
                    }
                    else
                    {
                        ImGuiEx.Text(ImGuiColors.DalamudOrange, $"{worldName} ({worldId})");
                    }
                }
            }
            ImGui.SetNextItemWidth(200f.Scale());
            UnavailableWorldSelector.Draw(ref AddUnavailableWorld);
            ImGui.SameLine();
            if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Plus, "Add".Loc(), enabled: AddUnavailableWorld != 0 && !C.UnavailableWorlds.Contains((uint)AddUnavailableWorld)))
            {
                C.UnavailableWorlds.Add((uint)AddUnavailableWorld);
                AddUnavailableWorld = 0;
                EzConfig.Save();
            }
        })

        .Section("Address Book".Loc())
        .Widget(() =>
        {
            ImGui.Checkbox("Disable pathing to a plot".Loc(), ref C.AddressNoPathing);
            ImGuiEx.HelpMarker("You will be left at a closest aetheryte to the ward".Loc());
            ImGui.Checkbox("Disable entering an apartment".Loc(), ref C.AddressApartmentNoEntry);
            ImGuiEx.HelpMarker("You will be left at an entry confirmation dialogue".Loc());
        })

        .Section("Movement".Loc())
        .Checkbox("Use Mount when auto-moving".Loc(), () => ref C.UseMount)
        .Widget(() =>
        {
            Dictionary<int, string> mounts = [new KeyValuePair<int, string>(0, "Mount roulette".Loc()), .. Svc.Data.GetExcelSheet<Mount>().Where(x => x.Singular != "").ToDictionary(x => (int)x.RowId, x => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(x.Singular.GetText()))];
            ImGui.SetNextItemWidth(200f);
            ImGuiEx.Combo("Preferred Mount".Loc(), ref C.Mount, mounts.Keys, names: mounts);
        })
        .Checkbox("Use Sprint when auto-moving".Loc(), () => ref C.UseSprintPeloton)
        .Checkbox("Use Peloton when auto-moving".Loc(), () => ref C.UsePeloton)
        .Widget(() =>
        {
            // 「自動移動時使用坐騎」(C.UseMount) 一直存在，但走 vnavmesh 的野外導航那條路
            // (自訂落點 / /li goto) 是寫死不上坐騎的。這個選項只是把那條路接回既有的坐騎流程。
            // 📌 預設開(使用者裁決)：不上坐騎的舊行為只是實作限制，不是想要的行為。
            ImGui.Checkbox("Mount up for outdoor navigation".Loc(), ref C.GotoUseMount);
            ImGuiEx.HelpMarker("Applies to custom landing positions and \"/li goto\". Uses the same mount settings as above. Whether mounting is possible at all is left to the game - in a city, indoors, in combat or in a duty it simply walks instead.".Loc());
            if(C.GotoUseMount)
            {
                ImGui.Indent();
                ImGui.SetNextItemWidth(200f.Scale());
                ImGui.SliderFloat("Minimum path length to mount, yalms".Loc(), ref C.GotoMountMinDistance, 10f, 300f);
                ImGuiEx.HelpMarker("Shorter paths are walked - getting on and off a mount costs more time than it saves. This is the length of the actual path, not the straight-line distance.".Loc());
                ImGui.Unindent();
            }
        })
        .Widget(() =>
        {
            // 卡住偵測。vnavmesh 給的是折線路徑，而 Lifestream 是直線走向下一個航點 ——
            // 中間擦到欄杆、小石頭這種網格沒模到的小障礙物，角色就會頂著它推到那個航點的
            // 30 秒逾時為止，然後整趟作廢。這個選項是那個狀態唯一的出路。
            // 📌 預設開(見 Config.MovementStuckRecovery 的說明)：舊行為不是使用者選的，是失敗狀態。
            ImGui.Checkbox("Recover when movement gets stuck".Loc(), ref C.MovementStuckRecovery);
            ImGuiEx.HelpMarker("When auto-moving stops making progress for a few seconds, Lifestream jumps, and if that does not help it recalculates the path from where you are standing. It only ever runs when you are already stuck - without it the character keeps pushing against the obstacle until the 30 second timeout throws the whole trip away.".Loc());
            if(C.MovementStuckRecovery)
            {
                ImGui.Indent();
                ImGui.SetNextItemWidth(200f.Scale());
                ImGui.SliderInt("Maximum recovery attempts per path".Loc(), ref C.MovementStuckMaxRecoveries, 1, 20);
                ImGuiEx.HelpMarker("Jumping and recalculating are used alternately. Once these are used up the previous behaviour takes over, so terrain that genuinely cannot be passed still fails instead of looping forever.".Loc());
                ImGui.Unindent();
            }
        })

        .Section("Arrival Announcement".Loc())
        .Widget(() =>
        {
            // 單向通知，對方沒安裝時整條路徑都是 no-op(見 IPC/TataruPraiseIPC.cs)。
            // 📌 預設開：TataruPraise 自己的總開關預設是關的，所以這裡預設開不會讓任何人
            //    突然聽到聲音，只是「裝了就會用上」。
            ImGui.Checkbox("Ask Tataru to announce arrival".Loc(), ref C.TataruPraiseOnArrival);
            ImGuiEx.PluginAvailabilityIndicator([new("TataruPraise", "TataruPraise")]);
            ImGuiEx.HelpMarker("When a /li teleport or navigation chain runs all the way through and you have actually arrived, Lifestream asks TataruPraise to say one short line. It stays quiet when the chain is aborted or times out, and when auto-movement fails to reach the destination. Without TataruPraise installed this does nothing.".Loc());
        })

        .Section("Character Select Menu".Loc())
        .Checkbox("Enable Data center and World visit from Character Select Menu".Loc(), () => ref C.AllowDCTravelFromCharaSelect)
        .Checkbox("Use world visit instead of DC visit to travel to same world on guest DC".Loc(), () => ref C.UseGuestWorldTravel)

        .Section("Wotsit Integration".Loc())
        .Widget(() =>
        {
            var anyChanged = ImGui.Checkbox("Enable Wotsit Integration for teleporting to Aethernet destinations".Loc(), ref C.WotsitIntegrationEnabled);
            ImGuiEx.PluginAvailabilityIndicator([new("Dalamud.FindAnything", "Wotsit")]);

            if(C.WotsitIntegrationEnabled)
            {
                ImGui.Indent();
                if(ImGui.Checkbox("Include world select window".Loc(), ref C.WotsitIntegrationIncludes.WorldSelect))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("Include auto-teleport to property".Loc(), ref C.WotsitIntegrationIncludes.PropertyAuto))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("Include teleport to private estate".Loc(), ref C.WotsitIntegrationIncludes.PropertyPrivate))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("Include teleport to free company estate".Loc(), ref C.WotsitIntegrationIncludes.PropertyFreeCompany))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("Include teleport to apartment".Loc(), ref C.WotsitIntegrationIncludes.PropertyApartment))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("Include teleport to inn room".Loc(), ref C.WotsitIntegrationIncludes.PropertyInn))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("Include teleport to grand company".Loc(), ref C.WotsitIntegrationIncludes.GrandCompany))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("Include teleport to market board".Loc(), ref C.WotsitIntegrationIncludes.MarketBoard))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("Include teleport to island sanctuary".Loc(), ref C.WotsitIntegrationIncludes.IslandSanctuary))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("Include auto-teleport to aethernet destinations".Loc(), ref C.WotsitIntegrationIncludes.AetheryteAethernet))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("Include address book entries".Loc(), ref C.WotsitIntegrationIncludes.AddressBook))
                {
                    anyChanged = true;
                }
                if(ImGui.Checkbox("Include custom aliases".Loc(), ref C.WotsitIntegrationIncludes.CustomAlias))
                {
                    anyChanged = true;
                }
                ImGui.Unindent();
            }

            if(anyChanged)
            {
                PluginLog.Debug("Wotsit integration settings changed, re-initializing immediately");
                S.Ipc.WotsitManager.TryClearWotsit();
                S.Ipc.WotsitManager.MaybeTryInit(true);
            }
        })

        .Draw();
    }

    private static void DrawOverlay()
    {
        new NuiBuilder()
        .Section("General Overlay Settings".Loc())
        .Widget(() =>
        {
            ImGui.Checkbox("Enable Overlay".Loc(), ref C.Enable);
            if(C.Enable)
            {
                ImGui.Indent();
                ImGui.Checkbox("Display Aethernet menu".Loc(), ref C.ShowAethernet);
                ImGui.Checkbox("Display World Visit menu".Loc(), ref C.ShowWorldVisit);
                ImGui.Checkbox("Display Housing Ward buttons".Loc(), ref C.ShowWards);

                UtilsUI.NextSection();

                ImGui.Checkbox("Fixed Lifestream Overlay position".Loc(), ref C.FixedPosition);
                if(C.FixedPosition)
                {
                    ImGui.Indent();
                    ImGui.SetNextItemWidth(200f.Scale());
                    ImGuiEx.EnumCombo("Horizontal base position".Loc(), ref C.PosHorizontal);
                    ImGui.SetNextItemWidth(200f.Scale());
                    ImGuiEx.EnumCombo("Vertical base position".Loc(), ref C.PosVertical);
                    ImGui.SetNextItemWidth(200f.Scale());
                    ImGui.DragFloat2("Offset".Loc(), ref C.Offset);

                    ImGui.Unindent();
                }

                UtilsUI.NextSection();

                ImGui.SetNextItemWidth(100f.Scale());
                fixed(int* ptr = &C.ButtonWidthArray[0])
                fixed(byte* sptr = System.Text.Encoding.UTF8.GetBytes("Button left/right padding".Loc() + "\0"))
                {
                    ImGuiNative.InputInt3(sptr, ptr, ImGuiInputTextFlags.None);
                }
                ImGui.SetNextItemWidth(100f.Scale());
                ImGui.InputInt("Aetheryte button top/bottom padding".Loc(), ref C.ButtonHeightAetheryte);
                ImGui.SetNextItemWidth(100f.Scale());
                ImGui.InputInt("World button top/bottom padding".Loc(), ref C.ButtonHeightWorld);
                ImGui.Unindent();

                ImGui.Checkbox("Left-align text on buttons".Loc(), ref C.LeftAlignButtons);
                if(C.LeftAlignButtons)
                {
                    ImGui.SetNextItemWidth(100f);
                    ImGui.DragInt("Left padding, spaces".Loc(), ref C.LeftAlignPadding, 0.1f, 0, 20);
                }
            }
        })

        .Section("Instance changer".Loc())
        .Checkbox("Enabled".Loc(), () => ref C.ShowInstanceSwitcher)
        .Checkbox("Retry on failure".Loc(), () => ref C.InstanceSwitcherRepeat)
        .Checkbox("Return to the ground when flying before changing instance".Loc(), () => ref C.EnableFlydownInstance)
        .Checkbox("Teleport to the zone's aetheryte first if none is nearby".Loc(), () => ref C.InstanceTpToAetheryte)
        .Checkbox("Summon mount again after changing instance".Loc(), () => ref C.InstanceRemount)
        .Widget("Display instance number in Server Info Bar".Loc(), (x) =>
        {
            if(ImGui.Checkbox(x, ref C.EnableDtrBar))
            {
                S.DtrManager.Refresh();
            }
        })
        .SliderInt(150f, "Extra button height".Loc(), () => ref C.InstanceButtonHeight, 0, 50)
        .Widget("Reset Instance Data".Loc(), (x) =>
        {
            if(ImGuiEx.Button(x, C.PublicInstances.Count > 0))
            {
                C.PublicInstances.Clear();
                EzConfig.Save();
            }
        })

        .Section("Game Window Integration".Loc())
        .Checkbox("Hide Lifestream if the following game windows are open".Loc(), () => ref C.HideAddon)
        .If(() => C.HideAddon)
        .Widget(() =>
        {
            if(ImGui.BeginTable("HideAddonTable", 2, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.TableSetupColumn("col1", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("col2");

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGuiEx.SetNextItemFullWidth();
                ImGui.InputTextWithHint("##addnew", "Window name... /xldata ai - to find it".Loc(), ref AddNew, 100);
                ImGui.TableNextColumn();
                if(ImGuiEx.IconButton(FontAwesomeIcon.Plus))
                {
                    C.HideAddonList.Add(AddNew);
                    AddNew = "";
                }

                List<string> focused = [];
                try
                {
                    // 🔴 外面這層 try/catch 對這一行是**假安全**：RaptureAtkUnitManager.Instance()
                    //    是 CS 的手寫包裝（RaptureAtkModule 為 null 時回 null），裸解參考產生的是
                    //    AccessViolationException —— 在 .NET Core 屬 corrupted-state exception，
                    //    catch(Exception) 攔不到，只能事前擋。try 保留給 NameString 那類受管理例外。
                    //    取不到就維持空清單，畫面顯示「沒有聚焦中的視窗」，不崩潰。
                    var raptureAtkUnitManager = RaptureAtkUnitManager.Instance();
                    if(raptureAtkUnitManager != null)
                    {
                        foreach(var x in raptureAtkUnitManager->FocusedUnitsList.Entries)
                        {
                            if(x.Value == null) continue;
                            focused.Add(x.Value->NameString);
                        }
                    }
                }
                catch(Exception e) { e.Log(); }

                if(focused != null)
                {
                    foreach(var name in focused)
                    {
                        if(name == null) continue;
                        if(C.HideAddonList.Contains(name)) continue;
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGuiEx.TextV(EColor.Green, "Focused: ??".Loc(name));
                        ImGui.TableNextColumn();
                        ImGui.PushID(name);
                        if(ImGuiEx.IconButton(FontAwesomeIcon.Plus))
                        {
                            C.HideAddonList.Add(name);
                        }
                        ImGui.PopID();
                    }
                }

                ImGui.TableNextRow();
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, 0x88888888);
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, 0x88888888);
                ImGui.TableNextColumn();
                ImGui.Dummy(new Vector2(5f));

                foreach(var s in C.HideAddonList)
                {
                    ImGui.PushID(s);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGuiEx.TextV(focused.Contains(s) ? EColor.Green : null, s);
                    ImGui.TableNextColumn();
                    if(ImGuiEx.IconButton(FontAwesomeIcon.Trash))
                    {
                        new TickScheduler(() => C.HideAddonList.Remove(s));
                    }
                    ImGui.PopID();
                }

                ImGui.EndTable();
            }
        })
        .EndIf()
        .Draw();

        if(C.Hidden.Count > 0)
        {
            new NuiBuilder()
            .Section("Hidden Aetherytes".Loc())
            .Widget(() =>
            {
                uint toRem = 0;
                foreach(var x in C.Hidden)
                {
                    ImGuiEx.Text($"{Svc.Data.GetExcelSheet<Aetheryte>().GetRowOrDefault(x)?.AethernetName.ValueNullable?.Name.ToString() ?? x.ToString()}");
                    ImGui.SameLine();
                    if(ImGui.SmallButton("Delete".Loc() + $"##{x}"))
                    {
                        toRem = x;
                    }
                }
                if(toRem > 0)
                {
                    C.Hidden.Remove(toRem);
                }
            })
            .Draw();
        }
    }

    private static void DrawExpert()
    {
        new NuiBuilder()
        .Section("Expert Settings".Loc())
        .Widget(() =>
        {
            ImGui.Checkbox("Slow down aetheryte teleporting".Loc(), ref C.SlowTeleport);
            ImGuiEx.HelpMarker("Slows down aethernet teleportation by specified amount.".Loc());
            if(C.SlowTeleport)
            {
                ImGui.Indent();
                ImGui.SetNextItemWidth(200f.Scale());
                ImGui.DragInt("Teleport delay (ms)".Loc(), ref C.SlowTeleportThrottle);
                ImGui.Unindent();
            }
            ImGuiEx.CheckboxInverted("Skip waiting until game screen is ready".Loc(), ref C.WaitForScreenReady);
            ImGuiEx.HelpMarker("Enable this option for faster teleports but be careful that you may get stuck.".Loc());
            ImGui.Checkbox("Hide progress bar".Loc(), ref C.NoProgressBar);
            ImGuiEx.HelpMarker("Hiding progress bar leaves you with no way to stop Lifestream from executing it's tasks.".Loc());
            ImGuiEx.CheckboxInverted("Don't walk to nearby aetheryte on world change command from greater distance".Loc(), ref C.WalkToAetheryte);
            ImGui.Checkbox("Progress overlay at top of the sreen".Loc(), ref C.ProgressOverlayToTop);
            if(ImGui.Button("Reset progress bar".Loc()))
            {
                C.NoProgressBar = false;
                ProgressOverlay.ResetOverlay();
                Notify.Success("Progress bar has been reset.".Loc());
            }
            ImGuiEx.HelpMarker("Use this if the progress bar has stopped showing up. It re-enables the bar and re-creates its window, which also clears the error state that Dalamud locks a window into after a drawing error. The bar's position is recalculated from the current screen size every frame, so it can not get stuck off-screen.".Loc());
            ImGui.Checkbox("Allow custom alias and house alias to override built-in commands".Loc(), ref C.AllowCustomOverrides);
            ImGui.Indent();
            ImGuiEx.TextWrapped(EColor.RedBright, "Warning! Other plugins may rely on built-in commands. Ensure that it is not the case if you decide to enable this option and override commands.".Loc());
            ImGui.Unindent();
        })
        .Draw();
    }
}
