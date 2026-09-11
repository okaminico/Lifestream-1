using ECommons.Configuration;
using ECommons.GameHelpers;
using ECommons.MathHelpers;
using ECommons.SplatoonAPI;
using Lifestream.Data;
using Lifestream.Tasks.SameWorld;
using Newtonsoft.Json;
using NightmareUI.ImGuiElements;
using Aetheryte = Lumina.Excel.Sheets.Aetheryte;

namespace Lifestream.GUI;
public static class TabCustomAlias
{
    /// <summary>
    /// 「切換世界」這個別名指令選的是<b>傳送目的地</b>，所以要濾掉去不了的世界。
    /// 刻意自己開一個 selector 而不是用 <c>WorldSelector.Instance</c> —— 那是共用單例，
    /// 在它身上設 <c>ShouldHideWorld</c> 會一路影響到地址簿與傳送封鎖的世界欄，
    /// 讓既有的拉姆條目變得無法編輯。
    /// </summary>
    private static readonly WorldSelector ChangeWorldSelector = new("##changeworld")
    {
        ShouldHideWorld = PublicWorlds.IsUnavailable,
    };

    private static ImGuiEx.RealtimeDragDrop<CustomAliasCommand> DragDrop = new("CusACmd", x => x.ID);

    public static void Draw()
    {
        var selector = S.CustomAliasFileSystemManager.FileSystem.Selector;
        selector.Draw(150f);
        ImGui.SameLine();
        if(ImGui.BeginChild("Child"))
        {
            if(selector.Selected != null)
            {
                var item = selector.Selected;
                DrawAlias(item);
            }
            else
            {
                ImGuiEx.TextWrapped("To begin, select an alias you want to edit or create a new one.".Loc());
            }
        }
        ImGui.EndChild();
    }

    private static List<Action> PostTableActions = [];
    private static void DrawAlias(CustomAlias selected)
    {
        if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Plus, "Add new".Loc()))
        {
            selected.Commands.Add(new());
        }
        ImGui.SameLine();
        if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Paste, "Paste".Loc()))
        {
            try
            {
                var result = JsonConvert.DeserializeObject<CustomAliasCommand>(Paste());
                if(result == null) throw new NullReferenceException();
                selected.Commands.Add(result);
            }
            catch(Exception e)
            {
                Notify.Error(e.Message);
                e.Log();
            }
        }
        ImGui.SameLine();
        ImGui.Checkbox("##en", ref selected.Enabled);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100f.Scale());
        if(!selected.Enabled) ImGui.BeginDisabled();
        ImGui.InputText($"##Alias", ref selected.Alias, 50);
        if(!selected.Enabled) ImGui.EndDisabled();
        ImGuiEx.Tooltip("Enabled".Loc());
        ImGui.SameLine();
        ImGuiEx.HelpMarker("Will be available via \"/li ??\" command".Loc(selected.Alias));
        ImGui.SameLine();
        if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Play, "Run".Loc(), enabled: !Utils.IsBusy()))
        {
            selected.Enqueue();
        }
        ImGui.SameLine();
        ImGuiEx.Text("Visualisation:".Loc());
        ImGuiEx.PluginAvailabilityIndicator([new("Splatoon")]);
        DragDrop.Begin();
        var cursor = ImGui.GetCursorPos();
        foreach(var x in PostTableActions)
        {
            x();
        }
        ImGui.SetCursorPos(cursor);
        PostTableActions.Clear();
        if(ImGuiEx.BeginDefaultTable(["Control".Loc(), "~" + "Command".Loc()], false))
        {
            for(var i = 0; i < selected.Commands.Count; i++)
            {
                var x = selected.Commands[i];
                ImGui.TableNextRow();
                DragDrop.SetRowColor(x.ID);
                ImGui.TableNextColumn();
                DragDrop.NextRow();
                DragDrop.DrawButtonDummy(x, selected.Commands, i);
                ImGui.TableNextColumn();
                var curpos = ImGui.GetCursorPos() + ImGui.GetContentRegionAvail() with { Y = 0 };
                if(x.Kind == CustomAliasKind.Move_to_point)
                {
                    var insertIndex = i + 1;
                    PostTableActions.Add(() =>
                    {
                        ImGui.PushFont(UiBuilder.IconFont);
                        ImGui.SetCursorPos(curpos - ImGuiHelpers.GetButtonSize(FontAwesomeIcon.Clone.ToIconString()) with { Y = 0 });
                        ImGui.PopFont();
                        if(ImGuiEx.IconButton(FontAwesomeIcon.Clone, x.ID, enabled: Player.Available))
                        {
                            new TickScheduler(() =>
                            {
                                selected.Commands.Insert(insertIndex, new()
                                {
                                    Kind = CustomAliasKind.Move_to_point,
                                    UseFlight = x.UseFlight,
                                    Point = Player.Position
                                });
                            });
                        }
                        ImGuiEx.Tooltip("Clone this command and set it's coordinates to player's coordinates".Loc());
                    });
                }

                ImGuiEx.TreeNodeCollapsingHeader("Command ??:".Loc(i + 1) + $" {x.Kind.ToString().Replace('_', ' ')}{GetExtraText(x)}###{x.ID}", () => DrawCommand(x, selected), ImGuiTreeNodeFlags.CollapsingHeader);
                DrawSplatoon(x, i);


            }
            ImGui.EndTable();
        }
        DragDrop.End();
    }

    private static string GetExtraText(CustomAliasCommand x)
    {
        if(x.Kind == CustomAliasKind.Move_to_point)
        {
            return $" {x.Point:F1}";
        }
        return "";
    }

    private static void DrawSplatoon(CustomAliasCommand command, int index)
    {
        if(!Splatoon.IsConnected()) return;
        if(command.Kind == CustomAliasKind.Circular_movement)
        {
            {
                var point = S.Ipc.SplatoonManager.GetNextPoint($"{index + 1}: Circular movement");
                point.SetRefCoord(command.CenterPoint.ToVector3());
                Splatoon.DisplayOnce(point);
            }
            {
                var point = S.Ipc.SplatoonManager.GetNextPoint($"{index + 1}: Circular exit");
                point.SetRefCoord(command.CircularExitPoint);
                Splatoon.DisplayOnce(point);
            }
            {
                var point = S.Ipc.SplatoonManager.GetNextPoint();
                point.SetRefCoord(command.CenterPoint.ToVector3());
                point.Filled = false;
                point.radius = command.Clamp == null ? Math.Clamp(Player.DistanceTo(command.CenterPoint), 1f, 10f) : (command.Clamp.Value.Min + command.Clamp.Value.Max) / 2f;
                Splatoon.DisplayOnce(point);
            }
        }
        else if(command.Kind == CustomAliasKind.Move_to_point)
        {
            var point = S.Ipc.SplatoonManager.GetNextPoint($"{index + 1}: Walk to");
            point.SetRefCoord(command.Point);
            point.radius = command.Scatter;
            Splatoon.DisplayOnce(point);
        }
        else if(command.Kind == CustomAliasKind.Navmesh_to_point)
        {
            var point = S.Ipc.SplatoonManager.GetNextPoint($"{index + 1}: Navmesh to");
            point.SetRefCoord(command.Point);
            Splatoon.DisplayOnce(point);
        }
    }


    private static readonly uint[] Aetherytes = Svc.Data.GetExcelSheet<Aetheryte>().Where(x => x.PlaceName.ValueNullable?.Name.ToString().IsNullOrEmpty() == false && x.IsAetheryte).Select(x => x.RowId).ToArray();
    private static readonly Dictionary<uint, string> AetherytePlaceNames = Aetherytes.Select(Svc.Data.GetExcelSheet<Aetheryte>().GetRow).ToDictionary(x => x.RowId, x => x.PlaceName.Value.Name.ToString());

    private static void DrawCommand(CustomAliasCommand command, CustomAlias selected)
    {
        ImGui.PushID(command.ID);
        if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Copy, "Copy".Loc()))
        {
            Copy(EzConfig.DefaultSerializationFactory.Serialize(command, false));
        }
        ImGui.SameLine();
        if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Trash, "Delete".Loc(), ImGuiEx.Ctrl))
        {
            new TickScheduler(() => selected.Commands.Remove(command));
        }
        ImGuiEx.Tooltip("Press CTRL and click".Loc());

        ImGui.Separator();
        ImGui.SetNextItemWidth(150f.Scale());
        ImGuiEx.EnumCombo("Alias kind".Loc(), ref command.Kind);

        if(command.Kind == CustomAliasKind.Teleport_to_Aetheryte)
        {
            ImGui.SetNextItemWidth(150f.Scale());
            ImGuiEx.Combo("Select aetheryte to teleport to".Loc(), ref command.Aetheryte, Aetherytes, names: AetherytePlaceNames);
            ImGui.SetNextItemWidth(60f.Scale());
            ImGui.DragFloat("Skip teleport if already at aetheryte within this range".Loc(), ref command.SkipTeleport, 0.01f);
        }

        if(command.Kind.EqualsAny(CustomAliasKind.Move_to_point, CustomAliasKind.Navmesh_to_point))
        {
            Utils.DrawVector3Selector($"walktopoint{command.ID}", ref command.Point);
            ImGui.SameLine();
            ImGuiEx.Text(UiBuilder.IconFont, FontAwesomeIcon.ArrowsLeftRight.ToIconString());
            ImGui.SameLine();
            ImGui.SetNextItemWidth(50f);
            ImGui.SliderFloat($"##scatter", ref command.Scatter, 0f, 2f);
            ImGuiEx.Tooltip("Scatter".Loc());
        }

        if(command.Kind.EqualsAny(CustomAliasKind.Move_to_point))
        {
            drawFlight();
        }

        if(command.Kind.EqualsAny(CustomAliasKind.Navmesh_to_point))
        {
            ImGui.SameLine();
            ImGuiEx.ButtonCheckbox(FontAwesomeIcon.FastForward, ref command.UseTA, EColor.Green);
            ImGuiEx.Tooltip("Use TextAdvance for movement. Flight settings are inherited from TextAdvance.".Loc());
            if(!command.UseTA)
            {
                drawFlight();
            }
        }

        void drawFlight()
        {
            ImGui.SameLine();
            ImGuiEx.ButtonCheckbox(FontAwesomeIcon.Plane, ref command.UseFlight, EColor.Green);
            ImGuiEx.Tooltip("Fly for movement. Don't forget to use \"Mount Up\" command before. ".Loc());
        }

        if(command.Kind == CustomAliasKind.Change_world)
        {
            ImGui.SetNextItemWidth(150f.Scale());
            ChangeWorldSelector.Draw(ref command.World);
            ImGui.SameLine();
            ImGuiEx.Text("Select world".Loc());
        }

        if(command.Kind == CustomAliasKind.Use_Aethernet)
        {
            ImGui.SetNextItemWidth(150f.Scale());
            if(ImGui.BeginCombo("Select aethernet shard to teleport to".Loc(), command.Aetheryte == 0 ? "- Not selected -".Loc() : Utils.KnownAetherytes.SafeSelect(command.Aetheryte, command.Aetheryte.ToString()), ImGuiComboFlags.HeightLarge))
            {
                ref var filter = ref Ref<string>.Get($"Filter{command.ID}");
                ImGui.SetNextItemWidth(200f);
                ImGui.InputTextWithHint("##filter", "Filter".Loc(), ref filter, 50);
                foreach(var x in Utils.KnownAetherytesByCategories)
                {
                    bool shouldHide(ref string filter, KeyValuePair<uint, string> v) => filter.Length > 0 && !v.Value.Contains(filter, StringComparison.OrdinalIgnoreCase) && !x.Key.Contains(filter, StringComparison.OrdinalIgnoreCase);
                    foreach(var v in x.Value)
                    {
                        if(!shouldHide(ref filter, v)) goto Display;
                    }
                    continue;
                Display:
                    ImGuiEx.Text(EColor.YellowBright, $"{x.Key}:");
                    ImGui.Indent();
                    foreach(var v in x.Value)
                    {
                        if(shouldHide(ref filter, v)) continue;
                        var sel = command.Aetheryte == v.Key;
                        if(sel && ImGui.IsWindowAppearing()) ImGui.SetScrollHereY();
                        if(ImGui.Selectable($"{v.Value}##{v.Key}", sel))
                        {
                            command.Aetheryte = v.Key;
                        }
                    }
                    ImGui.Unindent();
                    ImGui.Separator();
                }
                ImGui.EndCombo();
            }
        }

        if(command.Kind == CustomAliasKind.Circular_movement)
        {
            if(ImGui.BeginTable("circular", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings))
            {
                ImGui.TableSetupColumn("1");
                ImGui.TableSetupColumn("1", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGuiEx.TextV("Center point: ".Loc());
                ImGui.TableNextColumn();
                Utils.DrawVector2Selector("center", ref command.CenterPoint);

                ImGui.TableNextColumn();
                ImGuiEx.TextV("Exit point: ".Loc());
                ImGui.TableNextColumn();
                Utils.DrawVector3Selector($"exit{command.ID}", ref command.CircularExitPoint);
                ImGui.Checkbox("Finish by walking to exit point".Loc(), ref command.WalkToExit);

                ImGui.TableNextColumn();
                ImGuiEx.TextV("Precision: ".Loc());
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(100f.Scale());
                ImGui.DragFloat("##precision", ref command.Precision.ValidateRange(4f, 100f), 0.01f);

                ImGui.TableNextColumn();
                ImGuiEx.TextV("Tolerance: ".Loc());
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(100f.Scale());
                ImGui.DragInt("##tol", ref command.Tolerance.ValidateRange(1, (int)(command.Precision * 0.75f)), 0.01f);

                ImGui.TableNextColumn();
                ImGuiEx.TextV("Distance limit: ".Loc());
                ImGui.TableNextColumn();
                var en = command.Clamp != null;
                if(ImGui.Checkbox($"##clamp", ref en))
                {
                    if(en)
                    {
                        command.Clamp = (0, 10);
                    }
                    else
                    {
                        command.Clamp = null;
                    }
                }
                if(command.Clamp != null)
                {
                    var v = command.Clamp.Value;
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(50f.Scale());
                    ImGui.DragFloat("##prec1", ref v.Min, 0.01f);
                    ImGui.SameLine();
                    ImGuiEx.Text("-");
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(50f.Scale());
                    ImGui.DragFloat("##prec2", ref v.Max, 0.01f);
                    if(v.Min < v.Max)
                    {
                        command.Clamp = v;
                    }
                    if(Svc.Targets.Target != null)
                    {
                        ImGui.SameLine();
                        ImGuiEx.Text("To target: ??".Loc(Player.DistanceTo(Svc.Targets.Target).ToString("F1")));
                    }
                }

                ImGui.EndTable();
            }
        }
        if(command.Kind == CustomAliasKind.Interact)
        {
            ImGui.SetNextItemWidth(150f.Scale());
            ImGuiEx.InputUint("Data ID".Loc(), ref command.DataID);
            ImGui.SameLine();
            if(ImGuiEx.Button("Target".Loc(), Svc.Targets.Target?.BaseId != 0))
            {
                command.DataID = Svc.Targets.Target.BaseId;
            }
        }
        if(command.Kind.EqualsAny(CustomAliasKind.Select_Yes, CustomAliasKind.Select_List_Option))
        {
            ImGuiEx.TextWrapped("List entries that you would like to select/confirm:".Loc());
            if(ImGuiEx.BeginDefaultTable("ItemLst", ["~1", "2"], false))
            {
                for(var i = 0; i < command.SelectOption.Count; i++)
                {
                    ImGui.PushID(i);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGuiEx.SetNextItemFullWidth();
                    var str = command.SelectOption[i];
                    if(ImGui.InputText("##selectOpt", ref str, 500))
                    {
                        command.SelectOption[i] = str;
                    }
                    ImGui.TableNextColumn();
                    if(ImGuiEx.IconButton(FontAwesomeIcon.Trash))
                    {
                        var idx = i;
                        new TickScheduler(() => command.SelectOption.RemoveAt(idx));
                    }
                    ImGui.PopID();
                }
                ImGui.EndTable();
                if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Plus, "Add New Option".Loc()))
                {
                    command.SelectOption.Add("");
                }
            }
        }
        if(command.Kind.EqualsAny(CustomAliasKind.Select_Yes, CustomAliasKind.Select_List_Option, CustomAliasKind.Confirm_Contents_Finder))
        {
            ImGui.Checkbox("Skip on screen fade".Loc(), ref command.StopOnScreenFade);
        }
        ImGui.PopID();
    }
}
