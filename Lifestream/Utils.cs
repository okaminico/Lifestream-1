using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using ECommons.Automation;
using ECommons.ChatMethods;
using ECommons.Configuration;
using ECommons.ExcelServices;
using ECommons.ExcelServices.Sheets;
using ECommons.ExcelServices.TerritoryEnumeration;
using ECommons.GameHelpers;
using ECommons.Interop;
using ECommons.MathHelpers;
using ECommons.Reflection;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lifestream.Data;
using Lifestream.Enums;
using Lifestream.GUI;
using Lifestream.Systems.Custom;
using Lifestream.Systems.Legacy;
using Lifestream.Systems.Residential;
using Lifestream.Tasks.CrossDC;
using Lifestream.Tasks.SameWorld;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using NightmareUI;
using System;
using System.Collections.Specialized;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using TerraFX.Interop.Windows;
using static FFXIVClientStructs.FFXIV.Client.UI.AddonAirShipExploration;
using Action = System.Action;
using CharaData = (string Name, ushort World);
using FXWindows = TerraFX.Interop.Windows.Windows;

namespace Lifestream;

internal static unsafe partial class Utils
{
    public static string[] LifestreamNativeCommands = ["auto", "home", "house", "private", "fc", "free", "company", "free company", "apartment", "apt", "shared", "inn", "hinn", "gc", "gcc", "hc", "hcc", "fcgc", "gcfc", "mb", "market", "island", "is", "sanctuary", "cosmic", "ardorum", "moon", "tp"];

    public static Vector3 Scatter(this Vector3 point, float radius)
    {
        if(radius > 0)
        {
            var angle = Random.Shared.NextDouble() * Math.PI * 2;

            var distance = Math.Sqrt(Random.Shared.NextDouble()) * radius;

            var offsetX = (float)(Math.Cos(angle) * distance);
            var offsetZ = (float)(Math.Sin(angle) * distance);

            return new Vector3(point.X + offsetX, point.Y, point.Z + offsetZ);
        }
        else
        {
            return point;
        }
    }

    public static bool EnqueueTeleport(string destination, string additionalCommand)
    {
        foreach(var x in Svc.AetheryteList.Where(s => s.AetheryteData.IsValid))
        {
            if(x.AetheryteData.Value.AethernetName.ToString().Contains(destination, StringComparison.OrdinalIgnoreCase))
            {
                if(S.TeleportService.TeleportToAetheryte(x.AetheryteId, wait: !additionalCommand.IsNullOrEmpty()))
                {
                    ChatPrinter.Green($"[Lifestream] Destination (Aethernet): {x.AetheryteData
                        .Value.AethernetName.ValueNullable?.Name} at {ExcelTerritoryHelper.GetName(x.AetheryteData.Value.Territory.RowId)}");
                    return true;
                }
            }
        }
        foreach(var x in Svc.AetheryteList.Where(s => s.AetheryteData.IsValid && s.AetheryteData.Value.PlaceName.IsValid))
        {
            if(x.AetheryteData.Value.PlaceName.Value.Name.ToString().Contains(destination, StringComparison.OrdinalIgnoreCase))
            {
                if(S.TeleportService.TeleportToAetheryte(x.AetheryteId, wait: !additionalCommand.IsNullOrEmpty()))
                {
                    ChatPrinter.Green($"[Lifestream] Destination (Place): {x.AetheryteData
                        .Value.PlaceName.ValueNullable?.Name} at {ExcelTerritoryHelper.GetName(x.AetheryteData.Value.Territory.RowId)}");
                    return true;
                }
            }
        }
        foreach(var x in Svc.AetheryteList.Where(s => s.AetheryteData.IsValid && s.AetheryteData.Value.Territory.IsValid && s.AetheryteData.Value.Territory.Value.PlaceName.IsValid))
        {
            if(x.AetheryteData.Value.Territory.Value.PlaceName.Value.Name.ToString().Contains(destination, StringComparison.OrdinalIgnoreCase))
            {
                if(S.TeleportService.TeleportToAetheryte(x.AetheryteId, wait: !additionalCommand.IsNullOrEmpty()))
                {
                    ChatPrinter.Green($"[Lifestream] Destination (Zone): {x.AetheryteData
                        .Value.Territory.Value.PlaceName.Value.Name} at {ExcelTerritoryHelper.GetName(x.AetheryteData.Value.Territory.RowId)}");
                    return true;
                }
            }
        }
        return false;
    }

    public static IGameObject GetWorkshopEntrance()
    {
        return Svc.Objects.FirstOrDefault(x => x.IsTargetable && x.Name.ToString().EqualsIgnoreCaseAny(Lang.AdditionalChambersEntrance));
    }

    public static bool TryFindEqualsOrContains<T>(IEnumerable<T> haystack, Func<T, string> haystackConverterToString, string needle, out T result)
    {
        return TryFindEqualsOrContains(haystack, haystackConverterToString, [needle], out result);
    }

    public static bool TryFindEqualsOrContains<T>(IEnumerable<T> haystack, Func<T, string> haystackConverterToString, IEnumerable<string> needles, out T result)
    {
        foreach(var x in haystack)
        {
            foreach(var n in needles)
            {
                if(haystackConverterToString(x).EqualsIgnoreCase(n))
                {
                    result = x;
                    return true;
                }
            }
        }
        foreach(var x in haystack)
        {
            foreach(var n in needles)
            {
                if(haystackConverterToString(x).StartsWith(n, StringComparison.OrdinalIgnoreCase))
                {
                    result = x;
                    return true;
                }
            }
        }
        foreach(var x in haystack)
        {
            foreach(var n in needles)
            {
                if(haystackConverterToString(x).Contains(n, StringComparison.OrdinalIgnoreCase))
                {
                    result = x;
                    return true;
                }
            }
        }
        result = default;
        return false;
    }

    public static Dictionary<uint, string> KnownAetherytes
    {
        get
        {
            if(field == null)
            {
                field = [];
                foreach(var x in KnownAetherytesByCategories)
                {
                    foreach(var v in x.Value)
                    {
                        field[v.Key] = v.Value;
                    }
                }
            }
            return field;
        }
    }

    public static Dictionary<string, Dictionary<uint, string>> KnownAetherytesByCategories
    {
        get
        {
            if(field == null)
            {
                field = [];
                foreach(var x in S.Data.DataStore.Aetherytes)
                {
                    var dict = new Dictionary<uint, string>()
                    {
                        [x.Key.ID] = x.Key.Name,
                    };
                    field[ExcelTerritoryHelper.GetName(x.Key.TerritoryType)] = dict;
                    foreach(var v in x.Value)
                    {
                        dict[v.ID] = v.Name;
                    }
                    if(x.Key.ID == 70)
                    {
                        dict[TaskAetheryteAethernetTeleport.FirmamentAethernetId] = "Firmament";
                    }
                    if(x.Key.ID == TaskAetheryteAethernetTeleport.SinusArdorumRootAetheryteId)
                    {
                        // 175 沒有 AethernetName(master 名稱是空字串),改用地名;並比照蒼天街掛上渴望灣
                        if(dict[x.Key.ID] == "")
                        {
                            dict[x.Key.ID] = Svc.Data.GetExcelSheet<Aetheryte>().GetRowOrDefault(x.Key.ID)?.PlaceName.ValueNullable?.Name.GetText() ?? x.Key.ID.ToString();
                        }
                        dict[TaskAetheryteAethernetTeleport.SinusArdorumAethernetId] = "Sinus Ardorum";
                    }
                }
                foreach(var x in S.Data.ResidentialAethernet.ZoneInfo)
                {
                    var dict = new Dictionary<uint, string>();
                    field[ExcelTerritoryHelper.GetName(x.Key)] = dict;
                    foreach(var v in x.Value.Aetherytes)
                    {
                        dict[v.ID] = v.Name;
                    }
                }
                foreach(var x in S.Data.CustomAethernet.ZoneInfo)
                {
                    var dict = new Dictionary<uint, string>();
                    field[ExcelTerritoryHelper.GetName(x.Key)] = dict;
                    foreach(var v in x.Value.Aetherytes)
                    {
                        dict[v.ID] = v.Name;
                    }
                }
            }
            return field;
        }
    } = null;

    public static bool ApproachConditionIsMet()
    {
        return (P.ActiveAetheryte == null || !P.ActiveAetheryte.Value.IsAetheryte) && Utils.GetReachableAetheryte(x => x.IsAetheryte()) != null;
    }

    public static string GetAethernetNameWithOverrides(uint id)
    {
        // TC (Traditional Chinese, USERJOY server) isn't one of the languages the bundled
        // KnownAetherytes dataset was captured in, so its names never match what actually shows in
        // the TC client's aethernet menu, causing "Destination could not be found" even for
        // completely standard destinations. Try the live Excel sheet first - it resolves in
        // whatever language the running client's game data actually is, so it works correctly
        // regardless of client language - and only fall back to the bundled dataset (for entries,
        // like Firmament's synthetic ID, that aren't real Aetheryte sheet rows at all).
        var live = Svc.Data.GetExcelSheet<Aetheryte>().GetRowOrDefault(id)?.AethernetName.Value.Name.GetText();
        if(!string.IsNullOrEmpty(live)) return live;
        if(Utils.KnownAetherytes.TryGetValue(id, out var ret)) return ret;
        return null;
    }

    public static bool WotsitInstalled()
    {
        return Svc.PluginInterface.InstalledPlugins.Any(x => x.InternalName == "Dalamud.FindAnything" && x.IsLoaded);
    }

    public static string ParseSheetPattern(string s)
    {
        try
        {
            {
                var match = ParseSheetValueRegex().Match(s);
                if(match.Success)
                {
                    var type = typeof(Addon).Assembly.GetType($"Lumina.Excel.Sheets.{match.Groups[1].Value}", true);
                    var rowId = uint.Parse(match.Groups[2].Value);
                    var col = match.Groups[3].Value;
                    var result = Svc.Data.GetType().GetMethod("GetExcelSheet", ReflectionHelper.AllFlags).MakeGenericMethod([type]).Invoke(Svc.Data, [null, null]).Call("GetRow", [rowId]).GetFoP(col);
                    if(result is ReadOnlySeString ross)
                    {
                        return ross.GetText();
                    }
                    return result.ToString();
                }
            }
            {
                var match = ParseQuestDialogueTextSheetRegex().Match(s);
                if(match.Success)
                {
                    var sheet = Svc.Data.GetExcelSheet<QuestDialogueText>(name: match.Groups[1].Value);
                    var rowId = uint.Parse(match.Groups[2].Value);
                    var col = match.Groups[3].Value;
                    var result = sheet.Call("GetRow", [rowId]).GetFoP(col);
                    if(result is ReadOnlySeString ross)
                    {
                        return ross.GetText();
                    }
                    return result.ToString();
                }
            }
            return s;
        }
        catch(Exception e)
        {
            e.Log();
            return s;
        }
    }

    [GeneratedRegex(@"^<([a-z]+):([0-9]+):([a-z]+)>$", RegexOptions.IgnoreCase)]
    private static partial Regex ParseSheetValueRegex();

    [GeneratedRegex(@"^<QuestDialogueText:([a-z_/0-9]+):([0-9]+):([a-z]+)>$", RegexOptions.IgnoreCase)]
    private static partial Regex ParseQuestDialogueTextSheetRegex();

    public static string GetMountName(int id)
    {
        return Svc.Data.GetExcelSheet<Mount>().GetRow((uint)id).Singular.ExtractText();
    }
    public static string GetWorldFromCID(ulong cid)
    {
        return Utils.GetCharaName(cid)?.Split("@").SafeSelect(1);
    }

    public static bool IsInsideHouse()
    {
        return P.Territory.EqualsAny<uint>(
            Houses.Private_Cottage_Mist, Houses.Private_House_Mist, Houses.Private_Mansion_Mist,
            Houses.Private_Cottage_Empyreum, Houses.Private_House_Empyreum, Houses.Private_Mansion_Empyreum,
            Houses.Private_Cottage_Shirogane, Houses.Private_House_Shirogane, Houses.Private_Mansion_Shirogane,
            Houses.Private_Cottage_The_Goblet, Houses.Private_House_The_Goblet, Houses.Private_Mansion_The_Goblet,
            Houses.Private_Cottage_The_Lavender_Beds, Houses.Private_House_The_Lavender_Beds, Houses.Private_Mansion_The_Lavender_Beds,
            1249, 1250, 1251
            );
    }

    public static bool IsInsideWorkshop()
    {
        return P.Territory.EqualsAny(Houses.Company_Workshop_Empyreum, Houses.Company_Workshop_Mist, Houses.Company_Workshop_Shirogane, Houses.Company_Workshop_The_Goblet, Houses.Company_Workshop_The_Lavender_Beds);
    }

    public static bool IsInsidePrivateChambers()
    {
        return P.Territory.EqualsAny(Houses.Private_Chambers_Empyreum, Houses.Private_Chambers_Mist, Houses.Private_Chambers_Shirogane, Houses.Private_Chambers_The_Goblet, Houses.Private_Chambers_The_Lavender_Beds);
    }

    public static bool IsTerritoryResidentialDistrict(ushort obj)
    {
        return obj.EqualsAny(ResidentalAreas.Mist, ResidentalAreas.Shirogane, ResidentalAreas.Empyreum, ResidentalAreas.The_Goblet, ResidentalAreas.The_Lavender_Beds);
    }

    public static void EnsureEnhancedLoginIsOff()
    {
        /*try
        {
            if(Svc.PluginInterface.InstalledPlugins.Any(x => x.InternalName == "HaselTweaks" && x.IsLoaded))
            {
                if(DalamudReflector.TryGetDalamudPlugin("HaselTweaks", out var instance, out var context, false, true))
                {
                    var configWindow = ReflectionHelper.CallStatic(context.Assemblies, "HaselCommon.Service", [], "Get", ["HaselTweaks.Windows.PluginWindow"], []);
                    var tweaks = (System.Collections.IEnumerable)configWindow.GetFoP("Tweaks");
                    foreach(var x in tweaks)
                    {
                        if(x.GetFoP<string>("InternalName") == "EnhancedLoginLogout" && x.GetFoP<int>("Status") == 5)
                        {
                            configWindow.GetFoP("TweakManager").Call("UserDisableTweak", [x], true);
                            new PopupWindow(() =>
                            {
                                ImGuiEx.Text($"""
                                    Enhanced Login/Logout from HaselTweaks plugin has been detected.
                                    It is not compatible with Lifestream and has been disabled.
                                    """);
                            });
                        }
                    }
                }
            }
        }
        catch(Exception e)
        {
            e.Log();
        }*/
    }

    public static string GetCharaName(ulong cid)
    {
        if(C.CharaMap.TryGetValue(cid, out var name)) return name;
        return $"#{cid:X16}";
    }

    public static void ReadClipboardFiles()
    {
        try
        {
            var fType = AppDomain.CurrentDomain.GetAssemblies().First(x => x.GetName().Name == "System.Windows.Forms");
            var clipboard = fType.GetType("System.Windows.Forms.Clipboard");
            if((bool)clipboard.GetMethod("ContainsFileDropList").Invoke(null, []))
            {
                var cb = (StringCollection)clipboard.GetMethod("GetFileDropList").Invoke(null, []);
                foreach(var f in cb)
                {
                    DuoLog.Information($"{f}");
                }
            }
        }
        catch(Exception e)
        {
            e.Log();
        }
    }

    public static bool IsMountedEx()
    {
        if(Svc.Condition[ConditionFlag.Mounted]) return true;
        if(IsPlayerFalling()) return true;
        return false;
    }

    public static bool DismountIfNeeded()
    {
        if(Utils.IsMountedEx())
        {
            EzThrottler.Throttle("PlayerMounted", 200, true);
            if(EzThrottler.Throttle("DismountPlayer", 1000))
            {
                Chat.ExecuteGeneralAction(23);
            }
            return false;
        }
        if(!EzThrottler.Check("PlayerMounted")) return false;
        return true;
    }

    public static bool IsPlayerFalling()
    {
        var p = Svc.Objects.LocalPlayer;
        if(p == null)
            return true;

        // 0 if grounded
        // 1 = "jumpsquat"
        // 3 = going up
        // 4 = stopped
        // 5 = going down
        var isJumping = *(byte*)(p.Address + 496 + 208) > 0;
        // 1 iff dismounting and haven't hit the ground yet
        var isAirDismount = **(byte**)(p.Address + 496 + 904) == 1;

        return isJumping || isAirDismount;
    }

    public static void DrawWorldSelector(ICollection<int> worldList)
    {
        ImGuiEx.CollectionCheckbox("All", PublicWorlds.Get().Select(x => (int)x.RowId), worldList);
        ImGui.Indent();
        // 用 PublicWorlds.AllRegions() 而不是 Enum.GetValues<Region>()：ECommons 的列舉沒有台服的 8，
        // 直接迭代列舉會讓陸行鳥資料中心與其底下的世界整組消失——而同一個畫面上方的
        // PublicWorlds.Get()（全部聚合）是含台服的，兩者會不一致。
        var regions = PublicWorlds.AllRegions();
        foreach(var r in regions)
        {
            ImGuiEx.CollectionCheckbox(PublicWorlds.RegionDisplayName(r), PublicWorlds.Get(r).Select(x => (int)x.RowId), worldList);
            var dc = ExcelWorldHelper.GetDataCenters(r);
            ImGui.Indent();
            foreach(var d in dc)
            {
                var worlds = PublicWorlds.Get(d.RowId);
                ImGuiEx.CollectionCheckbox(d.Name.ToString(), worlds.Select(x => (int)x.RowId), worldList);
                ImGui.Indent();
                foreach(var w in worlds.OrderBy(x => x.Name.ToString()))
                {
                    ImGuiEx.CollectionCheckbox(w.Name.ToString(), (int)w.RowId, worldList);
                }
                ImGui.Unindent();
            }
            ImGui.Unindent();
        }
        ImGui.Unindent();
    }

    public static bool IsTravelBlocked(string charaName, Number charaWorld, Number sourceWorld, string targetWorld)
    {
        return IsTravelBlocked(charaName, charaWorld, sourceWorld, ExcelWorldHelper.Get(targetWorld).Value.RowId);
    }

    public static bool IsTravelBlocked(string charaName, Number charaWorld, Number sourceWorld, Number targetWorld)
    {
        foreach(var x in C.TravelBans)
        {
            if(x.IsEnabled && x.CharaName == charaName && x.CharaHomeWorld == charaWorld)
            {
                if(x.BannedFrom.Contains(sourceWorld) && x.BannedTo.Contains(targetWorld))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static void AssertCanTravel(string charaName, Number charaWorld, Number sourceWorld, string targetWorld)
    {
        AssertCanTravel(charaName, charaWorld, sourceWorld, ExcelWorldHelper.Get(targetWorld).Value.RowId);
    }

    public static void AssertCanTravel(string charaName, Number charaWorld, Number sourceWorld, Number targetWorld)
    {
        if(IsTravelBlocked(charaName, charaWorld, sourceWorld, targetWorld))
        {
            var err = $"Character {charaName}@{ExcelWorldHelper.GetName(charaWorld)} can not travel from {ExcelWorldHelper.GetName(sourceWorld)} to {ExcelWorldHelper.GetName(targetWorld)}. Access Lifestream - Travel Block to change it.";
            Svc.Toasts.ShowError(err);
            Notify.Error(err);
            DuoLog.Error(err);
            throw new InvalidOperationException(err);
        }
    }

    public static bool GenericThrottle => FrameThrottler.Throttle("LifestreamGenericThrottle", 10);
    public static void RethrottleGeneric(int num = 10)
    {
        FrameThrottler.Throttle("LifestreamGenericThrottle", num, true);
    }
    public static void ScreenToWorldSelector(string id, ref Vector3 point)
    {
        ref var isInWorldToScreen = ref Ref<bool>.Get($"{id}_screenToWorldSelector");
        if(isInWorldToScreen)
        {
            if(Svc.GameGui.ScreenToWorld(ImGui.GetIO().MousePos, out var worldPos))
            {
                point = worldPos;
            }
            ImGui.BeginTooltip();
            ImGuiEx.Text(LocText.PointLeftClickToFinish.Loc(point.ToString("F2")));
            ImGui.EndTooltip();
            if(IsKeyPressed((int)Keys.LButton))
            {
                isInWorldToScreen = false;
            }
        }
    }

    public static void BeginScreenToWorldSelection(string id, Vector3 point)
    {
        ref var isInWorldToScreen = ref Ref<bool>.Get($"{id}_screenToWorldSelector");
        if(Svc.GameGui.WorldToScreen(point, out var screenPos))
        {
            SetCursorTo((int)screenPos.X, (int)screenPos.Y);
        }
        isInWorldToScreen = true;
    }

    public static void ScreenToWorldSelector(string id, ref Vector2 point)
    {
        ref var isInWorldToScreen = ref Ref<bool>.Get($"{id}_screenToWorldSelector");
        if(isInWorldToScreen)
        {
            //PluginLog.Debug($"{ImGui.GetIO().MousePos}");
            if(Svc.GameGui.ScreenToWorld(ImGui.GetIO().MousePos, out var worldPos))
            {
                point = worldPos.ToVector2();
            }
            ImGui.BeginTooltip();
            ImGuiEx.Text(LocText.PointLeftClickToFinish.Loc(point.ToString("F2")));
            ImGui.EndTooltip();
            if(IsKeyPressed((int)Keys.LButton))
            {
                isInWorldToScreen = false;
            }
        }
    }

    public static void BeginScreenToWorldSelection(string id, Vector2 point)
    {
        ref var isInWorldToScreen = ref Ref<bool>.Get($"{id}_screenToWorldSelector");
        if(Svc.GameGui.WorldToScreen(point.ToVector3(Player.Position.Y), out var screenPos))
        {
            SetCursorTo((int)screenPos.X, (int)screenPos.Y);
        }
        isInWorldToScreen = true;
    }

    public static void SetCursorTo(int x, int y)
    {
        for(var i = 0; i < 1000; i++)
        {
            if(WindowFunctions.TryFindGameWindow(out var hwnd))
            {
                var point = new POINT() { x = x, y = y };
                if(FXWindows.ClientToScreen(hwnd, &point))
                {
                    FXWindows.SetCursorPos(point.x, point.y);
                }
                break;
            }
        }

    }

    public static void DrawVector2Selector(string id, ref Vector2 value)
    {
        ImGui.SetNextItemWidth(150f.Scale());
        ImGui.DragFloat2($"##vec{id}", ref value, 0.01f);
        ImGui.SameLine();
        if(ImGuiEx.IconButton(FontAwesomeIcon.MapPin, $"myPos{id}", enabled: Player.Interactable))
        {
            value = Player.Position.ToVector2();
        }
        ImGuiEx.Tooltip("To player positon".Loc());
        ImGui.SameLine();
        if(ImGuiEx.IconButton(FontAwesomeIcon.Crosshairs, $"target{id}", enabled: Svc.Targets.Target != null))
        {
            value = Svc.Targets.Target.Position.ToVector2();
        }
        ImGuiEx.Tooltip("To target positon".Loc());
        ImGui.SameLine();
        if(ImGuiEx.IconButton(FontAwesomeIcon.MousePointer, $"target{id}", enabled: Player.Interactable))
        {
            BeginScreenToWorldSelection(id, value);
        }
        ScreenToWorldSelector(id, ref value);
        ImGuiEx.Tooltip("Select with mouse".Loc());
        ImGui.SameLine();
        // AgentMap.Instance() 是 AgentGetterGenerator 產出的兩層可空取得器
        // (`agentModule == null ? null : GetAgentByInternalId(...)`)——合法回 null。
        // 這裡在繪製路徑每幀跑,取一次、判一次,拿不到就把按鈕停用(fail-closed)。
        var flagAgent = AgentMap.Instance();
        if(ImGuiEx.IconButton(FontAwesomeIcon.Flag, $"flag{id}", enabled: Player.Interactable && flagAgent != null && flagAgent->FlagMarkerCount > 0))
        {
            var marker = flagAgent->FlagMapMarkers[0];
            value = new(marker.XFloat, marker.YFloat);
        }
        ScreenToWorldSelector(id, ref value);
        ImGuiEx.Tooltip("To map flag".Loc());
    }

    public static void DrawVector3Selector(string id, ref Vector3 value)
    {
        ImGui.SetNextItemWidth(150f.Scale());
        ImGui.DragFloat3($"##vec{id}", ref value, 0.01f);
        ImGui.SameLine();
        if(ImGuiEx.IconButton(FontAwesomeIcon.MapPin, $"myPos{id}", enabled: Player.Interactable))
        {
            value = Player.Position;
        }
        ImGuiEx.Tooltip("To player positon".Loc());
        ImGui.SameLine();
        if(ImGuiEx.IconButton(FontAwesomeIcon.Crosshairs, $"target{id}", enabled: Svc.Targets.Target != null))
        {
            value = Svc.Targets.Target.Position;
        }
        ImGuiEx.Tooltip("To target positon".Loc());
        ImGui.SameLine();
        if(ImGuiEx.IconButton(FontAwesomeIcon.MousePointer, $"target{id}", enabled: Player.Interactable))
        {
            BeginScreenToWorldSelection(id, value);
        }
        ScreenToWorldSelector(id, ref value);
        ImGuiEx.Tooltip("Select with mouse".Loc());
        ImGui.SameLine();
        // 同上:AgentMap 取得器合法回 null,取一次、判一次,拿不到就停用按鈕。
        var flagAgent = AgentMap.Instance();
        if(ImGuiEx.IconButton(FontAwesomeIcon.Flag, $"flag{id}", enabled: Player.Interactable && flagAgent != null && flagAgent->FlagMarkerCount > 0))
        {
            var marker = flagAgent->FlagMapMarkers[0];
            value = new(marker.XFloat, 0, marker.YFloat);
        }
        ScreenToWorldSelector(id, ref value);
        ImGuiEx.Tooltip("To map flag".Loc());
    }

    public static IEnumerable<uint> GetAllRegisteredAethernetDestinations()
    {
        foreach(var x in S.Data.DataStore.Aetherytes)
        {
            yield return x.Key.ID;
            foreach(var v in x.Value)
            {
                yield return v.ID;
            }
        }
    }

    public static WorldChangeAetheryte AdjustGateway(this WorldChangeAetheryte gateway)
    {
        if(!Svc.AetheryteList.Any(x => x.AetheryteId == (int)gateway))
        {
            foreach(var c in Enum.GetValues<WorldChangeAetheryte>())
            {
                if(Svc.AetheryteList.Any(x => x.AetheryteId == (int)c))
                {
                    DuoLog.Warning($"{gateway} is not unlocked, but {c} is, adjusting.");
                    gateway = c;
                    break;
                }
            }
        }
        return gateway;
    }

    public static bool IsAddonVisible(string name)
    {
        if(TryGetAddonByName<AtkUnitBase>(name, out var addon) && addon->IsVisible) return true;
        return false;
    }

    public static List<World> GetVisitableWorldsFrom(World source)
    {
        var ret = new List<World>();
        foreach(var x in PublicWorlds.Get(source.GetRegion()))
        {
            ret.Add(x);
        }
        foreach(var x in PublicWorlds.Get(ExcelWorldHelper.Region.OC))
        {
            if(!ret.Contains(x)) ret.Add(x);
        }
        return ret;
    }

    public static List<HousePathData> GetHousePathDatas()
    {
        if(P.DisableHousePathData) return [];
        return C.HousePathDatas;
    }

    public static bool IsAetheryteEligibleForCustomAlias(IGameObject go)
    {
        if(go.ObjectKind == ObjectKind.Aetheryte && TryGetTinyAetheryteFromIGameObject(go, out var tiny))
        {
            return tiny.Value.ID == S.Data.DataStore.GetMaster(tiny.Value).ID;
        }
        return true;
    }

    public static uint[] AethernetShards = [2000151, 2000153, 2000154, 2000155, 2000156, 2000157, 2003395, 2003396, 2003397, 2003398, 2003399, 2003400, 2003401, 2003402, 2003403, 2003404, 2003405, 2003406, 2003407, 2003408, 2003409, 2003995, 2003996, 2003997, 2003998, 2003999, 2004000, 2004968, 2004969, 2004970, 2004971, 2004972, 2004973, 2004974, 2004976, 2004977, 2004978, 2004979, 2004980, 2004981, 2004982, 2004983, 2004984, 2004985, 2004986, 2004987, 2004988, 2004989, 2007434, 2007435, 2007436, 2007437, 2007438, 2007439, 2007855, 2007856, 2007857, 2007858, 2007859, 2007860, 2007861, 2007862, 2007863, 2007864, 2007865, 2007866, 2007867, 2007868, 2007869, 2007870, 2009421, 2009432, 2009433, 2009562, 2009563, 2009564, 2009565, 2009615, 2009616, 2009617, 2009618, 2009713, 2009714, 2009715, 2009981, 2010135, 2011142, 2011162, 2011163, 2011241, 2011243, 2011373, 2011374, 2011384, 2011385, 2011386, 2011387, 2011388, 2011389, 2011573, 2011574, 2011575, 2011677, 2011678, 2011679, 2011680, 2011681, 2011682, 2011683, 2011684, 2011685, 2011686, 2011687, 2011688, 2011689, 2011690, 2011691, 2011692, 2012252, 2012253, 2011160, 2011572, 2014664, 2014744, 2014665, 2014666, 2014667,];

    public static uint[] HousingAethernet = [MainCities.Limsa_Lominsa_Lower_Decks, MainCities.Uldah_Steps_of_Nald, MainCities.New_Gridania, MainCities.Foundation, MainCities.Kugane];

    public static HousePathData GetFCPathData() => Utils.GetHousePathDatas().FirstOrDefault(x => x.CID == Player.CID && !x.IsPrivate);
    public static HousePathData GetPrivatePathData() => Utils.GetHousePathDatas().FirstOrDefault(x => x.CID == Player.CID && x.IsPrivate);
    public static HousePathData GetCustomPathData(ResidentialAetheryteKind kind, int ward, int plot)
    {
        return C.HousePathDatas.FirstOrDefault(x => x.ResidentialDistrict == kind && x.Ward == ward && x.Plot == plot) ?? C.CustomHousePathDatas.FirstOrDefault(x => x.ResidentialDistrict == kind && x.Ward == ward && x.Plot == plot);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="territory"></param>
    /// <param name="plot">Start with 0</param>
    /// <returns></returns>
    public static Vector3? GetPlotEntrance(uint territory, int plot)
    {
        if(S.Data.ResidentialAethernet.HousingData.Data.TryGetValue(territory, out var data) && data.Count > plot && data[plot].Path.Count > 0) return data[plot].Path[^1];
        return null;
    }

    public static IGameObject GetNearestEntrance(out float Distance)
    {
        var currentDistance = float.MaxValue;
        IGameObject currentObject = null;

        foreach(var x in Svc.Objects)
        {
            if(x.IsTargetable && x.Name.ToString().EqualsIgnoreCaseAny([.. Lang.Entrance]))
            {
                var distance = Vector3.Distance(Svc.Objects.LocalPlayer.Position, x.Position);
                if(distance < currentDistance)
                {
                    currentDistance = distance;
                    currentObject = x;
                }
            }
        }
        Distance = currentDistance;
        return currentObject;
    }

    public static void DisplayInfo(string s, bool? displayChat = null, bool? displayPopup = null)
    {
        if(displayChat ?? C.DisplayChatTeleport) ChatPrinter.Green($"[Lifestream] {s}");
        if(displayPopup ?? C.DisplayPopupNotifications) Notify.Info(s);
    }

    public static HouseEnterMode GetHouseEnterMode(this HousePathData data)
    {
        if(data != null && data.EnableHouseEnterModeOverride) return data.EnterModeOverride;
        return C.HouseEnterMode;
    }

    public static bool IsBusy()
    {
        return P.TaskManager.IsBusy || P.followPath?.waypointsInternal.Count > 0;
    }

    public static bool CanFly() => S.Memory.FlightAddr != 0 && S.Memory.IsFlightProhibited(S.Memory.FlightAddr) == 0;

    public static bool TryGetWorldFromDataCenter(string s, out string world, out uint dataCenter)
    {
        foreach(var x in Svc.Data.GetExcelSheet<WorldDCGroupType>())
        {
            if(x.RowId == 0 || x.Name == "") continue;
            if(x.Name.GetText().StartsWith(s, StringComparison.OrdinalIgnoreCase))
            {
                var worlds = PublicWorlds.Get(x.RowId);
                if(worlds.Length > 0)
                {
                    world = worlds[Random.Shared.Next(worlds.Length)].Name.ToString();
                    dataCenter = x.RowId;
                    if(S.Data.DataStore.Worlds.Contains(world) || S.Data.DataStore.DCWorlds.Contains(world))
                    {
                        return true;
                    }
                }
            }
        }
        dataCenter = default;
        world = default;
        return false;
    }

    public static bool TryParseAddressBookEntry(string s, out AddressBookEntry entry, bool retry = false)
    {
        entry = null;
        {
            var regex = ReplaceAddressBookRegex(@"(%worlds)%delimiter(%city)%delimiter(W|ward)%shortDelimiter%numeric%delimiter(P|plot)%shortDelimiter%numeric");
            PluginLog.Debug($"Testing vs: {regex}");
            var result = Regex.Match(s, regex, RegexOptions.IgnoreCase);
            if(result.Success)
            {
                PluginLog.Debug($"→Success: {result.Groups.Values.Select(x => x.Value).Skip(1).Print()}");
                entry = BuildAddressBookEntry(result.Groups[1].Value, result.Groups[2].Value, result.Groups[4].Value, result.Groups[6].Value, false, false);
                if(entry != null) return true;
            }
        }
        {
            var regex = ReplaceAddressBookRegex(@"(%worlds)%delimiter(%city)%delimiter(W|ward)%shortDelimiter%numeric%optDelimiter(s|sub|subdivision)%delimiter(A|apartment)%shortDelimiter%numeric");
            PluginLog.Debug($"Testing vs: {regex}");
            var result = Regex.Match(s, regex, RegexOptions.IgnoreCase);
            if(result.Success)
            {
                PluginLog.Debug($"→Success: {result.Groups.Values.Select(x => x.Value).Skip(1).Print()}");
                entry = BuildAddressBookEntry(result.Groups[1].Value, result.Groups[2].Value, result.Groups[4].Value, result.Groups[7].Value, true, true);
                if(entry != null) return true;
            }
        }
        {
            var regex = ReplaceAddressBookRegex(@"(%worlds)%delimiter(%city)%delimiter(W|ward)%shortDelimiter%numeric%delimiter(A|apartment)%shortDelimiter%numeric%optDelimiter(s|sub|subdivision)");
            PluginLog.Debug($"Testing vs: {regex}");
            var result = Regex.Match(s, regex, RegexOptions.IgnoreCase);
            if(result.Success)
            {
                PluginLog.Debug($"→Success: {result.Groups.Values.Select(x => x.Value).Skip(1).Print()}");
                entry = BuildAddressBookEntry(result.Groups[1].Value, result.Groups[2].Value, result.Groups[4].Value, result.Groups[6].Value, true, true);
                if(entry != null) return true;
            }
        }
        {
            var regex = ReplaceAddressBookRegex(@"(%worlds)%delimiter(%city)%delimiter(W|ward)%shortDelimiter%numeric%delimiter(A|apartment)%shortDelimiter%numeric");
            PluginLog.Debug($"Testing vs: {regex}");
            var result = Regex.Match(s, regex, RegexOptions.IgnoreCase);
            if(result.Success)
            {
                PluginLog.Debug($"→Success: {result.Groups.Values.Select(x => x.Value).Skip(1).Print()}");
                entry = BuildAddressBookEntry(result.Groups[1].Value, result.Groups[2].Value, result.Groups[4].Value, result.Groups[6].Value, true, false);
                if(entry != null) return true;
            }
        }
        {
            var regex = ReplaceAddressBookRegex(@"(%worlds)%delimiter(%city)%delimiter(W|ward|)%shortDelimiter%numeric%delimiter(P|plot|)%shortDelimiter%numeric");
            PluginLog.Debug($"Testing vs: {regex}");
            var result = Regex.Match(s, regex, RegexOptions.IgnoreCase);
            if(result.Success)
            {
                PluginLog.Debug($"→Success: {result.Groups.Values.Select(x => x.Value).Skip(1).Print()}");
                entry = BuildAddressBookEntry(result.Groups[1].Value, result.Groups[2].Value, result.Groups[4].Value, result.Groups[6].Value, false, false);
                if(entry != null) return true;
            }
        }
        if(!retry)
        {
            if(TryParseAddressBookEntry(Player.CurrentWorld + ", " + s, out entry, true))
            {
                return entry != null;
            }
        }
        return entry != null;
    }

    public static string ReplaceAddressBookRegex(string str)
    {
        var cities = "goblet|the goblet|lavender beds|the lavender beds|lavender|lb|empy|empyreum|shiro|shirogane|mist";
        var worlds = PublicWorlds.Get().Select(x => x.Name.ToString()).Join("|") + "|[a-z]{3,30}";
        return str.Replace("%worlds", worlds)
            .Replace("%delimiter", @"[\s\.\,\-\(\)\t]{1,10}")
            .Replace("%optDelimiter", @"[\s\.\,\-\(\)\t]{0,10}")
            .Replace("%city", cities)
            .Replace("%shortDelimiter", @"[\s\.\-\t]{0,3}")
            .Replace("%numeric", "([0-9]{1,2})");
    }

    public static AddressBookEntry BuildAddressBookEntry(string worldStr, string cityStr, string wardNum, string plotApartmentNum, bool isApartment, bool isSubdivision, string name = null)
    {
        var world = ExcelWorldHelper.Get(worldStr, true);
        if(world == null)
        {
            foreach(var x in PublicWorlds.Get())
            {
                if(x.Name.ToString().StartsWith(worldStr, StringComparison.OrdinalIgnoreCase))
                {
                    world = x;
                    break;
                }
            }
        }
        if(world == null) return null;
        var city = ParseResidentialAetheryteKind(cityStr);
        if(city == null) return null;
        if(int.TryParse(wardNum, out var ward) && int.TryParse(plotApartmentNum, out var plot))
        {
            var entry = new AddressBookEntry()
            {
                City = city.Value,
                World = (int)world?.RowId,
                PropertyType = isApartment ? PropertyType.Apartment : PropertyType.House,
                Ward = ward,
                Apartment = plot,
                Plot = plot,
                ApartmentSubdivision = isSubdivision,
            };
            if(name != null) entry.Name = name;
            return entry;
        }
        return null;
    }

    public static ResidentialAetheryteKind? ParseResidentialAetheryteKind(string s)
    {
        if(s.ContainsAny(StringComparison.OrdinalIgnoreCase, "mist"))
        {
            return ResidentialAetheryteKind.Limsa;
        }
        if(s.ContainsAny(StringComparison.OrdinalIgnoreCase, "goblet"))
        {
            return ResidentialAetheryteKind.Uldah;
        }
        if(s.ContainsAny(StringComparison.OrdinalIgnoreCase, "empy"))
        {
            return ResidentialAetheryteKind.Foundation;
        }
        if(s.ContainsAny(StringComparison.OrdinalIgnoreCase, "shiro"))
        {
            return ResidentialAetheryteKind.Kugane;
        }
        if(s.ContainsAny(StringComparison.OrdinalIgnoreCase, "lavender", "beds", "lb"))
        {
            return ResidentialAetheryteKind.Gridania;
        }
        return null;
    }

    public static bool IsTeleporterInstalled()
    {
        return Svc.PluginInterface.InstalledPlugins.Any(x => x.InternalName == "TeleporterPlugin" && x.IsLoaded);
    }

    private static bool ReportedIsHereNullEntry = false;
    private static bool ReportedIsHereNullPlayer = false;

    public static bool IsHere(this AddressBookEntry entry)
    {
        // 絕不從這裡擲例外：本方法在通訊錄分頁的每幀繪製路徑上。
        // ECommons 的 ConfigWindow.Draw 用 GenericHelpers.Safe 包住整個主視窗，
        // 例外會被吞下並每幀寫一行 Error（不會觸發 Dalamud 的視窗錯誤畫面），
        // 但堆疊會從這裡一路展開到 Safe()，ImGui 的 BeginTabBar / BeginTabItem /
        // BeginChild 都不會收尾，分頁內容每幀繪製到一半就斷掉。
        if(entry == null)
        {
            if(!ReportedIsHereNullEntry)
            {
                ReportedIsHereNullEntry = true;
                PluginLog.Information("[AddressBook] IsHere 提前結束：通訊錄項目(entry) 為 null，視為「不在此處」。此訊息每次載入外掛只顯示一次。");
            }
            return false;
        }
        var h = HousingManager.Instance();
        if(h == null) return false;
        var player = Player.Object;
        if(player == null)
        {
            if(!ReportedIsHereNullPlayer)
            {
                ReportedIsHereNullPlayer = true;
                PluginLog.Information("[AddressBook] IsHere 提前結束：Player.Object(本機角色) 為 null，通常是登入畫面、角色選擇畫面或正在切換區域。視為「不在此處」。此訊息每次載入外掛只顯示一次。");
            }
            return false;
        }
        if(entry.World != player.CurrentWorld.RowId) return false;
        if(h->GetCurrentWard() != entry.Ward - 1) return false;
        if(Utils.GetResidentialAetheryteByTerritoryType(P.Territory) != entry.City) return false;
        if(entry.PropertyType == PropertyType.House)
        {
            return h->GetCurrentPlot() == entry.Plot - 1;
        }
        if(entry.PropertyType == PropertyType.Apartment)
        {
            if(entry.ApartmentSubdivision && h->GetCurrentDivision() != 2) return false;
            return entry.Apartment == h->GetCurrentRoom();
        }
        return false;
    }

    public static bool IsQuickTravelAvailable(this AddressBookEntry entry)
    {
        if(S.Data.ResidentialAethernet.HousingData.Data.SafeSelect(entry.City.GetResidentialTerritory())?.SafeSelect(entry.Ward - 1)?.AethernetID.EqualsAny(ResidentialAethernet.StartingAetherytes) != false) return false;
        var h = HousingManager.Instance();
        return h != null && entry.City.GetResidentialTerritory() == P.Territory && Player.Available && h->GetCurrentWard() == entry.Ward - 1 && S.Data.ResidentialAethernet.ActiveAetheryte != null && entry.World == Player.Object.CurrentWorld.RowId;
    }

    public static void GoTo(this AddressBookEntry entry)
    {
        if(!Player.Available)
        {
            Notify.Error($"Can not travel while character is not available");
            return;
        }
        if(!S.Data.DataStore.DCWorlds.Contains(ExcelWorldHelper.GetName(entry.World)) && !S.Data.DataStore.Worlds.Contains(ExcelWorldHelper.GetName(entry.World)))
        {
            Notify.Error($"Can not travel to {ExcelWorldHelper.GetName(entry.World)}");
            return;
        }
        if(entry.IsQuickTravelAvailable())
        {
            if(entry.PropertyType == PropertyType.House)
            {
                TaskTpAndGoToWard.EnqueueFromResidentialAetheryte(entry.City, entry.Plot - 1, false, default, false);
            }
            else if(entry.PropertyType == PropertyType.Apartment)
            {
                TaskTpAndGoToWard.EnqueueFromResidentialAetheryte(entry.City, entry.Apartment - 1, true, entry.ApartmentSubdivision, false);
            }
        }
        else
        {
            if(entry.PropertyType == PropertyType.House)
            {
                TaskTpAndGoToWard.Enqueue(ExcelWorldHelper.GetName(entry.World), entry.City, entry.Ward, entry.Plot - 1, false, default);
            }
            else if(entry.PropertyType == PropertyType.Apartment)
            {
                TaskTpAndGoToWard.Enqueue(ExcelWorldHelper.GetName(entry.World), entry.City, entry.Ward, entry.Apartment - 1, true, entry.ApartmentSubdivision);
            }
        }
    }

    public static string FancyDigits(this int n)
    {
        return n.ToString().ReplaceByChar(Lang.Digits.Normal, Lang.Digits.GameFont);
    }

    public static void SaveGeneratedHousingData()
    {
        EzConfig.SaveConfiguration(S.Data.ResidentialAethernet.HousingData, "GeneratedHousingData.json", false);
    }

    public static float CalculatePathDistance(Vector3[] vectors)
    {
        var distance = 0f;
        for(var i = 0; i < vectors.Length - 1; i++)
        {
            distance += Vector3.Distance(vectors[i], vectors[i + 1]);
        }
        return distance;
    }

    public static bool? WaitForScreen() => IsScreenReady();

    public static bool? WaitForScreenFalse() => !IsScreenReady();

    public static bool ResidentialAetheryteEnumSelector(string name, ref ResidentialAetheryteKind refConfigField)
    {
        var ret = false;
        var names = TabAddressBook.ResidentialNames;
        if(ImGui.BeginCombo(name, names.SafeSelect(refConfigField) ?? $"{refConfigField}"))
        {
            var values = Enum.GetValues<ResidentialAetheryteKind>();
            foreach(var x in values)
            {
                var equals = x == refConfigField;
                if(x.RenderIcon(ImGui.CalcTextSize("A").Y)) ImGui.SameLine(0, 1);
                if(ImGui.Selectable(names.SafeSelect(x) ?? $"{x}", equals))
                {
                    ret = true;
                    refConfigField = x;
                }
                if(ImGui.IsWindowAppearing() && equals) ImGui.SetScrollHereY();
            }
            ImGui.EndCombo();
        }
        return ret;
    }

    public static string GetAutoName(this AddressBookEntry entry)
    {
        var builder = new StringBuilder();
        builder.Append(ExcelWorldHelper.GetName(entry.World));
        builder.Append(", ");
        builder.Append(TabAddressBook.ResidentialNames.SafeSelect(entry.City) ?? "???");
        builder.Append(", Ward ");
        builder.Append(entry.Ward);
        if(entry.PropertyType == PropertyType.House)
        {
            builder.Append(", Plot ");
            builder.Append(entry.Plot);
        }
        if(entry.PropertyType == PropertyType.Apartment)
        {
            builder.Append(", Apartment ");
            builder.Append(entry.Apartment);
            if(entry.ApartmentSubdivision)
            {
                builder.Append(" (subdivision)");
            }
        }
        return builder.ToString();
    }

    public static bool RenderIcon(this ResidentialAetheryteKind residentialAetheryte, float? size = null)
    {
        return NuiTools.RenderResidentialIcon(residentialAetheryte.GetResidentialTerritory(), size);
    }

    internal static void TryNotify(string s)
    {
        if(C.EnableNotifications)
        {
            P.NotificationMasterApi.DisplayTrayNotification(P.Name, s);
        }
    }

    internal static string GetDataCenterName(string world)
    {
        return GetDataCenter(world).Name.ToString();
    }

    internal static WorldDCGroupType GetDataCenter(string world)
    {
        return Svc.Data.GetExcelSheet<World>().First(x => x.Name == world).DataCenter.Value;
    }

    internal static int Minutes(this int min)
    {
        return min * 60 * 1000;
    }
    internal static int Seconds(this int sec)
    {
        return sec * 1000;
    }

    internal static bool CanAutoLogin()
    {
        if(Svc.ClientState.IsLoggedIn || Svc.Condition.Any()) return false;
        if(!TryGetAddonByName<AtkUnitBase>("_TitleMenu", out var title) || !IsAddonReady(title)) return false;
        if(title->UldManager.NodeListCount <= 3) return false;

        // 🔴 上界與元素判空是兩件事（見 AtkNodeSafety 的說明）：索引在範圍內時 NodeList[3]
        //    仍然可能是 null，->Color 是對 null 加偏移後讀取，AVE 在 .NET Core 是
        //    corrupted-state exception，try/catch 攔不到。
        //    GetNodeSafe 的上界條件與上面那行等價（index 3 需要 NodeListCount > 3），上界維持不變。
        var titleNode = GetNodeSafe(&title->UldManager, 3);
        if(titleNode == null || titleNode->Color.A != 0xFF) return false;

        return !TryGetAddonByName<AtkUnitBase>("TitleDCWorldMap", out _)
            && !TryGetAddonByName<AtkUnitBase>("TitleConnect", out _);
    }

    internal static Dictionary<WorldChangeAetheryte, uint> WCATerritories = new()
    {
        [WorldChangeAetheryte.Uldah] = 130,
        [WorldChangeAetheryte.Limsa] = 129,
        [WorldChangeAetheryte.Gridania] = 132
    };

    internal static bool TryGetCharacterIndex(string name, uint world, out int index)
    {
        index = GetCharacterNames().IndexOf((name, (ushort)world));
        return index >= 0;
    }

    internal static List<CharaData> GetCharacterNames()
    {
        List<CharaData> ret = [];
        /*var data = CSFramework.Instance()->UIModule->GetRaptureAtkModule()->AtkModule.GetStringArrayData(1);
        if (data != null)
        {
            for (int i = 60; i < data->AtkArrayData.Size; i++)
            {
                if (data->StringArray[i] == null) break;
                var item = data->StringArray[i];
                if (item != null)
                {
                    var str = MemoryHelper.ReadSeStringNullTerminated((nint)item).GetText();
                    if (str == "") break;
                    ret.Add(str);
                }
            }
        }*/
        var agent = AgentLobby.Instance();
        // AgentLobby 取得器合法回 null(角色選擇畫面之外、或 UIModule 尚未建立)。
        // 拿不到就回空清單:呼叫端 TryGetCharacterIndex 會得到 index < 0 → 判定「找不到角色」,
        // 流程停在原地重試,不會拿 null 去解參考 LobbyData。
        if(agent == null) return ret;
        //if (agent->AgentInterface.IsAgentActive())
        {
            var charaSpan = agent->LobbyData.CharaSelectEntries.AsSpan();
            for(var i = 0; i < charaSpan.Length; i++)
            {
                var s = charaSpan[i];
                // 🔴 元素本身是可為 null 的指標(項目還沒填完就是 null),直接 s.Value->Name 是 AccessViolation。
                // 🔴 但這裡不能用 continue 跳過:本清單的「索引」會被 DCChange.SelectCharacter 原封不動
                // 當成 _CharaSelectListMenu 的角色索引送出去(Callback.Fire(addon, false, 29, 0, index)),
                // 少一項就讓後面每個角色的索引往前位移一格 ⇒ 失敗形式是「登入到錯的角色」,比崩潰更糟。
                // 填一個永遠不會被 IndexOf 命中的佔位項(空名字＋世界 0)來保住位置對齊。
                if(s.Value == null)
                {
                    ret.Add(("", (ushort)0));
                    continue;
                }
                ret.Add(($"{s.Value->Name.Read()}", s.Value->HomeWorldId));
            }
        }
        return ret;
    }

    internal static string GetInnNameFromTerritory(uint tt)
    {
        if(tt == 0) return "Autodetect";
        if(Svc.Data.GetExcelSheet<TerritoryType>().TryGetRow(tt, out var t))
        {
            var inn = Svc.Data.GetExcelSheet<TerritoryType>().FirstOrNull(x => x.PlaceNameRegion.ValueNullable?.RowId == t.PlaceNameRegion.Value.RowId && x.TerritoryIntendedUse.RowId == (int)TerritoryIntendedUseEnum.Inn);
            if(inn != null)
            {
                return inn.Value.PlaceNameZone.Value.Name.ToString();
            }
        }
        return "???";
    }

    internal static IGameObject GetReachableMasterAetheryte(bool littleDistance = false) => GetReachableAetheryte(x => Utils.TryGetTinyAetheryteFromIGameObject(x, out var ae) && ae.Value.IsAetheryte, littleDistance);

    internal static IGameObject GetReachableWorldChangeAetheryte(bool littleDistance = false) => GetReachableAetheryte(x => Utils.TryGetTinyAetheryteFromIGameObject(x, out var ae) && ae?.IsWorldChangeAetheryte() == true, littleDistance);

    internal static IGameObject GetReachableResidentialAetheryte(bool littleDistance = false) => GetReachableAetheryte(x => Utils.TryGetTinyAetheryteFromIGameObject(x, out var ae) && ae?.IsResidentialAetheryte() == true, littleDistance);

    internal static IGameObject GetReachableAetheryte(Predicate<IGameObject> predicate, bool littleDistance = false)
    {
        if(!Player.Available) return null;
        var a = Svc.Objects.OrderBy(x => Vector3.DistanceSquared(Player.Object.Position, x.Position)).FirstOrDefault(x => predicate(x));
        if(a != null && a.IsTargetable && Vector3.Distance(a.Position, Player.Object.Position) < (littleDistance ? 13f : 30f))
        {
            return a;
        }
        return null;
    }

    /// <summary>
    /// 「值得嘗試鎖定+自動移動走過去用」的距離上限。刻意比 <see cref="GetReachableAetheryte"/>
    /// 預設的 30y 寬:那個門檻是給「已經在互動範圍附近,頂多再修正幾步」的情境用的,
    /// 而這裡是「要不要為了省一次傳送,花最多 20 秒鎖定+自動移動走過去」——使用者實測回報過
    /// 「有一小段距離的也不會嘗試,而是直接傳送」,30y 對這個用途太保守。50y 是一般可鎖定
    /// 目標的距離量級,而走位本身有 20 秒時限兜底(見 TaskGotoDestination.EnqueueAethernetRoute 的
    /// WaitArriveAtNetworkNode),逾時就退回傳送,不會有「卡住」的風險。
    /// </summary>
    private const float AethernetNetworkNodeApproachRange = 50f;

    /// <summary>
    /// 找出「屬於指定以太之光網路」且摸得到的節點——主水晶本身,或它底下任一個子節點(城內以太之光)。
    /// 用途:2.0 的城市各有兩張地圖(利姆薩上/下層甲板、烏爾達哈娜爾神殿/太陽神草原、格里達尼亞
    /// 新/舊街),兩張圖屬於同一個以太之光網路,但只有其中一張圖上有主水晶本體。人站在沒有主水晶
    /// 的那張圖時,<see cref="GetReachableMasterAetheryte"/> 永遠摸不到東西;這個方法改成只要是
    /// 同一個網路的任何節點都算,身邊摸得到就能直接互動、不必先傳送到另一張圖的主水晶。
    /// </summary>
    /// <param name="root">要找哪一個乙太網的節點。</param>
    /// <param name="excludeId">
    /// 要排除的節點 ID,通常是「這趟的目的地本身」。用目的地自己開選單再選自己會被遊戲以
    /// LogMessage 1478「此處為目前所在地。」拒絕,所以找可走節點時要把它排掉,讓流程退回原本的
    /// 「傳送到主水晶再走乙太網」——那正是修改前的行為。
    /// </param>
    internal static IGameObject GetReachableAethernetNetworkNode(TinyAetheryte root, uint? excludeId = null)
    {
        if(!Player.Available) return null;
        if(!S.Data.DataStore.Aetherytes.TryGetValue(root, out var children)) return null;
        var a = Svc.Objects.OrderBy(x => Vector3.DistanceSquared(Player.Object.Position, x.Position))
            .FirstOrDefault(x => Utils.TryGetTinyAetheryteFromIGameObjectLenient(x, out var ae)
                && ae.Value.ID != excludeId
                && (ae.Value.ID == root.ID || children.Any(c => c.ID == ae.Value.ID)));
        if(a != null && a.IsTargetable && Vector3.Distance(a.Position, Player.Object.Position) < AethernetNetworkNodeApproachRange)
        {
            return a;
        }
        return null;
    }

    /// <summary>
    /// 找出「指定的那一座乙太之光」在物件表裡的實體。找不到代表它沒載入(太遠,或根本不在這一區),
    /// 呼叫端必須要有退路。判定用寬鬆版,理由見 <see cref="TryGetTinyAetheryteFromIGameObjectLenient"/>。
    ///
    /// 🔴 回傳的 <see cref="IGameObject"/> **只在當下這一幀有效**,絕對不要跨幀保存 —— 呼叫端一律
    /// 每次重查(<c>Address</c> 在物件建構時就凍結,不會重新解析)。
    /// </summary>
    internal static IGameObject GetAethernetNodeObject(TinyAetheryte node)
    {
        if(!Player.Available) return null;
        if(node.TerritoryType != P.Territory) return null;
        return Svc.Objects.FirstOrDefault(x => Utils.TryGetTinyAetheryteFromIGameObjectLenient(x, out var ae) && ae.Value.ID == node.ID);
    }

    /// <summary>
    /// 目前 <see cref="Lifestream.ActiveAetheryte"/>(每幀依 <see cref="GetValidAetheryte"/> 更新,
    /// 只在真的站到節點旁邊才會設值)是否已經是指定以太之光網路裡的節點——主水晶本身或任一子節點。
    /// 用來判斷「走到能互動的節點了嗎」,不管走到的是主水晶還是網路裡的哪一個城內以太之光。
    /// </summary>
    /// <param name="root">要比對哪一個乙太網。</param>
    /// <param name="excludeId">
    /// 與 <see cref="GetReachableAethernetNetworkNode"/> 的同名參數同一個理由:走位途中剛好經過目的地
    /// 本身時,不要把它當成「已抵達可用節點」而拿它開選單(選自己會被拒絕)。
    /// </param>
    internal static bool IsActiveAetheryteInNetwork(TinyAetheryte root, uint? excludeId = null)
    {
        if(P.ActiveAetheryte == null) return false;
        if(P.ActiveAetheryte.Value.ID == excludeId) return false;
        if(P.ActiveAetheryte.Value.ID == root.ID) return true;
        return S.Data.DataStore.Aetherytes.TryGetValue(root, out var children) && children.Any(c => c.ID == P.ActiveAetheryte.Value.ID);
    }

    internal static bool IsDisallowedToChangeWorld()
    {
        return Svc.Condition[ConditionFlag.WaitingToVisitOtherWorld]
            || Svc.Condition[ConditionFlag.ReadyingVisitOtherWorld]
            || Svc.Condition[ConditionFlag.InDutyQueue]
            || Svc.Condition[ConditionFlag.BoundByDuty95]
            || Svc.Party.Length > 0
            ;
    }

    internal static bool IsDisallowedToUseAethernet()
    {
        return Svc.Condition[ConditionFlag.WaitingToVisitOtherWorld] || Svc.Condition[ConditionFlag.Jumping];
    }

    internal static string[] DefaultAddons = [
        "Inventory",
        "InventoryLarge",
        "AreaMap",
        "InventoryRetainerLarge",
        "Currency",
        "Bank",
        "RetainerTask",
        "RetainerList",
        "SelectYesNo",
        "SelectString",
        "SystemMenu",
        "MountNoteBook",
        "FriendList",
        "BlackList",
        "Character",
        "ItemSearch",
        "MonsterNote",
        "GatheringNote",
        "RecipeNote",
        "ContentsFinder",
        "LookingForGroup",
        "Journal",
        "ContentsInfo",
        "SocialList",
        "Macro",
        "ConfigKeybind",
        "GSInfoGeneral",
        "ContentsInfo",
        "PartyMemberList",
        "ContentsFinderConfirm",
        "SelectString"
    ];
    internal static bool IsAddonsVisible(IEnumerable<string> addons)
    {
        foreach(var x in addons)
        {
            if(TryGetAddonByName<AtkUnitBase>(x, out var a) && a->IsVisible) return true;
        }
        return false;
    }
    internal static bool IsMapActive()
    {
        if(TryGetAddonByName<AtkUnitBase>("AreaMap", out var map) && IsAddonReady(map) && map->IsVisible)
        {
            return true;
        }
        return false;
    }

    internal static AetheryteUseState CanUseAetheryte()
    {
        if(P.TaskManager.IsBusy || IsOccupied() || IsDisallowedToUseAethernet()) return AetheryteUseState.None;
        if(S.Data.DataStore.Territories.Contains(P.Territory) && P.ActiveAetheryte != null) return AetheryteUseState.Normal;
        if(S.Data.ResidentialAethernet.IsInResidentialZone() && S.Data.ResidentialAethernet.ActiveAetheryte != null) return AetheryteUseState.Residential;
        if(S.Data.CustomAethernet.ZoneInfo.ContainsKey(P.Territory) && S.Data.CustomAethernet.ActiveAetheryte != null) return AetheryteUseState.Custom;
        return AetheryteUseState.None;
    }

    internal static TinyAetheryte GetMaster()
    {
        return P.ActiveAetheryte.Value.GetMaster();
    }

    internal static TinyAetheryte GetMaster(this TinyAetheryte a)
    {
        return a.IsAetheryte ? a : S.Data.DataStore.GetMaster(a);
    }

    internal static bool IsWorldChangeAetheryte(this TinyAetheryte t)
    {
        return t.ID.EqualsAny<uint>(2, 8, 9);
    }

    internal static bool IsResidentialAetheryte(this TinyAetheryte t)
    {
        return t.ID.EqualsAny<uint>(2, 8, 9, 70, 111);
    }

    private static Dictionary<ResidentialAetheryteKind, uint> TerritoryForResidentialAetheryte = new()
    {
        [ResidentialAetheryteKind.Uldah] = MainCities.Uldah_Steps_of_Nald,
        [ResidentialAetheryteKind.Gridania] = MainCities.New_Gridania,
        [ResidentialAetheryteKind.Limsa] = MainCities.Limsa_Lominsa_Lower_Decks,
        [ResidentialAetheryteKind.Kugane] = MainCities.Kugane,
        [ResidentialAetheryteKind.Foundation] = MainCities.Foundation,
    };
    private static Dictionary<ResidentialAetheryteKind, uint> ResidentialTerritoryForResidentialAetheryte = new()
    {
        [ResidentialAetheryteKind.Uldah] = ResidentalAreas.The_Goblet,
        [ResidentialAetheryteKind.Gridania] = ResidentalAreas.The_Lavender_Beds,
        [ResidentialAetheryteKind.Limsa] = ResidentalAreas.Mist,
        [ResidentialAetheryteKind.Kugane] = ResidentalAreas.Shirogane,
        [ResidentialAetheryteKind.Foundation] = ResidentalAreas.Empyreum,
    };
    private static Dictionary<WorldChangeAetheryte, uint> TerritoryForWorldChangeAetheryte = new()
    {
        [WorldChangeAetheryte.Uldah] = MainCities.Uldah_Steps_of_Nald,
        [WorldChangeAetheryte.Gridania] = MainCities.New_Gridania,
        [WorldChangeAetheryte.Limsa] = MainCities.Limsa_Lominsa_Lower_Decks,
    };

    /// <summary>
    /// 
    /// </summary>
    /// <param name="r"></param>
    /// <returns>Aetheryte city (Limsa, Uldah, etc)</returns>
    internal static uint GetTerritory(this ResidentialAetheryteKind r)
    {
        return TerritoryForResidentialAetheryte[r];
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="r"></param>
    /// <returns>Residential TerritoryType id (mist, goblet, etc)</returns>
    internal static uint GetResidentialTerritory(this ResidentialAetheryteKind r)
    {
        return ResidentialTerritoryForResidentialAetheryte[r];
    }
    internal static string GetName(this ResidentialAetheryteKind r)
    {
        return ExcelTerritoryHelper.Get(r.GetResidentialTerritory())?.PlaceName.ValueNullable?.Name.ToString();
    }

    internal static uint GetTerritory(this WorldChangeAetheryte r)
    {
        return TerritoryForWorldChangeAetheryte[r];
    }

    internal static ResidentialAetheryteKind? GetResidentialAetheryteByTerritoryType(uint territoryType)
    {
        var t = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryType);
        if(t == null) return null;
        if(t.Value.PlaceNameRegion.RowId == 2402) return ResidentialAetheryteKind.Kugane;
        if(t.Value.PlaceNameRegion.RowId == 25) return ResidentialAetheryteKind.Foundation;
        if(t.Value.PlaceNameRegion.RowId == 23) return ResidentialAetheryteKind.Gridania;
        if(t.Value.PlaceNameRegion.RowId == 24) return ResidentialAetheryteKind.Uldah;
        if(t.Value.PlaceNameRegion.RowId == 22) return ResidentialAetheryteKind.Limsa;
        return null;
    }

    internal static WorldChangeAetheryte? GetWorldChangeAetheryteByTerritoryType(uint territoryType)
    {
        var c = TerritoryForWorldChangeAetheryte.FindKeysByValue(territoryType);
        return c.Any() ? c.First() : null;
    }

    internal static bool TryGetTinyAetheryteFromIGameObject(IGameObject a, out TinyAetheryte? t, uint? TerritoryType = null)
        => TryGetTinyAetheryteFromIGameObjectCore(a, false, out t, TerritoryType);

    /// <summary>
    /// 跟 <see cref="TryGetTinyAetheryteFromIGameObject"/> 完全相同,只差在「什麼算乙太之光物件」的判定
    /// 放寬成 <see cref="IsAetheryte(IGameObject)"/>:<see cref="ObjectKind.Aetheryte"/> 算,
    /// DataId 落在 <see cref="AethernetShards"/> 裡的 EventObj 也算。
    ///
    /// 🔴 為什麼一定要有這一版:嚴格版硬性要求 <c>ObjectKind == ObjectKind.Aetheryte</c>,
    /// 但 <see cref="IsAetheryte(IGameObject)"/> 這個擴充方法存在的唯一理由,就是「有些乙太之光在物件表裡
    /// 不是這個 ObjectKind,只能靠 DataId 認出來」——而每幀決定 <see cref="Lifestream.ActiveAetheryte"/> 的
    /// <see cref="GetValidAetheryte"/>(實機一直在用、已驗證可行)用的正是寬鬆版。
    /// 拿嚴格版去找「身邊摸得到的同網路節點」,在那些城市會永遠回 null,而**失敗形式是完全靜默地退回傳送**
    /// ——跟這個最佳化不存在時一模一樣,使用者只會看到「說改了卻沒改」。
    ///
    /// ⚠️ 嚴格版刻意維持原樣:它被用在世界移動/住宅區/主水晶身分比對等流程,放寬會連帶改變那些流程的行為。
    /// </summary>
    internal static bool TryGetTinyAetheryteFromIGameObjectLenient(IGameObject a, out TinyAetheryte? t, uint? TerritoryType = null)
        => TryGetTinyAetheryteFromIGameObjectCore(a, true, out t, TerritoryType);

    private static bool TryGetTinyAetheryteFromIGameObjectCore(IGameObject a, bool lenientObjectKind, out TinyAetheryte? t, uint? TerritoryType)
    {
        TerritoryType ??= P.Territory;
        if(a == null)
        {
            t = default;
            return false;
        }
        if(lenientObjectKind ? a.IsAetheryte() : a.ObjectKind == ObjectKind.Aetheryte)
        {
            var pos2 = a.Position.ToVector2();
            foreach(var x in S.Data.DataStore.Aetherytes)
            {
                if(x.Key.TerritoryType == TerritoryType && Vector2.Distance(x.Key.Position, pos2) < 10)
                {
                    t = x.Key;
                    return true;
                }
                foreach(var l in x.Value)
                {
                    if(l.TerritoryType == TerritoryType && Vector2.Distance(l.Position, pos2) < 10)
                    {
                        t = l;
                        return true;
                    }
                }
            }
        }
        t = default;
        return false;
    }

    internal static float ConvertMapMarkerToMapCoordinate(int pos, float scale)
    {
        var num = scale / 100f;
        var rawPosition = (int)((float)(pos - 1024.0) / num * 1000f);
        return ConvertRawPositionToMapCoordinate(rawPosition, scale);
    }

    internal static float ConvertMapMarkerToRawPosition(int pos, float scale)
    {
        var num = scale / 100f;
        var rawPosition = ((float)(pos - 1024.0) / num);
        return rawPosition;
    }

    internal static float ConvertRawPositionToMapCoordinate(int pos, float scale)
    {
        var num = scale / 100f;
        return (float)((pos / 1000f * num + 1024.0) / 2048.0 * 41.0 / num + 1.0);
    }

    internal static AtkUnitBase* GetSpecificYesno(params string[] s) => GetSpecificYesno(false, s);

    internal static AtkUnitBase* GetSpecificYesno(bool contains, params string[] s)
    {
        for(var i = 1; i < 100; i++)
        {
            try
            {
                var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("SelectYesno", i).Address;
                if(addon == null) return null;
                if(IsAddonReady(addon))
                {
                    // 讀不到就跳過這個 SelectYesno 繼續往下掃(fail-closed):
                    // 不能拿空字串去比對,否則「讀不到」會被誤判成「內容為空的相符」而按下確認。
                    if(!TryGetNodeText(addon, 15, out var rawText)) continue;
                    // 讀到 U+FFFD ＝ 這扇窗的記憶體正在變動(多半是關閉中),這一幀當作沒比中,不要按它。
                    if(AddonPressGuard.IsTextUnstable("SelectYesno", rawText)) continue;
                    var text = rawText.Replace(" ", "");
                    if(contains ?
                        text.ContainsAny(s.Select(x => x.Replace(" ", "")))
                        : text.EqualsAny(s.Select(x => x.Replace(" ", "")))
                        )
                    {
                        PluginLog.Verbose($"SelectYesno {s.Print()} addon {i}");
                        return addon;
                    }
                }
            }
            catch(Exception e)
            {
                e.Log();
                return null;
            }
        }
        return null;
    }

    // 登出確認視窗有兩種文字:一般情況是 Addon#115,正在排隊等待副本時遊戲會換成 Addon#17531。
    // 上游 0726a4fc 用 GetRow(17531) 直接取第二組,但台服 7.20 的 Addon 表沒有這一列
    // (離線實查 exd-tc/7.20/Addon.csv:共 14850 列、最大 row id 102700,17531 前後最近的兩列是 17200 與 101000),
    // 而 Lumina 的 GetRow(uint) 對不存在的列會擲 ArgumentOutOfRangeException
    // (Lumina/src/Lumina/Excel/ExcelSheet.cs 的 GetRow:Unsafe.IsNullRef 時 throw)。
    // 每次輪詢都會走到這裡,原樣合會在每個登出流程持續擲例外,所以一律改用 TryGetRow:
    // 取不到就只留 115 那一組,判定與合併這顆之前逐字相同(而不是變成靜默不登出)。
    // 台服拿不到 17531 是常態不是錯誤,因此結果快取起來,診斷只在第一次解析時寫一行 Information。
    private const uint LogOutYesnoAddonRow = 115;
    private const uint LogOutYesnoQueuedForDutyAddonRow = 17531;
    private static string[] LogOutYesnoTextsCache;

    internal static AtkUnitBase* GetLogOutYesno()
    {
        var texts = GetLogOutYesnoTexts();
        // 兩列都取不到才會是空的;此時不要拿空陣列去比對,否則等於「任何視窗都不相符」以外還可能誤按。
        if(texts.Length == 0) return null;
        return GetSpecificYesno(texts);
    }

    private static string[] GetLogOutYesnoTexts()
    {
        if(LogOutYesnoTextsCache != null) return LogOutYesnoTextsCache;
        var sheet = Svc.Data.GetExcelSheet<Addon>();
        var texts = new List<string>();
        var hasNormal = sheet.TryGetRow(LogOutYesnoAddonRow, out var normal);
        if(hasNormal) texts.Add(normal.Text.GetText());
        var hasQueued = sheet.TryGetRow(LogOutYesnoQueuedForDutyAddonRow, out var queued);
        if(hasQueued) texts.Add(queued.Text.GetText());
        LogOutYesnoTextsCache = [.. texts];
        PluginLog.Information($"[Lifestream] 登出確認視窗文字解析完成:Addon#{LogOutYesnoAddonRow}={(hasNormal ? "有" : "無")}、" +
            $"Addon#{LogOutYesnoQueuedForDutyAddonRow}={(hasQueued ? "有" : "無(台服常態,排隊等副本時的登出沿用一般判定)")};" +
            $"共 {LogOutYesnoTextsCache.Length} 組候選文字。");
        return LogOutYesnoTextsCache;
    }

    internal static string[] GetAvailableWorldDestinations()
    {
        if(TryGetAddonByName<AtkUnitBase>("WorldTravelSelect", out var addon) && IsAddonReady(addon))
        {
            List<string> arr = [];
            for(var i = 3; i <= 9; i++)
            {
                // 🔴 原本是六跳裸鏈。取不到就 break ＝ 與既有的「空字串代表清單到底了」同一條路徑,
                // 回傳截短的清單(fail-closed:少列出目的地只會讓人手動處理,列出錯的才會傳錯地方)。
                var item = GetComponentNodeSafe(addon, 4, i);
                if(!TryGetNodeText(GetComponentNodeSafe(item, 4), out var text)) break;
                if(text == "") break;
                arr.Add(text);
            }
            return [.. arr];
        }
        return Array.Empty<string>();
    }

    internal static string[] GetAvailableAethernetDestinations()
    {
        if(TryGetAddonByName<AtkUnitBase>("TelepotTown", out var addon) && IsAddonReady(addon))
        {
            List<string> arr = [];
            for(var i = 1; i <= 52; i++)
            {
                // 🔴 同上的六跳裸鏈;這裡的迴圈上限 52 也完全沒對 NodeListCount 驗過 ——
                // GetComponentNodeSafe 會把上界與元素判空一起做掉。
                var item = GetComponentNodeSafe(addon, 16, i);
                if(!TryGetNodeText(GetComponentNodeSafe(item, 3), out var rawText)) break;
                var text = rawText.Trim();
                if(text == "") break;
                arr.Add(text);
            }
            return [.. arr];
        }
        return Array.Empty<string>();
    }

    internal static IGameObject GetValidAetheryte()
    {
        foreach(var x in Svc.Objects)
        {
            if(x.IsAetheryte())
            {
                var d2d = Vector2.Distance(Svc.Objects.LocalPlayer.Position.ToVector2(), x.Position.ToVector2());
                var d3d = Vector3.Distance(Svc.Objects.LocalPlayer.Position, x.Position);
                if(S.Data.ResidentialAethernet.IsInResidentialZone() && d3d > 4.6f) continue;
                if(S.Data.CustomAethernet.ZoneInfo.TryGetValue(P.Territory, out var zinfo) && d3d > zinfo.MaxInteractionDistance) continue;

                if(d2d < 11f
                    && d3d < 15f
                    && x.IsVPosValid()
                    && x.IsTargetable)
                {
                    return x;
                }
            }
        }
        return null;
    }

    public static bool IsAetheryte(this IGameObject obj)
    {
        if(obj.ObjectKind == ObjectKind.Aetheryte) return true;
        return Utils.AethernetShards.Contains(obj.BaseId);
    }

    internal static bool IsVPosValid(this IGameObject x)
    {
        /*if(x.Name.ToString() == Lang.AethernetShard)
        {
            return MathF.Abs(Player.Object.Position.Y - x.Position.Y) < 0.965;
        }*/
        return true;
    }

    internal static bool TrySelectSpecificEntry(string text, Func<bool> Throttle)
    {
        return TrySelectSpecificEntry(new string[] { text }, Throttle);
    }

    internal static bool TrySelectSpecificEntry(IEnumerable<string> text, Func<bool> Throttle)
    {
        if(TryGetAddonByName<AddonSelectString>("SelectString", out var addon) && IsAddonReady(&addon->AtkUnitBase))
        {
            var entries = GetEntries(addon);
            // 讀到 U+FFFD ＝ 選單記憶體正在變動(多半是上一層選單關閉中),這一幀不碰。
            if(AddonPressGuard.AnyTextUnstable("SelectString", entries)) return false;
            var entry = entries.FirstOrDefault(x => x.EqualsAny(text));
            if(entry != null)
            {
                var index = entries.IndexOf(entry);
                // SelectString 全外掛唯一的 choke point(14 個呼叫端)。粒度=(窗,位址,索引):同一扇仍開著的選單
                // 選不同項目照常放行,只擋「同位址同索引在窗走完前再選」。被擋回 false 與「還沒找到」走同一條路。
                if(index >= 0 && Throttle() && AddonPressGuard.TryPressOnce("SelectString", addon, "TrySelectSpecificEntry", paramKey: index.ToString()))
                {
                    new AddonMaster.SelectString(addon).Entries[index].Select();
                    PluginLog.Debug($"TrySelectSpecificEntry: selecting {entry}/{index} as requested by {text.Print()}");
                    return true;
                }
            }
        }
        return false;
    }

    internal static List<string> GetEntries(AddonSelectString* addon)
    {
        var list = new List<string>();
        for(var i = 0; i < addon->PopupMenu.PopupMenu.EntryCount; i++)
        {
            list.Add(MemoryHelper.ReadSeStringNullTerminated((nint)addon->PopupMenu.PopupMenu.EntryNames[i].Value).GetText().Trim());
        }
        //PluginLog.Debug($"{list.Print()}");
        return list;
    }

    internal static int GetServiceAccount(string name, uint world) => GetServiceAccount($"{name}@{ExcelWorldHelper.GetName(world)}");

    internal static int GetServiceAccount(string nameWithWorld)
    {
        if(P.AutoRetainerApi?.Ready == true && C.UseAutoRetainerAccounts)
        {
            var chars = P.AutoRetainerApi.GetRegisteredCharacters();
            foreach(var c in chars)
            {
                var data = P.AutoRetainerApi.GetOfflineCharacterData(c);
                if(data != null)
                {
                    var name = $"{data.Name}@{data.World}";
                    if(nameWithWorld == name && data.ServiceAccount > -1)
                    {
                        return data.ServiceAccount;
                    }
                }
            }
        }
        if(C.ServiceAccounts.TryGetValue(nameWithWorld, out var ret))
        {
            if(ret > -1) return ret;
        }
        return 0;
    }

    internal static void CheckConfigMigration()
    {
        // int ButtonWidth -> int[3] ButtonWidthArray
        if(C.ButtonWidthArray is null) MigrateConfigButtonWidthToButtonWidthArray();
        EzConfig.Save();
    }

    internal static void MigrateConfigButtonWidthToButtonWidthArray()
    {
        C.ButtonWidthArray = [C.ButtonWidth, C.ButtonWidth, C.ButtonWidth];
    }

    public static bool IsLoggingOutInstant(uint territoryType)
    {
        if(Svc.Data.GetExcelSheet<TerritoryType>().TryGetRow(territoryType, out var sheet))
        {
            return sheet.TerritoryIntendedUse.Value.DisableLogoutTimer;
        }

        return false;
    }
}
