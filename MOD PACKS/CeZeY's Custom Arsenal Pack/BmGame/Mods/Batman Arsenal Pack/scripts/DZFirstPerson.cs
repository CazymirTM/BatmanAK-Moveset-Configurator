using System;
using System.Numerics;
using BmSDK;
using BmSDK.Framework;
using BmSDK.Engine;
using BmSDK.BmGame;

namespace DeadZoneAim;

public partial class DZAim
{
    static bool FPWanted, FPActive;
    static float FPW;
    static Vector3 FPHead, FPHeadRel; static bool FPHeadOk;
    static float FPRun;
    static float FPDisruptorBaseFov = 51.77f;
    static string FPSignal = "";
    static string FPReason = "", FPLastReason = "";
    static Pawn? FPHiddenPawn; static bool FPHeadHidden;
    static bool FPFacing;
    static float FPPrevT = -1f;

    const string FPIniText = @"
[fp]
; First-person view (Deathstroke only). Set it here.
fp=0
; Eyes relative to the head bone Bip01_Head, units (1 m = 100): forward along the view and up
fp_offset_fwd=10
fp_offset_up=6
; While running the eyes shift forward (the body leans): units at full run speed (600 units/s)
fp_run_fwd=15
; First-person FOV: multiplier of the game camera's FOV (1 — same as the camera)
fp_fov_scale=1.1
; Head position smoothing, s (0 — no smoothing; higher — less bobbing)
fp_smooth=0.05
; 1st/3rd person switch smoothness, s
fp_blend=0.2
; 1 — the body turns where the camera looks (strafe, like in a shooter)
fp_face=1
; Standing still: if the view turns away from the body by more than fp_turn_deg, the body turns (FaceInDirection, the game's turn animations);
; 0 — don't turn
fp_turn_deg=45
; 1 — no zoom on the sniper rifle (Disruptor) in first person: FOV as when not aiming
fp_sniper_nozoom=1
; 1 — aim ray and shots from the eyes (in first person the game camera sits at the eyes; the reticle doesn't jump due to parallax)
fp_aim_from_eye=1
; 1 — third-person view during special moves (takedowns, grabs, counters) and cutscenes
fp_tps_special=1
; Camera states that are always third person (comma-separated)
fp_tps_states=
";

    static bool FPKey(Cfg c, string k, string v)
    {
        float f() => float.Parse(v, Inv);
        switch (k)
        {
            case "fp": c.FP = v != "0"; return true;
            case "fp_offset_fwd": c.FPFwd = f(); return true;
            case "fp_offset_up": c.FPUp = f(); return true;
            case "fp_run_fwd": c.FPRunFwd = f(); return true;
            case "fp_fov_scale": c.FPFovScale = f(); return true;
            case "fp_smooth": c.FPSmooth = f(); return true;
            case "fp_blend": c.FPBlend = f(); return true;
            case "fp_face": c.FPFace = v != "0"; return true;
            case "fp_turn_deg": c.FPTurnDeg = f(); return true;
            case "fp_sniper_nozoom": c.FPSniperNoZoom = v != "0"; return true;
            case "fp_aim_from_eye": c.FPAimFromEye = v != "0"; return true;
            case "fp_tps_special": c.FPTpsSpecial = v != "0"; return true;
            case "fp_tps_states": c.FPTpsStates = v; return true;
        }
        return false;
    }

    static void ToggleFP()
    {
        C.FP = !C.FP;
        SaveIniValue("fp", C.FP ? "1" : "0");
        L("view: " + (C.FP ? "FIRST PERSON" : "third person"));
    }

    static void FPFrame(RPlayerController pc)
    {
        float now = Now;
        float dt = FPPrevT < 0 ? 0f : Math.Min(now - FPPrevT, 0.1f);
        FPPrevT = now;

        var pawn = pc.Pawn;
        string reason = "";
        if (!C.FP) reason = "disabled";
        else if (pawn is not MPawnPlayerRedHoodBase) reason = "not Deathstroke";
        else if (pawn.Mesh == null) reason = "no mesh";
        else if (C.FPTpsSpecial && pawn is RPawnCombat pcmb && pcmb.CurrentSpecialMove != null) reason = "special move " + pcmb.CurrentSpecialMove.Class.Name;
        else if (C.FPTpsSpecial && CinematicSafe(pc)) reason = "cutscene";
        else if (C.FPTpsStates != "" && ("," + C.FPTpsStates.Replace(" ", "") + ",").Contains("," + CamState + ",")) reason = "camera " + CamState;
        FPActive = reason == "";
        FPReason = reason;
        if (reason != FPLastReason)
        {
            if (C.FP || FPLastReason != "disabled") L(FPActive ? "first person: on" : "first person: third person (" + reason + ")");
            FPLastReason = reason;
        }

        float target = FPActive ? 1f : 0f;
        if (dt <= 0f) FPW = target;
        else FPW += (target - FPW) * (1f - MathF.Exp(-dt / Math.Max(0.01f, C.FPBlend)));
        if (MathF.Abs(FPW - target) < 1e-3f) FPW = target;

        if (pawn?.Mesh != null && FPW > 0f)
        {
            var hp = pawn.Mesh.GetBoneLocation(new FName("Bip01_Head"), 0);
            if (hp != Vector3.Zero)
            {
                var rel = hp - pawn.Location;
                if (!FPHeadOk || C.FPSmooth <= 0f || dt <= 0f || Vector3.Distance(rel, FPHeadRel) > 100f) FPHeadRel = rel;
                else FPHeadRel += (rel - FPHeadRel) * (1f - MathF.Exp(-dt / C.FPSmooth));
                FPHead = pawn.Location + FPHeadRel;
                FPHeadOk = true;
            }
            var hv = pawn.Velocity; hv.Z = 0;
            float runT = Math.Clamp(hv.Length() / 600f, 0f, 1f);
            FPRun += (runT - FPRun) * (dt > 0f ? 1f - MathF.Exp(-dt / 0.15f) : 1f);
        }
        else { FPHeadOk = false; FPRun = 0f; }
        if (pc.PlayerCamera is R3rdPersonCamera rc && rc.DisruptorSniperFOVAngle > 1f) FPDisruptorBaseFov = rc.DisruptorSniperFOVAngle;

        string sig = FPActive && FPW > 0.5f ? "1" : "0";
        if (sig != FPSignal) { FPSignal = sig; try { AppDomain.CurrentDomain.SetData("DZAim.FP", sig); } catch { } }

        SetHeadHidden(pawn, FPW > 0.5f);

        FPTurn(pawn as RPawnCharacter);

        var r3 = pc.PlayerCamera as R3rdPersonCamera;
        if (r3 != null)
        {
            bool face = FPActive && C.FPFace;
            if (face) { r3.bBatmanFacesCamera = true; FPFacing = true; }
            else if (FPFacing) { r3.bBatmanFacesCamera = false; FPFacing = false; }
        }
    }

    static bool CinematicSafe(RPlayerController pc)
    {
        try { return pc.InCinematicMode(); } catch { return false; }
    }

    static void SetHeadHidden(Pawn? pawn, bool hide)
    {
        try
        {
            if (hide && pawn?.Mesh != null)
            {
                if (FPHeadHidden && FPHiddenPawn == pawn) return;
                SetHeadHidden(FPHiddenPawn, false);
                pawn.Mesh.HideBoneByName(new FName("Bip01_Head"), SkeletalMeshComponent.EPhysBodyOp.PBO_None);
                FPHiddenPawn = pawn; FPHeadHidden = true;
                int bi = pawn.Mesh.MatchRefBone(new FName("Bip01_Head"));
                L($"first person: head hidden (bone #{bi}, hidden={(bi >= 0 && pawn.Mesh.IsBoneHidden(bi))})");
            }
            else if (!hide && FPHeadHidden)
            {
                if (FPHiddenPawn?.Mesh != null) FPHiddenPawn.Mesh.UnHideBoneByName(new FName("Bip01_Head"));
                FPHeadHidden = false; FPHiddenPawn = null;
                L("first person: head shown");
            }
        }
        catch (Exception e) { L("EXC head " + e.Message); FPHeadHidden = false; FPHiddenPawn = null; }
    }

    static void FPApply(ref Vector3 vl, Rotator vr, ref float fov)
    {
        if (FPW <= 0f || !FPHeadOk) return;
        Axes(vr, out var x, out _, out var z);
        var hf = new Vector3(x.X, x.Y, 0f);
        if (hf.LengthSquared() > 1e-6f) hf = Vector3.Normalize(hf);
        var eye = FPHead + x * C.FPFwd + hf * (C.FPRunFwd * FPRun) + z * C.FPUp;
        FPEye = eye;
        vl += (eye - vl) * FPW;
        fov = FPViewFov(fov);
    }

    static Vector3 FPEye;
    static bool FPEyeAim => C.FPAimFromEye && FPActive && FPW >= 0.999f && FPHeadOk;

    static float FPLastNormalFov = 86f;
    static float FPViewFov(float realFov)
    {
        if (FPW <= 0f) return realFov;
        bool sniperZoom = C.FPSniperNoZoom && CamState.StartsWith("DisruptorSniper");
        if (!sniperZoom && realFov > 60f) FPLastNormalFov = realFov;
        float baseFov = sniperZoom ? FPLastNormalFov * Math.Min(1f, realFov / Math.Max(5f, FPDisruptorBaseFov)) : realFov;
        float fpFov = baseFov * C.FPFovScale;
        return realFov + (fpFov - realFov) * FPW;
    }

    static bool FPTurning;
    static void FPTurn(RPawnCharacter? pawn)
    {
        if (pawn == null) return;
        try
        {
            bool can = FPActive && C.FPTurnDeg > 0f && pawn.Physics == Actor.EPhysics.PHYS_Walking;
            var vel = pawn.Velocity; vel.Z = 0;
            if (can && vel.Length() > 30f) can = false;
            var g = (pawn.InvManager as RInventoryManager)?.CurrentGadget;
            if (can && g != null && g.bAiming) can = false;
            float pyaw = pawn.Rotation.Yaw, cyaw = RealRot.Yaw;
            float diff = Wrap180(cyaw - pyaw);
            if (!can)
            {
                if (FPTurning) { pawn.StopFaceAt(); FPTurning = false; }
                return;
            }
            if (!FPTurning && MathF.Abs(diff) > C.FPTurnDeg)
            {
                FPTurning = true;
                L($"first person: turning body by {F(diff)}°");
            }
            if (FPTurning)
            {
                if (MathF.Abs(diff) < 5f) { pawn.StopFaceAt(); FPTurning = false; return; }
                var dir = new Vector3(MathF.Cos(cyaw * D2R), MathF.Sin(cyaw * D2R), 0f);
                pawn.FaceInDirection(dir);
            }
        }
        catch (Exception e) { L("EXC turn " + e.Message); FPTurning = false; }
    }

    static void FPReset()
    {
        try { SetHeadHidden(null, false); } catch { }
        FPW = 0f; FPActive = false; FPHeadOk = false; FPPrevT = -1f; FPLastReason = "";
        FPSignal = ""; try { AppDomain.CurrentDomain.SetData("DZAim.FP", "0"); } catch { }
    }
}
