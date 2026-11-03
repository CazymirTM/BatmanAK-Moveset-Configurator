using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;
using Tomlyn.Model;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace CeZeY.OracleScript;

[Script]
public partial class Oracle : Script
{
    private const int ConfigPollTicks = 60;
    private const float MaintainGap = 0.25f;
    private const string OriginalsKey = "CeZeY.Oracle.Originals";

    private static Keys _menuKey = Keys.F3;
    private static bool _remember = true;
    private static float _applyDelay = 4f;
    private static bool _activeList;
    private static bool _diagnostics;
    private static readonly Dictionary<Keys, string> _hotkeys = new();

    private static bool _ready;
    private static float _applyLeft;
    private static bool _applied;
    private static float _maintainLeft;
    private static readonly Queue<Feature> _applyQueue = new();
    private static nint _lastPawn;
    private static nint _checkedPawn;
    private static bool _inFrontend;
    private static long _lastTick;
    private static double _clock;

    private static DateTime _configStamp = DateTime.MinValue;
    private static string _lastConfigError = "";
    private static DateTime _nextErrorLog = DateTime.MinValue;
    private static string _logPath = "";
    private static string _modDir = "";

    private static int _configTicks;

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
        BuildFeatures();
        EnsurePadSection();
        LoadConfig(true);
        SnapshotNgpp();
        InitMusic();
        ArmApply();
        Log("Loaded. " + Features.Count + " features, menu_key=" + _menuKey + " remember=" + _remember + " hotkeys=" + _hotkeys.Count);
    }

    public override void OnEnterGame()
    {
        CloseMenu();
        _frozenAt.Clear();
        _freezeList.Clear();
        _resized.Clear();
        _pdd = null;
        _batmobile = null;
        _tpPhase = TpIdle;
        _blackout = 0f;
        _counterAskLogged = false;
        if (!CharacterBusy)
        {
            RememberLeak();
            ResetCharacter();
        }
        ForgetRain();
        ResetAtmosphereCache();
        ArmApply();
    }

    public override void OnEnterMenu()
    {
        CloseMenu();
        _ready = false;
    }

    public override void OnUnload()
    {
        CloseMenu();
        RestorePadBindings("mod unloaded");

        try
        {
            UnfreezeAll();
        }
        catch
        {
        }

        RestoreCamera();
        RestoreGlide();
        StaminaOff();

        try
        {
            RestorePostProcess();
            RestoreRain();
        }
        catch
        {
        }

        try
        {
            _music?.Post(_music.Close);
        }
        catch
        {
        }
    }

    public override void OnKeyDown(Keys key)
    {
        if (!FallbackActive)
            HandleKey(key);
    }

    private static void HandleKey(Keys key)
    {
        KeyboardUsed();
        if (EntryKey(key))
            return;

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

            if (!_menuOpen && _hotkeys.TryGetValue(key, out string? id) && FeatureById.TryGetValue(id, out Feature? f))
                Hotkey(f);
        }
        catch (Exception ex)
        {
            Log("Key handling failed: " + ex.Message);
        }
    }

    public override void OnTick()
    {
        _sdkTick = Stopwatch.GetTimestamp();
        TickAll();
    }

    private static void TickAll()
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
            UpdateCheats(dt);
        }
        catch (Exception ex)
        {
            LogThrottled("Cheat update deferred safely: " + ex.Message);
        }

        try
        {
            PadGameTick();
        }
        catch (Exception ex)
        {
            LogThrottled("Controller map replay failed: " + ex.Message);
        }

        try
        {
            MenuTick(dt);
        }
        catch (Exception ex)
        {
            LogThrottled("Menu tick failed: " + ex.Message);
        }
    }

    private static void LeaveGame()
    {
        RememberLeak();
        if (PlayingOther || CharacterBusy)
            ResetCharacter();
        if (_musicPlaying || _track >= 0)
            MusicStop();
        if (RainChanged)
            ForgetRain();
        if (_cam.Ptr != 0)
            ForgetCamera();
        if (_effectOriginals.Count == 0 && _volumeOriginals.Count == 0)
            return;

        ResetCharacter();
        _ppMode = 0;
        RestorePostProcess();
        _applied = false;
        _applyLeft = _applyDelay;
    }

    private static void ArmApply()
    {
        _ready = false;
        _applied = false;
        _applyLeft = _applyDelay;
        _lastPawn = 0;
    }

    private static bool InGame()
    {
        try
        {
            RPlayerController pc = Game.GetPlayerController(0);
            if (pc == null || !pc.IsValid)
                return false;

            Pawn pawn = pc.Pawn;
            if (pawn == null || !pawn.IsValid)
                return false;

            if (pawn.Ptr != _checkedPawn)
            {
                _checkedPawn = pawn.Ptr;
                _inFrontend = pawn.GetPathName(null).StartsWith("frontend.", StringComparison.OrdinalIgnoreCase);
            }

            return !_inFrontend;
        }
        catch
        {
            return false;
        }
    }

    private static nint CurrentPawnPtr()
    {
        Pawn pawn = Game.GetPlayerController(0)?.Pawn;
        return pawn != null && pawn.IsValid ? pawn.Ptr : 0;
    }

    private static void UpdateCheats(float dt)
    {
        _ready = InGame();
        if (_ready && GuardLoadedCharacter())
            return;
        if (!_ready)
        {
            if (_inFrontend)
                LeaveGame();
            return;
        }

        if (!_applied)
        {
            _applyLeft -= dt;
            if (_applyLeft > 0f)
                return;

            _applied = true;
            _applyQueue.Clear();
            foreach (Feature f in Features)
            {
                if (f.Active && f.Kind != KindAction)
                    _applyQueue.Enqueue(f);
            }

            _lastPawn = CurrentPawnPtr();
            if (_applyQueue.Count > 0)
                Log("Switching " + _applyQueue.Count + " saved cheats back on.");
            return;
        }

        if (_applyQueue.Count > 0)
        {
            Feature next = _applyQueue.Dequeue();
            if (next.Active)
                Apply(next, "load");
        }

        foreach (Feature f in Features)
        {
            if (f.Fast && f.Active)
                SafeRun(f, f.Maintain!, "tick");
        }

        try
        {
            CharacterTick(dt);
            TeleportTick(dt);
            AtmoTick(dt);
            RainTick(dt);
            PostProcessTick();
            CameraTick();
        }
        catch (Exception ex)
        {
            LogThrottled("World update failed: " + ex.Message);
        }

        nint pawn = CurrentPawnPtr();
        bool newPawn = pawn != _lastPawn;
        _lastPawn = pawn;

        _maintainLeft -= dt;
        if (_maintainLeft > 0f && !newPawn)
            return;

        _maintainLeft = MaintainGap;
        foreach (Feature f in Features)
        {
            if (f.Maintain != null && f.Active && !f.Fast)
                SafeRun(f, f.Maintain, "maintain");
        }
    }

    private static void SafeRun(Feature f, Action a, string what)
    {
        try
        {
            a();
        }
        catch (Exception ex)
        {
            LogThrottled(f.Label + " " + what + " failed: " + ex.Message);
        }
    }

    private static void Apply(Feature f, string reason)
    {
        SafeRun(f, () => ApplyQuiet(f), "apply");
        Log(f.Label + " -> " + f.StateText() + " (" + reason + ")");
    }

    private static void ApplyQuiet(Feature f)
    {
        switch (f.Kind)
        {
            case KindToggle:
                f.Toggle?.Invoke(f.On);
                break;
            case KindSlider:
                f.Slide?.Invoke(f.Value);
                break;
            case KindChoice:
                f.Pick?.Invoke(f.Choice);
                break;
        }
    }

    private static void Changed(Feature f, string reason = "menu")
    {
        if (_ready && _applied)
            Apply(f, reason);
        else
            Log(f.Label + " -> " + f.StateText() + " (queued until the game is loaded)");

        SaveFeature(f);
    }

    private static void RunAction(Feature f)
    {
        if (!_ready)
        {
            Log(f.Label + " skipped: not in game.");
            return;
        }

        try
        {
            f.Run?.Invoke();
            Log(f.Label + " done.");
        }
        catch (Exception ex)
        {
            Log(f.Label + " failed: " + ex.Message);
        }
    }

    private static void Hotkey(Feature f)
    {
        switch (f.Kind)
        {
            case KindToggle:
                f.On = !f.On;
                Changed(f, "hotkey");
                ShowToast(f.Label, f.On ? "ON" : "OFF", f.On);
                break;
            case KindAction:
                if (f.Permanent)
                    return;
                RunAction(f);
                ShowToast(f.Label, "DONE", true);
                break;
            case KindChoice:
                f.Choice = (f.Choice + 1) % f.Choices!.Length;
                Changed(f, "hotkey");
                ShowToast(f.Label, f.Choices[f.Choice], f.Choice != 0);
                break;
            case KindSlider:
                f.Value = f.Value == f.Default ? f.HotValue : f.Default;
                Changed(f, "hotkey");
                ShowToast(f.Label, f.Format!(f.Value), f.Value != f.Default);
                break;
        }
    }

    private static void TurnAllOff()
    {
        foreach (Feature f in Features)
        {
            if (!f.Active || f.Kind == KindAction)
                continue;

            f.On = false;
            f.Value = f.Default;
            f.Choice = 0;
            Changed(f);
        }
    }

    private static Dictionary<string, string> Originals
    {
        get
        {
            if (AppDomain.CurrentDomain.GetData(OriginalsKey) is Dictionary<string, string> d)
                return d;

            var made = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AppDomain.CurrentDomain.SetData(OriginalsKey, made);
            return made;
        }
    }

    private static string Exec(string command)
    {
        RPlayerController pc = Game.GetPlayerController(0);
        if (pc == null || !pc.IsValid)
            return "";

        string result = pc.ConsoleCommand(new FString(command), false).ToString() ?? "";
        if (_diagnostics)
            Log("exec " + command + " => " + result.Trim());
        return result;
    }

    private static string GetProp(string cls, string prop)
    {
        return Exec("get " + cls + " " + prop).Trim();
    }

    private static void SetProp(string cls, string prop, string value)
    {
        Exec("set " + cls + " " + prop + " " + value);
    }

    private static void SetPropTracked(string cls, string prop, string value)
    {
        string key = cls + " " + prop;
        if (!Originals.ContainsKey(key))
        {
            string original = GetProp(cls, prop);
            if (original.Length > 0 && original.Length < 200 && !original.Contains("Unrecognized", StringComparison.OrdinalIgnoreCase))
            {
                Originals[key] = original;
                if (_diagnostics)
                    Log("Original " + key + " = " + original);
            }
        }

        SetProp(cls, prop, value);
    }

    private static void RestoreProp(string cls, string prop, string fallback)
    {
        string key = cls + " " + prop;
        if (!Originals.TryGetValue(key, out string? v))
        {
            if (GetProp(cls, prop) == fallback)
                return;
            v = fallback;
        }

        SetProp(cls, prop, v);
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

    private static TomlTable Section(TomlTable? root, string name)
    {
        return root != null && root.TryGetValue(name, out object? raw) && raw is TomlTable t ? t : new TomlTable();
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

        TomlTable general = Section(root, "general");
        _menuKey = System.Enum.TryParse(ReadString(general, "menu_key", "F3"), true, out Keys mk) ? mk : Keys.F3;
        _remember = ReadBool(general, "remember_cheats", true);
        _applyDelay = Math.Clamp(ReadFloat(general, "apply_delay", 4f), 0f, 30f);
        _activeList = ReadBool(general, "active_list", false);
        _diagnostics = ReadBool(general, "diagnostics", false);
        string theme = ReadString(general, "menu_color", "emerald");
        _theme = Math.Max(0, Array.FindIndex(ThemeKeys, k => k.Equals(theme, StringComparison.OrdinalIgnoreCase)));
        _rainbowSpeed = Math.Clamp(ReadFloat(general, "rainbow_speed", 1f), 0.25f, 3f);
        _lowEnd = ReadBool(general, "low_end_mode", false);
        _ngppSetting = ReadBool(general, "new_game_plus_plus", false);
        ReadPadConfig(Section(root, "gamepad"));

        TomlTable cheats = Section(root, "cheats");
        foreach (Feature f in Features)
        {
            if (f.Kind == KindAction)
                continue;

            bool wasActive = f.Active;
            string before = f.StateText();

            if (!_remember && first)
            {
                f.On = false;
                f.Value = f.Default;
                f.Choice = 0;
            }
            else
            {
                switch (f.Kind)
                {
                    case KindToggle:
                        f.On = ReadBool(cheats, f.Id, f.Default > 0.5f);
                        break;
                    case KindSlider:
                        f.Value = Math.Clamp(ReadFloat(cheats, f.Id, f.Default), f.Min, f.Max);
                        break;
                    case KindChoice:
                        string pick = ReadString(cheats, f.Id, f.ChoiceKeys![0]);
                        f.Choice = Math.Max(0, Array.FindIndex(f.ChoiceKeys!, k => k.Equals(pick, StringComparison.OrdinalIgnoreCase)));
                        break;
                }
            }

            if (!first && _applied && f.StateText() != before && (wasActive || f.Active))
                Apply(f, "mod.toml");
        }

        _hotkeys.Clear();
        TomlTable keys = Section(root, "hotkeys");
        foreach (KeyValuePair<string, object> kv in keys)
        {
            if (kv.Value is string keyName && keyName.Length > 0 && FeatureById.ContainsKey(kv.Key) && System.Enum.TryParse(keyName, true, out Keys k))
                _hotkeys[k] = kv.Key;
        }

        if (!first)
            Log("Settings reloaded from mod.toml.");
    }

    private static bool IsAssignment(string trimmed, string key)
    {
        if (!trimmed.StartsWith(key, StringComparison.Ordinal))
            return false;

        return trimmed.Substring(key.Length).TrimStart().StartsWith("=");
    }

    private static void SaveSetting(string section, string key, string tomlValue)
    {
        try
        {
            string path = Path.Combine(_modDir, "mod.toml");
            var lines = File.Exists(path) ? new List<string>(File.ReadAllLines(path)) : new List<string>();

            int start = -1;
            int end = lines.Count;
            int keyLine = -1;

            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].Trim();

                if (trimmed.StartsWith("["))
                {
                    if (start >= 0)
                    {
                        end = i;
                        break;
                    }

                    if (trimmed.StartsWith("[" + section + "]"))
                        start = i;

                    continue;
                }

                if (start >= 0 && keyLine < 0 && IsAssignment(trimmed, key))
                    keyLine = i;
            }

            string newLine = key + " = " + tomlValue;

            if (keyLine >= 0)
                lines[keyLine] = newLine;
            else if (start >= 0)
            {
                int at = end;
                while (at > start + 1 && lines[at - 1].Trim().Length == 0)
                    at--;
                lines.Insert(at, newLine);
            }
            else
            {
                lines.Add("");
                lines.Add("[" + section + "]");
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

    private static void SaveFeature(Feature f)
    {
        switch (f.Kind)
        {
            case KindToggle:
                SaveSetting("cheats", f.Id, f.On ? "true" : "false");
                break;
            case KindSlider:
                SaveSetting("cheats", f.Id, f.Value.ToString("0.0##", CultureInfo.InvariantCulture));
                break;
            case KindChoice:
                SaveSetting("cheats", f.Id, "\"" + f.ChoiceKeys![f.Choice] + "\"");
                break;
        }
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
            _logPath = Path.Combine(_modDir, "Oracle.log");
            File.WriteAllText(_logPath, "Oracle " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine);
        }
        catch
        {
            _logPath = "";
        }
    }

    private static void Log(string text)
    {
        Debug.Log("[Oracle] " + text);

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
