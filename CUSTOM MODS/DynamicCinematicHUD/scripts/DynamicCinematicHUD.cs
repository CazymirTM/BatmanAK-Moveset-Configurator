using System;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Framework;
using Tomlyn.Model;

namespace CeZeY.DynamicCinematicHudScript;

[Script]
public partial class DynamicCinematicHUD : Script
{
    private const int CtxTraversal = 1;
    private const int CtxFinisher = 2;
    private const int CtxCount = 2;
    private const int ConfigPollTicks = 60;
    private const int DetectIntervalTicks = 3;
    private const float MinTraversalSpeed = 120f;
    private const float SpecialMoveSpeed = 600f;
    private const float DiveSpeed = 1500f;
    private const float CutsceneCameraHold = 0.75f;

    private static readonly string[] CtxNames = { "TRAVERSAL", "FINISHER" };
    private static readonly char[] CtxLetters = { 'T', 'F' };

    private static bool _enabled = true;
    private static readonly bool[] _trigOn = { true, true };
    private static float _travDelay = 0.6f;
    private static float _linger = 1.0f;
    private static bool _instant;
    private static float _fadeOut = 0.6f;
    private static float _fadeIn = 0.45f;
    private static bool _camOn = true;
    private static float _fov;
    private static float _camDist;
    private static float _camHeight;
    private static float _blend = 4f;
    private static Keys _toggleKey = Keys.F7;
    private static Keys _menuKey = Keys.F2;
    private static bool _diag;

    private static int _rawCtx;
    private static int _activeCtx;
    private static bool _inCutscene;
    private static readonly bool[] _isActive = new bool[CtxCount];
    private static readonly float[] _held = new float[CtxCount];
    private static readonly float[] _lingerLeft = new float[CtxCount];
    private static float _camCutLeft;
    private static long _lastTick;
    private static float _travSpeed;
    private static string _moveName = "";
    private static string _finisherName = "";
    private static string _stateName = "";
    private static nint _pc0Ptr;
    private static Vector3 _lastPos;
    private static long _lastPosMs;
    private static bool _hasLastPos;
    private static string _lastDiag = "";
    private static string _lastMoveLogged = "";

    private static DateTime _configStamp = DateTime.MinValue;
    private static string _lastConfigError = "";
    private static DateTime _nextErrorLog = DateTime.MinValue;
    private static string _logPath = "";
    private static string _modDir = "";

    private int _configTicks;
    private int _detectTicks;
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
        Log("Loaded. enabled=" + _enabled + " camera=" + _camOn + " menu_key=" + _menuKey + " toggle_key=" + _toggleKey);
    }

    public override void OnEnterGame()
    {
        CloseMenu();
        _pc0Ptr = 0;
        _camHasLast = false;
        _hasLastPos = false;
        ResetContexts();
        ResetFades();
    }

    public override void OnUnload()
    {
        CloseMenu();

        try
        {
            RestoreHudNow();
        }
        catch
        {
        }

        try
        {
            UndoCameraOnUnload();
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
                SetEnabled(!_enabled);
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
            UpdateContexts(dt);
        }
        catch (Exception ex)
        {
            LogThrottled("Context update failed: " + ex.Message);
        }

        try
        {
            UpdateHud(dt);
        }
        catch (Exception ex)
        {
            LogThrottled("HUD fade deferred safely: " + ex.Message);
        }

        try
        {
            MenuTick(dt);
        }
        catch (Exception ex)
        {
            LogThrottled("Menu tick failed: " + ex.Message);
        }

        if (_diag && ++_diagTicks >= 30)
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

    private static void SetEnabled(bool on)
    {
        _enabled = on;
        SaveBool("general", "enabled", on);

        if (!on)
        {
            ResetContexts();
            _curFov = 0f;
            _curDist = 0f;
            _curHeight = 0f;
        }

        Log("Mod " + (on ? "ENABLED" : "DISABLED") + ".");
    }

    private static void ResetContexts()
    {
        _rawCtx = 0;
        _activeCtx = 0;
        _inCutscene = false;
        _camCutLeft = 0f;
        Array.Clear(_isActive, 0, _isActive.Length);
        Array.Clear(_held, 0, _held.Length);
        Array.Clear(_lingerLeft, 0, _lingerLeft.Length);
    }

    private void UpdateContexts(float dt)
    {
        if (!_enabled)
        {
            if (_rawCtx != 0 || _activeCtx != 0 || _inCutscene)
                ResetContexts();
            return;
        }

        if (++_detectTicks >= DetectIntervalTicks)
        {
            _detectTicks = 0;
            _rawCtx = Detect();
        }

        if (_inCutscene)
            _camCutLeft = CutsceneCameraHold;
        else if (_camCutLeft > 0f)
            _camCutLeft -= dt;

        int active = 0;
        for (int i = 0; i < CtxCount; i++)
        {
            int bit = 1 << i;
            bool trig = _trigOn[i] && (_rawCtx & bit) != 0;
            _held[i] = trig ? _held[i] + dt : 0f;
            float need = i == 0 ? _travDelay : 0f;

            if (trig && (_isActive[i] || _held[i] >= need))
            {
                _isActive[i] = true;
                _lingerLeft[i] = _linger;
            }
            else if (_isActive[i])
            {
                _lingerLeft[i] -= dt;
                if (_lingerLeft[i] <= 0f)
                    _isActive[i] = false;
            }

            if (_isActive[i])
                active |= bit;
        }

        _activeCtx = active;
    }

    private static bool IsFinisherMove(RCombatMove mv)
    {
        return mv is RCombatMove_BatmanComboTakedown
            || mv is RCombatMove_BatmanBeatdown
            || mv is RCombatMove_BatmanAerialToBeatdown
            || mv is RCombatMove_BatmanFloorTakedown
            || mv is RCombatMove_BatmanGroupFloorTakedown
            || mv is RCombatMove_BatmanEnvironmentAttack
            || mv is RCombatMove_BatmanBatmobileTakedown;
    }

    private static bool IsTraversalState(string st)
    {
        return st.Contains("Glide", StringComparison.OrdinalIgnoreCase)
            || st.Contains("Grapple", StringComparison.OrdinalIgnoreCase)
            || st.Contains("Swing", StringComparison.OrdinalIgnoreCase)
            || st.Contains("Zip", StringComparison.OrdinalIgnoreCase)
            || st.Contains("Dive", StringComparison.OrdinalIgnoreCase);
    }

    private static int Detect()
    {
        RPlayerController? pc = null;
        RPawnPlayer? pawn = null;

        try
        {
            pc = Game.GetPlayerController(0);
        }
        catch
        {
        }

        if (pc == null || !pc.IsValid)
        {
            _inCutscene = false;
            _finisherName = "";
            _moveName = "";
            _stateName = "";
            return 0;
        }

        _pc0Ptr = pc.Ptr;

        try
        {
            pawn = Game.GetPlayerPawn(0);
        }
        catch
        {
        }

        if (pawn != null && !pawn.IsValid)
            pawn = null;

        string st = "";
        try { st = pc.GetStateName().ToString() ?? ""; } catch { }
        _stateName = st;

        bool cut = st == "CinematicMode";
        if (!cut) { try { cut = pc.bCinematicMode || pc.bForceCinematicMode; } catch { } }
        if (!cut) { try { cut = pc.IsMatineePlaing(); } catch { } }
        if (!cut && pawn != null) { try { cut = pawn.bUsedByMatinee; } catch { } }
        if (!cut) { try { cut = pc.IsPlayingFullScreenMovie(); } catch { } }
        _inCutscene = cut;

        int mask = 0;

        string fin = "";
        string move = "";
        if (pawn != null)
        {
            try
            {
                RCombatMove? mv = pawn.CombatMove;
                if (mv != null && mv.IsValid)
                {
                    move = mv.GetType().Name;
                    if (IsFinisherMove(mv))
                        fin = move;
                }
            }
            catch
            {
            }

            if (fin.Length == 0)
            {
                try
                {
                    RStealthTakeDownStage? stage = pawn.CurrentStealthTakeDown;
                    if (stage != null && stage.IsValid)
                        fin = "StealthTakedown";
                }
                catch
                {
                }
            }
        }

        _moveName = move;
        _finisherName = fin;
        if (fin.Length > 0)
            mask |= CtxFinisher;

        if (_diag && move != _lastMoveLogged)
        {
            _lastMoveLogged = move;
            Log("MOVE -> " + (move.Length == 0 ? "(none)" : move) + (fin.Length > 0 ? "   [counts as finisher]" : ""));
        }

        _travSpeed = 0f;
        if (pawn != null)
        {
            try
            {
                long now = Environment.TickCount64;
                Vector3 p = pawn.Location;
                if (_hasLastPos && now > _lastPosMs)
                {
                    float seconds = (now - _lastPosMs) / 1000f;
                    if (seconds < 0.5f)
                        _travSpeed = Vector3.Distance(p, _lastPos) / seconds;
                }

                _lastPos = p;
                _lastPosMs = now;
                _hasLastPos = true;
            }
            catch
            {
                _hasLastPos = false;
            }
        }

        bool trav = false;
        try { trav = pawn != null && pawn.bGliding; } catch { }
        if (!trav)
            trav = IsTraversalState(st);

        if (!trav)
        {
            bool grapple = false;
            try { grapple = pc.bGrappleMoving || pc.bSwingGrapple; } catch { }
            trav = grapple && _travSpeed >= MinTraversalSpeed;
        }

        if (!trav)
            trav = st == "SpecialMove" && _travSpeed >= SpecialMoveSpeed;

        if (!trav)
            trav = st == "FallingMode" && _travSpeed >= DiveSpeed;

        if (trav)
            mask |= CtxTraversal;

        return mask;
    }

    private static TomlTable Section(TomlTable? root, string name)
    {
        return root != null && root.TryGetValue(name, out object? raw) && raw is TomlTable table ? table : new TomlTable();
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

    private static string MaskToString(int mask)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < CtxCount; i++)
        {
            if ((mask & (1 << i)) != 0)
                sb.Append(CtxLetters[i]);
        }

        return sb.ToString();
    }

    private static int ParseMask(string text)
    {
        int mask = 0;
        foreach (char ch in text.ToUpperInvariant())
        {
            int i = Array.IndexOf(CtxLetters, ch);
            if (i >= 0)
                mask |= 1 << i;
        }

        return mask;
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
        TomlTable triggers = Section(root, "triggers");
        TomlTable transition = Section(root, "transition");
        TomlTable elements = Section(root, "elements");
        TomlTable camera = Section(root, "camera");

        bool newEnabled = ReadBool(general, "enabled", true);
        _toggleKey = System.Enum.TryParse(ReadString(general, "toggle_key", "F7"), true, out Keys parsedToggle) ? parsedToggle : Keys.F7;
        _menuKey = System.Enum.TryParse(ReadString(general, "menu_key", "F2"), true, out Keys parsedMenu) ? parsedMenu : Keys.F2;
        _diag = ReadBool(general, "diagnostics", false);

        _trigOn[0] = ReadBool(triggers, "traversal", true);
        _trigOn[1] = ReadBool(triggers, "finisher", true);
        _travDelay = Math.Clamp(ReadFloat(triggers, "traversal_delay", 0.6f), 0f, 3f);
        _linger = Math.Clamp(ReadFloat(triggers, "linger", 1.0f), 0f, 5f);

        _instant = ReadString(transition, "style", "smooth").Trim().Equals("instant", StringComparison.OrdinalIgnoreCase);
        _fadeOut = Math.Clamp(ReadFloat(transition, "fade_out", 0.6f), 0.05f, 2f);
        _fadeIn = Math.Clamp(ReadFloat(transition, "fade_in", 0.45f), 0.05f, 2f);

        foreach (HudElement e in Elements)
            e.Mask = ParseMask(ReadString(elements, e.Key, MaskToString(e.DefaultMask)));

        _camOn = ReadBool(camera, "enabled", true);
        _fov = Math.Clamp(ReadFloat(camera, "fov", 0f), -40f, 60f);
        _camDist = Math.Clamp(ReadFloat(camera, "distance", 0f), -200f, 400f);
        _camHeight = Math.Clamp(ReadFloat(camera, "height", 0f), -100f, 200f);
        _blend = Math.Clamp(ReadFloat(camera, "blend_speed", 4f), 0.5f, 20f);

        if (_enabled != newEnabled)
        {
            _enabled = newEnabled;
            if (!newEnabled)
            {
                ResetContexts();
                _curFov = 0f;
                _curDist = 0f;
                _curHeight = 0f;
            }
        }

        Log("Settings: enabled=" + _enabled + " triggers=" + _trigOn[0] + "/" + _trigOn[1] +
            " travDelay=" + _travDelay + " linger=" + _linger + " style=" + (_instant ? "instant" : "smooth") +
            " fadeOut=" + _fadeOut + " fadeIn=" + _fadeIn + " camera=" + _camOn +
            " fov=" + _fov + " dist=" + _camDist + " height=" + _camHeight + " diagnostics=" + _diag);
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
            var lines = File.Exists(path) ? new System.Collections.Generic.List<string>(File.ReadAllLines(path)) : new System.Collections.Generic.List<string>();
            string header = "[" + section + "]";

            int sectionStart = -1;
            int keyLine = -1;

            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].Trim();

                if (trimmed.StartsWith("["))
                {
                    if (sectionStart >= 0)
                        break;

                    if (trimmed.StartsWith(header))
                        sectionStart = i;

                    continue;
                }

                if (sectionStart >= 0 && keyLine < 0 && IsAssignment(trimmed, key))
                    keyLine = i;
            }

            string newLine = key + " = " + tomlValue;

            if (keyLine >= 0)
                lines[keyLine] = newLine;
            else if (sectionStart >= 0)
                lines.Insert(sectionStart + 1, newLine);
            else
            {
                lines.Add("");
                lines.Add(header);
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

    private static void SaveBool(string section, string key, bool value)
    {
        SaveSetting(section, key, value ? "true" : "false");
    }

    private static void SaveNum(string section, string key, float value)
    {
        SaveSetting(section, key, value.ToString("0.0##", CultureInfo.InvariantCulture));
    }

    private static string CtxText(int mask)
    {
        if (mask == 0)
            return "idle";

        var parts = new System.Collections.Generic.List<string>();
        for (int i = 0; i < CtxCount; i++)
        {
            if ((mask & (1 << i)) != 0)
                parts.Add(CtxNames[i].ToLowerInvariant());
        }

        return string.Join("+", parts);
    }

    private static void PollDiagnostics()
    {
        var sb = new StringBuilder();
        sb.Append("CTX raw=").Append(CtxText(_rawCtx)).Append(" active=").Append(CtxText(_activeCtx));
        sb.Append(" cutscene=").Append(_inCutscene);
        sb.Append(" move='").Append(_moveName).Append("' finisher='").Append(_finisherName).Append('\'');
        sb.Append(" speed~").Append(((int)(_travSpeed / 100f) * 100).ToString(CultureInfo.InvariantCulture));
        sb.Append(" state='").Append(_stateName).Append('\'');
        sb.Append(" faded=[");
        foreach (HudElement e in Elements)
        {
            if (e.Level < 1f)
                sb.Append(e.Key).Append(' ');
        }

        sb.Append(']');
        if (_camErrors > 0)
            sb.Append(" camErrors=").Append(_camErrors).Append(" last='").Append(_camLastError).Append('\'');

        string text = sb.ToString();
        if (text == _lastDiag)
            return;

        _lastDiag = text;
        Log(text);
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
            _logPath = Path.Combine(_modDir, "DynamicCinematicHUD.log");
            File.WriteAllText(_logPath, "DynamicCinematicHUD " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine);
        }
        catch
        {
            _logPath = "";
        }
    }

    private static void Log(string text)
    {
        Debug.Log("[DynamicCinematicHUD] " + text);

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
