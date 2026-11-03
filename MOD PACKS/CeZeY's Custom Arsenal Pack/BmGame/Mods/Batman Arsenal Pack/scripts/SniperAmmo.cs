#nullable enable

using System;
using System.Collections.Generic;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace BatmanDeathstrokeSniper;

internal static class SniperAmmo
{
    private const float AnimStart = 0.4f;
    private const float FillAt = 2.6f;
    private const float EndAt = 3.4f;
    private const float SoundAt = 0.1f;

    public static int MagSize => SniperConfig.C.Magazine;
    public static int Mag = 3;
    public static bool Reloading;

    private static float relT;
    private static bool soundDone, filled;
    private static float autoReloadAt = -1f;
    private static float nextDry;

    private static AnimSequence? reloadSeq;
    private static bool reloadSeqTried;
    private static RAnimUtil_OverlayPlayer.FOverlayId animId;
    private static bool animOn, seekPending;
    private static int seekTries;

    private static bool haveOrig;
    private static int origMax, origRemaining, origAmmo, origMaxAmmo, origOverride;
    private static IntPtr origDis;

    public static void Reset()
    {
        Mag = MagSize;
        Reloading = false;
        animOn = false;
        autoReloadAt = -1f;
        haveOrig = false;
        origDis = IntPtr.Zero;
        reloadSeq = null;
        reloadSeqTried = false;
    }

    public static bool CanShoot(RDisruptorSniper g, RPawnPlayer pawn)
    {
        if (Reloading)
        {
            return false;
        }

        if (Mag <= 0)
        {
            DryFire(g, pawn);

            return false;
        }

        return true;
    }

    public static void Consume()
    {
        Mag = Math.Max(0, Mag - 1);

        if (Mag == 0)
        {
            autoReloadAt = Util.Now + 0.4f;
        }
    }

    private static void DryFire(RDisruptorSniper g, RPawnPlayer pawn)
    {
        if (Util.Now < nextDry)
        {
            return;
        }

        nextDry = Util.Now + 0.35f;

        AkEvent? e = SniperProps.DisDryEvent ?? SniperProps.DryFireEvent;

        if (e != null)
        {
            try { g.PlayGadgetSound(e, false); } catch { }
        }

        try { g.sNextTargetModuleEvent = new FString("DryFire"); } catch { }

        StartReload(pawn, g, "magazine empty");
    }

    public static void StartReload(RPawnPlayer pawn, RDisruptorSniper? g, string why)
    {
        if (g == null || Reloading)
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
        soundDone = filled = false;
        autoReloadAt = -1f;

        if (Sniper.InHand(g))
        {
            StartAnim(pawn);
        }

        ModLog.Info("Ammo: reload (" + why + "), " + Mag + "/" + MagSize + ".");
    }

    private static AnimSequence? ReloadSeq()
    {
        if (reloadSeq != null && reloadSeq.IsValid)
        {
            return reloadSeq;
        }

        if (reloadSeqTried)
        {
            return null;
        }

        reloadSeqTried = true;

        AnimSet? set = Game.FindObject<AnimSet>("Anim_Thug_Rifle.Code.TGRifle_Movement");

        if (set != null)
        {
            foreach (AnimSequence s in set.Sequences)
            {
                if (s != null && s.SequenceName.ToString() == "Reload")
                {
                    reloadSeq = s;
                    s.AddToRoot();
                    set.AddToRoot();
                    break;
                }
            }
        }

        ModLog.Info("Ammo: rifle thug reload animation " + (reloadSeq != null ? "found (" + reloadSeq.SequenceLength.ToString("0.00", Util.Inv) + " s)" : "not loaded; reloading with sound only") + ".");

        return reloadSeq;
    }

    private static void StartAnim(RPawnPlayer pawn)
    {
        if (!SniperConfig.C.ReloadAnim)
        {
            return;
        }

        AnimSequence? seq = ReloadSeq();

        if (seq == null)
        {
            return;
        }

        var cfg = new RAnimConfig.FCustomAnimConfig();

        cfg.CustomUpperBodyAnim = seq;
        animId = pawn.PlayCustomWeaponOverlay(cfg);
        animOn = true;
        seekPending = true;
        seekTries = 5;
        TrySeek(pawn);
    }

    private static void TrySeek(RPawnPlayer pawn)
    {
        AnimSequence? seq = reloadSeq;
        RAnimNode_Pose? node = pawn.Anim;

        if (seq == null || node == null || seq.SequenceLength <= 0f)
        {
            seekPending = false;

            return;
        }

        float norm = Math.Clamp((AnimStart + relT) / seq.SequenceLength, 0f, 0.99f);
        var arr = node.WeaponOverlays.PlayingOverlays;

        for (int k = 0; k < arr.Count; k++)
        {
            var e = arr[k];

            if (e.Id != animId.AnimId)
            {
                continue;
            }

            var st = e.State;
            var t = st.Time;

            t.Current = norm;
            t.Previous = norm;
            st.Time = t;
            e.State = st;
            arr[k] = e;
            seekPending = false;

            return;
        }

        if (--seekTries <= 0)
        {
            seekPending = false;
        }
    }

    private static void StopAnim(RPawnPlayer pawn)
    {
        if (!animOn)
        {
            return;
        }

        animOn = false;

        try
        {
            if (pawn.IsPlayingOverlay(animId))
            {
                pawn.StopWeaponOverlay();
            }
        }
        catch
        {
        }
    }

    public static void Cancel(RPawnPlayer? pawn, string why)
    {
        if (!Reloading)
        {
            return;
        }

        Reloading = false;

        if (pawn != null)
        {
            StopAnim(pawn);
        }

        ModLog.Info("Ammo: reload " + why + ".");
    }

    public static void Tick(RPawnPlayer pawn, RDisruptorSniper dis, float dt)
    {
        if (Mag > MagSize)
        {
            Mag = MagSize;
        }

        bool current = (pawn.InvManager as RInventoryManager)?.CurrentGadget is RDisruptorSniper cur && Util.Same(cur, dis);

        if (Reloading)
        {
            if (!current)
            {
                Cancel(pawn, "cancelled: gadget switched");
            }
            else
            {
                bool inHand = Sniper.InHand(dis);

                if (animOn && !inHand)
                {
                    StopAnim(pawn);
                }

                if (animOn && seekPending)
                {
                    TrySeek(pawn);
                }

                relT += dt;

                if (!soundDone && relT >= SoundAt)
                {
                    soundDone = true;

                    if (SniperProps.ReloadEvent != null)
                    {
                        try { dis.PlayGadgetSound(SniperProps.ReloadEvent, false); } catch { }
                    }
                }

                if (!filled && relT >= FillAt)
                {
                    filled = true;
                    Mag = MagSize;
                }

                if (relT >= EndAt)
                {
                    Reloading = false;
                    StopAnim(pawn);
                    ModLog.Info("Ammo: reload complete, " + Mag + "/" + MagSize + ".");
                }
            }
        }

        if (autoReloadAt > 0f && Util.Now >= autoReloadAt)
        {
            autoReloadAt = -1f;

            if (current && Mag == 0)
            {
                StartReload(pawn, dis, "automatic");
            }
        }

        UpdateHud(dis);
    }

    private static void UpdateHud(RDisruptorSniper ds)
    {
        if (!haveOrig || origDis != ds.Ptr)
        {
            if (LoadOrig(ds))
            {
                goto saved;
            }

            origMax = ds.iAntiEquipmentMaximumAmmo;
            origRemaining = ds.iAntiEquipmentRemainingAmmo;
            origAmmo = ds.Ammo;
            origMaxAmmo = ds.MaxAmmo;
            origOverride = ds.CurrentRechargeValOverride;
            origDis = ds.Ptr;
            haveOrig = true;
            StoreOrig();
            ModLog.Info("Ammo: saved the Disruptor's charges (" + origRemaining + "/" + origMax + ", corner " + origAmmo + "/" + origMaxAmmo + ").");
        }

        saved:

        int ov = Reloading ? (int)Math.Clamp(relT / EndAt * 100f, 0f, 99f) : -1;

        if (ds.iAntiEquipmentMaximumAmmo != MagSize)
        {
            ds.iAntiEquipmentMaximumAmmo = MagSize;
        }

        if (ds.iAntiEquipmentRemainingAmmo != Mag)
        {
            ds.iAntiEquipmentRemainingAmmo = Mag;
        }

        ds.MaxAmmo = MagSize;
        ds.Ammo = Mag;
        ds.CurrentRechargeValOverride = ov;
        ds.UpdateGadgetHUDParams();
    }

    private const string OrigKey = "BatmanSniper.DisruptorCharges";

    private static void StoreOrig()
    {
        try { AppDomain.CurrentDomain.SetData(OrigKey, origDis.ToInt64() + "|" + origMax + "|" + origRemaining + "|" + origAmmo + "|" + origMaxAmmo + "|" + origOverride); } catch { }
    }

    private static bool LoadOrig(RDisruptorSniper ds)
    {
        try
        {
            if (AppDomain.CurrentDomain.GetData(OrigKey) is not string s)
            {
                return false;
            }

            string[] p = s.Split('|');

            if (p.Length != 6 || long.Parse(p[0]) != ds.Ptr.ToInt64())
            {
                return false;
            }

            origMax = int.Parse(p[1]);
            origRemaining = int.Parse(p[2]);
            origAmmo = int.Parse(p[3]);
            origMaxAmmo = int.Parse(p[4]);
            origOverride = int.Parse(p[5]);
            origDis = ds.Ptr;
            haveOrig = true;

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void RestoreDisruptor(RDisruptorSniper? ds, RPawnPlayer? pawn)
    {
        Cancel(pawn, "cancelled: back to the Disruptor");

        if (!haveOrig || ds == null || !ds.IsValid || ds.Ptr != origDis)
        {
            haveOrig = false;

            return;
        }

        ds.iAntiEquipmentMaximumAmmo = origMax;
        ds.iAntiEquipmentRemainingAmmo = origRemaining;
        ds.MaxAmmo = origMaxAmmo;
        ds.Ammo = origAmmo;
        ds.CurrentRechargeValOverride = origOverride;

        try { ds.UpdateGadgetHUDParams(); } catch { }

        haveOrig = false;
        try { AppDomain.CurrentDomain.SetData(OrigKey, null); } catch { }
        ModLog.Info("Ammo: Disruptor charges restored (" + origRemaining + "/" + origMax + ").");
    }
}
