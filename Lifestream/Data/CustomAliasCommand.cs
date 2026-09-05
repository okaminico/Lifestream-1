using ECommons.Automation.NeoTaskManager.Tasks;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using ECommons.MathHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using Lifestream.Tasks.SameWorld;
using Lifestream.Tasks.Utility;
using Lumina.Excel.Sheets;
using Action = System.Action;

namespace Lifestream.Data;
[Serializable]
public class CustomAliasCommand
{
    private static readonly CustomAliasCommand Default = new();

    internal string ID = Guid.NewGuid().ToString();
    public CustomAliasKind Kind;
    public Vector3 Point;
    public uint Aetheryte;
    public int World;
    public Vector2 CenterPoint;
    public Vector3 CircularExitPoint;
    public (float Min, float Max)? Clamp = null;
    public float Precision = 20f;
    public int Tolerance = 1;
    public bool WalkToExit = true;
    public float SkipTeleport = 15f;
    public uint DataID = 0;
    public bool UseTA = false;
    public List<string> SelectOption = [];
    public bool StopOnScreenFade = false;
    public bool NoDisableYesAlready = false;
    public bool UseFlight = false;
    public float Scatter = 0f;

    public bool ShouldSerializeScatter() => Kind.EqualsAny(CustomAliasKind.Move_to_point) && Scatter > 0f;
    public bool ShouldSerializeUseFlight() => Kind.EqualsAny(CustomAliasKind.Move_to_point, CustomAliasKind.Navmesh_to_point) && UseFlight != Default.UseFlight;
    public bool ShouldSerializePoint() => Point != Default.Point;
    public bool ShouldSerializeAetheryte() => Aetheryte != Default.Aetheryte;
    public bool ShouldSerializeWorld() => World != Default.World;
    public bool ShouldSerializeCenterPoint() => CenterPoint != Default.CenterPoint;
    public bool ShouldSerializeCircularExitPoint() => CircularExitPoint != Default.CircularExitPoint;
    public bool ShouldSerializeClamp() => Clamp != Default.Clamp;
    public bool ShouldSerializePrecision() => Precision != Default.Precision;
    public bool ShouldSerializeTolerance() => Tolerance != Default.Tolerance;
    public bool ShouldSerializeWalkToExit() => WalkToExit != Default.WalkToExit;
    public bool ShouldSerializeSkipTeleport() => SkipTeleport != Default.SkipTeleport;
    public bool ShouldSerializeDataID() => DataID != Default.DataID;
    public bool ShouldSerializeUseTA() => UseTA != Default.UseTA;
    public bool ShouldSerializeSelectOption() => SelectOption.Count > 0;
    public bool ShouldSerializeStopOnScreenFade() => StopOnScreenFade != Default.StopOnScreenFade;
    public bool ShouldSerializeNoDisableYesAlready() => NoDisableYesAlready != Default.NoDisableYesAlready;

    public void Enqueue(List<Vector3> appendMovement)
    {
        if(Kind == CustomAliasKind.Change_world)
        {
            P.TaskManager.Enqueue(() => IsScreenReady() && Player.Interactable);
            if(World != Player.Object.CurrentWorld.RowId)
            {
                var world = ExcelWorldHelper.GetName(World);
                if(S.Ipc.IPCProvider.CanVisitCrossDC(world))
                {
                    P.TPAndChangeWorld(world, true, skipChecks: true);
                }
                else if(S.Ipc.IPCProvider.CanVisitSameDC(world))
                {
                    P.TPAndChangeWorld(world, false, skipChecks: true);
                }
            }
        }
        else if(Kind == CustomAliasKind.Move_to_point)
        {
            P.TaskManager.Enqueue(() => IsScreenReady() && Player.Interactable);
            // 🔴 不能裸排 FlyIfCan:它在不可飛的區域回 null,而 NeoTaskManager 的 null＝中止整條佇列,
            //    於是「勾了使用飛行」的別名一到不能飛的地方就整條靜默斷掉(連移動都不會做)。
            //    包過的版本把「不能飛」降級成「用走的」。
            if(UseFlight) P.TaskManager.Enqueue(FlightTasks.FlyIfCanOrGiveUp);
            P.TaskManager.Enqueue(() => TaskMoveToHouse.UseSprint(false));
            P.TaskManager.Enqueue(() => P.FollowPath.Move([Point.Scatter(Scatter), .. appendMovement], true));
            P.TaskManager.Enqueue(() => P.FollowPath.Waypoints.Count == 0);
        }
        else if(Kind == CustomAliasKind.Navmesh_to_point)
        {
            P.TaskManager.Enqueue(() => IsScreenReady() && Player.Interactable);
            // ⚠️ 刻意直接傳 IsReady 這個方法群組，不要包成 `IsReady() == true`：
            // 它在 IPC 整個叫不動時是**回 null**（同時自己印一次錯誤），而 null 在 NeoTaskManager
            // 的語意是「中止」。包成 == true 會把那個 null 變成 false，於是這一步空轉滿預設的
            // 30 秒逾時，而 IsReady 每一幀都再印一次聊天欄錯誤 —— 一次故障洗出上千行。
            P.TaskManager.Enqueue(S.Ipc.VnavmeshIPC.IsReady, "CustomAliasNavmeshWaitNavReady");
            if(UseTA && Svc.PluginInterface.InstalledPlugins.Any(x => x.Name == "TextAdvance" && x.IsLoaded))
            {
                P.TaskManager.Enqueue(() =>
                {
                    S.Ipc.TextAdvanceIPC.EnqueueMoveTo2DPoint(new()
                    {
                        Position = Point,
                        NoInteract = true,
                    }, 5f);
                });
                P.TaskManager.Enqueue(S.Ipc.TextAdvanceIPC.IsBusy, new(abortOnTimeout: false, timeLimitMS: 5000));
                P.TaskManager.Enqueue(() => !S.Ipc.TextAdvanceIPC.IsBusy(), new(timeLimitMS: 1000 * 60 * 5));
                P.TaskManager.Enqueue(() => P.FollowPath.Move([.. appendMovement], true));
                P.TaskManager.Enqueue(() => IsScreenReady() && Player.Interactable);
                P.TaskManager.Enqueue(() => P.FollowPath.Waypoints.Count == 0);
            }
            else
            {
                // 🔴 同上:裸排 FlyIfCan 在不可飛區域會回 null 而把整條佇列中止掉。
                if(UseFlight) P.TaskManager.Enqueue(FlightTasks.FlyIfCanOrGiveUp);
                P.TaskManager.Enqueue(() =>
                {
                    // 🔴 起飛沒成功就不能再要求飛行路線:vnavmesh 的 FollowPath 對「路徑要求飛行、
                    //    角色卻沒上坐騎」是 `_movement.Enabled = false; return;` —— 角色**站著不動**
                    //    而且零訊息。這一格只在「原本就會中止整條佇列」的情況下才會走到,
                    //    對本來就飛得起來的別名行為完全不變。
                    var fly = UseFlight && Svc.Condition[ConditionFlag.InFlight];
                    if(UseFlight && !fly) PluginLog.Information("[CustomAlias] 沒有飛起來,改用地面路線尋路。");
                    var task = S.Ipc.VnavmeshIPC.Pathfind(Player.Position, Point, fly);
                    P.TaskManager.InsertMulti(
                        new(() => task.IsCompleted),
                        new(() => TaskMoveToHouse.UseSprint(false)),
                        new(() => P.FollowPath.Move([.. task.Result, .. appendMovement], true)),
                        new(() => P.FollowPath.Waypoints.Count == 0)
                        );
                });
            }
        }
        else if(Kind == CustomAliasKind.Teleport_to_Aetheryte)
        {
            P.TaskManager.Enqueue(() => IsScreenReady() && Player.Interactable);
            P.TaskManager.Enqueue(() =>
            {
                // 別名裡的以太之光 ID 是設定檔回讀的，可能跨版本殘留或匯入自其他服務版本。
                // 裸 GetRow 查無此列時 Lumina 會擲例外，整條別名任務鏈會在沒有任何訊息的
                // 情況下斷掉；改成查不到就記一行使用者看得見的錯誤並跳過這一步。
                if(!Svc.Data.GetExcelSheet<Aetheryte>().TryGetRow(Aetheryte, out var aetheryte))
                {
                    DuoLog.Error($"此別名的乙太之光（ID {Aetheryte}）已不存在，略過這一步。");
                    return;
                }
                var nearestAetheryte = Svc.Objects.OrderBy(Player.DistanceTo).FirstOrDefault(x => x.IsTargetable && x.IsAetheryte() && Utils.IsAetheryteEligibleForCustomAlias(x));
                if(nearestAetheryte == null || P.Territory != aetheryte.Territory.RowId || Player.DistanceTo(nearestAetheryte) > SkipTeleport)
                {
                    P.TaskManager.InsertMulti(
                        new((Action)(() => S.TeleportService.TeleportToAetheryte(Aetheryte))),
                        new(() => !IsScreenReady()),
                        new(() => IsScreenReady())
                        );
                }
            });
        }
        else if(Kind == CustomAliasKind.Use_Aethernet)
        {
            P.TaskManager.Enqueue(() => IsScreenReady() && Player.Interactable, "Wait until screen ready");
            P.TaskManager.Enqueue(() =>
            {
                P.TaskManager.InsertStack(() =>
                {
                    var aethernetPoint = Utils.GetAethernetNameWithOverrides(Aetheryte);
                    TaskTryTpToAethernetDestination.Enqueue(aethernetPoint);
                });
            }, "Teleport to aethernet destination");
            P.TaskManager.Enqueue(() => !IsScreenReady(), "Wait until screen is not ready");
            P.TaskManager.Enqueue(IsScreenReady, "Wait until screen is ready");
        }
        else if(Kind == CustomAliasKind.Circular_movement)
        {
            P.TaskManager.Enqueue(() => IsScreenReady() && Player.Interactable);
            P.TaskManager.Enqueue(() => TaskMoveToHouse.UseSprint(false));
            P.TaskManager.Enqueue(() => P.FollowPath.Move([.. MathHelper.CalculateCircularMovement(CenterPoint, Player.Position.ToVector2(), CircularExitPoint.ToVector2(), out _, Precision, Tolerance, Clamp).Select(x => x.ToVector3(Player.Position.Y)).ToList(), .. (Vector3[])(WalkToExit ? [CircularExitPoint] : []), .. appendMovement], true));
            P.TaskManager.Enqueue(() => P.FollowPath.Waypoints.Count == 0);
        }
        else if(Kind == CustomAliasKind.Interact)
        {
            P.TaskManager.Enqueue(() => IsScreenReady() && Player.Interactable);
            P.TaskManager.EnqueueTask(NeoTasks.InteractWithObject(() => Svc.Objects.OrderBy(Player.DistanceTo).FirstOrDefault(x => x.IsTargetable && x.BaseId == DataID)));
        }
        else if(Kind == CustomAliasKind.Mount_Up)
        {
            P.TaskManager.Enqueue(() => IsScreenReady() && Player.Interactable);
            P.TaskManager.Enqueue(TaskMount.MountIfCan);
        }
        else if(Kind == CustomAliasKind.Select_Yes)
        {
            P.TaskManager.Enqueue(() =>
            {
                if(StopOnScreenFade && !IsScreenReady()) return true;
                if(TryGetAddonMaster<AddonMaster.SelectYesno>(out var m) && m.IsAddonReady)
                {
                    //PluginLog.Debug($"Parsed text: [{m.Text}], options: {SelectOption.Where(x => x.Length > 0).Select(Utils.ParseSheetPattern).Print("\n")}");
                    var text = m.Text;
                    // 讀到 U+FFFD ＝ 窗記憶體變動中(多半是上一個步驟按過、正在關閉),這一幀不碰。
                    if(AddonPressGuard.IsTextUnstable("SelectYesno", text)) return false;
                    // 別名鏈可連續配置兩個 Select_Yes:下一步驟在下一 tick 就會掃到仍 IsAddonReady 的關閉中同址窗,
                    // 節流 key 帶步驟 ID 互不相干擋不到 ⇒ 由 AddonPressGuard 記位址,同一扇窗只按一次。
                    if(text.ContainsAny(SelectOption.Where(x => x.Length > 0).Select(Utils.ParseSheetPattern)) && EzThrottler.Throttle($"CustomCommandSelectYesno_{ID}", 200) && AddonPressGuard.TryPressOnce("SelectYesno", m, $"CustomAlias.SelectYes({ID})"))
                    {
                        m.Yes();
                        return true;
                    }
                }
                return false;
            }, new(abortOnTimeout: false, timeLimitMS: 10000));
        }
        else if(Kind == CustomAliasKind.Select_List_Option)
        {
            P.TaskManager.Enqueue(() =>
            {
                if(StopOnScreenFade && !IsScreenReady()) return true;
                {
                    if(TryGetAddonMaster<AddonMaster.SelectString>(out var m) && m.IsAddonReady)
                    {
                        if(AddonPressGuard.AnyTextUnstable("SelectString", m.Entries.Select(x => x.Text))) return false;
                        if(Utils.TryFindEqualsOrContains(m.Entries, e => e.Text, SelectOption.Where(x => x.Length > 0).Select(Utils.ParseSheetPattern), out var e) && EzThrottler.Throttle($"CustomCommandSelectString_{ID}", 200) && AddonPressGuard.TryPressOnce("SelectString", m, $"CustomAlias.SelectListOption({ID})", paramKey: e.Index.ToString()))
                        {
                            e.Select();
                            return true;
                        }
                    }
                }
                {
                    if(TryGetAddonMaster<AddonMaster.SelectIconString>(out var m) && m.IsAddonReady)
                    {
                        if(AddonPressGuard.AnyTextUnstable("SelectIconString", m.Entries.Select(x => x.Text))) return false;
                        if(Utils.TryFindEqualsOrContains(m.Entries, e => e.Text, SelectOption.Where(x => x.Length > 0).Select(Utils.ParseSheetPattern), out var e) && EzThrottler.Throttle($"CustomCommandSelectString_{ID}", 200) && AddonPressGuard.TryPressOnce("SelectIconString", m, $"CustomAlias.SelectListOption({ID})", paramKey: e.Index.ToString()))
                        {
                            e.Select();
                            return true;
                        }
                    }
                }
                return false;
            }, new(abortOnTimeout: false, timeLimitMS: 10000));
        }
        else if(Kind == CustomAliasKind.Confirm_Contents_Finder)
        {
            P.TaskManager.Enqueue((Action)(() => EzThrottler.Throttle($"CustomCommandCFCConfirm_{ID}", 1000, true)));
            P.TaskManager.Enqueue(() =>
            {
                if(StopOnScreenFade && !IsScreenReady()) return true;
                if(TryGetAddonMaster<AddonMaster.ContentsFinderConfirm>(out var m) && m.IsAddonReady)
                {
                    if(EzThrottler.Throttle($"CustomCommandCFCConfirm_{ID}", 2000) && AddonPressGuard.TryPressOnce("ContentsFinderConfirm", m, $"CustomAlias.CFCConfirm({ID})"))
                    {
                        m.Commence();
                        return true;
                    }
                }
                return false;
            }, new(abortOnTimeout: false, timeLimitMS: 20000));
        }
    }
}
