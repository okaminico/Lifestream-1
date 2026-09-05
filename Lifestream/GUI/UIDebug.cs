using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Utility;
using ECommons.Automation;
using ECommons.Automation.UIInput;
using ECommons.Configuration;
using Callback = ECommons.Automation.Callback;
using ECommons.ExcelServices;
using ECommons.EzSharedDataManager;
using ECommons.GameHelpers;
using ECommons.LanguageHelpers;
using ECommons.MathHelpers;
using ECommons.Reflection;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lifestream.AtkReaders;
using Lifestream.Data;
using Lifestream.Enums;
using Lifestream.Schedulers;
using Lifestream.Systems.Legacy;
using Lifestream.Systems.Residential;
using Lifestream.Tasks;
using Lifestream.Tasks.CrossDC;
using Lifestream.Tasks.Utility;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using NightmareUI.ImGuiElements;
using Path = System.IO.Path;

namespace Lifestream.GUI;

internal static unsafe class UIDebug
{
    internal static uint DebugTerritory = 0;
    internal static TinyAetheryte? DebugAetheryte = null;
    internal static int DC = 0;
    internal static int Destination = 0;
    internal static List<Vector3> DebugPath = [];
    internal static void Draw()
    {
        ImGuiEx.EzTabBar("debug",
            InternalLog.ImGuiTab(),
            ("Data editor".Loc(), Editor, null, true),
            ("Housing data".Loc(), Housing, null, true),
            ("AtkReader", Reader, null, true),
            ("Debug".Loc(), Debug, null, true),
            ("Multipath".Loc(), TabMultipath.Draw, null, true)
            );
    }

    private static int Resize = 60;
    private static int LastPlot = -1;
    private static bool doCurPlot = false;
    private static bool ShowPathes = false;
    private static bool ShowFirstPoint = true;
    private static int u;
    private static int v;
    private static List<Vector3> CurrentPath = null;
    private static bool DoAutotest = false;
    private static int AutotestPlot = 0;
    private static int AutotestWard = 30;
    private static ResidentialAetheryteKind AutotestKind = ResidentialAetheryteKind.Gridania;
    private static List<long> StuckRecords = [];
    private static Vector3? LastPosition = null;

    private static void Housing()
    {
        if(CurrentPath != null)
        {
            if(ImGui.Begin($"Lifestream {"Edit Path".Loc()}"))
            {
                if(ImGui.Button("Finish".Loc()))
                {
                    Svc.Framework.RunOnFrameworkThread(() =>
                    {
                        Utils.SaveGeneratedHousingData();
                    });
                    CurrentPath = null;
                }
                if(CurrentPath != null)
                {
                    UIHouseReg.DrawPathEditor(CurrentPath);
                }
            }
            ImGui.End();
        }
        if(ImGui.Button("Load from config folder".Loc()))
        {
            var d = EzConfig.LoadConfiguration<HousingData>("GeneratedHousingData.json", true);
            if(d != null) S.Data.ResidentialAethernet.HousingData = d;
        }
        var data = S.Data.ResidentialAethernet.HousingData.Data;
        if(ImGui.CollapsingHeader("Autotest".Loc()))
        {
            if(DoAutotest)
            {
                if(Utils.IsBusy())
                {
                    EzThrottler.Throttle("Autotest", 500, true);
                }
                else
                {
                    if(AutotestPlot >= 60)
                    {
                        DuoLog.Information("Autotest complete".Loc());
                        DoAutotest = false;
                    }
                    else if(EzThrottler.Throttle("Autotest"))
                    {
                        AutotestPlot++;
                        DuoLog.Information($"{"Now going to plot".Loc()} {AutotestPlot}");
                        TaskTpAndGoToWard.Enqueue(Player.CurrentWorld, AutotestKind, AutotestWard, AutotestPlot - 1, false, false);
                    }
                }
            }
            ImGui.Checkbox("Autotest active".Loc(), ref DoAutotest);
            ImGuiEx.EnumCombo("Autotest aetheryte".Loc(), ref AutotestKind);
            ImGui.InputInt("Autotest ward".Loc(), ref AutotestWard);
            ImGui.InputInt("Autotest current plot".Loc(), ref AutotestPlot);
            if(DoAutotest && EzThrottler.Throttle("StuckAutocheck", 1000))
            {
                if(P.FollowPath.Waypoints.Count > 0)
                {
                    if(LastPosition != null && Vector3.DistanceSquared(LastPosition.Value, Player.Position) < 1)
                    {
                        StuckRecords.Add(Environment.TickCount64);
                        StuckRecords.RemoveAll(x => Environment.TickCount64 - x > 10000);
                        if(StuckRecords.Count > 1)
                        {
                            DuoLog.Information($"{"Stuck at".Loc()} {AutotestPlot} - {AutotestKind}");
                            DoAutotest = false;
                            P.FollowPath.Stop();
                            Utils.TryNotify("Stuck".Loc());
                        }
                    }
                    LastPosition = Player.Position;
                }
                else
                {
                    LastPosition = null;
                }
            }
        }
        if(data.TryGetValue(P.Territory, out var plots))
        {
            if(ImGui.CollapsingHeader("Control".Loc()))
            {
                ImGui.Checkbox($"{"Show pathes".Loc()}", ref ShowPathes);
                ImGui.SameLine();
                ImGui.Checkbox("Show first point".Loc(), ref ShowFirstPoint);
                if(ShowPathes)
                {
                    var aetheryte = S.Data.ResidentialAethernet.ActiveAetheryte ?? S.Data.ResidentialAethernet.GetFromIGameObject(Svc.Targets.Target);
                    if(aetheryte != null)
                    {
                        foreach(var x in plots)
                        {
                            if(x.AethernetID == aetheryte.Value.ID && x.Path.Count > 0)
                            {
                                S.Ipc.SplatoonManager.RenderPath(ShowFirstPoint ? x.Path : x.Path[1..]);
                            }
                        }
                    }
                }
                // HousingManager.Instance() 是 [StaticAddress(..., isPointer: true)],不在住宅區/登入前是 null。
                // 同 repo 其餘六處全都判過空(Utils.cs:943/963、UIHouseReg.cs:497、TabAddressBook.cs:69、
                // 同檔 :749),只有這裡漏掉 —— 是個例不是慣例,所以照既有形狀補上。
                var housingManager = HousingManager.Instance();
                if(housingManager == null)
                {
                    // 「讀不到」和「不在地皮上」是兩件事:後者 GetCurrentPlot() 回 -1 會顯示成 Plot: 0,
                    // 這裡不沿用那個顯示,免得把未知畫成一個看起來像數值的東西。LastPlot 也維持前值不動。
                    ImGuiEx.Text("Plot: (housing manager unavailable)".Loc());
                }
                else
                {
                    var curPlot = housingManager->GetCurrentPlot();
                    if(curPlot != -1) LastPlot = curPlot;
                    ImGuiEx.Text($"{"Plot:".Loc()}{curPlot + 1}");
                }
                ImGui.SetNextItemWidth(150f.Scale());
                ImGui.InputInt($"{"Resize".Loc()}", ref Resize);
                ImGui.SameLine();
                if(ImGui.Button("Resize arrays".Loc()))
                {
                    while(plots.Count > Resize) plots.RemoveAt(plots.Count - 1);
                    while(plots.Count < Resize) plots.Add(new());
                }
                if(ImGui.Button("Begin path calculation".Loc()))
                {
                    Chat.ExecuteCommand("/clearlog");
                    var aetheryte = S.Data.ResidentialAethernet.ActiveAetheryte ?? S.Data.ResidentialAethernet.GetFromIGameObject(Svc.Targets.Target);
                    if(aetheryte != null)
                    {
                        P.TaskManager.Enqueue(() => S.Ipc.VnavmeshIPC.Rebuild());
                        P.TaskManager.Enqueue(() => S.Ipc.VnavmeshIPC.IsReady(), TaskSettings.TimeoutInfinite);
                        for(var i = 0; i < plots.Count; i++)
                        {
                            var x = plots[i];
                            if(x.AethernetID == aetheryte.Value.ID)
                            {
                                var index = i;
                                TaskGeneratePath.Enqueue(i, x);
                            }
                        }
                        for(var i = 0; i < plots.Count; i++)
                        {
                            var x = plots[i];
                            if(x.AethernetID != aetheryte.Value.ID && x.Path.Count > 0)
                            {
                                var index = i;
                                TaskGeneratePath.EnqueueValidate(i, x, aetheryte.Value);
                            }
                        }
                        P.TaskManager.Enqueue(() => P.NotificationMasterApi.DisplayTrayNotification("Path Completed".Loc()));
                    }
                }
                if(ImGui.Button($"{"For plot".Loc()} {LastPlot + 1}"))
                {
                    doCurPlot = true;
                }
            }
            List<ImGuiEx.EzTableEntry> entries = [];
            for(var i = 0; i < plots.Count; i++)
            {
                var index = i;
                var plot = plots[i];
                entries.Add(
                    new("Num".Loc(), () => ImGuiEx.Text($"{index + 1}")),
                    new("Front".Loc(), () => ImGuiEx.Text($"{plot.Front}")),
                    new("Aethernet".Loc(), () => ImGuiEx.Text($"{Svc.Data.GetExcelSheet<HousingAethernet>().GetRowOrDefault(plot.AethernetID)?.PlaceName.ValueNullable?.Name ?? plot.AethernetID.ToString()}")),
                    new("Edit".Loc(), () =>
                    {
                        if(ImGui.Button($"{"Edit".Loc()}{index + 1}"))
                        {
                            CurrentPath = plots[index].Path;
                        }
                    }),
                    new("Action".Loc(), () =>
                    {
                        if(ImGui.Button($"{"Set".Loc()}{index + 1}") || (doCurPlot && index == LastPlot))
                        {
                            LastPlot = -1;
                            doCurPlot = false;
                            Chat.ExecuteCommand("/clearlog");
                            DuoLog.Information($"{"For plot".Loc()} {index + 1}");
                            plot.Front = Player.Object.Position;
                            // 🔴 Svc.Objects 的元素是共用包裝:本 pin 每格 × 每種 kind 只預配一個實例,存取時就地
                            //    改寫 Address(ObjectTable.cs:198-231)。而 .Where 是延後求值 —— 原本整個列舉是在
                            //    下面 Task.Run 的執行緒池執行緒上、與 framework 幀完全不同步時才發生,中間還隔著
                            //    可長達數秒的 path.Wait()。讀到的可能已經是別的物件(靜默拿到錯座標,結果寫回設定檔),
                            //    也可能已經懸空(AccessViolationException,corrupted-state exception 攔不到)。
                            //    ⚠️ 本 fork 的 AssertMainThread 只警告不 throw,所以這條路徑不會有任何錯誤訊息。
                            //    ⇒ 在主執行緒(這裡就是 Draw 回呼)當場把要用的值抄成純資料再交給 Task.Run。
                            //    🔴 只加 .ToList() 不夠:那固化的是包裝物件的清單,包裝本身照樣會被就地改寫。要抄的是值。
                            //    ResidentialAetheryte 是 struct 且 Name 在建構時就算好,複製之後完全脫離遊戲記憶體。
                            var candidates = Svc.Objects
                                .Where(x => x.BaseId.EqualsAny(Utils.AethernetShards) && Vector3.Distance(plot.Front, x.Position) < 100f && S.Data.ResidentialAethernet.GetFromIGameObject(x) != null)
                                .Select(SnapshotAethernetCandidate)
                                .ToArray();
                            Task.Run(() =>
                            {
                                var currentDistance = float.MaxValue;
                                var currentAetheryte = -1;
                                foreach(var x in candidates)
                                {
                                    DuoLog.Information($"Candidate: {x.Aetheryte.Name}");
                                    var path = S.Ipc.VnavmeshIPC.Pathfind(plot.Front, x.Position, false);
                                    path.Wait();
                                    if(path.Result != null)
                                    {
                                        var distance = Utils.CalculatePathDistance([.. path.Result]);
                                        DuoLog.Information($"-- Distance: {distance} - best: {distance < currentDistance}");
                                        if(distance < currentDistance)
                                        {
                                            currentDistance = distance;
                                            currentAetheryte = (int)x.Aetheryte.ID;
                                        }
                                    }
                                    else
                                    {
                                        DuoLog.Information($"-- Failed to calculate distance");
                                    }
                                }
                                Svc.Framework.RunOnFrameworkThread(() =>
                                {
                                    plot.AethernetID = (uint)currentAetheryte;
                                    Utils.SaveGeneratedHousingData();
                                });
                            });
                        }
                    }),
                    new("Path".Loc(), () =>
                    {
                        ImGuiEx.Text($"{"Points".Loc()}: {plot.Path.Count}, {"Distance".Loc()}: {Utils.CalculatePathDistance([Player.Object.Position, .. plot.Path])}");
                        if(ImGui.IsItemHovered())
                        {
                            S.Ipc.SplatoonManager.RenderPath(plot.Path);
                        }
                    })
                    );
            }
            if(ImGui.BeginChild("Table"))
            {
                ImGuiEx.EzTable(entries);
            }
            ImGui.EndChild();
        }
        else
        {
            if(ImGui.Button($"{"Create data for".Loc()} {ExcelTerritoryHelper.GetName(P.Territory)}"))
            {
                data[P.Territory] = [];
            }
        }
    }

    /// <summary>
    /// 把一個乙太之光候選在「主執行緒的當下這一幀」抄成純資料 —— 這裡就是離開遊戲記憶體的邊界。
    /// 回傳的兩個成員都是值:Vector3 是複製,ResidentialAetheryte 是 struct 且 Name 在建構時就算好,
    /// 之後不管跨執行緒還是跨幀使用,都不會再碰到 ObjectTable 的共用包裝(它會被就地改寫 Address)。
    /// 🔴 呼叫端必須在列舉 Svc.Objects 的同一幀就 .ToArray():LINQ 是延後求值,不落地等於沒抄。
    /// </summary>
    private static (Vector3 Position, ResidentialAetheryte Aetheryte) SnapshotAethernetCandidate(IGameObject obj)
        => (obj.Position, S.Data.ResidentialAethernet.GetFromIGameObject(obj).Value);

    private static void Reader()
    {
        {
            if(TryGetAddonByName<AtkUnitBase>("LobbyDKTWorldList", out var addon) && IsAddonReady(addon))
            {
                var r = new ReaderLobbyDKTWorldList(addon);
                ImGuiEx.Text($"Source: {r.Source}");
                ImGuiEx.Text($"Destination: {r.Destination}");
                foreach(var dc in r.Regions)
                {
                    ImGuiEx.Text($"  {dc.RegionTitle}");
                    foreach(var world in dc.DataCenters)
                    {
                        ImGuiEx.Text($"    {world.Id}/{world.Name}");
                    }
                }
            }
        }
        {
            if(TryGetAddonByName<AtkUnitBase>("TelepotTown", out var addon) && IsAddonReady(addon))
            {
                var reader = new ReaderTelepotTown(addon);
                for(var i = 0; i < reader.DestinationData.Count; i++)
                {
                    var data = reader.DestinationData[i];
                    var name = reader.DestinationName[i];
                    ImGuiEx.Text($"{data.Type}|{data.State}|{data.CallbackData}|{data.IconID}|{name.Name}");
                }
            }
        }
    }

    private static int index = 0;
    private static string str = "";
    private static string str2 = "";
    private static string str3 = "";
    private static string World = "";
    private static ResidentialAetheryteKind ResiA;
    private static int Ward = 1;
    private static Vector2 uv0;
    private static Vector2 uv1;
    private static Vector2 size;
    private static string addr = "";
    private static string CharaName = "";
    private static int WorldSel;

    private static void Debug()
    {
        if(ImGui.CollapsingHeader("IPC test - travel from chara select screen".Loc()))
        {
            ref var name = ref Ref<string>.Get("name");
            ref var world = ref Ref<string>.Get("world");
            ref var dest = ref Ref<string>.Get("dest");
            ref var nologin = ref Ref<bool>.Get("nologin");
            ImGui.InputText("Chara name".Loc(), ref name, 100);
            ImGui.InputText("Chara world".Loc(), ref world, 100);
            ImGui.InputText("Destination".Loc(), ref dest, 100);
            ImGui.Checkbox("No login".Loc(), ref nologin);
            ImGuiEx.Text($"CanInitiateTravelFromCharaSelectList: {S.Ipc.IPCProvider.CanInitiateTravelFromCharaSelectList()}");
            ImGuiEx.Text($"CanAutoLogin: {S.Ipc.IPCProvider.CanAutoLogin()}");
            if(ImGui.Button("ConnectAndOpenCharaSelect")) DuoLog.Information($"{S.Ipc.IPCProvider.ConnectAndOpenCharaSelect(name, world)}");
            if(ImGui.Button("InitiateTravelFromCharaSelectScreen")) DuoLog.Information($"{S.Ipc.IPCProvider.InitiateTravelFromCharaSelectScreen(name, world, dest, nologin)}");
            if(ImGui.Button("ConnectAndTravel")) DuoLog.Information($"{S.Ipc.IPCProvider.ConnectAndTravel(name, world, dest, nologin)}");
        }
        if(ImGui.CollapsingHeader("ApproachConditionIsMet"))
        {
            ImGuiEx.Text($"ApproachConditionIsMet: {Utils.ApproachConditionIsMet()}");
            ImGuiEx.Text($"IsAetheryte: {P.ActiveAetheryte?.IsAetheryte}");
            ImGuiEx.Text($"GetReachableAetheryte: {Utils.GetReachableAetheryte(x => x.IsAetheryte())}");
        }
        if(ImGui.CollapsingHeader("S.Data.DataStore.Aetherytes"))
        {
            foreach(var x in S.Data.DataStore.Aetherytes)
            {
                ImGuiEx.Text($"{x.Key.Name} ({Svc.Data.GetExcelSheet<Aetheryte>(ClientLanguage.English).GetRowOrDefault(x.Key.ID).Value.AethernetName.Value.Name.GetText()})");
                ImGui.Indent();
                ImGuiEx.Text($"{x.Value.Select(s => $"{s.Name} ({Svc.Data.GetExcelSheet<Aetheryte>(ClientLanguage.English).GetRowOrDefault(s.ID).Value.AethernetName.Value.Name.GetText()})").Print("\n")}");
                ImGui.Unindent();
            }
        }
        if(ImGui.CollapsingHeader("Agent Map debug".Loc()))
        {
            if(TryGetAddonByName<AddonAreaMap>("AreaMap", out var addon))
            {
                ImGuiEx.Text($"{addon->HoveredCoords} - press ctrl to copy");
                // Framework 是 [StaticAddress(..., isPointer: true)],合法回 null。
                // 拿不到就當作「視窗非作用中」→ 不複製(fail-closed)。
                var fw = CSFramework.Instance();
                if(ImGuiEx.Ctrl && EzThrottler.Throttle("Copy") && fw != null && !fw->WindowInactive)
                {
                    Copy($", new({addon->HoveredCoords.X}f, {addon->HoveredCoords.Y}f)");
                }
            }
        }
        if(ImGui.CollapsingHeader("IPC debug".Loc()))
        {
            ref var id = ref Ref<int>.Get("aetheryteId");
            ImGui.InputInt("aetheryte id".Loc(), ref id);
            if(ImGui.Button("AethernetTeleportById")) DuoLog.Information($"{S.Ipc.IPCProvider.AethernetTeleportById((uint)id)}");
            if(ImGui.Button("HousingAethernetTeleportById")) DuoLog.Information($"{S.Ipc.IPCProvider.HousingAethernetTeleportById((uint)id)}");
            if(ImGui.Button("AethernetTeleportByPlaceNameId")) DuoLog.Information($"{S.Ipc.IPCProvider.AethernetTeleportByPlaceNameId((uint)id)}");
            if(ImGui.Button("AethernetTeleportToFirmament")) DuoLog.Information($"{S.Ipc.IPCProvider.AethernetTeleportToFirmament()}");
            if(ImGui.Button("GetActiveAetheryte")) DuoLog.Information($"{S.Ipc.IPCProvider.GetActiveAetheryte()}");
            if(ImGui.Button("GetActiveResidentialAetheryte")) DuoLog.Information($"{S.Ipc.IPCProvider.GetActiveResidentialAetheryte()}");
        }
        ImGuiEx.Text($"{"Active aetheryte".Loc()}: {P.ActiveAetheryte}");
        if(ImGui.CollapsingHeader("Chat".Loc()))
        {
            if(ImGui.Button("Send message (echo)".Loc())) Chat.ExecuteCommand($"/e Test test test {Random.Shared.Next()}");
            if(ImGui.Button("Send message (current channel)".Loc())) Chat.SendMessage($"Password: {Random.Shared.Next()}");
            if(ImGui.Button("Use sprint".Loc())) Chat.ExecuteAction(3);
            if(ImGui.Button("Use jump".Loc())) Chat.ExecuteGeneralAction(2);
            try
            {
                if(ImGui.Button("Try invalid string".Loc())) Chat.ExecuteCommand("/e \u000012345");
            }
            catch(Exception e)
            {
                e.Log();
            }
        }
        ImGui.Text(Utils.ParseSheetPattern("<Addon:10:Text>"));
        ImGui.Text(Utils.ParseSheetPattern("<Addon:10:RowId>"));
        if(ImGui.CollapsingHeader("DawnStory"))
        {
            if(TryGetAddonMaster<AddonMaster.DawnStory>(out var m) && m.IsAddonReady)
            {
                ImGuiEx.Text($"Cnt: {m.Reader.EntryCount}");
                foreach(var x in m.Entries)
                {
                    ImGuiEx.Text($"{x.Name} / {x.ReaderEntryName.Level} / {x.ReaderEntry.Callback} / {x.Index}");
                    if(ImGuiEx.HoveredAndClicked() && x.Status != 2 && AddonPressGuard.TryPressOnce("DawnStory", m.Base, "UIDebug.DawnStory", paramKey: x.Index.ToString(), escapeIsRoutine: true))
                    {
                        x.Select();
                    }
                }
            }
        }
        if(ImGui.CollapsingHeader("ReaderLobbyDKTWorldList"))
        {
            if(TryGetAddonByName<AtkUnitBase>("LobbyDKTWorldList", out var addon) && IsAddonReady(addon))
            {
                var r = new ReaderLobbyDKTWorldList(addon);
                ImGuiEx.Text($"""
                    Source {r.Source}
                    Destination {r.Destination}
                    SelectedDataCenter {r.SelectedDataCenter}
                    """);
                ImGuiEx.Text($"Regions:");
                ImGui.Indent();
                foreach(var region in r.Regions)
                {
                    ImGuiEx.Text($"""
                        {region.RegionTitle}
                        """);
                    ImGuiEx.Text("DataCenters");
                    foreach(var dc in region.DataCenters)
                    {
                        ImGui.Indent();
                        ImGuiEx.Text($"""
                            {dc.Name}
                            """);
                        ImGui.Unindent();
                    }
                }
                ImGui.Separator();
                ImGuiEx.Text($"Worlds: {r.GetNumWorlds()}");
                ImGui.Indent();
                foreach(var x in r.Worlds)
                {
                    ImGuiEx.Text($"{x.WorldName}, active={x.IsAvailable}");
                }
                ImGui.Unindent();
                ImGui.Unindent();
            }
        }
        if(ImGui.CollapsingHeader("Context".Loc()))
        {
            if(TryGetAddonMaster<AddonMaster.ContextMenu>(out var m))
            {
                foreach(var e in m.Entries)
                {
                    ImGuiEx.Text($"{e.Text} / {e.Enabled}");
                }
            }
        }
        if(ImGui.CollapsingHeader("CharaSelect".Loc()))
        {
            if(TryGetAddonMaster<AddonMaster._CharaSelectListMenu>(out var m))
            {
                foreach(var x in m.Characters)
                {
                    ImGuiEx.Text($"{x.Name}/{x.CurrentWorld}/{x.HomeWorld}/{x.IsSelected}");
                }
            }
        }
        if(ImGui.CollapsingHeader("Custom aethernet".Loc()))
        {
            if(ImGui.Button("Copy target".Loc()) && Svc.Targets.Target != null)
            {
                var pname = TerritoryInfo.Instance()->AreaPlaceNameId;
                var pname2 = TerritoryInfo.Instance()->SubAreaPlaceNameId;
                Copy($"""
                    new(new({Svc.Targets.Target.Position.X:F1}f, {Svc.Targets.Target.Position.Z:F1}f), {P.Territory}, GetPlaceName({pname}), Base), //{Svc.Data.GetExcelSheet<PlaceName>().GetRowOrDefault(pname)?.Name.GetText()} ({pname}), {Svc.Data.GetExcelSheet<PlaceName>().GetRowOrDefault(pname2)?.Name.GetText()} ({pname2}), 
                    """);
            }
            ImGuiEx.Text($"Active: {S.Data.CustomAethernet.ActiveAetheryte}");
            ImGuiEx.Text($"Valid: {Utils.GetValidAetheryte()}");
            if(Utils.GetValidAetheryte() != null) ImGuiEx.Text($"FromIGameObject: {S.Data.CustomAethernet.GetFromIGameObject(Utils.GetValidAetheryte())}");
        }
        if(ImGui.Button("Get file list".Loc())) Utils.ReadClipboardFiles();
        if(ImGui.Button("Open PF self".Loc()))
        {
            // AgentModule.Instance() 在 UIModule 尚未建立時回 null;GetAgentByInternalId 本身也可能回 null。
            // 這個指標最後會被交給原生的 OpenPartyFinderInfo(hook Original),傳 null 會崩在遊戲裡,
            // 所以兩層都要判,拿不到就整個不呼叫。
            var lfgAgentModule = AgentModule.Instance();
            var lfgAgent = lfgAgentModule == null ? null : lfgAgentModule->GetAgentByInternalId(AgentId.LookingForGroup);
            if(lfgAgent == null)
            {
                DuoLog.Warning("AgentLookingForGroup 尚未就緒,略過。");
            }
            else
            {
                S.Memory.OpenPartyFinderInfoDetour(lfgAgent, Player.CID);
            }
        }
        if(ImGui.CollapsingHeader("Lobby2"))
        {
            if(TryGetAddonMaster<AddonMaster._CharaSelectListMenu>(out var m))
            {
                foreach(var x in m.Characters)
                {
                    ImGuiEx.Text($"{x.Name} is at {ExcelWorldHelper.GetName(x.Entry->CurrentWorldId)}/{x.IsVisitingAnotherDC}/{x.Entry->LoginFlags}");
                }
            }
        }
        if(ImGui.CollapsingHeader("Curcular movelemt".Loc()))
        {
            ImGuiEx.Text($"{MathHelper.IsPointPerpendicularToLineSegment(Player.Position.ToVector2(), new(-135f, -85f), new(-125.000f, -80f))}");
            ImGuiEx.Text($"{MathHelper.FindClosestPointOnLine(Player.Position.ToVector2(), new(-135f, -85f), new(-125.000f, -80f))}");
            ImGuiEx.Text($"{Vector2.Distance(Player.Position.ToVector2(), MathHelper.FindClosestPointOnLine(Player.Position.ToVector2(), new(-135f, -85f), new(-125.000f, -80f)))}");
            ref var target = ref Ref<Vector3>.Get();
            ref var exit = ref Ref<Vector3>.Get("exit");
            ref var list = ref Ref<List<Vector3>>.Get();
            ref var listList = ref Ref<List<List<Vector3>>>.Get();
            ref var prec = ref Ref<float>.Get("precision");
            ref var tol = ref Ref<int>.Get("tlr");
            ref var lim1 = ref Ref<float>.Get("lim1");
            ref var lim2 = ref Ref<float>.Get("lim2");
            ref var auto = ref Ref<bool>.Get("autocalc");
            if(ImGui.Button("Import".Loc()))
            {
                try
                {
                    var des = JsonConvert.DeserializeObject<CustomAliasCommand>(Paste());
                    target = des.CenterPoint.ToVector3();
                    exit = des.CircularExitPoint;
                    prec = des.Precision;
                    tol = des.Tolerance;
                    lim1 = des.Clamp?.Min ?? 0;
                    lim2 = des.Clamp?.Max ?? 0;
                }
                catch(Exception e)
                {
                    e.LogDuo();
                }
            }
            if(ImGui.Button("Set target".Loc())) target = Svc.Targets.Target?.Position ?? default;
            ImGui.SameLine();
            ImGuiEx.Text($"{target}");
            if(ImGui.Button("Set exit".Loc())) exit = Player.Position;
            ImGui.SameLine();
            ImGuiEx.Text($"{exit}");
            ImGui.InputFloat("Precision".Loc(), ref prec);
            ImGui.InputInt("Tolerance".Loc(), ref tol);
            ImGui.InputFloat("Limit1".Loc(), ref lim1);
            ImGui.InputFloat("Limit2".Loc(), ref lim2);
            if(ImGui.Button("Calculate".Loc()) || (auto && EzThrottler.Throttle("AutoRec", 100)))
            {
                (float, float)? lmt = lim2 > lim1 ? (lim1, lim2) : null;
                list = MathHelper.CalculateCircularMovement(target, Player.Position, exit, out listList, prec, tol, lmt);
            }
            ImGui.SameLine();
            ImGui.Checkbox("AutoCalc".Loc(), ref auto);
            if(list != null)
            {
                ImGuiEx.Text($"List: {list.Print()}");
                S.Ipc.SplatoonManager.RenderPath(list, false, true);
            }
            if(listList != null)
            {
                foreach(var x in listList)
                {
                    ImGuiEx.Text($"Candidate: {x.Print()}");
                    if(ImGui.IsItemHovered())
                    {
                        S.Ipc.SplatoonManager.RenderPath(x, false, true);
                    }
                }
            }
        }
        if(ImGui.CollapsingHeader("CharaSelectListMenu"))
        {
            // 🔴 RaptureAtkUnitManager.Instance() 是 CS 裡的**手寫**包裝
            //    （`raptureAtkModule == null ? null : &raptureAtkModule->RaptureAtkUnitManager`），
            //    不是產生器的 [StaticAddress] —— 它會合法回 null（UIModule/RaptureAtkModule
            //    任一層還沒建好時）。裸解參考是攔不到的 AVE（corrupted-state exception）。
            //    這是偵錯視窗的每幀繪製路徑：不記 log，取不到就顯示一行說明。
            var raptureAtkUnitManager = RaptureAtkUnitManager.Instance();
            if(raptureAtkUnitManager == null)
            {
                ImGuiEx.Text("RaptureAtkUnitManager 尚未就緒");
            }
            else
            {
                var list = raptureAtkUnitManager->FocusedUnitsList;
                foreach(var x in list.Entries)
                {
                    if(x.Value == null) continue;
                    ImGuiEx.Text($"{x.Value->NameString}");
                }
            }
            { if(TryGetAddonMaster<AddonMaster._CharaSelectListMenu>(out var m)) ImGuiEx.Text($"Selected chara: {m.Characters.FirstOrDefault(x => x.IsSelected)?.Name}"); }
        }
        ImGui.Checkbox("DisableHousePathData", ref P.DisableHousePathData);
        if(ImGui.CollapsingHeader("HUD"))
        {
            // 🔴 同一個家族的第三種來源：[Agent(AgentId.Hud)] 產生的 Instance() 樣板是
            //    `agentModule == null ? null : (T*)agentModule->GetAgentByInternalId(...)`
            //    —— 一樣會合法回 null（AgentGetterGenerator.cs:44 直證）。
            //    ⚠️ 這一處不在原掃描清單裡，是修上面那處時同檔同形一併掃到的。
            var hud = AgentHUD.Instance();
            if(hud == null)
            {
                ImGuiEx.Text("AgentHUD 尚未就緒");
            }
            else
            {
                for(var i = 0; i < hud->MapMarkers.Count; i++)
                {
                    var marker = hud->MapMarkers[i];
                    var pos = new Vector3(marker.Position.X, marker.Position.Y, marker.Position.Z);
                    ImGuiEx.Text($"Marker {marker.IconId}, pos: {pos:F1}, distance: {Vector3.Distance(Player.Position, pos):f1}");
                    if(ThreadLoadImageHandler.TryGetIconTextureWrap(marker.IconId, false, out var w))
                    {
                        ImGui.SameLine();
                        ImGui.Image(w.Handle, new(30f));
                    }
                }
            }
        }
        var data = Svc.Data.GetExcelSheet<Addon>().GetRow(195);
        var text = data.Text.GetText();
        if(ImGui.Button("Lumina"))
        {
            /*foreach(var x in data.Text.Payloads)
            {
                PluginLog.Information($"Payload {x.PayloadType}, text: {x.ToString()}");
            }*/
        }
        if(ImGui.Button("Dalamud"))
        {
            foreach(var x in data.Text.ToDalamudString().Payloads)
            {
                PluginLog.Information($"Payload {x.Type}, text: {x.ToString()}");
            }
        }
        if(ImGui.Button("YesNo"))
        {
            if(TryGetAddonByName<AddonSelectYesno>("SelectYesno", out var addon))
            {
                // PromptText 是 addon 上的節點指標,節點還沒建好時是 null,
                // 直接解參考 NodeText 就是 AccessViolation(try/catch 攔不到)。
                // 這是使用者按按鈕觸發的除錯路徑,取不到就明講,不要安靜地什麼都不印。
                if(addon->PromptText == null)
                {
                    PluginLog.Information("PromptText 節點取不到（尚未建立）");
                }
                else
                {
                    foreach(var x in addon->PromptText->NodeText.Read().Payloads)
                    {
                        PluginLog.Information($"Payload {x.Type}, text: {x.ToString()}");
                    }
                }
            }
        }
        ImGui.InputText("##copyaddon", ref text, 300);
        if(ImGui.CollapsingHeader("Misc".Loc()))
        {
            if(ImGui.Button("Switch".Loc()))
            {
                bool Do()
                {
                    if(TryGetAddonByName<AddonRepair>("Repair", out var addon) && addon->AtkUnitBase.IsVisible)
                    {
                        // 🔴 節點鏈原本未判空(GetNodeById 找不到合法回 null,GetAsAtkComponentButton 對 null this ＝ AVE);
                        //    換頁不關窗 ⇒ 走多次互動窗的 15 幀逃生口,使用者中途關窗的那幾幀不會再被按到。
                        var node = addon->AtkUnitBase.GetNodeById(14);
                        var fwdBtn = node == null ? null : node->GetAsAtkComponentButton();
                        if(fwdBtn == null) return false;
                        if(!AddonPressGuard.TryPressOnce("Repair", addon, "UIDebug.RepairSwitch", paramKey: "page", escapeIsRoutine: true)) return false;
                        fwdBtn->ClickAddonButton((AtkComponentBase*)addon, 2, EventType.CHANGE);

                        return true;

                    }
                    return false;
                }
                for(var i = 0; i < 10000; i++)
                {
                    P.TaskManager.Enqueue(Do);
                }
            }
        }
        if(ImGui.CollapsingHeader("Instance".Loc()))
        {
            // MaxInstances 的 StaticAddress sig 在台服掃不到時欄位為 null,裸解參考會擲例外。
            // 除錯視窗顯示「N/A」而不是崩掉(把「不知道」畫成看似正常的值也會誤導看視窗的人)。
            ImGuiEx.Text($"""
                Max instances: {(S.Memory.MaxInstances == null ? "N/A (sig unresolved)" : (*S.Memory.MaxInstances).ToString())}
                Initialized: {S.InstanceHandler.InstancesInitizliaed(out var maxInstances)} {maxInstances}
                GetInstance: {S.InstanceHandler.GetInstance()}
                DrawConditions: {S.Gui.Overlay.DrawConditions()}
                """);
            if(ImGui.Button("instance data reset".Loc())) C.PublicInstances.Clear();
            if(ImGui.Button("game version reset".Loc())) C.GameVersion = "";
        }
        ImGuiEx.Text($"Player interactable: {Player.Interactable}");
        // AgentMap 取得器合法回 null。拿不到時印 "?" 而不是 false ——
        // 把「不知道」畫成一個看起來正常的值會直接誤導看偵錯視窗的人。
        var dbgMap = AgentMap.Instance();
        ImGuiEx.Text($"Is moving: {(dbgMap == null ? "?" : $"{dbgMap->IsPlayerMoving}")}");
        ImGuiEx.Text($"IsOccupied: {IsOccupied()}");
        ImGuiEx.Text($"Casting: {Player.Object?.IsCasting}");
        if(ImGui.CollapsingHeader("Data test".Loc()))
        {
            foreach(var x in S.Data.DataStore.Aetherytes)
            {
                ImGuiEx.Text($"""
                    Key:
                        Name: {x.Key.Name}
                        ID: {x.Key.ID}
                        Pos: {x.Key.Position}
                        Group: {x.Key.Group}
                        Territory: {ExcelTerritoryHelper.GetName(x.Key.TerritoryType, true)}
                    Value:
                        Cnt: {x.Value.Count}
                    """);
                foreach(var z in x.Value)
                {
                    ImGui.Indent();
                    ImGuiEx.Text($"""
                        Name: {z.Name}
                        ID: {z.ID}
                        Pos: {z.Position}
                        Group: {z.Group}
                        Territory: {ExcelTerritoryHelper.GetName(z.TerritoryType, true)}
                        """);
                    ImGui.Unindent();
                }
            }
        }
        if(ImGui.CollapsingHeader("Lobby test".Loc()))
        {
            ImGui.InputText("Chara name".Loc(), ref CharaName, 100);
            WorldSelector.Instance.Draw(ref WorldSel);
            if(ImGui.Button("Select"))
            {
                DCChange.SelectCharacter(CharaName, (uint)WorldSel);
            }
            if(ImGui.Button("Context"))
            {
                DCChange.OpenContextMenuForChara(CharaName, (uint)WorldSel, (uint)WorldSel);
            }
            // AgentLobby 取得器合法回 null(不在角色選擇畫面時本來就沒有這個 agent)。
            var agent = AgentLobby.Instance();
            if(agent == null)
            {
                ImGuiEx.Text("AgentLobby 尚未就緒");
            }
            else
            {
                ImGuiEx.Text($"Active: {agent->IsAgentActive()}");
                for(var i = 0; i < agent->LobbyData.CharaSelectEntries.Count; i++)
                {
                    var c = agent->LobbyData.CharaSelectEntries[i].Value;
                    ImGuiEx.Text($"Locked: {agent->TemporaryLocked}");
                    // CharaSelectEntries 的元素本身是可為 null 的指標(項目還沒填完就是 null),
                    // 直接 c->Name.Read() 是 AccessViolation。
                    // 這是每影格的 ImGui 繪製路徑,不寫 log；「取不到」直接畫在列上,
                    // 不要靜默跳過整列害人以為角色少了一個。
                    if(c == null)
                    {
                        ImGuiEx.Text($"{i}: (空項目)");
                    }
                    else
                    {
                        ImGuiEx.Text($"{i}: {c->Name.Read()}/{c->HomeWorldName.Read()}");
                    }
                }
            }
        }
        if(ImGui.CollapsingHeader("Addon test".Loc()))
        {
            // 手動除錯按鈕也走同一條路:原本連 IsAddonReady 都沒有,對尚未就緒的 SelectString 讀 PopupMenu、送 Select 一樣是 AVE。
            if(TryGetAddonByName<AddonSelectString>("SelectString", out var addon) && IsAddonReady(&addon->AtkUnitBase))
            {
                ImGuiEx.Text($"Entries: {addon->PopupMenu.PopupMenu.EntryCount}");
                foreach(var entry in new AddonMaster.SelectString(addon).Entries)
                {
                    var entryText = entry.Text;
                    ImGuiEx.Text($"{entryText}");
                    // 讀到 U+FFFD ＝ 這扇窗記憶體正在變動(多半是關閉中),這一幀不要按它。
                    if(ImGuiEx.HoveredAndClicked() && !AddonPressGuard.IsTextUnstable("SelectString", entryText) && AddonPressGuard.TryPressOnce("SelectString", addon, "UIDebug.SelectString", paramKey: entry.Index.ToString()))
                    {
                        entry.Select();
                    }
                }
            }
        }
        if(ImGui.Button("Refresh color".Loc()))
        {
            DalamudReflector.GetService("Dalamud.Plugin.Ipc.Internal.DataShare").GetFoP<System.Collections.IDictionary>("caches").Remove("ECommonsPatreonBannerRandomColor");
            ((System.Collections.IDictionary)typeof(EzSharedData).GetFieldPropertyUnion("Cache", ReflectionHelper.AllFlags).GetValue(null)).Remove("ECommonsPatreonBannerRandomColor");
        }
        if(ImGui.CollapsingHeader("Render".Loc()))
        {

            if(ImGui.Button("Save".Loc())) Svc.Data.GetFile("ui/uld/Teleport_hr1.tex").SaveFile("d:\\file.tex");
        }
        if(ImGui.CollapsingHeader("Housing manager".Loc()))
        {
            var h = HousingManager.Instance();
            if(h == null)
            {
                ImGuiEx.Text("null".Loc());
            }
            else
            {
                ImGuiEx.Text($"{"Ward:".Loc()} {h->GetCurrentWard()}");
                ImGuiEx.Text($"{"Plot:".Loc()} {h->GetCurrentPlot()}");
                ImGuiEx.Text($"{"Division:".Loc()} {h->GetCurrentDivision()}");
            }
        }
        if(ImGui.CollapsingHeader("Path"))
        {
            if(ImGui.Button("Add".Loc())) DebugPath.Add(Player.Object.Position);
            //if (ImGui.Button("Go")) P.FollowPath.Waypoints.AddRange(Enumerable.Reverse(DebugPath));
            if(ImGui.Button("Copy".Loc())) Copy($"new Vector3({Player.Object.Position.X}f, {Player.Object.Position.Y}f, {Player.Object.Position.Z}f);");
            for(var i = 0; i < DebugPath.Count; i++)
            {
                ImGuiEx.Text($"{DebugPath[i]}");
                if(ImGuiEx.HoveredAndClicked())
                {
                    DebugPath.RemoveAt(i);
                    break;
                }
            }
        }
        if(ImGui.CollapsingHeader("TPW"))
        {
            ImGui.InputText("World".Loc(), ref World, 100);
            ImGuiEx.EnumCombo("Resi", ref ResiA);
            ImGui.InputInt("Ward".Loc(), ref Ward);
            if(ImGui.Button("Go".Loc()))
            {
                TaskTpAndGoToWard.Enqueue(World, ResiA, Ward, 1, false, false);
            }
        }
        if(ImGui.CollapsingHeader("State".Loc()))
        {
            ImGuiEx.Text($"CanUseAetheryte = {Utils.CanUseAetheryte()}");
            ImGuiEx.Text($"ResidentialAethernet.ActiveAetheryte = {S.Data.ResidentialAethernet.ActiveAetheryte}");
            ImGuiEx.Text($"GetValidAetheryte = {Utils.GetValidAetheryte()}");
        }
        if(ImGui.CollapsingHeader("Housing aethernet".Loc()))
        {
            foreach(var x in S.Data.ResidentialAethernet.ZoneInfo)
            {
                if(ImGuiEx.TreeNode($"{x}"))
                {
                    foreach(var a in x.Value.Aetherytes)
                    {
                        ImGuiEx.Text($"{a.Name} / {a.Position} / {ExcelTerritoryHelper.GetName(a.TerritoryType)}");
                    }
                    ImGui.TreePop();
                }
            }
        }
        if(ImGui.CollapsingHeader("DCV"))
        {
            if(ImGui.Button("Unlock all worlds".Loc())) UnlockAllWorlds();
            if(ImGui.Button("Enable AtkComponentTreeList_vf31Hook hook"))
            {
                S.Memory.AtkComponentTreeList_vf31Hook.Enable();
            }
            {
                if(TryGetAddonByName<AtkUnitBase>("LobbyDKTWorldList", out var addon) && ImGui.Button("Try event".Loc()))
                {
                    //S.Memory.ConstructEvent(addon);
                    // 🔴 NodeList[7] 上界與元素都沒驗;GetAsAtkComponentList() 是 [MemberFunction],
                    //    對 null 節點呼叫等於把 this = 0 交給遊戲原生碼。
                    //    取不到就顯示 "?" —— 這是診斷欄,把「不知道」畫成一個位址會直接誤導判讀。
                    //    ⚠️ `+ 456` 是**指標運算**(456 × sizeof(AtkComponentList))而不是位元組偏移,
                    //    看起來很可疑;但這一行只是把數字印出來、從來沒有解參考,所以維持原樣不動。
                    var dktNode = GetNodeSafe(&addon->UldManager, 7);
                    var dktList = dktNode == null ? null : dktNode->GetAsAtkComponentList();
                    ImGuiEx.Text($"PTR: {(dktList == null ? "?" : $"{(nint)(dktList + 456):X16}")}");
                }
            }
            if(ImGui.Button($"{nameof(DCChange.Logout)}")) PluginLog.Information($"{DCChange.Logout()}");
            if(ImGui.Button($"{nameof(DCChange.SelectYesLogout)}")) PluginLog.Information($"{DCChange.SelectYesLogout()}");
            if(ImGui.Button($"Enable AddonDKTWorldCheck_ReceiveEventHook")) S.Memory.AddonDKTWorldList_ReceiveEventHook.Enable();
            if(ImGui.Button($"{nameof(DCChange.TitleScreenClickStart)}")) PluginLog.Information($"{DCChange.TitleScreenClickStart()}");
            //if (ImGui.Button($"{nameof(DCChange.OpenContextMenuForChara)}")) PluginLog.Information($"{DCChange.OpenContextMenuForChara(str)}");
            ImGui.SameLine();
            ImGui.InputText($"{"Chara name".Loc()}", ref str, 100);
            if(ImGui.Button($"{nameof(DCChange.SelectVisitAnotherDC)}")) PluginLog.Information($"{DCChange.SelectVisitAnotherDC()}");
            if(ImGui.Button($"{nameof(DCChange.SelectTargetDataCenter)}")) PluginLog.Information($"{DCChange.SelectTargetDataCenter(str2)}");
            ImGui.SameLine();
            ImGui.InputText($"{"dc name".Loc()}", ref str2, 100);
            if(ImGui.Button($"{nameof(DCChange.SelectTargetWorld)}")) PluginLog.Information($"{DCChange.SelectTargetWorld(str3, null)}");
            ImGui.SameLine();
            ImGui.InputText($"{"w name".Loc()}", ref str3, 100);
            if(ImGui.Button($"{nameof(DCChange.ConfirmDcVisit)}")) PluginLog.Information($"{DCChange.ConfirmDcVisit()}");
            if(ImGui.Button($"{nameof(DCChange.ConfirmDcVisit2)}")) PluginLog.Information($"{DCChange.ConfirmDcVisit2(default, default, default, default)}");
            if(ImGui.Button($"{nameof(DCChange.SelectOk)}")) PluginLog.Information($"{DCChange.SelectOk()}");
            if(ImGui.Button($"{nameof(DCChange.ConfirmDcVisitIntention)}")) PluginLog.Information($"{DCChange.ConfirmDcVisitIntention()}");
            if(ImGui.Button($"{nameof(DCChange.SelectYesLogin)}")) PluginLog.Information($"{DCChange.SelectYesLogin()}");
            ImGui.InputInt("Index".Loc(), ref index);
            if(ImGui.Button("Open context menu".Loc()))
            {
                if(TryGetAddonByName<AtkUnitBase>("_CharaSelectListMenu", out var addon) && IsAddonReady(addon) && AddonPressGuard.TryPressOnce("_CharaSelectListMenu", addon, "UIDebug.OpenContextMenu", paramKey: $"17|1|{index}", escapeIsRoutine: true))
                {
                    Callback.Fire(addon, false, (int)17, (int)1, (int)index);
                }
            }
            ImGuiEx.TextWrapped($"Names: {Utils.GetCharacterNames().Print()}");
        }
        if(ImGui.CollapsingHeader("Throttle"))
        {
            EzThrottler.ImGuiPrintDebugInfo();
            FrameThrottler.ImGuiPrintDebugInfo();
        }
        if(Svc.Targets.Target != null && Player.Available)
        {
            ImGuiEx.Text($"v.dist: {Svc.Targets.Target.Position.Y - Player.Object.Position.Y}");
            ImGuiEx.Text($"DTT3D: {Vector3.Distance(Svc.Targets.Target.Position, Player.Object.Position)}");
        }
    }

    private static void UnlockAllWorlds()
    {
        if(TryGetAddonByName<AtkUnitBase>("LobbyDKTWorldList", out var addon) && IsAddonReady(addon))
        {
            // 🔴 這裡有兩個不同的問題:
            //    ①六跳裸鏈(NodeList[6] → Component → NodeList[i] → Component → NodeList[8] → 文字節點),
            //      上界與元素全程沒驗;真正的炸點是下面讀/寫 Alpha_2 那兩行,不是取節點那一行。
            //    ②addon->AtkValues[160 + …] 是**寫入**,而陣列長度是 AtkValuesCount(ushort),
            //      原本連上界都沒看 —— 越界寫比越界讀更糟,踩到的是相鄰的原生記憶體,
            //      失敗形式是別的地方莫名其妙壞掉,完全指不回這一行。
            //    取不到就跳過該筆(這是除錯用的「解鎖全部世界」按鈕,少改一筆的代價只是沒解鎖)。
            for(var i = 3; i < 3 + 8; i++)
            {
                var valueIndex = 160 + (i - 3) * 8;
                if(addon->AtkValues != null && valueIndex < addon->AtkValuesCount)
                {
                    addon->AtkValues[valueIndex].Int = 0;
                }
                var t = GetTextNodeSafe(GetComponentNodeSafe(GetComponentNodeSafe(addon, 6, i), 8));
                if(t != null && t->Alpha_2 != 255)
                {
                    t->Alpha_2 = 255;
                }
            }
        }
    }

    private static void Editor()
    {
        var bsize = ImGuiHelpers.GetButtonSize("A") with { X = 280 };
        if(ImGui.Button("Save".Loc()))
        {
            ImGui.SetClipboardText(JsonConvert.SerializeObject(S.Data.DataStore.StaticData));
            S.Data.DataStore.StaticData.SaveConfiguration(Path.Combine(Svc.PluginInterface.AssemblyLocation.DirectoryName, S.Data.DataStore.FileName));
        }
        foreach(var x in S.Data.DataStore.Aetherytes)
        {
            ImGui.Separator();
            if(ImGui.Button($"{x.Key.Name}", bsize))
            {
                DebugAetheryte = x.Key;
            }
            {
                {
                    ImGui.SameLine();
                    if(!S.Data.DataStore.StaticData.SortOrder.ContainsKey(x.Key.ID)) S.Data.DataStore.StaticData.SortOrder[x.Key.ID] = 0;
                    var d = (int)S.Data.DataStore.StaticData.SortOrder[x.Key.ID];
                    ImGui.SetNextItemWidth(100f.Scale());
                    if(ImGui.InputInt($"##{x.Key.Name}{x.Key.ID}sort", ref d))
                    {
                        S.Data.DataStore.StaticData.SortOrder[x.Key.ID] = (uint)d;
                    }
                }
                if(ImGui.GetIO().KeyCtrl)
                {
                    ImGui.SameLine();
                    ImGuiEx.Text($"{x.Key.Position}");
                }
                if(Svc.Targets.Target != null)
                {
                    ImGui.SameLine();
                    if(ImGui.Button("Pos".Loc() + "##" + x.Key.ID))
                    {
                        S.Data.DataStore.StaticData.CustomPositions[x.Key.ID] = Svc.Targets.Target.Position;
                        DuoLog.Information("Written ?? for ??".Loc(Svc.Targets.Target.Position, x.Key.ID));
                    }
                }
            }
            foreach(var l in x.Value)
            {
                if(ImGui.Button($"    {l.Name}", bsize)) DebugAetheryte = l;
                {
                    {
                        ImGui.SameLine();
                        if(!S.Data.DataStore.StaticData.SortOrder.ContainsKey(l.ID)) S.Data.DataStore.StaticData.SortOrder[l.ID] = 0;
                        var d = (int)S.Data.DataStore.StaticData.SortOrder[l.ID];
                        ImGui.SetNextItemWidth(100f.Scale());
                        if(ImGui.InputInt($"##{l.Name}{l.ID}sort", ref d))
                        {
                            S.Data.DataStore.StaticData.SortOrder[l.ID] = (uint)d;
                        }
                    }
                    if(ImGui.GetIO().KeyCtrl)
                    {
                        ImGui.SameLine();
                        ImGuiEx.Text($"{l.Position}");
                    }
                    if(Svc.Targets.Target != null)
                    {
                        ImGui.SameLine();
                        if(ImGui.Button("Pos".Loc() + "##" + l.ID))
                        {
                            S.Data.DataStore.StaticData.CustomPositions[l.ID] = Svc.Targets.Target.Position;
                            DuoLog.Information("Written ?? for ??".Loc(Svc.Targets.Target.Position, l.ID));
                        }
                    }
                }
            }
        }
        ImGuiEx.Text(Utils.GetAvailableAethernetDestinations().Join("\n"));
        if(ImGui.Button("null".Loc())) DebugAetheryte = null;
    }
}
