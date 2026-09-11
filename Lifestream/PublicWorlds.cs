using ECommons.DalamudServices;
using ECommons.ExcelServices;
using Lumina.Excel.Sheets;

namespace Lifestream;

/// <summary>
/// Provides the live-world view used by Lifestream.
/// </summary>
internal static class PublicWorlds
{
    // Taiwan 7.3 ships its eight production worlds with World.IsPublic = false.
    // Keep the compatibility exception narrow so lobby and development worlds
    // outside the Taiwan production data center remain filtered out.
    internal const uint TaiwanDataCenterId = 151;
    internal const uint TaiwanFirstWorldId = 4028;
    internal const uint TaiwanLastWorldId = 4035;

    private static readonly Dictionary<string, uint> TaiwanWorldAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["伊弗利特"] = 4028,
        ["火神"] = 4028,
        ["Ifrit"] = 4028,
        ["迦樓羅"] = 4029,
        ["風神"] = 4029,
        ["Garuda"] = 4029,
        ["利維坦"] = 4030,
        ["Leviathan"] = 4030,
        ["鳳凰"] = 4031,
        ["Phoenix"] = 4031,
        ["奧汀"] = 4032,
        ["Odin"] = 4032,
        ["巴哈姆特"] = 4033,
        ["巴哈"] = 4033,
        ["Bahamut"] = 4033,
        ["拉姆"] = 4034,
        ["Ramuh"] = 4034,
        ["泰坦"] = 4035,
        ["Titan"] = 4035,
    };

    internal static bool IsTaiwanWorld(uint worldId)
        => worldId is >= TaiwanFirstWorldId and <= TaiwanLastWorldId;

    internal static bool IsTaiwanWorld(World world)
        => IsTaiwanWorld(world.RowId);

    internal static bool IsPublic(World world)
        => world.IsPublic
            || IsTaiwanWorld(world);

    /// <summary>
    /// 這個世界目前「去不了」——已停止營運，或使用者自己把它排除掉了。
    /// </summary>
    /// <remarks>
    /// 🔴 只用在「可以去哪些世界」：世界選單、<c>/li &lt;世界&gt;</c> 的比對、跨區傳送的替代
    /// 世界、角色選擇畫面的目的地，以及 <c>CanVisitSameDC</c>／<c>CanVisitCrossDC</c>
    /// 這兩個對外 IPC 判準。
    /// <para>
    /// 🔴 <b>絕對不可以</b>用在「台服有哪些世界」—— 那些地方少一個世界會讓既有資料顯示不出來，
    /// 或是讓原生陣列被少讀一列：
    /// <list type="bullet">
    /// <item><c>AddressBookEntry.IsValid</c>：既有地址簿條目的世界合法性檢查。</item>
    /// <item><c>Utils.ReplaceAddressBookRegex</c>／<c>Utils.BuildAddressBookEntry</c>／
    /// <see cref="NormalizeTaiwanWorldName"/>：世界名稱的解析與前綴比對。</item>
    /// <item><c>ReaderLobbyDKTWorldList.GetNumWorlds</c>：這個回傳值是<b>要從原生
    /// AtkArrayData 讀幾列</b>，不是政策清單。少一列＝大廳世界清單最後一個世界讀不到。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 📌 設定載入前 <c>P</c>／<c>P.Config</c> 可能還是 null，這裡容忍(視為沒有任何排除)，
    /// 行為與加這個功能之前一致。
    /// </para>
    /// </remarks>
    internal static bool IsUnavailable(uint worldId)
        => P != null && C?.UnavailableWorlds?.Contains(worldId) == true;

    internal static bool IsUnavailable(World world) => IsUnavailable(world.RowId);

    /// <summary>把一份世界清單濾成「可以去的世界」清單。所有傳送候選都應該經過這裡。</summary>
    internal static World[] Travelable(IEnumerable<World> worlds)
        => [.. worlds.Where(world => !IsUnavailable(world.RowId))];

    // ECommons 的 ExcelWorldHelper.Region 列舉只有 JP=1/NA=2/EU=3/OC=4，沒有台服的 8
    // （陸行鳥 WorldDCGroupType(151).Region == 8）。GetRegion() 是把該欄位直接轉型回來的，
    // 所以 (Region)8 在執行期拿得到、只是沒有名字 —— 任何用 Enum.GetValues<Region>()
    // 迭代地區的 UI 都會完全漏掉台服（見 Utils.DrawWorldSelector）。
    internal const byte TaiwanRegionByte = 8;

    internal static ExcelWorldHelper.Region TaiwanRegion => (ExcelWorldHelper.Region)TaiwanRegionByte;

    /// <summary>所有實際存在世界的地區，含 ECommons 列舉裡沒有的台服。</summary>
    internal static ExcelWorldHelper.Region[] AllRegions()
        => [.. Enum.GetValues<ExcelWorldHelper.Region>(), TaiwanRegion];

    /// <summary>地區的顯示名稱；未命名的台服地區 ToString() 只會印出裸數字 "8"，這裡補上名稱。</summary>
    internal static string RegionDisplayName(ExcelWorldHelper.Region region)
        => (byte)region == TaiwanRegionByte ? "Taiwan" : region.ToString();

    internal static World[] Get(ExcelWorldHelper.Region? region = null)
        => [.. Svc.Data.GetExcelSheet<World>()
            .Where(world => IsPublic(world)
                && (region == null || world.GetRegion() == region.Value))];

    internal static World[] Get(uint dataCenter)
        => dataCenter == TaiwanDataCenterId
            ? GetTaiwanWorlds()
            : [.. Svc.Data.GetExcelSheet<World>()
                .Where(world => IsPublic(world) && world.DataCenter.RowId == dataCenter)];

    internal static World[] GetTaiwanWorlds()
        => [.. Svc.Data.GetExcelSheet<World>()
            .Where(IsTaiwanWorld)
            .OrderBy(world => world.RowId)];

    internal static string NormalizeTaiwanWorldName(string input)
    {
        var trimmed = input.Trim();
        if(!TaiwanWorldAliases.TryGetValue(trimmed, out var worldId))
            return trimmed;

        var world = Svc.Data.GetExcelSheet<World>().GetRowOrDefault(worldId);
        return world?.Name.ToString() ?? trimmed;
    }
}
