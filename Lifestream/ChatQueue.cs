using Dalamud.Plugin.Services;
using ECommons.ChatMethods;
using System.Collections.Concurrent;

namespace Lifestream;

/// <summary>
/// Lifestream 自己的聊天欄／錯誤浮報輸出閘口。
/// <para>
/// 🔴 為什麼需要它：<c>IChatGui.Print</c> 背後是 Dalamud 的一個佇列，本 pin 的實機版本用的是
/// 裸 <c>Queue&lt;T&gt;</c>（<c>Dalamud/Game/Gui/ChatGui.cs</c>），<c>IToastGui.ShowError</c> 也一樣。
/// 從 framework 以外的執行緒直接送進去是 racy 的：失敗形式不是「訊息晚一點出現」，
/// 而是**佇列本身壞掉**（丟訊息、重複、甚至讓整個聊天輸出停擺）。
/// </para>
/// <para>
/// 而 Lifestream 的 IPC 端點（<c>Lifestream.ExecuteCommand</c>、<c>GoToMapPoint</c>、
/// <c>TPAndChangeWorld</c>、<c>ChangeWorld</c> …）是跑在**呼叫端的執行緒**上的
/// —— Dalamud 的 CallGate 不做任何 marshal。這些端點同步可達的聊天輸出就會踩到上面那條。
/// </para>
/// <para>
/// 🔑 做法：所有訊息先進 <see cref="ConcurrentQueue{T}"/>，由 <c>Framework.Update</c> 排乾。
/// 已經在 framework 執行緒上時**當場排乾**（先進佇列再排乾），所以：
/// <list type="bullet">
/// <item>既有的任務鏈（NeoTaskManager 跑在 framework 上）行為逐字不變 —— 一樣是當下那一行就印出去；</item>
/// <item>順序永遠是 FIFO，不會因為「有些走佇列有些直送」而亂序。</item>
/// </list>
/// ⚠️ 刻意**不用**逐則 <c>RunOnFrameworkThread</c>：<c>ThreadBoundTaskScheduler</c> 不保序，
/// 連續三行訊息可能倒著出現。
/// </para>
/// <para>
/// 📌 <c>DuoLog.*</c> 與 <c>Notify.*</c> 不必改 —— ECommons 那兩支自己就包了 <c>TickScheduler</c>。
/// </para>
/// </summary>
internal static class ChatQueue
{
    private enum OutputKind
    {
        Chat,
        ErrorToast,
    }

    private readonly record struct Pending(OutputKind Kind, UIColor Color, string Text);

    /// <summary>待送上限。framework 停擺（讀取畫面／卸載中）時避免無限成長。</summary>
    private const int MaxPending = 512;

    private static readonly ConcurrentQueue<Pending> Pendings = new();
    private static readonly object SubscriptionLock = new();
    private static bool Subscribed;
    private static bool OverflowReported;

    /// <summary>在外掛進入點呼叫一次。重複呼叫是安全的。</summary>
    internal static void Enable()
    {
        lock(SubscriptionLock)
        {
            if(Subscribed) return;
            Svc.Framework.Update += OnFrameworkUpdate;
            Subscribed = true;
        }
    }

    /// <summary>卸載時呼叫。重複呼叫是安全的。</summary>
    internal static void Disable()
    {
        lock(SubscriptionLock)
        {
            if(!Subscribed) return;
            Svc.Framework.Update -= OnFrameworkUpdate;
            Subscribed = false;
        }
    }

    internal static void Red(string text) => Post(OutputKind.Chat, UIColor.Red, text);
    internal static void Orange(string text) => Post(OutputKind.Chat, UIColor.Orange, text);
    internal static void Yellow(string text) => Post(OutputKind.Chat, UIColor.Yellow, text);
    internal static void Green(string text) => Post(OutputKind.Chat, UIColor.Green, text);
    internal static void PrintColored(UIColor color, string text) => Post(OutputKind.Chat, color, text);

    /// <summary>遊戲內建的紅色錯誤浮報。走的是 Dalamud 另一個（同樣是裸佇列的）通道。</summary>
    internal static void ErrorToast(string text) => Post(OutputKind.ErrorToast, UIColor.Red, text);

    private static void Post(OutputKind kind, UIColor color, string text)
    {
        if(text == null) return;
        if(Pendings.Count >= MaxPending)
        {
            // 靜默丟訊息是最難查的失敗形式，所以一定要留下痕跡；但不能每則都寫，
            // 否則塞爆的當下會連 log 一起洗版。排乾之後才會再報一次。
            if(!OverflowReported)
            {
                OverflowReported = true;
                PluginLog.Warning($"[ChatQueue] 待送訊息已達上限 {MaxPending}，後續訊息會被丟棄直到佇列排乾。第一則被丟棄的是：{text}");
            }
            return;
        }
        Pendings.Enqueue(new(kind, color, text));
        // 已經在 framework 執行緒上就當場排乾：對既有呼叫點（幾乎全在任務鏈上）行為零改變。
        if(Svc.Framework.IsInFrameworkUpdateThread) Drain();
    }

    private static void OnFrameworkUpdate(IFramework framework) => Drain();

    private static void Drain()
    {
        while(Pendings.TryDequeue(out var pending))
        {
            try
            {
                if(pending.Kind == OutputKind.ErrorToast)
                {
                    Svc.Toasts.ShowError(pending.Text);
                }
                else
                {
                    ChatPrinter.PrintColored(pending.Color, pending.Text);
                }
            }
            catch(Exception e)
            {
                // 這裡若讓例外逃出去，會打斷 Framework.Update 這一輪剩下的監聽器。
                PluginLog.Error($"[ChatQueue] 送出訊息時發生例外（{e.GetType().Name}）：{e.Message}");
            }
        }
        OverflowReported = false;
    }
}
