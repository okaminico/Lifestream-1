using ECommons.Configuration;
using ECommons.Events;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using Lifestream.Data;
using Lifestream.Tasks.SameWorld;
using Lifestream.Tasks.Shortcuts;
using Lumina.Excel.Sheets;
using static ECommons.Singletons.SingletonServiceManager;
using Map = Lumina.Excel.Sheets.Map;
using Path = System.IO.Path;

namespace Lifestream.Systems.Legacy;

public class DataStore
{
    internal string FileName = "StaticData.json";
    internal uint[] Territories;
    internal Dictionary<TinyAetheryte, List<TinyAetheryte>> Aetherytes = [];
    internal string[] Worlds = Array.Empty<string>();
    internal string[] DCWorlds = Array.Empty<string>();
    internal Dictionary<TaskISShortcut.IslandNPC, string[]> IslandNPCs = [];
    internal StaticData StaticData;

    internal TinyAetheryte GetMaster(TinyAetheryte aetheryte)
    {
        foreach(var x in Aetherytes.Keys)
        {
            if(x.Group == aetheryte.Group) return x;
        }
        return default;
    }

    public DataStore()
    {
        var terr = new List<uint>();
        StaticData = EzConfig.LoadConfiguration<StaticData>(Path.Combine(Svc.PluginInterface.AssemblyLocation.DirectoryName, FileName), false);
        Svc.Data.GetExcelSheet<Aetheryte>().Each(x =>
        {
            // 「最佳威兔洞」(175)沒有 AethernetGroup,但比照蒼天街建模渴望灣需要它成為 master
            // (ActiveAetheryte/overlay 機制只認得 Aetherytes 字典裡的乙太之光)。
            if(x.AethernetGroup != 0 || x.RowId == TaskAetheryteAethernetTeleport.SinusArdorumRootAetheryteId)
            {
                if(x.IsAetheryte)
                {
                    Aetherytes[GetTinyAetheryte(x)] = [];
                    terr.Add(x.Territory.Value.RowId);
                }
            }
        });
        Svc.Data.GetExcelSheet<Aetheryte>().Each(x =>
        {
            if(x.AethernetGroup != 0)
            {
                if(!x.IsAetheryte)
                {
                    var a = GetTinyAetheryte(x);
                    Aetherytes[GetMaster(a)].Add(a);
                    terr.Add(x.Territory.Value.RowId);
                }
            }
        });
        foreach(var x in Aetherytes.Keys.ToArray())
        {
            Aetherytes[x] = [.. Aetherytes[x].OrderBy(x => GetAetheryteSortOrder(x.ID))];
        }
        Territories = [.. terr];
        if(ProperOnLogin.PlayerPresent)
        {
            BuildWorlds();
        }

        foreach(TaskISShortcut.IslandNPC npc in Enum.GetValues(typeof(TaskISShortcut.IslandNPC)))
        {
            if(Svc.Data.GetExcelSheet<ENpcResident>().TryGetRow((uint)npc, out var row))
            {
                IslandNPCs.Add(npc, [row.Singular.ToString(), row.Title.ToString()]);
            }
        }

        ProperOnLogin.RegisterAvailable(BuildWorlds);
    }

    internal uint GetAetheryteSortOrder(uint id)
    {
        var ret = 10000u;
        if(StaticData.SortOrder.TryGetValue(id, out var x))
        {
            ret += x;
        }
        if(C.Favorites.Contains(id))
        {
            ret -= 10000u;
        }
        return ret;
    }

    internal void BuildWorlds()
    {
        BuildWorlds(Svc.Objects.LocalPlayer.CurrentWorld.Value.DataCenter.Value.RowId);
        if(Player.Available)
        {
            if(P.AutoRetainerApi?.Ready == true && C.UseAutoRetainerAccounts)
            {
                var data = P.AutoRetainerApi.GetOfflineCharacterData(Player.CID);
                if(data != null)
                {
                    C.ServiceAccounts[Player.NameWithWorld] = data.ServiceAccount;
                }
            }
            else if(!C.ServiceAccounts.ContainsKey(Player.NameWithWorld))
            {
                C.ServiceAccounts[Player.NameWithWorld] = -1;
            }
        }
    }

    internal void BuildWorlds(uint dc)
    {
        // 台服例外:八個正式世界的 World.IsPublic 全是 False,走一般路徑會得到空清單。
        var playerWorld = Player.Object;
        if(dc == PublicWorlds.TaiwanDataCenterId
            || playerWorld != null
            && (PublicWorlds.IsTaiwanWorld(playerWorld.CurrentWorld.RowId)
                || PublicWorlds.IsTaiwanWorld(playerWorld.HomeWorld.RowId)))
        {
            Worlds = [.. PublicWorlds.Travelable(PublicWorlds.GetTaiwanWorlds())
                .Select(x => x.Name.ToString())
                .Order()];
            DCWorlds = [];
            PluginLog.Debug($"Built Taiwan worlds: {Worlds.Print()}");
            return;
        }

        Worlds = [.. Svc.Data.GetExcelSheet<World>().Where(x => x.DataCenter.Value.RowId == dc && PublicWorlds.IsPublic(x) && !PublicWorlds.IsUnavailable(x)).Select(x => x.Name.ToString()).Order()];
        PluginLog.Debug($"Built worlds: {Worlds.Print()}");
        DCWorlds = Svc.Data.GetExcelSheet<World>().Where(x => x.DataCenter.Value.RowId != dc && PublicWorlds.IsPublic(x) && !PublicWorlds.IsUnavailable(x) && (x.DataCenter.Value.Region == Player.Object.HomeWorld.Value.DataCenter.Value.Region || x.DataCenter.Value.Region == 4)).Select(x => x.Name.ToString()).ToArray();
        PluginLog.Debug($"Built DCworlds: {DCWorlds.Print()}");
    }

    internal TinyAetheryte GetTinyAetheryte(Aetheryte aetheryte)
    {
        var AethersX = 0f;
        var AethersY = 0f;
        if(StaticData.CustomPositions.TryGetValue(aetheryte.RowId, out var pos))
        {
            AethersX = pos.X;
            AethersY = pos.Z;
        }
        else
        {
            var map = Svc.Data.GetExcelSheet<Map>().FirstOrDefault(m => m.TerritoryType.RowId == aetheryte.Territory.Value.RowId);
            var scale = map.SizeFactor;
            if(Svc.Data.GetSubrowExcelSheet<MapMarker>().AllRows().TryGetFirst(m => m.DataType == (aetheryte.IsAetheryte ? 3 : 4) && m.DataKey.RowId == (aetheryte.IsAetheryte ? aetheryte.RowId : aetheryte.AethernetName.RowId), out var mapMarker))
            {
                AethersX = Utils.ConvertMapMarkerToRawPosition(mapMarker.X, scale);
                AethersY = Utils.ConvertMapMarkerToRawPosition(mapMarker.Y, scale);
            }
        }
        return new(new(AethersX, AethersY), aetheryte.Territory.Value.RowId, aetheryte.RowId, aetheryte.AethernetGroup);
    }
}
