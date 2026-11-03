#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace BatmanRedHoodGuns;

internal static class ModLog
{
    private static readonly HashSet<string> loggedOnce = new HashSet<string>(StringComparer.Ordinal);

    public static void Info(string message) { try { Debug.Log("[BatmanGuns] " + message); } catch { } }

    public static void Warn(string message) { try { Debug.LogWarning("[BatmanGuns] " + message); } catch { } }

    public static void Once(string key, string message)
    {
        if (loggedOnce.Add(key))
        {
            Info(message);
        }
    }

    public static void ResetOnce() => loggedOnce.Clear();
}

internal static class Guns
{
    public const string Package = "Playable_DLCRedHood_SF";
    public const string ClassPath = "BmScript.MDualHandgunsRh";
    public const string ClassName = "MDualHandgunsRh";

    public const string ReplacedGadget = "RMagneticBlastBm";
    public const string ReplacedTitle = "REC GUN";

    public static RInventoryGadget? Gun;

    public static bool IsGun(GameObject? gadget) =>
        gadget != null && gadget.IsValid && Gun != null && Gun.IsValid && gadget.Ptr == Gun.Ptr;

    public static RPawnPlayer? Batman()
    {
        RPawnPlayer? pawn = Game.GetPlayerController(0)?.Pawn as RPawnPlayer;

        return pawn != null && pawn.IsValid && IsBatman(pawn) ? pawn : null;
    }

    public static bool IsBatman(RPawnPlayer pawn) => Util.ClassName(pawn).StartsWith("RPawnPlayerBm", StringComparison.Ordinal);

    public static bool HeldBy(out RPawnPlayer? pawn, out RInventoryManager? inventory)
    {
        pawn = Batman();
        inventory = pawn?.InvManager as RInventoryManager;

        return pawn != null && inventory != null && inventory.IsValid && IsGun(inventory.CurrentGadget);
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

    public static bool IsNone(FName name)
    {
        string text = name.ToString();

        return string.IsNullOrEmpty(text) || string.Equals(text, "None", StringComparison.OrdinalIgnoreCase);
    }

    public static string FullName(GameObject gameObject)
    {
        GameObject? outer = gameObject.Outer;

        return outer != null && outer.IsValid ? outer.Name + "." + gameObject.Name : gameObject.Name.ToString();
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

    public static bool ContainsSet(TArray<AnimSet> sets, AnimSet set)
    {
        for (int index = 0; index < sets.Count; index++)
        {
            AnimSet? existing = sets[index];

            if (existing != null && existing.IsValid && existing.Ptr == set.Ptr)
            {
                return true;
            }
        }

        return false;
    }
}

internal static unsafe class UCall
{
    private static Delegate? processEvent;

    private static Delegate ProcessEvent()
    {
        if (processEvent == null)
        {
            Type functions = typeof(Game).Assembly.GetType("BmSDK.Framework.GameFunctions", throwOnError: true)!;

            processEvent = (Delegate)functions.GetProperty("ProcessEvent", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;
        }

        return processEvent;
    }

    public static Function? Find(GameObject self, string name)
    {
        foreach (Struct level in self.Class.EnumerateSupersAndSelf())
        {
            foreach (Field field in level.EnumerateFields())
            {
                if (field is Function function && function.Name.ToString() == name)
                {
                    return function;
                }
            }
        }

        return null;
    }

    public static bool Has(GameObject self, string name) => Find(self, name) != null;

    public sealed class Frame
    {
        private readonly Dictionary<string, Property> parms;

        public readonly IntPtr Buffer;

        public Frame(Function function, IntPtr buffer)
        {
            Buffer = buffer;
            parms = function.EnumerateFields().OfType<Property>().ToDictionary(p => p.Name.ToString(), p => p);
        }

        public IntPtr this[string name] => Buffer + parms[name].Offset;

        public Property? Return => parms.Values.FirstOrDefault(p => p.PropertyFlags.HasFlag(Property.EPropertyFlags.CPF_ReturnParm));
    }

    public static bool Call(GameObject self, string name, Action<Frame>? setup = null, Action<Frame>? read = null)
    {
        Function? function = Find(self, name);

        if (function == null)
        {
            ModLog.Once("nofunc:" + name, "UCall: " + Util.ClassName(self) + " has no function " + name + ".");

            return false;
        }

        int size = 16;

        foreach (Property property in function.EnumerateFields().OfType<Property>())
        {
            size = Math.Max(size, property.Offset + property.ElementSize * Math.Max(1, property.ArrayDim));
        }

        IntPtr buffer = Marshal.AllocHGlobal(size + 16);

        try
        {
            new Span<byte>((void*)buffer, size + 16).Clear();

            Frame frame = new Frame(function, buffer);

            setup?.Invoke(frame);
            ProcessEvent().DynamicInvoke(self.Ptr, function.Ptr, buffer, IntPtr.Zero);
            read?.Invoke(frame);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return true;
    }
}

internal static class SniperLink
{
    private const string InHandKey = "BatmanSniper.InHand";
    private const string VictimsKey = "BatmanSniper.Victims";
    private const long VictimWindowMs = 6000;

    public static bool SniperInHands()
    {
        try
        {
            return AppDomain.CurrentDomain.GetData(InHandKey) as string == "1";
        }
        catch
        {
            return false;
        }
    }

    public static bool IsSniperVictim(GameObject? pawn)
    {
        try
        {
            if (pawn == null || AppDomain.CurrentDomain.GetData(VictimsKey) is not string list || list.Length == 0)
            {
                return false;
            }

            string ptr = pawn.Ptr.ToInt64().ToString();
            long now = Environment.TickCount64;

            foreach (string entry in list.Split(';'))
            {
                int at = entry.IndexOf('@');

                if (at > 0 && entry.Substring(0, at) == ptr && long.TryParse(entry.Substring(at + 1), out long t) && now - t <= VictimWindowMs)
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }
}
