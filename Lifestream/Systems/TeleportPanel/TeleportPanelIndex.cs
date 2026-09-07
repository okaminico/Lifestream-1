using ECommons.ExcelServices;
using ECommons.GameHelpers;
using System.Threading;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lifestream.Systems.Legacy;
using Lifestream.Tasks.SameWorld;
using Lumina.Excel.Sheets;

namespace Lifestream.Systems.TeleportPanel;

/// <summary>
/// 傳送面板的一個可選目的地。可能是主水晶(走 Telepo)或城內乙太之光(走乙太之光網路)。
/// ⚠️ 這裡只存 ID 與靜態表資料，不存任何原生指標，也不存 IGameObject。
/// </summary>
public sealed class TeleportPanelEntry
{
    /// <summary>Aetheryte 表的 RowId。這也是 <see cref="Data.Config.Favorites"/> /
    /// <see cref="Data.Config.Renames"/> / <see cref="Data.Config.AetheryteLandings"/> 的鍵，
    /// 與 DailyRoutines BetterTeleport 使用的鍵**完全相同**，所以設定可以直接沿用。</summary>
    public uint Id;

    /// <summary>房屋類乙太之光的子索引(私人房屋/公會房屋/公寓)。主水晶恆為 0。</summary>
    public byte SubIndex;

    /// <summary>true = 主水晶(可直接 Telepo)；false = 城內乙太之光(要走乙太之光網路)。</summary>
    public bool IsAetheryte;

    /// <summary>城內乙太之光所屬的主水晶 RowId；主水晶自身為 0。</summary>
    public uint MasterId;

    public uint Territory;
    public uint MapId;
    public string Name = "";
    public string ZoneName = "";
    public string RegionName = "";
    public uint GilCost;

    /// <summary>乙太之光本體在世界中的座標(來自地圖標記，Y 多半是 0)。解析不到時為 null。</summary>
    public Vector3? Position;

    /// <summary>套用使用者備註後的顯示名稱(備註會取代原名，與 DR 的行為一致)。</summary>
    public string DisplayName => C.Renames.TryGetValue(Id, out var v) && v != "" ? v : Name;

    /// <summary>
    /// 搜尋比對用的靜態字串(原名 + 區域名 + 地區名)，建索引時就組好。
    /// ⚠️ 刻意**不**用屬性每次組字串：清單是逐幀重畫的，那等於每幀配置數百個字串。
    /// 會變動的備註改在 <see cref="Matches"/> 裡另外比對，不需要重組。
    /// </summary>
    public string SearchText = "";

    public bool Matches(string search)
        => SearchText.Contains(search, StringComparison.OrdinalIgnoreCase)
        || (C.Renames.TryGetValue(Id, out var v) && v.Contains(search, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 建立並快取傳送面板的目的地清單。
///
/// 資料來源刻意分成兩半，因為兩者的「可傳送」定義不同：
///   - 主水晶：直接列舉 <see cref="Svc.AetheryteList"/>。它就是遊戲自己認定「你現在能傳送到哪」的
///     權威清單，還附帶正確的 SubIndex(房屋)與傳送費，不必自己推。
///   - 城內乙太之光：<see cref="Svc.AetheryteList"/> 不含它們，改由 Lifestream 既有的
///     <see cref="DataStore.Aetherytes"/> 取得，再用 <see cref="UIState.IsAetheryteUnlocked"/> 過濾。
///     這跟 TaskGotoDestination 判斷解鎖的方法一致(只讀 UIState 的解鎖點陣圖，無特徵碼、無 hook)。
/// </summary>
public static unsafe class TeleportPanelIndex
{
    /// <summary>
    /// 索引快取。🔴 **只整份替換，絕不就地修改** —— 讀端（可能是別的外掛的執行緒）拿到的一律是
    /// 一份之後不會再變動的完整快照，所以讀端不需要鎖。參照指派本身是原子的，用
    /// <see cref="Volatile"/> 是為了保證寫端「先把內容填好、最後才公開參照」不會被重排序。
    /// </summary>
    private static List<TeleportPanelEntry> Cache;

    /// <summary>快取還沒建好時回傳的東西。共用一份即可 —— 它永遠是空的，也永遠不會被寫入。</summary>
    private static readonly List<TeleportPanelEntry> EmptyIndex = [];

    /// <summary>這份快取是替哪個角色建的。換角之後上一個角色的清單一律作廢。</summary>
    private static ulong CacheBuiltForCid;

    /// <summary>建快取當下 <see cref="Svc.AetheryteList"/> 的長度。變了就代表解鎖了新主水晶／房屋有異動。</summary>
    private static int CacheBuiltForAetheryteCount = -1;

    private static long CacheBuiltAt;
    private static int CacheEntryCount = -1;
    private static bool ColdReadReported;

    /// <summary>
    /// 週期性重建間隔。城內乙太之光的共鳴**沒有任何事件可掛**（只能讀 UIState 的點陣圖），
    /// 而那個判斷是幾百次迴圈，不能每幀做 —— 所以定期回頭看一眼。
    /// </summary>
    private const long PeriodicRefreshMs = 60000;

    /// <summary>乙太之光座標查一次就永久快取：那是靜態遊戲資料，而且逐幀重算會全表掃 MapMarker。</summary>
    /// <remarks>⚠️ 這張表沒有同步。它只從 <see cref="Build"/> 與 framework 執行緒上的
    /// <see cref="GetPosition"/> 被碰到，而前者現在保證只在 framework 執行緒跑。</remarks>
    private static readonly Dictionary<uint, Vector3?> PositionCache = [];

    /// <summary>使快取失效。🔴 只能從 framework 執行緒呼叫（現有呼叫點都是開窗／設定頁）。</summary>
    public static void Invalidate() => Volatile.Write(ref Cache, null);

    /// <summary>
    /// 取得索引。快取冷的時候會就地重建 —— <b>但只有在 framework 執行緒上</b>。
    /// <para>
    /// 🔴 為什麼要分執行緒：<see cref="Build"/> 會讀 <see cref="Svc.AetheryteList"/>（原生支撐的清單）
    /// 與 <see cref="UIState.Instance"/>（裸原生指標）。而 Lifestream 的 IPC 端點是跑在
    /// **呼叫端的執行緒**上的（Dalamud 的 CallGate 不做 marshal），舊版的 <c>Cache ??= Build()</c>
    /// 等於讓別的外掛在自己的執行緒上讀遊戲記憶體 —— 失敗形式是崩潰，不是拿到舊值。
    /// 順帶一提 <c>??=</c> 本身也不是原子的，兩個執行緒會各建一份、還一起寫 <see cref="PositionCache"/>。
    /// </para>
    /// 不在 framework 執行緒時退回 <see cref="GetSnapshot"/>：只讀既有快照，冷的時候回空清單。
    /// </summary>
    public static List<TeleportPanelEntry> Get()
    {
        if(!Svc.Framework.IsInFrameworkUpdateThread) return GetSnapshot();
        var cache = Volatile.Read(ref Cache);
        return cache ?? Rebuild("on demand");
    }

    /// <summary>
    /// 任何執行緒都可以呼叫：**只讀既有快照，絕不重建**。
    /// 快取冷的時候回傳空清單（不是 null），並寫一次 Information 說明是怎麼回事。
    /// </summary>
    public static List<TeleportPanelEntry> GetSnapshot()
    {
        var cache = Volatile.Read(ref Cache);
        if(cache != null) return cache;
        if(!ColdReadReported)
        {
            ColdReadReported = true;
            PluginLog.Information("[TeleportPanel] 傳送面板索引還沒建好就有人來取用（多半是別的外掛透過 IPC 呼叫，而當下還在讀取畫面或角色選擇畫面）。這一次回傳空清單；登入後索引會在下一個遊戲幀建好，請呼叫端稍後再試一次。");
        }
        return EmptyIndex;
    }

    /// <summary>
    /// 每幀從 <c>Framework_Update</c> 呼叫。索引的重建**只會**發生在這裡或 framework 執行緒上的
    /// <see cref="Get"/>，兩者都在 framework 執行緒 —— 這就是「原生讀取不離開 framework 執行緒」的閘門。
    /// </summary>
    internal static void Tick()
    {
        if(!Player.Available)
        {
            // 登出／角色選擇畫面：上一個角色的清單不能繼續拿來回答 IPC。
            if(Volatile.Read(ref Cache) != null)
            {
                Invalidate();
                CacheBuiltForCid = 0;
                CacheBuiltForAetheryteCount = -1;
                CacheEntryCount = -1;
                ColdReadReported = false;
            }
            return;
        }

        var reason = WhyRebuild();
        if(reason == null) return;
        try
        {
            Rebuild(reason);
        }
        catch(Exception e)
        {
            // 🔴 Framework_Update 沒有 try/catch，這裡讓例外逃出去會打斷同一幀後面所有的邏輯。
            //    放一份空快取進去，避免每幀重試洗版；下一次週期性重建會再試一次。
            PluginLog.Error($"[TeleportPanel] 重建索引失敗（{e.GetType().Name}）：{e.Message}");
            Publish(EmptyIndex);
        }
    }

    /// <summary>要不要重建，以及為什麼。回 null＝不用重建。</summary>
    private static string WhyRebuild()
    {
        if(Volatile.Read(ref Cache) == null) return "cold";
        if(CacheBuiltForCid != Player.CID) return "character changed";
        var count = AetheryteListLength();
        if(count >= 0 && count != CacheBuiltForAetheryteCount) return "aetheryte list changed";
        if(Environment.TickCount64 - CacheBuiltAt >= PeriodicRefreshMs) return "periodic";
        return null;
    }

    /// <summary>🔴 只能在 framework 執行緒呼叫。</summary>
    private static List<TeleportPanelEntry> Rebuild(string reason)
    {
        var built = Build();
        var changed = built.Count != CacheEntryCount;
        Publish(built);
        // 週期性重建在內容沒變時不寫 log:那會變成每分鐘一行的純噪音。
        if(changed || reason != "periodic")
        {
            PluginLog.Debug($"[TeleportPanel] Built index ({reason}): {built.Count(x => x.IsAetheryte)} aetherytes, {built.Count(x => !x.IsAetheryte)} aethernet shards.");
        }
        return built;
    }

    /// <summary>
    /// 公開一份新快取。🔴 順序是刻意的：所有描述欄位先寫好，**參照最後才公開** ——
    /// 讀端只要看到非 null 的 <see cref="Cache"/>，內容就一定是完整的。
    /// </summary>
    private static void Publish(List<TeleportPanelEntry> built)
    {
        CacheBuiltForCid = Player.Available ? Player.CID : 0;
        CacheBuiltForAetheryteCount = AetheryteListLength();
        CacheBuiltAt = Environment.TickCount64;
        CacheEntryCount = built.Count;
        Volatile.Write(ref Cache, built);
        ColdReadReported = false;
    }

    /// <summary>解鎖的主水晶／房屋筆數。取不到時回 -1（＝這個判準這次不參與決策）。</summary>
    private static int AetheryteListLength()
    {
        try
        {
            return Svc.AetheryteList.Length;
        }
        catch(Exception e)
        {
            PluginLog.Debug($"[TeleportPanel] Could not read AetheryteList.Length: {e.Message}");
            return -1;
        }
    }

    /// <summary>
    /// 🔴 **只能在 framework 執行緒呼叫** —— 它讀 <see cref="Svc.AetheryteList"/> 與
    /// <see cref="UIState.Instance"/>（裸原生指標）。唯一的兩個呼叫點是 <see cref="Get"/>
    /// （已判過執行緒）與 <see cref="Tick"/>（本來就在 framework 上）。
    /// </summary>
    private static List<TeleportPanelEntry> Build()
    {
        var result = new List<TeleportPanelEntry>();
        var aetheryteSheet = Svc.Data.GetExcelSheet<Aetheryte>();
        var territorySheet = Svc.Data.GetExcelSheet<TerritoryType>();

        // ── 主水晶(含房屋) ────────────────────────────────────────────────────────
        try
        {
            foreach(var entry in Svc.AetheryteList)
            {
                var data = entry.AetheryteData.ValueNullable;
                if(data == null) continue;
                var e = new TeleportPanelEntry
                {
                    Id = entry.AetheryteId,
                    SubIndex = entry.SubIndex,
                    IsAetheryte = true,
                    Territory = entry.TerritoryId,
                    GilCost = (uint)entry.GilCost,
                };
                FillNames(e, data.Value, territorySheet);
                // 同一個 RowId 會因為房屋子索引出現多筆，名稱要能分辨是哪一間。
                if(entry.SubIndex > 0)
                {
                    e.Name = entry.Ward > 0
                        ? $"{e.Name} ({"Ward".Loc()} {entry.Ward}, {(entry.Plot > 0 ? $"{"Plot".Loc()} {entry.Plot}" : $"#{entry.SubIndex}")})"
                        : $"{e.Name} #{entry.SubIndex}";
                }
                e.Position = GetPosition(e.Id);
                FinishEntry(e);
                result.Add(e);
            }
        }
        catch(Exception ex)
        {
            PluginLog.Warning($"[TeleportPanel] Could not enumerate AetheryteList: {ex.Message}");
        }

        // ── 城內乙太之光 ─────────────────────────────────────────────────────────
        // DataStore 是由 SingletonServiceManager 在啟動排程裡建立的。理論上視窗不可能比它早畫，
        // 但寧可少列一段清單也不要在 Draw 裡丟 NRE。
        var uiState = UIState.Instance();
        if(uiState != null && S.Data.DataStore?.Aetherytes != null)
        {
            foreach(var (master, children) in S.Data.DataStore.Aetherytes)
            {
                foreach(var child in children)
                {
                    // 選單上不會出現的隱藏節點(飛空艇著陸場之類)不能當目的地
                    if(child.Invisible) continue;
                    if(!uiState->IsAetheryteUnlocked(child.ID)) continue;
                    if(!aetheryteSheet.TryGetRow(child.ID, out var row)) continue;

                    var e = new TeleportPanelEntry
                    {
                        Id = child.ID,
                        SubIndex = 0,
                        IsAetheryte = false,
                        MasterId = master.ID,
                        Territory = child.TerritoryType,
                    };
                    FillNames(e, row, territorySheet);
                    if(e.Name == "") e.Name = child.Name;
                    e.Position = GetPosition(e.Id);
                    FinishEntry(e);
                    result.Add(e);
                }
            }
        }

        AddGatewayDestinations(result, territorySheet, uiState);

        return result;
    }

    /// <summary>
    /// 蒼天街與渴望灣 —— 以及蒼天街自己的城內乙太之光。
    ///
    /// 🔑 為什麼上面兩段都撈不到它們:這兩個區域在 <c>Aetheryte</c> 表裡**一列都沒有**
    /// (台服 7.20 全表查證:Territory 欄等於 886 或 1237 的列數為 0)。
    /// <see cref="Svc.AetheryteList"/> 自然沒有,<see cref="DataStore.Aetherytes"/> 也沒有 ——
    /// 它們是靠母城乙太之光選單裡的專用項目進去的,Lifestream 用偽 id 自己建模
    /// (見 <see cref="TaskAetheryteAethernetTeleport.GatewayRoutes"/> 與
    /// <see cref="Systems.Custom.CustomAethernet"/>)。
    ///
    /// 後果不只是「清單短一截」:我的最愛的鍵就是這裡的 <see cref="TeleportPanelEntry.Id"/>，
    /// 列不出來就等於**加不進我的最愛** —— 這正是這一段要修的問題。
    ///
    /// <see cref="TeleportPanelEntry.MasterId"/> 一律填母城乙太之光(蒼天街→伊修加爾德下層 70、
    /// 渴望灣→最佳威兔洞 175),也就是「歸屬」的實際意義:傳送時就是從那一座走。
    /// ⚠️ 顯示上的地區/區域分組仍照實填(蒼天街在庫爾札斯、渴望灣在星外天域),
    /// 沒有把它們假裝成母城的一部分 —— 那會讓地圖預覽與「必須在某區域」的落點判斷全部對不上。
    /// </summary>
    private static void AddGatewayDestinations(List<TeleportPanelEntry> result,
        Lumina.Excel.ExcelSheet<TerritoryType> territorySheet, UIState* uiState)
    {
        if(uiState == null) return;
        foreach(var route in TaskAetheryteAethernetTeleport.GatewayRoutes)
        {
            // 這兩個開關既有的語意就是「要不要把這個地點掛進母城乙太之光的清單」，沿用它，
            // 預設(都是開)等於這些目的地直接出現，不需要使用者再去設定裡找開關。
            if(!route.IsEnabled()) continue;
            // 進得去的前提是先傳送到母城乙太之光。沒共鳴就列不得 ——
            // 判斷方式與上面城內乙太之光那段一致(只讀 UIState 的解鎖點陣圖)。
            if(!uiState->IsAetheryteUnlocked(route.RootAetheryteId)) continue;

            var e = new TeleportPanelEntry
            {
                Id = route.AethernetId,
                SubIndex = 0,
                IsAetheryte = false,
                MasterId = route.RootAetheryteId,
                Territory = route.DestinationTerritory,
            };
            // 名稱留空 → FillTerritoryNames 會用區域名(蒼天街 / 渴望灣)，那正是選單上的說法。
            FillTerritoryNames(e, territorySheet);
            FinishEntry(e);
            result.Add(e);

            // 玄關區域內部自己的乙太之光網路。目前只有蒼天街有(渴望灣沒有節點，查無此territory)。
            if(S.Data.CustomAethernet?.ZoneInfo == null) continue;
            if(!S.Data.CustomAethernet.ZoneInfo.TryGetValue(route.DestinationTerritory, out var zone)) continue;
            foreach(var shard in zone.Aetherytes)
            {
                var s = new TeleportPanelEntry
                {
                    Id = shard.ID,
                    SubIndex = 0,
                    IsAetheryte = false,
                    MasterId = route.RootAetheryteId,
                    Territory = shard.TerritoryType,
                    Name = shard.Name,
                    // CustomAetheryte.Position 存的是世界座標的 XZ(與 IGameObject.Position.ToVector2()
                    // 比對用)，補成 Vector3 才能餵給地圖預覽與「是不是就站在這裡」的判斷。
                    Position = new Vector3(shard.Position.X, 0f, shard.Position.Y),
                };
                FillTerritoryNames(s, territorySheet);
                FinishEntry(s);
                result.Add(s);
            }
        }
    }

    private static void FillNames(TeleportPanelEntry e, Aetheryte row, Lumina.Excel.ExcelSheet<TerritoryType> territorySheet)
    {
        // 主水晶用 PlaceName，城內乙太之光用 AethernetName —— 兩者只會有一個非空。
        var place = row.PlaceName.ValueNullable?.Name.ToString() ?? "";
        var aethernet = row.AethernetName.ValueNullable?.Name.ToString() ?? "";
        e.Name = row.IsAetheryte ? (place != "" ? place : aethernet) : (aethernet != "" ? aethernet : place);

        FillTerritoryNames(e, territorySheet);
    }

    /// <summary>
    /// 依 <see cref="TeleportPanelEntry.Territory"/> 補上地圖 id、區域名、地區名。
    /// <see cref="TeleportPanelEntry.Name"/> 只在**還是空的**時候才用區域名補 ——
    /// 呼叫端已經填了名字(城內乙太之光)就不能蓋掉。
    /// </summary>
    private static void FillTerritoryNames(TeleportPanelEntry e, Lumina.Excel.ExcelSheet<TerritoryType> territorySheet)
    {
        if(territorySheet.TryGetRow(e.Territory, out var terr))
        {
            e.MapId = terr.Map.RowId;
            e.ZoneName = terr.PlaceName.ValueNullable?.Name.ToString() ?? "";
            e.RegionName = terr.PlaceNameRegion.ValueNullable?.Name.ToString() ?? "";
        }
        if(e.RegionName == "") e.RegionName = "Other".Loc();
        if(e.Name == "") e.Name = e.ZoneName != "" ? e.ZoneName : $"#{e.Id}";
    }

    /// <summary>建索引的最後一步：把搜尋字串組好，之後逐幀比對就不用再配置字串。</summary>
    private static void FinishEntry(TeleportPanelEntry e)
        => e.SearchText = $"{e.Name}\n{e.ZoneName}\n{e.RegionName}";

    /// <summary>
    /// 乙太之光的世界座標。ECommons 的 <see cref="ECommons.GameHelpers.Map.AetherytePosition"/>
    /// 對沒有 Level 資料的節點會全表掃 MapMarker，所以查一次就快取(null 也快取，避免重複丟例外)。
    /// </summary>
    public static Vector3? GetPosition(uint aetheryteId)
    {
        if(PositionCache.TryGetValue(aetheryteId, out var cached)) return cached;
        Vector3? pos;
        try
        {
            pos = ECommons.GameHelpers.Map.AetherytePosition(aetheryteId);
        }
        catch(Exception e)
        {
            PluginLog.Debug($"[TeleportPanel] Could not resolve position of aetheryte {aetheryteId}: {e.Message}");
            pos = null;
        }
        PositionCache[aetheryteId] = pos;
        return pos;
    }
}
