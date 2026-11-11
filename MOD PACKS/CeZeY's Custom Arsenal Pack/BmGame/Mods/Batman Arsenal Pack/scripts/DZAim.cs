using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using BmSDK;
using BmSDK.Framework;
using BmSDK.Engine;
using BmSDK.BmGame;
using BmSDK.BmScript;

namespace DeadZoneAim;

[Script]
public partial class DZAim : Script
{
    const string Version = "0.12";
    static readonly string Dir = DZPaths.Dir;
    static readonly string LogPath = Dir + "aim_log.txt";
    static readonly string IniPath = Dir + "DZAim.ini";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    const float D2R = (float)(Math.PI / 180.0), R2D = (float)(180.0 / Math.PI);

    sealed class Cfg
    {
        public bool Enabled = true;
        public bool DSOnly = true;
        public int Mode;
        public float TestDeg = 10f;
        public float ZoneX = 0.8f, ZoneY = 0.8f;
        public float Stiffness = 60f, Damping = 24.5f;
        public float SoftStart = 0.7f, SoftCurve = 2f;
        public float Orbit = 1f;
        public int PivotSource = 2;
        public float PivotZ = 60f;
        public float ResetJumpDeg = 34f, ResetMoveUU = 150f, ReleaseTau = 0.12f;
        public int ReticleMode = 1;
        public int AimSource;
        public float TraceDist = 20000f;
        public string CamStates = "*";
        public float LogInterval = 1f;
        public bool FP;
        public float FPTurnDeg = 45f;
        public bool FPSniperNoZoom = true, FPAimFromEye = true;
        public float FPRunFwd = 15f;
        public float FPFwd = 10f, FPUp = 6f, FPFovScale = 1.1f, FPSmooth = 0.05f, FPBlend = 0.2f;
        public bool FPFace = true, FPTpsSpecial = true;
        public string FPTpsStates = "";
        public bool RecoilOn = true;
        public float RStrength = 2f, RRecover = 0.5f, RKickK = 250f, RKickD = 20f, RBurstWindow = 0.25f, RBurstScale = 0.6f;
        public int RMethod;
        public GunRecoil RPistol = new(1.4f, 0.5f, 0.4f, 0.4f, 0.6f), RShotgun = new(4.5f, 2f, 1.2f, 0.35f, 3.5f),
                         RSniper = new(5f, 2.5f, 0.8f, 0.8f, 3f), RAuto = new(0.8f, 0.35f, 0.4f, 0.5f, 0.35f);
        public bool RPunchOn = true;
        public float RPunchK = 400f, RPunchD = 22f, RPunchRoll = 0.35f, RPunchFov = 0.6f;
        public float AimSens = 0.6f;
        public int RestoreAt;
    }
    static Cfg C = new();
    static readonly HashSet<string> States = new(), ExcludedStates = new();
    static bool AnyState = true;
    static bool ExtraAllowed() { try { return AppDomain.CurrentDomain.GetData("DZAim.Allow") as string == "1" || AppDomain.CurrentDomain.GetData("DZAim.AllowRifle") as string == "1"; } catch { return false; } }
    static bool CamAllowed(string s) =>!ExcludedStates.Contains(s) && (AnyState || States.Contains(s));

    sealed class Zone
    {
        public float RetX, RetY, VelX, VelY;
        public void Reset() { RetX = RetY = VelX = VelY = 0; }

        public void OnDelta(float dx, float dy)
        {
            float nx = RetX + dx, ny = RetY + dy;
            float dist = MathF.Sqrt(nx * nx + ny * ny);
            float soft = C.SoftStart;
            if (dist > soft && soft < 1f)
            {
                float t = Math.Clamp((dist - soft) / (1f - soft), 0f, 1f);
                float leak = MathF.Pow(t, C.SoftCurve);
                RetX += dx * (1f - leak); RetY += dy * (1f - leak);
            }
            else { RetX = nx; RetY = ny; }
            float l = MathF.Sqrt(RetX * RetX + RetY * RetY);
            if (l > 1f) { RetX /= l; RetY /= l; }
        }

        public void Step(float dt)
        {
            VelX += (RetX * C.Stiffness - VelX * C.Damping) * dt;
            VelY += (RetY * C.Stiffness - VelY * C.Damping) * dt;
            RetX -= VelX * dt; RetY -= VelY * dt;
            if (!float.IsFinite(RetX) || !float.IsFinite(RetY) || !float.IsFinite(VelX) || !float.IsFinite(VelY)) Reset();
        }
    }
    static readonly Zone Z = new();

    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static bool HavePrev, Live, WasActive;
    static float PrevYaw, PrevPitch, PrevT;
    static Vector3 PrevLoc;
    static float RetYawDeg, RetPitchDeg;
    static float AxRad = 0.5f, AyRad = 0.3f;
    static Vector3 Pivot; static bool PivotOk; static float OrbitW;
    static Vector3 RealLoc; static Rotator RealRot; static float RealFov = 60f; static float Aspect = 16f / 9f;
    static string CamState = "", LastCamState = "", LastGadget = "";
    static float LastStepT;
    static float FrameDYaw, FrameDPitch;

    static int nPT, nCamUpd, nUpdCam, nHud, nTick, nSteps;
    static float SumAbsRet, MaxRet;
    static float NextLog;
    static int TraceFrames; static readonly StringBuilder Seq = new();
    static string LastFrameSeq = "";

    static void L(string s)
    {
        BmSDK.Framework.Debug.Log("[DZAim] " + s);
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff ") + s + Environment.NewLine); } catch { }
    }
    static void Mark(char c) { if (TraceFrames > 0 && Seq.Length < 400) Seq.Append(c); }
    static string F(float v) => v.ToString("0.###", Inv);
    static string V(Vector3 v) => $"({F(v.X)},{F(v.Y)},{F(v.Z)})";
    static string R(Rotator r) => $"[p{F(r.Pitch)} y{F(r.Yaw)} r{F(r.Roll)}]";
    static float Wrap180(float a) { a %= 360f; if (a > 180f) a -= 360f; if (a < -180f) a += 360f; return a; }
    static float Now => (float)Clock.Elapsed.TotalSeconds;

    public override void Main()
    {
        LoadIni(true);
        L($"==== DZAim {Version} loaded: Enabled={C.Enabled} DeathstrokeOnly={C.DSOnly} Mode={C.Mode} zone {F(C.ZoneX)}x{F(C.ZoneY)} k={F(C.Stiffness)} c={F(C.Damping)} states [{C.CamStates}] recoil={(C.RecoilOn ? "ON" : "OFF")} x{F(C.RStrength)} punch={(C.RPunchOn ? "ON" : "OFF")}");
    }

    public override void OnEnterGame() { HardReset("enter game"); Live = false; FPReset(); }

    public override void OnUnload() { FPReset(); }

    static bool RVOn; static Vector3 RVLoc; static Rotator RVRot; static float RVFov;
    static bool ComputeRenderView(out Vector3 vl, out Rotator vr, out float fov)
    {
        vl = RealLoc; vr = RealRot; fov = RealFov;
        bool any = false;
        if (C.Enabled && C.Mode == 1 && InAimState) { vr = new Rotator(RealRot.Pitch, RealRot.Yaw + C.TestDeg, RealRot.Roll); any = true; }
        else if (C.Mode == 0 && Live) { Lagged(RealLoc, RealRot, out vl, out vr); any = true; }
        if (FPW > 0f && FPHeadOk) { FPApply(ref vl, vr, ref fov); any = true; }
        if (ApplyPunch(ref vr, ref fov)) any = true;
        return any;
    }

    static DateTime IniStamp; static float NextIniCheck;

    static void LoadIni(bool first)
    {
        try
        {
            if (!File.Exists(IniPath)) { WriteDefaultIni(); }
            IniStamp = File.GetLastWriteTimeUtc(IniPath);
            var c = new Cfg();
            foreach (var raw in File.ReadAllLines(IniPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[') continue;
                int eq = line.IndexOf('='); if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
                int sc = v.IndexOf(';'); if (sc >= 0) v = v.Substring(0, sc).Trim();
                float f() => float.Parse(v, Inv);
                switch (k)
                {
                    case "enabled": c.Enabled = v != "0"; break;
                    case "deathstroke_only": c.DSOnly = v != "0"; break;
                    case "mode": c.Mode = (int)f(); break;
                    case "test_deg": c.TestDeg = f(); break;
                    case "zone_x": c.ZoneX = f(); break;
                    case "zone_y": c.ZoneY = f(); break;
                    case "stiffness": c.Stiffness = f(); break;
                    case "damping": c.Damping = f(); break;
                    case "soft_start": c.SoftStart = f(); break;
                    case "soft_curve": c.SoftCurve = f(); break;
                    case "orbit": c.Orbit = f(); break;
                    case "pivot_source": c.PivotSource = (int)f(); break;
                    case "pivot_z": c.PivotZ = f(); break;
                    case "reset_jump_deg": c.ResetJumpDeg = f(); break;
                    case "reset_move_uu": c.ResetMoveUU = f(); break;
                    case "release_tau": c.ReleaseTau = f(); break;
                    case "reticle_mode": c.ReticleMode = (int)f(); break;
                    case "aim_source": c.AimSource = (int)f(); break;
                    case "trace_dist": c.TraceDist = f(); break;
                    case "cam_states": c.CamStates = v; break;
                    case "log_interval": c.LogInterval = f(); break;
                    case "restore_at": c.RestoreAt = (int)f(); break;
                    case "aim_sens": c.AimSens = f(); break;
                    default: if (!RecoilKey(c, k, v)) FPKey(c, k, v); break;
                }
            }
            if (!(c.ZoneX > 0.01f)) c.ZoneX = 0.8f;
            if (!(c.ZoneY > 0.01f)) c.ZoneY = 0.8f;
            if (!(c.Stiffness >= 0)) c.Stiffness = 60; if (!(c.Damping >= 0)) c.Damping = 24.5f;
            if (!(c.SoftStart >= 0 && c.SoftStart <= 1)) c.SoftStart = 0.7f;
            if (!(c.ReleaseTau > 0.005f)) c.ReleaseTau = 0.12f;
            if (!(c.AimSens > 0.01f && c.AimSens <= 5f)) c.AimSens = 0.6f;
            C = c;
            States.Clear(); ExcludedStates.Clear(); AnyState = false;
            foreach (var s in c.CamStates.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (s == "*") AnyState = true;
                else if (s.StartsWith("-")) ExcludedStates.Add(s.Substring(1));
                else States.Add(s);
            }
            if (!first) L($"ini reloaded: Enabled={C.Enabled} Mode={C.Mode} zone {F(C.ZoneX)}x{F(C.ZoneY)} k={F(C.Stiffness)} c={F(C.Damping)} soft={F(C.SoftStart)} orbit={F(C.Orbit)} pivot={C.PivotSource} reticle={C.ReticleMode} aim={C.AimSource} states [{C.CamStates}] recoil={(C.RecoilOn ? "ON" : "OFF")} x{F(C.RStrength)} punch={(C.RPunchOn ? "ON" : "OFF")}");
        }
        catch (Exception e) { L("EXC ini: " + e.Message); }
    }

    static void SaveIniValue(string key, string value)
    {
        try
        {
            var lines = new List<string>(File.ReadAllLines(IniPath));
            bool done = false;
            for (int i = 0; i < lines.Count; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) { lines[i] = key + "=" + value; done = true; break; }
            }
            if (!done) lines.Add(key + "=" + value);
            File.WriteAllLines(IniPath, lines, new UTF8Encoding(false));
            IniStamp = File.GetLastWriteTimeUtc(IniPath);
        }
        catch (Exception e) { L("EXC save ini: " + e.Message); }
    }

    static void WriteDefaultIni()
    {
        var s = @"; Dead Zone Aiming for Batman: Arkham Knight — settings. The file is hot-reloaded (save it — takes effect immediately).
[aim]
; 1 — mod enabled
enabled=1
; 1 — only for Deathstroke (Playable Deathstroke mod); 0 — for every character and the Batmobile
deathstroke_only=1
; 0 — dead zone; 1 — TEST: picture rotated by test_deg in yaw (set here only)
mode=0
test_deg=10
; Zone size: zone_x — fraction of half the screen width, zone_y — of half the height (1.0 = screen edge)
zone_x=0.8
zone_y=0.8
; Spring with which the picture catches up with the reticle (bobot: 150/24.5; TLOU2 1.1.1: 60/24.5)
stiffness=60
damping=24.5
; Soft edge: from which fraction of the radius part of the rotation goes straight into the picture (leak = t^soft_curve)
soft_start=0.7
soft_curve=2
; Aim speed: mouse multiplier while aiming (1 — as in the game; lower — slower)
aim_sens=0.6
; Where it works: the zone turns on while a gadget is aimed (any gadget). cam_states — filter by camera state:
; * — any; comma-separated list — only those; -Name — exclude (e.g. *,-DisruptorSniperStanding). Names are in the log.
; the zone also holds through short camera transitions (throw: Batarang → Walk → Batarang) while the gadget is aimed
cam_states=*

[camera]
; 1 — picture orbits around the camera point at the character (he stays in place in the frame), 0 — rotates in place; fractions — blend
orbit=1
; Orbit center: 0 — R3rdPersonCamera.LastCameraPivotPos, 1 — pawn position + pivot_z, 2 — LastCameraPivotPos + CurrentCameraPivotOffset
pivot_source=2
pivot_z=60
; Reset on a game camera jump: angle (degrees) / position (units, 100 = 1 m); offset release time on exit, s
reset_jump_deg=34
reset_move_uu=150
release_tau=0.12

[reticle]
; Reticle = the gadget's stock cursor moved to the aim point ): 0 — 3D point, projected to the screen by the game;
; 1 — screen coordinates computed by the mod from this frame's lagging picture
reticle_mode=1
; Aim ray: 0 — game camera (CameraCache.POV); 1 — gadget's GetAutoTargetRotation/Origin
aim_source=0
; Ray length to the aim point, units
trace_dist=20000

[debug]
; Summary interval in aim_log.txt, s
log_interval=1
; When to restore the game view in the camera cache: 0 — right after the frame is rendered (default), 1 — at the start of PlayerTick (0.2–0.4)
restore_at=0
";
        File.WriteAllText(IniPath, s + RecoilIniText + FPIniText, new UTF8Encoding(false));
    }

    static string ModeName(int m) => m switch
    {
        0 => "0 dead zone",
        _ => $"1 TEST: picture +{F(C.TestDeg)}° yaw (written to CameraCache)",
    };

    static bool PackOff;

    public override void OnTick()
    {
        if (!BatmanArsenalPack.Arsenal.Pack)
        {
            if (!PackOff)
            {
                PackOff = true;
                try { if (Game.GetPlayerController(0) is RPlayerController opc) RestoreReal(opc); } catch { }
                HardReset("pack turned off in the Arsenal Pack menu"); Live = false; FPReset();
            }
            return;
        }
        if (PackOff) { PackOff = false; HardReset("pack turned on"); }
        try
        {
            nTick++;
            float now = Now;
            if (now >= NextIniCheck)
            {
                NextIniCheck = now + 0.5f;
                try
                {
                    var st = File.GetLastWriteTimeUtc(IniPath);
                    if (st != IniStamp && (DateTime.UtcNow - st).TotalSeconds > 0.25) LoadIni(false);
                }
                catch { }
            }
            var pc = Game.GetPlayerController(0) as RPlayerController;
            if (pc == null) return;
            RestoreReal(pc);

            var g = (pc.Pawn?.InvManager as RInventoryManager)?.CurrentGadget;
            string gs = g == null ? "None" : g.Class.Name + ":" + g.GetStateName() + (g.bAiming ? ":aim" : "");
            if (gs != LastGadget)
            {
                L($"gadget: {LastGadget} → {gs}");
                if (g is RBatarang b && g.GetStateName().ToString() == "Firing" && !LastGadget.Contains(":Firing")) LogThrow(b);
                LastGadget = gs;
            }

            if (now - LastStepT > 0.25f) { Mark('t'); StepFrame(pc, "tick"); }

            if (TraceFrames > 0)
            {
                Seq.Append('|');
                if (--TraceFrames == 0) { L("call order over 6 frames (P PlayerTick, U UpdateCamera, C OnCameraUpdated, F FlushThreeDeeCursors (reticle), t step from tick, | OnTick): " + Seq); Seq.Clear(); }
            }

            if (now >= NextLog && (Live || WasActive || C.Mode != 0))
            {
                NextLog = now + Math.Max(0.2f, C.LogInterval);
                Summary(pc, g);
            }
            if (!Live && !WasActive) { nPT = nCamUpd = nUpdCam = nHud = nTick = nSteps = 0; SumAbsRet = MaxRet = 0; }
        }
        catch (Exception e) { L("EXC OnTick " + e); }
    }

    static void Summary(RPlayerController pc, RInventoryGadget? g)
    {
        int t = Math.Max(1, nTick);
        string auto = "";
        try
        {
            if (g != null && g.GetStateName().ToString() == "Primed")
            {
                var ar = g.GetAutoTargetRotation();
                var ao = g.GetAutoTargetOrigin(ar);
                auto = $" autoRot{R(ar)} Δ(auto−camera) y{F(Wrap180(ar.Yaw - RealRot.Yaw))} p{F(Wrap180(ar.Pitch - RealRot.Pitch))} autoOrg{V(ao)}";
            }
        }
        catch (Exception e) { auto = " auto EXC " + e.Message; }
        var pr = pc.Rotation;
        L($"[{CamState}] mode={C.Mode} live={Live} frames={t} per frame: PlayerTick {F((float)nPT / t)} OnCamUpd {F((float)nCamUpd / t)} UpdCam {F((float)nUpdCam / t)} Flush {F((float)nHud / t)} reticle x{F(LastScreen.X)} y{F(LastScreen.Y)} jitter {F(nJerk > 0 ? Jerk / nJerk * 1000f : 0)}‰ steps {nSteps} writes {nWrites} restores {nRestores} (after render {nRestoreF}) MouseInput {F((float)nMouse / t)} sens ×{F(C.AimSens)}" +
          $" | ret° y{F(RetYawDeg)} p{F(RetPitchDeg)} avg|ret| {F(SumAbsRet / Math.Max(1, nSteps))} max {F(MaxRet)} | axis° {F(AxRad * R2D)}x{F(AyRad * R2D)} fov {F(RealFov)} aspect {F(Aspect)}" +
          $" | cam{V(RealLoc)}{R(RealRot)} pc.Rot Δy{F(Wrap180(pr.Yaw - RealRot.Yaw))} Δp{F(Wrap180(pr.Pitch - RealRot.Pitch))} pivot {(PivotOk ? F(Vector3.Distance(Pivot, RealLoc)) + "u" : "none")} orbitW {F(OrbitW)}{auto}");
        nPT = nCamUpd = nUpdCam = nHud = nTick = nSteps = nWrites = nRestores = nRestoreF = nMouse = 0; SumAbsRet = MaxRet = 0; Jerk = 0; nJerk = 0;
    }

    static void DumpCamera()
    {
        var pc = Game.GetPlayerController(0) as RPlayerController;
        var cam = pc?.PlayerCamera;
        if (cam == null) { L("dump: no camera"); return; }
        var pov = cam.CameraCache.POV;
        var sb = new StringBuilder();
        sb.Append($"dump: camera {cam.Class.Name} state {cam.GetStateName()} POV {V(pov.Location)}{R(pov.Rotation)} fov {F(pov.FOV)} | pc.Rot {R(pc!.Rotation)}");
        if (cam is R3rdPersonCamera r3)
        {
            sb.Append($" | LastCameraPivotPos {V(r3.LastCameraPivotPos)} (to camera {F(Vector3.Distance(r3.LastCameraPivotPos, pov.Location))}) CurrentCameraPivotOffset {V(r3.CurrentCameraPivotOffset)}");
            sb.Append($" ActualCameraPosition {V(r3.ActualCameraPosition)} CrosshairAngleOffset {F(r3.CrosshairAngleOffset)} ×{F(r3.CrosshairAngleOffsetFactor)} OverrideAll {pc.OverrideAllCamera}");
        }
        var pi = pc.PlayerInput;
        if (pi != null) sb.Append($" | input aTurn {F(pi.aTurn)} aLookUp {F(pi.aLookUp)} aMouseX {F(pi.aMouseX)} aMouseY {F(pi.aMouseY)}");
        if (pc.Pawn != null) sb.Append($" | pawn {pc.Pawn.Class.Name} {V(pc.Pawn.Location)} to camera {F(Vector3.Distance(pc.Pawn.Location, pov.Location))}");
        try { Game.GetGameViewportClient().GetViewportSize(out Vector2 vs); sb.Append($" | viewport {F(vs.X)}x{F(vs.Y)}"); } catch { }
        L(sb.ToString());
    }

    static void HardReset(string why)
    {
        Z.Reset(); HavePrev = false; PivotOk = false;
        RetYawDeg = RetPitchDeg = 0;
        if (why != "") L("reset: " + why);
    }

    static void StepFrame(RPlayerController pc, string from)
    {
        var cam = pc.PlayerCamera;
        if (cam == null) { Live = false; return; }
        float now = Now;
        LastStepT = now;
        var pov = RealPov(cam);
        float yaw = pov.Rotation.Yaw, pitch = Wrap180(pov.Rotation.Pitch);
        RealLoc = pov.Location; RealRot = new Rotator(pitch, yaw, pov.Rotation.Roll); RealFov = pov.FOV;
        CamState = cam.GetStateName().ToString();
        if (CamState != LastCamState) { L($"camera: {LastCamState} → {CamState} (fov {F(RealFov)})"); LastCamState = CamState; }

        try { Game.GetGameViewportClient().GetViewportSize(out Vector2 vs); if (vs.X > 10 && vs.Y > 10) Aspect = vs.X / vs.Y; } catch { }
        float th = MathF.Tan(Math.Clamp(FPViewFov(RealFov), 5f, 170f) * 0.5f * D2R);
        AxRad = MathF.Atan(C.ZoneX * th);
        AyRad = MathF.Atan(C.ZoneY * th / Aspect);

        bool paused = false;
        try { paused = pc.IsPaused(); } catch { }
        var gad = (pc.Pawn?.InvManager as RInventoryManager)?.CurrentGadget;
        Aiming = gad != null && gad.bAiming;
        bool primed = Aiming && gad!.GetStateName().ToString() == "Primed";
        bool pawnOk = !C.DSOnly || pc.Pawn is MPawnPlayerRedHoodBase || ExtraAllowed();
        InAimState = primed && CamAllowed(CamState) && pawnOk;
        bool active = C.Enabled && C.Mode == 0 && !paused && Aiming && CamAllowed(CamState) && pawnOk && (InAimState || WasActive);

        float dt = HavePrev ? now - PrevT : 0f;
        bool cut = false;
        float dyaw = 0, dpitch = 0;
        if (HavePrev)
        {
            dyaw = Wrap180(yaw - PrevYaw); dpitch = pitch - PrevPitch;
            float jump = MathF.Sqrt(dyaw * dyaw + dpitch * dpitch);
            float moved = Vector3.Distance(RealLoc, PrevLoc);
            if (jump > C.ResetJumpDeg) { HardReset($"angle jump {F(jump)}°"); cut = true; }
            else if (moved > C.ResetMoveUU) { HardReset($"position jump {F(moved)} u"); cut = true; }
            else if (dt > 1f) { HardReset($"pause {F(dt)} s"); cut = true; }
        }
        float sdt = Math.Min(dt, 0.1f);
        if (HavePrev && !cut && sdt > 0f && !paused)
        {
            if (active)
            {
                Z.OnDelta(dyaw * D2R / AxRad, dpitch * D2R / AyRad);
                int n = Math.Max(1, (int)MathF.Ceiling(sdt * 240f));
                for (int i = 0; i < n; i++) Z.Step(sdt / n);
                nSteps++;
            }
            else
            {
                float a = 1f - MathF.Exp(-sdt / C.ReleaseTau);
                Z.RetX -= Z.RetX * a; Z.RetY -= Z.RetY * a; Z.VelX = Z.VelY = 0;
            }
        }
        FrameDYaw = cut ? 0f : dyaw; FrameDPitch = cut ? 0f : dpitch;
        HavePrev = true; PrevYaw = yaw; PrevPitch = pitch; PrevLoc = RealLoc; PrevT = now;

        if (active && !WasActive) L($"zone: ENTER ({CamState}, fov {F(RealFov)}, axes {F(AxRad * R2D)}°x{F(AyRad * R2D)}°)");
        if (!active && WasActive) L($"zone: exit ({CamState}) — releasing offset");
        WasActive = active;

        bool settled = MathF.Abs(Z.RetX) < 1e-3f && MathF.Abs(Z.RetY) < 1e-3f;
        if (!active && (settled || !Live)) { Z.Reset(); Live = false; RetYawDeg = RetPitchDeg = 0; OrbitW = 0; PivotOk = false; return; }
        Live = true;
        RetYawDeg = Z.RetX * AxRad * R2D; RetPitchDeg = Z.RetY * AyRad * R2D;
        float ar = MathF.Sqrt(Z.RetX * Z.RetX + Z.RetY * Z.RetY); SumAbsRet += ar; if (ar > MaxRet) MaxRet = ar;

        Vector3 pv = Vector3.Zero; bool pvOk = false;
        if (C.PivotSource != 1 && cam is R3rdPersonCamera r3) { pv = r3.LastCameraPivotPos + (C.PivotSource == 2 ? r3.CurrentCameraPivotOffset : Vector3.Zero); pvOk = true; }
        else if (pc.Pawn != null) { pv = pc.Pawn.Location + new Vector3(0, 0, C.PivotZ); pvOk = true; }
        float pd = pvOk ? Vector3.Distance(pv, RealLoc) : 0f;
        pvOk = pvOk && pd > 20f && pd < 1000f;
        if (pvOk)
        {
            if (!PivotOk) { Pivot = pv; PivotOk = true; }
            else Pivot += (pv - Pivot) * (1f - MathF.Exp(-sdt / 0.1f));
        }
        float target = pvOk ? Math.Clamp(C.Orbit, 0f, 1f) : 0f;
        OrbitW += (target - OrbitW) * (sdt > 0 ? 1f - MathF.Exp(-sdt / 0.15f) : 1f);
    }

    static void Axes(Rotator r, out Vector3 X, out Vector3 Y, out Vector3 Zv)
    {
        float sp = MathF.Sin(r.Pitch * D2R), cp = MathF.Cos(r.Pitch * D2R);
        float sy = MathF.Sin(r.Yaw * D2R), cy = MathF.Cos(r.Yaw * D2R);
        float sr = MathF.Sin(r.Roll * D2R), cr = MathF.Cos(r.Roll * D2R);
        X = new Vector3(cp * cy, cp * sy, sp);
        Y = new Vector3(sr * sp * cy - cr * sy, sr * sp * sy + cr * cy, -sr * cp);
        Zv = new Vector3(-(cr * sp * cy + sr * sy), cy * sr - cr * sp * sy, cr * cp);
    }

    static void Lagged(Vector3 loc, Rotator rot, out Vector3 vloc, out Rotator vrot)
    {
        vrot = new Rotator(rot.Pitch - RetPitchDeg, rot.Yaw - RetYawDeg, rot.Roll);
        vloc = loc;
        if (PivotOk && OrbitW > 1e-4f)
        {
            Axes(rot, out var xa, out var ya, out var za);
            Axes(vrot, out var xv, out var yv, out var zv);
            var w = loc - Pivot;
            var orbitPos = Pivot + xv * Vector3.Dot(xa, w) + yv * Vector3.Dot(ya, w) + zv * Vector3.Dot(za, w);
            vloc = loc + (orbitPos - loc) * OrbitW;
        }
    }

    [Redirect(typeof(R3rdPersonCamera), nameof(R3rdPersonCamera.UpdateCamera))]
    static void UpdateCameraR(R3rdPersonCamera self, float DeltaTime)
    {
        if (!BatmanArsenalPack.Arsenal.Pack)
        {
            self.UpdateCamera(DeltaTime);
            return;
        }

        Mark('U'); nUpdCam++;
        self.UpdateCamera(DeltaTime);
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.OnCameraUpdated))]
    static void OnCameraUpdatedR(RPlayerController self)
    {
        if (!BatmanArsenalPack.Arsenal.Pack)
        {
            self.OnCameraUpdated();
            return;
        }

        self.OnCameraUpdated();
        Mark('C'); nCamUpd++;
        try
        {
            if (!self.IsLocalPlayerController()) return;
            StepFrame(self, "cam");
            RecoilAfterCamera(FrameDYaw, FrameDPitch);
            FPFrame(self);
            var cam = self.PlayerCamera;
            RVOn = false;
            if (cam == null) return;
            if (!ComputeRenderView(out var vl, out var vr, out var vf)) return;
            RVOn = true; RVLoc = vl; RVRot = vr; RVFov = vf;
            var cc = cam.CameraCache; var p = cc.POV;
            SavedReal = p;
            p.Location = vl; p.Rotation = vr; p.FOV = vf;
            cc.POV = p; cam.CameraCache = cc;
            Written = cam.CameraCache.POV;
            HaveWritten = true; nWrites++;
        }
        catch (Exception e) { L("EXC OnCameraUpdated " + e); }
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.PlayerTick))]
    static void PlayerTickR(RPlayerController self, float DeltaTime)
    {
        if (!BatmanArsenalPack.Arsenal.Pack)
        {
            self.PlayerTick(DeltaTime);
            return;
        }

        Mark('P'); nPT++;
        try { if (self.IsLocalPlayerController()) { RestoreReal(self); RecoilFrame(self); } }
        catch (Exception e) { L("EXC PlayerTick " + e.Message); }
        self.PlayerTick(DeltaTime);
    }

    static GameObject.FTPOV SavedReal, Written;
    static bool HaveWritten;
    static int nWrites, nRestores, nRestoreF, nMouse;

    [Redirect(typeof(RPlayerInput), nameof(RPlayerInput.MouseInput))]
    static void MouseInputR(RPlayerInput self, float DeltaTime, float FOVScale)
    {
        if (!BatmanArsenalPack.Arsenal.Pack)
        {
            self.MouseInput(DeltaTime, FOVScale);
            return;
        }

        self.MouseInput(DeltaTime, FOVScale);
        nMouse++;
        try
        {
            if (C.Enabled && C.Mode == 0 && WasActive && MathF.Abs(C.AimSens - 1f) > 1e-3f)
            {
                self.aMouseX *= C.AimSens;
                self.aMouseY *= C.AimSens;
            }
            RecoilMouse(self);
        }
        catch (Exception e) { L("EXC MouseInput " + e.Message); }
    }

    static bool IsWritten(GameObject.FTPOV p) =>
        HaveWritten && Vector3.DistanceSquared(p.Location, Written.Location) < 1e-4f &&
        MathF.Abs(Wrap180(p.Rotation.Yaw - Written.Rotation.Yaw)) < 1e-3f && MathF.Abs(Wrap180(p.Rotation.Pitch - Written.Rotation.Pitch)) < 1e-3f;

    static GameObject.FTPOV RealPov(Camera cam)
    {
        var p = cam.CameraCache.POV;
        if (IsWritten(p)) return SavedReal;
        if (HavePartial && SamePov(p, PartialWritten)) return SavedReal;
        return p;
    }

    static GameObject.FTPOV PartialWritten;
    static bool HavePartial;
    static bool SamePov(GameObject.FTPOV a, GameObject.FTPOV b) =>
        Vector3.DistanceSquared(a.Location, b.Location) < 1e-4f &&
        MathF.Abs(Wrap180(a.Rotation.Yaw - b.Rotation.Yaw)) < 1e-3f && MathF.Abs(Wrap180(a.Rotation.Pitch - b.Rotation.Pitch)) < 1e-3f;

    static bool RestoreReal(RPlayerController pc)
    {
        if (!HaveWritten) return false;
        var cam = pc.PlayerCamera;
        HaveWritten = false;
        if (cam == null) return false;
        var cc = cam.CameraCache;
        if (!IsWrittenNoFlag(cc.POV)) return false;
        var back = SavedReal;
        HavePartial = false;
        if (FPEyeAim) back.Location = Written.Location;
        cc.POV = back; cam.CameraCache = cc; nRestores++;
        if (FPEyeAim) { PartialWritten = cam.CameraCache.POV; HavePartial = true; }
        return true;
    }

    static bool IsWrittenNoFlag(GameObject.FTPOV p) =>
        Vector3.DistanceSquared(p.Location, Written.Location) < 1e-4f &&
        MathF.Abs(Wrap180(p.Rotation.Yaw - Written.Rotation.Yaw)) < 1e-3f && MathF.Abs(Wrap180(p.Rotation.Pitch - Written.Rotation.Pitch)) < 1e-3f;

    [Redirect(typeof(RHudModuleTargets), nameof(RHudModuleTargets.FlushThreeDeeCursors))]
    static void FlushR(RHudModuleTargets self)
    {
        if (!BatmanArsenalPack.Arsenal.Pack)
        {
            self.FlushThreeDeeCursors();
            return;
        }

        Mark('F'); nHud++;
        try { if ((C.Enabled && (Live || (C.Mode == 1 && InAimState))) || FPW > 0f) MoveCentreCursors(self); }
        catch (Exception e) { L("EXC Flush " + e.Message); }
        self.FlushThreeDeeCursors();
        if (C.RestoreAt == 0)
            try { if (Game.GetPlayerController(0) is RPlayerController pc && RestoreReal(pc)) nRestoreF++; }
            catch (Exception e) { L("EXC restore " + e.Message); }
    }
    static void LogThrow(RBatarang b)
    {
        try
        {
            var tp = b.TargetPosition;
            string s = $"throw {b.Class.Name}: ret° y{F(RetYawDeg)} p{F(RetPitchDeg)} live={Live}, target {N(b.CurrentBatarangTarget)}, TargetPosition {V(tp)}";
            if (Now - LastTTime < 0.5f)
            {
                var a = Vector3.Normalize(LastT - RealLoc); var c = Vector3.Normalize(tp - RealLoc);
                float ang = MathF.Acos(Math.Clamp(Vector3.Dot(a, c), -1f, 1f)) * R2D;
                s += $" | aim point {V(LastT)}, angle between aim and throw {F(ang)}°";
            }
            L(s);
        }
        catch (Exception e) { L("EXC LogThrow " + e.Message); }
    }
    static string N(GameObject? o) => o == null ? "None" : o.Name.ToString();

    static bool LoggedNoMain;
    static Vector3 LastT; static float LastTTime = -10f;
    static bool Aiming, InAimState;

    static readonly HashSet<string> MovedIds = new();

    static void MoveCentreCursors(RHudModuleTargets tm)
    {
        var pc = Game.GetPlayerController(0) as RPlayerController;
        if (pc == null) return;
        var g = (pc.Pawn?.InvManager as RInventoryManager)?.CurrentGadget;
        var T = AimPoint(pc, g);
        T = SmoothDepth(T);

        Vector3 vl = RVOn ? RVLoc : RealLoc; Rotator vr = RVOn ? RVRot : RealRot; float vfov = RVOn ? RVFov : RealFov;
        Axes(vr, out var xv, out var yv, out var zv);
        var v = T - vl; float fx = Vector3.Dot(xv, v);
        float th = MathF.Tan(Math.Clamp(vfov, 5f, 170f) * 0.5f * D2R);
        bool screenOk = fx > 1f;
        float sx = screenOk ? 0.5f * (Vector3.Dot(yv, v) / fx) / th : 0f;
        float sy = screenOk ? -0.5f * (Vector3.Dot(zv, v) / fx) / th * Aspect : 0f;
        var s = new Vector2(sx, sy);
        if (screenOk && HaveS2) { Jerk += (s - 2 * S1 + S2).Length(); nJerk++; }
        S2 = S1; S1 = s; HaveS2 = HaveS1 && screenOk; HaveS1 = screenOk;
        LastScreen = s;

        var arr = tm.ThreeDeeTargetArray;
        for (int k = 0; k < arr.Count; k++)
        {
            var e = arr[k];
            if (!e.KeepAliveFlag || (e.AttributeFlags & 16) == 0) continue;
            if (MathF.Abs(e.Location.X) > 1e-4f || MathF.Abs(e.Location.Y) > 1e-4f) continue;
            string id = e.CursorID.ToString();
            if (id == "") continue;
            if (C.ReticleMode == 0) { e.AttributeFlags &= ~(16 | 32); e.Location = T; }
            else { if (!screenOk) continue; e.Location = new Vector3(sx, sy, 0); }
            arr[k] = e;
            if (MovedIds.Add(id + "/" + e.AnimName)) L($"reticle: moved cursor \"{id}\" ({e.AnimName}), gadget {(g == null ? "None" : g.Class.Name.ToString())}");
        }
    }

    // Rifle only (DZAim.AllowRifle): the reticle is the aim trace's hit point seen from the
    // orbited view, so its screen spot depends on the hit depth. When the trace flips between a
    // thug and the background (or through gaps between his limbs) the reticle jumped 45-90 px
    // for single frames. The hit distance is smoothed in 1/distance over 0.1 s, so a depth
    // change glides instead of jumping; a steady aim still lands exactly on the hit point.
    const float DepthTau = 0.1f;
    static float InvDepth, InvDepthT = -10f;
    static Vector3 AimOrg, AimDir;
    static bool AimRay;

    static Vector3 SmoothDepth(Vector3 T)
    {
        if (!AimRay) return T;
        bool rifle;
        try { rifle = AppDomain.CurrentDomain.GetData("DZAim.AllowRifle") as string == "1"; } catch { rifle = false; }
        float now = Now;
        float d = Vector3.Dot(T - AimOrg, AimDir);
        if (!rifle || d < 1f) { InvDepthT = -10f; return T; }
        float inv = 1f / d;
        float dt = now - InvDepthT;
        if (dt > 0.25f || dt < 0f) InvDepth = inv;
        else InvDepth += (inv - InvDepth) * (1f - MathF.Exp(-dt / DepthTau));
        InvDepthT = now;
        return AimOrg + AimDir * (1f / Math.Max(InvDepth, 1e-6f));
    }

    static Vector2 LastScreen, S1, S2;
    static bool HaveS1, HaveS2;
    static float Jerk; static int nJerk;

    static Vector3 AimPoint(RPlayerController pc, RInventoryGadget? g)
    {
        Vector3 org = FPEyeAim ? FPEye : RealLoc; Rotator rot = RealRot;
        if (C.AimSource == 1 && g != null) { rot = g.GetAutoTargetRotation(); org = g.GetAutoTargetOrigin(rot); }
        var dir = rot.ToDirection();
        AimOrg = org; AimDir = dir; AimRay = true;
        float skip = PivotOk ? Math.Max(0f, Vector3.Dot(Pivot - org, dir)) : 0f;
        var start = org + dir * skip;
        var end = org + dir * C.TraceDist;
        Vector3 hl, hn; Actor.FTraceHitInfo hi;
        var tracer = (Actor?)pc.Pawn ?? pc;
        var hit = tracer.Trace(out hl, out hn, end, start, BmSDK.Component.ECollisionFilter.COF_TraceWorldActors, Vector3.Zero, out hi, 0);
        var T = hit != null ? hl : end;
        LastT = T; LastTTime = Now;
        return T;
    }
}
