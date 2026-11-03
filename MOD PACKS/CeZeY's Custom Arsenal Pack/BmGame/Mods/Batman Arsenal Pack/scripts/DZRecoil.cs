using System;
using System.Numerics;
using BmSDK;
using BmSDK.Framework;
using BmSDK.Engine;
using BmSDK.BmGame;

namespace DeadZoneAim;

public partial class DZAim
{
    sealed class GunRecoil
    {
        public float Kick, Climb, Yaw, Recover, Punch;
        public GunRecoil(float k, float c, float y, float rec, float punch) { Kick = k; Climb = c; Yaw = y; Recover = rec; Punch = punch; }
    }

    static float RKickP, RKickY, RVelP, RVelY, RClimb;
    static float RAppliedP, RAppliedY;
    static float RPrevT = -1f, RLastShotT = -10f;
    static float RClimbTau = 0.5f;
    static string RLastSignal = "";
    static readonly Random RRng = new Random();
    static float RPendP, RPendY;
    static float GainXY, GainXX, GainPY, GainPP;
    static float FrameMouseX, FrameMouseY;
    static float RMeasureUntil = -1f, RMeasureApplied, RMeasureCam;
    static float PunchP, PunchPV, PunchR, PunchRV, PunchPrevT = -1f;
    static bool PunchActive => PunchP != 0 || PunchPV != 0 || PunchR != 0 || PunchRV != 0;

    static GunRecoil GunCfg(string w) => w switch
    {
        "shotgun" => C.RShotgun,
        "sniper" => C.RSniper,
        "auto" => C.RAuto,
        _ => C.RPistol,
    };

    static bool RecoilKey(Cfg c, string k, string v)
    {
        float f() => float.Parse(v, Inv);
        switch (k)
        {
            case "recoil": c.RecoilOn = v != "0"; return true;
            case "recoil_strength": c.RStrength = f(); return true;
            case "recoil_recover_s": c.RRecover = f(); return true;
            case "recoil_kick_stiffness": c.RKickK = f(); return true;
            case "recoil_kick_damping": c.RKickD = f(); return true;
            case "recoil_burst_window": c.RBurstWindow = f(); return true;
            case "recoil_burst_scale": c.RBurstScale = f(); return true;
            case "recoil_method": c.RMethod = (int)f(); return true;
            case "recoil_punch": c.RPunchOn = v != "0"; return true;
            case "recoil_punch_stiffness": c.RPunchK = f(); return true;
            case "recoil_punch_damping": c.RPunchD = f(); return true;
            case "recoil_punch_roll": c.RPunchRoll = f(); return true;
            case "recoil_punch_fov": c.RPunchFov = f(); return true;
        }
        int us = k.IndexOf('_');
        if (us <= 0) return false;
        GunRecoil? g = k.Substring(0, us) switch { "pistol" => c.RPistol, "shotgun" => c.RShotgun, "sniper" => c.RSniper, "auto" => c.RAuto, _ => null };
        if (g == null) return false;
        switch (k.Substring(us + 1))
        {
            case "kick": g.Kick = f(); return true;
            case "climb": g.Climb = f(); return true;
            case "yaw": g.Yaw = f(); return true;
            case "recover": g.Recover = f(); return true;
            case "punch": g.Punch = f(); return true;
        }
        return false;
    }

    const string RecoilIniText = @"
[recoil]
; Deathstroke recoil (pistols, shotgun, sniper rifle, auto rifle — aimed shots). The real aim is kicked up/sideways (shots go
; there), the dead zone lets the picture catch up; the view punch additionally jolts the picture (render only).
; 1 — on
recoil=1
; Global multiplier for kick/climb/yaw/punch
recoil_strength=2
; Per weapon, degrees: kick — fast aim kick up (spring back), climb — barrel climb per shot (returns over _recover seconds,
; 0 = recoil_recover_s), yaw — random sideways kick, punch — render-only picture jolt up (+ roll and FOV, see below)
pistol_kick=1.4
pistol_climb=0.5
pistol_yaw=0.4
pistol_recover=0.4
pistol_punch=0.6
shotgun_kick=4.5
shotgun_climb=2
shotgun_yaw=1.2
shotgun_recover=0.35
shotgun_punch=3.5
sniper_kick=5
sniper_climb=2.5
sniper_yaw=0.8
sniper_recover=0.8
sniper_punch=3
; Deathstroke auto rifle — per bullet (~9 per second), the burst reduction is not applied
auto_kick=0.8
auto_climb=0.35
auto_yaw=0.4
auto_recover=0.5
auto_punch=0.35
recoil_recover_s=0.5
; Aim-kick spring (TLOU2: 250 / 20 — slight overshoot)
recoil_kick_stiffness=250
recoil_kick_damping=20
; Burst: shots closer than recoil_burst_window (s) — each ×recoil_burst_scale
recoil_burst_window=0.25
recoil_burst_scale=0.6
; How the aim is turned: 0 — controller rotation (SetRotation), 1 — via mouse input (MouseInput)
recoil_method=0
; View punch (render only, does not move the aim): 1 — on; spring; roll — random roll as a share of the punch;
; fov — degrees of FOV widening per degree of punch
recoil_punch=1
recoil_punch_stiffness=400
recoil_punch_damping=22
recoil_punch_roll=0.35
recoil_punch_fov=0.6
";

    static float PeakPerVel(float k, float d)
    {
        float x = 0f, v = 1f, peak = 0f;
        const float h = 1f / 1000f;
        for (int i = 0; i < 1000; i++) { v += (-x * k - v * d) * h; x += v * h; if (x > peak) peak = x; }
        return Math.Max(peak, 1e-3f);
    }

    static void PollShot()
    {
        string s;
        try { s = AppDomain.CurrentDomain.GetData("DZAim.Shot") as string ?? ""; } catch { return; }
        if (s == RLastSignal) return;
        bool first = RLastSignal == "";
        RLastSignal = s;
        if (first && s != "" && Now < 1f) return;
        int bar = s.IndexOf('|');
        OnShot(bar > 0 ? s.Substring(0, bar) : s);
    }

    static void OnShot(string weapon)
    {
        float now = Now;
        float since = now - RLastShotT;
        RLastShotT = now;
        if (!C.Enabled || !C.RecoilOn) { L($"shot {weapon} — recoil is OFF (recoil=0 in DZAim.ini or mod disabled)"); return; }
        var g = GunCfg(weapon);
        bool burst = since < C.RBurstWindow;
        float m = C.RStrength * (burst && weapon != "auto" ? C.RBurstScale : 1f);
        float side = (float)(RRng.NextDouble() * 2.0 - 1.0);
        float kPeak = PeakPerVel(C.RKickK, C.RKickD);
        RVelP += g.Kick * m / kPeak;
        RVelY += side * g.Yaw * m / kPeak;
        RClimb = Math.Min(RClimb + g.Climb * m, 15f);
        RClimbTau = g.Recover > 0f ? g.Recover : C.RRecover;
        float punch = C.RPunchOn ? Math.Clamp(g.Punch * m, 0f, 15f) : 0f;
        if (punch > 0f)
        {
            float pPeak = PeakPerVel(C.RPunchK, C.RPunchD);
            float rollSide = RRng.Next(2) == 0 ? -1f : 1f;
            PunchPV += punch / pPeak;
            PunchRV += rollSide * punch * C.RPunchRoll / pPeak;
        }
        RMeasureUntil = now + 0.1f; RMeasureApplied = 0f; RMeasureCam = 0f;
        L($"shot {weapon}: kick {F(g.Kick * m)}° climb {F(g.Climb * m)}° (recover {F(RClimbTau)} s) yaw {F(side * g.Yaw * m)}° punch {F(punch)}°{(burst && weapon != "auto" ? " (burst ×" + F(C.RBurstScale) + ")" : "")} ret° y{F(RetYawDeg)} p{F(RetPitchDeg)}");
    }

    static void RecoilFrame(RPlayerController pc)
    {
        PollShot();
        float now = Now;
        float dt = RPrevT < 0 ? 0f : Math.Min(now - RPrevT, 0.1f);
        RPrevT = now;
        if (dt <= 0f) return;
        bool idle = RKickP == 0 && RKickY == 0 && RVelP == 0 && RVelY == 0 && RClimb == 0 && RAppliedP == 0 && RAppliedY == 0;
        if (idle) return;

        int n = Math.Max(1, (int)MathF.Ceiling(dt * 240f));
        float sd = dt / n;
        for (int i = 0; i < n; i++)
        {
            RVelP += (-RKickP * C.RKickK - RVelP * C.RKickD) * sd;
            RVelY += (-RKickY * C.RKickK - RVelY * C.RKickD) * sd;
            RKickP += RVelP * sd;
            RKickY += RVelY * sd;
        }
        RClimb *= MathF.Exp(-dt / Math.Max(0.05f, RClimbTau));
        if (!float.IsFinite(RKickP) || !float.IsFinite(RKickY)) { RKickP = RKickY = RVelP = RVelY = 0; }

        float p = RKickP + RClimb, y = RKickY;
        if (MathF.Abs(p) < 1e-3f && MathF.Abs(y) < 1e-3f && MathF.Abs(RVelP) < 1e-2f && MathF.Abs(RVelY) < 1e-2f) { RKickP = RKickY = RVelP = RVelY = RClimb = 0; p = y = 0; }
        float dp = p - RAppliedP, dy = y - RAppliedY;
        RAppliedP = p; RAppliedY = y;
        if (MathF.Abs(dp) < 1e-5f && MathF.Abs(dy) < 1e-5f) return;
        if (now < RMeasureUntil) RMeasureApplied += dp;

        if (C.RMethod == 1) { RPendP += dp; RPendY += dy; return; }
        var r = pc.Rotation;
        pc.SetRotation(new Rotator(r.Pitch + dp, r.Yaw + dy, r.Roll));
    }

    static void PunchFrame()
    {
        float now = Now;
        float dt = PunchPrevT < 0 ? 0f : Math.Min(now - PunchPrevT, 0.1f);
        PunchPrevT = now;
        if (dt <= 0f || !PunchActive) return;
        int n = Math.Max(1, (int)MathF.Ceiling(dt * 240f));
        float sd = dt / n;
        for (int i = 0; i < n; i++)
        {
            PunchPV += (-PunchP * C.RPunchK - PunchPV * C.RPunchD) * sd;
            PunchRV += (-PunchR * C.RPunchK - PunchRV * C.RPunchD) * sd;
            PunchP += PunchPV * sd;
            PunchR += PunchRV * sd;
        }
        if (!float.IsFinite(PunchP) || !float.IsFinite(PunchR) || !float.IsFinite(PunchPV) || !float.IsFinite(PunchRV)) PunchP = PunchR = PunchPV = PunchRV = 0;
        if (MathF.Abs(PunchP) < 1e-3f && MathF.Abs(PunchR) < 1e-3f && MathF.Abs(PunchPV) < 1e-2f && MathF.Abs(PunchRV) < 1e-2f) PunchP = PunchR = PunchPV = PunchRV = 0;
    }

    static bool ApplyPunch(ref Rotator vr, ref float fov)
    {
        if (!PunchActive || !C.RPunchOn) return false;
        float p = Math.Clamp(PunchP, -20f, 20f), r = Math.Clamp(PunchR, -10f, 10f);
        vr = new Rotator(vr.Pitch + p, vr.Yaw, vr.Roll + r);
        fov = Math.Clamp(fov + p * C.RPunchFov, 5f, 170f);
        return true;
    }

    static void RecoilMouse(RPlayerInput pi)
    {
        FrameMouseX += pi.aMouseX; FrameMouseY += pi.aMouseY;
        if (C.RMethod != 1 || (RPendP == 0 && RPendY == 0)) return;
        if (GainXX < 50f || GainPP < 50f) return;
        float gx = GainXY / GainXX, gp = GainPY / GainPP;
        if (MathF.Abs(gx) > 1e-5f) pi.aMouseX += RPendY / gx;
        if (MathF.Abs(gp) > 1e-5f) pi.aMouseY += RPendP / gp;
        RPendP = RPendY = 0;
    }

    static void RecoilAfterCamera(float dyawDeg, float dpitchDeg)
    {
        PunchFrame();
        float now = Now;
        if (now < RMeasureUntil) RMeasureCam += dpitchDeg;
        else if (RMeasureUntil > 0)
        {
            L($"recoil: 0.1 s after the shot applied up {F(RMeasureApplied)}°, game camera rose {F(RMeasureCam)}° (method {C.RMethod})");
            RMeasureUntil = -1f;
        }
        if (RAppliedP == 0 && RAppliedY == 0)
        {
            if (MathF.Abs(FrameMouseX) > 0.5f) { GainXY += dyawDeg * FrameMouseX; GainXX += FrameMouseX * FrameMouseX; }
            if (MathF.Abs(FrameMouseY) > 0.5f) { GainPY += dpitchDeg * FrameMouseY; GainPP += FrameMouseY * FrameMouseY; }
            if (GainXX > 1e6f) { GainXY *= 0.5f; GainXX *= 0.5f; }
            if (GainPP > 1e6f) { GainPY *= 0.5f; GainPP *= 0.5f; }
        }
        FrameMouseX = FrameMouseY = 0;
    }

    static void ResetRecoil()
    {
        RKickP = RKickY = RVelP = RVelY = RClimb = 0;
        RAppliedP = RAppliedY = 0;
        RPendP = RPendY = 0;
        PunchP = PunchPV = PunchR = PunchRV = 0;
    }
}
