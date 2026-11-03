#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BmSDK.Framework;

namespace BatmanRedHoodGuns;

internal static class GunsConfig
{
    public sealed class Settings
    {
        public bool GunsSelected;
        public float SwitchHold = 1f;
        public bool AlwaysInHands;

        public bool Enabled = true;
        public float ZoneX = 0.8f;
        public float ZoneY = 0.8f;
        public float Stiffness = 60f;
        public float Damping = 24.5f;
        public float SoftStart = 0.7f;
        public float SoftCurve = 2f;
        public float AimSens = 0.75f;

        public float Orbit = 1f;
        public int PivotSource = 2;
        public float PivotZ = 60f;
        public float ResetJumpDeg = 34f;
        public float ResetMoveUU = 150f;
        public float ReleaseTau = 0.12f;
        public float TraceDist = 20000f;

        public bool Recoil = true;
        public float Strength = 2f;
        public float Kick = 1.4f;
        public float Climb = 0.5f;
        public float Yaw = 0.4f;
        public float Recover = 0.4f;
        public float Punch = 0.6f;
        public float RecoverDefault = 0.5f;
        public float KickStiffness = 250f;
        public float KickDamping = 20f;
        public float BurstWindow = 0.25f;
        public float BurstScale = 0.6f;
        public bool PunchOn = true;
        public float PunchStiffness = 400f;
        public float PunchDamping = 22f;
        public float PunchRoll = 0.35f;
        public float PunchFov = 0.6f;

        public bool PadEnabled = true;
        public ushort PadSwitch = GunsPad.Y;
        public ushort PadHands = GunsPad.X;
        public ushort PadReload = GunsPad.X;
        public bool PadReloadNeedsAim = true;
        public float PadAimSens = 0.75f;
    }

    public const string FileName = "BatmanGuns.ini";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static Settings C = new Settings();

    private static string path = "";
    private static DateTime stamp;
    private static DateTime nextCheck = DateTime.MinValue;

    public static void Init(string modDirectory)
    {
        path = Path.Combine(modDirectory, FileName);
        Load(true);
    }

    public static void Tick()
    {
        DateTime now = DateTime.UtcNow;

        if (path.Length == 0 || now < nextCheck)
        {
            return;
        }

        nextCheck = now + TimeSpan.FromSeconds(0.5);

        try
        {
            DateTime written = File.GetLastWriteTimeUtc(path);

            if (written != stamp && (now - written).TotalSeconds > 0.25)
            {
                Load(false);
            }
        }
        catch
        {
        }
    }

    public static void SetGunsSelected(bool guns)
    {
        C.GunsSelected = guns;
        Save("selected", guns ? "guns" : "rec");
    }

    public static void SetAlwaysInHands(bool on)
    {
        C.AlwaysInHands = on;
        Save("always_in_hands", on ? "1" : "0");
    }

    private static void Load(bool first)
    {
        try
        {
            if (!File.Exists(path))
            {
                Settings seeded = new Settings();
                string imported = ImportDeadZoneAim(seeded);

                File.WriteAllText(path, Render(seeded), new UTF8Encoding(false));
                ModLog.Info("Config: wrote " + path + (imported.Length > 0 ? " (dead zone values taken from " + imported + ")" : "") + ".");
            }

            AddMissingSections();
            stamp = File.GetLastWriteTimeUtc(path);

            Settings settings = new Settings();

            Apply(settings, Parse(path));
            C = settings;

            ModLog.Info("Config " + (first ? "loaded" : "reloaded") + ": slot=" + (C.GunsSelected ? "guns" : "rec") + ", dead zone " + (C.Enabled ? "on" : "off") + " " + F(C.ZoneX) + "x" + F(C.ZoneY) +
                " k=" + F(C.Stiffness) + " c=" + F(C.Damping) + " aim speed x" + F(C.AimSens) + ", recoil " + (C.Recoil ? "on x" + F(C.Strength) : "off") + ".");
        }
        catch (Exception exception)
        {
            ModLog.Warn("Config: reading " + path + " failed: " + Util.Unwrap(exception));
        }
    }

    private static void AddMissingSections()
    {
        string text = File.ReadAllText(path);

        if (text.IndexOf("[gamepad]", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return;
        }

        File.AppendAllText(path, (text.EndsWith("\n") ? "" : "\n") + GamepadSection(new Settings()), new UTF8Encoding(false));
        ModLog.Info("Config: added the [gamepad] section to " + path + ".");
    }

    private static string ImportDeadZoneAim(Settings settings)
    {
        try
        {
            string source = Path.Combine(FileUtils.GetGamePath(), "Mods", "Batman Arsenal Pack", "DZAim.ini");

            if (!File.Exists(source))
            {
                return "";
            }

            Dictionary<string, string> values = Parse(source);

            values.Remove("enabled");
            values.Remove("selected");
            Apply(settings, values);

            return source;
        }
        catch (Exception exception)
        {
            ModLog.Warn("Config: reading DZAim.ini failed: " + Util.Unwrap(exception));

            return "";
        }
    }

    private static Dictionary<string, string> Parse(string file)
    {
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in File.ReadAllLines(file))
        {
            string line = raw.Trim();

            if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[')
            {
                continue;
            }

            int equals = line.IndexOf('=');

            if (equals <= 0)
            {
                continue;
            }

            string value = line.Substring(equals + 1);
            int comment = value.IndexOf(';');

            if (comment >= 0)
            {
                value = value.Substring(0, comment);
            }

            values[line.Substring(0, equals).Trim().ToLowerInvariant()] = value.Trim();
        }

        return values;
    }

    private static void Apply(Settings s, Dictionary<string, string> values)
    {
        foreach (KeyValuePair<string, string> pair in values)
        {
            if (pair.Key == "selected")
            {
                s.GunsSelected = pair.Value.Equals("guns", StringComparison.OrdinalIgnoreCase);

                continue;
            }

            if (pair.Key is "pad_switch" or "pad_hands" or "pad_reload")
            {
                if (GunsPad.TryParseButton(pair.Value, out ushort mask))
                {
                    if (pair.Key == "pad_switch") s.PadSwitch = mask;
                    else if (pair.Key == "pad_hands") s.PadHands = mask;
                    else s.PadReload = mask;
                }

                continue;
            }

            if (!float.TryParse(pair.Value, NumberStyles.Float, Inv, out float f) || !float.IsFinite(f))
            {
                continue;
            }

            bool on = f != 0f;

            switch (pair.Key)
            {
                case "switch_hold": s.SwitchHold = Math.Clamp(f, 0.3f, 3f); break;
                case "always_in_hands": s.AlwaysInHands = on; break;
                case "enabled": s.Enabled = on; break;
                case "zone_x": s.ZoneX = f; break;
                case "zone_y": s.ZoneY = f; break;
                case "stiffness": s.Stiffness = f; break;
                case "damping": s.Damping = f; break;
                case "soft_start": s.SoftStart = f; break;
                case "soft_curve": s.SoftCurve = f; break;
                case "aim_sens": s.AimSens = f; break;
                case "orbit": s.Orbit = f; break;
                case "pivot_source": s.PivotSource = (int)f; break;
                case "pivot_z": s.PivotZ = f; break;
                case "reset_jump_deg": s.ResetJumpDeg = f; break;
                case "reset_move_uu": s.ResetMoveUU = f; break;
                case "release_tau": s.ReleaseTau = f; break;
                case "trace_dist": s.TraceDist = f; break;
                case "recoil": s.Recoil = on; break;
                case "recoil_strength": s.Strength = f; break;
                case "pistol_kick": s.Kick = f; break;
                case "pistol_climb": s.Climb = f; break;
                case "pistol_yaw": s.Yaw = f; break;
                case "pistol_recover": s.Recover = f; break;
                case "pistol_punch": s.Punch = f; break;
                case "recoil_recover_s": s.RecoverDefault = f; break;
                case "recoil_kick_stiffness": s.KickStiffness = f; break;
                case "recoil_kick_damping": s.KickDamping = f; break;
                case "recoil_burst_window": s.BurstWindow = f; break;
                case "recoil_burst_scale": s.BurstScale = f; break;
                case "recoil_punch": s.PunchOn = on; break;
                case "recoil_punch_stiffness": s.PunchStiffness = f; break;
                case "recoil_punch_damping": s.PunchDamping = f; break;
                case "recoil_punch_roll": s.PunchRoll = f; break;
                case "recoil_punch_fov": s.PunchFov = f; break;
                case "pad": s.PadEnabled = on; break;
                case "pad_reload_needs_aim": s.PadReloadNeedsAim = on; break;
                case "pad_aim_sens": s.PadAimSens = f; break;
            }
        }

        if (!(s.ZoneX > 0.01f)) s.ZoneX = 0.8f;
        if (!(s.ZoneY > 0.01f)) s.ZoneY = 0.8f;
        if (!(s.Stiffness >= 0f)) s.Stiffness = 60f;
        if (!(s.Damping >= 0f)) s.Damping = 24.5f;
        if (!(s.SoftStart >= 0f && s.SoftStart <= 1f)) s.SoftStart = 0.7f;
        if (!(s.ReleaseTau > 0.005f)) s.ReleaseTau = 0.12f;
        if (!(s.AimSens > 0.01f && s.AimSens <= 5f)) s.AimSens = 0.75f;
        if (!(s.PadAimSens > 0.01f && s.PadAimSens <= 5f)) s.PadAimSens = 0.75f;
    }

    private static void Save(string key, string value)
    {
        try
        {
            if (path.Length == 0 || !File.Exists(path))
            {
                return;
            }

            List<string> lines = new List<string>(File.ReadAllLines(path));
            bool done = false;

            for (int index = 0; index < lines.Count; index++)
            {
                if (lines[index].TrimStart().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                {
                    lines[index] = key + "=" + value;
                    done = true;

                    break;
                }
            }

            if (!done)
            {
                lines.Add(key + "=" + value);
            }

            File.WriteAllLines(path, lines, new UTF8Encoding(false));
            stamp = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception)
        {
            ModLog.Warn("Config: saving " + key + " failed: " + Util.Unwrap(exception));
        }
    }

    private static string F(float value) => value.ToString("0.###", Inv);

    private static string B(bool value) => value ? "1" : "0";

    private static string Render(Settings s) =>
$@"; Batman Red Hood Guns settings. Saved changes apply right away, no restart needed.

[slot]
; Which gadget sits in the REC gun's wheel slot: rec or guns. Holding that slot's key switches it (the mod saves it here).
selected={(s.GunsSelected ? "guns" : "rec")}
; How long to hold the key before it switches, seconds
switch_hold={F(s.SwitchHold)}
; 1 = Batman always holds the pistols in his hands, whatever gadget is selected (F5 toggles it)
always_in_hands={B(s.AlwaysInHands)}

[deadzone]
; Dead zone aiming while the guns are aimed: the reticle moves freely inside a zone and the camera follows on a spring.
; Shots always go where the reticle is. 1 = on, 0 = off (the reticle stays in the centre).
enabled={B(s.Enabled)}
; Zone size: zone_x is a fraction of half the screen width, zone_y of half the height (1.0 = screen edge)
zone_x={F(s.ZoneX)}
zone_y={F(s.ZoneY)}
; Spring with which the camera catches up with the reticle
stiffness={F(s.Stiffness)}
damping={F(s.Damping)}
; Soft edge: from this fraction of the zone, part of the mouse movement turns the camera directly (leak = t^soft_curve)
soft_start={F(s.SoftStart)}
soft_curve={F(s.SoftCurve)}
; Mouse speed while aiming the guns (1 = as in the game, lower = slower)
aim_sens={F(s.AimSens)}

[camera]
; 1 = the picture orbits around Batman (he stays in place on screen), 0 = it turns in place; fractions blend
orbit={F(s.Orbit)}
; Orbit centre: 0 = camera pivot, 1 = Batman's position + pivot_z, 2 = camera pivot + its current offset
pivot_source={s.PivotSource}
pivot_z={F(s.PivotZ)}
; The zone resets when the game camera jumps by this angle (degrees) or distance (units, 100 = 1 m)
reset_jump_deg={F(s.ResetJumpDeg)}
reset_move_uu={F(s.ResetMoveUU)}
; How fast the reticle returns to the centre after lowering the guns, seconds
release_tau={F(s.ReleaseTau)}
; Length of the aim ray, units
trace_dist={F(s.TraceDist)}

[recoil]
; Aimed shots kick the aim up and sideways (the reticle jumps, the camera catches up) and jolt the picture. 1 = on
recoil={B(s.Recoil)}
; Multiplier for all recoil values below
recoil_strength={F(s.Strength)}
; Degrees per shot: kick = fast kick up (springs back), climb = barrel climb (returns over pistol_recover seconds),
; yaw = random sideways kick, punch = picture jolt only (does not move the aim)
pistol_kick={F(s.Kick)}
pistol_climb={F(s.Climb)}
pistol_yaw={F(s.Yaw)}
pistol_recover={F(s.Recover)}
pistol_punch={F(s.Punch)}
recoil_recover_s={F(s.RecoverDefault)}
recoil_kick_stiffness={F(s.KickStiffness)}
recoil_kick_damping={F(s.KickDamping)}
; Shots closer together than recoil_burst_window seconds each kick recoil_burst_scale times as much
recoil_burst_window={F(s.BurstWindow)}
recoil_burst_scale={F(s.BurstScale)}
; Picture jolt: 1 = on; spring; roll as a share of the jolt; FOV widening per degree of jolt
recoil_punch={B(s.PunchOn)}
recoil_punch_stiffness={F(s.PunchStiffness)}
recoil_punch_damping={F(s.PunchDamping)}
recoil_punch_roll={F(s.PunchRoll)}
recoil_punch_fov={F(s.PunchFov)}
" + GamepadSection(s);

    private static string GamepadSection(Settings s) =>
$@"
[gamepad]
; Xbox / XInput controller support. 1 = on
pad={B(s.PadEnabled)}
; On the gadget wheel, with the REC gun / guns slot highlighted: pad_switch swaps REC gun <-> guns, pad_hands toggles always_in_hands.
; Buttons: A B X Y LB RB LS RS BACK START UP DOWN LEFT RIGHT, or none
pad_switch={(s.PadSwitch == 0 ? "none" : GunsPad.ButtonName(s.PadSwitch))}
pad_hands={(s.PadHands == 0 ? "none" : GunsPad.ButtonName(s.PadHands))}
; Reload button while the guns are held; with pad_reload_needs_aim=1 only while aiming them (LT), so it never blocks a strike
pad_reload={(s.PadReload == 0 ? "none" : GunsPad.ButtonName(s.PadReload))}
pad_reload_needs_aim={B(s.PadReloadNeedsAim)}
; Right stick speed while aiming the guns (1 = as in the game, lower = slower)
pad_aim_sens={F(s.PadAimSens)}
";
}
