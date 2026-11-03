#nullable enable

using System;
using System.Diagnostics;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace BatmanRedHoodGuns;

internal static class GunsDeadZone
{
    private const float D2R = MathF.PI / 180f;
    private const float R2D = 180f / MathF.PI;

    private static GunsConfig.Settings C => GunsConfig.C;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static float Now => (float)Clock.Elapsed.TotalSeconds;

    private static float retX, retY, velX, velY;
    private static bool havePrev, live, wasActive;
    private static float prevYaw, prevPitch, prevT;
    private static Vector3 prevLoc;
    private static float retYawDeg, retPitchDeg;
    private static float axRad = 0.5f, ayRad = 0.3f;
    private static Vector3 pivot;
    private static bool pivotOk;
    private static float orbitW;
    private static Vector3 realLoc;
    private static Rotator realRot;
    private static float realFov = 60f;
    private static float aspect = 16f / 9f;

    private static bool viewOn;
    private static Vector3 viewLoc;
    private static Rotator viewRot;
    private static float viewFov;

    private static GameObject.FTPOV savedReal, written;
    private static bool haveWritten;

    public static bool Live => live;

    public static void Reset()
    {
        ResetZone();
        live = false;
        wasActive = false;
        viewOn = false;
        ResetRecoil();
    }

    private static void ResetZone()
    {
        retX = retY = velX = velY = 0f;
        havePrev = false;
        pivotOk = false;
        retYawDeg = retPitchDeg = 0f;
    }

    private static float Wrap180(float a)
    {
        a %= 360f;

        if (a > 180f) a -= 360f;
        if (a < -180f) a += 360f;

        return a;
    }

    private static bool WantActive(RPlayerController pc)
    {
        if (!C.Enabled || Guns.Gun == null || !GunsAim.IsAimed || !Guns.HeldBy(out _, out _))
        {
            return false;
        }

        try
        {
            return !pc.IsPaused();
        }
        catch
        {
            return true;
        }
    }

    private static void OnDelta(float dx, float dy)
    {
        float nx = retX + dx;
        float ny = retY + dy;
        float dist = MathF.Sqrt(nx * nx + ny * ny);
        float soft = C.SoftStart;

        if (dist > soft && soft < 1f)
        {
            float leak = MathF.Pow(Math.Clamp((dist - soft) / (1f - soft), 0f, 1f), C.SoftCurve);

            retX += dx * (1f - leak);
            retY += dy * (1f - leak);
        }
        else
        {
            retX = nx;
            retY = ny;
        }

        float length = MathF.Sqrt(retX * retX + retY * retY);

        if (length > 1f)
        {
            retX /= length;
            retY /= length;
        }
    }

    private static void Spring(float dt)
    {
        velX += (retX * C.Stiffness - velX * C.Damping) * dt;
        velY += (retY * C.Stiffness - velY * C.Damping) * dt;
        retX -= velX * dt;
        retY -= velY * dt;

        if (!float.IsFinite(retX) || !float.IsFinite(retY) || !float.IsFinite(velX) || !float.IsFinite(velY))
        {
            retX = retY = velX = velY = 0f;
        }
    }

    private static void StepFrame(RPlayerController pc)
    {
        Camera? cam = pc.PlayerCamera;
        bool active = cam != null && WantActive(pc);

        if (cam == null || (!active && !live))
        {
            ResetZone();
            live = false;
            wasActive = false;
            orbitW = 0f;

            return;
        }

        float now = Now;
        GameObject.FTPOV pov = RealPov(cam);
        float yaw = pov.Rotation.Yaw;
        float pitch = Wrap180(pov.Rotation.Pitch);

        realLoc = pov.Location;
        realRot = new Rotator(pitch, yaw, pov.Rotation.Roll);
        realFov = pov.FOV;

        try
        {
            Game.GetGameViewportClient().GetViewportSize(out Vector2 size);

            if (size.X > 10f && size.Y > 10f)
            {
                aspect = size.X / size.Y;
            }
        }
        catch
        {
        }

        float tanHalf = MathF.Tan(Math.Clamp(realFov, 5f, 170f) * 0.5f * D2R);

        axRad = MathF.Atan(C.ZoneX * tanHalf);
        ayRad = MathF.Atan(C.ZoneY * tanHalf / aspect);

        float dt = havePrev ? now - prevT : 0f;
        bool cut = false;
        float dyaw = 0f;
        float dpitch = 0f;

        if (havePrev)
        {
            dyaw = Wrap180(yaw - prevYaw);
            dpitch = pitch - prevPitch;

            float jump = MathF.Sqrt(dyaw * dyaw + dpitch * dpitch);

            if (jump > C.ResetJumpDeg || Vector3.Distance(realLoc, prevLoc) > C.ResetMoveUU || dt > 1f)
            {
                ResetZone();
                cut = true;
            }
        }

        float sdt = Math.Min(dt, 0.1f);

        if (havePrev && !cut && sdt > 0f)
        {
            if (active)
            {
                OnDelta(dyaw * D2R / axRad, dpitch * D2R / ayRad);

                int steps = Math.Max(1, (int)MathF.Ceiling(sdt * 240f));

                for (int i = 0; i < steps; i++)
                {
                    Spring(sdt / steps);
                }
            }
            else
            {
                float a = 1f - MathF.Exp(-sdt / C.ReleaseTau);

                retX -= retX * a;
                retY -= retY * a;
                velX = velY = 0f;
            }
        }

        havePrev = true;
        prevYaw = yaw;
        prevPitch = pitch;
        prevLoc = realLoc;
        prevT = now;

        if (active != wasActive)
        {
            ModLog.Info("DeadZone: " + (active ? "on (fov " + realFov.ToString("0") + ", zone " + (axRad * R2D).ToString("0.0") + "x" + (ayRad * R2D).ToString("0.0") + " deg)" : "off, reticle returning to the centre") + ".");
        }

        wasActive = active;

        bool settled = MathF.Abs(retX) < 1e-3f && MathF.Abs(retY) < 1e-3f;

        if (!active && (settled || !live))
        {
            ResetZone();
            live = false;
            orbitW = 0f;

            return;
        }

        live = true;
        retYawDeg = retX * axRad * R2D;
        retPitchDeg = retY * ayRad * R2D;

        Vector3 pv = Vector3.Zero;
        bool pvOk = false;

        if (C.PivotSource != 1 && cam is R3rdPersonCamera third)
        {
            pv = third.LastCameraPivotPos + (C.PivotSource == 2 ? third.CurrentCameraPivotOffset : Vector3.Zero);
            pvOk = true;
        }
        else if (pc.Pawn != null)
        {
            pv = pc.Pawn.Location + new Vector3(0f, 0f, C.PivotZ);
            pvOk = true;
        }

        float pd = pvOk ? Vector3.Distance(pv, realLoc) : 0f;

        pvOk = pvOk && pd > 20f && pd < 1000f;

        if (pvOk)
        {
            if (!pivotOk)
            {
                pivot = pv;
                pivotOk = true;
            }
            else
            {
                pivot += (pv - pivot) * (1f - MathF.Exp(-sdt / 0.1f));
            }
        }

        float target = pvOk ? Math.Clamp(C.Orbit, 0f, 1f) : 0f;

        orbitW += (target - orbitW) * (sdt > 0f ? 1f - MathF.Exp(-sdt / 0.15f) : 1f);
    }

    private static void Axes(Rotator r, out Vector3 x, out Vector3 y, out Vector3 z)
    {
        float sp = MathF.Sin(r.Pitch * D2R), cp = MathF.Cos(r.Pitch * D2R);
        float sy = MathF.Sin(r.Yaw * D2R), cy = MathF.Cos(r.Yaw * D2R);
        float sr = MathF.Sin(r.Roll * D2R), cr = MathF.Cos(r.Roll * D2R);

        x = new Vector3(cp * cy, cp * sy, sp);
        y = new Vector3(sr * sp * cy - cr * sy, sr * sp * sy + cr * cy, -sr * cp);
        z = new Vector3(-(cr * sp * cy + sr * sy), cy * sr - cr * sp * sy, cr * cp);
    }

    private static void Lagged(Vector3 loc, Rotator rot, out Vector3 vloc, out Rotator vrot)
    {
        vrot = new Rotator(rot.Pitch - retPitchDeg, rot.Yaw - retYawDeg, rot.Roll);
        vloc = loc;

        if (pivotOk && orbitW > 1e-4f)
        {
            Axes(rot, out Vector3 xa, out Vector3 ya, out Vector3 za);
            Axes(vrot, out Vector3 xv, out Vector3 yv, out Vector3 zv);

            Vector3 w = loc - pivot;
            Vector3 orbitPos = pivot + xv * Vector3.Dot(xa, w) + yv * Vector3.Dot(ya, w) + zv * Vector3.Dot(za, w);

            vloc = loc + (orbitPos - loc) * orbitW;
        }
    }

    private static bool ComputeView(out Vector3 vl, out Rotator vr, out float fov)
    {
        vl = realLoc;
        vr = realRot;
        fov = realFov;

        bool any = false;

        if (live)
        {
            Lagged(realLoc, realRot, out vl, out vr);
            any = true;
        }

        if (ApplyPunch(ref vr, ref fov))
        {
            any = true;
        }

        return any;
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.OnCameraUpdated))]
    private static void OnCameraUpdatedRedirect(RPlayerController self)
    {
        if (!BatmanArsenalPack.Arsenal.Guns)
        {
            self.OnCameraUpdated();
            return;
        }

        self.OnCameraUpdated();

        try
        {
            if (Guns.Gun == null || !self.IsLocalPlayerController())
            {
                return;
            }

            StepFrame(self);
            PunchFrame();

            viewOn = false;

            Camera? cam = self.PlayerCamera;

            if (cam == null || !ComputeView(out Vector3 vl, out Rotator vr, out float vf))
            {
                return;
            }

            viewOn = true;
            viewLoc = vl;
            viewRot = vr;
            viewFov = vf;

            var cache = cam.CameraCache;
            GameObject.FTPOV pov = cache.POV;

            savedReal = pov;
            pov.Location = vl;
            pov.Rotation = vr;
            pov.FOV = vf;
            cache.POV = pov;
            cam.CameraCache = cache;
            written = cam.CameraCache.POV;
            haveWritten = true;
        }
        catch (Exception exception)
        {
            ModLog.Once("dz-camera", "DeadZone: camera update failed: " + Util.Unwrap(exception));
        }
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.PlayerTick))]
    private static void PlayerTickRedirect(RPlayerController self, float DeltaTime)
    {
        if (!BatmanArsenalPack.Arsenal.Guns)
        {
            self.PlayerTick(DeltaTime);
            return;
        }

        try
        {
            if (Guns.Gun != null && self.IsLocalPlayerController())
            {
                RestoreReal(self);
                RecoilFrame(self);
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("dz-tick", "DeadZone: player tick failed: " + Util.Unwrap(exception));
        }

        self.PlayerTick(DeltaTime);
    }

    [Redirect(typeof(RPlayerInput), nameof(RPlayerInput.MouseInput))]
    private static void MouseInputRedirect(RPlayerInput self, float DeltaTime, float FOVScale)
    {
        if (!BatmanArsenalPack.Arsenal.Guns)
        {
            self.MouseInput(DeltaTime, FOVScale);
            return;
        }

        self.MouseInput(DeltaTime, FOVScale);

        try
        {
            if (wasActive && MathF.Abs(C.AimSens - 1f) > 1e-3f)
            {
                self.aMouseX *= C.AimSens;
                self.aMouseY *= C.AimSens;
            }

            if (wasActive && C.PadEnabled && MathF.Abs(C.PadAimSens - 1f) > 1e-3f)
            {
                self.aTurn *= C.PadAimSens;
                self.aLookUp *= C.PadAimSens;
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("dz-mouse", "DeadZone: mouse scaling failed: " + Util.Unwrap(exception));
        }
    }

    [Redirect(typeof(RHudModuleTargets), nameof(RHudModuleTargets.FlushThreeDeeCursors))]
    private static void FlushThreeDeeCursorsRedirect(RHudModuleTargets self)
    {
        if (!BatmanArsenalPack.Arsenal.Guns)
        {
            self.FlushThreeDeeCursors();
            return;
        }

        self.FlushThreeDeeCursors();

        try
        {
            if (haveWritten && Game.GetPlayerController(0) is RPlayerController pc)
            {
                RestoreReal(pc);
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("dz-restore", "DeadZone: restoring the game view failed: " + Util.Unwrap(exception));
        }
    }

    private static bool SamePov(GameObject.FTPOV a, GameObject.FTPOV b) =>
        Vector3.DistanceSquared(a.Location, b.Location) < 1e-4f &&
        MathF.Abs(Wrap180(a.Rotation.Yaw - b.Rotation.Yaw)) < 1e-3f && MathF.Abs(Wrap180(a.Rotation.Pitch - b.Rotation.Pitch)) < 1e-3f;

    private static GameObject.FTPOV RealPov(Camera cam)
    {
        GameObject.FTPOV pov = cam.CameraCache.POV;

        return haveWritten && SamePov(pov, written) ? savedReal : pov;
    }

    private static void RestoreReal(RPlayerController pc)
    {
        if (!haveWritten)
        {
            return;
        }

        haveWritten = false;

        Camera? cam = pc.PlayerCamera;

        if (cam == null)
        {
            return;
        }

        var cache = cam.CameraCache;

        if (!SamePov(cache.POV, written))
        {
            return;
        }

        cache.POV = savedReal;
        cam.CameraCache = cache;
    }

    public static void ViewPoint(RPlayerController controller, out Vector3 location, out Rotator rotation)
    {
        controller.GetPlayerViewPoint(out location, out rotation);

        if (haveWritten && Vector3.DistanceSquared(location, written.Location) < 1e-4f && MathF.Abs(Wrap180(rotation.Yaw - written.Rotation.Yaw)) < 1e-3f &&
            MathF.Abs(Wrap180(rotation.Pitch - written.Rotation.Pitch)) < 1e-3f)
        {
            location = savedReal.Location;
            rotation = savedReal.Rotation;
        }
    }

    public static bool ReticleScreen(RPlayerController pc, out float x, out float y)
    {
        x = 0f;
        y = 0f;

        if (!live)
        {
            return false;
        }

        Vector3 aimPoint = AimPoint(pc);
        Vector3 vl = viewOn ? viewLoc : realLoc;
        Rotator vr = viewOn ? viewRot : realRot;
        float vfov = viewOn ? viewFov : realFov;

        Axes(vr, out Vector3 xv, out Vector3 yv, out Vector3 zv);

        Vector3 v = aimPoint - vl;
        float forward = Vector3.Dot(xv, v);

        if (forward <= 1f)
        {
            return false;
        }

        float tanHalf = MathF.Tan(Math.Clamp(vfov, 5f, 170f) * 0.5f * D2R);

        x = 0.5f * (Vector3.Dot(yv, v) / forward) / tanHalf;
        y = -0.5f * (Vector3.Dot(zv, v) / forward) / tanHalf * aspect;

        return true;
    }

    private static Vector3 AimPoint(RPlayerController pc)
    {
        Vector3 dir = realRot.ToDirection();
        float skip = pivotOk ? Math.Max(0f, Vector3.Dot(pivot - realLoc, dir)) : 0f;
        Vector3 start = realLoc + dir * skip;
        Vector3 end = realLoc + dir * C.TraceDist;
        Actor tracer = (Actor?)pc.Pawn ?? pc;

        try
        {
            Actor? hit = tracer.Trace(out Vector3 hitLocation, out _, end, start, BmSDK.Component.ECollisionFilter.COF_TraceWorldActors, Vector3.Zero, out Actor.FTraceHitInfo _, 0);

            return hit != null ? hitLocation : end;
        }
        catch
        {
            return end;
        }
    }

    private static float kickP, kickY, kickVelP, kickVelY, climb;
    private static float appliedP, appliedY;
    private static float recoilPrevT = -1f;
    private static float lastShotT = -10f;
    private static float climbTau = 0.4f;
    private static float punchP, punchVelP, punchR, punchVelR;
    private static float punchPrevT = -1f;
    private static readonly Random Rng = new Random();

    private static bool PunchActive => punchP != 0f || punchVelP != 0f || punchR != 0f || punchVelR != 0f;

    private static bool RecoilBusy => kickP != 0f || kickY != 0f || kickVelP != 0f || kickVelY != 0f || climb != 0f || appliedP != 0f || appliedY != 0f;

    private static void ResetRecoil()
    {
        kickP = kickY = kickVelP = kickVelY = climb = 0f;
        appliedP = appliedY = 0f;
        punchP = punchVelP = punchR = punchVelR = 0f;
    }

    private static float PeakPerVelocity(float k, float d)
    {
        float x = 0f;
        float v = 1f;
        float peak = 0f;
        const float h = 1f / 1000f;

        for (int i = 0; i < 1000; i++)
        {
            v += (-x * k - v * d) * h;
            x += v * h;

            if (x > peak)
            {
                peak = x;
            }
        }

        return Math.Max(peak, 1e-3f);
    }

    public static void OnShot()
    {
        float now = Now;
        float since = now - lastShotT;

        lastShotT = now;

        if (!C.Recoil || !live)
        {
            return;
        }

        float m = C.Strength * (since < C.BurstWindow ? C.BurstScale : 1f);
        float side = (float)(Rng.NextDouble() * 2.0 - 1.0);
        float kickPeak = PeakPerVelocity(C.KickStiffness, C.KickDamping);

        kickVelP += C.Kick * m / kickPeak;
        kickVelY += side * C.Yaw * m / kickPeak;
        climb = Math.Min(climb + C.Climb * m, 15f);
        climbTau = C.Recover > 0f ? C.Recover : C.RecoverDefault;

        float punch = C.PunchOn ? Math.Clamp(C.Punch * m, 0f, 15f) : 0f;

        if (punch > 0f)
        {
            float punchPeak = PeakPerVelocity(C.PunchStiffness, C.PunchDamping);

            punchVelP += punch / punchPeak;
            punchVelR += (Rng.Next(2) == 0 ? -1f : 1f) * punch * C.PunchRoll / punchPeak;
        }
    }

    private static void RecoilFrame(RPlayerController pc)
    {
        float now = Now;
        float dt = recoilPrevT < 0f ? 0f : Math.Min(now - recoilPrevT, 0.1f);

        recoilPrevT = now;

        if (dt <= 0f || !RecoilBusy)
        {
            return;
        }

        int steps = Math.Max(1, (int)MathF.Ceiling(dt * 240f));
        float sd = dt / steps;

        for (int i = 0; i < steps; i++)
        {
            kickVelP += (-kickP * C.KickStiffness - kickVelP * C.KickDamping) * sd;
            kickVelY += (-kickY * C.KickStiffness - kickVelY * C.KickDamping) * sd;
            kickP += kickVelP * sd;
            kickY += kickVelY * sd;
        }

        climb *= MathF.Exp(-dt / Math.Max(0.05f, climbTau));

        if (!float.IsFinite(kickP) || !float.IsFinite(kickY))
        {
            kickP = kickY = kickVelP = kickVelY = 0f;
        }

        float p = kickP + climb;
        float y = kickY;

        if (MathF.Abs(p) < 1e-3f && MathF.Abs(y) < 1e-3f && MathF.Abs(kickVelP) < 1e-2f && MathF.Abs(kickVelY) < 1e-2f)
        {
            kickP = kickY = kickVelP = kickVelY = climb = 0f;
            p = y = 0f;
        }

        float dp = p - appliedP;
        float dy = y - appliedY;

        appliedP = p;
        appliedY = y;

        if (MathF.Abs(dp) < 1e-5f && MathF.Abs(dy) < 1e-5f)
        {
            return;
        }

        Rotator r = pc.Rotation;

        pc.SetRotation(new Rotator(r.Pitch + dp, r.Yaw + dy, r.Roll));
    }

    private static void PunchFrame()
    {
        float now = Now;
        float dt = punchPrevT < 0f ? 0f : Math.Min(now - punchPrevT, 0.1f);

        punchPrevT = now;

        if (dt <= 0f || !PunchActive)
        {
            return;
        }

        int steps = Math.Max(1, (int)MathF.Ceiling(dt * 240f));
        float sd = dt / steps;

        for (int i = 0; i < steps; i++)
        {
            punchVelP += (-punchP * C.PunchStiffness - punchVelP * C.PunchDamping) * sd;
            punchVelR += (-punchR * C.PunchStiffness - punchVelR * C.PunchDamping) * sd;
            punchP += punchVelP * sd;
            punchR += punchVelR * sd;
        }

        if (!float.IsFinite(punchP) || !float.IsFinite(punchR) || !float.IsFinite(punchVelP) || !float.IsFinite(punchVelR) ||
            (MathF.Abs(punchP) < 1e-3f && MathF.Abs(punchR) < 1e-3f && MathF.Abs(punchVelP) < 1e-2f && MathF.Abs(punchVelR) < 1e-2f))
        {
            punchP = punchR = punchVelP = punchVelR = 0f;
        }
    }

    private static bool ApplyPunch(ref Rotator vr, ref float fov)
    {
        if (!PunchActive || !C.PunchOn)
        {
            return false;
        }

        float p = Math.Clamp(punchP, -20f, 20f);
        float r = Math.Clamp(punchR, -10f, 10f);

        vr = new Rotator(vr.Pitch + p, vr.Yaw, vr.Roll + r);
        fov = Math.Clamp(fov + p * C.PunchFov, 5f, 170f);

        return true;
    }

    public static string Describe() =>
        "deadzone=" + (C.Enabled ? "on" : "off") + " live=" + live + " reticle=" + retYawDeg.ToString("0.0") + "," + retPitchDeg.ToString("0.0") + " deg, orbit=" + orbitW.ToString("0.00") +
        ", recoil=" + (C.Recoil ? "on" : "off") + " applied=" + appliedP.ToString("0.00") + "/" + appliedY.ToString("0.00");
}
