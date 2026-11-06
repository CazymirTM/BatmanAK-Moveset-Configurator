#nullable enable

using System;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace BatmanDeathstrokeSniper;

[Script]
public class BatmanDeathstrokeSniperScript : Script
{
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    private IntPtr trackedPawn = IntPtr.Zero;
    private DateTime installAt = DateTime.MinValue;
    private bool installed;
    private bool tickFailureLogged;
    private bool lastSniper;
    private string dzSignal = "";
    private long lastTick;

    public override void Main()
    {
        Util.TryCall("config", () => SniperConfig.Init(Mod.DirectoryPath));
    }

    public override void OnLoad()
    {
        Util.TryCall("config", () => SniperConfig.Init(Mod.DirectoryPath));
    }

    public override void OnEnterGame()
    {
        ResetTracking();
        ModLog.Info("Loaded. Hold the Disruptor key (7) to switch Disruptor <-> sniper rifle, R = reload, " + SniperConfig.C.LaserKey + " = laser on/off, NumPad / = state dump.");
    }

    public override void OnEnterMenu() => ResetTracking();

    public override void OnUnload()
    {
        if (Sniper.Batman() != null)
        {
            Util.TryCall("laser", SniperLaser.Destroy);
        }

        SetDeadZone(false);
        SetInHand(false);
        ResetTracking();
    }

    public override void OnKeyDown(Keys key)
    {
        SniperPad.KeyboardUsed();

        if (!BatmanArsenalPack.Arsenal.Sniper)
        {
            return;
        }

        if (key == Keys.R && installed && Sniper.Selected && Sniper.Held(out RPawnPlayer? holder, out RDisruptorSniper? dis) && holder != null)
        {
            Util.TryCall("reload", () => SniperAmmo.StartReload(holder, dis, "R"));
        }

        if (key == SniperConfig.C.LaserKey && installed)
        {
            Util.TryCall("laser toggle", SniperMode.ToggleLaser);
        }

        if (key == Keys.Divide || key == Keys.OemQuestion)
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
        SniperConfig.Tick();

        if (!BatmanArsenalPack.Arsenal.Sniper)
        {
            Deactivate();

            return;
        }

        long now64 = Environment.TickCount64;
        float dt = lastTick == 0 ? 0f : Math.Clamp((now64 - lastTick) / 1000f, 0f, 0.1f);

        lastTick = now64;

        Util.TryCall("bullets", () => SniperFire.TickBullets(dt));
        Util.TryCall("fear", SniperFear.Tick);

        RPlayerController? controller = Game.GetPlayerController(0);
        RPawnPlayer? pawn = controller?.Pawn as RPawnPlayer;

        if (controller == null || pawn == null || !pawn.IsValid || !Sniper.IsBatman(pawn))
        {
            if (trackedPawn != IntPtr.Zero)
            {
                ResetTracking();
            }

            SetDeadZone(false);
            SetInHand(false);

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
            SniperMode.Installed = true;
            ModLog.Info("Installed; the Disruptor slot holds the " + (Sniper.Selected ? "sniper rifle" : "Disruptor") + ".");
        }

        SniperMode.Tick(controller);

        RDisruptorSniper? dis = Sniper.Dis(pawn);

        if (dis == null)
        {
            SniperLaser.Off();
            SetDeadZone(false);
            SetInHand(false);

            return;
        }

        bool sniper = Sniper.Selected;

        if (lastSniper && !sniper)
        {
            SniperAmmo.RestoreDisruptor(dis, pawn);
        }

        lastSniper = sniper;

        SniperProps.Update(pawn, dis, sniper, dt);

        if (!sniper)
        {
            SniperLaser.Off();
            SetDeadZone(false);
            SetInHand(false);

            return;
        }

        SniperFire.KillFilter(dis);
        SniperAmmo.Tick(pawn, dis, dt);

        bool held = (pawn.InvManager as RInventoryManager)?.CurrentGadget is RDisruptorSniper cur && Util.Same(cur, dis);

        SetDeadZone(held && SniperConfig.C.DeadZone);
        SetInHand(held && Sniper.InHand(dis));

        Util.TryCall("laser", () => SniperLaser.Tick(controller, pawn, dis, held && Sniper.InHand(dis) && SniperLaser.Aimed(dis) && !SniperAmmo.Reloading));
    }

    private string inHandSignal = "";

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
            AppDomain.CurrentDomain.SetData("BatmanSniper.InHand", v);
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
            AppDomain.CurrentDomain.SetData("DZAim.Allow", v);
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
            RPawnPlayer? pawn = Sniper.Batman();
            RDisruptorSniper? dis = Sniper.Dis(pawn);

            if (pawn != null && pawn.Ptr == trackedPawn)
            {
                SniperLaser.Destroy();

                if (dis != null)
                {
                    SniperAmmo.RestoreDisruptor(dis, pawn);
                    SniperProps.Deactivate(pawn, dis);
                }
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn("Turning the sniper rifle off failed: " + Util.Unwrap(exception));
        }

        SetDeadZone(false);
        SetInHand(false);
        ResetTracking();
        ModLog.Info("Sniper rifle turned OFF in the Arsenal Pack menu; the Disruptor is back to normal.");
    }

    private void ResetTracking()
    {
        trackedPawn = IntPtr.Zero;
        installed = false;
        lastSniper = false;
        SniperMode.Installed = false;
        SniperMode.Reset();
        SniperProps.Reset();
        SniperAmmo.Reset();
        SniperFire.Reset();
        SniperFear.Reset();
        SniperLaser.Reset();
    }

    private void Report()
    {
        RPawnPlayer? pawn = Sniper.Batman();
        RDisruptorSniper? dis = Sniper.Dis(pawn);

        if (pawn == null)
        {
            ModLog.Info("Report: the player is not Batman.");

            return;
        }

        if (dis == null)
        {
            ModLog.Info("Report: Batman has no Disruptor.");

            return;
        }

        string cur = Util.ClassName((pawn.InvManager as RInventoryManager)?.CurrentGadget);

        ModLog.Info("Report: mode " + (Sniper.Selected ? "SNIPER" : "DISRUPTOR") + ", holding " + cur + ", disruptor state " + dis.GadgetState + " (" + dis.GetStateName() + "), wheel slot " + Sniper.WheelIndex(pawn) +
            ", ammo " + SniperAmmo.Mag + "/" + SniperAmmo.MagSize + (SniperAmmo.Reloading ? " reloading" : "") + ", charges " + dis.iAntiEquipmentRemainingAmmo + "/" + dis.iAntiEquipmentMaximumAmmo +
            ", filter " + dis.bPostProcessIsTurnedOn + ", dead zone signal " + dzSignal + ".");
        ModLog.Info("Report: " + SniperProps.Report(pawn, dis) + ".");

        try
        {
            var near = new System.Collections.Generic.List<(float d, string s)>();

            for (Pawn? p = pawn.WorldInfo.PawnList; p != null; p = p.NextPawn)
            {
                if (p is RPawnVillain v && v.IsAliveAndWell())
                {
                    float d = System.Numerics.Vector3.Distance(v.Location, pawn.Location);

                    near.Add((d, Util.ClassName(v) + " @" + Util.V(v.Location) + " " + (d / 100f).ToString("0", Util.Inv) + "m"));
                }
            }

            near.Sort((a, b) => a.d.CompareTo(b.d));
            ModLog.Info("Report: " + near.Count + " villains alive; nearest: " + string.Join(" | ", near.GetRange(0, Math.Min(6, near.Count)).ConvertAll(x => x.s)));
        }
        catch (Exception exception)
        {
            ModLog.Info("Report: villain scan failed: " + Util.Unwrap(exception));
        }
    }
}
