// Arkham Mod Menu client, protocol v1. Copy this file into your mod's scripts/ folder unchanged.
//
// It keeps your settings in <your mod>/Settings.ini and your presets in <your mod>/Presets/*.ini, and registers
// them with the Arkham Mod Menu framework when that mod is installed. Without the framework your mod still works:
// players edit Settings.ini by hand (changes are picked up within a second while you call Poll()).
//
//   static ModMenu s_menu = null!;
//   public override void Main() => Setup();
//   public override void OnLoad() => Setup();          // hot reload: register again
//   void Setup()
//   {
//       s_menu = new ModMenu(Mod, "hello", "Hello World")
//           .Page("General")
//           .Toggle("Enabled", "Enabled", true, "Turns the mod on or off.")
//           .Slider("Speed", "Speed", 1, 10, 1, 5, "{0}x", "How fast things happen.")
//           .Choice("Mode", "Mode", "calm", ["calm=Calm", "angry=Angry"], "Which mood.")
//           .Register();
//       s_menu.Changed += key => Debug.Log($"{key} is now {s_menu.Str(key)}");
//   }
//   public override void OnTick() => s_menu.Poll();
//
// The framework only draws and edits; the values live here. Everything crossing between mods uses BCL types
// (string, delegates, Dictionary) because every BmSDK mod is its own assembly.

#nullable enable
using System.Globalization;
using System.Text;

namespace ArkhamModMenu.Client;

public sealed class ModMenu
{
    public const string RegistryKey = "ArkhamModMenu.Registry.v1";

    public string Id { get; }
    public string Name { get; }
    public string Dir { get; }
    public string SettingsPath => Path.Combine(Dir, "Settings.ini");
    public string PresetsDir => Path.Combine(Dir, "Presets");

    /// <summary>Raised with the setting's key after it changed (menu, preset, reset or a hand edit of the ini).</summary>
    public event Action<string>? Changed;

    readonly List<string> _schema = new();
    readonly List<string> _order = new();
    readonly Dictionary<string, string> _defaults = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _help = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    DateTime _stamp;
    long _nextPoll;

    public ModMenu(Mod mod, string id, string name) : this(mod.DirectoryPath, id, name) { }

    public ModMenu(string modDir, string id, string name)
    {
        Dir = modDir;
        Id = id;
        Name = name;
    }

    // ---- schema

    string _title = "";

    /// <summary>Optional screen heading, two lines like the game's "GAME / OPTIONS": a small top line and a big
    /// bottom line. Without it the heading is the mod's name. Title("Hello", "World") or Title("Hello World").</summary>
    public ModMenu Title(string top, string bottom = "")
    {
        _title = bottom == "" ? top : top + "\n" + bottom;
        return this;
    }

    public ModMenu Page(string title) => Line("page", title);

    public ModMenu Header(string title) => Line("header", title);

    public ModMenu Toggle(string key, string label, bool def, string help = "") =>
        Setting(key, def ? "on" : "off", help, "toggle", key, label, help);

    /// <param name="format">How the value is shown, {0} is the number (e.g. "{0} m").</param>
    public ModMenu Slider(string key, string label, float min, float max, float step, float def, string format = "{0}",
        string help = "") =>
        Setting(key, Num(def), help, "slider", key, label, Num(min), Num(max), Num(step), format, help);

    /// <summary>A slider drawn as a segmented bar, like the game's volume settings.</summary>
    public ModMenu Bar(string key, string label, int segments, int def, string help = "") =>
        Setting(key, Num(def), help, "bar", key, label, "0", Num(segments), "1", "{0}", help);

    /// <param name="options">"value=Shown label" or just "value".</param>
    public ModMenu Choice(string key, string label, string def, string[] options, string help = "") =>
        Setting(key, def, help, "choice", key, label, string.Join(",", options), help);

    /// <summary>A button. When pressed, Changed fires with the key (the value isn't stored).</summary>
    public ModMenu Button(string key, string label, string help = "") => Line("action", key, label, help);

    ModMenu Setting(string key, string def, string help, params string[] fields)
    {
        _defaults[key] = def;
        _help[key] = help;
        if (!_order.Contains(key, StringComparer.OrdinalIgnoreCase)) _order.Add(key);
        return Line(fields);
    }

    ModMenu Line(params string[] fields)
    {
        _schema.Add(string.Join("|", fields.Select(f => f.Replace("|", "/"))));
        return this;
    }

    /// <summary>Loads Settings.ini and registers with the framework (if installed). Call after the schema.</summary>
    public ModMenu Register()
    {
        Load();
        try
        {
            var entry = new Dictionary<string, object>
            {
                ["name"] = Name,
                ["schema"] = string.Join("\n", _schema),
                ["get"] = new Func<string, string>(Str),
                ["set"] = new Action<string, string>(SetFromMenu),
                ["presets"] = new Func<string[]>(PresetNames),
                ["loadPreset"] = new Action<string>(LoadPreset),
                ["savePreset"] = new Func<string>(SavePreset),
                ["reset"] = new Action(ResetToDefaults),
                ["activePreset"] = new Func<string>(ActivePreset),
                ["title"] = _title,
            };
            var reg = Registry();
            lock (reg) reg[Id] = entry;
        }
        catch (Exception e)
        {
            Debug.LogError($"[{Name}] mod menu registration failed: {e.Message}");
        }

        return this;
    }

    static Dictionary<string, Dictionary<string, object>> Registry()
    {
        lock (typeof(AppDomain))
        {
            if (AppDomain.CurrentDomain.GetData(RegistryKey) is Dictionary<string, Dictionary<string, object>> reg)
                return reg;
            reg = new();
            AppDomain.CurrentDomain.SetData(RegistryKey, reg);
            return reg;
        }
    }

    // ---- values

    public string Str(string key) =>
        _values.TryGetValue(key, out var v) ? v : _defaults.TryGetValue(key, out var d) ? d : "";

    public bool Bool(string key) => Str(key).Trim().ToLowerInvariant() is "on" or "true" or "1" or "yes";

    public float Float(string key) =>
        float.TryParse(Str(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f
        : float.TryParse(_defaults.GetValueOrDefault(key, "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out f) ? f : 0f;

    public int Int(string key) => (int)MathF.Round(Float(key));

    public void Set(string key, string value)
    {
        if (Str(key) == value) return;
        _values[key] = value;
        Save();
        Raise(key);
    }

    public void Set(string key, bool value) => Set(key, value ? "on" : "off");

    public void Set(string key, float value) => Set(key, Num(value));

    void SetFromMenu(string key, string value)
    {
        if (!_defaults.ContainsKey(key)) { Raise(key); return; }   // an action button
        Set(key, value);
    }

    /// <summary>Re-reads Settings.ini when it was edited by hand. Cheap; call it every tick.</summary>
    public void Poll()
    {
        var now = Environment.TickCount64;
        if (now < _nextPoll) return;
        _nextPoll = now + 1000;
        try
        {
            if (File.Exists(SettingsPath) && File.GetLastWriteTimeUtc(SettingsPath) != _stamp) ReloadAndRaise(SettingsPath);
        }
        catch { }
    }

    void Raise(string key)
    {
        try { Changed?.Invoke(key); }
        catch (Exception e) { Debug.LogError($"[{Name}] Changed({key}) failed: {e}"); }
    }

    // ---- file

    void Load()
    {
        _values.Clear();
        if (File.Exists(SettingsPath))
        {
            Read(SettingsPath, _values);
            _stamp = File.GetLastWriteTimeUtc(SettingsPath);
        }

        // Write it once so new settings show up in the file with their help text.
        if (_order.Any(k => !_values.ContainsKey(k))) Save();
    }

    static void Read(string path, Dictionary<string, string> into)
    {
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw;
            var semi = line.IndexOf(';');
            if (semi >= 0) line = line[..semi];
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            into[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
    }

    void Save() => Write(SettingsPath);

    void Write(string path)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("; ").Append(Name).Append(" settings. Edit here or in the in-game Mods menu (Arkham Mod Menu).\n");
            foreach (var key in _order)
            {
                if (_help.TryGetValue(key, out var h) && h != "") sb.Append("\n; ").Append(h).Append('\n');
                else sb.Append('\n');
                sb.Append(key).Append(" = ").Append(Str(key)).Append('\n');
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, sb.ToString());
            if (path == SettingsPath) _stamp = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception e)
        {
            Debug.LogError($"[{Name}] couldn't write {path}: {e.Message}");
        }
    }

    void ReloadAndRaise(string fromPath)
    {
        var old = _order.ToDictionary(k => k, Str, StringComparer.OrdinalIgnoreCase);
        var read = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Read(fromPath, read);
        _values.Clear();
        foreach (var (k, v) in read) _values[k] = v;
        if (fromPath != SettingsPath) Save();
        else _stamp = File.GetLastWriteTimeUtc(SettingsPath);
        foreach (var key in _order)
            if (old[key] != Str(key))
                Raise(key);
    }

    // ---- presets

    public string[] PresetNames()
    {
        try
        {
            return Directory.Exists(PresetsDir)
                ? Directory.GetFiles(PresetsDir, "*.ini").Select(Path.GetFileNameWithoutExtension).OfType<string>()
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray()
                : [];
        }
        catch { return []; }
    }

    /// <summary>The preset whose values all match the current settings, or "" (changed since / none).</summary>
    public string ActivePreset()
    {
        foreach (var name in PresetNames())
        {
            try
            {
                var preset = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Read(Path.Combine(PresetsDir, name + ".ini"), preset);
                if (preset.Count > 0 && preset.All(kv => !_defaults.ContainsKey(kv.Key) || Str(kv.Key) == kv.Value)) return name;
            }
            catch { }
        }

        return "";
    }

    public void LoadPreset(string name)
    {
        var path = Path.Combine(PresetsDir, name + ".ini");
        if (!File.Exists(path)) return;
        ReloadAndRaise(path);
    }

    /// <summary>Saves the current settings as "Custom N"; returns the name.</summary>
    public string SavePreset()
    {
        var existing = PresetNames();
        var n = 1;
        while (existing.Contains($"Custom {n}", StringComparer.OrdinalIgnoreCase)) n++;
        var name = $"Custom {n}";
        Write(Path.Combine(PresetsDir, name + ".ini"));
        return name;
    }

    public void ResetToDefaults()
    {
        var old = _order.ToDictionary(k => k, Str, StringComparer.OrdinalIgnoreCase);
        _values.Clear();
        Save();
        foreach (var key in _order)
            if (old[key] != Str(key))
                Raise(key);
    }

    static string Num(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);
}
