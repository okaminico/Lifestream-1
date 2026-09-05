using Dalamud.Plugin.Ipc.Exceptions;

namespace Lifestream.IPC;

/// <summary>
/// 單向橋接到「塔塔露誇獎」(TataruPraise)：一整條 /li 傳送／導航鏈真的抵達目的地時請它念一句。
/// </summary>
/// <remarks>
/// 🔴 <b>零組件相依。</b>只用 Dalamud 原生 CallGate 的字串契約，對方沒安裝時本檔的每一條路徑都是 no-op。
/// <para>
/// 🔴 契約名與情境鍵逐字取自 TataruPraise 的 <c>IpcContract.cs</c> 與 <c>Core/PraiseCategory.cs</c>。
/// CallGate 是純字串比對，名字打錯不會有任何錯誤訊息，只會永遠得到「這個頻道沒有人註冊」——
/// <b>靜默斷線</b>。所以三個字串都寫成常數，不散在呼叫點上。
/// </para>
/// <para>
/// 🔴 <b>只能從主執行緒(framework tick / Draw)呼叫。</b>IPC 的實作是在呼叫端的執行緒上跑的，
/// 從背景 Task 叫過去等於把對方的程式碼拉到背景執行緒。唯一的呼叫點
/// (<see cref="Tasks.Utility.TaskAnnounceArrival"/>) 是一個 NeoTaskManager 任務，
/// 而 NeoTaskManager 的 Tick 掛在 <c>Svc.Framework.Update</c> 上。
/// </para>
/// <para>
/// ⚠️ 這是<b>單向通知</b>：回傳值只拿來寫記錄，不影響 Lifestream 的任何流程，
/// 也不因為對方回 false 而重試。回 false 的正常情形包括：總開關關著、還在冷卻、
/// 上一句還在播、這個情境沒有任何已合成語音的句子。
/// </para>
/// </remarks>
internal static class TataruPraiseIPC
{
    /// <summary><c>Func&lt;bool&gt;</c>：總開關開著而且真的有可播的內容。</summary>
    internal const string TagIsAvailable = "TataruPraise.IsAvailable";

    /// <summary>
    /// <c>Func&lt;string, bool&gt;</c>：<b>指定的那個情境</b>現在出得了聲嗎
    /// （總開關開著＋這個情境沒被關掉＋這個情境至少有一句已合成的語音）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>閘門要問的是這一個，不是 <see cref="TagIsAvailable"/>。</b>後者問的是
    /// 「整池<b>有某個情境</b>播得出來」，於是「別的情境有語音、<b>抵達</b>一句都沒有」時
    /// 照樣通過，接著 <c>Praise</c> 回 <c>false</c>——呼叫端就分不出「不能出聲」與「這次剛好沒出聲」。
    /// <para>
    /// 📌 它刻意<b>不看冷卻</b>：冷卻是「這次剛好不出聲」，不是「不能出聲」。
    /// </para>
    /// <para>
    /// 🔴 舊版 TataruPraise 沒有註冊這個端點，<c>InvokeFunc</c> 會擲 <c>IpcNotReadyError</c>，
    /// 剛好落進既有的 catch＝安靜不出聲，這是正確的 fail-safe。
    /// <b>失敗時絕不可以退回去叫 <see cref="TagIsAvailable"/></b>——那樣就把這個端點的意義整個抵銷掉了。
    /// </para>
    /// </remarks>
    internal const string TagIsAvailableFor = "TataruPraise.IsAvailableFor";

    /// <summary><c>Func&lt;string, bool&gt;</c>：從指定情境的誇獎池挑一句念。</summary>
    internal const string TagPraise = "TataruPraise.Praise";

    /// <summary>
    /// 送過去的情境字串。
    /// ⚠️ TataruPraise 拿這個字串當 <c>pool.json</c> 的鍵，<b>對不上就靜默不出聲</b>
    /// (它會寫一行 Information 說未知情境，同一個情境只印一次)。
    /// </summary>
    internal const string CategoryArrived = "抵達";

    /// <summary>
    /// 請塔塔露念一句「抵達」。對方沒裝、關著、或池裡沒東西，這裡都是安靜的 no-op。
    /// </summary>
    /// <param name="reason">寫進記錄用的來源描述，讓 log 分得出是哪一條鏈叫的。</param>
    internal static void TryPraiseArrival(string reason)
    {
        if(!C.TataruPraiseOnArrival) return;

        try
        {
            // 先問 IsAvailableFor(「抵達」)：對方的總開關關著、這個情境被使用者關掉、或這個
            // 情境一句已合成的都沒有，就不要浪費它的冷卻。
            if(!Svc.PluginInterface.GetIpcSubscriber<string, bool>(TagIsAvailableFor).InvokeFunc(CategoryArrived)) return;

            var accepted = Svc.PluginInterface.GetIpcSubscriber<string, bool>(TagPraise).InvokeFunc(CategoryArrived);
            // Information 級：這是「使用者說沒出聲」時唯一問得出真相的一行(使用者跑 LogLevel 1)。
            PluginLog.Information($"[TataruPraise] {reason}：Praise(「{CategoryArrived}」) 回傳 {accepted}。");
        }
        catch(IpcNotReadyError)
        {
            // 對方沒安裝／沒載入。這是完全正常的狀態，刻意不寫 log——沒裝的人每趟都會走到這裡。
        }
        catch(Exception e)
        {
            PluginLog.Information($"[TataruPraise] 呼叫失敗({reason})：{e.Message}");
        }
    }
}
