using ECommons.ExcelServices;
using ECommons.GameHelpers;
using ECommons.SimpleGui;
using Lumina.Excel.Sheets;

namespace Lifestream.GUI.Windows;
public class SelectWorldWindow : Window
{
    private SelectWorldWindow() : base("Lifestream: Select World", ImGuiWindowFlags.AlwaysAutoResize)
    {
        EzConfigGui.WindowSystem.AddWindow(this);
    }

    public override void Draw()
    {
        // 台服:World.IsPublic 全為 False 使得 DataStore 名稱解析不可靠,
        // 直接以 RowId 範圍列出八個正式世界(cycleapple 851230d 的修正)
        if(Player.Object != null
            && (PublicWorlds.IsTaiwanWorld(Player.Object.HomeWorld.RowId)
                || PublicWorlds.IsTaiwanWorld(Player.Object.CurrentWorld.RowId)))
        {
            // 排除清單(預設含已停止營運的拉姆 4034)在建清單的這一刻就濾掉，
            // 讓選單與 /li 的候選共用同一份真值，不要在兩個地方各判一次。
            var taiwanWorlds = PublicWorlds.Travelable(PublicWorlds.GetTaiwanWorlds());
            if(taiwanWorlds.Length == 0)
            {
                ImGuiEx.Text("No available destinations".Loc());
                return;
            }
            DrawTaiwanWorlds([.. taiwanWorlds.Select(world => (World?)world)]);
            return;
        }

        var worlds = S.Data.DataStore.DCWorlds.Concat(S.Data.DataStore.Worlds).Select(x => ExcelWorldHelper.Get(x)).OrderBy(x => x?.Name.ToString());
        if(!worlds.Any())
        {
            ImGuiEx.Text("No available destinations".Loc());
            return;
        }
        var datacenters = worlds.Select(x => x?.DataCenter).DistinctBy(x => x?.RowId).OrderBy(x => x.Value.ValueNullable?.Region).ToArray();
        if(ImGui.BeginTable("LifestreamSelectWorld", datacenters.Length, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersV | ImGuiTableFlags.BordersOuter))
        {
            foreach(var dc in datacenters)
            {
                var modifier = "";
                if(Player.Object?.HomeWorld.ValueNullable?.DataCenter.RowId == dc?.RowId) modifier += "";
                if(Player.Object?.CurrentWorld.ValueNullable?.DataCenter.RowId != dc?.RowId) modifier += "";
                ImGui.TableSetupColumn($"{modifier}{dc.Value.ValueNullable?.Name}");
            }
            ImGui.TableHeadersRow();
            ImGui.TableNextRow();
            var buttonSize = Vector2.Zero;
            foreach(var w in worlds)
            {
                var newSize = ImGuiHelpers.GetButtonSize("" + w?.Name.ToString());
                if(newSize.X > buttonSize.X) buttonSize = newSize;
            }
            buttonSize += new Vector2(0, C.ButtonHeightWorld);
            foreach(var dc in datacenters)
            {
                ImGui.TableNextColumn();
                foreach(var world in worlds)
                {
                    if(world?.DataCenter.RowId == dc?.RowId)
                    {
                        var modifier = "";
                        if(Player.Object?.HomeWorld.RowId == world?.RowId) modifier += "";
                        if(ImGuiEx.Button(modifier + world?.Name.ToString(), buttonSize, !Utils.IsBusy() && Player.Interactable && Player.Object?.CurrentWorld.RowId != world?.RowId))
                        {
                            P.ProcessCommand("/li", world?.Name.ToString());
                        }
                    }
                }
            }
            ImGui.EndTable();
        }
    }

    private static void DrawTaiwanWorlds(World?[] worlds)
    {
        if(!ImGui.BeginTable("LifestreamSelectTaiwanWorld", 1, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersV | ImGuiTableFlags.BordersOuter))
            return;

        //  = 遊戲字型的「家」圖示,與上方一般路徑的 modifier 用法一致
        var modifier = "";
        if(Player.Object != null
            && (PublicWorlds.IsTaiwanWorld(Player.Object.HomeWorld.RowId)
                || PublicWorlds.IsTaiwanWorld(Player.Object.CurrentWorld.RowId)))
            modifier = "";

        var dcName = Svc.Data.GetExcelSheet<WorldDCGroupType>().GetRowOrDefault(PublicWorlds.TaiwanDataCenterId)?.Name.ToString() ?? "";
        ImGui.TableSetupColumn($"{modifier}{dcName}");
        ImGui.TableHeadersRow();
        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        var buttonSize = worlds
            .Select(world => ImGuiHelpers.GetButtonSize("" + world?.Name.ToString()))
            .Aggregate(Vector2.Zero, (current, size) => new Vector2(Math.Max(current.X, size.X), Math.Max(current.Y, size.Y)));
        buttonSize += new Vector2(0, C.ButtonHeightWorld);

        foreach(var world in worlds)
        {
            var worldModifier = Player.Object?.HomeWorld.RowId == world?.RowId ? "" : "";
            if(ImGuiEx.Button(worldModifier + world?.Name.ToString(), buttonSize,
                   !Utils.IsBusy() && Player.Interactable && Player.Object?.CurrentWorld.RowId != world?.RowId))
                P.ProcessCommand("/li", world?.Name.ToString());
        }

        ImGui.EndTable();
    }
}
