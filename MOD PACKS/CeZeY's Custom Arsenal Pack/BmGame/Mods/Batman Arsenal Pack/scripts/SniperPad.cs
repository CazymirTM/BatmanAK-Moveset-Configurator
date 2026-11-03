#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;
using Canvas = BmSDK.Engine.Canvas;

namespace BatmanDeathstrokeSniper;

internal static class SniperPad
{
    public const ushort LeftShoulder = 0x0100;
    public const ushort RightShoulder = 0x0200;
    public const ushort A = 0x1000;
    public const ushort B = 0x2000;
    public const ushort X = 0x4000;
    public const ushort Y = 0x8000;

    private static readonly Dictionary<string, ushort> Names = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = A, ["b"] = B, ["x"] = X, ["y"] = Y,
        ["lb"] = LeftShoulder, ["rb"] = RightShoulder,
        ["ls"] = 0x0040, ["rs"] = 0x0080, ["back"] = 0x0020, ["start"] = 0x0010,
        ["up"] = 0x0001, ["down"] = 0x0002, ["left"] = 0x0004, ["right"] = 0x0008,
        ["none"] = 0, ["off"] = 0,
    };

    public static bool TryParseButton(string text, out ushort mask) => Names.TryGetValue(text.Trim(), out mask);

    public static string ButtonName(ushort mask)
    {
        foreach (KeyValuePair<string, ushort> pair in Names)
        {
            if (pair.Value == mask && mask != 0)
            {
                return pair.Key.ToUpperInvariant();
            }
        }

        return "NONE";
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

    private const short StickNoise = 9000;
    private const byte TriggerNoise = 40;

    private static bool legacy;
    private static bool unavailable;
    private static int padIndex = -1;
    private static DateTime nextScan = DateTime.MinValue;
    private static uint lastPacket;
    private static ushort buttons, previous;
    private static byte leftTrigger;
    private static DateTime lastPadInput = DateTime.MinValue;
    private static DateTime lastKeyInput = DateTime.MinValue;
    private static DateTime sniperHeldAt = DateTime.MinValue;
    private static DateTime reloadAt = DateTime.MinValue;

    public static bool Connected => padIndex >= 0;

    public static bool UsingPad => Connected && lastPadInput > lastKeyInput;

    public static bool WheelHint { get; private set; }

    public static void KeyboardUsed() => lastKeyInput = DateTime.UtcNow;

    private static bool Pressed(ushort mask) => mask != 0 && (buttons & mask) == mask && (previous & mask) != mask;

    private static bool Read(int index, out XInputState state)
    {
        state = default;

        if (unavailable)
        {
            return false;
        }

        try
        {
            return (legacy ? GetState910(index, out state) : GetState14(index, out state)) == 0;
        }
        catch (DllNotFoundException) when (!legacy)
        {
            legacy = true;

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

            padIndex = -1;
            buttons = 0;
            leftTrigger = 0;
            nextScan = DateTime.MinValue;
        }

        if (DateTime.UtcNow < nextScan)
        {
            return;
        }

        nextScan = DateTime.UtcNow + TimeSpan.FromSeconds(2);

        for (int index = 0; index < 4; index++)
        {
            if (Read(index, out XInputState state))
            {
                padIndex = index;
                lastPacket = state.PacketNumber;
                buttons = previous = state.Gamepad.Buttons;
                ModLog.Info("Pad: controller " + index + " connected.");

                return;
            }
        }
    }

    private static void Take(XInputState state)
    {
        XInputGamepad pad = state.Gamepad;

        buttons = pad.Buttons;
        leftTrigger = pad.LeftTrigger;

        if (state.PacketNumber == lastPacket)
        {
            return;
        }

        lastPacket = state.PacketNumber;

        if (buttons != 0 || pad.LeftTrigger > TriggerNoise || pad.RightTrigger > TriggerNoise ||
            Math.Abs((int)pad.ThumbLX) > StickNoise || Math.Abs((int)pad.ThumbLY) > StickNoise ||
            Math.Abs((int)pad.ThumbRX) > StickNoise || Math.Abs((int)pad.ThumbRY) > StickNoise)
        {
            lastPadInput = DateTime.UtcNow;
        }
    }

    public static void Frame()
    {
        SniperConfig.Settings c = SniperConfig.C;

        if (!c.PadEnabled || Game.GetPlayerController(0) is not RPlayerController controller || !controller.IsValid || Sniper.Batman() is not RPawnPlayer pawn)
        {
            WheelHint = false;

            return;
        }

        bool held = Sniper.Selected && Sniper.Held(out _, out RDisruptorSniper? dis) && dis != null;

        if (held)
        {
            sniperHeldAt = DateTime.UtcNow;
        }

        Poll();

        if (!Connected)
        {
            WheelHint = false;

            return;
        }

        RGFxMovieRadialGadgetSelect? radial = controller.RadialGadgetSelect;
        bool wheelOpen = radial != null && radial.IsValid && radial.bWheelOpen;
        int slot = Sniper.WheelIndex(pawn);
        bool onSlot = wheelOpen && slot >= 0 && radial!.SelectedGadgetIndex == slot;

        WheelHint = onSlot && UsingPad;

        if (onSlot && Pressed(c.PadSwitch))
        {
            Util.TryCall("pad switch", () => SniperMode.Toggle(controller, equip: false));
        }

        if (!wheelOpen && held && Pressed(c.PadReload) && leftTrigger > TriggerNoise && DateTime.UtcNow - reloadAt > TimeSpan.FromSeconds(0.3))
        {
            reloadAt = DateTime.UtcNow;
            Sniper.Held(out _, out RDisruptorSniper? d);
            Util.TryCall("pad reload", () => SniperAmmo.StartReload(pawn, d, "LT+" + ButtonName(c.PadReload)));
        }
    }

    public static bool BlockGameQuickfire()
    {
        try
        {
            SniperConfig.Settings c = SniperConfig.C;

            if (!c.PadEnabled || c.PadReload == 0 || padIndex < 0 || !Sniper.Selected || DateTime.UtcNow - sniperHeldAt > TimeSpan.FromSeconds(0.5) || !Read(padIndex, out XInputState state))
            {
                return false;
            }

            if ((state.Gamepad.Buttons & c.PadReload) == 0 || state.Gamepad.LeftTrigger <= TriggerNoise)
            {
                return false;
            }

            ModLog.Once("pad-block", "Pad: LT+" + ButtonName(c.PadReload) + " reloads the sniper rifle instead of the game's quickfire.");

            if (DateTime.UtcNow - reloadAt > TimeSpan.FromSeconds(0.3) && Sniper.Held(out RPawnPlayer? holder, out RDisruptorSniper? dis) && holder != null)
            {
                reloadAt = DateTime.UtcNow;
                SniperAmmo.StartReload(holder, dis, "LT+" + ButtonName(c.PadReload));
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void DrawWheelHint(Canvas? c)
    {
        if (!WheelHint || c == null || !SniperMode.LoadResources() || SniperConfig.C.PadSwitch == 0)
        {
            return;
        }

        Font? oldFont = c.Font;

        if (SniperMode.HudFont != null)
        {
            c.Font = SniperMode.HudFont;
        }

        try
        {
            float s = Math.Clamp(c.ClipY / 1080f, 0.75f, 2.5f);
            float scale = 1.45f * s;
            string label = Sniper.Selected ? "SWITCH TO THE DISRUPTOR" : "SWITCH TO THE SNIPER RIFLE";
            string name = ButtonName(SniperConfig.C.PadSwitch);

            c.TextSize(label, out float labelW, out float lineH, scale, scale);
            c.TextSize(name, out float nameW, out float nameH, scale, scale);

            float badge = lineH + 6f * s;
            float badgeW = Math.Max(badge, nameW + 12f * s);
            float padX = 20f * s;
            float padY = 10f * s;
            float w = badgeW + 10f * s + labelW + padX * 2f;
            float h = badge + padY * 2f;
            float x = (c.ClipX - w) * 0.5f;
            float y = c.ClipY * 0.835f;

            SniperMode.Rect(c, x, y, w, h, 10, 8, 10, 0.8f);
            SniperMode.Rect(c, x, y, w, 3f * s, 240, 125, 25, 1f);
            SniperMode.Rect(c, x + padX, y + padY, badgeW, badge, 235, 185, 30, 1f);
            SniperMode.Text(c, x + padX + (badgeW - nameW) * 0.5f, y + padY + (badge - nameH) * 0.5f, name, 15, 12, 15, 1f, scale);
            SniperMode.Text(c, x + padX + badgeW + 10f * s, y + padY + (badge - lineH) * 0.5f, label, 245, 240, 240, 1f, scale);
        }
        finally
        {
            c.Font = oldFont;
        }
    }
}
