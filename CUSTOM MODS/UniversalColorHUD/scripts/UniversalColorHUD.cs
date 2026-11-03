using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Framework;
using Tomlyn.Model;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace CeZeY.UniversalColorHudScript;

[Script]
public partial class UniversalColorHUD : Script
{
    private const int ConfigPollTicks = 60;
    private const int SearchIntervalTicks = 30;
    private const int RefreshIntervalTicks = 120;
    private const float MaintainInterval = 0.25f;
    private const float MaxPushRate = 60f;

    private readonly record struct Tint(float RO, float GO, float BO, float RS, float GS, float BS)
    {
        public static readonly Tint Identity = new(0f, 0f, 0f, 1f, 1f, 1f);

        public static Tint Lerp(Tint a, Tint b, float t) => new(
            a.RO + (b.RO - a.RO) * t, a.GO + (b.GO - a.GO) * t, a.BO + (b.BO - a.BO) * t,
            a.RS + (b.RS - a.RS) * t, a.GS + (b.GS - a.GS) * t, a.BS + (b.BS - a.BS) * t);

        public float Distance(Tint o) => Math.Max(
            Math.Max(Math.Abs(RO - o.RO), Math.Max(Math.Abs(GO - o.GO), Math.Abs(BO - o.BO))) / 255f,
            Math.Max(Math.Abs(RS - o.RS), Math.Max(Math.Abs(GS - o.GS), Math.Abs(BS - o.BS))));
    }

    private readonly record struct Rgb(float R, float G, float B);

    private sealed record Preset(string Key, string Label, Rgb Color);

    private static readonly Rgb HudBase = new(0.55f, 0.88f, 1.00f);
    private static readonly Tint JokerTint = new(64f, 0f, 128f, 0.6f, 0.5f, 0.7f);

    private static readonly Preset[] Presets =
    {
        new("default", "GAME DEFAULT", HudBase),
        new("red", "RED", new(1.00f, 0.18f, 0.15f)),
        new("orange", "ORANGE", new(1.00f, 0.50f, 0.10f)),
        new("yellow", "YELLOW", new(1.00f, 0.88f, 0.20f)),
        new("green", "GREEN", new(0.30f, 1.00f, 0.35f)),
        new("cyan", "CYAN", new(0.20f, 0.95f, 1.00f)),
        new("blue", "BLUE", new(0.25f, 0.45f, 1.00f)),
        new("purple", "PURPLE", new(0.62f, 0.30f, 1.00f)),
        new("pink", "PINK", new(1.00f, 0.35f, 0.75f)),
        new("white", "WHITE", new(1.00f, 1.00f, 1.00f)),
        new("joker", "JOKER", new(0.62f, 0.42f, 1.00f)),
    };

    private const int DefaultPreset = 0;
    private const int JokerPreset = 10;

    private static string _colorText = "red";
    private static int _preset = 1;
    private static float _hue = 3f;
    private static float _sat = 0.85f;
    private static float _val = 1f;
    private static bool _rainbow;
    private static float _rainbowSeconds = 10f;
    private static float _rainbowHz = 20f;
    private static float _blendTime = 0.4f;
    private static bool _autoActivate = true;
    private static float _autoDelay = 6f;
    private static Keys _toggleKey = Keys.F6;
    private static Keys _menuKey = Keys.F1;
    private static bool _diagnostics;

    private static bool _enabled;
    private static bool _autoPending;
    private static float _autoLeft;
    private static bool _touched;
    private static Tint _current = Tint.Identity;
    private static Tint _pushed = Tint.Identity;
    private static nint _pushedHud;
    private static float _sincePush;
    private static float _sinceMaintain;
    private static bool _flagChanged;
    private static long _lastTick;
    private static double _clock;

    private static RGFxMovieModularHud? _hud;
    private static int _searchCooldown;
    private static int _ticksSinceRefresh;

    private static DateTime _configStamp = DateTime.MinValue;
    private static string _lastConfigError = "";
    private static DateTime _nextErrorLog = DateTime.MinValue;
    private static string _logPath = "";
    private static string _modDir = "";
    private static string _lastDiag = "";

    private int _configTicks;
    private int _diagTicks;

    public override void Main()
    {
        Init();
    }

    public override void OnLoad()
    {
        Init();
    }

    private void Init()
    {
        _modDir = Mod.DirectoryPath;
        InitLog();
        LoadConfig(true);
        ArmAuto();
        Log("Loaded. color=" + _colorText + " rainbow=" + _rainbow + " auto=" + _autoActivate + " toggle_key=" + _toggleKey + " menu_key=" + _menuKey);
    }

    public override void OnEnterGame()
    {
        CloseMenu();
        _hud = null;
        _searchCooldown = 0;
        _ticksSinceRefresh = 0;
        _touched = false;
        _flagChanged = false;
        _current = Tint.Identity;
        _pushed = Tint.Identity;
        _pushedHud = 0;
        ArmAuto();
    }

    public override void OnUnload()
    {
        CloseMenu();

        try
        {
            if (_touched && IsUsable(_hud))
                Revert(_hud!);
        }
        catch
        {
        }
    }

    public override void OnKeyDown(Keys key)
    {
        try
        {
            if (key == _menuKey)
            {
                if (_menuOpen)
                    CloseMenu();
                else
                    OpenMenu();
                return;
            }

            if (_menuOpen && HandleMenuKey(key, false))
                return;

            if (key == _toggleKey)
            {
                SetEnabled(!_enabled, "toggle key");
                ShowToast();
            }
        }
        catch (Exception ex)
        {
            Log("Key handling failed: " + ex.Message);
        }
    }

    public override void OnTick()
    {
        long now = Stopwatch.GetTimestamp();
        float dt = _lastTick == 0 ? 0f : Math.Clamp((float)Stopwatch.GetElapsedTime(_lastTick, now).TotalSeconds, 0f, 0.25f);
        _lastTick = now;
        _clock += dt;

        if (++_configTicks >= ConfigPollTicks)
        {
            _configTicks = 0;
            try
            {
                LoadConfig(false);
            }
            catch (Exception ex)
            {
                LogThrottled("Config check failed: " + ex.Message);
            }
        }

        try
        {
            UpdateColor(dt);
        }
        catch (Exception ex)
        {
            LogThrottled("Color update deferred safely: " + ex.Message);
        }

        try
        {
            MenuTick(dt);
        }
        catch (Exception ex)
        {
            LogThrottled("Menu tick failed: " + ex.Message);
        }

        if (_diagnostics && ++_diagTicks >= 30)
        {
            _diagTicks = 0;
            try
            {
                PollDiagnostics();
            }
            catch (Exception ex)
            {
                LogThrottled("Diagnostics failed: " + ex.Message);
            }
        }
    }

    private static void ArmAuto()
    {
        _autoPending = _autoActivate;
        _autoLeft = _autoDelay;
        _enabled = false;
        if (_autoPending)
            Log("Auto-enable armed: color fades in " + _autoDelay.ToString("0.0", CultureInfo.InvariantCulture) + " s after the HUD is ready.");
    }

    private static void SetEnabled(bool on, string reason)
    {
        _autoPending = false;
        _enabled = on;
        Log("HUD color " + (on ? "ON" : "OFF") + " (" + reason + ").");
    }

    private static Rgb Hsv(float h, float s, float v)
    {
        h = ((h % 360f) + 360f) % 360f / 60f;
        int i = (int)MathF.Floor(h) % 6;
        float f = h - MathF.Floor(h);
        float p = v * (1f - s);
        float q = v * (1f - s * f);
        float t = v * (1f - s * (1f - f));
        return i switch
        {
            0 => new(v, t, p),
            1 => new(q, v, p),
            2 => new(p, v, t),
            3 => new(p, q, v),
            4 => new(t, p, v),
            _ => new(v, p, q),
        };
    }

    private static (float H, float S, float V) ToHsv(Rgb c)
    {
        float max = Math.Max(c.R, Math.Max(c.G, c.B));
        float min = Math.Min(c.R, Math.Min(c.G, c.B));
        float d = max - min;
        float h = 0f;
        if (d > 0.0001f)
        {
            if (max == c.R)
                h = 60f * (((c.G - c.B) / d) % 6f);
            else if (max == c.G)
                h = 60f * ((c.B - c.R) / d + 2f);
            else
                h = 60f * ((c.R - c.G) / d + 4f);
        }

        return ((h + 360f) % 360f, max <= 0f ? 0f : d / max, max);
    }

    private static Rgb CustomColor => Hsv(_hue, _sat, _val);

    private static Rgb ChosenColor => _preset >= 0 ? Presets[_preset].Color : CustomColor;

    private static Rgb RainbowColor => Hsv((float)(_clock / Math.Max(1f, _rainbowSeconds) % 1.0) * 360f, 0.8f, 1f);

    private static Rgb DisplayColor => _rainbow ? RainbowColor : ChosenColor;

    private static Tint TintFor(Rgb c)
    {
        return new Tint(0f, 0f, 0f, Math.Clamp(c.R / HudBase.R, 0f, 4f), Math.Clamp(c.G / HudBase.G, 0f, 4f), Math.Clamp(c.B / HudBase.B, 0f, 4f));
    }

    private static Tint TargetTint()
    {
        if (!_enabled)
            return Tint.Identity;
        if (_rainbow)
            return TintFor(RainbowColor);
        if (_preset == DefaultPreset)
            return Tint.Identity;
        if (_preset == JokerPreset)
            return JokerTint;
        return TintFor(ChosenColor);
    }

    private static string Hex(Rgb c)
    {
        static int B(float v) => (int)Math.Round(Math.Clamp(v, 0f, 1f) * 255f);
        return "#" + B(c.R).ToString("X2") + B(c.G).ToString("X2") + B(c.B).ToString("X2");
    }

    private static void SelectPreset(int index)
    {
        _preset = index;
        _colorText = Presets[index].Key;
        SyncHsv();
        SaveSetting("color", "\"" + _colorText + "\"");
        StopRainbowForPick();
    }

    private static void SetCustom()
    {
        _preset = -1;
        _colorText = Hex(CustomColor);
        SaveSetting("color", "\"" + _colorText + "\"");
        StopRainbowForPick();
    }

    private static void StopRainbowForPick()
    {
        if (!_rainbow)
            return;
        _rainbow = false;
        SaveSetting("rainbow", "false");
    }

    private static void SyncHsv()
    {
        var hsv = ToHsv(ChosenColor);
        if (hsv.S > 0.01f)
            _hue = MathF.Round(hsv.H);
        _sat = MathF.Round(hsv.S * 100f) / 100f;
        _val = Math.Max(0.2f, MathF.Round(hsv.V * 100f) / 100f);
    }

    private static void ApplyColorText(string text)
    {
        string key = (text ?? "").Trim();
        if (key.Equals("purple_joker", StringComparison.OrdinalIgnoreCase))
            key = "joker";

        int index = Array.FindIndex(Presets, p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _preset = index;
            _colorText = Presets[index].Key;
            SyncHsv();
            return;
        }

        if (TryParseHex(key, out Rgb rgb))
        {
            _preset = -1;
            _colorText = key.StartsWith("#") ? key.ToUpperInvariant() : "#" + key.ToUpperInvariant();
            var hsv = ToHsv(rgb);
            _hue = hsv.H;
            _sat = hsv.S;
            _val = Math.Max(0.2f, hsv.V);
            return;
        }

        Log("Unknown color \"" + text + "\", using red.");
        _preset = 1;
        _colorText = "red";
        SyncHsv();
    }

    private static bool TryParseHex(string text, out Rgb rgb)
    {
        rgb = new(1f, 1f, 1f);
        string digits = text.Trim().TrimStart('#');

        if (digits.Length != 6 || !int.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
            return false;

        rgb = new(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f);
        return true;
    }

    private static void UpdateColor(float dt)
    {
        if (_autoPending)
        {
            RGFxMovieModularHud? waiting = ResolveHud();
            if (IsUsable(waiting) && HudReady(waiting!))
            {
                _autoLeft -= dt;
                if (_autoLeft <= 0f)
                    SetEnabled(true, "auto after load");
            }
        }

        if (!_enabled && !_touched)
            return;

        RGFxMovieModularHud? hud = ResolveHud();
        if (!IsUsable(hud) || !HudReady(hud!))
            return;

        Tint target = TargetTint();
        float k = _blendTime <= 0.01f || dt <= 0f ? 1f : 1f - MathF.Exp(-dt * 4.6f / _blendTime);
        _current = Tint.Lerp(_current, target, k);
        if (_current.Distance(target) < 0.002f)
            _current = target;

        _sincePush += dt;
        _sinceMaintain += dt;

        float minGap = 1f / (_rainbow && _enabled ? _rainbowHz : MaxPushRate);
        bool changed = _current.Distance(_pushed) > 0.001f && _sincePush >= minGap;
        bool newHud = hud!.Ptr != _pushedHud;
        bool maintain = _sinceMaintain >= MaintainInterval;

        if (!changed && !newHud && !maintain)
            return;

        if (!_enabled && _current == Tint.Identity)
        {
            Revert(hud);
            return;
        }

        Push(hud, _current);
    }

    private static void Push(RGFxMovieModularHud hud, Tint t)
    {
        hud.SetColorTransform(t.RO, t.GO, t.BO, t.RS, t.GS, t.BS);

        foreach (RGFxMovieModularHudBase? movie in SubMovies(hud))
        {
            if (IsUsable(movie))
                movie!.SetColorTransform(t.RO, t.GO, t.BO, t.RS, t.GS, t.BS);
        }

        RHudModuleHealthBar? hb = hud.HealthBarModule;
        if (IsUsable(hb) && !hb!.CanBeTinted)
        {
            hb.CanBeTinted = true;
            _flagChanged = true;
        }

        if (hud.Ptr != _pushedHud && _diagnostics)
            Log("Tint active on HUD 0x" + hud.Ptr.ToString("X") + " (" + (_rainbow ? "cycle" : _colorText) + ").");

        _touched = true;
        _pushed = t;
        _pushedHud = hud.Ptr;
        _sincePush = 0f;
        _sinceMaintain = 0f;
    }

    private static IEnumerable<RGFxMovieModularHudBase?> SubMovies(RGFxMovieModularHud hud)
    {
        yield return hud.TargetsEtcGfxMovie;
        yield return hud.LocalRadarGfxMovie;
        yield return hud.BatmobileGfxMovie;

        foreach (var disposable in hud.DisposableGfxMovies)
            yield return disposable;
    }

    private static void Revert(RGFxMovieModularHud hud)
    {
        Push(hud, Tint.Identity);

        RHudModuleHealthBar? hb = hud.HealthBarModule;
        if (_flagChanged && IsUsable(hb))
            hb!.CanBeTinted = false;

        _flagChanged = false;
        _touched = false;
        _current = Tint.Identity;
        _pushedHud = 0;
        Log("HUD color reverted to the game default.");
    }

    private static RGFxMovieModularHud? ResolveHud()
    {
        if (!IsUsable(_hud))
        {
            if (--_searchCooldown > 0)
                return null;

            _hud = FindHud();
            _searchCooldown = SearchIntervalTicks;
            _ticksSinceRefresh = 0;
        }
        else if (++_ticksSinceRefresh >= RefreshIntervalTicks)
        {
            RGFxMovieModularHud? found = FindHud();
            if (found != null)
                _hud = found;
            _ticksSinceRefresh = 0;
        }

        return _hud;
    }

    private static bool IsUsable(GameObject? obj) => obj != null && obj.IsValid;

    private static RGFxMovieModularHud? FindHud()
    {
        try
        {
            RGFxMovieModularHud? fallback = null;
            foreach (RGFxMovieModularHud h in Game.FindObjects<RGFxMovieModularHud>())
            {
                try
                {
                    if (h == null || !h.IsValid || h.Ptr == 0 || h.IsClassDefaultObject)
                        continue;

                    RHudModuleHealthBar? hb = h.HealthBarModule;
                    if (hb == null || !hb.IsValid)
                        continue;

                    RGFxMovieModularHud? owner = hb.ModularHudMovie;
                    if (owner != null && owner.IsValid && owner.Ptr == h.Ptr)
                        return h;

                    fallback ??= h;
                }
                catch
                {
                }
            }

            return fallback;
        }
        catch
        {
            return null;
        }
    }

    private static bool HudReady(RGFxMovieModularHud hud)
    {
        try
        {
            if (!hud.bCommonElementsInitialised)
                return false;

            RHudModuleHealthBar? hb = hud.HealthBarModule;
            return hb != null && hb.IsValid;
        }
        catch
        {
            return false;
        }
    }

    private static void PollDiagnostics()
    {
        RGFxMovieModularHud? hud = IsUsable(_hud) ? _hud : FindHud();
        if (!IsUsable(hud))
            return;

        RHudModuleHealthBar? hb = hud!.HealthBarModule;
        if (!IsUsable(hb))
            return;

        string state =
            "visible=" + hb!.bCurrentlyVisible +
            " ready=" + hb.IsReadyToCallFunctions() +
            " tintable=" + hb.CanBeTinted +
            " on=" + _enabled + " pending=" + _autoPending +
            " pushed=" + _pushed.RS.ToString("0.00", CultureInfo.InvariantCulture) + "," +
            _pushed.GS.ToString("0.00", CultureInfo.InvariantCulture) + "," +
            _pushed.BS.ToString("0.00", CultureInfo.InvariantCulture);

        if (state == _lastDiag)
            return;

        _lastDiag = state;
        Log("STATE " + state);
    }

    private static string ReadString(TomlTable table, string key, string fallback)
    {
        return table.TryGetValue(key, out object? value) && value is string text ? text : fallback;
    }

    private static bool ReadBool(TomlTable table, string key, bool fallback)
    {
        return table.TryGetValue(key, out object? value) && value is bool flag ? flag : fallback;
    }

    private static float ReadFloat(TomlTable table, string key, float fallback)
    {
        if (!table.TryGetValue(key, out object? value))
            return fallback;

        return value switch
        {
            double d => (float)d,
            long l => l,
            _ => fallback
        };
    }

    private static void LoadConfig(bool first)
    {
        string path = Path.Combine(_modDir, "mod.toml");
        if (!File.Exists(path))
        {
            if (first)
                Log("mod.toml not found, using defaults.");
            return;
        }

        DateTime stamp = File.GetLastWriteTimeUtc(path);
        if (!first && stamp == _configStamp)
            return;

        TomlTable? root;
        try
        {
            root = Tomlyn.TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            if (ex.Message != _lastConfigError)
            {
                _lastConfigError = ex.Message;
                Log("mod.toml could not be read yet, keeping the current settings: " + ex.Message);
            }

            return;
        }

        _lastConfigError = "";
        _configStamp = stamp;

        TomlTable section = root != null && root.TryGetValue("hud", out object? raw) && raw is TomlTable table ? table : new TomlTable();

        ApplyColorText(ReadString(section, "color", "red"));
        _rainbow = ReadBool(section, "rainbow", false);
        _rainbowSeconds = Math.Clamp(ReadFloat(section, "rainbow_speed", 10f), 2f, 60f);
        _rainbowHz = Math.Clamp(ReadFloat(section, "rainbow_updates_per_second", 20f), 5f, 120f);
        _blendTime = Math.Clamp(ReadFloat(section, "transition_time", 0.4f), 0f, 2f);
        _autoActivate = ReadBool(section, "auto_activate", true);
        _autoDelay = Math.Clamp(ReadFloat(section, "auto_press_delay", 6f), 0f, 15f);
        _toggleKey = System.Enum.TryParse(ReadString(section, "toggle_key", "F6"), true, out Keys parsedKey) ? parsedKey : Keys.F6;
        _menuKey = System.Enum.TryParse(ReadString(section, "menu_key", "F1"), true, out Keys parsedMenu) ? parsedMenu : Keys.F1;
        _diagnostics = ReadBool(section, "diagnostics", false);

        Log("Settings: color=" + _colorText + " rainbow=" + _rainbow + " rainbow_speed=" + _rainbowSeconds +
            "s rate=" + _rainbowHz + " transition=" + _blendTime + "s auto_activate=" + _autoActivate +
            " delay=" + _autoDelay + "s toggle_key=" + _toggleKey + " menu_key=" + _menuKey + " diagnostics=" + _diagnostics);
    }

    private static bool IsAssignment(string trimmed, string key)
    {
        if (!trimmed.StartsWith(key, StringComparison.Ordinal))
            return false;

        return trimmed.Substring(key.Length).TrimStart().StartsWith("=");
    }

    private static void SaveSetting(string key, string tomlValue)
    {
        try
        {
            string path = Path.Combine(_modDir, "mod.toml");
            var lines = File.Exists(path) ? new List<string>(File.ReadAllLines(path)) : new List<string>();

            int hudStart = -1;
            int keyLine = -1;

            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].Trim();

                if (trimmed.StartsWith("["))
                {
                    if (hudStart >= 0)
                        break;

                    if (trimmed.StartsWith("[hud]"))
                        hudStart = i;

                    continue;
                }

                if (hudStart >= 0 && keyLine < 0 && IsAssignment(trimmed, key))
                    keyLine = i;
            }

            string newLine = key + " = " + tomlValue;

            if (keyLine >= 0)
                lines[keyLine] = newLine;
            else if (hudStart >= 0)
                lines.Insert(hudStart + 1, newLine);
            else
            {
                lines.Add("");
                lines.Add("[hud]");
                lines.Add(newLine);
            }

            File.WriteAllLines(path, lines);
            _configStamp = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex)
        {
            Log("Could not save mod.toml: " + ex.Message);
        }
    }

    private static void SaveNum(string key, float value)
    {
        SaveSetting(key, value.ToString("0.0##", CultureInfo.InvariantCulture));
    }

    private static void LogThrottled(string text)
    {
        if (DateTime.UtcNow < _nextErrorLog)
            return;

        _nextErrorLog = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        Log(text);
    }

    private static void InitLog()
    {
        try
        {
            _logPath = Path.Combine(_modDir, "UniversalColorHUD.log");
            File.WriteAllText(_logPath, "UniversalColorHUD " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine);
        }
        catch
        {
            _logPath = "";
        }
    }

    private static void Log(string text)
    {
        Debug.Log("[UniversalColorHUD] " + text);

        if (string.IsNullOrEmpty(_logPath))
            return;

        try
        {
            File.AppendAllText(_logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " | " + text + Environment.NewLine);
        }
        catch
        {
        }
    }
}
