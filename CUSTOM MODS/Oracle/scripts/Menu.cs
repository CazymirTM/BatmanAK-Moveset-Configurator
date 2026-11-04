using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;
using Canvas = BmSDK.Engine.Canvas;
using FLinearColor = BmSDK.GameObject.FLinearColor;
using GameViewportClient = BmSDK.Engine.GameViewportClient;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace CeZeY.OracleScript;

public partial class Oracle
{
    private const float RepeatDelay = 0.38f;
    private const float RepeatRate = 0.06f;
    private const float ToastTime = 1.8f;
    private const float ConfirmTime = 10f;
    private const float HeaderH = 34f;
    private const float RowH = 38f;
    private const int MaxRows = 12;
    private const string MenuOwnerKey = "CeZeY.ModMenuOwner";
    private const string MenuOwnerName = "Oracle";

    private readonly record struct Rgba(byte R, byte G, byte B, byte A = 255);

    private static readonly Rgba Panel = new(6, 10, 12, 238);
    private static readonly Rgba TextHi = new(240, 246, 244);
    private static readonly Rgba TextMid = new(176, 192, 188);
    private static readonly Rgba TextLow = new(110, 128, 124);
    private static readonly Rgba White = new(255, 255, 255);
    private static readonly Rgba Black = new(0, 0, 0);
    private static readonly Rgba Off = new(46, 56, 58);
    private static readonly Rgba Warn = new(255, 96, 82);

    private static readonly string[] ThemeKeys = { "emerald", "cyan", "blue", "violet", "magenta", "red", "orange", "gold", "white", "rainbow" };
    private static readonly string[] ThemeNames = { "EMERALD", "CYAN", "BLUE", "VIOLET", "MAGENTA", "RED", "ORANGE", "GOLD", "WHITE", "RAINBOW" };
    private static readonly Rgba[] ThemeColors =
    {
        new(54, 232, 162), new(64, 206, 255), new(84, 140, 255), new(158, 112, 255), new(240, 92, 220),
        new(255, 76, 76), new(255, 146, 48), new(255, 204, 64), new(226, 234, 238)
    };
    private const int ThemeRainbow = 9;
    private const float RainbowPeriod = 9f;
    private const float LowEndStep = 1f / 15f;

    private static int _theme;
    private static float _rainbowSpeed = 1f;
    private static bool _lowEnd;
    private static Rgba _accent = new(54, 232, 162);
    private static float _hue;
    private static float _hueStep;
    private static Rgba[]? _rainbow;
    private static readonly float[] _linear = BuildLinear();
    private static readonly Dictionary<(string, float), float> _textW = new();
    private static readonly Dictionary<float, float> _textH = new();
    private static float _textScale;

    private static Rgba Accent => _accent;

    private sealed class MenuRow
    {
        public string? Header;
        public Feature? F;
        public bool Setting;
    }

    private static readonly string[] PageNames = { "PLAYER", "COMBAT", "ENEMIES", "VISUAL", "CAMERA", "BATMOBILE", "WORLD", "CHARACTER", "MUSIC", "FUN", "SYSTEM" };
    private static List<MenuRow>[]? _pages;

    private static bool _menuOpen;
    private static float _menuAnim;
    private static int _page;
    private static int _menuIndex;
    private static float _selY = -1f;
    private static float _scrollY;
    private static float _tabX = -1f;
    private static float _tabW;
    private static nint _inputPc;
    private static readonly Dictionary<int, float> _anim = new();
    private static long _drawTick;
    private static float _toastLeft;
    private static string _toastTitle = "";
    private static string _toastState = "";
    private static bool _toastOn;
    private static Feature? _flashRow;
    private static float _flash;
    private static bool _flashOk;
    private static Feature? _confirmRow;
    private static float _confirmLeft;
    private static readonly Keys[] RepeatKeys = { Keys.Left, Keys.Right, Keys.Up, Keys.Down };
    private static readonly float[] _repeatHeld = { -1f, -1f, -1f, -1f };

    private static Texture2D? _white;
    private static Font? _font;
    private static float _s = 1f;
    private static float _ga = 1f;
    private static float _dt;
    private static double _uiClock;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static List<MenuRow>[] Pages => _pages ??= BuildPages();

    private static List<MenuRow>[] BuildPages()
    {
        var pages = new List<MenuRow>[PageNames.Length];
        for (int t = 0; t < pages.Length; t++)
            pages[t] = new List<MenuRow>();

        string[] lastGroup = new string[PageNames.Length];
        foreach (Feature f in Features)
        {
            if (f.Show != null && !f.Show())
                continue;

            List<MenuRow> rows = pages[f.Tab];
            if (f.Group != lastGroup[f.Tab])
            {
                lastGroup[f.Tab] = f.Group;
                rows.Add(new MenuRow { Header = f.Group });
            }

            rows.Add(new MenuRow { F = f });
        }

        List<MenuRow> sys = pages[PageNames.Length - 1];
        sys.Add(new MenuRow { Header = "BEHAVIOUR" });
        sys.Add(new MenuRow
        {
            Setting = true,
            F = new Feature
            {
                Id = "remember_cheats", Label = "REMEMBER CHEATS", Kind = KindToggle,
                Help = "Turns your cheats back on every time a save loads.",
                Toggle = on => { _remember = on; SaveSetting("general", "remember_cheats", on ? "true" : "false"); }
            }
        });
        sys.Add(new MenuRow
        {
            Setting = true,
            F = new Feature
            {
                Id = "apply_delay", Label = "DELAY AFTER LOADING", Kind = KindSlider, Min = 0f, Max = 15f, Step = 0.5f, Default = 4f,
                Help = "Seconds to wait after a load before your saved cheats switch back on.",
                Format = v => v.ToString("0.0", CultureInfo.InvariantCulture) + " s",
                Slide = v => { _applyDelay = v; SaveSetting("general", "apply_delay", v.ToString("0.0##", CultureInfo.InvariantCulture)); }
            }
        });
        sys.Add(new MenuRow { Header = "MENU LOOK" });
        sys.Add(new MenuRow
        {
            Setting = true,
            F = new Feature
            {
                Id = "menu_color", Label = "MENU COLOUR", Kind = KindChoice, ChoiceKeys = ThemeKeys, Choices = ThemeNames,
                Help = "Colour of the Oracle menu, pop-ups and teleport screen. RAINBOW cycles through every colour.",
                Pick = i => { _theme = i; SaveSetting("general", "menu_color", "\"" + ThemeKeys[i] + "\""); }
            }
        });
        sys.Add(new MenuRow
        {
            Setting = true,
            F = new Feature
            {
                Id = "rainbow_speed", Label = "RAINBOW SPEED", Kind = KindSlider, Min = 0.25f, Max = 3f, Step = 0.25f, Default = 1f,
                Help = "How fast the RAINBOW colour cycles. Only used when the menu colour is RAINBOW.",
                Format = Times,
                Slide = v => { _rainbowSpeed = v; SaveSetting("general", "rainbow_speed", v.ToString("0.0##", CultureInfo.InvariantCulture)); }
            }
        });
        sys.Add(new MenuRow
        {
            Setting = true,
            F = new Feature
            {
                Id = "low_end", Label = "LOW-END MODE", Kind = KindToggle,
                Help = "Lighter menu for slower PCs: the rainbow steps 15 times a second and the menu draws fewer effects.",
                Toggle = on => { _lowEnd = on; SaveSetting("general", "low_end_mode", on ? "true" : "false"); }
            }
        });
        sys.Add(new MenuRow { Header = "MAIN MENU" });
        sys.Add(new MenuRow
        {
            Setting = true,
            F = new Feature
            {
                Id = "new_game_plus_plus", Label = "NEW GAME ++", Kind = KindToggle,
                Help = "Adds NEW GAME ++ to the main menu once your save has a New Game + run: play New Game + again from the start. Restart the game after switching.",
                Toggle = on => { _ngppSetting = on; SaveSetting("general", "new_game_plus_plus", on ? "true" : "false"); }
            }
        });
        sys.Add(new MenuRow { Header = "CONTROLLER" });
        sys.Add(new MenuRow
        {
            Setting = true,
            F = new Feature
            {
                Id = "pad_enabled", Label = "CONTROLLER SUPPORT", Kind = KindToggle,
                Help = "Use an Xbox controller in the menu: hold the open button to open it, D-pad or left stick to move, A to pick, LB / RB for tabs, B to close.",
                Toggle = on => { _padEnabled = on; SaveSetting("gamepad", "enabled", on ? "true" : "false"); }
            }
        });
        sys.Add(new MenuRow
        {
            Setting = true,
            F = new Feature
            {
                Id = "pad_hold", Label = "HOLD TO OPEN", Kind = KindSlider, Min = 0f, Max = 1.5f, Step = 0.1f, Default = 0.5f,
                Help = "Seconds to hold the controller's open button (set in mod.toml, BACK by default). A shorter tap on BACK still opens the map.",
                Format = v => v <= 0f ? "PRESS" : v.ToString("0.0", CultureInfo.InvariantCulture) + " s",
                Slide = v => { _padOpenHold = v; SaveSetting("gamepad", "open_hold", v.ToString("0.0##", CultureInfo.InvariantCulture)); }
            }
        });
        sys.Add(new MenuRow { Header = "DISPLAY" });
        sys.Add(new MenuRow
        {
            Setting = true,
            F = new Feature
            {
                Id = "active_list", Label = "ACTIVE CHEATS LIST", Kind = KindToggle,
                Help = "Shows a small list of running cheats in the corner while you play.",
                Toggle = on => { _activeList = on; SaveSetting("general", "active_list", on ? "true" : "false"); }
            }
        });
        sys.Add(new MenuRow { Header = "RESET" });
        sys.Add(new MenuRow
        {
            Setting = true,
            F = new Feature
            {
                Id = "all_off", Label = "TURN EVERYTHING OFF", Kind = KindAction,
                Help = "Switches every cheat off and puts the game back to normal.",
                Run = TurnAllOff
            }
        });

        return pages;
    }

    private static void SyncSettingRows()
    {
        foreach (MenuRow r in Pages[PageNames.Length - 1])
        {
            if (r.F == null)
                continue;

            switch (r.F.Id)
            {
                case "remember_cheats":
                    r.F.On = _remember;
                    break;
                case "active_list":
                    r.F.On = _activeList;
                    break;
                case "apply_delay":
                    r.F.Value = _applyDelay;
                    break;
                case "menu_color":
                    r.F.Choice = _theme;
                    break;
                case "rainbow_speed":
                    r.F.Value = _rainbowSpeed;
                    break;
                case "low_end":
                    r.F.On = _lowEnd;
                    break;
                case "new_game_plus_plus":
                    r.F.On = _ngppSetting;
                    break;
                case "pad_enabled":
                    r.F.On = _padEnabled;
                    break;
                case "pad_hold":
                    r.F.Value = _padOpenHold;
                    break;
            }
        }
    }

    private static bool OwnsMenu()
    {
        return AppDomain.CurrentDomain.GetData(MenuOwnerKey) as string == MenuOwnerName;
    }

    private static void OpenMenu()
    {
        _menuOpen = true;
        SyncSettingRows();
        _menuIndex = NextSelectable(Pages[_page], -1, 1);
        _selY = -1f;
        _confirmRow = null;
        AppDomain.CurrentDomain.SetData(MenuOwnerKey, MenuOwnerName);

        try
        {
            RPlayerController pc = Game.GetPlayerController(0);
            if (pc != null && pc.IsValid)
            {
                pc.IgnoreMoveInput(true);
                _inputPc = pc.Ptr;
            }
        }
        catch
        {
        }
    }

    private static void CloseMenu()
    {
        _menuOpen = false;
        _confirmRow = null;
        Array.Fill(_repeatHeld, -1f);
        PadMenuClosed();

        if (OwnsMenu())
            AppDomain.CurrentDomain.SetData(MenuOwnerKey, null);

        if (_inputPc != 0)
        {
            try
            {
                RPlayerController pc = Game.GetPlayerController(0);
                if (pc != null && pc.IsValid && pc.Ptr == _inputPc)
                    pc.IgnoreMoveInput(false);
            }
            catch
            {
            }
        }

        _inputPc = 0;
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.OpenLevelUpOrMap))]
    private static void OpenLevelUpOrMapRedirect(RPlayerController self)
    {
        if (_menuOpen || PadDefersMap())
            return;

        self.OpenLevelUpOrMap();
    }

    private static int NextSelectable(List<MenuRow> rows, int from, int dir)
    {
        int i = from;
        for (int guard = 0; guard < rows.Count; guard++)
        {
            i = (i + dir + rows.Count) % rows.Count;
            if (rows[i].F != null)
                return i;
        }

        return 0;
    }

    private static bool HandleMenuKey(Keys key, bool repeat)
    {
        List<MenuRow> rows = Pages[_page];
        _menuIndex = Math.Clamp(_menuIndex, 0, rows.Count - 1);
        MenuRow row = rows[_menuIndex];
        Feature? f = row.F;

        if (_confirmRow != null)
        {
            if (repeat)
                return true;
            if (key == Keys.Enter && f == _confirmRow)
                Activate(row);
            else if (key != Keys.Enter)
                _confirmRow = null;
            return true;
        }

        if (!repeat)
        {
            int r = Array.IndexOf(RepeatKeys, key);
            if (r >= 0)
                _repeatHeld[r] = 0f;
        }

        switch (key)
        {
            case Keys.Up:
            case Keys.Down:
                _menuIndex = NextSelectable(rows, _menuIndex, key == Keys.Up ? -1 : 1);
                _confirmRow = null;
                return true;
            case Keys.Left:
            case Keys.Right:
                if (f == null || (repeat && f.Kind != KindSlider))
                    return true;
                Adjust(row, key == Keys.Left ? -1 : 1);
                return true;
            case Keys.Enter:
                if (f == null)
                    return true;
                if (f.Kind == KindToggle || f.Kind == KindChoice)
                    Adjust(row, 1);
                else if (f.Kind == KindAction)
                    Activate(row);
                else if (f.Kind == KindSlider)
                {
                    f.Value = f.Default;
                    Commit(row);
                }
                return true;
            case Keys.Tab:
            case Keys.PageDown:
                SwitchPage(1);
                return true;
            case Keys.PageUp:
                SwitchPage(-1);
                return true;
            case Keys.Back:
                CloseMenu();
                return true;
        }

        return false;
    }

    private static void SwitchPage(int delta)
    {
        _page = (_page + delta + Pages.Length) % Pages.Length;
        _menuIndex = NextSelectable(Pages[_page], -1, 1);
        _selY = -1f;
        _scrollY = 0f;
        _confirmRow = null;
        SyncSettingRows();
    }

    private static void Commit(MenuRow row)
    {
        Feature f = row.F!;
        if (row.Setting)
        {
            if (f.Kind == KindToggle)
                f.Toggle?.Invoke(f.On);
            else if (f.Kind == KindSlider)
                f.Slide?.Invoke(f.Value);
            else if (f.Kind == KindChoice)
                f.Pick?.Invoke(f.Choice);
            return;
        }

        Changed(f);
    }

    private static void Adjust(MenuRow row, int dir)
    {
        Feature f = row.F!;
        switch (f.Kind)
        {
            case KindToggle:
                f.On = !f.On;
                Commit(row);
                break;
            case KindSlider:
                float v = MathF.Round(Math.Clamp(f.Value + dir * f.Step, f.Min, f.Max) / f.Step) * f.Step;
                v = MathF.Round(v, 3);
                if (MathF.Abs(v - f.Value) < 0.0001f)
                    return;
                f.Value = v;
                Commit(row);
                break;
            case KindChoice:
                f.Choice = (f.Choice + dir + f.Choices!.Length) % f.Choices.Length;
                Commit(row);
                break;
        }
    }

    private static void Activate(MenuRow row)
    {
        Feature f = row.F!;
        if ((f.Permanent || f.Confirm) && _confirmRow != f)
        {
            _confirmRow = f;
            _confirmLeft = ConfirmTime;
            return;
        }

        _confirmRow = null;
        bool ok = true;
        if (row.Setting)
            f.Run?.Invoke();
        else
            ok = RunActionChecked(f);

        _flashRow = f;
        _flash = 1f;
        _flashOk = ok;
    }

    private static bool RunActionChecked(Feature f)
    {
        if (!_ready)
        {
            Log(f.Label + " skipped: not in game.");
            return false;
        }

        try
        {
            f.Run?.Invoke();
            Log(f.Label + " done.");
            return true;
        }
        catch (Exception ex)
        {
            Log(f.Label + " failed: " + ex.Message);
            return false;
        }
    }

    private static void MenuTick(float dt)
    {
        if (!_menuOpen)
            return;

        if (!OwnsMenu())
        {
            CloseMenu();
            return;
        }

        for (int i = 0; i < RepeatKeys.Length; i++)
        {
            if (_repeatHeld[i] < 0f)
                continue;

            if ((GetAsyncKeyState((int)RepeatKeys[i]) & 0x8000) == 0)
            {
                _repeatHeld[i] = -1f;
                continue;
            }

            _repeatHeld[i] += dt;
            if (_repeatHeld[i] >= RepeatDelay)
            {
                _repeatHeld[i] -= RepeatRate;
                HandleMenuKey(RepeatKeys[i], true);
            }
        }
    }

    private static void ShowNotice(string title, string state, bool on)
    {
        _toastTitle = title;
        _toastState = state;
        _toastOn = on;
        _toastLeft = ToastTime + 1.2f;
    }

    private static void ShowToast(string title, string state, bool on)
    {
        if (_menuOpen)
            return;

        _toastTitle = title;
        _toastState = state;
        _toastOn = on;
        _toastLeft = ToastTime;
    }

    private static bool GamePaused()
    {
        try
        {
            RPlayerController pc = Game.GetPlayerController(0);
            return pc != null && pc.IsValid && pc.IsPaused();
        }
        catch
        {
            return false;
        }
    }

    private static int ActiveCount(int tab = -1)
    {
        int n = 0;
        foreach (Feature f in Features)
        {
            if (f.Active && (tab < 0 || f.Tab == tab))
                n++;
        }

        return n;
    }

    [Redirect(typeof(GameViewportClient), "PostRender")]
    private static void ViewportPostRenderRedirect(GameViewportClient self, Canvas canvas)
    {
        self.PostRender(canvas);

        try
        {
            MusicFrame(GamePaused());
        }
        catch (Exception ex)
        {
            LogThrottled("Music update failed: " + ex.Message);
        }

        try
        {
            PadFrame();
        }
        catch (Exception ex)
        {
            LogThrottled("Controller update failed: " + ex.Message);
        }

        try
        {
            FallbackTick();
        }
        catch (Exception ex)
        {
            LogThrottled("Fallback tick failed: " + ex.Message);
        }

        try
        {
            DrawOverlay(canvas);
        }
        catch
        {
        }
    }

    private static void DrawOverlay(Canvas? c)
    {
        bool paused = GamePaused();
        if (_menuOpen && (paused || !OwnsMenu()))
        {
            CloseMenu();
            _menuAnim = 0f;
        }

        long now = Stopwatch.GetTimestamp();
        _dt = _drawTick == 0 ? 0f : Math.Clamp((float)Stopwatch.GetElapsedTime(_drawTick, now).TotalSeconds, 0f, 0.1f);
        _drawTick = now;
        _uiClock += _dt;
        UpdateAccent();

        float target = _menuOpen ? 1f : 0f;
        float speed = _menuOpen ? 1f / 0.2f : 1f / 0.14f;
        _menuAnim = _menuAnim < target ? Math.Min(target, _menuAnim + _dt * speed) : Math.Max(target, _menuAnim - _dt * speed);
        if (_toastLeft > 0f)
            _toastLeft -= _dt;
        if (_confirmLeft > 0f)
        {
            _confirmLeft -= _dt;
            if (_confirmLeft <= 0f)
                _confirmRow = null;
        }

        bool list = _activeList && !_menuOpen && !paused && _ready && ActiveCount() > 0;
        bool readout = FovReadoutVisible && !paused && _blackout <= 0f && _menuAnim <= 0f;
        if ((_menuAnim <= 0f && _toastLeft <= 0f && !list && !readout && _blackout <= 0f) || c == null)
            return;

        if (_white == null || !_white.IsValid)
            _white = Game.FindObject<Texture2D>("EngineResources.WhiteSquareTexture");
        if (_font == null || !_font.IsValid)
            _font = Game.FindObject<Font>("BmFonts.EngineSmallFont");
        if (_white == null)
            return;

        _s = Math.Clamp(c.ClipY / 1080f, 0.75f, 2.5f);
        Font? oldFont = c.Font;
        if (_font != null)
            c.Font = _font;

        try
        {
            if (_blackout > 0f)
            {
                _ga = 1f;
                Rect(c, 0f, 0f, c.ClipX, c.ClipY, Black, Math.Clamp(_blackout, 0f, 1f));
                if (TeleportBusy || CharacterBusy)
                    DrawRelocating(c);
            }

            if (_menuAnim > 0f)
            {
                DrawMenu(c);
                if (_confirmRow != null && _menuOpen)
                    DrawConfirm(c);
            }
            else if (list)
                DrawActiveList(c);

            if (readout)
                DrawFovReadout(c);

            if (_toastLeft > 0f)
                DrawToast(c);
        }
        finally
        {
            c.Font = oldFont;
        }
    }

    private static float S(float v) => v * _s;

    private static float EaseOut(float t) => 1f - MathF.Pow(1f - Math.Clamp(t, 0f, 1f), 3f);

    private static float Approach(int key, float target, float rate)
    {
        if (!_anim.TryGetValue(key, out float v))
            v = target;
        v += (target - v) * (1f - MathF.Exp(-rate * _dt));
        if (MathF.Abs(target - v) < 0.001f)
            v = target;
        _anim[key] = v;
        return v;
    }

    private static Rgba Mix(Rgba a, Rgba b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new Rgba((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t), (byte)(a.A + (b.A - a.A) * t));
    }

    private static float Linear(byte v) => _linear[v];

    private static float[] BuildLinear()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++)
            t[i] = MathF.Pow(i / 255f, 2.2f);
        return t;
    }

    private static Rgba[] BuildRainbow()
    {
        var t = new Rgba[360];
        for (int i = 0; i < t.Length; i++)
        {
            float h = i / 60f;
            float x = 1f - MathF.Abs(h % 2f - 1f);
            (float r, float g, float b) = (int)h switch
            {
                0 => (1f, x, 0f),
                1 => (x, 1f, 0f),
                2 => (0f, 1f, x),
                3 => (0f, x, 1f),
                4 => (x, 0f, 1f),
                _ => (1f, 0f, x)
            };
            const float lo = 0.27f;
            t[i] = new Rgba((byte)(255f * (lo + (1f - lo) * r)), (byte)(255f * (lo + (1f - lo) * g)), (byte)(255f * (lo + (1f - lo) * b)));
        }

        return t;
    }

    private static void UpdateAccent()
    {
        if (_theme != ThemeRainbow)
        {
            _accent = ThemeColors[Math.Clamp(_theme, 0, ThemeColors.Length - 1)];
            return;
        }

        _rainbow ??= BuildRainbow();
        _hue = (_hue + _dt * _rainbowSpeed / RainbowPeriod) % 1f;
        if (_lowEnd)
        {
            _hueStep -= _dt;
            if (_hueStep > 0f)
                return;
            _hueStep = LowEndStep;
        }

        _accent = _rainbow[(int)(_hue * _rainbow.Length) % _rainbow.Length];
    }

    private static Rgba ThemeSwatch(int pick)
    {
        if (pick != ThemeRainbow)
            return ThemeColors[Math.Clamp(pick, 0, ThemeColors.Length - 1)];
        return _theme == ThemeRainbow ? _accent : (_rainbow ??= BuildRainbow())[(int)(_uiClock * 40.0) % 360];
    }

    private static void Rect(Canvas c, float x, float y, float w, float h, Rgba col, float alpha = 1f)
    {
        if (w <= 0f || h <= 0f)
            return;

        c.SetPos(MathF.Round(x), MathF.Round(y), 0f);
        var lc = new FLinearColor { R = Linear(col.R), G = Linear(col.G), B = Linear(col.B), A = col.A / 255f * alpha * _ga };
        c.DrawTile(_white!, MathF.Round(w), MathF.Round(h), 0f, 0f, 32f, 32f, lc, false, EngineTypes.EBlendMode.BLEND_Translucent);
    }

    private static void Frame(Canvas c, float x, float y, float w, float h, float t, Rgba col, float alpha = 1f)
    {
        Rect(c, x, y, w, t, col, alpha);
        Rect(c, x, y + h - t, w, t, col, alpha);
        Rect(c, x, y + t, t, h - 2f * t, col, alpha);
        Rect(c, x + w - t, y + t, t, h - 2f * t, col, alpha);
    }

    private static void HGradient(Canvas c, float x, float y, float w, float h, Rgba col, float a0, float a1)
    {
        int slices = _lowEnd ? 8 : 32;
        for (int i = 0; i < slices; i++)
        {
            float x0 = MathF.Round(x + w * i / slices);
            float x1 = MathF.Round(x + w * (i + 1) / slices);
            float t = (i + 0.5f) / slices;
            Rect(c, x0, y, x1 - x0, h, col, a0 + (a1 - a0) * t);
        }
    }

    private static void VGradient(Canvas c, float x, float y, float w, float h, Rgba col, float a0, float a1)
    {
        int slices = _lowEnd ? 6 : 24;
        for (int i = 0; i < slices; i++)
        {
            float y0 = MathF.Round(y + h * i / slices);
            float y1 = MathF.Round(y + h * (i + 1) / slices);
            float t = (i + 0.5f) / slices;
            Rect(c, x, y0, w, y1 - y0, col, a0 + (a1 - a0) * t);
        }
    }

    private static float TextW(Canvas c, string text, float scale)
    {
        CheckTextCache();
        if (_textW.TryGetValue((text, scale), out float w))
            return w;

        c.TextSize(text, out w, out _, scale * _s, scale * _s);
        _textW[(text, scale)] = w;
        return w;
    }

    private static float TextH(Canvas c, float scale)
    {
        CheckTextCache();
        if (_textH.TryGetValue(scale, out float h))
            return h;

        c.TextSize("AQ", out _, out h, scale * _s, scale * _s);
        _textH[scale] = h;
        return h;
    }

    private static void CheckTextCache()
    {
        if (_s == _textScale && _textW.Count < 4096)
            return;

        _textW.Clear();
        _textH.Clear();
        _textScale = _s;
    }

    private static float Text(Canvas c, float x, float y, string text, Rgba col, float scale, float alpha = 1f)
    {
        c.SetDrawColor(col.R, col.G, col.B, (byte)Math.Clamp(col.A * alpha * _ga, 0f, 255f));
        c.SetPos(MathF.Round(x), MathF.Round(y), 0f);
        c.DrawText(text, false, scale * _s, scale * _s, out _);
        return TextW(c, text, scale);
    }

    private static float TextMiddle(Canvas c, float x, float cy, string text, Rgba col, float scale, float alpha = 1f)
    {
        return Text(c, x, cy - TextH(c, scale) * 0.5f, text, col, scale, alpha);
    }

    private static float Spaced(Canvas c, float x, float y, string text, Rgba col, float scale, float spacing, float alpha = 1f)
    {
        float start = x;
        foreach (char ch in text)
            x += Text(c, x, y, ch.ToString(), col, scale, alpha) + S(spacing);

        return x - start - S(spacing);
    }

    private static float SpacedW(Canvas c, string text, float scale, float spacing)
    {
        float w = 0f;
        foreach (char ch in text)
            w += TextW(c, ch.ToString(), scale) + S(spacing);
        return text.Length == 0 ? 0f : w - S(spacing);
    }

    private static string FitLine(Canvas c, string text, float width, float scale, out string line)
    {
        if (TextW(c, text, scale) <= width)
        {
            line = text;
            return "";
        }

        int cut = text.Length;
        while (cut > 0)
        {
            int space = text.LastIndexOf(' ', cut - 1);
            if (space <= 0)
                break;
            cut = space;
            if (TextW(c, text.Substring(0, cut), scale) <= width)
                break;
        }

        line = text.Substring(0, cut);
        return text.Substring(cut).TrimStart();
    }

    private static float RowHeight(MenuRow r) => r.F == null ? S(HeaderH) : S(RowH);

    private static float BodyHeight(List<MenuRow> rows)
    {
        float h = 0f;
        foreach (MenuRow r in rows)
            h += RowHeight(r);
        return h;
    }

    private static void DrawMenu(Canvas c)
    {
        List<MenuRow>[] pages = Pages;
        List<MenuRow> rows = pages[_page];
        _menuIndex = Math.Clamp(_menuIndex, 0, rows.Count - 1);
        Rgba accent = Accent;

        float e = EaseOut(_menuAnim);
        _ga = e;

        float maxBody = Math.Min(S(MaxRows * RowH + 2 * HeaderH), c.ClipY - S(330));
        float tallest = 0f;
        foreach (List<MenuRow> p in pages)
            tallest = Math.Max(tallest, BodyHeight(p));
        float bodyH = Math.Min(tallest, maxBody);
        float contentH = BodyHeight(rows);

        float w = S(840);
        float pad = S(32);
        float headH = S(150);
        float footH = S(96);
        float h = headH + bodyH + S(10) + footH;
        float x = S(72) - S(30) * (1f - e);
        float y = MathF.Max(S(30), (c.ClipY - h) * 0.5f);
        float right = x + w - pad;
        float hx = x + pad;

        Rect(c, x + S(8), y + S(10), w, h, Black, 0.35f);
        Rect(c, x, y, w, h, Panel);
        VGradient(c, x, y, w, S(140), accent, 0.12f, 0f);
        Rect(c, x, y, w, S(1), accent, 0.55f);
        Rect(c, x, y + h - S(1), w, S(1), White, 0.06f);
        Rect(c, x, y, S(3), h, accent, 0.9f);
        Rect(c, x + w - S(18), y, S(18), S(2), accent);
        Rect(c, x + w - S(2), y, S(2), S(18), accent);
        Rect(c, x + w - S(18), y + h - S(2), S(18), S(2), accent, 0.6f);
        Rect(c, x + w - S(2), y + h - S(18), S(2), S(18), accent, 0.6f);

        if (!_lowEnd)
        {
            float sweep = (float)(_uiClock % 3.2 / 3.2);
            float sy = y + S(4) + sweep * (headH - S(24));
            HGradient(c, x + S(3), sy, w - S(3), S(1), accent, 0.0f, 0.22f * (1f - sweep));
        }

        float lineY = y + S(20);
        float tagW = Spaced(c, hx, lineY, "ORACLE NETWORK  //  SECURE LINK", accent, 0.68f, 3f, 0.9f);
        if ((_uiClock % 1.0) < 0.55)
            Rect(c, hx + tagW + S(8), lineY + S(1), S(8), TextH(c, 0.68f) - S(2), accent, 0.9f);

        float titleY = y + S(38);
        float titleW = Spaced(c, hx, titleY, "ORACLE", TextHi, 1.9f, 8f);
        Rect(c, hx + titleW + S(16), titleY + S(8), S(1), TextH(c, 1.9f) - S(16), accent, 0.6f);
        Spaced(c, hx + titleW + S(30), titleY + TextH(c, 1.9f) * 0.5f - TextH(c, 0.9f) * 0.5f, "CHEAT TERMINAL", TextMid, 0.9f, 3f);

        int active = ActiveCount();
        string chip = active == 0 ? "STANDBY" : active + " ACTIVE";
        Rgba chipCol = active > 0 ? accent : TextLow;
        float chipW = SpacedW(c, chip, 0.72f, 2f) + S(40);
        float chipX = right - chipW;
        float chipY = y + S(34);
        Rect(c, chipX, chipY, chipW, S(24), chipCol, active > 0 ? 0.14f : 0.06f);
        Frame(c, chipX, chipY, chipW, S(24), S(1), chipCol, 0.75f);
        float pulse = active > 0 ? 0.55f + 0.45f * MathF.Sin((float)_uiClock * 4f) : 0.6f;
        Rect(c, chipX + S(10), chipY + S(9), S(6), S(6), chipCol, pulse);
        Spaced(c, chipX + S(26), chipY + S(12) - TextH(c, 0.72f) * 0.5f, chip, chipCol, 0.72f, 2f);

        DrawTabs(c, hx, right, y + S(104), y + headH - S(14), accent);

        float by = y + headH;
        float target = 0f;
        float ry = 0f;
        for (int i = 0; i < rows.Count; i++)
        {
            if (i == _menuIndex)
                target = ry;
            ry += RowHeight(rows[i]);
        }

        float wantScroll = _scrollY;
        if (contentH <= bodyH)
            wantScroll = 0f;
        else
        {
            float headerAbove = _menuIndex > 0 && rows[_menuIndex - 1].F == null ? S(HeaderH) : 0f;
            if (target - headerAbove < wantScroll)
                wantScroll = target - headerAbove;
            if (target + S(RowH) > wantScroll + bodyH)
                wantScroll = target + S(RowH) - bodyH;
            wantScroll = Math.Clamp(wantScroll, 0f, contentH - bodyH);
        }

        _scrollY += (wantScroll - _scrollY) * (1f - MathF.Exp(-18f * _dt));
        if (MathF.Abs(wantScroll - _scrollY) < 0.5f)
            _scrollY = wantScroll;

        float selTarget = by + target - _scrollY;
        _selY = _selY < 0f ? selTarget : _selY + (selTarget - _selY) * (1f - MathF.Exp(-22f * _dt));
        float selTop = Math.Max(_selY, by);
        float selBottom = Math.Min(_selY + S(RowH), by + bodyH);
        if (selBottom > selTop)
        {
            HGradient(c, x + S(3), selTop, w - S(3), selBottom - selTop, accent, 0.22f, 0.0f);
            Rect(c, x, selTop, S(3), selBottom - selTop, TextHi);
        }

        ry = by - _scrollY;
        for (int i = 0; i < rows.Count; i++)
        {
            MenuRow row = rows[i];
            float rh = RowHeight(row);
            float top = ry;
            ry += rh;

            float vis = Math.Min(1f, Math.Min((top + rh - by) / S(14), (by + bodyH - top) / S(14)));
            if (vis <= 0f)
                continue;
            if (top < by - S(2) || top + rh > by + bodyH + S(2))
                vis = Math.Min(vis, 0.35f);

            float ga = _ga;
            _ga *= Math.Clamp(vis, 0f, 1f);
            if (row.F == null)
                DrawHeaderRow(c, row.Header!, hx, right, top, accent);
            else
                DrawItem(c, row, i, hx, right, top, accent);
            _ga = ga;
        }

        if (contentH > bodyH)
        {
            float trackX = x + w - S(10);
            float thumbH = Math.Max(S(24), bodyH * bodyH / contentH);
            float thumbY = by + (bodyH - thumbH) * (_scrollY / (contentH - bodyH));
            Rect(c, trackX, by, S(2), bodyH, White, 0.06f);
            Rect(c, trackX, thumbY, S(2), thumbH, accent, 0.8f);
        }

        float fy = by + bodyH + S(10);
        Rect(c, hx, fy, right - hx, S(1), White, 0.08f);
        Feature? sel = rows[_menuIndex].F;
        string help = sel == null ? "" : sel.Help;
        if (sel != null && sel == _confirmRow)
            help = sel.Permanent ? "This changes your save file and can't be undone." : sel.Help;
        Rgba helpCol = sel != null && sel == _confirmRow ? Warn : TextMid;
        string rest = FitLine(c, help, right - hx, 0.85f, out string first);
        Text(c, hx, fy + S(rest.Length > 0 ? 8 : 14), first, helpCol, 0.85f, 0.95f);
        if (rest.Length > 0)
            Text(c, hx, fy + S(28), FitLine(c, rest, right - hx, 0.85f, out string second).Length > 0 ? second + "..." : second, helpCol, 0.85f, 0.95f);

        float kx = hx;
        float ky = fy + S(58);
        if (UsingPad)
        {
            kx = KeyHint(c, kx, ky, "D-PAD", "SELECT / ADJUST");
            kx = KeyHint(c, kx, ky, "A", "TOGGLE / RUN");
            kx = KeyHint(c, kx, ky, "LB / RB", "TABS");
            KeyHint(c, kx, ky, "B", "CLOSE");
            return;
        }

        kx = KeyHint(c, kx, ky, "UP / DOWN", "SELECT");
        kx = KeyHint(c, kx, ky, "LEFT / RIGHT", "ADJUST");
        kx = KeyHint(c, kx, ky, "ENTER", "TOGGLE / RUN");
        kx = KeyHint(c, kx, ky, "TAB", "NEXT TAB");
        KeyHint(c, kx, ky, _menuKey.ToString().ToUpperInvariant(), "CLOSE");
    }

    private static void DrawTabs(Canvas c, float hx, float right, float ty, float lineY, Rgba accent)
    {
        float scale = 0.84f;
        float spacing = 2f;
        float gap = S(28);
        float total = 0f;
        var counts = new int[PageNames.Length];
        for (int i = 0; i < PageNames.Length; i++)
        {
            counts[i] = i < PageNames.Length - 1 ? ActiveCount(i) : 0;
            total += SpacedW(c, PageNames[i], scale, spacing) + (counts[i] > 0 ? TextW(c, counts[i].ToString(), 0.66f) + S(14) : 0f);
        }

        float avail = right - hx;
        float minGaps = S(12) * (PageNames.Length - 1);
        if (total + minGaps > avail)
        {
            float k = Math.Max(0.6f, (avail - minGaps) / total);
            scale *= k;
            spacing *= k;
            total = 0f;
            for (int i = 0; i < PageNames.Length; i++)
                total += SpacedW(c, PageNames[i], scale, spacing) + (counts[i] > 0 ? TextW(c, counts[i].ToString(), 0.66f) + S(14) : 0f);
        }

        gap = Math.Clamp((avail - total) / (PageNames.Length - 1), S(8), S(30));

        float tx = hx;
        for (int i = 0; i < PageNames.Length; i++)
        {
            bool on = i == _page;
            float tw = Spaced(c, tx, ty, PageNames[i], on ? TextHi : TextLow, scale, spacing);
            float full = tw;
            if (counts[i] > 0)
            {
                string n = counts[i].ToString();
                float nw = TextW(c, n, 0.66f) + S(8);
                float bx = tx + tw + S(6);
                float bh = S(16);
                float bcy = ty + TextH(c, scale) * 0.5f;
                Rect(c, bx, bcy - bh * 0.5f, nw, bh, accent, on ? 0.9f : 0.35f);
                TextMiddle(c, bx + S(4), bcy, n, on ? Black : TextHi, 0.66f);
                full += nw + S(6);
            }

            if (on)
            {
                float k = 1f - MathF.Exp(-20f * _dt);
                _tabX = _tabX < 0f ? tx : _tabX + (tx - _tabX) * k;
                _tabW = _tabW <= 0f ? full : _tabW + (full - _tabW) * k;
            }

            tx += full + gap;
        }

        Rect(c, hx, lineY, right - hx, S(1), White, 0.08f);
        Rect(c, _tabX, lineY - S(1), _tabW, S(3), accent);
    }

    private static void DrawHeaderRow(Canvas c, string label, float hx, float right, float ry, Rgba accent)
    {
        float ty = ry + S(14);
        bool warn = label.Contains("SAVE", StringComparison.Ordinal);
        Rgba col = warn ? Warn : accent;
        float tw = Spaced(c, hx, ty, label, col, 0.68f, 3f, 0.95f);
        float ly = ty + TextH(c, 0.68f) * 0.5f;
        Rect(c, hx + tw + S(14), ly, right - hx - tw - S(14), S(1), White, 0.08f);
    }

    private static void DrawItem(Canvas c, MenuRow row, int i, float hx, float right, float ry, Rgba accent)
    {
        Feature f = row.F!;
        bool sel = i == _menuIndex;
        float cy = ry + S(RowH) * 0.5f;
        int key = _page * 1000 + i * 8;
        float shift = Approach(key + 7, sel ? 1f : 0f, 18f);
        float lw = TextMiddle(c, hx + S(6) * shift, cy, f.Label, Mix(TextMid, TextHi, shift), 0.95f);

        if (!row.Setting && f.Kind != KindAction && f.Active)
            Rect(c, hx - S(14), cy - S(3), S(5), S(5), accent, 0.9f);

        if (row.Setting && f.Id == "new_game_plus_plus" && f.On != NgppThisSession)
            Tag(c, hx + S(6) * shift + lw + S(12), cy, "RESTART GAME");

        switch (f.Kind)
        {
            case KindToggle:
                DrawSwitch(c, right, cy, f.On, sel, key, accent);
                break;
            case KindSlider:
                DrawSlider(c, right, cy, f, sel, key, accent);
                break;
            case KindChoice:
                DrawChoice(c, right, cy, f, sel, key, accent);
                break;
            case KindAction:
                DrawAction(c, right, cy, f, sel, accent, hx + S(6) * shift + lw);
                break;
        }
    }

    private static void DrawSwitch(Canvas c, float right, float cy, bool on, bool sel, int key, Rgba accent)
    {
        float t = Approach(key + 1, on ? 1f : 0f, 16f);
        float tw = S(42);
        float th = S(20);
        float tx = right - tw;
        float ty = cy - th * 0.5f;

        Rect(c, tx, ty, tw, th, Mix(Off, accent, t), 0.95f);
        if (sel)
            Frame(c, tx - S(2), ty - S(2), tw + S(4), th + S(4), S(1), TextHi, 0.55f);

        float k = S(14);
        Rect(c, tx + S(3) + t * (tw - k - S(6)), ty + S(3), k, k, Mix(TextMid, White, t));

        string label = on ? "ON" : "OFF";
        float lw = SpacedW(c, label, 0.78f, 2f);
        Spaced(c, tx - S(14) - lw, cy - TextH(c, 0.78f) * 0.5f, label, on ? accent : TextLow, 0.78f, 2f);
    }

    private static void DrawSlider(Canvas c, float right, float cy, Feature f, bool sel, int key, Rgba accent)
    {
        float v = f.Value;
        float range = f.Max - f.Min;
        float t = range <= 0f ? 0f : Math.Clamp((v - f.Min) / range, 0f, 1f);
        float at = Approach(key + 2, t, 20f);
        float dt = range <= 0f ? 0f : Math.Clamp((f.Default - f.Min) / range, 0f, 1f);

        float valW = S(76);
        float trackW = S(200);
        float tx = right - valW - trackW;
        float th = S(4);
        bool changed = f.Active || (f.Kind == KindSlider && MathF.Abs(f.Value - f.Default) > 0.0001f);

        Rect(c, tx, cy - th * 0.5f, trackW, th, White, 0.12f);
        float a = Math.Min(at, dt);
        float b = Math.Max(at, dt);
        Rect(c, tx + a * trackW, cy - th * 0.5f, (b - a) * trackW, th, accent, sel ? 1f : 0.7f);
        Rect(c, tx + dt * trackW - S(1), cy - S(7), S(2), S(14), White, 0.35f);

        float kw = S(6);
        Rect(c, tx + at * trackW - kw * 0.5f - S(1), cy - S(10), kw + S(2), S(20), Black, 0.6f);
        Rect(c, tx + at * trackW - kw * 0.5f, cy - S(9), kw, S(18), sel ? TextHi : TextMid);

        string text = f.Format!(v);
        float vw = TextW(c, text, 0.95f);
        TextMiddle(c, right - vw, cy, text, changed ? accent : sel ? TextHi : TextMid, 0.95f);
    }

    private static void DrawChoice(Canvas c, float right, float cy, Feature f, bool sel, int key, Rgba accent)
    {
        string[] options = f.Choices!;
        float scale = 0.72f;
        float padX = S(12);
        float bh = S(24);
        float total = 0f;
        foreach (string o in options)
            total += SpacedW(c, o, scale, 2f) + padX * 2f;

        if (total > S(330))
        {
            DrawChoiceCompact(c, right, cy, f, sel, key, accent);
            return;
        }

        float x = right - total;
        Rect(c, x, cy - bh * 0.5f, total, bh, White, 0.05f);
        Frame(c, x, cy - bh * 0.5f, total, bh, S(1), sel ? TextHi : White, sel ? 0.5f : 0.15f);

        float slide = Approach(key + 3, f.Choice, 18f);
        float px = x;
        float selX = x;
        float selW = 0f;
        for (int i = 0; i < options.Length; i++)
        {
            float ow = SpacedW(c, options[i], scale, 2f) + padX * 2f;
            float m = 1f - Math.Clamp(MathF.Abs(slide - i), 0f, 1f);
            selX += ow * 0f;
            if (m > 0f)
            {
                selX = px;
                selW = ow;
            }

            px += ow;
        }

        float pillX = x;
        for (int i = 0; i < options.Length; i++)
        {
            float ow = SpacedW(c, options[i], scale, 2f) + padX * 2f;
            float m = 1f - Math.Clamp(MathF.Abs(slide - i), 0f, 1f);
            if (m > 0f)
                Rect(c, pillX + S(2), cy - bh * 0.5f + S(2), ow - S(4), bh - S(4), f.Choice == 0 ? TextMid : accent, 0.85f * m);
            pillX += ow;
        }

        px = x;
        for (int i = 0; i < options.Length; i++)
        {
            float ow = SpacedW(c, options[i], scale, 2f) + padX * 2f;
            bool cur = i == f.Choice;
            Spaced(c, px + padX, cy - TextH(c, scale) * 0.5f, options[i], cur ? Black : TextLow, scale, 2f);
            px += ow;
        }
    }

    private static void DrawChoiceCompact(Canvas c, float right, float cy, Feature f, bool sel, int key, Rgba accent)
    {
        float scale = 0.72f;
        float bw = S(250);
        float bh = S(24);
        float x = right - bw;
        bool on = f.Choice != 0;
        Rgba col = on ? accent : TextMid;

        Rect(c, x, cy - bh * 0.5f, bw, bh, col, on ? 0.12f : 0.05f);
        Frame(c, x, cy - bh * 0.5f, bw, bh, S(1), sel ? TextHi : col, sel ? 0.55f : 0.3f);

        float nudge = Approach(key + 4, 0f, 14f);
        TextMiddle(c, x + S(10) - nudge, cy, "<", sel ? TextHi : TextLow, 0.8f);
        float aw = TextW(c, ">", 0.8f);
        TextMiddle(c, right - S(10) - aw + nudge, cy, ">", sel ? TextHi : TextLow, 0.8f);

        string name = f.Choices![f.Choice];
        float nw = SpacedW(c, name, scale, 2f);
        bool themed = f.Id == "menu_color";
        float sw = f.Id == "atmosphere" || themed ? S(12) + S(10) : 0f;
        float nx = x + (bw - nw - sw) * 0.5f;
        if (sw > 0f)
        {
            Rgb3 rgb = themed ? default : AtmoSwatch(f.Choice);
            Rgba sc = themed ? ThemeSwatch(f.Choice) : new Rgba((byte)(Math.Clamp(rgb.R, 0f, 1f) * 255f), (byte)(Math.Clamp(rgb.G, 0f, 1f) * 255f), (byte)(Math.Clamp(rgb.B, 0f, 1f) * 255f));
            Rect(c, nx, cy - S(6), S(12), S(12), sc);
            Frame(c, nx, cy - S(6), S(12), S(12), S(1), White, 0.4f);
            nx += sw;
        }

        Spaced(c, nx, cy - TextH(c, scale) * 0.5f, name, on ? TextHi : TextMid, scale, 2f);

        string count = (f.Choice + 1) + "/" + f.Choices.Length;
        float cw = TextW(c, count, 0.62f);
        TextMiddle(c, x - S(12) - cw, cy, count, TextLow, 0.62f);
    }

    private static void DrawAction(Canvas c, float right, float cy, Feature f, bool sel, Rgba accent, float labelEnd)
    {
        if (f.Permanent)
            Tag(c, labelEnd + S(12), cy, "SAVE");

        bool flashing = _flashRow == f && _flash > 0f;
        if (flashing)
            _flash = Math.Max(0f, _flash - _dt * 0.8f);

        bool confirm = _confirmRow == f;
        string text = flashing ? (_flashOk ? "DONE" : "FAILED") : confirm ? "CONFIRM?" : "RUN";
        Rgba col = flashing ? (_flashOk ? accent : Warn) : confirm ? Warn : sel ? TextHi : TextLow;
        float tw = SpacedW(c, text, 0.72f, 2f) + S(20);
        float bh = S(22);
        float blink = confirm ? 0.6f + 0.4f * MathF.Sin((float)_uiClock * 9f) : 1f;
        Rect(c, right - tw, cy - bh * 0.5f, tw, bh, col, (sel ? 0.14f : 0.05f) * blink);
        Frame(c, right - tw, cy - bh * 0.5f, tw, bh, S(1), col, 0.6f * blink);
        Spaced(c, right - tw + S(10), cy - TextH(c, 0.72f) * 0.5f, text, col, 0.72f, 2f);
    }

    private static void Tag(Canvas c, float x, float cy, string tag)
    {
        float tw = SpacedW(c, tag, 0.6f, 2f) + S(10);
        Rect(c, x, cy - S(9), tw, S(18), Warn, 0.16f);
        Frame(c, x, cy - S(9), tw, S(18), S(1), Warn, 0.6f);
        Spaced(c, x + S(5), cy - TextH(c, 0.6f) * 0.5f, tag, Warn, 0.6f, 2f);
    }

    private static float KeyHint(Canvas c, float x, float y, string keyName, string action)
    {
        float scale = 0.66f;
        float kw = TextW(c, keyName, scale) + S(14);
        float kh = S(20);
        Rect(c, x, y - kh * 0.5f, kw, kh, White, 0.08f);
        Frame(c, x, y - kh * 0.5f, kw, kh, S(1), White, 0.22f);
        TextMiddle(c, x + S(7), y, keyName, TextHi, scale);
        float aw = TextMiddle(c, x + kw + S(8), y, action, TextLow, scale);
        return x + kw + S(8) + aw + S(20);
    }

    private static void DrawActiveList(Canvas c)
    {
        _ga = 1f;
        var names = new List<string>();
        foreach (Feature f in Features)
        {
            if (f.Active)
                names.Add(f.Kind == KindToggle ? f.Label : f.Label + "  " + f.StateText());
        }

        float scale = 0.66f;
        float lh = S(20);
        float w = S(40);
        foreach (string n in names)
            w = Math.Max(w, SpacedW(c, n, scale, 1.5f) + S(30));
        w = Math.Max(w, SpacedW(c, "ORACLE", 0.66f, 3f) + S(30));

        float x = c.ClipX - w - S(24);
        float y = S(150);
        float h = S(30) + names.Count * lh + S(8);
        Rect(c, x, y, w, h, Panel, 0.75f);
        Rect(c, x + w - S(2), y, S(2), h, Accent, 0.9f);
        Spaced(c, x + S(12), y + S(8), "ORACLE", Accent, 0.66f, 3f);
        float ly = y + S(30);
        foreach (string n in names)
        {
            Rect(c, x + S(12), ly + lh * 0.5f - S(2), S(4), S(4), Accent, 0.8f);
            Spaced(c, x + S(22), ly + lh * 0.5f - TextH(c, scale) * 0.5f, n, TextMid, scale, 1.5f);
            ly += lh;
        }
    }

    private static void DrawRelocating(Canvas c)
    {
        float a = Math.Clamp(_blackout, 0f, 1f);
        string title = CharacterBusy || _tpReturnResult.Length > 0 ? "ORACLE // SWITCHING CHARACTER" : "ORACLE // RELOCATING";
        float tw = SpacedW(c, title, 0.8f, 4f);
        float cx = c.ClipX * 0.5f;
        float cy = c.ClipY * 0.5f;
        Spaced(c, cx - tw * 0.5f, cy - S(18), title, Accent, 0.8f, 4f, a);

        float bw = S(220);
        float bx = cx - bw * 0.5f;
        float by = cy + S(14);
        Rect(c, bx, by, bw, S(2), White, 0.08f * a);
        float seg = bw * 0.28f;
        float p = (float)(_uiClock * 0.9 % 1.0);
        float sx = bx + (bw + seg) * p - seg;
        float x0 = Math.Max(bx, sx);
        float x1 = Math.Min(bx + bw, sx + seg);
        if (x1 > x0)
            Rect(c, x0, by, x1 - x0, S(2), Accent, 0.9f * a);
    }

    private static void DrawConfirm(Canvas c)
    {
        float ga = _ga;
        _ga = EaseOut(_menuAnim);
        string title = ConfirmText(_confirmRow!);
        string keys = UsingPad ? "A  CONFIRM        ANY OTHER BUTTON  CANCEL" : "ENTER  CONFIRM        ANY OTHER KEY  CANCEL";
        float tw = SpacedW(c, title, 1.05f, 3f);
        float kw = SpacedW(c, keys, 0.7f, 2f);
        float w = Math.Max(tw, kw) + S(90);
        float h = S(128);
        float x = (c.ClipX - w) * 0.5f;
        float y = (c.ClipY - h) * 0.5f;
        float pulse = 0.6f + 0.4f * MathF.Sin((float)_uiClock * 6f);

        Rect(c, 0f, 0f, c.ClipX, c.ClipY, Black, 0.35f);
        Rect(c, x + S(6), y + S(8), w, h, Black, 0.45f);
        Rect(c, x, y, w, h, Panel);
        Rect(c, x, y, w, S(2), Accent, 0.9f);
        Rect(c, x, y + h - S(2), w, S(2), Accent, 0.35f);
        Frame(c, x, y, w, h, S(1), Accent, 0.35f * pulse + 0.2f);
        Spaced(c, x + (w - SpacedW(c, "ORACLE // CONFIRM", 0.62f, 3f)) * 0.5f, y + S(16), "ORACLE // CONFIRM", Accent, 0.62f, 3f, 0.9f);
        Spaced(c, x + (w - tw) * 0.5f, y + S(46), title, TextHi, 1.05f, 3f);
        Spaced(c, x + (w - kw) * 0.5f, y + h - S(36), keys, TextMid, 0.7f, 2f);
        _ga = ga;
    }

    private static void DrawToast(Canvas c)
    {
        float t = Math.Min(1f, _toastLeft / 0.3f) * Math.Min(1f, (ToastTime - _toastLeft) / 0.15f);
        _ga = EaseOut(t);

        Rgba col = _toastOn ? Accent : TextLow;
        float tw = SpacedW(c, _toastTitle, 0.85f, 3f);
        float sw = SpacedW(c, _toastState, 0.85f, 3f);
        float w = tw + sw + S(64);
        float h = S(40);
        float x = (c.ClipX - w) * 0.5f;
        float y = S(150) - S(10) * (1f - _ga);

        Rect(c, x + S(4), y + S(5), w, h, Black, 0.3f);
        Rect(c, x, y, w, h, Panel);
        Rect(c, x, y, S(3), h, col);
        Rect(c, x, y, w, S(1), col, 0.5f);
        float ty = y + h * 0.5f - TextH(c, 0.85f) * 0.5f;
        Spaced(c, x + S(22), ty, _toastTitle, TextHi, 0.85f, 3f);
        Spaced(c, x + S(22) + tw + S(20), ty, _toastState, col, 0.85f, 3f);
    }
}
