#nullable enable

using System;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace BatmanAssaultRifle;

internal static class RifleReticle
{
    private const int Pips = 6;

    private static bool aimed;
    private static float aimedAt = -10f;
    private static int lastMag = -1;
    private static float pulseUntil;
    private static int calls3d;
    private static float lockedUntil;

    public static void SetAimed(bool on)
    {
        aimed = on;

        if (on)
        {
            aimedAt = Util.Now;
        }
    }

    private static bool Active()
    {
        return BatmanArsenalPack.Arsenal.Rifle && RifleMode.Installed && Rifle.Selected;
    }

    private static bool RifleAim()
    {
        try
        {
            return Rifle.HeldBy(out _, out _);
        }
        catch
        {
            return false;
        }
    }

    private static string Icon()
    {
        int size = Math.Max(1, RifleAmmo.MagSize);
        int mag = Math.Clamp(RifleAmmo.Mag, 0, size);
        float now = Util.Now;

        if (lastMag >= 0 && mag < lastMag)
        {
            pulseUntil = now + 0.1f;
        }

        lastMag = mag;

        int pips = RifleAmmo.Reloading ? 0 : (mag * Pips + size - 1) / size;
        string hudEvent = RifleAmmo.Reloading || mag <= 0 ? "DryFire" : now < pulseUntil ? "Pulse" : "";

        if (RifleFire.OnThug)
        {
            lockedUntil = now + 0.15f;
        }

        return "disrupt:normal:Null:" + (now < lockedUntil ? "true" : "false") + ":" + (pips * 10 + Pips) + ":0:" + hudEvent;
    }

    private static RHudModuleTargets? hud;
    private static int mainIndex = -1;

    public static void ForgetHud()
    {
        hud = null;
        mainIndex = -1;
    }

    public static bool Offset(out float x, out float y)
    {
        x = 0f;
        y = 0f;

        try
        {
            if (hud == null || !hud.IsValid)
            {
                return false;
            }

            var arr = hud.ThreeDeeTargetArray;

            if (mainIndex < 0 || mainIndex >= arr.Count || arr[mainIndex].CursorID.ToString() != "main")
            {
                mainIndex = -1;

                for (int k = 0; k < arr.Count; k++)
                {
                    if (arr[k].CursorID.ToString() == "main")
                    {
                        mainIndex = k;

                        break;
                    }
                }

                if (mainIndex < 0)
                {
                    return false;
                }
            }

            var e = arr[mainIndex];

            if ((e.AttributeFlags & 16) == 0 || MathF.Abs(e.Location.X) > 0.6f || MathF.Abs(e.Location.Y) > 0.6f)
            {
                return false;
            }

            x = e.Location.X;
            y = e.Location.Y;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int Draw(RHudModuleTargets self, int attributeFlags, bool forceMain, float cursorLockon)
    {
        hud = self;

        return self.TargetCursorCentreScreen(new FString("main"), new FString(Icon()), attributeFlags, -1, -1, new FString(""), forceMain, cursorLockon);
    }

    [Redirect(typeof(RHudModuleTargets), nameof(RHudModuleTargets.TargetCursorCentreScreen))]
    private static int TargetCursorCentreScreenRedirect(RHudModuleTargets self, FString cursorId, FString animName, int attributeFlags, int charges, int maxCharges, FString extraInfo, bool forceMain, float cursorLockon)
    {
        try
        {
            if (Active() && cursorId.ToString() == "main" && (animName.ToString() ?? "").StartsWith("batarang") && RifleAim())
            {
                ModLog.Once("reticle-centre", "Reticle: the rifle aim shows the Disruptor reticle (Batarang centre cursor flags " + attributeFlags + ").");

                return Draw(self, attributeFlags, forceMain, cursorLockon);
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("reticle-failed", "Reticle: drawing the Disruptor reticle failed: " + Util.Unwrap(exception));
        }

        return self.TargetCursorCentreScreen(new FString(cursorId.ToString() ?? ""), new FString(animName.ToString() ?? ""), attributeFlags, charges, maxCharges, new FString(extraInfo.ToString() ?? ""), forceMain, cursorLockon);
    }

    [Redirect(typeof(RHudModuleTargets), nameof(RHudModuleTargets.TargetCursor3dWithActorReference))]
    private static int TargetCursor3dWithActorReferenceRedirect(RHudModuleTargets self, FString cursorId, FString animName, Actor linkedActor, int attributeFlags, int charges, int maxCharges, FString extraInfo, bool forceMain, float cursorLockon, FString optionalFocusDetail, FString mapRefName, FString mapTypeName)
    {
        try
        {
            if (Active() && cursorId.ToString() == "main" && (animName.ToString() ?? "").StartsWith("batarang") && RifleAim())
            {
                calls3d++;
                ModLog.Once("reticle-lock", "Reticle: kept the rifle reticle on the aim point instead of the Batarang's lock on a thug (" + (animName.ToString() ?? "") + ", flags " + attributeFlags + ").");

                return Draw(self, attributeFlags | 16, forceMain, cursorLockon);
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("reticle-lock-failed", "Reticle: replacing the Batarang lock failed: " + Util.Unwrap(exception));
        }

        return self.TargetCursor3dWithActorReference(new FString(cursorId.ToString() ?? ""), new FString(animName.ToString() ?? ""), linkedActor, attributeFlags, charges, maxCharges, new FString(extraInfo.ToString() ?? ""), forceMain, cursorLockon, new FString(optionalFocusDetail.ToString() ?? ""), new FString(mapRefName.ToString() ?? ""), new FString(mapTypeName.ToString() ?? ""));
    }

    [Redirect(typeof(RHudModuleTargets), nameof(RHudModuleTargets.TargetCursor3D))]
    private static int TargetCursor3DRedirect(RHudModuleTargets self, FString cursorId, FString animName, System.Numerics.Vector3 location, int attributeFlags, int charges, int maxCharges, FString extraInfo, bool forceMain, float cursorLockon, FString optionalFocusDetail, FString mapRefName, FString mapTypeName)
    {
        try
        {
            if (Active() && cursorId.ToString() == "main" && (animName.ToString() ?? "").StartsWith("batarang") && RifleAim())
            {
                calls3d++;
                ModLog.Once("reticle-lock3d", "Reticle: kept the rifle reticle on the aim point instead of the Batarang's 3D lock (" + (animName.ToString() ?? "") + ", flags " + attributeFlags + ").");

                return Draw(self, attributeFlags | 16, forceMain, cursorLockon);
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("reticle-lock3d-failed", "Reticle: replacing the Batarang 3D lock failed: " + Util.Unwrap(exception));
        }

        return self.TargetCursor3D(new FString(cursorId.ToString() ?? ""), new FString(animName.ToString() ?? ""), location, attributeFlags, charges, maxCharges, new FString(extraInfo.ToString() ?? ""), forceMain, cursorLockon, new FString(optionalFocusDetail.ToString() ?? ""), new FString(mapRefName.ToString() ?? ""), new FString(mapTypeName.ToString() ?? ""));
    }

    public static string Report() => "reticle on " + Active() + " aimed " + aimed + ", locks replaced " + calls3d;
}
