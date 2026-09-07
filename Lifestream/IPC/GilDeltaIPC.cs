using Dalamud.Plugin.Ipc.Exceptions;
using Lumina.Excel.Sheets;

namespace Lifestream.IPC;

/// <summary>
/// 單向橋接到 GilDelta(金幣流水):Lifestream 每次真的送出一次<b>要錢的</b>主水晶傳送之前,
/// 先告訴 GilDelta「接下來那筆自己錢包的減少是傳送費」,讓那筆 -N 記成「傳送」而不是「其他」。
/// </summary>
/// <remarks>
/// 🔴 <b>零組件相依。</b>只用 Dalamud 原生 CallGate 的字串契約;GilDelta 沒安裝時本檔的每一條路徑都是 no-op。
/// <para>
/// 🔴 契約逐字取自 GilDelta 的 <c>Ipc/GilDeltaIpc.cs</c>:端點 <c>GilDelta.Hint</c>、
/// 簽章 <c>Func&lt;string, string, int, bool&gt;</c>;分類名取自 <c>Events/GilEventCategory.cs</c>
/// 的 <c>Teleport</c>(大小寫不拘,但<b>不接受數字形式</b>)。
/// CallGate 是純字串比對,名字打錯不會有任何錯誤訊息,只會永遠得到「這個頻道沒有人註冊」——
/// <b>靜默斷線</b>。所以每一個字串都寫成常數,不散在呼叫點上。
/// </para>
/// <para>
/// 🔴 <c>note</c> <b>必須自帶來源前綴</b>:Dalamud 不會告訴 GilDelta 是誰打過來的,
/// 這個字串是唯一能記下出處的地方。
/// </para>
/// <para>
/// 🔴 <b>只能從遊戲主執行緒呼叫。</b>IPC 的實作跑在呼叫端的執行緒上,而這裡還要讀 Lumina 表。
/// 唯一的呼叫點是 <see cref="Services.TeleportService"/> 送出 <c>Telepo::Teleport</c> 的那一行之前;
/// 該方法的所有上游都在 framework 執行緒上(NeoTaskManager 的 Tick 掛在 <c>Svc.Framework.Update</c>、
/// 指令處理、Draw,以及走 <see cref="IpcFrameworkGate"/> 的 <c>Lifestream.Teleport</c> 端點)。
/// </para>
/// <para>
/// ⚠️ 這是<b>單向通知,零行為改變</b>:回傳值只拿來寫記錄,不影響 Lifestream 的任何流程,
/// 也不因為對方回 false 而重試。傳送最後沒扣到錢(免費傳送目的地、用了傳送券、施法被打斷)
/// 也不必收回提示——GilDelta 只在「自己的錢包<b>減少</b>」時才會用掉它(<c>Events/HintScope.cs</c>),
/// 用不到就在 TTL 到期時自己消失。
/// </para>
/// <para>
/// ⚠️ 乙太網(<c>TelepotTown</c>)、住宅乙太網、同區乙太之光傳送<b>不走這裡</b>——
/// 它們不花錢,也不經過 <c>Telepo::Teleport</c>。
/// </para>
/// </remarks>
internal static class GilDeltaIPC
{
    /// <summary><c>Func&lt;string, string, int, bool&gt;</c>:category / note / ttlMs → 有沒有被收下。</summary>
    internal const string TagHint = "GilDelta.Hint";

    /// <summary>
    /// GilDelta <c>GilEventCategory</c> 的成員名。對不上會被拒絕(回 false),
    /// 而且對方那側每一個相異的錯值只寫一行 Information——所以打錯是查得到的,但很安靜。
    /// </summary>
    internal const string CategoryTeleport = "Teleport";

    /// <summary>來源前綴。GilDelta 不知道是誰呼叫的,只能靠這個字串。</summary>
    internal const string NotePrefix = "Lifestream:";

    /// <summary>
    /// 提示的有效期。傳送動畫加讀圖偶爾會超過 10 秒,所以不能設太短;設太長則會把之後
    /// 一筆不相干的支出吃掉。15 秒是兩邊的折衷。
    /// ⚠️ 必須 &gt; 0:GilDelta 對 0 與負數一律直接拒絕(<c>GilHintStore.Submit</c>)。
    /// </summary>
    internal const int HintTtlMs = 15000;

    /// <summary>
    /// 已經寫過一行失敗診斷了。只在遊戲主執行緒上讀寫(見類別註解)。
    /// ⚠️ 一次傳送寫一行就是洗版,所以整場遊戲只寫第一次。
    /// </summary>
    private static bool FailureReported;

    /// <summary>
    /// 告訴 GilDelta「接下來那筆自己錢包的減少是傳送費」。
    /// 對方沒裝、拒絕、或擲例外,這裡都是安靜的 no-op,呼叫端不必檢查回傳值。
    /// </summary>
    /// <param name="aetheryteId"><c>Aetheryte</c> 表的 row id,只拿來查名字。</param>
    /// <param name="subIndex">住宅區之類的子索引。非 0 才會附在名字後面。</param>
    internal static void HintTeleportFee(uint aetheryteId, uint subIndex)
    {
        try
        {
            var note = NotePrefix + DescribeDestination(aetheryteId, subIndex);
            var accepted = Svc.PluginInterface
                .GetIpcSubscriber<string, string, int, bool>(TagHint)
                .InvokeFunc(CategoryTeleport, note, HintTtlMs);
            if(!accepted) Report($"GilDelta 沒有收下傳送歸因提示(note:{note})。");
        }
        catch(IpcNotReadyError)
        {
            // GilDelta 沒安裝／沒載入。這是完全正常的狀態,刻意不寫 log——
            // 沒裝的人每一次傳送都會走到這裡。
        }
        catch(Exception e)
        {
            Report($"送出傳送歸因提示失敗:{e.Message}");
        }
    }

    /// <summary>整場遊戲只寫第一行;診斷不可以變成需要診斷的東西。</summary>
    private static void Report(string message)
    {
        if(FailureReported) return;
        FailureReported = true;
        // Information 級:這是「傳送費怎麼還是記成其他」時唯一問得出真相的一行。
        PluginLog.Information($"[GilDelta] {message} 本次遊戲不再重複這行。");
    }

    /// <summary>
    /// 目的地名。主水晶用 <c>PlaceName</c>、城內乙太之光用 <c>AethernetName</c>,
    /// 兩個都空(台服未實裝的列)就退回 <c>#id</c>——寧可給 id 也不要給空字串。
    /// </summary>
    private static string DescribeDestination(uint aetheryteId, uint subIndex)
    {
        var row = Svc.Data.GetExcelSheet<Aetheryte>().GetRowOrDefault(aetheryteId);
        var name = row?.PlaceName.ValueNullable?.Name.ToString() is { Length: > 0 } pn ? pn
            : row?.AethernetName.ValueNullable?.Name.ToString() is { Length: > 0 } an ? an
            : $"#{aetheryteId}";
        return subIndex == 0 ? name : $"{name} #{subIndex}";
    }
}
