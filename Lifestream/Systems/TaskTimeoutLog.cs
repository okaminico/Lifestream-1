using ECommons.Automation.NeoTaskManager;

namespace Lifestream.Systems;

/// <summary>
/// 讓 NeoTaskManager 的「任務逾時」在 dalamud.log 上說得出是<b>哪一步</b>逾時。
/// </summary>
/// <remarks>
/// <para>
/// 由來：ECommons 的 <c>TaskTimeoutException</c> 是一個<b>完全空的類別</b>
/// （<c>ECommons/Automation/NeoTaskManager/TaskTimeoutException.cs</c>：
/// <c>public class TaskTimeoutException : Exception { }</c>，連 Message 都沒有），
/// 而逾時時真正印出去的是 <c>TaskManager.Tick</c> 裡的 <c>e.LogWarning()</c>
/// —— 空訊息 ＋ 永遠指向 <c>TaskManager.Tick</c> 的堆疊 ⇒ <b>完全匿名</b>。
/// </para>
/// <para>
/// 📌 本外掛是全艦隊最會產生逾時的那一個（AutoRetainer 那邊留過一次實機量測：
/// 單一 91MB 的 dalamud.log 裡真正的 <c>TaskTimeoutException</c> 只有 4 筆，<b>全是 Lifestream 的</b>），
/// 而傳送前置等待、vnavmesh 尋路等待、跨資料中心拜訪這些步驟逾時之後
/// 預設就是 <c>AbortOnTimeout</c> 中止整條佇列 —— 使用者看到的是「按了沒反應」。
/// </para>
/// <para>
/// ⚠️ 本外掛的 TaskManager 是 <c>new(new(showDebug: true))</c>，所以帶任務名的那一行
/// （<c>→→Task timed out {Name}@{Location}</c>）其實有印，但它是 <b>Debug</b> 級 ——
/// 單一 log 檔動輒數十萬行 Debug，等於埋在噪音裡。這裡補的是 <b>Warning</b> 級的那一則。
/// </para>
/// <para>
/// 🔴 等級刻意維持 <c>Warning</c>：ECommons 原本就是 Warning，降級成 Information 只會弱化訊號。
/// 🔴 刻意<b>不</b>用 <c>DuoLog</c>：ECommons 的 <c>DuoLog.*</c> 在每一個等級都會無條件印到
/// 使用者的聊天視窗（沒有等級閘門），逾時是診斷資訊，不該洗聊天。
/// 🔴 也刻意<b>不</b>改 ECommons —— 全艦隊二十幾個消費端共用那一份。
/// ⚠️ <c>remainingTimeMS</c> 是 <c>ref</c>：寫它等於偷偷延長逾時，這裡<b>只讀不寫</b>。
/// </para>
/// </remarks>
public static class TaskTimeoutLog
{
    /// <summary>
    /// 把逾時說明掛到 <paramref name="taskManager"/> 上，並回傳同一個實例。
    /// </summary>
    /// <remarks>
    /// ⚠️ 這裡改的是 TaskManager <b>自己那份</b> <c>DefaultConfiguration</c>，不是傳進建構子的那個物件
    /// —— 建構子做的是 <c>new TaskManagerConfiguration{...}.With(傳進來的)</c>，事件被複製進另一個物件，
    /// 事後對傳進去的那個物件指派完全沒有效果。
    /// <para>
    /// <c>TimeoutSilently = true</c> 是為了蓋掉 ECommons 那行沒有任何資訊的匿名 Warning，
    /// 避免同一件事印兩遍。⚠️ 這個蓋法的前提是<b>沒有任務把 <c>ExecuteDefaultConfigurationEvents</c>
    /// 設成 false</b>（設了的話預設事件不會觸發，就會變成「靜默 ＋ 沒有人印」）。
    /// 本外掛目前一處都沒有用到那個旗標；之後若有人加，記得連這裡一起看。
    /// </para>
    /// </remarks>
    public static TaskManager Attach(TaskManager taskManager, string tag)
    {
        taskManager.DefaultConfiguration.TimeoutSilently = true;
        taskManager.DefaultConfiguration.OnTaskTimeout += (TaskManagerTask task, ref long remainingTimeMS) =>
        {
            // 該任務自己帶了處理器就交給它印，不要印兩遍。
            if(task.Configuration?.OnTaskTimeout != null) return;

            var limit = task.Configuration?.TimeLimitMS ?? taskManager.DefaultConfiguration.TimeLimitMS;
            var abort = task.Configuration?.AbortOnTimeout ?? taskManager.DefaultConfiguration.AbortOnTimeout ?? true;
            PluginLog.Warning(
                $"[{tag}] 任務逾時：{Describe(task)}，上限 {(limit.HasValue ? limit.Value.ToString() : "?")} ms" +
                (abort ? "，整條任務佇列會被中止。" : "，只丟棄這一步，其餘任務繼續。"));
        };
        return taskManager;
    }

    /// <summary>
    /// 盡量把一個任務描述成人看得懂的樣子。
    /// </summary>
    /// <remarks>
    /// 🔑 2026-09-10 用編譯器實測（net9 / Roslyn）確認過這三種形狀，<b>不要憑印象推</b>：
    /// <list type="bullet">
    /// <item>方法群組：<c>Name = MethodGroupTarget</c>、<c>Location = Outer</c> —— 兩個都有用。</item>
    /// <item>lambda：<c>Name = &lt;Run&gt;b__2_0</c>、<c>Location = &lt;&gt;c</c> 或
    /// <c>&lt;&gt;c__DisplayClass2_0</c> —— <b><c>Location</c> 裡一個類別名都沒有</b>，
    /// 有用的資訊反而在 <c>Name</c> 的角括號裡（外層方法名）。</item>
    /// <item>區域函式：<c>Name = &lt;Run&gt;g__Local|2_3</c>、<c>Location = Outer</c>。</item>
    /// </list>
    /// ⚠️ 所以這仍然只是「從完全查不出來」變成「查得到是哪個方法／哪個檔」，
    /// 不是「查得到是第幾行」。
    /// </remarks>
    public static string Describe(TaskManagerTask task)
    {
        var name = task.Name ?? "";
        var location = task.Location ?? "";
        if(TryGetEnclosingMethod(name, out var enclosing))
        {
            // lambda 的 Location 是編譯器產生的 <>c / <>c__DisplayClassN_M，印出來只是噪音。
            return location.StartsWith("<>", StringComparison.Ordinal)
                ? $"{enclosing}() 內的匿名步驟 [{name}]"
                : $"{enclosing}() 內的匿名步驟 [{name}@{location}]";
        }
        return $"[{name}@{location}]";
    }

    /// <summary>從 <c>&lt;外層方法&gt;b__N</c> / <c>&lt;外層方法&gt;g__名字|N_M</c> 取出外層方法名。</summary>
    private static bool TryGetEnclosingMethod(string name, out string enclosing)
    {
        enclosing = "";
        if(name.Length < 3 || name[0] != '<') return false;
        var end = name.IndexOf('>');
        if(end <= 1) return false;
        enclosing = name.Substring(1, end - 1);
        return true;
    }
}
