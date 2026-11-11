#nullable enable

using System;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace BatmanAssaultRifle;

internal static class RifleAmmo
{
    private const float SoundAt = 0.1f;
    private const float FillShare = 0.75f;

    public static int MagSize => RifleConfig.C.Magazine;
    public static int Mag = 37;
    public static bool Reloading;
    public static float Progress => Reloading ? Math.Clamp(relT / Math.Max(0.5f, RifleConfig.C.ReloadTime), 0f, 1f) : 0f;

    private static float relT;
    private static bool soundDone, filled;
    private static float autoReloadAt = -1f;
    private static float nextDry;

    public static void Reset()
    {
        Reloading = false;
        autoReloadAt = -1f;
        relT = 0f;
    }

    public static void Init()
    {
        Mag = MagSize;
        Reset();
    }

    public static bool CanShoot(RBatarang bat)
    {
        if (Reloading)
        {
            return false;
        }

        if (Mag <= 0)
        {
            DryFire(bat);

            return false;
        }

        return true;
    }

    public static void Consume()
    {
        Mag = Math.Max(0, Mag - 1);

        if (Mag == 0)
        {
            autoReloadAt = Util.Now + 0.35f;
        }
    }

    private static void DryFire(RBatarang bat)
    {
        if (Util.Now < nextDry)
        {
            return;
        }

        nextDry = Util.Now + 0.3f;

        if (RifleProps.DryEvent != null)
        {
            try { bat.PlayGadgetSound(RifleProps.DryEvent, false); } catch { }
        }

        if (autoReloadAt < 0f)
        {
            autoReloadAt = Util.Now + 0.2f;
        }
    }

    public static void StartReload(RPawnPlayer pawn, RBatarang? bat, string why)
    {
        if (bat == null || Reloading)
        {
            return;
        }

        if (Mag >= MagSize)
        {
            ModLog.Info("Ammo: magazine already full.");

            return;
        }

        Reloading = true;
        relT = 0f;
        RifleAnim.ReloadT = 0f;
        soundDone = filled = false;
        autoReloadAt = -1f;
        ModLog.Info("Ammo: reload (" + why + "), " + Mag + "/" + MagSize + ".");
    }

    public static void Cancel(string why)
    {
        if (!Reloading)
        {
            return;
        }

        Reloading = false;
        ModLog.Info("Ammo: reload " + why + ".");
    }

    public static void Tick(RPawnPlayer pawn, RBatarang bat, bool current, float dt)
    {
        if (Mag > MagSize)
        {
            Mag = MagSize;
        }

        if (Reloading)
        {
            if (!current)
            {
                Cancel("cancelled: gadget switched");
            }
            else
            {
                relT += dt;
                RifleAnim.ReloadT = relT;

                if (!soundDone && relT >= SoundAt)
                {
                    soundDone = true;

                    if (RifleProps.ReloadEvent != null)
                    {
                        try { bat.PlayGadgetSound(RifleProps.ReloadEvent, false); } catch { }
                    }
                }

                float total = Math.Max(0.5f, RifleConfig.C.ReloadTime);

                if (!filled && relT >= total * FillShare)
                {
                    filled = true;
                    Mag = MagSize;
                }

                if (relT >= total)
                {
                    Reloading = false;
                    Mag = MagSize;
                    ModLog.Info("Ammo: reload complete, " + Mag + "/" + MagSize + ".");
                }
            }
        }

        if (autoReloadAt > 0f && Util.Now >= autoReloadAt)
        {
            autoReloadAt = -1f;

            if (current && Mag == 0)
            {
                StartReload(pawn, bat, "automatic");
            }
        }
    }
}
