#nullable enable

using System;
using System.Diagnostics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;
using BatmanArsenalPack;
using Canvas = BmSDK.Engine.Canvas;
using GameViewportClient = BmSDK.Engine.GameViewportClient;
using Hud = BatmanRedHoodGuns.GunsMode;

namespace BatmanRobinStaff;

internal static class StaffMode
{
    private static readonly string[] KeyNames = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "=" };
    private static readonly TimeSpan StuckKeyTimeout = TimeSpan.FromSeconds(8);

    public const string DisplayClass = "RRobinStaffShield";

    public static bool Installed;
    public static string KeyLabel = "5";

    public static bool PutBack;

    private static bool keyDown;
    private static DateTime keyDownAt = DateTime.MinValue;
    private static bool switchedThisPress;
    private static bool wasCurrentAtPress;

    public static void Reset()
    {
        keyDown = false;
        switchedThisPress = false;
        wasCurrentAtPress = false;
        PutBack = false;
    }

    public static void Tick(RPlayerController controller)
    {
        if (!keyDown)
        {
            return;
        }

        TimeSpan held = DateTime.UtcNow - keyDownAt;

        if (held > StuckKeyTimeout)
        {
            keyDown = false;

            return;
        }

        if (!switchedThisPress && held.TotalSeconds >= StaffConfig.C.SwitchHold)
        {
            switchedThisPress = true;
            Util.TryCall("switch mode", () => Toggle(controller, equip: true));
        }
    }

    public static void Toggle(RPlayerController controller, bool equip)
    {
        RPawnPlayer? pawn = Staff.Batman();
        RFreezeSpray? freeze = Staff.Freeze(pawn);

        if (pawn == null || freeze == null || pawn.InvManager is not RInventoryManager inventory || !inventory.IsValid)
        {
            ModLog.Info("Mode: Batman has no Freeze Blast yet; nothing to switch.");

            return;
        }

        bool toStaff = !StaffConfig.C.StaffSelected;

        StaffConfig.SetSelected(toStaff);
        PutBack = false;

        if (equip && !Util.Same(inventory.CurrentGadget, freeze))
        {
            bool ok = inventory.SetCurrentGadget(freeze, true);

            Util.TryCall("GadgetsUpdated", controller.GadgetsUpdated);
            ModLog.Info("Mode: equipped the " + (toStaff ? "staff" : "Freeze Blast") + " (selected=" + ok + ").");
        }

        RefreshWheel(controller);

        if (controller.RadialGadgetSelect is RGFxMovieRadialGadgetSelect open && open.IsValid && open.bWheelOpen)
        {
            int highlighted = open.SelectedGadgetIndex;

            Util.TryCall("reselect slot", () => open.SelectWheelGadget(highlighted));
        }

        else if (controller.RadialGadgetSelect is RGFxMovieRadialGadgetSelect corner && corner.IsValid && Util.Same(inventory.CurrentGadget, freeze))
        {
            int slot = Staff.WheelIndex(pawn);

            Util.TryCall("ShowCurrentGadget", () => corner.ShowCurrentGadget(slot));
        }

        ModLog.Info("Mode: the Freeze Blast slot now holds " + (toStaff ? "Robin's staff" : "the Freeze Blast") + ".");
        ShowSlotToast();
    }

    public static void RefreshWheel(RPlayerController controller)
    {
        if (controller.RadialGadgetSelect is RGFxMovieRadialGadgetSelect radial && radial.IsValid)
        {
            Util.TryCall("SendGadgetsToFlash", radial.SendGadgetsToFlash);
        }
    }

    [Redirect(typeof(RGFxMovieRadialGadgetSelect), nameof(RGFxMovieRadialGadgetSelect.SendGadgetsToFlashPrivate))]
    private static void SendGadgetsToFlashPrivateRedirect(RGFxMovieRadialGadgetSelect self, FString characterAcronym, int maxSlots, FString gadgetList)
    {
        string acronym = characterAcronym.ToString() ?? "";
        string list = gadgetList.ToString() ?? "";

        if (Arsenal.Staff && Installed && StaffConfig.C.StaffSelected && Staff.Batman() != null)
        {
            string[] entries = list.Split(',');

            for (int index = 0; index < entries.Length; index++)
            {
                if (entries[index].StartsWith(Staff.FreezeClass + "|", StringComparison.Ordinal))
                {
                    entries[index] = DisplayClass + entries[index].Substring(Staff.FreezeClass.Length);
                }
            }

            list = string.Join(",", entries);
        }

        self.SendGadgetsToFlashPrivate(new FString(acronym), maxSlots, new FString(list));
    }

    private static string SwitchHint => StaffPad.UsingPad ? StaffPad.SwitchName + " ON THE GADGET WHEEL" : "HOLD " + KeyLabel;

    private static string TapHint => StaffPad.UsingPad ? "" : "TAP " + KeyLabel + " TO DRAW OR PUT BACK  |  ";

    private static void ShowSlotToast()
    {
        if (StaffConfig.C.StaffSelected)
        {
            ShowToast("ROBIN'S STAFF", TapHint + SwitchHint + " FOR THE FREEZE BLAST", GreenAccent);
        }
        else
        {
            ShowToast("FREEZE BLAST", "CRYOGENIC GADGET  |  " + SwitchHint + " FOR ROBIN'S STAFF", BlueAccent);
        }
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.SelectGadget))]
    private static void SelectGadgetRedirect(RPlayerController self, int gadget)
    {
        if (!Arsenal.Staff)
        {
            self.SelectGadget(gadget);

            return;
        }

        try
        {
            if (Installed)
            {
                RPawnPlayer? pawn = Staff.Batman();
                int wheel = pawn != null ? self.PCGadgetIndexToWheelIndex(gadget) : -1;

                if (wheel >= 0 && wheel == Staff.WheelIndex(pawn))
                {
                    keyDown = true;
                    keyDownAt = DateTime.UtcNow;
                    switchedThisPress = false;
                    wasCurrentAtPress = (pawn!.InvManager as RInventoryManager)?.CurrentGadget is RFreezeSpray;

                    if (gadget >= 0 && gadget < KeyNames.Length)
                    {
                        KeyLabel = KeyNames[gadget];
                    }
                }
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("select-failed", "Mode: key handling failed: " + Util.Unwrap(exception));
        }

        self.SelectGadget(gadget);
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.SelectGadgetReleased))]
    private static void SelectGadgetReleasedRedirect(RPlayerController self)
    {
        if (!Arsenal.Staff)
        {
            self.SelectGadgetReleased();

            return;
        }

        bool ours = keyDown;
        bool switched = ours && switchedThisPress;
        bool tap = ours && !switched && wasCurrentAtPress && StaffConfig.C.StaffSelected;

        keyDown = false;
        switchedThisPress = false;

        if (switched || tap)
        {
            try { self.KeyboardQuickGadget = -1; } catch { }
        }

        if (tap)
        {
            PutBack = !PutBack;
            ModLog.Info("Mode: tapped the key with the staff selected: " + (PutBack ? "put it back." : "draw it."));
        }

        self.SelectGadgetReleased();
    }

    private static bool StaffOwnsFreeze(GameObject? gadget) =>
        Installed && StaffConfig.C.StaffSelected && gadget is RFreezeSpray;

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.CanAimGadget))]
    private static bool CanAimGadgetRedirect(RPlayerController self, RInventoryGadget gadget)
    {
        if (Arsenal.Staff && StaffOwnsFreeze(gadget))
        {
            return false;
        }

        return self.CanAimGadget(gadget);
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.PlayerTick))]
    private static void PlayerTickRedirect(RPlayerController self, float deltaTime)
    {
        if (Arsenal.Staff && Installed && StaffConfig.C.StaffSelected && self.PlayerInput is RPlayerInput input && input.bReadyGadgetButton != 0 && (self.Pawn as RPawnPlayer)?.InvManager is RInventoryManager inventory && StaffOwnsFreeze(inventory.CurrentGadget))
        {
            input.bReadyGadgetButton = 0;
            ModLog.Once("aim-block", "Mode: aiming the Freeze Blast is off while the staff is in its slot.");
        }

        self.PlayerTick(deltaTime);
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.FireQuickGadget))]
    private static bool FireQuickGadgetRedirect(RPlayerController self, RInventoryGadget quickGadget, bool overrideCombatMove)
    {
        if (Arsenal.Staff && StaffOwnsFreeze(quickGadget))
        {
            ModLog.Once("quick-block", "Mode: Freeze Blast quickfire is off while the staff is in its slot.");

            return false;
        }

        return self.FireQuickGadget(quickGadget, overrideCombatMove);
    }

    private static readonly (byte r, byte g, byte b) GreenAccent = (70, 200, 90);
    private static readonly (byte r, byte g, byte b) BlueAccent = (90, 170, 255);

    private const float ToastTime = 1.8f;
    private static float toastLeft;
    private static long lastDraw;
    private static string toastTitle = "";
    private static string toastSub = "";
    private static (byte r, byte g, byte b) toastAccent = GreenAccent;

    private static void ShowToast(string title, string sub, (byte r, byte g, byte b) accent)
    {
        toastTitle = title;
        toastSub = sub;
        toastAccent = accent;
        toastLeft = ToastTime;
    }

    [Redirect(typeof(GameViewportClient), "PostRender")]
    private static void ViewportPostRenderRedirect(GameViewportClient self, Canvas canvas)
    {
        self.PostRender(canvas);

        if (!Arsenal.Staff)
        {
            return;
        }

        try
        {
            DrawToast(canvas);
        }
        catch
        {
        }

        try
        {
            if (Installed)
            {
                StaffPad.Frame();
                StaffPad.DrawWheelHint(canvas);
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("pad-frame", "Pad: frame failed: " + Util.Unwrap(exception));
        }
    }

    private static void DrawToast(Canvas? c)
    {
        long now = Stopwatch.GetTimestamp();
        float dt = lastDraw == 0 ? 0f : Math.Clamp((float)Stopwatch.GetElapsedTime(lastDraw, now).TotalSeconds, 0f, 0.1f);

        lastDraw = now;

        if (toastLeft <= 0f || c == null)
        {
            return;
        }

        toastLeft -= dt;

        if (!Hud.LoadResources())
        {
            return;
        }

        float age = ToastTime - toastLeft;
        float alpha = Math.Clamp(Math.Min(age / 0.15f, toastLeft / 0.35f), 0f, 1f);
        float slide = (1f - Hud.EaseOut(age / 0.25f)) * 18f;
        float s = Math.Clamp(c.ClipY / 1080f, 0.75f, 2.5f);
        Font? oldFont = c.Font;

        if (Hud.HudFont != null)
        {
            c.Font = Hud.HudFont;
        }

        try
        {
            (byte r, byte g, byte b) accent = toastAccent;
            float titleScale = 1.55f * s;
            float subScale = 0.8f * s;

            c.TextSize(toastTitle, out float titleW, out float titleH, titleScale, titleScale);
            c.TextSize(toastSub, out float subW, out float subH, subScale, subScale);

            float padX = 28f * s;
            float padY = 12f * s;
            float w = Math.Max(titleW, subW) + padX * 2f;
            float h = titleH + subH + padY * 2f + 4f * s;
            float x = (c.ClipX - w) * 0.5f;
            float y = c.ClipY * 0.68f + slide * s;

            Hud.Rect(c, x, y, w, h, 10, 8, 10, 0.72f * alpha);
            Hud.Rect(c, x, y, w, 2f * s, accent.r, accent.g, accent.b, alpha);
            Hud.Rect(c, x, y + h - 2f * s, w, 2f * s, accent.r, accent.g, accent.b, 0.5f * alpha);
            Hud.Rect(c, x, y, 4f * s, h, accent.r, accent.g, accent.b, alpha);
            Hud.Text(c, x + (w - titleW) * 0.5f, y + padY, toastTitle, 245, 240, 240, alpha, titleScale);
            Hud.Text(c, x + (w - subW) * 0.5f, y + padY + titleH + 4f * s, toastSub, accent.r, accent.g, accent.b, 0.9f * alpha, subScale);
        }
        finally
        {
            c.Font = oldFont;
        }
    }
}
