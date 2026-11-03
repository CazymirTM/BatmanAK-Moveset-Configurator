using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace CeZeY.OracleScript;

public partial class Oracle
{
    private const ushort PadUp = 0x0001;
    private const ushort PadDown = 0x0002;
    private const ushort PadLeft = 0x0004;
    private const ushort PadRight = 0x0008;
    private const ushort PadStart = 0x0010;
    private const ushort PadBack = 0x0020;
    private const ushort PadLS = 0x0040;
    private const ushort PadRS = 0x0080;
    private const ushort PadLB = 0x0100;
    private const ushort PadRB = 0x0200;
    private const ushort PadA = 0x1000;
    private const ushort PadB = 0x2000;
    private const ushort PadX = 0x4000;
    private const ushort PadY = 0x8000;

    private const short PadStickPush = 16000;
    private const short PadStickNoise = 9000;
    private const byte PadTriggerNoise = 40;
    private const double PadScanGap = 2.0;
    private const int PadRescanFrames = 30;

    private static readonly Dictionary<string, ushort> PadNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = PadA, ["b"] = PadB, ["x"] = PadX, ["y"] = PadY,
        ["lb"] = PadLB, ["rb"] = PadRB, ["ls"] = PadLS, ["rs"] = PadRS, ["l3"] = PadLS, ["r3"] = PadRS,
        ["back"] = PadBack, ["view"] = PadBack, ["select"] = PadBack, ["start"] = PadStart, ["menu"] = PadStart,
        ["up"] = PadUp, ["down"] = PadDown, ["left"] = PadLeft, ["right"] = PadRight
    };

    private static readonly ushort[] PadDirs = { PadUp, PadDown, PadLeft, PadRight };
    private static readonly Keys[] PadDirKeys = { Keys.Up, Keys.Down, Keys.Left, Keys.Right };

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
    private static extern int XInputGetState14(int userIndex, out XInputState state);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState910(int userIndex, out XInputState state);

    private static bool _padEnabled = true;
    private static ushort _padOpenMask = PadBack;
    private static string _padOpenName = "BACK";
    private static float _padOpenHold = 0.5f;

    private static bool _padLegacy;
    private static bool _padMissing;
    private static int _padIndex = -1;
    private static long _padNextScan;
    private static uint _padPacket;
    private static ushort _padNow;
    private static ushort _padPrev;
    private static long _padFrameTick;
    private static long _padUsedAt;
    private static long _keyUsedAt;

    private static float _padHeld = -1f;
    private static bool _padWaitRelease;
    private static bool _padMapDeferred;
    private static bool _padReplayMap;
    private static bool _padReplaying;
    private static bool _padEnterCar;
    private static readonly float[] _padRepeat = { -1f, -1f, -1f, -1f };

    private static nint _padBlockedPi;
    private static nint _padRepairedPi;
    private static int _padBlockFrames;
    private static bool _padRestorePending;

    private static bool PadConnected => _padIndex >= 0;

    private static bool UsingPad => PadConnected && _padUsedAt > _keyUsedAt;

    private static void KeyboardUsed() => _keyUsedAt = Stopwatch.GetTimestamp();

    private static bool PadHeld(ushort mask) => mask != 0 && (_padNow & mask) == mask;

    private static bool PadPressed(ushort mask) => mask != 0 && (_padNow & mask) == mask && (_padPrev & mask) != mask;

    private static bool TryParsePad(string text, out ushort mask, out string name)
    {
        mask = 0;
        var parts = new List<string>();
        foreach (string raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!PadNames.TryGetValue(raw, out ushort bit))
            {
                name = "";
                return false;
            }

            mask |= bit;
            parts.Add(raw.ToUpperInvariant());
        }

        name = string.Join(" + ", parts);
        return mask != 0;
    }

    private static void ReadPadConfig(Tomlyn.Model.TomlTable pad)
    {
        _padEnabled = ReadBool(pad, "enabled", true);
        _padOpenHold = Math.Clamp(ReadFloat(pad, "open_hold", 0.5f), 0f, 3f);
        string open = ReadString(pad, "open_button", "back");
        if (TryParsePad(open, out ushort mask, out string name))
        {
            _padOpenMask = mask;
            _padOpenName = name;
        }
        else
        {
            _padOpenMask = PadBack;
            _padOpenName = "BACK";
            Log("[gamepad] open_button \"" + open + "\" is not a button name, using back.");
        }
    }

    private static void EnsurePadSection()
    {
        try
        {
            string path = System.IO.Path.Combine(_modDir, "mod.toml");
            if (!System.IO.File.Exists(path))
                return;

            foreach (string line in System.IO.File.ReadLines(path))
            {
                if (line.Trim().StartsWith("[gamepad]", StringComparison.Ordinal))
                    return;
            }

            var lines = new List<string>(System.IO.File.ReadAllLines(path));
            int at = lines.FindIndex(l => l.Trim().StartsWith("[cheats]", StringComparison.Ordinal));
            if (at < 0)
                at = lines.Count;

            lines.InsertRange(at, new[]
            {
                "[gamepad]",
                "# true = an Xbox (XInput) controller can open and use the Oracle menu",
                "enabled = true",
                "",
                "# button to hold to open or close the menu: back (View), start, a, b, x, y, lb, rb, ls, rs, up, down, left, right",
                "# combine buttons with +, for example \"lb+rb\". With back, a quick tap still opens the map.",
                "open_button = \"back\"",
                "",
                "# seconds to hold it (0 = open on press)",
                "open_hold = 0.5",
                ""
            });
            System.IO.File.WriteAllLines(path, lines);
            _configStamp = DateTime.MinValue;
            Log("Added the [gamepad] section to mod.toml.");
        }
        catch (Exception ex)
        {
            Log("Could not add [gamepad] to mod.toml: " + ex.Message);
        }
    }

    private static bool ReadPad(int index, out XInputState state)
    {
        state = default;
        if (_padMissing)
            return false;

        try
        {
            return (_padLegacy ? XInputGetState910(index, out state) : XInputGetState14(index, out state)) == 0;
        }
        catch (DllNotFoundException) when (!_padLegacy)
        {
            _padLegacy = true;
            Log("Pad: xinput1_4.dll not found, using xinput9_1_0.dll.");
            return ReadPad(index, out state);
        }
        catch (Exception ex)
        {
            _padMissing = true;
            Log("Pad: XInput is not available, controller support is off: " + ex.Message);
            return false;
        }
    }

    private static void PollPad()
    {
        _padPrev = _padNow;

        if (_padIndex >= 0)
        {
            if (ReadPad(_padIndex, out XInputState state))
            {
                TakePad(state);
                return;
            }

            Log("Pad: controller " + _padIndex + " disconnected.");
            _padIndex = -1;
            _padNow = 0;
            _padNextScan = 0;
        }

        long now = Stopwatch.GetTimestamp();
        if (now < _padNextScan)
            return;

        _padNextScan = now + (long)(PadScanGap * Stopwatch.Frequency);
        for (int i = 0; i < 4; i++)
        {
            if (!ReadPad(i, out XInputState state))
                continue;

            _padIndex = i;
            _padPacket = state.PacketNumber;
            _padNow = state.Gamepad.Buttons;
            _padPrev = _padNow;
            Log("Pad: controller " + i + " connected.");
            return;
        }
    }

    private static void TakePad(XInputState state)
    {
        XInputGamepad g = state.Gamepad;
        ushort b = g.Buttons;
        if (g.ThumbLY > PadStickPush)
            b |= PadUp;
        else if (g.ThumbLY < -PadStickPush)
            b |= PadDown;
        if (g.ThumbLX < -PadStickPush)
            b |= PadLeft;
        else if (g.ThumbLX > PadStickPush)
            b |= PadRight;
        _padNow = b;

        if (state.PacketNumber == _padPacket)
            return;

        _padPacket = state.PacketNumber;
        if (g.Buttons != 0 || g.LeftTrigger > PadTriggerNoise || g.RightTrigger > PadTriggerNoise ||
            Math.Abs((int)g.ThumbLX) > PadStickNoise || Math.Abs((int)g.ThumbLY) > PadStickNoise ||
            Math.Abs((int)g.ThumbRX) > PadStickNoise || Math.Abs((int)g.ThumbRY) > PadStickNoise)
            _padUsedAt = Stopwatch.GetTimestamp();
    }

    private static void PadFrame()
    {
        long now = Stopwatch.GetTimestamp();
        float dt = _padFrameTick == 0 ? 0f : Math.Clamp((float)Stopwatch.GetElapsedTime(_padFrameTick, now).TotalSeconds, 0f, 0.1f);
        _padFrameTick = now;

        if (!_padEnabled)
        {
            if (_padBlockedPi != 0 || _padRestorePending)
                RestorePadBindings("controller support off");
            _padNow = 0;
            _padPrev = 0;
            return;
        }

        PollPad();
        if (!PadConnected)
        {
            _padHeld = -1f;
            _padMapDeferred = false;
            if (_padBlockedPi != 0 || _padRestorePending)
                RestorePadBindings("controller disconnected");
            return;
        }

        if (!_menuOpen)
        {
            if (_padRestorePending && (_padNow & ~PadStart) == 0)
                RestorePadBindings("menu closed");
            RepairPadBindings();
            PadClosedFrame(dt);
            return;
        }

        BlockPadBindings();
        PadMenuFrame(dt);
    }

    private static void PadClosedFrame(float dt)
    {
        Array.Fill(_padRepeat, -1f);

        if (PadPressed(PadLB) && (_padOpenMask & PadLB) == 0 && PlayingOther && !CharacterBusy)
            _padEnterCar = true;

        if (_padWaitRelease)
        {
            if (!PadHeld(_padOpenMask))
                _padWaitRelease = false;
            _padHeld = -1f;
            return;
        }

        if (!PadHeld(_padOpenMask))
        {
            if (_padMapDeferred)
            {
                _padMapDeferred = false;
                _padReplayMap = true;
            }

            _padHeld = -1f;
            return;
        }

        if (_padHeld < 0f)
        {
            if (!PadPressed(_padOpenMask) && !_padMapDeferred)
                return;
            _padHeld = 0f;
        }

        _padHeld += dt;
        if (_padHeld < _padOpenHold)
            return;

        _padHeld = -1f;
        _padWaitRelease = true;
        bool deferred = _padMapDeferred;
        _padMapDeferred = false;

        if (GamePaused() || !InGame() || TeleportBusy || CharacterBusy)
        {
            if (deferred)
                _padReplayMap = true;
            return;
        }

        _padUsedAt = Stopwatch.GetTimestamp();
        Log("Pad: menu opened with " + _padOpenName + ".");
        OpenMenu();
    }

    private static void PadMenuFrame(float dt)
    {
        _padMapDeferred = false;

        if (_padWaitRelease)
        {
            if (!PadHeld(_padOpenMask))
                _padWaitRelease = false;
        }
        else if (PadPressed(_padOpenMask))
        {
            _padWaitRelease = true;
            CloseMenu();
            return;
        }

        if (PadPressed(PadA))
            HandleMenuKey(Keys.Enter, false);
        else if (PadPressed(PadB))
            HandleMenuKey(Keys.Back, false);
        else if (PadPressed(PadLB))
            HandleMenuKey(Keys.PageUp, false);
        else if (PadPressed(PadRB))
            HandleMenuKey(Keys.PageDown, false);
        else if (_confirmRow != null && (PadPressed(PadX) || PadPressed(PadY)))
            HandleMenuKey(Keys.Escape, false);

        if (!_menuOpen)
            return;

        for (int i = 0; i < PadDirs.Length; i++)
        {
            if (!PadHeld(PadDirs[i]) || (_padOpenMask & PadDirs[i]) != 0 && _padWaitRelease)
            {
                _padRepeat[i] = -1f;
                continue;
            }

            if (_padRepeat[i] < 0f)
            {
                _padRepeat[i] = 0f;
                HandleMenuKey(PadDirKeys[i], false);
                continue;
            }

            _padRepeat[i] += dt;
            if (_padRepeat[i] >= RepeatDelay)
            {
                _padRepeat[i] -= RepeatRate;
                HandleMenuKey(PadDirKeys[i], true);
            }

            if (!_menuOpen)
                return;
        }
    }

    private static bool PadDefersMap()
    {
        if (_padReplaying || !_padEnabled || _padOpenMask != PadBack || _padIndex < 0 || _menuOpen)
            return false;

        if (!ReadPad(_padIndex, out XInputState state) || (state.Gamepad.Buttons & PadBack) == 0)
            return false;

        _padMapDeferred = true;
        return true;
    }

    private static void PadGameTick()
    {
        if (_padEnterCar)
        {
            _padEnterCar = false;
            if (!_menuOpen && !GamePaused() && EnterBatmobileNearby())
                Log("Pad: entered the Batmobile with LB.");
        }

        if (!_padReplayMap)
            return;

        _padReplayMap = false;
        RPlayerController pc = Game.GetPlayerController(0);
        if (pc == null || !pc.IsValid || _menuOpen || GamePaused())
            return;

        _padReplaying = true;
        try
        {
            pc.OpenLevelUpOrMap();
        }
        finally
        {
            _padReplaying = false;
        }
    }

    private static bool IsPadBind(string name)
    {
        return (name.StartsWith("XboxTypeS_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Gamepad_", StringComparison.OrdinalIgnoreCase)) &&
               !name.Equals("XboxTypeS_Start", StringComparison.OrdinalIgnoreCase);
    }

    private static PlayerInput? CurrentPlayerInput()
    {
        RPlayerController pc = Game.GetPlayerController(0);
        if (pc == null || !pc.IsValid)
            return null;

        PlayerInput pi = pc.PlayerInput;
        return pi != null && pi.IsValid ? pi : null;
    }

    private static void BlockPadBindings()
    {
        try
        {
            PlayerInput? pi = CurrentPlayerInput();
            if (pi == null)
                return;

            if (pi.Ptr == _padBlockedPi && ++_padBlockFrames < PadRescanFrames)
                return;

            _padBlockFrames = 0;
            _padRestorePending = false;
            int blocked = 0;
            Span<Input.FKeyBind> binds = pi.Bindings.AsSpan();
            for (int i = 0; i < binds.Length; i++)
            {
                ref Input.FKeyBind b = ref binds[i];
                if ((b.Control && b.Shift && b.Alt) || !IsPadBind(b.Name.ToString() ?? ""))
                    continue;

                b.Control = true;
                b.Shift = true;
                b.Alt = true;
                blocked++;
            }

            if (pi.Ptr != _padBlockedPi || blocked > 0)
                Log("Pad: game controller buttons paused while the menu is open (" + blocked + " bindings).");
            _padBlockedPi = pi.Ptr;
        }
        catch (Exception ex)
        {
            LogThrottled("Pad: could not pause the game's controller buttons: " + ex.Message);
        }
    }

    private static void PadMenuClosed()
    {
        Array.Fill(_padRepeat, -1f);
        if (_padBlockedPi == 0)
            return;

        if (PadConnected && (_padNow & ~PadStart) != 0)
            _padRestorePending = true;
        else
            RestorePadBindings("menu closed");
    }

    private static void RestorePadBindings(string why)
    {
        _padRestorePending = false;
        _padBlockedPi = 0;
        _padBlockFrames = 0;

        try
        {
            PlayerInput? pi = CurrentPlayerInput();
            if (pi == null)
                return;

            int restored = UnmarkPadBindings(pi);
            _padRepairedPi = pi.Ptr;
            if (restored > 0)
                Log("Pad: game controller buttons back on (" + why + ", " + restored + " bindings).");
        }
        catch (Exception ex)
        {
            Log("Pad: could not give the controller buttons back to the game: " + ex.Message);
        }
    }

    private static void RepairPadBindings()
    {
        if (_padBlockedPi != 0 || _padRestorePending)
            return;

        try
        {
            PlayerInput? pi = CurrentPlayerInput();
            if (pi == null || pi.Ptr == _padRepairedPi)
                return;

            _padRepairedPi = pi.Ptr;
            int restored = UnmarkPadBindings(pi);
            if (restored > 0)
                Log("Pad: repaired " + restored + " controller bindings left paused by an earlier session.");
        }
        catch
        {
        }
    }

    private static int UnmarkPadBindings(PlayerInput pi)
    {
        int restored = 0;
        Span<Input.FKeyBind> binds = pi.Bindings.AsSpan();
        for (int i = 0; i < binds.Length; i++)
        {
            ref Input.FKeyBind b = ref binds[i];
            if (!(b.Control && b.Shift && b.Alt) || !IsPadBind(b.Name.ToString() ?? ""))
                continue;

            b.Control = false;
            b.Shift = false;
            b.Alt = false;
            restored++;
        }

        return restored;
    }
}
