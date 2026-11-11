#nullable enable

using System;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace BatmanAssaultRifle;

[Script]
public class BatmanAssaultRifleScript : Script
{
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    private IntPtr trackedPawn = IntPtr.Zero;
    private DateTime installAt = DateTime.MinValue;
    private bool installed;
    private bool tickFailureLogged;
    private string dzSignal = "";
    private string inHandSignal = "";
    private long lastTick;
    private float holdAfterBurst;
    private DateTime nextSlotCheck = DateTime.MinValue;

    public override void Main()
    {
        Util.TryCall("config", () => RifleConfig.Init(Mod.DirectoryPath));
        RifleAmmo.Init();
    }

    public override void OnLoad()
    {
        Util.TryCall("config", () => RifleConfig.Init(Mod.DirectoryPath));
        RifleAmmo.Init();
    }

    public override void OnEnterGame()
    {
        ResetTracking();
        ModLog.Info("Loaded. Hold the Batarang key (1) to switch Batarang <-> assault rifle, hold the fire button for full auto, R = reload, NumPad * = state dump.");
    }

    public override void OnEnterMenu()
    {
        RifleProps.DropAudioGun(false);
        RifleReticle.ForgetHud();
        RifleIcon.Forget();
        WheelPages.Forget();
        Util.TryCall("stance restore", RifleStance.Restore);
        ResetTracking();
    }

    public override void OnUnload()
    {
        Util.TryCall("icon restore", RifleIcon.Release);
        Util.TryCall("wheel restore", WheelPages.Release);
        Util.TryCall("stance restore", RifleStance.Restore);
        SetDeadZone(false);
        SetInHand(false);
        ResetTracking();
    }

    public override void OnKeyDown(Keys key)
    {
        RiflePad.KeyboardUsed();

        if (!BatmanArsenalPack.Arsenal.Rifle)
        {
            return;
        }

        if (key == Keys.R && installed && Rifle.HeldBy(out RPawnPlayer? holder, out RBatarang? bat) && holder != null)
        {
            Util.TryCall("reload", () => RifleAmmo.StartReload(holder, bat, "R"));
        }

        if (key == Keys.Multiply)
        {
            Util.TryCall("report", Report);
        }
    }

    public override void OnTick()
    {
        try
        {
            Update();
        }
        catch (Exception exception)
        {
            if (!tickFailureLogged)
            {
                tickFailureLogged = true;
                ModLog.Warn("Tick failed (further failures are not logged): " + Util.Unwrap(exception));
            }
        }
    }

    private void Update()
    {
        RifleConfig.Tick();

        if (!BatmanArsenalPack.Arsenal.Rifle)
        {
            Deactivate();

            return;
        }

        long now64 = Environment.TickCount64;
        float dt = lastTick == 0 ? 0f : Math.Clamp((now64 - lastTick) / 1000f, 0f, 0.1f);

        lastTick = now64;

        Util.TryCall("fx", () => RifleFire.TickFx(dt));

        RPlayerController? controller = Game.GetPlayerController(0);
        RPawnPlayer? pawn = controller?.Pawn as RPawnPlayer;

        if (controller == null || pawn == null || !pawn.IsValid || !Rifle.IsBatman(pawn))
        {
            if (trackedPawn != IntPtr.Zero)
            {
                ResetTracking();
            }

            SetDeadZone(false);
            SetInHand(false);
            RifleReticle.SetAimed(false);
            RifleHud.Show = false;

            return;
        }

        DateTime now = DateTime.UtcNow;

        if (pawn.Ptr != trackedPawn)
        {
            ResetTracking();
            trackedPawn = pawn.Ptr;
            installAt = now + SettleTime;
            ModLog.Info("Batman pawn detected (" + Util.ClassName(pawn) + ").");

            return;
        }

        if (!installed)
        {
            if (now < installAt)
            {
                return;
            }

            installed = true;
            RifleMode.Installed = true;
            ModLog.Info("Installed; the Batarang slot holds the " + (Rifle.Selected ? "assault rifle" : "Batarang") + ".");
        }

        RifleMode.Tick(controller);
        RifleProps.KeepAudioGun(pawn);

        RBatarang? bat = Rifle.Bat(pawn);

        if (bat == null)
        {
            RifleReticle.SetAimed(false);
            RifleHud.Show = false;
            SetDeadZone(false);
            SetInHand(false);

            return;
        }

        bool rifle = Rifle.Selected;
        bool current = Rifle.IsCurrent(pawn, bat);

        RifleMode.UpdateLabel(controller, rifle && current);
        RifleIcon.CornerWanted = rifle && current;

        if (now >= nextSlotCheck)
        {
            nextSlotCheck = now + TimeSpan.FromSeconds(1);
            RifleIcon.Slot = Rifle.WheelIndex(pawn);
        }
        Util.TryCall("stance", () => RifleStance.Sync(pawn, rifle));

        if (!rifle)
        {
            RifleAnim.StopRecoil(pawn);
            RifleAnim.Stop(pawn);
            RifleProps.Update(pawn, bat, false, false, null, dt);
            RifleAmmo.Cancel("cancelled: back to the Batarang");
            RifleReticle.SetAimed(false);
            RifleHud.Show = false;
            SetDeadZone(false);
            SetInHand(false);

            return;
        }

        bool combatMove = pawn.CombatMove != null;
        bool aimed = current && !combatMove && Rifle.Primed(bat);

        RifleReticle.SetAimed(current && (aimed || bat.bAiming));

        RifleAmmo.Tick(pawn, bat, current, dt);
        RifleFire.Tick(controller, pawn, bat, aimed, dt);

        bool bursting = current && RifleFire.Bursting;

        if (bursting)
        {
            holdAfterBurst = 0.25f;
        }
        else if (holdAfterBurst > 0f)
        {
            holdAfterBurst -= dt;
        }

        bool reloading = current && RifleAmmo.Reloading;
        bool raising = current && !combatMove && bat.bAiming;

        bool inHand = current && (aimed || raising || bursting || reloading || holdAfterBurst > 0f);

        RifleAnim.Want want = !inHand ? RifleAnim.Want.None
            : reloading ? RifleAnim.Want.Reload
            : (RifleFire.Firing || bursting) ? RifleAnim.Want.Fire
            : RifleAnim.Want.Aim;
        bool recoil = false;

        Vector3? aimPoint = aimed ? RifleFire.AimPoint(bat, pawn) : null;

        RifleProps.Update(pawn, bat, true, inHand, aimPoint, dt);
        RifleAnim.Guard = aimed || raising || bursting;
        RifleAnim.Update(pawn, want, dt);
        RifleAnim.UpdateRecoil(pawn, recoil, dt);

        RifleHud.Show = inHand;
        SetDeadZone(aimed && RifleConfig.C.DeadZone);
        SetInHand(inHand);
    }

    private void SetInHand(bool on)
    {
        string v = on ? "1" : "0";

        if (v == inHandSignal)
        {
            return;
        }

        inHandSignal = v;

        try
        {
            AppDomain.CurrentDomain.SetData("BatmanRifle.InHand", v);
        }
        catch
        {
        }
    }

    private void SetDeadZone(bool on)
    {
        string v = on ? "1" : "0";

        if (v == dzSignal)
        {
            return;
        }

        dzSignal = v;

        try
        {
            AppDomain.CurrentDomain.SetData("DZAim.AllowRifle", v);
        }
        catch
        {
        }
    }

    private void Deactivate()
    {
        if (trackedPawn == IntPtr.Zero && !installed)
        {
            return;
        }

        try
        {
            RPawnPlayer? pawn = Rifle.Batman();

            if (pawn != null && pawn.Ptr == trackedPawn)
            {
                RBatarang? bat = Rifle.Bat(pawn);

                if (Game.GetPlayerController(0) is RPlayerController controller)
                {
                    RifleMode.UpdateLabel(controller, false);
                }

                RifleIcon.Release();

                RifleAnim.StopRecoil(pawn);
                RifleAnim.Stop(pawn);
                RifleProps.Deactivate(pawn, bat);
                RifleStance.Restore();
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn("Turning the assault rifle off failed: " + Util.Unwrap(exception));
        }

        SetDeadZone(false);
        SetInHand(false);
        RifleReticle.SetAimed(false);
        RifleHud.Show = false;
        ResetTracking();
        ModLog.Info("Assault rifle turned OFF in the Arsenal Pack menu; the Batarang is back to normal.");
    }

    private void ResetTracking()
    {
        trackedPawn = IntPtr.Zero;
        installed = false;
        RifleIcon.CornerWanted = false;
        nextSlotCheck = DateTime.MinValue;
        holdAfterBurst = 0f;
        RifleProps.DropAudioGun(true);
        RifleReticle.SetAimed(false);
        RifleMode.Installed = false;
        RifleMode.Reset();
        RifleProps.Reset();
        RifleStance.Reset();
        RifleAnim.Reset();
        RifleAmmo.Reset();
        RifleFire.Reset();
    }

    private void Report()
    {
        RPawnPlayer? pawn = Rifle.Batman();
        RBatarang? bat = Rifle.Bat(pawn);

        if (pawn == null || bat == null)
        {
            ModLog.Info("Report: not Batman, or no Batarang.");

            return;
        }

        string cur = Util.ClassName((pawn.InvManager as RInventoryManager)?.CurrentGadget);

        ModLog.Info("Report: mode " + (Rifle.Selected ? "RIFLE" : "BATARANG") + ", holding " + cur + ", batarang state " + bat.GetStateName() + " aim " + bat.bAiming + ", wheel slot " + Rifle.WheelIndex(pawn) +
            ", ammo " + RifleAmmo.Mag + "/" + RifleAmmo.MagSize + (RifleAmmo.Reloading ? " reloading" : "") + ", firing " + RifleFire.Firing + ", anim " + RifleAnim.Report() + ", dead zone " + dzSignal + ", " + RifleReticle.Report() + ".");
        ModLog.Info("Report: " + RifleProps.Report(pawn) + ".");
    }
}
