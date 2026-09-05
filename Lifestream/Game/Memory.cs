using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using ECommons.EzHookManager;
using ECommons.MathHelpers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lifestream.Enums;
using Lifestream.Tasks.SameWorld;
using System.Windows.Forms;

namespace Lifestream.Game;

public unsafe class Memory : IDisposable
{
    internal delegate void AddonDKTWorldList_ReceiveEventDelegate(nint a1, short a2, nint a3, AtkEvent* a4, InputData* a5);
    [Signature("48 89 74 24 ?? 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 ?? F6 81", DetourName = nameof(AddonDKTWorldList_ReceiveEventDetour), Fallibility = Fallibility.Fallible)]
    internal Hook<AddonDKTWorldList_ReceiveEventDelegate> AddonDKTWorldList_ReceiveEventHook;

    internal delegate void AtkComponentTreeList_vf31(nint a1, uint a2, byte a3);
    [Signature("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 8B DA 41 0F B6 F0", DetourName = nameof(AtkComponentTreeList_vf31Detour), Fallibility = Fallibility.Fallible)]
    internal Hook<AtkComponentTreeList_vf31> AtkComponentTreeList_vf31Hook;

    // MaxInstances(當前分線區的分線總數)靜態位址。
    // 上游 sig "4C 8D 0D ?? ?? ?? ?? 4C 8B 11 48 8B D9" 在台服 7.20 執行檔上 0 命中(已離線驗證)。
    // 下面這條是離線重新定位到的候選 sig,在台服執行檔唯一命中,解析出的靜態位址 = 0x14294D1C0
    // (lea r9,[rip+0x1e0a7bc] @ 0x140B429FD;ScanType.StaticAddress 走 iced 解 lea 的 MemoryDisplacement64,與上游同形)。
    // 執行期語意仍待實機自證,見 InstanceHandler.OnPostUpdate 的 [MaxInstances驗證] 診斷。
    // fail-closed:維持 Fallibility.Fallible —— sig 掃不到時欄位停在 null(default(int*)),
    //   消費端(InstanceHandler / UIDebug)的判空 fallback 照舊生效,不因填了 sig 而移除那道防護。
    [Signature("4C 8D 0D ?? ?? ?? ?? 4C 8B 11 44 0F B7 41 78 48 8B 91 80 00 00 00", ScanType = ScanType.StaticAddress, Fallibility = Fallibility.Fallible)]
    internal int* MaxInstances;

    internal delegate byte OpenPartyFinderInfoDelegate(void* agentLfg, ulong contentId);
    [EzHook("40 53 48 83 EC 20 48 8B D9 E8 ?? ?? ?? ?? 84 C0 74 07 C6 83 ?? ?? ?? ?? ?? 48 83 C4 20 5B C3 CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC 40 53", false)]
    internal EzHook<OpenPartyFinderInfoDelegate> OpenPartyFinderInfoHook;

    internal delegate nint IsFlightProhibitedDelegate(nint a1);
    internal IsFlightProhibitedDelegate IsFlightProhibited = EzDelegate.Get<IsFlightProhibitedDelegate>("40 53 48 83 EC 20 48 8B 1D ?? ?? ?? ?? 48 85 DB 0F 84 ?? ?? ?? ?? 80 3D");
    internal nint FlightAddr = Svc.SigScanner.TryScanText("48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 84 C0 75 11", out var result) ? result : default;

    // fail-closed:detour 是「原生程式碼直接呼叫的受管理函式」,受管理例外從這裡逸出會穿過
    // 沒有 SEH handler 的原生框架,行程直接被終止。所以這三支的自訂邏輯(全部只是記錄)一律進 try,
    // 而 **Original 一律留在 try 外照樣呼叫** —— 我們的記錄失敗絕不能改變遊戲原本的行為。
    // ⚠️ try 攔不到 AccessViolationException(在 .NET Core 是 corrupted-state exception)。
    //    針對裸指標的防護是下面那些判空,不是 try。
    private long _detourErrors;
    private DateTime _lastDetourErrorLog = DateTime.MinValue;

    private void OnDetourError(string site, Exception ex)
    {
        ++_detourErrors;
        // 節流:這些 detour 可能連續觸發,不節流會把 log 灌爆反而讓使用者回報不出東西。
        // Information 而不是 Debug —— 回報問題的使用者跑 LogLevel 1。
        var now = DateTime.UtcNow;
        if(now - _lastDetourErrorLog < TimeSpan.FromSeconds(30))
            return;
        _lastDetourErrorLog = now;
        PluginLog.Information($"[Memory] {site} 的診斷記錄擲出受管理例外,已吞下、Original 照常執行(累計 {_detourErrors}): {ex}");
    }

    internal byte OpenPartyFinderInfoDetour(void* agentLfg, ulong contentId)
    {
        try
        {
            PluginLog.Information($"{((nint)agentLfg):X16}, {contentId:X16}");
        }
        catch(Exception ex)
        {
            OnDetourError(nameof(OpenPartyFinderInfoDetour), ex);
        }
        return OpenPartyFinderInfoHook.Original(agentLfg, contentId);
    }

    private void AtkComponentTreeList_vf31Detour(nint a1, uint a2, byte a3)
    {
        try
        {
            PluginLog.Debug($"AtkComponentTreeList_vf31Detour: {a1:X16}, {a2}, {a3}");
        }
        catch(Exception ex)
        {
            OnDetourError(nameof(AtkComponentTreeList_vf31Detour), ex);
        }
        AtkComponentTreeList_vf31Hook.OriginalDisposeSafe(a1, a2, a3);
    }

    private void AddonDKTWorldList_ReceiveEventDetour(nint a1, short a2, nint a3, AtkEvent* a4, InputData* a5)
    {
        try
        {
            PluginLog.Debug($"AddonDKTWorldCheck_ReceiveEventDetour: {a1:X16}, {a2}, {a3:X16}, {(nint)a4:X16}, {(nint)a5:X16}");
            // 🔴 a4/a5 是遊戲(或 ConstructEvent)交進來的裸指標,解參考失敗是攔不到的 AVE ⇒ 只能靠判空。
            if(a4 != null)
                PluginLog.Debug($"  Event: {(nint)a4->Node:X16}, {(nint)a4->Target:X16}, {(nint)a4->Listener:X16}, {a4->Param}, {(nint)a4->NextEvent:X16}, {a4->State.EventType}, {a4->State.ReturnFlags}, {a4->State.StateFlags}");
            if(a5 != null)
            {
                // InputData.unk_8s 是 (UnknownStruct*)*unk_8 —— 兩層解參考,兩層都要驗
                var inner = a5->unk_8 != null ? *a5->unk_8 : (nint)0;
                if(inner != 0)
                    PluginLog.Debug($"  Data: {(nint)a5->unk_8:X16}({inner:X16}/{inner:X16}), [{a5->unk_8s->unk_4}/{a5->unk_8s->SelectedItem}] {a5->unk_16}, {a5->unk_24} | "); //{a5->RawDumpSpan.ToArray().Print()}
                else
                    PluginLog.Debug($"  Data: {(nint)a5->unk_8:X16}(null), {a5->unk_16}, {a5->unk_24} | ");
            }
            //var span = new Span<byte>((void*)*a5->unk_8, 0x40).ToArray().Select(x => $"{x:X2}");
            //PluginLog.Debug($"  Data 2, {a5->unk_8s->unk_4}, {MemoryHelper.ReadRaw((nint)a5->unk_8s->CategorySelection, 4).Print(",")},  :{string.Join(" ", span)}");
        }
        catch(Exception ex)
        {
            OnDetourError(nameof(AddonDKTWorldList_ReceiveEventDetour), ex);
        }
        AddonDKTWorldList_ReceiveEventHook.OriginalDisposeSafe(a1, a2, a3, a4, a5);
    }

    internal void ConstructEvent(AtkUnitBase* addon, int category, int which, int nodeIndex, int itemToSelect, int itemToHighlight)
    {
        if(itemToSelect == 0) throw new Exception("Enumeration starts with 1");
        // 🔴 nodeIndex 這條鏈原本從頭到尾零判空,而且是「合成事件送進遊戲原生碼」的路徑,
        //    錯的指標不是崩在這裡就是崩在遊戲裡:
        //    ①NodeList[nodeIndex] 上界沒驗 —— 越界讀到的是相鄰記憶體而不是 null,
        //      Target 於是變成一個看起來合法的垃圾指標,直接交給 ReceiveEvent。
        //    ②GetAsAtkComponentNode() 是 [MemberFunction],對 null 節點呼叫＝把 this = 0
        //      交給原生碼(AVE 是 corrupted-state exception,try/catch 攔不到)。
        //    ③&...->Component->AtkEventListener 是取位址不是解參考:Component 為 null 時
        //      **不會當場崩**,而是靜默算出毒指標交給遊戲 —— 崩潰現場指不到這一行。
        //    取不到就整個不送事件直接 return:呼叫端(DCChange 的世界/資料中心選取)在這之後
        //    一律 DCRethrottle() 並 return false,也就是下一輪重試,不會被當成已經選好了。
        var listNode = GetNodeSafe(addon == null ? null : &addon->UldManager, nodeIndex);
        var listComponentNode = listNode == null ? null : listNode->GetAsAtkComponentNode();
        if(listComponentNode == null || listComponentNode->Component == null)
        {
            PluginLog.Information($"ConstructEvent: NodeList[{nodeIndex}] 的清單元件取不到(版面未建好或已拆除),這一輪不送事件");
            return;
        }
        // 對 LobbyDKTWorldList 合成 ListItemClick 是「選了還沒生效就再選一次」的刻意重試迴圈(呼叫端 DCThrottle+500ms):
        // 粒度含 (which, category, itemToSelect),同位址不同項目照常放行;同位址同項目在 15 幀內不重送(清單選取不關窗,
        // 走多次互動窗的逃生口),擋的是外部關閉(使用者取消/逾時)落在輪詢期間的那幾幀。被擋就整個不送,呼叫端一律 DCRethrottle+return false。
        if(!AddonPressGuard.TryPressOnce("LobbyDKTWorldList", addon, nameof(ConstructEvent), paramKey: $"{which}|{category}|{itemToSelect}", escapeIsRoutine: true)) return;
        var Event = stackalloc AtkEvent[1]
        {
            new AtkEvent()
            {
                Node = null,
                Target = (AtkEventTarget*)listNode,
                Listener = &listComponentNode->Component->AtkEventListener,
                Param = 1,
                NextEvent = null,
                State = new()
                {
                    EventType = AtkEventType.ListItemClick,
                    ReturnFlags = 0,
                    StateFlags = 0,
                    UnkFlags3 = 0,
                }
            }
        };
        var Unk = stackalloc UnknownStruct[1]
        {
            new()
            {
                unk_4 = 1,
                SelectedItem = itemToSelect - 1 + (category << 8)
            }
        };
        var ptr = stackalloc nint[1]
        {
            (nint)Unk
        };
        var Data = stackalloc InputData[1]
        {
            new InputData()
            {
                unk_8 = ptr,
                unk_16 = itemToSelect,
                unk_24 = 0,
            }
        };
        AddonDKTWorldList_ReceiveEventDetour((nint)addon, 35, which, Event, Data);
        // 🔴 原本直接把 GetAsAtkComponentList() 的回值(可能是 null)當 this 傳進 vf31 ——
        //    那支原生函式第一件事就是解參考 a1。
        //    ⚠️ 這裡刻意只跳過「反白」這一步而不是連上面的選取事件一起擋掉:
        //    GetAsAtkComponentList() 對「型別不是 List 的元件」本來就可能合法地回 null,
        //    把它當成整個 ConstructEvent 的前置條件等於順手收緊既有行為。
        var treeList = listNode->GetAsAtkComponentList();
        if(treeList == null)
        {
            PluginLog.Information($"ConstructEvent: NodeList[{nodeIndex}] 取不到 AtkComponentList,略過反白(選取事件已送出)");
            return;
        }
        AtkComponentTreeList_vf31Detour((nint)treeList, (uint)itemToHighlight, 0);
    }

    internal Memory()
    {
        SignatureHelper.Initialise(this);
        EzSignatureHelper.Initialize(this);
        //AddonDKTWorldList_ReceiveEventHook.Enable();
    }

    public void Dispose()
    {
        AddonDKTWorldList_ReceiveEventHook?.Disable();
        AddonDKTWorldList_ReceiveEventHook?.Dispose();
        AtkComponentTreeList_vf31Hook?.Disable();
        AtkComponentTreeList_vf31Hook?.Dispose();
    }
}
