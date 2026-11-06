#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using Canvas = BmSDK.Engine.Canvas;
using Hud = BatmanRedHoodGuns.GunsMode;

namespace BatmanRobinStaff;

internal static class StaffPad
{
    private static readonly Dictionary<string, ushort> Names = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = 0x1000, ["b"] = 0x2000, ["x"] = 0x4000, ["y"] = 0x8000,
        ["lb"] = 0x0100, ["rb"] = 0x0200, ["ls"] = 0x0040, ["rs"] = 0x0080, ["back"] = 0x0020, ["start"] = 0x0010,
        ["up"] = 0x0001, ["down"] = 0x0002, ["left"] = 0x0004, ["right"] = 0x0008,
    };

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

    private static bool legacy;
    private static bool unavailable;
    private static int padIndex = -1;
    private static DateTime nextScan = DateTime.MinValue;
    private static uint lastPacket;
    private static ushort buttons, previous;
    private static DateTime lastPadInput = DateTime.MinValue;
    private static DateTime lastKeyInput = DateTime.MinValue;
    private static bool wheelHint;

    public static bool UsingPad => padIndex >= 0 && lastPadInput > lastKeyInput;

    public static string SwitchName => StaffConfig.C.PadSwitch.ToUpperInvariant();

    public static void KeyboardUsed() => lastKeyInput = DateTime.UtcNow;

    private static ushort SwitchMask => Names.TryGetValue(StaffConfig.C.PadSwitch, out ushort mask) ? mask : (ushort)0;

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
            ModLog.Warn("Pad: XInput is not available: " + Util.Unwrap(exception));

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
                buttons = state.Gamepad.Buttons;

                if (state.PacketNumber != lastPacket)
                {
                    lastPacket = state.PacketNumber;

                    if (buttons != 0)
                    {
                        lastPadInput = DateTime.UtcNow;
                    }
                }

                return;
            }

            padIndex = -1;
            buttons = 0;
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

                return;
            }
        }
    }

    public static void Frame()
    {
        wheelHint = false;

        if (Game.GetPlayerController(0) is not RPlayerController controller || !controller.IsValid || Staff.Batman() is not RPawnPlayer pawn)
        {
            return;
        }

        RGFxMovieRadialGadgetSelect? radial = controller.RadialGadgetSelect;
        int slot = Staff.WheelIndex(pawn);
        bool onSlot = radial != null && radial.IsValid && radial.bWheelOpen && slot >= 0 && radial.SelectedGadgetIndex == slot;

        if (radial != null && radial.IsValid)
        {
            StaffIcons.UpdateWheelText(radial, StaffConfig.C.StaffSelected);
        }

        if (!StaffConfig.C.PadEnabled)
        {
            return;
        }

        Poll();

        if (padIndex < 0)
        {
            return;
        }

        wheelHint = onSlot && UsingPad;

        ushort mask = SwitchMask;

        if (onSlot && mask != 0 && (buttons & mask) == mask && (previous & mask) != mask)
        {
            Util.TryCall("pad switch", () => StaffMode.Toggle(controller, equip: false));
        }
    }

    public static void DrawWheelHint(Canvas? c)
    {
        if (!wheelHint || c == null || !Hud.LoadResources() || SwitchMask == 0)
        {
            return;
        }

        Font? oldFont = c.Font;

        if (Hud.HudFont != null)
        {
            c.Font = Hud.HudFont;
        }

        try
        {
            float s = Math.Clamp(c.ClipY / 1080f, 0.75f, 2.5f);
            float scale = 1.45f * s;
            string label = StaffConfig.C.StaffSelected ? "SWITCH TO THE FREEZE BLAST" : "SWITCH TO ROBIN'S STAFF";
            string name = SwitchName;

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

            Hud.Rect(c, x, y, w, h, 10, 8, 10, 0.8f);
            Hud.Rect(c, x, y, w, 3f * s, 70, 200, 90, 1f);
            Hud.Rect(c, x + padX, y + padY, badgeW, badge, 235, 185, 30, 1f);
            Hud.Text(c, x + padX + (badgeW - nameW) * 0.5f, y + padY + (badge - nameH) * 0.5f, name, 15, 12, 15, 1f, scale);
            Hud.Text(c, x + padX + badgeW + 10f * s, y + padY + (badge - lineH) * 0.5f, label, 245, 240, 240, 1f, scale);
        }
        finally
        {
            c.Font = oldFont;
        }
    }
}
