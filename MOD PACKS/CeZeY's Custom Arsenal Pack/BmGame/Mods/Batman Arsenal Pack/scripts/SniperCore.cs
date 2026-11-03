#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace BatmanDeathstrokeSniper;

internal static class ModLog
{
    private static readonly HashSet<string> loggedOnce = new HashSet<string>(StringComparer.Ordinal);

    public static void Info(string message) { try { Debug.Log("[BatmanSniper] " + message); } catch { } }

    public static void Warn(string message) { try { Debug.LogWarning("[BatmanSniper] " + message); } catch { } }

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
}

internal static class Sniper
{
    public const string DefaultDisruptorClass = "RDisruptorSniperBm";

    public static RPawnPlayer? Batman()
    {
        RPawnPlayer? pawn = Game.GetPlayerController(0)?.Pawn as RPawnPlayer;

        return pawn != null && pawn.IsValid && IsBatman(pawn) ? pawn : null;
    }

    public static bool IsBatman(RPawnPlayer pawn) => Util.ClassName(pawn).StartsWith("RPawnPlayerBm", StringComparison.Ordinal);

    public static RDisruptorSniper? Dis(Pawn? pawn)
    {
        if (pawn == null || !pawn.IsValid)
        {
            return null;
        }

        for (Inventory? inv = pawn.InvManager?.InventoryChain; inv != null; inv = inv._Inventory)
        {
            if (inv is RDisruptorSniper d && d.IsValid)
            {
                return d;
            }
        }

        return null;
    }

    public static bool Selected => SniperConfig.C.SniperSelected;

    public static bool InHand(RDisruptorSniper d) => Convert.ToInt32(d.GadgetState) == 1;

    public static bool Held(out RPawnPlayer? pawn, out RDisruptorSniper? dis)
    {
        pawn = Batman();
        dis = null;

        if (pawn?.InvManager is not RInventoryManager inventory || !inventory.IsValid)
        {
            return false;
        }

        dis = inventory.CurrentGadget as RDisruptorSniper;

        return dis != null && dis.IsValid;
    }

    public static bool IsBatmansDisruptor(RDisruptorSniper self, out RPawnPlayer? pawn)
    {
        pawn = Batman();

        return pawn != null && Util.Same(self.Owner, pawn);
    }

    public static int WheelIndex(RPawnPlayer? pawn)
    {
        if (pawn?.InvManager is not RInventoryManager inventory || !inventory.IsValid)
        {
            return -1;
        }

        RDisruptorSniper? d = Dis(pawn);

        return Util.IndexOf(inventory.BM2SelectableGadgets, d != null ? Util.ClassName(d) : DefaultDisruptorClass);
    }
}

internal static class SniperConfig
{
    internal sealed class Settings
    {
        public bool SniperSelected;
        public float SwitchHold = 1.0f;

        public string RifleModel = "arkham";
        public bool ArkhamRifle => RifleModel == "arkham";
        public int Laser = 1;
        public float LaserScale = 1f;
        public bool LaserSound = true;
        public Keys LaserKey = Keys.F6;
        public int Magazine = 3;
        public float DamageMul = 12f;
        public float Impulse = 1500f;
        public float Range = 15000f;
        public float AimRadius = 45f;
        public int GunNoise = 2;
        public bool NoFilter = true;
        public bool DestroyObjects = true;
        public bool HitDrones = true;
        public bool DeadZone = true;
        public bool ReloadAnim = true;

        public bool Fear = true;
        public float FearRadius = 1500f;
        public float FearTime = 20f;
        public bool FearCower = true;

        public bool BackShow = true;
        public bool BackAuto;
        public string BackBone = "Bip01_Spine2";
        public Vector3 BackOffset = new Vector3(23.731f, 20.892f, 4.0f);
        public Rotator BackRot = new Rotator(203.978f, -12.623f, 199.973f);
        public Vector3 HandOffset = new Vector3(-14f, 2f, -5f);
        public Rotator HandRot = new Rotator(0f, 0f, 0f);
        public Vector3 AkBackOffset = new Vector3(23.731f, 20.892f, 4.0f);
        public Rotator AkBackRot = new Rotator(203.978f, -12.623f, 199.973f);
        public Vector3 AkHandOffset = new Vector3(-14f, 2f, -5f);
        public Rotator AkHandRot = new Rotator(0f, 0f, 0f);

        public float MuzzleScale = 2f;
        public int Trail = 5;
        public float TrailScale = 1.5f;
        public float SparkScale;

        public bool PadEnabled = true;
        public ushort PadSwitch = SniperPad.Y;
        public ushort PadReload = SniperPad.X;
    }

    public static Settings C = new Settings();

    private static string path = "";
    private static DateTime writeTime;
    private static float nextCheck;

    public static void Init(string modDir)
    {
        path = Path.Combine(modDir, "BatmanSniper.ini");

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
                ModLog.Info("Config: BatmanSniper.ini reloaded.");
            }
        }
        catch
        {
        }
    }

    public static void SetSniperSelected(bool on)
    {
        C.SniperSelected = on;
        Save();
    }

    private static bool Bool(string v) => v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase);

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
            string[] lines = File.ReadAllLines(path);

            foreach (string raw in lines)
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
                    case "selected": c.SniperSelected = v.Equals("sniper", StringComparison.OrdinalIgnoreCase); break;
                    case "switch_hold": c.SwitchHold = Math.Clamp(Float(v, 1f), 0.3f, 5f); break;
                    case "rifle_model": c.RifleModel = v.Equals("thug", StringComparison.OrdinalIgnoreCase) ? "thug" : "arkham"; break;
                    case "laser": c.Laser = Math.Clamp((int)Float(v, 1), 0, 1); break;
                    case "laser_scale": c.LaserScale = Math.Clamp(Float(v, 1f), 0.1f, 5f); break;
                    case "laser_sound": c.LaserSound = Bool(v); break;
                    case "laser_key": if (System.Enum.TryParse(v, true, out Keys lk) && lk != Keys.None) c.LaserKey = lk; break;
                    case "magazine": c.Magazine = Math.Clamp((int)Float(v, 3), 1, 6); break;
                    case "damage_mult": c.DamageMul = Math.Max(0.1f, Float(v, 12f)); break;
                    case "impulse": c.Impulse = Math.Max(0f, Float(v, 1500f)); break;
                    case "range": c.Range = Math.Max(500f, Float(v, 15000f)); break;
                    case "aim_radius": c.AimRadius = Math.Max(5f, Float(v, 45f)); break;
                    case "gun_noise": c.GunNoise = Math.Clamp((int)Float(v, 2), 0, 2); break;
                    case "no_disruptor_filter": c.NoFilter = Bool(v); break;
                    case "destroy_objects": c.DestroyObjects = Bool(v); break;
                    case "hit_drones": c.HitDrones = Bool(v); break;
                    case "dead_zone": c.DeadZone = Bool(v); break;
                    case "reload_anim": c.ReloadAnim = Bool(v); break;
                    case "fear": c.Fear = Bool(v); break;
                    case "fear_radius": c.FearRadius = Math.Clamp(Float(v, 15f), 2f, 60f) * 100f; break;
                    case "fear_time": c.FearTime = Math.Clamp(Float(v, 20f), 3f, 120f); break;
                    case "fear_cower": c.FearCower = Bool(v); break;
                    case "back_show": c.BackShow = Bool(v); break;
                    case "back_bone": c.BackBone = v; break;
                    case "back_pose": c.BackAuto = v.Equals("auto", StringComparison.OrdinalIgnoreCase); break;
                    case "back_offset": c.BackOffset = Vec(v, c.BackOffset); break;
                    case "back_rot": c.BackRot = Rot(v, c.BackRot); break;
                    case "hand_offset": c.HandOffset = Vec(v, c.HandOffset); break;
                    case "hand_rot": c.HandRot = Rot(v, c.HandRot); break;
                    case "ak_back_offset": c.AkBackOffset = Vec(v, c.AkBackOffset); break;
                    case "ak_back_rot": c.AkBackRot = Rot(v, c.AkBackRot); break;
                    case "ak_hand_offset": c.AkHandOffset = Vec(v, c.AkHandOffset); break;
                    case "ak_hand_rot": c.AkHandRot = Rot(v, c.AkHandRot); break;
                    case "muzzle_scale": c.MuzzleScale = Math.Max(0f, Float(v, 2f)); break;
                    case "trail": c.Trail = Math.Clamp((int)Float(v, 5), 0, SniperFire.TrailTypes.Length - 1); break;
                    case "trail_scale": c.TrailScale = Math.Max(0.1f, Float(v, 1.5f)); break;
                    case "spark_scale": c.SparkScale = Math.Max(0f, Float(v, 0f)); break;
                    case "pad_enabled": c.PadEnabled = Bool(v); break;
                    case "pad_switch": if (SniperPad.TryParseButton(v, out ushort sw)) c.PadSwitch = sw; break;
                    case "pad_reload": if (SniperPad.TryParseButton(v, out ushort rl)) c.PadReload = rl; break;
                }
            }

            C = c;
            writeTime = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception)
        {
            ModLog.Warn("Config: reading BatmanSniper.ini failed, keeping the current settings: " + Util.Unwrap(exception));
        }
    }

    public static void Save()
    {
        Settings c = C;

        try
        {
            string[] lines =
            {
                "; Batman Arsenal Pack - sniper rifle settings. Saved by the mod when you switch modes; edits are picked up while the game runs.",
                "[slot]",
                "; what the Disruptor slot (key 7) holds: disruptor | sniper. Hold the key (or Y on the gamepad gadget wheel) to switch.",
                "selected=" + (c.SniperSelected ? "sniper" : "disruptor"),
                "; seconds to hold the Disruptor key to switch",
                "switch_hold=" + c.SwitchHold.ToString(Util.Inv),
                "",
                "[sniper]",
                "; arkham = the Arkham Knight's (Red Hood's) own sniper rifle from his boss fight, thug = the militia sniper rifle",
                "rifle_model=" + c.RifleModel,
                "; 1 = red laser sight while the rifle is aimed (the militia snipers' own laser), 0 = off. laser_key toggles it in game.",
                "laser=" + c.Laser,
                "; key that toggles the laser on/off in game (F5 is the guns' always-in-hand toggle)",
                "laser_key=" + c.LaserKey,
                "; laser thickness / glow size",
                "laser_scale=" + c.LaserScale.ToString(Util.Inv),
                "; 1 = the laser switches on with the snipers' laser sound",
                "laser_sound=" + (c.LaserSound ? 1 : 0),
                "magazine=" + c.Magazine,
                "damage_mult=" + c.DamageMul.ToString(Util.Inv),
                "impulse=" + c.Impulse.ToString(Util.Inv),
                "range=" + c.Range.ToString(Util.Inv),
                "aim_radius=" + c.AimRadius.ToString(Util.Inv),
                "; thugs and the shot: 0 off, 1 thugs nearby hear it and investigate, 2 alarm everyone nearby",
                "gun_noise=" + c.GunNoise,
                "no_disruptor_filter=" + (c.NoFilter ? 1 : 0),
                "destroy_objects=" + (c.DestroyObjects ? 1 : 0),
                "hit_drones=" + (c.HitDrones ? 1 : 0),
                "; 1 = use the Dead Zone Aim mod (aim lag + recoil) for the sniper rifle when that mod is installed",
                "dead_zone=" + (c.DeadZone ? 1 : 0),
                "reload_anim=" + (c.ReloadAnim ? 1 : 0),
                "",
                "[fear]",
                "; 1 = when the sniper rifle kills a thug, the thugs near him who see it (or are close enough to hear it) flinch and run away",
                "fear=" + (c.Fear ? 1 : 0),
                "; metres around the victim",
                "fear_radius=" + (c.FearRadius / 100f).ToString(Util.Inv),
                "; seconds before the scared thugs pull themselves together",
                "fear_time=" + c.FearTime.ToString(Util.Inv),
                "; 1 = they flinch / cower first (the game's own combat cower reaction)",
                "fear_cower=" + (c.FearCower ? 1 : 0),
                "",
                "[holster]",
                "; 1 = the sniper rifle is always slung on Batman's back (hidden only while it is in his hands), 0 = never",
                "back_show=" + (c.BackShow ? 1 : 0),
                "back_bone=" + c.BackBone,
                "; auto = fit the rifle across Batman's back on first use (then saved as manual with the offsets below)",
                "back_pose=" + (c.BackAuto ? "auto" : "manual"),
                "back_offset=" + Util.V(c.BackOffset),
                "back_rot=" + Util.R(c.BackRot),
                "hand_offset=" + Util.V(c.HandOffset),
                "hand_rot=" + Util.R(c.HandRot),
                "; the same four for the Arkham Knight's rifle (rifle_model=arkham)",
                "ak_back_offset=" + Util.V(c.AkBackOffset),
                "ak_back_rot=" + Util.R(c.AkBackRot),
                "ak_hand_offset=" + Util.V(c.AkHandOffset),
                "ak_hand_rot=" + Util.R(c.AkHandRot),
                "",
                "[effects]",
                "muzzle_scale=" + c.MuzzleScale.ToString(Util.Inv),
                "; 0 off, 5 tracer (default), 9 shotgun tracer, 10 slow-motion bullet, 12 sniper beam (see SniperFire.cs for the list)",
                "trail=" + c.Trail,
                "trail_scale=" + c.TrailScale.ToString(Util.Inv),
                "spark_scale=" + c.SparkScale.ToString(Util.Inv),
                "",
                "[gamepad]",
                "pad_enabled=" + (c.PadEnabled ? 1 : 0),
                "; on the gadget wheel with the Disruptor highlighted",
                "pad_switch=" + SniperPad.ButtonName(c.PadSwitch).ToLowerInvariant(),
                "; with LT held and the sniper rifle aimed",
                "pad_reload=" + SniperPad.ButtonName(c.PadReload).ToLowerInvariant(),
            };

            File.WriteAllLines(path, lines);
            writeTime = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception)
        {
            ModLog.Warn("Config: writing BatmanSniper.ini failed: " + Util.Unwrap(exception));
        }
    }
}
