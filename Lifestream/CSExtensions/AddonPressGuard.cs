using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Plugin.Services;
using ECommons.Throttlers;

namespace Lifestream.CSExtensions;

/// <summary>
/// 「同一扇視窗的同一個按法,按過就不要再按,直到它真的收掉」的共用閘門。
/// 全外掛所有對 addon 的按法(<c>AddonMaster</c> 的 <c>Yes()</c>/<c>Select()</c>/<c>Click()</c>/<c>Start()</c>…、
/// <c>Callback.Fire</c>、<c>ClickAddonButton</c>、直送 <c>ReceiveEvent</c>、合成 <c>ListItemClick</c>、
/// <c>Close(true)</c>)都要先問過 <see cref="TryPressOnce(string, nint, string, string, bool)"/>。
/// </summary>
/// <remarks>
/// <para>
/// 🔴🔴 <b>存在的唯一理由是原生 AccessViolation</b>:<c>SelectYesno</c> 這類「按下即關」的窗被按下之後
/// 有<b>「正在關閉中」的幾幀</b>,這段期間 <c>GetAddonByName</c> 仍然回得到實例、
/// <c>IsVisible</c> 與 <c>UldManager.LoadedState == Loaded</c> 也都還成立 ——
/// 也就是說 <c>IsAddonReady</c> <b>三關全過、擋不住這個窗口</b>。
/// 此時再對它送 callback/輸入事件就是原生 AccessViolation(C0000005)。
/// AVE 在 .NET Core 是 corrupted-state exception,<c>try</c>/<c>catch</c> 完全攔不到,
/// 遊戲當場關閉 —— <b>唯一的防護是「不要送第二次」,不是「送了再接住」</b>。
/// 旁證:崩潰前讀窗上文字會出現 U+FFFD(窗記憶體變動中),見 <see cref="IsTextUnstable"/>。
/// </para>
/// <para>
/// 🔴 <b>節流不是防護</b>:節流記的是「上一次動作在哪一幀/哪個時刻」,不是「這扇窗已經按過」。
/// 本外掛的 <c>Utils.GenericThrottle</c>/<c>DCChange.DCThrottle</c> 是 <c>FrameThrottler</c> 10 幀
/// (數的是繪製幀,高 FPS 下比預期短),各站的 <c>EzThrottler</c> key 全域持久且<b>首次必放行</b>;
/// 而 <c>AddonMaster.SelectYesno.Yes()</c> 遇停用鈕會翻 <c>NodeFlags</c> 強制啟用再點,
/// 「按過的按鈕會被遊戲停用所以不會重按」也不成立。
/// </para>
/// <para>
/// 🔑 <b>做法</b>:按下之前先登記「這個名字底下的哪一個實例位址、被送過哪一組參數」,
/// 在觀察到那扇窗真的走完生命週期之前不准再送同一組。
/// 🔴 全程只做<b>位址等值比較,永遠不解參</b> —— 被記下的那個位址隨時可能已經失效。
/// </para>
/// <para>
/// 🔑 <b>粒度=(窗,位址,參數組)</b>:
/// <list type="bullet">
/// <item>「回答一次即終結」的窗(SelectYesno 族、確認鈕按下即關、取消/關閉)<b>不帶</b> <c>paramKey</c>,
/// 整扇窗一把 key —— 不管按的是「是」「否」還是取消,按過任何一個之後窗就在關閉中,別的都不准再送;
/// <see cref="SingleAnswerAddons"/> 裡的窗名一律強制併 key。</item>
/// <item>按下不會關的窗(清單選列、換頁、換區、Talk 翻頁、TelepotTown 的刻意雙送)帶 <c>paramKey</c>,
/// 同一扇窗對不同參數組可以各按一次(保住「同幀對同窗連送不同參數」的正常流程);
/// 但只要這扇窗<b>不帶參數的</b> key 已經記下這個位址(＝我們自己把它關了),任何參數組都不准再送。</item>
/// <item><c>SelectString</c>/<c>SelectIconString</c> 刻意<b>不</b>併 key(用索引當參數組):巢狀選單常常
/// 重用同一個實例只換內容,併 key 會讓下一層的選擇被擋到逃生口。</item>
/// </list>
/// </para>
/// <para>
/// <b>解除封鎖有兩條互補的觀察點</b>(兩條都只會讓封鎖<b>提早</b>解除,不會延後):
/// <list type="number">
/// <item><b>輪詢</b>:被記下的位址已經不在該名稱的 addon 清單裡(掃全索引 1..99,掃到第一個空的停,
/// 與 <c>Utils.GetSpecificYesno</c> 同一種走法)⇒ 那扇窗真的收乾淨了。本外掛所有按下點都是
/// <c>Framework.Update</c>(含跑在它上面的 NeoTaskManager)驅動,每個 tick 都會再進來一次,所以輪詢可行。</item>
/// <item><b><see cref="IAddonLifecycle"/> 事件</b>:<see cref="AddonEvent.PreFinalize"/>(這一扇正在被銷毀)
/// 與 <see cref="AddonEvent.PostSetup"/>(有新的一扇在這個位址被建立起來),只清<b>同位址</b>的紀錄。
/// 同名 addon 關掉再開常常會<b>重用同一塊記憶體位址</b>,只靠輪詢的話重開的那扇會被誤認成
/// 「按過的那扇還沒收掉」而白白被擋到逃生口。⚠️ 刻意<b>不</b>把 <c>PostRefresh</c> 當解除點:
/// 它有可能在「關閉中」那幾幀觸發,那會把封鎖提早解除,正好把這道防線變成沒有。</item>
/// </list>
/// ⚠️ 判準刻意<b>不</b>用「文字還對不對」或「還可不可見」:窗在拆除途中可能有幾幀讀不到文字、或已被設成不可見,
/// 拿那些當「窗不見了」會<b>正好在最危險的那幾幀</b>把封鎖解除掉。
/// </para>
/// <para>
/// 🔴 <b>逃生口是刻意的</b>:萬一某扇窗既不 finalize 也不重新 setup(上一次的 callback 根本沒生效、窗就是還開著),
/// 沒有逃生口的話呼叫端會<b>永遠</b>按不下去,等於把崩潰換成靜默失效;而 NeoTaskManager 預設 <c>abortOnTimeout</c>
/// 會清掉<b>整條</b>佇列。單答終結窗 <see cref="RePressEscapeFrames"/>(60 幀),多次互動窗
/// <see cref="RoutineRePressEscapeFrames"/>(15 幀,2026-09-02 艦隊 Talk 政策)。用幀數不用毫秒:
/// 危險窗口的長度本來就是以幀計的,遊戲卡頓時兩者一起拉長。
/// 🔴 幀的來源是 <b>framework tick</b> 而不是繪製幀(見 <see cref="frameCount"/>):繪製幀在過場動畫與
/// 隱藏 UI 期間會整段停止前進,而傳送/資料中心轉移正好大量伴隨過場 —— 用繪製幀的話逃生口永遠不到期。
/// </para>
/// <para>
/// 📌 <b>正常路徑行為零變化</b>:第一次看到某扇窗的某個按法一律當場按下去;被擋回 <see langword="false"/>
/// 對呼叫端的意義一律是「這一輪沒按到,下一輪再來」,與「addon 還沒出現」「節流還沒放行」走同一條既有路徑。
/// 🔴 絕不回 <see langword="null"/>:NeoTaskManager 的 <c>bool?</c> 三態裡 <see langword="null"/> 是 Abort。
/// </para>
/// <para>⚠️ 只在主執行緒使用(與呼叫端的 <c>EzThrottler</c> 同一個前提)。</para>
/// </remarks>
internal static unsafe class AddonPressGuard
{
    /// <summary>
    /// 已經按過、那扇窗卻既沒消失也沒重建時,最多再等這麼多幀才允許補按一次(單答終結窗)。
    /// </summary>
    /// <remarks>
    /// 🔑 這不是節流 —— 真正的防護是「同一扇窗的同一個按法只按一次」,這個值只是防死鎖的逃生口。
    /// 60 幀(60fps 下約 1 秒)遠遠大於「關閉中的那幾幀」,補按永遠不會落在危險窗口內。
    /// 走到這個逃生口代表「按了卻沒關掉」,寫 <c>Information</c>(使用者跑 LogLevel 1,Debug 收得到但單檔數十萬行會淹沒)。
    /// </remarks>
    internal const int RePressEscapeFrames = 60;

    /// <summary>
    /// 「按一次翻一頁、窗不會因為被按而消失」的多次互動窗(Talk 是代表;同形狀還有清單選列、換頁、換區、
    /// TelepotTown 的刻意雙送)專用的逃生口:<c>escapeIsRoutine</c> 為 <see langword="true"/> 時用它。
    /// </summary>
    /// <remarks>
    /// 這類窗走逃生口是<b>常態</b>(那才是翻到下一頁/再送下一次的方式),所以逃生口的長度直接決定節奏。
    /// 關閉中的危險窗口實測 &lt;10 幀,15 幀不落在裡面;每頁多等 0.25 秒幾乎無感。走逃生口寫 <c>Debug</c> 不洗版。
    /// ⚠️ 刻意<b>不</b>用「文字變了」當翻頁證據:關閉中的窗文字會讀壞(U+FFFD)。(2026-09-02 艦隊政策:Talk 類一律 15 幀。)
    /// </remarks>
    internal const int RoutineRePressEscapeFrames = 15;

    /// <summary>輪詢解除時最多掃到第幾個同名實例;掃到第一個空的就提早停。</summary>
    private const int MaxAddonIndex = 99;

    /// <summary>
    /// <see cref="PersistentAddons"/> 裡的窗被按下之後,最少要<b>連續</b>觀察到它「還在清單裡、
    /// 但已經被隱藏」這麼多幀,才把按下記號解除。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>為什麼需要這條路徑</b>:既有的兩條解除點(輪詢「位址從清單消失」、生命週期 PreFinalize/PostSetup)
    /// 對常駐窗<b>都是死路</b> —— 它從頭到尾都在清單裡、位址不變,兩個事件也不會再發生 ⇒ 記號一旦記下就
    /// <b>永不解除</b>,整扇窗退化成「每 <see cref="RePressEscapeFrames"/> 幀才准動作一次」。
    /// AutoRetainer 2026-09-04 的實機記錄:<c>ContextMenu</c> 送出 295 次,其中 290 次是走逃生口走出來的
    /// (＝記號從沒被解除過),另有 144 次使用者的右鍵被整個吞掉。
    /// </para>
    /// <para>
    /// 🔴 <b>為什麼是「連續 N 幀」而不是「這一幀不可見就解除」</b>:窗在拆除途中會有幾幀「已經被設成不可見、
    /// 但還沒拆完」,那正是這個守衛在防的危險窗口(實測 &lt;10 幀)。要求連續觀察到隱藏才解除,等於「等到穩定
    /// 隱藏＝拆除已經結束」才放行;看到可見<b>或這一幀查不到</b>就歸零重數。
    /// </para>
    /// <para>
    /// 📌 <b>20 幀換算成多久</b>:使用者實測的幀率是 <b>10.07 ms/幀</b>(僱員鈴前,n=290)⇒ 20 幀 ≈ <b>201 毫秒</b>。
    /// (不要用 60fps 去換算成 333 毫秒 —— 那個幀率在這台機器上不成立。)
    /// </para>
    /// <para>
    /// 🔴 <b>這個數字不承重。</b>真正把崩潰面拆掉的是 <see cref="TryPressOnce(string, nint, string, string, bool)"/>
    /// 對常駐窗多要求的那道「送出前必須可見」:它的放行條件(可見)與這裡的解除條件(連續不可見)
    /// <b>互為邏輯反面</b> ⇒ 記號被解除之後還要能送出,中間<b>必須</b>有一次遊戲自己把窗重新 Show 起來,
    /// 而正在拆除的窗不會被重新 Show。所以<b>就算這個幀數設得太短</b>,最壞也只是「那一發沒送出」,
    /// 不會變成對正在拆除的窗再送一次(＝攔不到的 AccessViolation)。N 只決定使用者要等多久。
    /// </para>
    /// <para>🔴 這個值只在 <see cref="PersistentAddons"/> 裡的窗名上生效;其餘窗名的解除條件<b>一個字都沒有改</b>。</para>
    /// </remarks>
    internal const int HiddenReleaseFrames = 20;

    /// <summary>
    /// 「一扇窗一生只回答一次」的視窗:這些名字底下的按法一律併成同一個 key(呼叫端傳的 <c>paramKey</c> 會被忽略)。
    /// </summary>
    /// <remarks>
    /// ⚠️ 只放<b>回答一次就結束</b>的窗。像 LobbyDKTWorldList(清單選列＋取消/確認鈕)、HousingSelectBlock(換頁＋確認)、
    /// MansionSelectRoom(換區＋選房)、_CharaSelectListMenu(選角不關窗)這種「窗一直開著、刻意連送不同 callback」的
    /// <b>絕對不能</b>放進來 —— 那會把正常流程一起擋掉;那些窗由呼叫端決定哪一發帶參數組、哪一發是「回答」。
    /// </remarks>
    private static readonly HashSet<string> SingleAnswerAddons = new(StringComparer.Ordinal)
    {
        "SelectYesno",
        "SelectOk",
        "ContentsFinderConfirm",
        "LobbyDKTCheck",
        "LobbyDKTCheckExec",
        "LobbyWKTCheckHome",
        "_TitleMenu",
        "TitleDCWorldMap",
        "_CharaSelectReturn",
        "ContextMenu",
        "WorldTravelSelect",
        "Trade",
    };

    /// <summary>
    /// 已經<b>實機證實</b>是「常駐」的窗名:關閉只是被設成不可見,實例與位址永遠留在 <c>AllLoadedUnitsList</c> 裡,
    /// 既不會從輪詢掃的清單消失,<see cref="AddonEvent.PreFinalize"/> 與 <see cref="AddonEvent.PostSetup"/> 也不會再發生。
    /// 只有這些窗名才套用「連續隱藏 <see cref="HiddenReleaseFrames"/> 幀就解除」這條新規則。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔑 <b>加名字進來的門檻＝實機 log 裡「真的送出的次數」≈「走逃生口的次數」</b>
    /// (兩者幾乎相等＝記號從來沒有被那兩條既有路徑解除過)。<c>ContextMenu</c> 在 AutoRetainer 的同一場
    /// 實機記錄裡是 295 次送出 : 290 次逃生口 : 144 次被吞掉,因此收錄。
    /// </para>
    /// <para>
    /// ⚠️ <b>沒有這種證據的名字一律不要加。</b>猜錯的方向是危險的:把一扇「會被銷毀」的窗誤標成常駐,
    /// 等於在它拆除途中提早 <see cref="HiddenReleaseFrames"/> 幀解除封鎖。已知的候選但<b>證據不足、刻意沒收</b>
    /// 的是 <c>ContextIconMenu</c>(與 <c>ContextMenu</c> 同一個 <c>AgentContext</c> 家族,直覺上同樣常駐,
    /// 但實機 log 裡沒有它的逃生口紀錄,無從證實)。
    /// </para>
    /// <para>
    /// 🔴 <b>加名字進來的代價:這份名單同時是「送出前必須可見」的名單。</b>
    /// <see cref="TryPressOnce(string, nint, string, string, bool)"/> 對名單內的窗多要求一道就地可見性檢查,
    /// 那是 <see cref="ReleaseHiddenPersistent"/> 解除條件的邏輯反面。名單外的窗一個字都沒有改。
    /// </para>
    /// <para>
    /// 📌 本外掛目前對 <c>ContextMenu</c> 只有兩個按下點(<c>DCChange.SelectVisitAnotherDC</c> 與
    /// <c>DCChange.SelectReturnToHomeWorld</c>),兩個都已經在同一個條件鏈裡先問過 <c>m.IsAddonReady</c>
    /// (＝含 <c>IsVisible</c>),所以這條新規則的反面關係在呼叫端也成立。
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> PersistentAddons = new(StringComparer.Ordinal)
    {
        "ContextMenu",
    };

    /// <summary>一個位址的按下紀錄。</summary>
    /// <remarks>
    /// 🔴 <b>刻意是 class 不是 struct。</b>寫成 struct(或原本那樣直接存 <c>long</c>)時,取出來的是複本,
    /// 「改完要寫回字典」就成了承重的一行 —— 而漏掉寫回<b>不會編譯失敗</b>,只會讓 <see cref="HiddenFrames"/>
    /// 永遠停在 0,常駐窗的記號就再也解除不掉(＝這次要修的那個形狀)。
    /// 改成 class 之後取出的是參考、就地改就生效,這個地雷從根本上不存在。
    /// ⚠️ 同時存在的紀錄實務上只有 0~3 個,多出來的配置可以忽略。
    /// </remarks>
    private sealed class PressRecord
    {
        /// <summary>最近一次按下的幀。所有逃生口判斷都拿它跟現在的幀比。</summary>
        public long Frame;

        /// <summary>
        /// 連續觀察到「位址還在清單裡、但那扇窗已經被隱藏」的幀數。只有 <see cref="PersistentAddons"/> 裡的
        /// 窗名會累加;看到可見(或這一幀查不到)就歸零,重新按下也歸零。
        /// 累到 <see cref="HiddenReleaseFrames"/> 就解除記號。
        /// </summary>
        public int HiddenFrames;
    }

    /// <summary>一把 key(窗名＋參數組)底下「已經按過的位址 → 按下當時的幀」。同名窗可能同時開好幾扇(SelectYesno 就會),所以是集合。</summary>
    private sealed class Slot
    {
        public string AddonName;
        public readonly Dictionary<nint, PressRecord> Pressed = [];
    }

    private static readonly Dictionary<string, Slot> Slots = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IAddonLifecycle.AddonEventDelegate> Watchers = new(StringComparer.Ordinal);

    // 輪詢用的可重用緩衝:沒有窗被記著時整支 ReleaseVanished 是一個整數比較就回來,不配置任何東西。
    private static readonly List<string> NamesBuf = [];
    private static readonly HashSet<nint> PresentBuf = [];
    private static readonly List<nint> RemoveBuf = [];
    private static readonly List<string> EmptyKeysBuf = [];
    // 隱藏解除專用的移除緩衝(與 RemoveBuf 分開:那支在 ReleaseVanished 裡用,兩者不巢狀但分開比較不容易寫錯)。
    private static readonly List<nint> HiddenRemoveBuf = [];
    // 「這個窗名第一次走隱藏解除」只寫一行 Information,之後不再寫(每場遊戲每個窗名最多一行,不會洗版)。
    private static readonly HashSet<string> HiddenReleaseReported = new(StringComparer.Ordinal);

    /// <summary>
    /// 逃生口用的幀計數器,每個 framework tick 加一(由 <see cref="Tick"/> 推進,<see cref="OnFrameworkUpdate"/> 當後援)。
    /// </summary>
    /// <remarks>
    /// 🔴🔴 <b>刻意不用 <c>Svc.PluginInterface.UiBuilder.FrameCount</c></b>:那個計數器是在
    /// <c>UiBuilder.OnDraw()</c> 的<b>最後面</b>才加的,而 <c>OnDraw()</c> 開頭有三個<b>直接 return</b>
    /// 的隱藏條件 —— ①使用者按熱鍵隱藏 UI ②<b>過場動畫</b>(<c>ToggleUiHideDuringCutscenes</c>,<b>預設開</b>)
    /// ③GPose。也就是說<b>過場動畫或隱藏 UI 期間,繪製幀完全停止前進</b>。
    /// <para>
    /// 這對本外掛特別致命:傳送與資料中心轉移途中<b>大量伴隨過場與黑畫面</b>,那正好是時鐘凍結的時候,
    /// 於是所有逃生口(<see cref="RePressEscapeFrames"/> / <see cref="RoutineRePressEscapeFrames"/>)
    /// <b>永遠不到期</b>。這是 fail-closed(不會崩、不會誤按),但「需要連續按同一扇窗才能推進」的站
    /// (Talk 翻頁是代表)會就此停擺。
    /// </para>
    /// <para>
    /// 改用 framework tick:它掛在遊戲自己的 <c>Framework::Tick</c> 上,與 ImGui/繪製那條路徑完全無關,
    /// 隱藏 UI 與過場期間照常前進。
    /// 🔑 <b>幀數常數不需要跟著調整</b>:正常情況下兩者都是「每個遊戲幀一次」、比例 1:1,
    /// 差別只在繪製幀會被上述三種情況<b>整段扣掉</b>,framework tick 不會。
    /// </para>
    /// </remarks>
    private static long frameCount;

    /// <summary><see cref="OnFrameworkUpdate"/> 是否已經掛上去(<see cref="EnsureFrameClock"/> 的冪等旗標)。</summary>
    private static bool frameClockRunning;

    /// <summary>這個 framework tick 是否已經由 <see cref="Tick"/> 推進過計數器(<see cref="OnFrameworkUpdate"/> 的後援判斷用)。</summary>
    private static bool tickedThisFrame;

    private static long CurrentFrame => frameCount;

    /// <summary>
    /// 從窗上讀出來的文字含 U+FFFD(替換字元)＝ 這幾幀窗的記憶體正在變動(多半是關閉中),
    /// 凡是靠文字做判定的按下點<b>這一幀不要碰</b>。這是崩潰前 log 裡實測看到的旁證,不是防護本體。
    /// </summary>
    /// <returns><see langword="true"/> ＝ 文字讀壞了,呼叫端這一幀什麼都不要做(走既有的「沒比中」路徑)。</returns>
    internal static bool IsTextUnstable(string addonName, string text)
    {
        if(string.IsNullOrEmpty(text) || text.IndexOf((char)0xFFFD) < 0) return false;
        if(EzThrottler.Throttle($"AddonPressGuard-Unstable-{addonName}", 1000))
            PluginLog.Information($"[AddonPressGuard] 「{addonName}」的文字讀到 U+FFFD(視窗記憶體正在變動,多半是關閉中),這一幀不碰它");
        return true;
    }

    /// <inheritdoc cref="IsTextUnstable(string, string)"/>
    internal static bool AnyTextUnstable(string addonName, IEnumerable<string> texts)
    {
        if(texts == null) return false;
        foreach(var text in texts)
        {
            if(IsTextUnstable(addonName, text)) return true;
        }
        return false;
    }

    /// <inheritdoc cref="TryPressOnce(string, nint, string, string, bool)"/>
    internal static bool TryPressOnce(string addonName, void* addon, string label, string paramKey = null, bool escapeIsRoutine = false)
        => TryPressOnce(addonName, (nint)addon, label, paramKey, escapeIsRoutine);

    /// <inheritdoc cref="TryPressOnce(string, nint, string, string, bool)"/>
    /// <remarks>給不是 <c>unsafe</c> 的呼叫端用(CustomAliasCommand、TaskChangeDatacenter):位址在這裡從 AddonMaster 取,呼叫端不必碰指標。</remarks>
    internal static bool TryPressOnce(string addonName, ECommons.UIHelpers.AddonMasterImplementations.IAddonMasterBase master, string label, string paramKey = null, bool escapeIsRoutine = false)
        => TryPressOnce(addonName, master == null ? 0 : (nint)master.Base, label, paramKey, escapeIsRoutine);

    /// <summary>
    /// 問「這扇窗的這一個按法現在可以送嗎」,可以的話<b>順便記下</b>已經按過。呼叫端拿到
    /// <see langword="true"/> 才去按;按法留給呼叫端自己決定。
    /// </summary>
    /// <param name="addonName">窗名。是輪詢/生命週期監聽解除封鎖時用的名字,也是 key 的前半。</param>
    /// <param name="addon">要按的 addon 位址。<b>只做等值比較,這裡永遠不解參。</b></param>
    /// <param name="label">被擋/走逃生口時寫進 log 的站名。</param>
    /// <param name="paramKey">
    /// <see langword="null"/>(預設)＝「回答一次即終結」的按法,整扇窗一把 key;
    /// 非空＝按下不會關窗的按法,同一扇窗對不同參數組各准按一次。<see cref="SingleAnswerAddons"/> 裡的窗一律視為 null。
    /// </param>
    /// <param name="escapeIsRoutine">
    /// <see langword="true"/> ＝ 這個按下點「同一扇窗本來就會被按很多次」,逃生口縮成
    /// <see cref="RoutineRePressEscapeFrames"/>,走逃生口是常態(寫 Debug、被擋不記);
    /// <see langword="false"/>(預設)＝ 走逃生口代表「按了卻沒關掉」這種該被回報的異常,寫 <c>Information</c>。
    /// </param>
    /// <returns><see langword="true"/> ＝ 可以按(而且已經記下);<see langword="false"/> ＝ 這一輪不要按。</returns>
    /// <remarks>
    /// 呼叫點要放在<b>緊接著送出動作之前</b>(通常是 <c>&amp;&amp;</c> 鏈的最後一項)—— 這支一回 <see langword="true"/>
    /// 就已經把「按過了」記下去,登記完卻不按的話會白白封鎖到逃生口為止。
    /// </remarks>
    internal static bool TryPressOnce(string addonName, nint addon, string label, string paramKey = null, bool escapeIsRoutine = false)
    {
        if(addon == 0 || string.IsNullOrEmpty(addonName)) return false;
        ReleaseVanished();
        EnsureWatching(addonName);
        EnsureFrameClock();
        if(SingleAnswerAddons.Contains(addonName)) paramKey = null;
        // 🔴🔴 常駐窗專用的「送出前必須可見」閘門。
        //    ⚠️ 這一道**不是**「擋得住正在關閉中的窗」的檢查 —— IsAddonReady 的三關(非 null / IsVisible /
        //    LoadedState == Loaded)在拆除途中是**全過**的(本檔開頭那段講的就是這件事),單獨看它一個東西都擋不到。
        //    **這個結論不可以當成通用結論搬去別的地方用。**
        //
        //    它在這裡有效的唯一理由是:**與 ReleaseHiddenPersistent 的解除條件互為邏輯反面**。
        //    那支的解除條件是「連續 HiddenReleaseFrames 幀**不可見**」,這裡的放行條件是「**可見**」
        //    ⇒ 記號被解除之後還要能再送出一發,中間**必須**有一次遊戲自己把這扇窗重新 Show 起來,
        //    而正在拆除的窗不會被重新 Show。兩者一組才是防護;只做其中一半的話,記號可以在拆除中途被解除,
        //    下一發直接打在正在拆的窗上 ＝ 攔不到的存取違規。
        // 🔴 只對 PersistentAddons 生效,名單外的窗完全不走這一行,行為與改動前逐字相同。
        // 🔴 查不到也回「不可見」(fail-closed):放行端的任何不確定都必須表現成「這一輪不送」。
        if(PersistentAddons.Contains(addonName) && LookUpVisibility(addonName, addon) != AddonVisibility.Visible)
        {
            if(EzThrottler.Throttle($"AddonPressGuard-PersistentHidden-{addonName}", 1000))
                PluginLog.Information($"[AddonPressGuard] {label}: 「{addonName}」是常駐窗,0x{addon:X} 這一幀不可見(或不在同名清單裡),不送 —— 它與「連續隱藏才解除」互為反面,兩者一組才是防護");
            return false;
        }
        var frame = CurrentFrame;
        if(paramKey != null
            && Slots.TryGetValue(addonName, out var answered)
            && answered.Pressed.TryGetValue(addon, out var answeredRec)
            && frame - answeredRec.Frame < RePressEscapeFrames)
        {
            // 這扇窗已經被「回答」過(我們自己按了關閉/取消/確認)。窗還在 ＝ 正在關閉中,任何參數組都不准再送。
            LogHold(addonName, addon, label, escapeIsRoutine);
            return false;
        }
        var key = paramKey == null ? addonName : addonName + "|" + paramKey;
        if(!Slots.TryGetValue(key, out var slot))
        {
            slot = new() { AddonName = addonName };
            Slots[key] = slot;
        }
        if(slot.Pressed.TryGetValue(addon, out var rec))
        {
            // 這一扇的這一個按法已經送過。窗還在 ＝ 可能正在關閉中,此時再送就是上面說的 AVE。
            var escapeFrames = escapeIsRoutine ? RoutineRePressEscapeFrames : RePressEscapeFrames;
            var waited = frame - rec.Frame;
            if(waited < escapeFrames)
            {
                LogHold(addonName, addon, label, escapeIsRoutine);
                return false;
            }
            // 逃生口:等了遠超過關閉所需的時間,窗仍在。視為那次沒生效(或這是另一扇重用了同一塊記憶體的新窗),放行補按一次。
            if(escapeIsRoutine)
            {
                if(EzThrottler.Throttle($"AddonPressGuard-RoutineEscape-{addonName}", 5000))
                    PluginLog.Debug($"[AddonPressGuard] {label}: 「{addonName}」(0x{addon:X}) 按下後 {waited} 幀窗還在(多次互動窗的常態),放行下一次");
            }
            else if(EzThrottler.Throttle($"AddonPressGuard-Escape-{addonName}", 10000))
            {
                PluginLog.Information($"[AddonPressGuard] {label}: 「{addonName}」(0x{addon:X}) 按下後 {waited} 幀既沒消失也沒重建,判定為「上一次沒生效」而不是「正在關閉」,放行補按一次");
            }
        }
        // PressRecord 是 class:已經有紀錄就就地更新,沒有才配一個新的。
        // 🔴 下面那行 HiddenFrames = 0 是承重的:隱藏解除是「從最後一次按下起算」連續隱藏幾幀。漏掉的話,
        //    「窗已經隱藏了 15 幀 → 逃生口在第 60 幀補按一次」之後只要再 5 幀就會解除記號,
        //    等於把補按之後的保護期縮到 5 幀。
        if(rec == null) slot.Pressed[addon] = rec = new PressRecord();
        rec.Frame = frame;
        rec.HiddenFrames = 0;
        LogPressDiag(addonName, addon, paramKey);
        return true;
    }

    /// <summary>
    /// 每幀從 <c>Framework_Update</c> 最前面無條件呼叫:推進逃生口用的幀計數器,並解除
    /// 「被記下的位址已從清單消失」的封鎖。沒有紀錄時 <see cref="ReleaseVanished"/> 是一個整數比較就回來。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>計數器一定要在 <see cref="ReleaseVanished"/> 之前推進</b>:那支對空集合有「沒有紀錄就直接回來」
    /// 的快速返回,寫在它後面的話,沒有窗被記著時時鐘就跟著停住,逃生口的幀差會失真。
    /// <para>
    /// 🔑 這裡是本外掛整條 <c>Framework.Update</c> 鏈上<b>最早</b>能推進計數器的點(<c>Framework_Update</c> 的第一行)。
    /// 之所以不只靠 <see cref="OnFrameworkUpdate"/>:本 pin 的 <c>FrameworkPluginScoped</c> 是把整條多播委派
    /// <b>當成一個</b>丟進 <c>PluginErrorHandler.InvokeAndCatch</c> 的,鏈上任何一支擲例外就<b>跳過排在它後面的全部</b>
    /// (而 <c>Framework_Update</c> 本身沒有 try/catch)。我們的監聽器是最後才掛上去的,只靠它的話
    /// 「上游擲例外的那幾幀」時鐘會停,而 TaskManager 驅動的按下點照跑 —— 又變回這次要修的那個形狀。
    /// </para>
    /// </remarks>
    internal static void Tick()
    {
        EnsureFrameClock();
        frameCount++;
        tickedThisFrame = true;
        ReleaseVanished();
        // 🔑 只在這裡呼叫,不放進 ReleaseVanished —— 那支在 TryPressOnce 裡也會被呼叫,
        //    放進去的話同一幀可能被數兩次,隱藏幀數就會提早到期(那是把保護期對半砍)。
        ReleaseHiddenPersistent();
    }

    /// <summary>外掛卸載時硬拆所有監聽器(不留指向本組件的委派)並清掉全部紀錄。</summary>
    internal static void ForceTeardown()
    {
        foreach(var (addonName, handler) in Watchers)
        {
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, addonName, handler);
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, addonName, handler);
        }
        Watchers.Clear();
        Slots.Clear();
        if(frameClockRunning)
        {
            Svc.Framework.Update -= OnFrameworkUpdate;
            frameClockRunning = false;
        }
        frameCount = 0;
        tickedThisFrame = false;
    }

    /// <summary>
    /// 跨外掛「按窗診斷」:在<b>真的送出按壓</b>的那一刻寫一行 <c>Debug</c>。
    /// </summary>
    /// <remarks>
    /// 全艦隊 15 份各自獨立的 <c>AddonPressGuard</c> 只擋自己按過的位址:外掛 A 按下之後
    /// 「關閉中」那幾幀,外掛 B 的表是空的 ⇒ 照按 ⇒ 攔不到的存取違規。
    /// 這一行的用途是用一輪實機 log 回答「跨外掛重按是不是真的在發生」,
    /// 格式<b>逐字</b>與其他外掛統一,才能按 <c>addr</c> 交叉比對。
    /// 🔴 刻意<b>不節流</b>(漏掉一次就是漏掉一個對照樣本);
    /// 🔴 位址只印數值,<b>不解參考</b>。
    /// </remarks>
    private static void LogPressDiag(string addonName, nint addon, string paramKey)
    {
        var name = string.IsNullOrEmpty(addonName) ? "?" : addonName;
        PluginLog.Debug($"[按窗診斷] plugin=Lifestream addon={name} addr=0x{addon:X} key={paramKey ?? string.Empty}");
    }

    /// <summary>被擋那一幀的診斷。單答終結窗寫 Information(使用者跑 LogLevel 1)、每扇窗 1 秒節流;多次互動窗被擋是常態,不記。</summary>
    private static void LogHold(string addonName, nint addon, string label, bool escapeIsRoutine)
    {
        if(escapeIsRoutine) return;
        if(EzThrottler.Throttle($"AddonPressGuard-Hold-{addonName}", 1000))
            PluginLog.Information($"[AddonPressGuard] {label}: 「{addonName}」(0x{addon:X}) 按過之後還沒觀察到它收掉,這一幀不再碰它 —— 對關閉中的視窗送事件是攔不到的存取違規");
    }

    /// <summary>
    /// 清掉「被記下的那個實例已經不在同名 addon 清單裡」的紀錄。同一個窗名底下不管有幾把 key 都只掃一次清單。
    /// </summary>
    /// <remarks>🔴 只做位址等值比較,永遠不解參(<c>GetAddonByName(name, i).Address</c> 只是讀清單裡的指標值)。</remarks>
    private static void ReleaseVanished()
    {
        if(Slots.Count == 0) return;
        NamesBuf.Clear();
        EmptyKeysBuf.Clear();
        foreach(var (key, slot) in Slots)
        {
            if(slot.Pressed.Count == 0)
            {
                EmptyKeysBuf.Add(key);
                continue;
            }
            if(!NamesBuf.Contains(slot.AddonName)) NamesBuf.Add(slot.AddonName);
        }
        foreach(var name in NamesBuf)
        {
            PresentBuf.Clear();
            for(var i = 1; i <= MaxAddonIndex; i++)
            {
                var present = Svc.GameGui.GetAddonByName(name, i).Address;
                if(present == 0) break;
                PresentBuf.Add(present);
            }
            foreach(var (key, slot) in Slots)
            {
                if(slot.AddonName != name || slot.Pressed.Count == 0) continue;
                RemoveBuf.Clear();
                foreach(var addr in slot.Pressed.Keys)
                {
                    if(!PresentBuf.Contains(addr)) RemoveBuf.Add(addr);
                }
                foreach(var addr in RemoveBuf) slot.Pressed.Remove(addr);
                if(slot.Pressed.Count == 0) EmptyKeysBuf.Add(key);
            }
        }
        // 空掉的 key 順手收掉,帶動態參數組的 key(TelepotTown 的 callback、清單索引)才不會無限累積。
        foreach(var key in EmptyKeysBuf) Slots.Remove(key);
    }

    /// <summary>這一幀對某個位址觀察到的可見狀態。</summary>
    /// <remarks>
    /// 🔴 <b>零值必須是一個「不解除、也不放行」的答案。</b>任何忘了指派／查詢失敗的路徑落在零值上時,
    /// 結果都必須是「維持封鎖」而不是「放行」—— 後者是崩潰,前者只是多等一下。
    /// </remarks>
    private enum AddonVisibility
    {
        /// <summary>這一幀在同名清單裡找不到這個位址。<b>這是零值</b>,語意是「不知道」。</summary>
        Unknown = 0,

        /// <summary>找到了,而且看得見。</summary>
        Visible = 1,

        /// <summary>找到了,但已經被設成不可見。</summary>
        Hidden = 2,
    }

    /// <summary>
    /// 在 <paramref name="addonName"/> 的<b>所有</b>同名實例裡找出 <paramref name="address"/>,
    /// 回報它這一幀看不看得見;找不到就回 <see cref="AddonVisibility.Unknown"/>。
    /// </summary>
    /// <remarks>
    /// 🔴 存下來的位址只做等值比較;解參考的是 <c>GetAddonByName</c> <b>這一幀剛交回來</b>的指標,
    /// 讀的是 <c>AtkUnitBase</c> 固定位移的 <c>IsVisible</c>(自帶判空,不再往下追任何二級指標)。
    /// 掃到第一個空的就停,上限 <see cref="MaxAddonIndex"/>。
    /// </remarks>
    private static AddonVisibility LookUpVisibility(string addonName, nint address)
    {
        if(address == 0 || string.IsNullOrEmpty(addonName)) return AddonVisibility.Unknown;
        for(var i = 1; i <= MaxAddonIndex; i++)
        {
            var unit = Svc.GameGui.GetAddonByName(addonName, i);
            if(unit.Address == 0) break;
            if(unit.Address != address) continue;
            return unit.IsVisible ? AddonVisibility.Visible : AddonVisibility.Hidden;
        }
        return AddonVisibility.Unknown;
    }

    /// <summary>
    /// <see cref="PersistentAddons"/> 專用的第三條解除路徑:連續觀察到「位址還在清單裡、但那扇窗已經隱藏」
    /// <see cref="HiddenReleaseFrames"/> 幀就解除按下記號。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>兩條安全不變量原封不動</b>:①存下來的位址只做等值比較,<b>永不解參</b>(解的是本幀剛拿回來的指標);
    /// ②解除<b>按位址</b>一筆一筆移除,不是按名稱整批清。
    /// </para>
    /// <para>
    /// 🔑 <b>這裡的查詢即使查歪了也只會更保守</b>:<b>只有「找到了、而且不可見」才累加</b>,
    /// 「查不到」與「找到但看得見」<b>一律歸零</b>。⇒ 同名實例超過掃描上限、窗已從清單移除、查詢本身出錯……
    /// 每一種都表現成「繼續封鎖」。
    /// </para>
    /// <para>
    /// 📌 成本:只在「表裡有常駐窗名的紀錄」時才掃,而那種紀錄正常情況下最多活
    /// <see cref="HiddenReleaseFrames"/> 幀(窗一收起來就被解除)。
    /// </para>
    /// </remarks>
    private static void ReleaseHiddenPersistent()
    {
        if(Slots.Count == 0) return;
        foreach(var slot in Slots.Values)
        {
            if(slot.Pressed.Count == 0 || !PersistentAddons.Contains(slot.AddonName)) continue;
            HiddenRemoveBuf.Clear();
            foreach(var (addr, rec) in slot.Pressed)
            {
                if(LookUpVisibility(slot.AddonName, addr) != AddonVisibility.Hidden)
                {
                    // 看得見(還開著,或落在關閉中的危險窗口內)／這一幀查不到 ⇒ 歸零重數。
                    rec.HiddenFrames = 0;
                    continue;
                }
                if(++rec.HiddenFrames < HiddenReleaseFrames) continue;
                HiddenRemoveBuf.Add(addr);
                if(HiddenReleaseReported.Add(slot.AddonName))
                    PluginLog.Information($"[AddonPressGuard] 「{slot.AddonName}」:偵測到這是常駐窗(隱藏而不銷毀),按下記號改由「連續隱藏 {HiddenReleaseFrames} 幀」解除。這一行每個窗名每次遊戲只寫一次。");
            }
            foreach(var addr in HiddenRemoveBuf) slot.Pressed.Remove(addr);
        }
    }

    /// <summary>同名窗的某個位址被銷毀(PreFinalize)或在該位址重新建立(PostSetup)時,只清那個位址的紀錄。</summary>
    private static void ReleaseAddress(string addonName, nint address)
    {
        if(address == 0 || Slots.Count == 0) return;
        foreach(var slot in Slots.Values)
        {
            if(slot.AddonName == addonName) slot.Pressed.Remove(address);
        }
    }

    /// <summary>掛上幀計數器用的 <c>Framework.Update</c> 監聽器(重複呼叫是 no-op)。</summary>
    /// <remarks>
    /// <see cref="Tick"/>(每個 framework tick 無條件進來)與 <see cref="TryPressOnce(string, nint, string, string, bool)"/>
    /// 兩邊都會叫:即使哪天 <c>Framework_Update</c> 裡那行 <c>Tick()</c> 被搬走或被條件包起來,時鐘也不會跟著停。
    /// <para>
    /// ⚠️ 這支多半是在 <c>Framework.Update</c> 的派送途中把自己加進<b>同一個事件</b>。多播委派是不可變的、
    /// 派送當下用的是加之前的那份實例,所以新監聽器從<b>下一個 tick</b> 才開始跑 —— 不會漏算也不會重複算。
    /// </para>
    /// </remarks>
    private static void EnsureFrameClock()
    {
        if(frameClockRunning) return;
        frameClockRunning = true;
        Svc.Framework.Update += OnFrameworkUpdate;
    }

    /// <summary>幀計數器的<b>後援</b>:只有在這個 tick 沒被 <see cref="Tick"/> 推進過時才補加一。</summary>
    /// <remarks>
    /// 涵蓋兩種 <see cref="Tick"/> 沒被叫到的情況:①哪天 <c>Framework_Update</c> 裡那行 <c>Tick()</c> 被搬走或被條件
    /// 包起來 ②鏈上排在 <c>Framework_Update</c> 前面的監聽器(ECommons 的 TaskManager)擲例外把它整支跳過。
    /// 本監聽器是最後掛上的,一定跑在 <see cref="Tick"/> 後面,所以旗標讀得到這個 tick 的結果。
    /// </remarks>
    private static void OnFrameworkUpdate(IFramework framework)
    {
        if(!tickedThisFrame) frameCount++;
        tickedThisFrame = false;
    }

    /// <summary>
    /// 第一次守護某個 addon 名稱時掛上解除封鎖用的監聽器。掛上去之後就不再拆(只在 <see cref="ForceTeardown"/> 拆):
    /// 這兩條監聽器只做一次字典移除,成本可忽略,而動態掛/拆比較容易留下懸空的監聽器。
    /// </summary>
    private static void EnsureWatching(string addonName)
    {
        if(Watchers.ContainsKey(addonName)) return;
        IAddonLifecycle.AddonEventDelegate handler = (_, args) => ReleaseAddress(addonName, args.Addon.Address);
        Watchers[addonName] = handler;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, addonName, handler);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, addonName, handler);
    }
}
