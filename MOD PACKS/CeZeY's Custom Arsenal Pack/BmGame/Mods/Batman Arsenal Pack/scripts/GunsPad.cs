#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;
using Canvas = BmSDK.Engine.Canvas;

namespace BatmanRedHoodGuns;

internal static class GunsPad
{
    public const ushort DPadUp = 0x0001;
    public const ushort DPadDown = 0x0002;
    public const ushort DPadLeft = 0x0004;
    public const ushort DPadRight = 0x0008;
    public const ushort Start = 0x0010;
    public const ushort Back = 0x0020;
    public const ushort LeftThumb = 0x0040;
    public const ushort RightThumb = 0x0080;
    public const ushort LeftShoulder = 0x0100;
    public const ushort RightShoulder = 0x0200;
    public const ushort A = 0x1000;
    public const ushort B = 0x2000;
    public const ushort X = 0x4000;
    public const ushort Y = 0x8000;

    private static readonly Dictionary<string, ushort> ButtonNames = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = A, ["b"] = B, ["x"] = X, ["y"] = Y,
        ["lb"] = LeftShoulder, ["rb"] = RightShoulder,
        ["ls"] = LeftThumb, ["rs"] = RightThumb, ["l3"] = LeftThumb, ["r3"] = RightThumb,
        ["back"] = Back, ["view"] = Back, ["start"] = Start, ["menu"] = Start,
        ["up"] = DPadUp, ["down"] = DPadDown, ["left"] = DPadLeft, ["right"] = DPadRight,
        ["none"] = 0, ["off"] = 0, ["0"] = 0,
    };

    public static bool TryParseButton(string text, out ushort mask) => ButtonNames.TryGetValue(text.Trim(), out mask);

    public static string ButtonName(ushort mask)
    {
        foreach (KeyValuePair<string, ushort> pair in ButtonNames)
        {
            if (pair.Value == mask && mask != 0)
            {
                return pair.Key.ToUpperInvariant();
            }
        }

        return "?";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int GetState14(int userIndex, out XInputState state);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern int GetState910(int userIndex, out XInputState state);

    private const int ErrorSuccess = 0;
    private const short StickNoise = 9000;
    private const byte TriggerNoise = 40;

    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);

    private static bool useLegacyDll;
    private static bool unavailable;
    private static int padIndex = -1;
    private static DateTime nextScan = DateTime.MinValue;
    private static uint lastPacket;

    private static ushort buttons;
    private static ushort previous;
    private static DateTime lastPadInput = DateTime.MinValue;
    private static DateTime lastKeyInput = DateTime.MinValue;

    public static bool Connected => padIndex >= 0;

    public static bool UsingPad => Connected && lastPadInput > lastKeyInput;

    public static bool WheelHint { get; private set; }

    public static void KeyboardUsed() => lastKeyInput = DateTime.UtcNow;

    public static bool Pressed(ushort mask) => mask != 0 && (buttons & mask) == mask && (previous & mask) != mask;

    public static bool Down(ushort mask) => mask != 0 && (buttons & mask) == mask;

    public static void Reset()
    {
        previous = buttons;
        WheelHint = false;
    }

    private static bool Read(int index, out XInputState state)
    {
        state = default;

        if (unavailable)
        {
            return false;
        }

        try
        {
            return (useLegacyDll ? GetState910(index, out state) : GetState14(index, out state)) == ErrorSuccess;
        }
        catch (DllNotFoundException) when (!useLegacyDll)
        {
            useLegacyDll = true;
            ModLog.Info("Pad: xinput1_4.dll not found, using xinput9_1_0.dll.");

            return Read(index, out state);
        }
        catch (Exception exception)
        {
            unavailable = true;
            ModLog.Warn("Pad: XInput is not available, gamepad controls are off: " + Util.Unwrap(exception));

            return false;
        }
    }

    private static void Poll()
    {
        previous = buttons;

        if (padIndex >= 0)
        {
            if (Read(padIndex, out XInputState state))
            {
                Take(state);

                return;
            }

            ModLog.Info("Pad: controller " + padIndex + " disconnected.");
            padIndex = -1;
            buttons = 0;
            nextScan = DateTime.MinValue;
        }

        DateTime now = DateTime.UtcNow;

        if (now < nextScan)
        {
            return;
        }

        nextScan = now + ScanInterval;

        for (int index = 0; index < 4; index++)
        {
            if (Read(index, out XInputState state))
            {
                padIndex = index;
                lastPacket = state.PacketNumber;
                buttons = state.Gamepad.Buttons;
                previous = buttons;
                ModLog.Info("Pad: controller " + index + " connected.");

                return;
            }
        }
    }

    private static void Take(XInputState state)
    {
        XInputGamepad pad = state.Gamepad;

        buttons = pad.Buttons;

        if (state.PacketNumber == lastPacket)
        {
            return;
        }

        lastPacket = state.PacketNumber;

        bool moved = buttons != 0 || pad.LeftTrigger > TriggerNoise || pad.RightTrigger > TriggerNoise ||
            Math.Abs((int)pad.ThumbLX) > StickNoise || Math.Abs((int)pad.ThumbLY) > StickNoise ||
            Math.Abs((int)pad.ThumbRX) > StickNoise || Math.Abs((int)pad.ThumbRY) > StickNoise;

        if (moved)
        {
            lastPadInput = DateTime.UtcNow;
        }
    }

    public static bool Installed;

    public static void Frame()
    {
        if (!Installed || Guns.Gun == null || !Guns.Gun.IsValid || Game.GetPlayerController(0) is not RPlayerController controller || !controller.IsValid || Guns.Batman() == null)
        {
            WheelHint = false;

            return;
        }

        if (Guns.HeldBy(out _, out _))
        {
            gunsHeldAt = DateTime.UtcNow;
        }

        Tick(controller);
    }

    private static void Tick(RPlayerController controller)
    {
        GunsConfig.Settings c = GunsConfig.C;

        if (!c.PadEnabled)
        {
            WheelHint = false;

            return;
        }

        Poll();

        if (!Connected)
        {
            WheelHint = false;

            return;
        }

        RGFxMovieRadialGadgetSelect? radial = controller.RadialGadgetSelect;
        bool wheelOpen = radial != null && radial.IsValid && radial.bWheelOpen;

        if (Diag)
        {
            Diagnose(controller, radial);
        }
        int shared = GunsWheel.SharedSlotIndex;
        bool onShared = wheelOpen && shared >= 0 && radial!.SelectedGadgetIndex == shared && Guns.Gun != null && Guns.Gun.IsValid;

        WheelHint = onShared && UsingPad;

        ushort watched = (ushort)(c.PadSwitch | c.PadHands | c.PadReload);
        ushort pressed = (ushort)(buttons & ~previous & watched);

        if (Pressed(c.PadReload))
        {
            reloadPressedAt = DateTime.UtcNow;
        }

        if (pressed != 0)
        {
            ModLog.Info("Pad: pressed " + ButtonName(pressed) + " (wheel " + (wheelOpen ? "open on slot " + radial!.SelectedGadgetIndex + ", shared slot " + shared : "closed") +
                ", guns " + (Guns.HeldBy(out _, out _) ? (GunsAim.IsAimed ? "aimed" : "held") : "not held") + ").");
        }

        if (onShared && Pressed(c.PadSwitch))
        {
            Util.TryCall("pad switch", () => GunsMode.SwitchFromWheel(controller));
        }

        if (onShared && Pressed(c.PadHands) && c.PadHands != c.PadSwitch)
        {
            Util.TryCall("pad always in hand", GunsMode.ToggleAlwaysInHands);
        }

        if (!wheelOpen && Pressed(c.PadReload) && DateTime.UtcNow - quickReloadAt > QuickReloadWindow && (!c.PadReloadNeedsAim || GunsAim.IsAimed) && Guns.HeldBy(out RPawnPlayer? holder, out _) && holder != null)
        {
            ModLog.Info("Pad: " + ButtonName(c.PadReload) + " pressed with the guns " + (GunsAim.IsAimed ? "aimed" : "held") + "; reloading.");
            Util.TryCall("pad reload", () => GunsHud.ManualReload(holder));
        }
    }

    public static bool Diag = false;

    private static readonly TimeSpan QuickReloadWindow = TimeSpan.FromSeconds(0.3);
    private static DateTime quickReloadAt = DateTime.MinValue;

    private static readonly TimeSpan HeldGrace = TimeSpan.FromSeconds(0.5);
    private static DateTime gunsHeldAt = DateTime.MinValue;

    private static DateTime reloadPressedAt = DateTime.MinValue;

    public static bool BlockGameQuickfire(string function, RInventoryGadget? gadget)
    {
        try
        {
            if (DateTime.UtcNow - reloadPressedAt < TimeSpan.FromSeconds(1.5))
            {
                ModLog.Info("Pad: the game called " + function + (gadget != null ? " " + Util.ClassName(gadget) : "") + " just after " + ButtonName(GunsConfig.C.PadReload) + ".");
            }

            GunsConfig.Settings c = GunsConfig.C;

            if ((gadget != null && Guns.IsGun(gadget)) || !c.PadEnabled || c.PadReload == 0 || padIndex < 0 || DateTime.UtcNow - gunsHeldAt > HeldGrace || !Read(padIndex, out XInputState state))
            {
                return false;
            }

            XInputGamepad pad = state.Gamepad;

            if ((pad.Buttons & c.PadReload) == 0 || (c.PadReloadNeedsAim && !GunsAim.IsAimed && pad.LeftTrigger <= TriggerNoise))
            {
                return false;
            }

            ReloadInstead(function + (gadget != null ? " " + Util.ClassName(gadget) : ""));

            return true;
        }
        catch (Exception exception)
        {
            ModLog.Once("pad-block", "Pad: checking the reload chord failed: " + Util.Unwrap(exception));

            return false;
        }
    }

    private static void ReloadInstead(string function)
    {
        ModLog.Info("Pad: blocked the game's " + function + " (LT+" + ButtonName(GunsConfig.C.PadReload) + " quickfire) while the guns are aimed.");

        if (DateTime.UtcNow - quickReloadAt <= QuickReloadWindow)
        {
            return;
        }

        quickReloadAt = DateTime.UtcNow;

        if (Guns.HeldBy(out RPawnPlayer? holder, out _) && holder != null)
        {
            ModLog.Info("Pad: reloading instead.");
            Util.TryCall("pad reload", () => GunsHud.ManualReload(holder));
        }
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.FireQuickGadgetType))]
    private static void FireQuickGadgetTypeRedirect(RPlayerController self, RPawnPlayer.EQuickGadgetType type, bool overrideCombatMove)
    {
        if (!BatmanArsenalPack.Arsenal.Guns)
        {
            self.FireQuickGadgetType(type, overrideCombatMove);
            return;
        }

        if (!BlockGameQuickfire("FireQuickGadgetType(" + type + ")", null))
        {
            self.FireQuickGadgetType(type, overrideCombatMove);
        }
    }

    private static string lastDiag = "";
    private static ushort lastDiagButtons;

    private static void Diagnose(RPlayerController controller, RGFxMovieRadialGadgetSelect? radial)
    {
        if (buttons != lastDiagButtons)
        {
            ModLog.Info("Pad: buttons 0x" + buttons.ToString("X4") + ".");
            lastDiagButtons = buttons;
        }

        if (radial == null || !radial.IsValid)
        {
            return;
        }

        string input = controller.PlayerInput is RPlayerInput pi ? pi.bGadgetSelectWheel.ToString() : "?";
        string state = "open=" + radial.bWheelOpen + " moving=" + radial.bIsMoving + " sel=" + radial.SelectedGadgetIndex + " inputWheel=" + input +
            " time=" + (radial.TimeInMenu > 0f ? "yes" : "0") + " queued=" + radial.PostCloseGadgetActionQueued;

        if (state != lastDiag)
        {
            ModLog.Info("Pad: wheel " + state + ".");
            lastDiag = state;
        }
    }

    public static void DrawWheelHint(Canvas? c)
    {
        if (!WheelHint || c == null || !GunsMode.LoadResources())
        {
            return;
        }

        GunsConfig.Settings settings = GunsConfig.C;
        Font? oldFont = c.Font;

        if (GunsMode.HudFont != null)
        {
            c.Font = GunsMode.HudFont;
        }

        try
        {
            float s = Math.Clamp(c.ClipY / 1080f, 0.75f, 2.5f);
            float scale = 1.45f * s;
            string first = GunsMode.GunsSelected ? "SWITCH TO " + Guns.ReplacedTitle : "SWITCH TO RED HOOD'S GUNS";
            string second = settings.PadHands != 0 && settings.PadHands != settings.PadSwitch
                ? (GunsConfig.C.AlwaysInHands ? "HOLSTER GUNS" : "GUNS ALWAYS IN HAND")
                : "";

            c.TextSize(first, out float firstW, out float lineH, scale, scale);
            c.TextSize(second, out float secondW, out _, scale, scale);

            float badge = lineH + 6f * s;
            float badgeGap = 10f * s;
            float gap = 44f * s;
            float padX = 20f * s;
            float padY = 10f * s;
            float firstBlock = badge + badgeGap + firstW;
            float secondBlock = second.Length > 0 ? badge + badgeGap + secondW : 0f;
            float w = firstBlock + (second.Length > 0 ? gap + secondBlock : 0f) + padX * 2f;
            float h = badge + padY * 2f;
            float x = (c.ClipX - w) * 0.5f;
            float y = c.ClipY * 0.835f;

            GunsMode.Rect(c, x, y, w, h, 10, 8, 10, 0.8f);
            GunsMode.Rect(c, x, y, w, 3f * s, 225, 45, 40, 1f);

            float cx = x + padX;

            DrawEntry(c, ref cx, y + padY, badge, settings.PadSwitch, first, scale, s);

            if (second.Length > 0)
            {
                cx += gap;
                DrawEntry(c, ref cx, y + padY, badge, settings.PadHands, second, scale, s);
            }
        }
        finally
        {
            c.Font = oldFont;
        }
    }

    private static (byte r, byte g, byte b) ButtonColor(ushort mask) => mask switch
    {
        A => (90, 185, 60),
        B => (215, 50, 45),
        X => (40, 125, 230),
        Y => (235, 185, 30),
        _ => (200, 200, 205),
    };

    private static void DrawEntry(Canvas c, ref float x, float y, float badge, ushort button, string label, float scale, float s)
    {
        (byte r, byte g, byte b) color = ButtonColor(button);
        string name = ButtonName(button);

        c.TextSize(name, out float nameW, out float nameH, scale, scale);
        c.TextSize(label, out float labelW, out float labelH, scale, scale);

        float badgeW = Math.Max(badge, nameW + 12f * s);

        GunsMode.Rect(c, x, y, badgeW, badge, color.r, color.g, color.b, 1f);
        GunsMode.Text(c, x + (badgeW - nameW) * 0.5f, y + (badge - nameH) * 0.5f, name, 15, 12, 15, 1f, scale);

        x += badgeW + 10f * s;
        GunsMode.Text(c, x, y + (badge - labelH) * 0.5f, label, 245, 240, 240, 1f, scale);
        x += labelW;
    }
}
