#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace BatmanRobinStaff;

internal static class ModLog
{
    private static readonly HashSet<string> loggedOnce = new HashSet<string>(StringComparer.Ordinal);

    public static void Info(string message) { try { Debug.Log("[BatmanStaff] " + message); } catch { } }

    public static void Warn(string message) { try { Debug.LogWarning("[BatmanStaff] " + message); } catch { } }

    public static void Once(string key, string message)
    {
        if (loggedOnce.Add(key))
        {
            Info(message);
        }
    }
}

internal static class Util
{
    public static string ClassName(GameObject? gameObject)
    {
        try
        {
            if (gameObject != null && gameObject.IsValid && gameObject.Class != null && gameObject.Class.IsValid)
            {
                return gameObject.Class.Name.ToString();
            }
        }
        catch
        {
        }

        return "<unknown>";
    }

    public static string Unwrap(Exception exception)
    {
        while (exception.InnerException != null)
        {
            exception = exception.InnerException;
        }

        return exception.GetType().Name + ": " + exception.Message;
    }

    public static void TryCall(string label, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            ModLog.Warn(label + " failed: " + Unwrap(exception));
        }
    }

    public static bool Same(GameObject? a, GameObject? b) => a != null && b != null && a.IsValid && b.IsValid && a.Ptr == b.Ptr;

    public static int IndexOf(TArray<FName> names, string name)
    {
        for (int i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i].ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}

internal static class Staff
{
    public const string FreezeClass = "RFreezeSprayBm";

    public static RPawnPlayer? Batman()
    {
        RPawnPlayer? pawn = Game.GetPlayerController(0)?.Pawn as RPawnPlayer;

        return pawn != null && pawn.IsValid && IsBatman(pawn) ? pawn : null;
    }

    public static bool IsBatman(RPawnPlayer pawn) => Util.ClassName(pawn).StartsWith("RPawnPlayerBm", StringComparison.Ordinal);

    public static RFreezeSpray? Freeze(Pawn? pawn)
    {
        if (pawn == null || !pawn.IsValid)
        {
            return null;
        }

        for (Inventory? inv = pawn.InvManager?.InventoryChain; inv != null; inv = inv._Inventory)
        {
            if (inv is RFreezeSpray f && f.IsValid)
            {
                return f;
            }
        }

        return null;
    }

    public static int WheelIndex(RPawnPlayer? pawn)
    {
        if (pawn?.InvManager is not RInventoryManager inventory || !inventory.IsValid)
        {
            return -1;
        }

        RFreezeSpray? f = Freeze(pawn);

        return Util.IndexOf(inventory.BM2SelectableGadgets, f != null ? Util.ClassName(f) : FreezeClass);
    }
}

internal static class StaffConfig
{
    internal sealed class Settings
    {
        public bool StaffSelected;
        public float SwitchHold = 1f;
        public bool PadEnabled = true;
        public string PadSwitch = "y";
    }

    public static Settings C = new Settings();
    public static string Dir = "";

    private static string path = "";
    private static DateTime lastWrite;
    private static DateTime nextCheck = DateTime.MinValue;

    public static void Init(string dir)
    {
        Dir = dir;
        path = Path.Combine(dir, "BatmanStaff.ini");

        if (!File.Exists(path))
        {
            Save();
        }

        Load();
    }

    public static void Tick()
    {
        DateTime now = DateTime.UtcNow;

        if (now < nextCheck || path.Length == 0)
        {
            return;
        }

        nextCheck = now + TimeSpan.FromSeconds(1);

        try
        {
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) != lastWrite)
            {
                Load();
            }
        }
        catch
        {
        }
    }

    public static void SetSelected(bool on)
    {
        C.StaffSelected = on;
        Save();
    }

    private static void Load()
    {
        Settings s = new Settings();

        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');

            if (line.StartsWith(";") || eq <= 0)
            {
                continue;
            }

            string key = line.Substring(0, eq).Trim().ToLowerInvariant();
            string value = line.Substring(eq + 1).Trim();

            switch (key)
            {
                case "selected":
                    s.StaffSelected = string.Equals(value, "staff", StringComparison.OrdinalIgnoreCase);
                    break;
                case "switch_hold":
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float hold))
                    {
                        s.SwitchHold = Math.Clamp(hold, 0.2f, 5f);
                    }

                    break;
                case "gamepad":
                    s.PadEnabled = value != "0";
                    break;
                case "pad_switch":
                    s.PadSwitch = value;
                    break;
            }
        }

        C = s;
        lastWrite = File.GetLastWriteTimeUtc(path);
    }

    private static void Save()
    {
        try
        {
            File.WriteAllText(path,
                "; Batman Arsenal Pack - Robin's staff settings\n" +
                "; selected: what the Freeze Blast slot holds (freeze or staff)\n" +
                "selected = " + (C.StaffSelected ? "staff" : "freeze") + "\n" +
                "; switch_hold: seconds to hold the Freeze Blast key to swap between the two\n" +
                "switch_hold = " + C.SwitchHold.ToString("0.##", CultureInfo.InvariantCulture) + "\n" +
                "; gamepad: 1 = press pad_switch on the gadget wheel, with the Freeze Blast slot highlighted, to swap\n" +
                "gamepad = " + (C.PadEnabled ? "1" : "0") + "\n" +
                "pad_switch = " + C.PadSwitch + "\n");
            lastWrite = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception)
        {
            ModLog.Warn("Saving BatmanStaff.ini failed: " + Util.Unwrap(exception));
        }
    }
}
