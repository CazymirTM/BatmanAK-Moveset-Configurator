#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace BatmanRedHoodGuns;

internal static class GunsHud
{
    public const int ClipSize = 12;
    private const int ReticlePips = 6;
    private static readonly TimeSpan ReloadTime = TimeSpan.FromSeconds(1.0);
    private static readonly TimeSpan RefillTime = TimeSpan.FromSeconds(1.0);

    public static float CursorScaleX = 0.5f;
    public static float CursorScaleY = 0.5f;

    public static int Ammo = ClipSize;
    private static DateTime reloadDoneAt = DateTime.MinValue;
    private static bool reloadAnimated;
    private static string hudEvent = "";
    private static DateTime pulseEndsAt = DateTime.MinValue;
    private static bool targetLocked;
    private static DateTime nextLockCheck = DateTime.MinValue;

    public static bool Reloading => reloadDoneAt != DateTime.MinValue;

    public static bool ReloadingEmptyClip => Reloading && reloadAnimated;

    public static float ReloadProgress => !Reloading ? 0f : Math.Clamp(1f - (float)((reloadDoneAt - DateTime.UtcNow).TotalSeconds / reloadLength.TotalSeconds), 0f, 1f);

    public static float ReticleX;
    public static float ReticleY;

    private static TimeSpan reloadLength = TimeSpan.FromSeconds(1);

    public static bool ManualReload(RPawnPlayer pawn)
    {
        if (Reloading || Ammo >= ClipSize)
        {
            ModLog.Once("reload-ignored:" + Reloading + ":" + Ammo, "Hud: reload key ignored (" + (Reloading ? "already reloading" : "clip full") + ").");

            return false;
        }

        StartReload(pawn, animated: GunsAim.IsAimed);
        ModLog.Info("Hud: manual reload (" + Ammo + "/" + ClipSize + ", " + (GunsAim.IsAimed ? "animated" : "guns lowered") + ").");

        return true;
    }

    public static void Reset()
    {
        Ammo = ClipSize;
        reloadDoneAt = DateTime.MinValue;
        hudEvent = "";
        targetLocked = false;
    }

    public static bool TryFire(RPawnPlayer pawn)
    {
        if (Reloading)
        {
            hudEvent = "DryFire";

            return false;
        }

        if (Ammo <= 0)
        {
            StartReload(pawn, animated: true);

            return false;
        }

        Ammo--;
        lastShotAt = DateTime.UtcNow;

        hudEvent = "Pulse";
        pulseEndsAt = DateTime.UtcNow + TimeSpan.FromSeconds(0.1);

        return true;
    }

    public static void OnQuickfire()
    {
        if (Reloading || Ammo <= 0)
        {
            return;
        }

        Ammo--;
        lastShotAt = DateTime.UtcNow;
    }

    private static readonly TimeSpan RefillDelay = TimeSpan.FromSeconds(2.5);
    private static DateTime lastShotAt = DateTime.MinValue;

    private static void StartReload(RPawnPlayer pawn, bool animated)
    {
        reloadAnimated = animated;
        reloadLength = animated ? ReloadTime : RefillTime;
        reloadDoneAt = DateTime.UtcNow + reloadLength;
        hudEvent = "DryFire";

        if (animated)
        {
            GunsAim.PlayReload(pawn);
        }
    }

    public static void Tick(RPawnPlayer pawn, bool aiming, DateTime now)
    {
        if (hudEvent == "Pulse" && now >= pulseEndsAt)
        {
            hudEvent = "";
        }

        if (Reloading && now >= reloadDoneAt)
        {
            reloadDoneAt = DateTime.MinValue;
            Ammo = ClipSize;
            hudEvent = "";
        }

        if (aiming && Ammo <= 0 && !Reloading && GunsAim.StrikeFinished(now))
        {
            StartReload(pawn, animated: true);
        }

        if (!aiming && Ammo < ClipSize && !Reloading && now - lastShotAt >= RefillDelay)
        {
            StartReload(pawn, animated: false);
        }

        if (aiming && now >= nextLockCheck)
        {
            nextLockCheck = now + TimeSpan.FromSeconds(0.1);
            targetLocked = GunsAim.TargetUnderReticle(pawn) != null;
        }
    }

    public static void OnAimChanged(bool aiming)
    {
        targetLocked = false;
    }

    [Redirect(typeof(RHudModuleTargets), nameof(RHudModuleTargets.TargetCursorCentreScreen))]
    private static int TargetCursorCentreScreenRedirect(RHudModuleTargets self, FString cursorId, FString animName, int attributeFlags, int charges, int maxCharges, FString extraInfo, bool forceMain, float cursorLockon)
    {
        try
        {
            if (!GunsAim.IsAimed && Guns.HeldBy(out _, out _) && cursorId.ToString() == "main")
            {
                ModLog.Once("quickfire-reticle", "Hud: hiding the game's centre reticle while the guns are out but not aimed (quickfire).");

                return -1;
            }

            if (GunsAim.IsAimed && Guns.HeldBy(out _, out _) && cursorId.ToString() == "main")
            {
                float x = 0f;
                float y = 0f;

                if (Game.GetPlayerController(0) is RPlayerController controller)
                {
                    GunsDeadZone.ReticleScreen(controller, out x, out y);
                }

                int pips = Reloading ? 0 : (Ammo + ClipSize / ReticlePips - 1) / (ClipSize / ReticlePips);
                string icon = "disrupt:normal:Null:" + (targetLocked ? "true" : "false") + ":" + (pips * 10 + ReticlePips) + ":0:" + hudEvent;

                ReticleX = x * 2f * CursorScaleX;
                ReticleY = y * 2f * CursorScaleY;

                return self.TargetCursorScreenSpace(Str("main"), Str(icon), ReticleX, ReticleY, attributeFlags, -1, -1, Str(""), forceMain, cursorLockon);
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("reticle-failed", "Hud: drawing the Disruptor reticle failed: " + Util.Unwrap(exception));
        }

        return self.TargetCursorCentreScreen(Str(cursorId.ToString() ?? ""), Str(animName.ToString() ?? ""), attributeFlags, charges, maxCharges, Str(extraInfo.ToString() ?? ""), forceMain, cursorLockon);
    }

    private static FString Str(string text) => new FString(text);

    public static string Describe()
    {
        string body = "";

        try
        {
            if (Guns.Batman() is RPawnPlayer pawn)
            {
                Rotator target = Rotator.FromDirection(pawn.GetTargetAimAtDirection());
                Rotator actual = Rotator.FromDirection(pawn.GetActualAimAtDirection());

                body = ", body aim target=" + target.Yaw.ToString("0") + "/" + target.Pitch.ToString("0") + " actual=" + actual.Yaw.ToString("0") + "/" + actual.Pitch.ToString("0") + " pawn yaw=" + pawn.Rotation.Yaw.ToString("0");
            }
        }
        catch (Exception exception)
        {
            body = ", body aim: " + Util.Unwrap(exception);
        }

        return "ammo=" + Ammo + "/" + ClipSize + ", reloading=" + Reloading + ", locked=" + targetLocked + body + ", " + GunsDeadZone.Describe();
    }
}
