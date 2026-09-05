using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace Lifestream.Movement;

// NOTE: the old hand-rolled `CameraEx` struct is gone on purpose (same fix as vnavmesh on TC 7.20).
// It carried hardcoded FieldOffsets that go stale every game patch: TC 7.20 shifted the real layout
// +0x10, so the 0x130-based offsets were reading FoV/MinFoV/MaxFoV as DirH/DirV — which is why
// legacy-mode path movement steered in a garbage direction (OverrideMovement uses DirH as its
// reference). FFXIVClientStructs.FFXIV.Client.Game.Camera has all the fields we need and is
// maintained/verified against the API13 pin we build on, so use it directly and let the pin track
// layout changes for us. CameraManager::GetActiveCamera() already returns Camera*, so no cast is
// needed either.

public unsafe class OverrideCamera : IDisposable
{
    public bool Enabled
    {
        get => _rmiCameraHook?.IsEnabled ?? false;
        set
        {
            if(_rmiCameraHook == null)
                return;
            if(value)
                _rmiCameraHook.Enable();
            else
                _rmiCameraHook.Disable();
        }
    }

    public bool IgnoreUserInput; // if true - override even if user tries to change camera orientation, otherwise override only if user does nothing
    public Angle DesiredAzimuth;
    public Angle DesiredAltitude;
    public Angle SpeedH = 360.Degrees(); // per second
    public Angle SpeedV = 360.Degrees(); // per second

    private delegate void RMICameraDelegate(Camera* self, int inputMode, float speedH, float speedV);
    // The upstream prologue signature (40 53 48 83 EC 70 44 0F 29 44 24 ?? 48 8B D9) still scans on
    // TC 7.20 but resolves to the WRONG function (0x...79110, while the verified camera-input
    // function is 0x...ED0B0 — cross-checked against vnavmesh's resolved address in the same game
    // session, with RMIWalk matching between both plugins as the base-address control). Switched to
    // the prologue signature vnavmesh verified on TC 7.20. Kept fallible so a future mismatch
    // degrades to "no camera auto-facing" instead of failing the whole plugin load.
    [Signature("48 8B C4 53 48 81 EC ?? ?? ?? ?? 44 0F 29 50 ??", Fallibility = Fallibility.Fallible)]
    private Hook<RMICameraDelegate>? _rmiCameraHook;

    public OverrideCamera()
    {
        Svc.Hook.InitializeFromAttributes(this);
        if(_rmiCameraHook != null)
            PluginLog.Information($"RMICamera address: 0x{_rmiCameraHook.Address:X}");
        else
            PluginLog.Error("RMICamera signature not found - camera auto-facing disabled");
    }

    public void Dispose()
    {
        _rmiCameraHook?.Dispose();
    }

    // fail-closed: a detour is a managed function the *native* code calls directly, so a managed
    // exception escaping it unwinds through native frames that have no handler for it. Everything we
    // add on top of Original() therefore runs inside a try, and the degraded behaviour is "don't
    // override" - Original has already run, so the game's own camera handling passes through intact.
    // NOTE: this does NOT protect against AccessViolationException (corrupted-state, uncatchable in
    // .NET Core). What it catches is managed exceptions - most importantly the
    // InvalidOperationException that ClientStructs' [StaticAddress]/[MemberFunction] members throw
    // when their signature stops resolving after a game patch.
    private long _detourErrors;
    private DateTime _lastDetourErrorLog = DateTime.MinValue;

    private void OnDetourError(Exception ex)
    {
        ++_detourErrors;
        // this runs per frame - never log unthrottled. Information (not Debug) because reporting
        // users run at LogLevel 1 - Debug is captured too, but drowned by the 100k+ Debug lines a single log file holds.
        var now = DateTime.UtcNow;
        if(now - _lastDetourErrorLog < TimeSpan.FromSeconds(30))
            return;
        _lastDetourErrorLog = now;
        PluginLog.Information($"OverrideCamera: camera override threw, leaving the game's own camera input alone (total {_detourErrors}): {ex}");
    }

    private void RMICameraDetour(Camera* self, int inputMode, float speedH, float speedV)
    {
        _rmiCameraHook!.OriginalDisposeSafe(self, inputMode, speedH, speedV);
        try
        {
            if(self == null)
                return;
            if(IgnoreUserInput || inputMode == 0) // let user override...
            {
                // 🔴 這裡是每幀跑的相機 detour。Framework 是 [StaticAddress(..., isPointer: true)],
                //    合法回 null;在 detour 裡對 null 解參考等於崩在原生層(AVE，try/catch 攔不到)。
                //    拿不到就用 dt = 0 —— 這一幀的 maxH/maxV 都是 0，等於「不動相機」，
                //    絕不在這裡丟例外，也絕不寫 log(每幀熱路徑)。
                var framework = Framework.Instance();
                var dt = framework == null ? 0f : framework->FrameDeltaTime;
                var deltaH = (DesiredAzimuth - self->DirH.Radians()).Normalized();
                var deltaV = (DesiredAltitude - self->DirV.Radians()).Normalized();
                var maxH = SpeedH.Rad * dt;
                var maxV = SpeedV.Rad * dt;
                self->InputDeltaH = Math.Clamp(deltaH.Rad, -maxH, maxH);
                self->InputDeltaV = Math.Clamp(deltaV.Rad, -maxV, maxV);
            }
        }
        catch(Exception ex)
        {
            OnDetourError(ex);
        }
    }
}
