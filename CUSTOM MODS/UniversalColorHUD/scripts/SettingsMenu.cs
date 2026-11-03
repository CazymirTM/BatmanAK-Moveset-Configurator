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

namespace CeZeY.UniversalColorHudScript;

public partial class UniversalColorHUD
{
    private const int RowToggle = 0;
    private const int RowSlider = 1;
    private const int RowSwatches = 2;
    private const int RowHeader = 3;
    private const int RowAction = 4;
    private const int TrackPlain = 0;
    private const int TrackHue = 1;
    private const int TrackSat = 2;
    private const int TrackVal = 3;
    private const float RepeatDelay = 0.38f;
    private const float RepeatRate = 0.06f;
    private const float ToastTime = 1.8f;
    private const string MenuOwnerKey = "CeZeY.ModMenuOwner";
    private const string MenuOwnerName = "UniversalColorHUD";

    private readonly record struct Rgba(byte R, byte G, byte B, byte A = 255);

    private static readonly Rgba Panel = new(7, 10, 15, 236);
    private static readonly Rgba TextHi = new(242, 245, 248);
    private static readonly Rgba TextMid = new(182, 191, 202);
    private static readonly Rgba TextLow = new(118, 130, 145);
    private static readonly Rgba White = new(255, 255, 255);
    private static readonly Rgba Black = new(0, 0, 0);
    private static readonly Rgba Off = new(52, 58, 66);

    private sealed class MenuRow
    {
        public int Kind;
        public string Label = "";
        public string Help = "";
        public Func<bool>? IsOn;
        public Action? Flip;
        public Func<float>? Get;
        public Action<float>? Set;
        public float Min;
        public float Max;
        public float Step;
        public int Track;
        public Func<float, string>? Format;
        public Func<string>? Right;
    }

    private static readonly string[] PageNames = { "COLOR", "EFFECTS", "SETTINGS" };
    private static List<MenuRow>[]? _pages;

    private static bool _menuOpen;
    private static float _menuAnim;
    private static int _page;
    private static int _menuIndex;
    private static float _selY = -1f;
    private static float _tabX = -1f;
    private static float _tabW;
    private static nint _inputPc;
    private static readonly Dictionary<int, float> _anim = new();
    private static long _drawTick;
    private static float _toastLeft;
    private static float _flash;
    private static readonly Keys[] RepeatKeys = { Keys.Left, Keys.Right, Keys.Up, Keys.Down };
    private static readonly float[] _repeatHeld = { -1f, -1f, -1f, -1f };

    private static Texture2D? _white;
    private static Font? _font;
    private static float _s = 1f;
    private static float _ga = 1f;
    private static float _dt;
    private static Rgba _accent = new(96, 178, 255);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static List<MenuRow>[] Pages => _pages ??= BuildPages();

    private static MenuRow Toggle(string label, string help, Func<bool> isOn, Action flip)
    {
        return new MenuRow { Kind = RowToggle, Label = label, Help = help, IsOn = isOn, Flip = flip };
    }

    private static MenuRow Slider(string label, string help, Func<float> get, Action<float> set, float min, float max, float step, Func<float, string> format, int track = TrackPlain)
    {
        return new MenuRow { Kind = RowSlider, Label = label, Help = help, Get = get, Set = set, Min = min, Max = max, Step = step, Format = format, Track = track };
    }

    private static MenuRow Header(string label, Func<string>? right = null)
    {
        return new MenuRow { Kind = RowHeader, Label = label, Right = right };
    }

    private static string Seconds(float v) => v.ToString("0.00", CultureInfo.InvariantCulture) + " s";

    private static string Whole(float v) => v.ToString("0", CultureInfo.InvariantCulture);

    private static string Percent(float v) => (v * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static List<MenuRow>[] BuildPages()
    {
        var color = new List<MenuRow>
        {
            Toggle("HUD COLOR", "Turns the color on or off. The " + _toggleKey + " key does the same.", () => _enabled, () => SetEnabled(!_enabled, "menu")),
            new MenuRow { Kind = RowSwatches, Label = "PRESET", Help = "LEFT / RIGHT picks a color. JOKER uses the game's own Joker tint." },
            Header("CUSTOM COLOR", () => Hex(ChosenColor)),
            Slider("HUE", "Position on the color wheel.", () => _hue, v => { _hue = v; SetCustom(); }, 0f, 360f, 5f, Whole, TrackHue),
            Slider("SATURATION", "0% is white, 100% is the pure color.", () => _sat, v => { _sat = v; SetCustom(); }, 0f, 1f, 0.05f, Percent, TrackSat),
            Slider("BRIGHTNESS", "Lower values give a darker, moodier HUD.", () => _val, v => { _val = v; SetCustom(); }, 0.2f, 1f, 0.05f, Percent, TrackVal),
        };

        var effects = new List<MenuRow>
        {
            Toggle("COLOR CYCLE", "Slowly cycles the HUD through every color.", () => _rainbow, () => { _rainbow = !_rainbow; SaveSetting("rainbow", _rainbow ? "true" : "false"); }),
            Slider("CYCLE LENGTH", "Seconds for one full trip around the color wheel.", () => _rainbowSeconds,
                v => { _rainbowSeconds = v; SaveNum("rainbow_speed", v); }, 2f, 60f, 1f, v => Whole(v) + " s", TrackHue),
            Slider("CYCLE UPDATE RATE", "Colour refreshes per second while cycling. Lower is lighter on weak PCs.", () => _rainbowHz,
                v => { _rainbowHz = v; SaveNum("rainbow_updates_per_second", v); }, 5f, 120f, 5f, v => Whole(v) + " / s"),
            Header("TRANSITIONS"),
            Slider("BLEND TIME", "How long the HUD takes to blend into a new color, or back to default.", () => _blendTime,
                v => { _blendTime = v; SaveNum("transition_time", v); }, 0f, 2f, 0.05f, Seconds),
        };

        var settings = new List<MenuRow>
        {
            Toggle("TURN ON AFTER LOADING", "Applies your color automatically every time a save loads.", () => _autoActivate,
                () => { _autoActivate = !_autoActivate; SaveSetting("auto_activate", _autoActivate ? "true" : "false"); }),
            Slider("DELAY AFTER LOADING", "Seconds to wait once the HUD is ready before the color fades in.", () => _autoDelay,
                v => { _autoDelay = v; SaveNum("auto_press_delay", v); }, 0f, 15f, 0.5f, v => v.ToString("0.0", CultureInfo.InvariantCulture) + " s"),
            Header("RESET"),
            new MenuRow { Kind = RowAction, Label = "RESET TO GAME DEFAULT", Help = "Picks the game's own blue HUD color and stops cycling.", Flip = () => { SelectPreset(DefaultPreset); _flash = 1f; } },
        };

        return new[] { color, effects, settings };
    }

    private static bool OwnsMenu()
    {
        return AppDomain.CurrentDomain.GetData(MenuOwnerKey) as string == MenuOwnerName;
    }

    private static void OpenMenu()
    {
        _menuOpen = true;
        _menuIndex = NextSelectable(Pages[_page], -1, 1);
        _selY = -1f;
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
        Array.Fill(_repeatHeld, -1f);

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
        if (_menuOpen)
            return;

        self.OpenLevelUpOrMap();
    }

    private static int NextSelectable(List<MenuRow> rows, int from, int dir)
    {
        int i = from;
        for (int guard = 0; guard < rows.Count; guard++)
        {
            i = (i + dir + rows.Count) % rows.Count;
            if (rows[i].Kind != RowHeader)
                return i;
        }

        return 0;
    }

    private static bool HandleMenuKey(Keys key, bool repeat)
    {
        List<MenuRow> rows = Pages[_page];
        _menuIndex = Math.Clamp(_menuIndex, 0, rows.Count - 1);
        MenuRow row = rows[_menuIndex];

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
                return true;
            case Keys.Left:
            case Keys.Right:
                if (repeat && row.Kind != RowSlider && row.Kind != RowSwatches)
                    return true;
                Adjust(row, key == Keys.Left ? -1 : 1);
                return true;
            case Keys.Enter:
                if (row.Kind == RowToggle || row.Kind == RowAction)
                    row.Flip?.Invoke();
                else if (row.Kind == RowSwatches)
                    Adjust(row, 1);
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
    }

    private static void Adjust(MenuRow row, int dir)
    {
        switch (row.Kind)
        {
            case RowToggle:
                row.Flip?.Invoke();
                break;
            case RowSlider:
                float v = MathF.Round(Math.Clamp(row.Get!() + dir * row.Step, row.Min, row.Max) / row.Step) * row.Step;
                row.Set!(MathF.Round(v, 3));
                break;
            case RowSwatches:
                int next = _preset < 0 ? (dir > 0 ? 0 : Presets.Length - 1) : (_preset + dir + Presets.Length) % Presets.Length;
                SelectPreset(next);
                break;
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

    private static void ShowToast()
    {
        if (!_menuOpen)
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

    [Redirect(typeof(GameViewportClient), "PostRender")]
    private static void ViewportPostRenderRedirect(GameViewportClient self, Canvas canvas)
    {
        self.PostRender(canvas);

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
        if (_menuOpen && (GamePaused() || !OwnsMenu()))
        {
            CloseMenu();
            _menuAnim = 0f;
        }

        long now = Stopwatch.GetTimestamp();
        _dt = _drawTick == 0 ? 0f : Math.Clamp((float)Stopwatch.GetElapsedTime(_drawTick, now).TotalSeconds, 0f, 0.1f);
        _drawTick = now;

        float target = _menuOpen ? 1f : 0f;
        float speed = _menuOpen ? 1f / 0.2f : 1f / 0.14f;
        _menuAnim = _menuAnim < target ? Math.Min(target, _menuAnim + _dt * speed) : Math.Max(target, _menuAnim - _dt * speed);
        if (_toastLeft > 0f)
            _toastLeft -= _dt;

        if ((_menuAnim <= 0f && _toastLeft <= 0f) || c == null)
            return;

        if (_white == null || !_white.IsValid)
            _white = Game.FindObject<Texture2D>("EngineResources.WhiteSquareTexture");
        if (_font == null || !_font.IsValid)
            _font = Game.FindObject<Font>("BmFonts.EngineSmallFont");
        if (_white == null)
            return;

        _s = Math.Clamp(c.ClipY / 1080f, 0.75f, 2.5f);
        _accent = AccentFor(DisplayColor);
        Font? oldFont = c.Font;
        if (_font != null)
            c.Font = _font;

        try
        {
            if (_menuAnim > 0f)
                DrawMenu(c);
            else
                DrawToast(c);
        }
        finally
        {
            c.Font = oldFont;
        }
    }

    private static Rgba ToRgba(Rgb c, byte a = 255)
    {
        return new Rgba((byte)Math.Clamp(c.R * 255f, 0f, 255f), (byte)Math.Clamp(c.G * 255f, 0f, 255f), (byte)Math.Clamp(c.B * 255f, 0f, 255f), a);
    }

    private static Rgba AccentFor(Rgb c)
    {
        float m = Math.Max(c.R, Math.Max(c.G, c.B));
        if (m < 0.05f)
            c = HudBase;
        else
            c = new Rgb(c.R / m, c.G / m, c.B / m);

        return ToRgba(new Rgb(c.R + (1f - c.R) * 0.2f, c.G + (1f - c.G) * 0.2f, c.B + (1f - c.B) * 0.2f));
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

    private static float Linear(byte v) => MathF.Pow(v / 255f, 2.2f);

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
        const int slices = 32;
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
        const int slices = 24;
        for (int i = 0; i < slices; i++)
        {
            float y0 = MathF.Round(y + h * i / slices);
            float y1 = MathF.Round(y + h * (i + 1) / slices);
            float t = (i + 0.5f) / slices;
            Rect(c, x, y0, w, y1 - y0, col, a0 + (a1 - a0) * t);
        }
    }

    private static void ColorRamp(Canvas c, float x, float y, float w, float h, Func<float, Rgb> color)
    {
        const int slices = 36;
        for (int i = 0; i < slices; i++)
        {
            float x0 = MathF.Round(x + w * i / slices);
            float x1 = MathF.Round(x + w * (i + 1) / slices);
            Rect(c, x0, y, x1 - x0, h, ToRgba(color((i + 0.5f) / slices)));
        }
    }

    private static float TextW(Canvas c, string text, float scale)
    {
        c.TextSize(text, out float w, out _, scale * _s, scale * _s);
        return w;
    }

    private static float TextH(Canvas c, float scale)
    {
        c.TextSize("AQ", out _, out float h, scale * _s, scale * _s);
        return h;
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

    private static float BodyHeight(List<MenuRow> rows)
    {
        float h = 0f;
        foreach (MenuRow r in rows)
            h += r.Kind == RowHeader ? S(36) : S(38);
        return h;
    }

    private static string StatusText()
    {
        if (_autoPending)
            return "STARTING";
        if (!_enabled)
            return "OFF";
        if (_rainbow)
            return "CYCLING";
        return _preset >= 0 ? Presets[_preset].Label : "CUSTOM";
    }

    private static void DrawMenu(Canvas c)
    {
        List<MenuRow>[] pages = Pages;
        List<MenuRow> rows = pages[_page];
        _menuIndex = Math.Clamp(_menuIndex, 0, rows.Count - 1);
        Rgba accent = _accent;

        float e = EaseOut(_menuAnim);
        _ga = e;

        float bodyH = 0f;
        foreach (List<MenuRow> p in pages)
            bodyH = Math.Max(bodyH, BodyHeight(p));

        float w = S(680);
        float pad = S(32);
        float headH = S(138);
        float footH = S(96);
        float h = headH + bodyH + S(10) + footH;
        float x = S(72) - S(30) * (1f - e);
        float y = MathF.Max(S(40), (c.ClipY - h) * 0.5f);
        float right = x + w - pad;

        Rect(c, x + S(8), y + S(10), w, h, Black, 0.35f);
        Rect(c, x, y, w, h, Panel);
        VGradient(c, x, y, w, S(130), accent, 0.10f, 0f);
        Rect(c, x, y, w, S(1), accent, 0.55f);
        Rect(c, x, y + h - S(1), w, S(1), White, 0.06f);
        Rect(c, x, y, S(3), h, accent, 0.9f);
        Rect(c, x + w - S(18), y, S(18), S(2), accent);
        Rect(c, x + w - S(2), y, S(2), S(18), accent);
        Rect(c, x + w - S(18), y + h - S(2), S(18), S(2), accent, 0.6f);
        Rect(c, x + w - S(2), y + h - S(18), S(2), S(18), accent, 0.6f);

        float hx = x + pad;
        Spaced(c, hx, y + S(22), "MOD SETTINGS", accent, 0.72f, 3f, 0.9f);
        Spaced(c, hx, y + S(42), "UNIVERSAL COLOR HUD", TextHi, 1.3f, 2.5f);

        string chip = _enabled ? "ON" : _autoPending ? "WAIT" : "OFF";
        Rgba chipCol = _enabled ? accent : TextLow;
        float chipW = SpacedW(c, chip, 0.72f, 2f) + S(46);
        float chipX = right - chipW;
        float chipY = y + S(30);
        Rect(c, chipX, chipY, chipW, S(24), chipCol, _enabled ? 0.14f : 0.06f);
        Frame(c, chipX, chipY, chipW, S(24), S(1), chipCol, 0.75f);
        Rect(c, chipX + S(8), chipY + S(6), S(12), S(12), ToRgba(DisplayColor));
        Frame(c, chipX + S(8), chipY + S(6), S(12), S(12), S(1), White, 0.5f);
        Spaced(c, chipX + S(30), chipY + S(12) - TextH(c, 0.72f) * 0.5f, chip, chipCol, 0.72f, 2f);

        float ty = y + S(92);
        float tx = hx;
        for (int i = 0; i < PageNames.Length; i++)
        {
            bool on = i == _page;
            float tw = SpacedW(c, PageNames[i], 0.9f, 2f);
            Spaced(c, tx, ty, PageNames[i], on ? TextHi : TextLow, 0.9f, 2f);
            if (on)
            {
                float k = 1f - MathF.Exp(-20f * _dt);
                _tabX = _tabX < 0f ? tx : _tabX + (tx - _tabX) * k;
                _tabW = _tabW <= 0f ? tw : _tabW + (tw - _tabW) * k;
            }

            tx += tw + S(36);
        }

        Rect(c, hx, y + headH - S(14), right - hx, S(1), White, 0.08f);
        Rect(c, _tabX, y + headH - S(15), _tabW, S(3), accent);

        string status = StatusText();
        float stW = SpacedW(c, status, 0.72f, 2f);
        Spaced(c, right - stW, ty + S(3), status, _enabled ? accent : TextLow, 0.72f, 2f);
        Rect(c, right - stW - S(14), ty + S(3) + TextH(c, 0.72f) * 0.5f - S(3), S(6), S(6), _enabled ? accent : TextLow);

        float by = y + headH;
        float target = by;
        float ry = by;
        for (int i = 0; i < rows.Count; i++)
        {
            float rh = rows[i].Kind == RowHeader ? S(36) : S(38);
            if (i == _menuIndex)
                target = ry;
            ry += rh;
        }

        _selY = _selY < 0f ? target : _selY + (target - _selY) * (1f - MathF.Exp(-22f * _dt));
        HGradient(c, x + S(3), _selY, w - S(3), S(38), accent, 0.22f, 0.0f);
        Rect(c, x, _selY, S(3), S(38), TextHi);

        ry = by;
        for (int i = 0; i < rows.Count; i++)
        {
            MenuRow row = rows[i];
            if (row.Kind == RowHeader)
            {
                DrawHeaderRow(c, row, hx, right, ry, accent);
                ry += S(36);
                continue;
            }

            bool sel = i == _menuIndex;
            float cy = ry + S(19);
            int key = _page * 1000 + i * 8;
            float shift = Approach(key + 7, sel ? 1f : 0f, 18f);
            float lw = TextMiddle(c, hx + S(6) * shift, cy, row.Label, Mix(TextMid, TextHi, shift), 0.95f);

            switch (row.Kind)
            {
                case RowToggle:
                    DrawSwitch(c, right, cy, row.IsOn!(), sel, key, accent);
                    break;
                case RowSlider:
                    DrawSlider(c, right, cy, row, sel, key, accent);
                    break;
                case RowSwatches:
                    DrawSwatches(c, right, cy, sel, hx + S(6) * shift + lw, accent);
                    break;
                case RowAction:
                    DrawAction(c, right, cy, sel, accent);
                    break;
            }

            ry += S(38);
        }

        float fy = by + bodyH + S(10);
        Rect(c, hx, fy, right - hx, S(1), White, 0.08f);
        Text(c, hx, fy + S(14), rows[_menuIndex].Help, TextMid, 0.85f, 0.95f);

        float kx = hx;
        float ky = fy + S(58);
        kx = KeyHint(c, kx, ky, "UP / DOWN", "SELECT");
        kx = KeyHint(c, kx, ky, "LEFT / RIGHT", "ADJUST");
        kx = KeyHint(c, kx, ky, "ENTER", "TOGGLE");
        kx = KeyHint(c, kx, ky, "TAB", "PAGE");
        KeyHint(c, kx, ky, _menuKey.ToString().ToUpperInvariant(), "CLOSE");
    }

    private static void DrawHeaderRow(Canvas c, MenuRow row, float hx, float right, float ry, Rgba accent)
    {
        float ty = ry + S(16);
        float tw = Spaced(c, hx, ty, row.Label, accent, 0.68f, 3f, 0.95f);
        float ly = ty + TextH(c, 0.68f) * 0.5f;
        float end = right;

        if (row.Right != null)
        {
            string text = row.Right();
            float rw = SpacedW(c, text, 0.68f, 2f);
            Spaced(c, right - rw, ty, text, TextMid, 0.68f, 2f);
            end = right - rw - S(14);
        }

        Rect(c, hx + tw + S(14), ly, end - hx - tw - S(14), S(1), White, 0.08f);
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

    private static void DrawSlider(Canvas c, float right, float cy, MenuRow row, bool sel, int key, Rgba accent)
    {
        float v = row.Get!();
        float range = row.Max - row.Min;
        float t = range <= 0f ? 0f : Math.Clamp((v - row.Min) / range, 0f, 1f);
        float at = Approach(key + 2, t, 20f);

        float valW = S(76);
        float trackW = S(200);
        float tx = right - valW - trackW;

        if (row.Track == TrackPlain)
        {
            float th = S(4);
            Rect(c, tx, cy - th * 0.5f, trackW, th, White, 0.12f);
            Rect(c, tx, cy - th * 0.5f, at * trackW, th, accent, sel ? 1f : 0.7f);
        }
        else
        {
            float th = S(8);
            float alpha = _ga;
            _ga *= sel ? 1f : 0.75f;
            switch (row.Track)
            {
                case TrackHue:
                    ColorRamp(c, tx, cy - th * 0.5f, trackW, th, p => Hsv(p * 360f, 0.85f, 1f));
                    break;
                case TrackSat:
                    ColorRamp(c, tx, cy - th * 0.5f, trackW, th, p => Hsv(_hue, p, _val));
                    break;
                case TrackVal:
                    ColorRamp(c, tx, cy - th * 0.5f, trackW, th, p => Hsv(_hue, _sat, 0.2f + 0.8f * p));
                    break;
            }

            _ga = alpha;
            Frame(c, tx, cy - th * 0.5f, trackW, th, S(1), Black, 0.5f);
        }

        float kw = S(6);
        Rect(c, tx + at * trackW - kw * 0.5f - S(1), cy - S(10), kw + S(2), S(20), Black, 0.6f);
        Rect(c, tx + at * trackW - kw * 0.5f, cy - S(9), kw, S(18), sel ? TextHi : TextMid);

        string text = row.Format!(v);
        float vw = TextW(c, text, 0.95f);
        TextMiddle(c, right - vw, cy, text, sel ? TextHi : TextMid, 0.95f);
    }

    private static void DrawSwatches(Canvas c, float right, float cy, bool sel, float labelEnd, Rgba accent)
    {
        string name = _rainbow ? "CYCLING" : _preset >= 0 ? Presets[_preset].Label : "CUSTOM";
        Spaced(c, labelEnd + S(14), cy - TextH(c, 0.72f) * 0.5f, name, accent, 0.72f, 2f);

        float b = S(20);
        float gap = S(7);
        float x = right - Presets.Length * b - (Presets.Length - 1) * gap;
        for (int i = 0; i < Presets.Length; i++)
        {
            bool cur = !_rainbow && i == _preset;
            float lift = Approach(5000 + i, cur ? 1f : 0f, 18f);
            float bx = x + i * (b + gap);
            float by = cy - b * 0.5f - S(3) * lift;

            Rect(c, bx, by, b, b, ToRgba(Presets[i].Color));
            if (i == JokerPreset)
                Rect(c, bx, by + b * 0.5f, b, b * 0.5f, new Rgba(60, 200, 90), 0.85f);
            Frame(c, bx, by, b, b, S(1), Black, 0.6f);

            if (cur)
            {
                Frame(c, bx - S(3), by - S(3), b + S(6), b + S(6), S(2), sel ? TextHi : TextMid, 0.9f);
                Rect(c, bx + S(4), by + b + S(7), b - S(8), S(2), accent, lift);
            }
        }
    }

    private static void DrawAction(Canvas c, float right, float cy, bool sel, Rgba accent)
    {
        if (_flash > 0f)
            _flash = Math.Max(0f, _flash - _dt * 0.8f);

        string text = _flash > 0f ? "DONE" : "ENTER";
        float tw = SpacedW(c, text, 0.72f, 2f) + S(20);
        float bh = S(22);
        Rgba col = _flash > 0f ? accent : sel ? TextHi : TextLow;
        Rect(c, right - tw, cy - bh * 0.5f, tw, bh, col, sel ? 0.12f : 0.05f);
        Frame(c, right - tw, cy - bh * 0.5f, tw, bh, S(1), col, 0.6f);
        Spaced(c, right - tw + S(10), cy - TextH(c, 0.72f) * 0.5f, text, col, 0.72f, 2f);
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

    private static void DrawToast(Canvas c)
    {
        float t = Math.Min(1f, _toastLeft / 0.3f) * Math.Min(1f, (ToastTime - _toastLeft) / 0.15f);
        _ga = EaseOut(t);

        string title = "HUD COLOR";
        string state = _enabled ? "ON" : "OFF";
        Rgba col = _enabled ? _accent : TextLow;
        float tw = SpacedW(c, title, 0.85f, 3f);
        float sw = SpacedW(c, state, 0.85f, 3f);
        float w = tw + sw + S(78);
        float h = S(40);
        float x = (c.ClipX - w) * 0.5f;
        float y = S(150) - S(10) * (1f - _ga);

        Rect(c, x + S(4), y + S(5), w, h, Black, 0.3f);
        Rect(c, x, y, w, h, Panel);
        Rect(c, x, y, S(3), h, col);
        Rect(c, x, y, w, S(1), col, 0.5f);
        float ty = y + h * 0.5f - TextH(c, 0.85f) * 0.5f;
        Spaced(c, x + S(22), ty, title, TextHi, 0.85f, 3f);
        Rect(c, x + S(22) + tw + S(12), y + h * 0.5f - S(6), S(12), S(12), ToRgba(DisplayColor));
        Spaced(c, x + S(22) + tw + S(36), ty, state, col, 0.85f, 3f);
    }
}
