#nullable enable

using System;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;
using BatmanArsenalPack;

namespace BatmanRobinStaff;

[Script]
public class BatmanRobinStaffScript : Script
{
    private enum Phase { Stowed, Drawing, Out, Stowing }

    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    private const float DrawFrom = 0.80f;
    private const float DrawShowAt = 0.70f;
    private const float DrawTime = 0.85f;
    private const float StowShowUntil = 0.70f;
    private const float StowTime = 1.1f;
    private const float StowEnd = 0.86f;

    private IntPtr trackedPawn = IntPtr.Zero;
    private DateTime installAt = DateTime.MinValue;
    private bool installed;
    private bool tickFailureLogged;
    private Phase phase = Phase.Stowed;
    private float phaseTime;
    private bool soundDone;
    private int overlayId = -1;
    private long lastTick;
    private string inHandSignal = "";
    private bool iconsShown;

    public override void Main() => Util.TryCall("config", () => StaffConfig.Init(Mod.DirectoryPath));

    public override void OnLoad() => Util.TryCall("config", () => StaffConfig.Init(Mod.DirectoryPath));

    public override void OnEnterGame()
    {
        LetGo();
        ModLog.Info("Loaded. Hold the Freeze Blast key (5) to swap Freeze Blast <-> Robin's staff, tap it to draw or put the staff back.");
    }

    public override void OnEnterMenu() => LetGo();

    public override void OnUnload()
    {
        try
        {
            RPawnPlayer? pawn = Staff.Batman();

            if (pawn != null && pawn.Ptr == trackedPawn)
            {
                StaffMoves.Restore(pawn);
                StaffProp.Detach(pawn);
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn("Unload failed: " + Util.Unwrap(exception));
        }

        SetInHand(false);
    }

    public override void OnKeyDown(Keys key)
    {
        StaffPad.KeyboardUsed();

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
        StaffConfig.Tick();

        if (!Arsenal.Staff)
        {
            Deactivate();

            return;
        }

        long now64 = Environment.TickCount64;
        float dt = lastTick == 0 ? 0f : Math.Clamp((now64 - lastTick) / 1000f, 0f, 0.1f);

        lastTick = now64;

        RPlayerController? controller = Game.GetPlayerController(0);
        RPawnPlayer? pawn = controller?.Pawn as RPawnPlayer;

        if (controller == null || pawn == null || !pawn.IsValid || !Staff.IsBatman(pawn))
        {
            if (trackedPawn != IntPtr.Zero)
            {
                ForgetPawn();
            }

            SetInHand(false);

            return;
        }

        DateTime now = DateTime.UtcNow;

        if (pawn.Ptr != trackedPawn)
        {
            ForgetPawn();
            StaffMoves.Adopt(pawn);
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
            StaffMode.Installed = true;
            StaffProp.RemoveLeftovers(pawn);
            StaffMode.RefreshWheel(controller);

            iconsShown = true;
            ModLog.Info("Installed; the Freeze Blast slot holds " + (StaffConfig.C.StaffSelected ? "Robin's staff" : "the Freeze Blast") + ".");
        }

        StaffMode.Tick(controller);

        RFreezeSpray? freeze = Staff.Freeze(pawn);

        if (freeze == null || pawn.InvManager is not RInventoryManager inventory || !inventory.IsValid)
        {
            return;
        }

        bool current = Util.Same(inventory.CurrentGadget, freeze);

        if (!current)
        {
            StaffMode.PutBack = false;
        }

        bool selected = StaffConfig.C.StaffSelected;
        bool want = selected && current && !StaffMode.PutBack && !pawn.IsInBatmobile();

        if (want)
        {
            StaffProp.EnsureAssets();
        }

        StaffMoves.Heal(pawn);
        Step(controller, pawn, freeze, want && StaffProp.Ready, dt);
        StaffProp.Tick(pawn);

        int slot = Staff.WheelIndex(pawn);

        if ((selected || iconsShown) && StaffIcons.Tick(controller, slot, current))
        {
            iconsShown = selected;
        }

        StaffIcons.UpdateLabel(controller, selected && current);

        BatmanRedHoodGuns.GunsGrip.StaffHand = phase == Phase.Out || phase == Phase.Drawing;

        if (!Arsenal.Guns)
        {
            BatmanRedHoodGuns.GunsGrip.Tick(pawn, false, false);
        }

        SetInHand(phase == Phase.Out || phase == Phase.Drawing);
    }

    private void Step(RPlayerController controller, RPawnPlayer pawn, RFreezeSpray freeze, bool want, float dt)
    {
        bool busy = pawn.CombatMove != null || pawn.CurrentSpecialMove != null;

        phaseTime += dt;

        switch (phase)
        {
            case Phase.Stowed:
                if (!want)
                {
                    StaffMoves.Restore(pawn);
                }
                else
                {
                    if (!StaffMoves.Apply(controller, pawn))
                    {
                        break;
                    }

                    if (!busy && PlayPutAway(pawn))
                    {
                        Enter(Phase.Drawing);
                        SetOverlayTime(pawn, DrawFrom);
                    }
                    else
                    {
                        StaffProp.Show(pawn, true);
                        Sound(freeze, StaffProp.ExtendEvent);
                        Enter(Phase.Out);
                    }
                }

                break;

            case Phase.Drawing:
            {
                float t = DrawFrom * (1f - phaseTime / DrawTime);

                if (!want || pawn.CombatMove != null)
                {
                    StopOverlay(pawn);
                    StaffProp.Show(pawn, true);
                    Enter(Phase.Out);

                    break;
                }

                if (t <= DrawShowAt && !soundDone)
                {
                    soundDone = true;
                    StaffProp.Show(pawn, true);
                    Sound(freeze, StaffProp.ExtendEvent);
                }

                if (t <= 0.02f)
                {
                    StopOverlay(pawn);
                    Enter(Phase.Out);

                    break;
                }

                SetOverlayTime(pawn, t);

                break;
            }

            case Phase.Out:
                if (!StaffMoves.RobinActive && want)
                {
                    StaffMoves.Apply(controller, pawn);
                }

                if (!want)
                {
                    if (pawn.IsInBatmobile())
                    {
                        StaffMoves.Restore(pawn);
                        Finish(pawn);
                    }
                    else if (pawn.CombatMove == null)
                    {
                        StaffMoves.Restore(pawn);

                        if (pawn.CurrentSpecialMove == null && PlayPutAway(pawn))
                        {
                            Enter(Phase.Stowing);
                        }
                        else
                        {
                            Sound(freeze, StaffProp.RetractEvent);
                            Finish(pawn);
                        }
                    }
                }

                break;

            case Phase.Stowing:
            {
                float t = StowEnd * phaseTime / StowTime;

                if (want)
                {
                    StopOverlay(pawn);
                    StaffMoves.Apply(controller, pawn);
                    StaffProp.Show(pawn, true);
                    Enter(Phase.Out);

                    break;
                }

                if (t >= StowShowUntil && !soundDone)
                {
                    soundDone = true;
                    StaffProp.Show(pawn, false);
                    Sound(freeze, StaffProp.RetractEvent);
                }

                if (t >= StowEnd || pawn.CombatMove != null)
                {
                    StopOverlay(pawn);
                    Finish(pawn);

                    break;
                }

                SetOverlayTime(pawn, t);

                break;
            }
        }
    }

    private void Enter(Phase next)
    {
        phase = next;
        phaseTime = 0f;
        soundDone = false;
        ModLog.Info("Staff: " + next + ".");
    }

    private void Finish(RPawnPlayer pawn)
    {
        StaffProp.Show(pawn, false);
        Enter(Phase.Stowed);
    }

    private bool PlayPutAway(RPawnPlayer pawn)
    {
        AnimSequence? sequence = StaffProp.PutAway;

        if (sequence == null || !sequence.IsValid)
        {
            return false;
        }

        RAnimConfig.FCustomAnimConfig config = default;

        config.UpperBodyAnim = sequence.SequenceName;
        config.CustomUpperBodyAnim = sequence;

        try
        {
            pawn.StopWeaponOverlay();
            overlayId = pawn.PlayCustomWeaponOverlay(config).AnimId;

            return true;
        }
        catch (Exception exception)
        {
            ModLog.Once("overlay-failed", "Staff: playing the animation failed: " + Util.Unwrap(exception));

            return false;
        }
    }

    private void StopOverlay(RPawnPlayer pawn)
    {
        overlayId = -1;
        Util.TryCall("StopWeaponOverlay", pawn.StopWeaponOverlay);
    }

    private void SetOverlayTime(RPawnPlayer pawn, float normalized)
    {
        RAnimNode_Pose? node = pawn.Anim;

        if (node == null)
        {
            return;
        }

        var overlays = node.WeaponOverlays.PlayingOverlays;

        for (int k = 0; k < overlays.Count; k++)
        {
            var entry = overlays[k];

            if (entry.Id != overlayId)
            {
                continue;
            }

            var state = entry.State;
            var time = state.Time;

            time.Current = normalized;
            time.Previous = normalized;
            state.Time = time;
            entry.State = state;
            overlays[k] = entry;

            return;
        }
    }

    private static void Sound(RFreezeSpray freeze, AkEvent? sound)
    {
        if (sound != null)
        {
            try { freeze.PlayGadgetSound(sound, false); } catch { }
        }
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
            AppDomain.CurrentDomain.SetData("BatmanStaff.InHand", v);
        }
        catch
        {
        }
    }

    private void LetGo()
    {
        try
        {
            RPawnPlayer? pawn = Staff.Batman();

            if (pawn != null && pawn.Ptr == trackedPawn)
            {
                StaffMoves.Restore(pawn);
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn("Letting go of the pawn failed: " + Util.Unwrap(exception));
        }

        ForgetPawn();
    }

    private void ForgetPawn()
    {
        StaffProp.Forget();
        StaffMode.Installed = false;
        StaffMode.Reset();
        StaffIcons.Reset();
        BatmanRedHoodGuns.GunsGrip.StaffHand = false;
        trackedPawn = IntPtr.Zero;
        installed = false;
        phase = Phase.Stowed;
        phaseTime = 0f;
        overlayId = -1;
        iconsShown = false;
    }

    private void Deactivate()
    {
        if (trackedPawn == IntPtr.Zero && !installed)
        {
            return;
        }

        try
        {
            RPawnPlayer? pawn = Staff.Batman();

            if (pawn != null && pawn.Ptr == trackedPawn)
            {
                if (phase == Phase.Drawing || phase == Phase.Stowing)
                {
                    StopOverlay(pawn);
                }

                StaffMoves.Restore(pawn);
                StaffProp.Detach(pawn);

                if (Game.GetPlayerController(0) is RPlayerController controller)
                {
                    StaffIcons.Off(controller, Staff.WheelIndex(pawn));
                    StaffMode.RefreshWheel(controller);
                }
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn("Turning the staff off failed: " + Util.Unwrap(exception));
        }

        SetInHand(false);
        ForgetPawn();
        ModLog.Info("Robin's staff turned OFF in the Arsenal Pack menu; the Freeze Blast and Batman's moves are back to normal.");
    }

    private void Report()
    {
        RPawnPlayer? pawn = Staff.Batman();

        if (pawn == null)
        {
            ModLog.Info("Report: the player is not Batman.");

            return;
        }

        ModLog.Info("Report: phase " + phase + ", slot holds " + (StaffConfig.C.StaffSelected ? "STAFF" : "FREEZE") + ", put back " + StaffMode.PutBack + ", holding " + Util.ClassName((pawn.InvManager as RInventoryManager)?.CurrentGadget) + ", " + StaffMoves.Report(pawn) + ", " + StaffProp.Report(pawn) + ".");
    }
}
