using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BmSDK;
using BmSDK.Framework;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace CeZeY.OracleScript;

public partial class Oracle
{
    private const double FallbackAfter = 0.5;

    private static long _sdkTick = Stopwatch.GetTimestamp();
    private static bool _fallbackLogged;
    private static readonly bool[] _fallbackDown = new bool[256];
    private static readonly List<Keys> _fallbackKeys = new();
    private static readonly Keys[] MenuKeys = { Keys.Up, Keys.Down, Keys.Left, Keys.Right, Keys.Enter, Keys.Tab, Keys.PageUp, Keys.PageDown, Keys.Back };

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    private static bool FallbackActive => Stopwatch.GetElapsedTime(_sdkTick).TotalSeconds > FallbackAfter;

    private static bool GameInFront()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    private static void FallbackTick()
    {
        if (!FallbackActive || GamePaused())
            return;

        if (!_fallbackLogged)
        {
            _fallbackLogged = true;
            Log("The game stopped sending ticks to mods here, Oracle now reads the keyboard itself.");
        }

        _fallbackKeys.Clear();
        _fallbackKeys.Add(_menuKey);
        _fallbackKeys.AddRange(MenuKeys);
        _fallbackKeys.AddRange(_hotkeys.Keys);
        if (PlayingOther)
            _fallbackKeys.Add(Keys.E);

        bool front = GameInFront();
        foreach (Keys k in _fallbackKeys)
        {
            int vk = (int)k & 0xFF;
            bool down = front && (GetAsyncKeyState(vk) & 0x8000) != 0;
            if (down && !_fallbackDown[vk])
                HandleKey(k);
            _fallbackDown[vk] = down;
        }

        TickAll();
    }
}
