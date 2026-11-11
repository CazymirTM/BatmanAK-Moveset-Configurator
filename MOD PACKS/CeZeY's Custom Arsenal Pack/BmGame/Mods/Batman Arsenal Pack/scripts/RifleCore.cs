#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace BatmanAssaultRifle;

internal static class ModLog
{
    private static readonly HashSet<string> loggedOnce = new HashSet<string>(StringComparer.Ordinal);

    public static void Info(string message) { try { Debug.Log("[BatmanRifle] " + message); } catch { } }

    public static void Warn(string message) { try { Debug.LogWarning("[BatmanRifle] " + message); } catch { } }

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
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string ClassName(GameObject? o)
    {
        try
        {
            if (o != null && o.IsValid && o.Class != null && o.Class.IsValid)
            {
                return o.Class.Name.ToString();
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

    public static string V(Vector3 v) => string.Format(Inv, "{0:0.###},{1:0.###},{2:0.###}", v.X, v.Y, v.Z);

    public static string R(Rotator r) => string.Format(Inv, "{0:0.###},{1:0.###},{2:0.###}", r.Pitch, r.Yaw, r.Roll);

    public static float Now => (float)(Environment.TickCount64 / 1000.0);

    public static void Basis(Rotator r, out Vector3 x, out Vector3 y, out Vector3 z)
    {
        double P = r.Pitch * Math.PI / 180, Yw = r.Yaw * Math.PI / 180, Rl = r.Roll * Math.PI / 180;
        float SP = (float)Math.Sin(P), CP = (float)Math.Cos(P), SY = (float)Math.Sin(Yw), CY = (float)Math.Cos(Yw), SR = (float)Math.Sin(Rl), CR = (float)Math.Cos(Rl);

        x = new Vector3(CP * CY, CP * SY, SP);
        y = new Vector3(SR * SP * CY - CR * SY, SR * SP * SY + CR * CY, -SR * CP);
        z = new Vector3(-(CR * SP * CY + SR * SY), CY * SR - CR * SP * SY, CR * CP);
    }

    public static Rotator FromBasis(Vector3 x, Vector3 y, Vector3 z)
    {
        double pitch = Math.Atan2(x.Z, Math.Sqrt(x.X * x.X + x.Y * x.Y));
        double yaw = Math.Atan2(x.Y, x.X);
        var sy = new Vector3((float)-Math.Sin(yaw), (float)Math.Cos(yaw), 0);
        double roll = Math.Atan2(Vector3.Dot(z, sy), Vector3.Dot(y, sy));
        const double k = 180 / Math.PI;

        return new Rotator((float)(pitch * k), (float)(yaw * k), (float)(roll * k));
    }

    public static Vector3 ToLocal(Vector3 w, Vector3 xb, Vector3 yb, Vector3 zb) => new Vector3(Vector3.Dot(w, xb), Vector3.Dot(w, yb), Vector3.Dot(w, zb));

    public static Vector3 ToWorld(Vector3 l, Vector3 xb, Vector3 yb, Vector3 zb) => xb * l.X + yb * l.Y + zb * l.Z;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private static readonly uint ownPid = (uint)Environment.ProcessId;

    public static bool GameFocused()
    {
        try
        {
            IntPtr fg = GetForegroundWindow();

            if (fg == IntPtr.Zero)
            {
                return false;
            }

            GetWindowThreadProcessId(fg, out uint pid);

            return pid == ownPid;
        }
        catch
        {
            return true;
        }
    }

    public static bool KeyDown(int vk)
    {
        try
        {
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }
        catch
        {
            return false;
        }
    }
}

internal static class Rifle
{
    public const string BatarangClass = "RBatarangBm";

    public static RPawnPlayer? Batman()
    {
        RPawnPlayer? pawn = Game.GetPlayerController(0)?.Pawn as RPawnPlayer;

        return pawn != null && pawn.IsValid && IsBatman(pawn) ? pawn : null;
    }

    public static bool IsBatman(RPawnPlayer pawn) => Util.ClassName(pawn).StartsWith("RPawnPlayerBm", StringComparison.Ordinal);

    public static RBatarang? Bat(Pawn? pawn)
    {
        if (pawn == null || !pawn.IsValid)
        {
            return null;
        }

        for (Inventory? inv = pawn.InvManager?.InventoryChain; inv != null; inv = inv._Inventory)
        {
            if (inv is RBatarang b && b.IsValid && Util.ClassName(b) == BatarangClass)
            {
                return b;
            }
        }

        return null;
    }

    public static bool Selected => RifleConfig.C.RifleSelected;

    public static bool IsCurrent(RPawnPlayer pawn, RBatarang bat) => (pawn.InvManager as RInventoryManager)?.CurrentGadget is RBatarang cur && Util.Same(cur, bat);

    public static bool Primed(RBatarang bat)
    {
        try
        {
            return bat.bAiming && bat.GetStateName().ToString() == "Primed";
        }
        catch
        {
            return false;
        }
    }

    public static bool IsBatmansBatarang(RBatarang self, out RPawnPlayer? pawn)
    {
        pawn = Batman();

        return pawn != null && Util.ClassName(self) == BatarangClass && Util.Same(self.Owner, pawn);
    }

    public static int WheelIndex(RPawnPlayer? pawn)
    {
        if (pawn?.InvManager is not RInventoryManager inventory || !inventory.IsValid)
        {
            return -1;
        }

        return Util.IndexOf(inventory.BM2SelectableGadgets, BatarangClass);
    }

    public static bool HeldBy(out RPawnPlayer? pawn, out RBatarang? bat)
    {
        pawn = Batman();
        bat = pawn != null ? Bat(pawn) : null;

        return pawn != null && bat != null && Selected && IsCurrent(pawn, bat);
    }
}

internal static class RifleConfig
{
    internal sealed class Settings
    {
        public bool RifleSelected;
        public float SwitchHold = 1.0f;

        public int Magazine = 37;
        public float Rpm = 750f;
        public int HitsToKill = 3;
        public bool Headshots = true;
        public string HitType = "RDmgType_QuickBatarang";
        public float WoundDamage = 1f;
        public float KillDamageMul = 12f;
        public float Impulse = 600f;
        public float Range = 12000f;
        public float AimAssist = 22f;
        public float Spread = 0.5f;
        public float SpreadPerShot = 0.18f;
        public float SpreadMax = 3.0f;
        public float SpreadRecover = 4.0f;
        public int GunNoise = 2;
        public bool DestroyObjects = true;
        public bool HitDrones = true;
        public float DroneDamage = 250f;
        public bool DeadZone = true;
        public int Burst = 5;

        public bool ReloadAnim = true;
        public float ReloadTime = 3.0f;

        public string MuzzleFx = "thug";
        public float MuzzleScale = 1f;
        public int Trail = 5;
        public float TrailScale = 1f;
        public float SparkScale = 0.6f;
        public bool ShotSound = true;

        public bool BackShow = true;
        public string BackBone = "Bip01_Spine2";
        public bool BackAuto;
        public Vector3 BackOffset = new Vector3(23.731f, 20.892f, 4f);
        public Rotator BackRot = new Rotator(203.978f, -12.623f, 199.973f);
        public float BackRight = 18f;
        public string HandBone = "Gundummy";
        public Vector3 HandOffset = Vector3.Zero;
        public Rotator HandRot = new Rotator(0f, 0f, 0f);
        public float PitchFix = 0.6f;

        public string RifleModel = "m4";
        public bool M4 => RifleModel == "m4";
        public float CustomScale = 1f;
        public bool RiflePaint;
        public byte ColorR = 200, ColorG = 35, ColorB = 35;
        public string RifleCamo = "off";
        public Vector3 M4HandOffset = Vector3.Zero;
        public Rotator M4HandRot = new Rotator(0f, 0f, 0f);
        public Vector3 M4BackOffset = new Vector3(23.731f, 20.892f, 4f);
        public Rotator M4BackRot = new Rotator(203.978f, -12.623f, 199.973f);

        public bool PadEnabled = true;
        public ushort PadSwitch = RiflePad.Y;
        public ushort PadReload = RiflePad.X;
    }

    public static Settings C = new Settings();

    private static string path = "";
    private static DateTime writeTime;
    private static float nextCheck;

    public static bool Loaded => path.Length > 0;

    public static void Init(string modDir)
    {
        path = Path.Combine(modDir, "BatmanRifle.ini");

        if (!File.Exists(path))
        {
            Save();
        }

        Load();
    }

    public static void Tick()
    {
        if (path.Length == 0 || Util.Now < nextCheck)
        {
            return;
        }

        nextCheck = Util.Now + 1f;

        try
        {
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) != writeTime)
            {
                Load();
                ModLog.Info("Config: BatmanRifle.ini reloaded.");
            }
        }
        catch
        {
        }
    }

    public static void SetRifleSelected(bool on)
    {
        C.RifleSelected = on;
        Save();
    }

    public static void SetRifleModel(string model)
    {
        C.RifleModel = NormModel(model);
        Save();
    }

    public static void SetCustomScale(float scale)
    {
        C.CustomScale = Math.Clamp(scale, 0.3f, 3f);
        Save();
    }

    public static void SetRiflePaint(bool on)
    {
        C.RiflePaint = on;
        Save();
    }

    public static void SetRifleRgb(byte r, byte g, byte b)
    {
        C.ColorR = r;
        C.ColorG = g;
        C.ColorB = b;
        Save();
    }

    public static void SetRifleCamo(string camo)
    {
        C.RifleCamo = NormCamo(camo);
        Save();
    }

    public static string NormModel(string v) => v.Equals("thug", StringComparison.OrdinalIgnoreCase) || v.Equals("militia", StringComparison.OrdinalIgnoreCase) ? "thug" : "m4";

    public static string NormCamo(string v) => v.Equals("colored", StringComparison.OrdinalIgnoreCase) ? "colored" : v.Equals("gold", StringComparison.OrdinalIgnoreCase) ? "gold" : "off";

    private static bool Bool(string v) => v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase);

    private static float Float(string v, float def) => float.TryParse(v, NumberStyles.Float, Util.Inv, out float f) ? f : def;

    private static Vector3 Vec(string v, Vector3 def)
    {
        string[] p = v.Split(',');

        return p.Length == 3 && float.TryParse(p[0], NumberStyles.Float, Util.Inv, out float x) && float.TryParse(p[1], NumberStyles.Float, Util.Inv, out float y) && float.TryParse(p[2], NumberStyles.Float, Util.Inv, out float z)
            ? new Vector3(x, y, z) : def;
    }

    private static Rotator Rot(string v, Rotator def)
    {
        Vector3 d = Vec(v, new Vector3(def.Pitch, def.Yaw, def.Roll));

        return new Rotator(d.X, d.Y, d.Z);
    }

    private static void Load()
    {
        try
        {
            var c = new Settings();

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();

                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("["))
                {
                    continue;
                }

                int eq = line.IndexOf('=');

                if (eq <= 0)
                {
                    continue;
                }

                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();
                int sc = v.IndexOf(';');

                if (sc >= 0)
                {
                    v = v.Substring(0, sc).Trim();
                }

                switch (k)
                {
                    case "selected": c.RifleSelected = v.Equals("rifle", StringComparison.OrdinalIgnoreCase); break;
                    case "switch_hold": c.SwitchHold = Math.Clamp(Float(v, 1f), 0.3f, 5f); break;
                    case "magazine": c.Magazine = Math.Clamp((int)Float(v, 37), 1, 200); break;
                    case "rpm": c.Rpm = Math.Clamp(Float(v, 750f), 60f, 1500f); break;
                    case "hits_to_kill": c.HitsToKill = Math.Clamp((int)Float(v, 3), 1, 20); break;
                    case "headshots": c.Headshots = Bool(v); break;
                    case "hit_type": c.HitType = v.Length > 0 ? v : c.HitType; break;
                    case "wound_damage": c.WoundDamage = Math.Max(0f, Float(v, 1f)); break;
                    case "kill_damage_mult": c.KillDamageMul = Math.Max(0.1f, Float(v, 12f)); break;
                    case "impulse": c.Impulse = Math.Max(0f, Float(v, 600f)); break;
                    case "range": c.Range = Math.Max(500f, Float(v, 12000f)); break;
                    case "aim_assist": c.AimAssist = Math.Max(0f, Float(v, 22f)); break;
                    case "spread": c.Spread = Math.Clamp(Float(v, 0.5f), 0f, 10f); break;
                    case "spread_per_shot": c.SpreadPerShot = Math.Clamp(Float(v, 0.18f), 0f, 5f); break;
                    case "spread_max": c.SpreadMax = Math.Clamp(Float(v, 3f), 0f, 15f); break;
                    case "spread_recover": c.SpreadRecover = Math.Clamp(Float(v, 4f), 0.1f, 50f); break;
                    case "gun_noise": c.GunNoise = Math.Clamp((int)Float(v, 2), 0, 2); break;
                    case "destroy_objects": c.DestroyObjects = Bool(v); break;
                    case "hit_drones": c.HitDrones = Bool(v); break;
                    case "drone_damage": c.DroneDamage = Math.Max(1f, Float(v, 250f)); break;
                    case "dead_zone": c.DeadZone = Bool(v); break;
                    case "quick_burst": c.Burst = Math.Clamp((int)Float(v, 5), 0, 30); break;
                    case "reload_anim": c.ReloadAnim = Bool(v); break;
                    case "reload_time": c.ReloadTime = Math.Clamp(Float(v, 3f), 0.5f, 6f); break;
                    case "muzzle_fx": c.MuzzleFx = v.Equals("redhood", StringComparison.OrdinalIgnoreCase) ? "redhood" : "thug"; break;
                    case "muzzle_scale": c.MuzzleScale = Math.Max(0f, Float(v, 1f)); break;
                    case "trail": c.Trail = Math.Clamp((int)Float(v, 5), 0, RifleFire.TrailTypes.Length - 1); break;
                    case "trail_scale": c.TrailScale = Math.Max(0.1f, Float(v, 1f)); break;
                    case "spark_scale": c.SparkScale = Math.Max(0f, Float(v, 0.6f)); break;
                    case "shot_sound": c.ShotSound = Bool(v); break;
                    case "back_show": c.BackShow = Bool(v); break;
                    case "back_bone": c.BackBone = v; break;
                    case "back_pose": c.BackAuto = v.Equals("auto", StringComparison.OrdinalIgnoreCase); break;
                    case "back_offset": c.BackOffset = Vec(v, c.BackOffset); break;
                    case "back_rot": c.BackRot = Rot(v, c.BackRot); break;
                    case "back_right": c.BackRight = Math.Clamp(Float(v, 18f), -40f, 40f); break;
                    case "hand_bone": c.HandBone = v; break;
                    case "hand_offset": c.HandOffset = Vec(v, c.HandOffset); break;
                    case "hand_rot": c.HandRot = Rot(v, c.HandRot); break;
                    case "pitch_fix": c.PitchFix = Math.Clamp(Float(v, 0.6f), 0f, 1f); break;
                    case "rifle_model": c.RifleModel = NormModel(v); break;
                    case "custom_scale": c.CustomScale = Math.Clamp(Float(v, 1f), 0.3f, 3f); break;
                    case "rifle_color": c.RiflePaint = Bool(v); break;
                    case "rifle_camo": c.RifleCamo = NormCamo(v); break;
                    case "rifle_rgb":
                        {
                            Vector3 rgb = Vec(v, new Vector3(c.ColorR, c.ColorG, c.ColorB));

                            c.ColorR = (byte)Math.Clamp(rgb.X, 0f, 255f);
                            c.ColorG = (byte)Math.Clamp(rgb.Y, 0f, 255f);
                            c.ColorB = (byte)Math.Clamp(rgb.Z, 0f, 255f);
                            break;
                        }
                    case "m4_hand_offset": c.M4HandOffset = Vec(v, c.M4HandOffset); break;
                    case "m4_hand_rot": c.M4HandRot = Rot(v, c.M4HandRot); break;
                    case "m4_back_offset": c.M4BackOffset = Vec(v, c.M4BackOffset); break;
                    case "m4_back_rot": c.M4BackRot = Rot(v, c.M4BackRot); break;
                    case "pad_enabled": c.PadEnabled = Bool(v); break;
                    case "pad_switch": if (RiflePad.TryParseButton(v, out ushort sw)) c.PadSwitch = sw; break;
                    case "pad_reload": if (RiflePad.TryParseButton(v, out ushort rl)) c.PadReload = rl; break;
                }
            }

            C = c;
            writeTime = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception)
        {
            ModLog.Warn("Config: reading BatmanRifle.ini failed, keeping the current settings: " + Util.Unwrap(exception));
        }
    }

    public static void Save()
    {
        Settings c = C;

        try
        {
            string[] lines =
            {
                "; Batman Arsenal Pack - assault rifle settings. Saved by the mod when you switch modes; edits are picked up while the game runs.",
                "[slot]",
                "; what the Batarang slot (key 1) holds: batarang | rifle. Hold the key (or Y on the gamepad gadget wheel) to switch.",
                "selected=" + (c.RifleSelected ? "rifle" : "batarang"),
                "; seconds to hold the Batarang key to switch",
                "switch_hold=" + c.SwitchHold.ToString(Util.Inv),
                "",
                "[rifle]",
                "; rounds per magazine",
                "magazine=" + c.Magazine,
                "; rounds per minute while the trigger is held (full auto)",
                "rpm=" + c.Rpm.ToString(Util.Inv),
                "; body hits that kill a thug (the last one is lethal, the others make him stagger); 1 = every hit kills",
                "hits_to_kill=" + c.HitsToKill,
                "; 1 = a head hit kills at once",
                "headshots=" + (c.Headshots ? 1 : 0),
                "; damage type of the wounding hits (the thug's reaction): RDmgType_QuickBatarang, RDmgType_Rifle, RDmgType_StrikeNoRepel ...",
                "hit_type=" + c.HitType,
                "wound_damage=" + c.WoundDamage.ToString(Util.Inv),
                "kill_damage_mult=" + c.KillDamageMul.ToString(Util.Inv),
                "impulse=" + c.Impulse.ToString(Util.Inv),
                "range=" + c.Range.ToString(Util.Inv),
                "; units around the bullet's path that still count as a hit on a thug",
                "aim_assist=" + c.AimAssist.ToString(Util.Inv),
                "; bullet spread in degrees: first shot, added per shot while the trigger is held, maximum, recovery per second",
                "spread=" + c.Spread.ToString(Util.Inv),
                "spread_per_shot=" + c.SpreadPerShot.ToString(Util.Inv),
                "spread_max=" + c.SpreadMax.ToString(Util.Inv),
                "spread_recover=" + c.SpreadRecover.ToString(Util.Inv),
                "; thugs and the shots: 0 off, 1 thugs nearby hear it and investigate, 2 alarm everyone nearby",
                "gun_noise=" + c.GunNoise,
                "destroy_objects=" + (c.DestroyObjects ? 1 : 0),
                "hit_drones=" + (c.HitDrones ? 1 : 0),
                "drone_damage=" + c.DroneDamage.ToString(Util.Inv),
                "; 1 = Dead Zone Aim (aim lag + recoil) while the rifle is aimed",
                "dead_zone=" + (c.DeadZone ? 1 : 0),
                "; rounds fired from the hip by a quick batarang (double tap the key / quickfire in combat), 0 = no quickfire",
                "quick_burst=" + c.Burst,
                "",
                "[model]",
                "; m4 = the M4 Carbine from BmGame\\CookedPCConsole\\BatmanArsenalM4C_SF.upk (when that file is missing the militia rifle is used), thug = the militia's own assault rifle",
                "; M4 model: \"M4 Carbine\" by Umang Rank (Sketchfab). The creator must be credited; commercial use allowed; all rights reserved by Umang Rank.",
                "; Converted for Arkham Knight: parts merged, hidden rounds removed, textures resized, camo and paint masks added.",
                "rifle_model=" + c.RifleModel,
                "; size of the M4 (in hand and on the back), 1 = as converted",
                "custom_scale=" + c.CustomScale.ToString(Util.Inv),
                "; 1 = paint the M4 in rifle_rgb, 0 = its own colours",
                "rifle_color=" + (c.RiflePaint ? 1 : 0),
                "rifle_rgb=" + c.ColorR + "," + c.ColorG + "," + c.ColorB,
                "; M4 camo blotches on the receiver, handguard and stock: off = no camo, colored = the camo takes rifle_rgb (with rifle_color=1 the whole rifle is",
                "; rifle_rgb with the camo a lighter, warmer shade), gold = gold camo (with rifle_color=1 the rest of the rifle takes rifle_rgb)",
                "rifle_camo=" + c.RifleCamo,
                "; where the M4 sits, used instead of hand_offset/hand_rot and back_offset/back_rot below while the M4 is the model",
                "m4_hand_offset=" + Util.V(c.M4HandOffset),
                "m4_hand_rot=" + Util.R(c.M4HandRot),
                "m4_back_offset=" + Util.V(c.M4BackOffset),
                "m4_back_rot=" + Util.R(c.M4BackRot),
                "",
                "[reload]",
                "; 1 = the rifle thugs' own reload animation",
                "reload_anim=" + (c.ReloadAnim ? 1 : 0),
                "; seconds",
                "reload_time=" + c.ReloadTime.ToString(Util.Inv),
                "",
                "[effects]",
                "; thug = the rifle thugs' own muzzle flash, redhood = Red Hood's pistol flash (Red Hood DLC)",
                "muzzle_fx=" + c.MuzzleFx,
                "muzzle_scale=" + c.MuzzleScale.ToString(Util.Inv),
                "; 0 off, 5 tracer (default), 6 sentry gun tracer, 9 shotgun tracer (see RifleFire.cs for the list)",
                "trail=" + c.Trail,
                "trail_scale=" + c.TrailScale.ToString(Util.Inv),
                "; bullet impact sparks, 0 = off",
                "spark_scale=" + c.SparkScale.ToString(Util.Inv),
                "shot_sound=" + (c.ShotSound ? 1 : 0),
                "",
                "[holster]",
                "; 1 = the rifle is slung on Batman's back when it is not in his hands",
                "back_show=" + (c.BackShow ? 1 : 0),
                "back_bone=" + c.BackBone,
                "; manual = back_offset/back_rot below (default = the sniper rifle's back placement), auto = fit the rifle across Batman's back on first use",
                "back_pose=" + (c.BackAuto ? "auto" : "manual"),
                "back_offset=" + Util.V(c.BackOffset),
                "back_rot=" + Util.R(c.BackRot),
                "; units the rifle sits to Batman's right of that spot, so it does not clip into the sniper rifle (negative = left)",
                "back_right=" + c.BackRight.ToString(Util.Inv),
                "; the rifle in his hands follows this bone of the rifle thugs' animations",
                "hand_bone=" + c.HandBone,
                "hand_offset=" + Util.V(c.HandOffset),
                "hand_rot=" + Util.R(c.HandRot),
                "; how much of the missing up/down aim the rifle makes up by tilting in his hands (0 = none, 1 = all)",
                "pitch_fix=" + c.PitchFix.ToString(Util.Inv),
                "",
                "[gamepad]",
                "pad_enabled=" + (c.PadEnabled ? 1 : 0),
                "; on the gadget wheel with the Batarang highlighted",
                "pad_switch=" + RiflePad.ButtonName(c.PadSwitch).ToLowerInvariant(),
                "; with LT held and the rifle aimed",
                "pad_reload=" + RiflePad.ButtonName(c.PadReload).ToLowerInvariant(),
            };

            File.WriteAllLines(path, lines);
            writeTime = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception)
        {
            ModLog.Warn("Config: writing BatmanRifle.ini failed: " + Util.Unwrap(exception));
        }
    }
}
