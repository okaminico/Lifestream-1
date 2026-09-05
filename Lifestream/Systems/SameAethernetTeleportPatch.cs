using Dalamud.Memory;

namespace Lifestream.Systems;

/// <summary>
/// 「允許傳送到目前所在的乙太之光」記憶體修補(移植自 DailyRoutines 的 SameAethernetTeleport)。
///
/// 遊戲在送出乙太之光網路傳送請求前，會先比對「你選的節點」與「你現在站的節點」，
/// 相同就直接顯示 LogMessage 1478「此處為目前所在地。」並中止，不送出請求。
/// 這個修補把那個判斷式的 <c>jne</c>(0x75) 改成無條件 <c>jmp</c>(0xEB)，讓它永遠跳過拒絕分支。
///
/// 🔴 使用者已明確裁決「這個風險在可控內」。仍要理解性質：
/// 送出的是原版客戶端不會送出的傳送請求，屬於記憶體修補而非封包偽造，但仍可能被視為非法用戶端行為。
/// 因此預設關閉，且設定畫面必須把風險寫清楚。
///
/// ── 離線鑑識結果(TC 7.20, ffxiv_dx11.exe 2026-07-28, image base 0x140000000) ────────────
/// 掃描器先以已知特徵碼校準：<c>E8 ?? ?? ?? ?? 85 C0 75 02 33 C0</c> → .text 唯一命中
/// 0x140894804，跟隨 E8 後為 0x140894420(ActionManager::GetActionInRangeOrLoS)，與既有記錄一致。
///
/// 兩顆修補點在台服的實際情形：
///
///   Patch A  0x140B0F609   75 1E            jne 0x140B0F629
///            落地(不跳)→ mov edx, 0x5C6 (=1478「此處為目前所在地。」) → jmp 0x14096B6D0(顯示訊息)
///            跳走      → cmp byte [rbx+0x1F], 0 → 未共鳴時 1477「尚未共鳴，無法傳送。」
///            判斷式本體：movzx eax, byte [r9+8] ; cmp al, bpl  (目前節點 vs 選取節點)
///            所屬函式 sub_140B0F5E0(RUNTIME_FUNCTION 0x140B0F5E0–0x140B0F6AA，起點即 prologue)
///
///   Patch B  0x140B0F809   75 1C            jne 0x140B0F827
///            落地(不跳)→ mov edx, 0x5C6 (=1478) → call 0x14096B6D0
///            跳走      → cmp byte [rbp+0x1F], 0 → mov edx, 0x5C5 (=1477) 保留
///            判斷式本體：movzx eax, byte [rdx+8] ; cmp al, bl
///            ⚠️ 它落在的 RUNTIME_FUNCTION 0x140B0F7F7–0x140B0F849 只是被切開的 chunk，
///            起點不是 prologue；沿 UNW_FLAG_CHAININFO 追到真正的函式進入點 sub_140B0F780。
///
/// 所以兩顆修補點**不在同一個函式**，是相鄰的兩個函式(DR 的兩條特徵碼形狀相似容易誤判成同一個)。
/// 兩者都不是內聯後的孤兒副本，xref 實測：
///   sub_140B0F5E0 ← call 0x140B0E344、jmp 0x140B0E5F5、jmp 0x140B0E6BE  (3 處)
///   sub_140B0F780 ← call 0x140B0E384                                   (1 處)
/// 均非死碼。1477/1478/10669 三筆 LogMessage 也已對過台服 EXD 實際文字。
///
/// ── 安全設計(比 DR 嚴格) ───────────────────────────────────────────────────────
/// DR 在型別載入時就把 MemoryPatch 建好、Init() 直接 Enable，沒有任何命中數驗證 ——
/// 特徵碼一旦漂移就會**靜默改寫別的碼**。這裡改成：
///   1. 自己數命中數，**命中數 != 1 一律不修補**，並寫一行 Information。
///   2. 額外驗證該位址目前的位元組確實是預期的 0x75，不是才不寫。
///   3. 記下原始位元組，停用/卸載時逐一還原。
///   4. 位址解析成功後快取，重複開關不重掃。
/// 這三道閘門讓「特徵碼假設不成立」的結果退化成「功能不啟用」，而不是改壞遊戲。
/// </summary>
public static unsafe class SameAethernetTeleportPatch
{
    /// <summary>條件跳躍 jne(0x75)：判斷成立(選取節點==目前節點)時**不跳**，落地顯示拒絕訊息。</summary>
    private const byte ExpectedOriginalByte = 0x75;

    /// <summary>改成無條件 jmp(0xEB)：永遠跳過拒絕分支。</summary>
    private const byte PatchedByte = 0xEB;

    private sealed class Site(string name, string signature)
    {
        public readonly string Name = name;
        public readonly string Signature = signature;
        public nint Address;
        public byte OriginalByte;
        public bool Applied;
    }

    private static readonly Site[] Sites =
    [
        new("A", "75 ?? 48 8B 49 ?? 48 8B 01 FF 50 ?? 48 8B C8 BA ?? ?? ?? ?? 48 83 C4 ?? 5E 5D"),
        new("B", "75 ?? 48 8B 4E ?? 48 8B 01 FF 50 ?? 48 8B C8 BA ?? ?? ?? ?? E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 80 7D"),
    ];

    /// <summary>解析是否已跑過(不論成功失敗)。失敗不重試：特徵碼不會在同一場遊戲中途變好。</summary>
    private static bool Resolved;

    /// <summary>解析失敗的原因，給設定畫面顯示。null = 沒有問題。</summary>
    public static string ResolveError { get; private set; }

    public static bool IsApplied => Sites.All(x => x.Applied);

    /// <summary>
    /// 啟用修補。任何一顆解析或驗證失敗就整組不啟用(不做半套)，並回傳 false。
    /// </summary>
    public static bool Enable()
    {
        if(IsApplied) return true;
        if(!Resolve()) return false;

        foreach(var site in Sites)
        {
            if(site.Applied) continue;
            try
            {
                var old = MemoryHelper.ChangePermission(site.Address, 1, MemoryProtection.ExecuteReadWrite);
                *(byte*)site.Address = PatchedByte;
                MemoryHelper.ChangePermission(site.Address, 1, old);
                site.Applied = true;
                PluginLog.Information($"[SameAethernetTeleport] Patch {site.Name} applied at 0x{site.Address:X} ({ExpectedOriginalByte:X2} -> {PatchedByte:X2}).");
            }
            catch(Exception e)
            {
                ResolveError = $"Failed to write patch {site.Name}: {e.Message}";
                PluginLog.Error($"[SameAethernetTeleport] {ResolveError}");
                // 已經寫進去的要收回，不留半套狀態。
                Disable();
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 還原所有已套用的修補。卸載外掛時一定要呼叫 —— 否則遊戲會帶著被改過的碼繼續跑。
    /// </summary>
    public static void Disable()
    {
        foreach(var site in Sites)
        {
            if(!site.Applied) continue;
            try
            {
                var old = MemoryHelper.ChangePermission(site.Address, 1, MemoryProtection.ExecuteReadWrite);
                *(byte*)site.Address = site.OriginalByte;
                MemoryHelper.ChangePermission(site.Address, 1, old);
                site.Applied = false;
                PluginLog.Information($"[SameAethernetTeleport] Patch {site.Name} reverted at 0x{site.Address:X} (restored {site.OriginalByte:X2}).");
            }
            catch(Exception e)
            {
                // 還原失敗只能記錄 —— 這是唯一一種會留下痕跡的失敗，要看得見。
                PluginLog.Error($"[SameAethernetTeleport] FAILED to revert patch {site.Name} at 0x{site.Address:X}: {e.Message}");
            }
        }
    }

    /// <summary>
    /// 解析兩顆修補點的位址。**命中數必須剛好是 1**，否則整組放棄。
    /// 這是「假設不成立也不會壞」的關鍵閘門：沒有它，特徵碼漂移就等於隨機改寫程式碼。
    /// </summary>
    private static bool Resolve()
    {
        if(Resolved) return ResolveError == null;
        Resolved = true;

        try
        {
            var textBase = Svc.SigScanner.TextSectionBase;
            var textSize = Svc.SigScanner.TextSectionSize;
            if(textBase == nint.Zero || textSize <= 0)
            {
                ResolveError = "Could not determine the game's .text section.";
                PluginLog.Information($"[SameAethernetTeleport] {ResolveError}");
                return false;
            }

            var text = new ReadOnlySpan<byte>((void*)textBase, textSize);
            foreach(var site in Sites)
            {
                ParseSignature(site.Signature, out var pattern, out var mask);
                var hits = ScanAll(text, textBase, pattern, mask, limit: 8);

                // 命中數一律寫 Information —— 使用者的記錄等級只會濾掉 Verbose、Debug 收得到但單檔數十萬行會淹沒，
                // 而這正是「功能沒作用時第一個要看的數字」。
                PluginLog.Information($"[SameAethernetTeleport] Signature {site.Name}: {hits.Count} match(es) in .text"
                    + (hits.Count > 0 ? $" [{hits.Select(x => $"0x{x:X}").Print(", ")}]" : ""));

                if(hits.Count != 1)
                {
                    ResolveError = $"Signature {site.Name} matched {hits.Count} times (expected exactly 1) - patch not applied.";
                    PluginLog.Information($"[SameAethernetTeleport] {ResolveError}");
                    return false;
                }

                var addr = hits[0];
                var current = *(byte*)addr;
                if(current != ExpectedOriginalByte)
                {
                    ResolveError = $"Signature {site.Name} resolved to 0x{addr:X} but the byte there is 0x{current:X2}, expected 0x{ExpectedOriginalByte:X2} - patch not applied.";
                    PluginLog.Information($"[SameAethernetTeleport] {ResolveError}");
                    return false;
                }

                site.Address = addr;
                site.OriginalByte = current;
            }

            // 兩顆都解析成功才算數。順帶把兩者距離寫進 log：離線鑑識時它們相差 0x200，
            // 差太多代表其中一條特徵碼很可能解到不相干的地方。
            PluginLog.Information($"[SameAethernetTeleport] Resolved A=0x{Sites[0].Address:X} B=0x{Sites[1].Address:X} (delta 0x{Math.Abs(Sites[1].Address - Sites[0].Address):X}).");
            ResolveError = null;
            return true;
        }
        catch(Exception e)
        {
            ResolveError = $"Signature resolution threw: {e.Message}";
            PluginLog.Error($"[SameAethernetTeleport] {ResolveError}");
            return false;
        }
    }

    /// <summary>"75 ?? 48" → pattern[] + mask[](true=必須相符)。</summary>
    private static void ParseSignature(string signature, out byte[] pattern, out bool[] mask)
    {
        var tokens = signature.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        pattern = new byte[tokens.Length];
        mask = new bool[tokens.Length];
        for(var i = 0; i < tokens.Length; i++)
        {
            if(tokens[i] is "??" or "?")
            {
                pattern[i] = 0;
                mask[i] = false;
            }
            else
            {
                pattern[i] = Convert.ToByte(tokens[i], 16);
                mask[i] = true;
            }
        }
        if(!mask[0]) throw new ArgumentException($"Signature must not start with a wildcard: {signature}");
    }

    /// <summary>
    /// 掃出**所有**命中。Dalamud 的 ScanText 只回第一個，數不出命中數，所以自己走一遍。
    /// 直接在對映的 .text 上讀取(不複製 33MB)，第一個位元組用 Span.IndexOf 向量化跳躍。
    /// </summary>
    private static List<nint> ScanAll(ReadOnlySpan<byte> haystack, nint haystackBase, byte[] pattern, bool[] mask, int limit)
    {
        var result = new List<nint>();
        var last = haystack.Length - pattern.Length;
        var i = 0;
        while(i <= last)
        {
            var rel = haystack[i..(last + 1)].IndexOf(pattern[0]);
            if(rel < 0) break;
            i += rel;

            var ok = true;
            for(var j = 1; j < pattern.Length; j++)
            {
                if(mask[j] && haystack[i + j] != pattern[j])
                {
                    ok = false;
                    break;
                }
            }
            if(ok)
            {
                result.Add(haystackBase + i);
                if(result.Count >= limit) break;
            }
            i++;
        }
        return result;
    }
}
