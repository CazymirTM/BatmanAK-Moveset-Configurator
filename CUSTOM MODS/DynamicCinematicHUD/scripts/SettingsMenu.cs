using System;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Globalization;
using System.Runtime.InteropServices;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;
using Canvas = BmSDK.Engine.Canvas;
using FLinearColor = BmSDK.GameObject.FLinearColor;
using GameViewportClient = BmSDK.Engine.GameViewportClient;

namespace CeZeY.DynamicCinematicHudScript;

public partial class DynamicCinematicHUD
{
    private const int RowToggle = 0;
    private const int RowSlider = 1;
    private const int RowChoice = 2;
    private const int RowElement = 3;
    private const int RowHeader = 4;
    private const int RowAction = 5;
    private const float RepeatDelay = 0.38f;
    private const float RepeatRate = 0.06f;
    private const float ToastTime = 1.8f;
    private const string MenuOwnerKey = "CeZeY.ModMenuOwner";
    private const string MenuOwnerName = "DynamicCinematicHUD";

    private readonly record struct Rgba(byte R, byte G, byte B, byte A = 255);

    private static readonly Rgba Panel = new(7, 10, 15, 236);
    private static readonly Rgba Accent = new(96, 178, 255);
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
        public Func<float, string>? Format;
        public Func<string>? Choice;
        public int Element = -1;
    }

    private static readonly string[] PageNames = { "HUD", "ELEMENTS", "CAMERA" };
    private static List<MenuRow>[]? _pages;

    private static bool _menuOpen;
    private static float _menuAnim;
    private static int _page;
    private static int _menuIndex;
    private static int _cell;
    private static float _selY = -1f;
    private static float _tabX = -1f;
    private static float _tabW;
    private static nint _inputPc;
    private static readonly Dictionary<int, float> _anim = new();
    private static long _drawTick;
    private static float _toastLeft;
    private static readonly Keys[] RepeatKeys = { Keys.Left, Keys.Right, Keys.Up, Keys.Down };
    private static readonly float[] _repeatHeld = { -1f, -1f, -1f, -1f };

    private static Texture2D? _white;
    private static Font? _font;
    private static float _s = 1f;
    private static float _ga = 1f;
    private static float _dt;
    private static float _flash;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static List<MenuRow>[] Pages => _pages ??= BuildPages();

    private static MenuRow Toggle(string label, string help, Func<bool> isOn, Action flip)
    {
        return new MenuRow { Kind = RowToggle, Label = label, Help = help, IsOn = isOn, Flip = flip };
    }

    private static MenuRow Slider(string label, string help, Func<float> get, Action<float> set, float min, float max, float step, Func<float, string> format)
    {
        return new MenuRow { Kind = RowSlider, Label = label, Help = help, Get = get, Set = set, Min = min, Max = max, Step = step, Format = format };
    }

    private static MenuRow Header(string label)
    {
        return new MenuRow { Kind = RowHeader, Label = label };
    }

    private static string Seconds(float v) => v.ToString("0.00", CultureInfo.InvariantCulture) + " s";

    private static string Signed(float v) => (v > 0f ? "+" : "") + v.ToString("0", CultureInfo.InvariantCulture);

    private static string Plain(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    private static List<MenuRow>[] BuildPages()
    {
        var hud = new List<MenuRow>
        {
            Toggle("CINEMATIC HUD", "Master switch for HUD fading and camera tweaks.", () => _enabled, () => SetEnabled(!_enabled)),
            Header("TRIGGERS"),
            Toggle("FADE DURING TRAVERSAL", "Gliding, grappling and diving.", () => _trigOn[0], () => ToggleTrigger(0)),
            Toggle("FADE DURING FINISHERS", "Takedowns, beatdowns and environment finishers.", () => _trigOn[1], () => ToggleTrigger(1)),
            Slider("TRAVERSAL DELAY", "How long traversal has to last before the HUD fades.", () => _travDelay,
                v => { _travDelay = v; SaveNum("triggers", "traversal_delay", v); }, 0f, 3f, 0.1f, Seconds),
            Slider("RETURN DELAY", "How long the HUD stays hidden after the moment ends.", () => _linger,
                v => { _linger = v; SaveNum("triggers", "linger", v); }, 0f, 5f, 0.1f, Seconds),
            Header("TRANSITION"),
            new MenuRow
            {
                Kind = RowChoice, Label = "STYLE", Help = "Smooth eases the HUD out and back in. Instant cuts it.",
                Choice = () => _instant ? "INSTANT" : "SMOOTH",
                Flip = () => { _instant = !_instant; SaveSetting("transition", "style", _instant ? "\"instant\"" : "\"smooth\""); },
            },
            Slider("FADE OUT", "Time the HUD takes to disappear.", () => _fadeOut,
                v => { _fadeOut = v; SaveNum("transition", "fade_out", v); }, 0.05f, 2f, 0.05f, Seconds),
            Slider("FADE IN", "Time the HUD takes to come back.", () => _fadeIn,
                v => { _fadeIn = v; SaveNum("transition", "fade_in", v); }, 0.05f, 2f, 0.05f, Seconds),
        };

        var elements = new List<MenuRow> { Header("ELEMENT") };
        for (int i = 0; i < Elements.Length; i++)
            elements.Add(new MenuRow { Kind = RowElement, Label = Elements[i].Label, Help = Elements[i].Help, Element = i });

        var camera = new List<MenuRow>
        {
            Toggle("CAMERA TWEAKS", "Offsets added on top of the game camera. Paused during cutscenes.", () => _camOn,
                () => { _camOn = !_camOn; SaveBool("camera", "enabled", _camOn); }),
            Header("FRAMING"),
            Slider("FIELD OF VIEW", "Degrees added to the game's field of view.", () => _fov,
                v => { _fov = v; SaveNum("camera", "fov", v); }, -40f, 60f, 1f, Signed),
            Slider("DISTANCE", "Positive pulls the camera back, negative brings it closer.", () => _camDist,
                v => { _camDist = v; SaveNum("camera", "distance", v); }, -200f, 400f, 10f, Signed),
            Slider("HEIGHT", "Raises or lowers the camera.", () => _camHeight,
                v => { _camHeight = v; SaveNum("camera", "height", v); }, -100f, 200f, 5f, Signed),
            Slider("BLEND SPEED", "How quickly the camera eases into these values.", () => _blend,
                v => { _blend = v; SaveNum("camera", "blend_speed", v); }, 0.5f, 20f, 0.5f, Plain),
            Header("RESET"),
            new MenuRow { Kind = RowAction, Label = "RESET CAMERA TO VANILLA", Help = "Sets field of view, distance and height back to 0.", Flip = ResetCamera },
        };

        return new[] { hud, elements, camera };
    }

    private static void ResetCamera()
    {
        _fov = 0f;
        _camDist = 0f;
        _camHeight = 0f;
        SaveNum("camera", "fov", 0f);
        SaveNum("camera", "distance", 0f);
        SaveNum("camera", "height", 0f);
        _flash = 1f;
    }

    private static void ToggleTrigger(int i)
    {
        _trigOn[i] = !_trigOn[i];
        SaveBool("triggers", i == 0 ? "traversal" : "finisher", _trigOn[i]);
    }

    private static void ToggleCell(int element, int cell)
    {
        HudElement e = Elements[element];
        e.Mask ^= 1 << cell;
        SaveSetting("elements", e.Key, "\"" + MaskToString(e.Mask) + "\"");
    }

    private static bool OwnsMenu()
    {
        return AppDomain.CurrentDomain.GetData(MenuOwnerKey) as string == MenuOwnerName;
    }

    private static void OpenMenu()
    {
        _menuOpen = true;
        AppDomain.CurrentDomain.SetData(MenuOwnerKey, MenuOwnerName);
        _menuIndex = NextSelectable(Pages[_page], -1, 1);
        _cell = 0;
        _selY = -1f;

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
                if (repeat && row.Kind != RowSlider)
                    return true;
                Adjust(row, key == Keys.Left ? -1 : 1);
                return true;
            case Keys.Enter:
                Activate(row);
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
        _cell = 0;
        _selY = -1f;
    }

    private static void Adjust(MenuRow row, int dir)
    {
        switch (row.Kind)
        {
            case RowToggle:
            case RowChoice:
                row.Flip?.Invoke();
                break;
            case RowSlider:
                float v = MathF.Round(Math.Clamp(row.Get!() + dir * row.Step, row.Min, row.Max) / row.Step) * row.Step;
                row.Set!(MathF.Round(v, 3));
                break;
            case RowElement:
                _cell = Math.Clamp(_cell + dir, 0, CtxCount - 1);
                break;
        }
    }

    private static void Activate(MenuRow row)
    {
        if (row.Kind == RowElement)
            ToggleCell(row.Element, _cell);
        else if (row.Kind == RowToggle || row.Kind == RowChoice || row.Kind == RowAction)
            row.Flip?.Invoke();
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

    private static void Rect(Canvas c, float x, float y, float w, float h, Rgba col, float alpha = 1f)
    {
        if (w <= 0f || h <= 0f)
            return;

        c.SetPos(MathF.Round(x), MathF.Round(y), 0f);
        var lc = new FLinearColor { R = Linear(col.R), G = Linear(col.G), B = Linear(col.B), A = col.A / 255f * alpha * _ga };
        c.DrawTile(_white!, MathF.Round(w), MathF.Round(h), 0f, 0f, 32f, 32f, lc, false, EngineTypes.EBlendMode.BLEND_Translucent);
    }

    private static float Linear(byte v) => MathF.Pow(v / 255f, 2.2f);

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
        {
            string s = ch.ToString();
            x += Text(c, x, y, s, col, scale, alpha) + S(spacing);
        }

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

    private static void DrawMenu(Canvas c)
    {
        List<MenuRow>[] pages = Pages;
        List<MenuRow> rows = pages[_page];
        _menuIndex = Math.Clamp(_menuIndex, 0, rows.Count - 1);

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
        VGradient(c, x, y, w, S(130), Accent, 0.09f, 0f);
        Rect(c, x, y, w, S(1), Accent, 0.55f);
        Rect(c, x, y + h - S(1), w, S(1), White, 0.06f);
        Rect(c, x, y, S(3), h, Accent, 0.9f);
        Rect(c, x + w - S(18), y, S(18), S(2), Accent);
        Rect(c, x + w - S(2), y, S(2), S(18), Accent);
        Rect(c, x + w - S(18), y + h - S(2), S(18), S(2), Accent, 0.6f);
        Rect(c, x + w - S(2), y + h - S(18), S(2), S(18), Accent, 0.6f);

        float hx = x + pad;
        Spaced(c, hx, y + S(22), "MOD SETTINGS", Accent, 0.72f, 3f, 0.9f);
        Spaced(c, hx, y + S(42), "DYNAMIC CINEMATIC HUD", TextHi, 1.3f, 2.5f);

        string chip = _enabled ? "ACTIVE" : "DISABLED";
        Rgba chipCol = _enabled ? Accent : TextLow;
        float chipW = SpacedW(c, chip, 0.72f, 2f) + S(36);
        float chipX = right - chipW;
        float chipY = y + S(30);
        Rect(c, chipX, chipY, chipW, S(24), chipCol, _enabled ? 0.14f : 0.06f);
        Frame(c, chipX, chipY, chipW, S(24), S(1), chipCol, 0.75f);
        Rect(c, chipX + S(11), chipY + S(9), S(6), S(6), chipCol);
        Spaced(c, chipX + S(24), chipY + S(12) - TextH(c, 0.72f) * 0.5f, chip, chipCol, 0.72f, 2f);

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
        Rect(c, _tabX, y + headH - S(15), _tabW, S(3), Accent);

        string live = !_enabled ? "OFF" : _activeCtx == 0 ? "IDLE" : CtxText(_activeCtx).ToUpperInvariant();
        bool liveOn = _enabled && _activeCtx != 0;
        float liveW = SpacedW(c, live, 0.72f, 2f);
        Spaced(c, right - liveW, ty + S(3), live, liveOn ? Accent : TextLow, 0.72f, 2f);
        Rect(c, right - liveW - S(14), ty + S(3) + TextH(c, 0.72f) * 0.5f - S(3), S(6), S(6), liveOn ? Accent : TextLow);

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
        HGradient(c, x + S(3), _selY, w - S(3), S(38), Accent, 0.24f, 0.0f);
        Rect(c, x, _selY, S(3), S(38), TextHi);

        ry = by;
        for (int i = 0; i < rows.Count; i++)
        {
            MenuRow row = rows[i];
            if (row.Kind == RowHeader)
            {
                DrawHeaderRow(c, row, hx, right, ry);
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
                    DrawSwitch(c, right, cy, row.IsOn!(), sel, key);
                    break;
                case RowSlider:
                    DrawSlider(c, right, cy, row, sel, key);
                    break;
                case RowChoice:
                    DrawChoice(c, right, cy, row.Choice!(), sel);
                    break;
                case RowElement:
                    DrawElementRow(c, right, cy, row.Element, sel, key, hx + S(6) * shift + lw);
                    break;
                case RowAction:
                    DrawAction(c, right, cy, sel);
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
        kx = KeyHint(c, kx, ky, "LEFT / RIGHT", rows[_menuIndex].Kind == RowElement ? "COLUMN" : "ADJUST");
        kx = KeyHint(c, kx, ky, "ENTER", "TOGGLE");
        kx = KeyHint(c, kx, ky, "TAB", "PAGE");
        KeyHint(c, kx, ky, _menuKey.ToString().ToUpperInvariant(), "CLOSE");
    }

    private static float ColumnX(float right, int k) => right - S(46) - (CtxCount - 1 - k) * S(128);

    private static void DrawHeaderRow(Canvas c, MenuRow row, float hx, float right, float ry)
    {
        float ty = ry + S(16);
        float tw = Spaced(c, hx, ty, row.Label, Accent, 0.68f, 3f, 0.95f);
        float ly = ty + TextH(c, 0.68f) * 0.5f;

        if (_page != 1)
        {
            Rect(c, hx + tw + S(14), ly, right - hx - tw - S(14), S(1), White, 0.08f);
            return;
        }

        float first = ColumnX(right, 0) - S(60);
        Rect(c, hx + tw + S(14), ly, first - hx - tw - S(24), S(1), White, 0.08f);
        for (int k = 0; k < CtxCount; k++)
        {
            float cw = SpacedW(c, CtxNames[k], 0.68f, 2f);
            Spaced(c, ColumnX(right, k) - cw * 0.5f, ty, CtxNames[k], TextLow, 0.68f, 2f);
        }
    }

    private static void DrawSwitch(Canvas c, float right, float cy, bool on, bool sel, int key)
    {
        float t = Approach(key + 1, on ? 1f : 0f, 16f);
        float tw = S(42);
        float th = S(20);
        float tx = right - tw;
        float ty = cy - th * 0.5f;

        Rect(c, tx, ty, tw, th, Mix(Off, Accent, t), 0.95f);
        if (sel)
            Frame(c, tx - S(2), ty - S(2), tw + S(4), th + S(4), S(1), TextHi, 0.55f);

        float k = S(14);
        Rect(c, tx + S(3) + t * (tw - k - S(6)), ty + S(3), k, k, Mix(TextMid, White, t));

        string label = on ? "ON" : "OFF";
        float lw = SpacedW(c, label, 0.78f, 2f);
        Spaced(c, tx - S(14) - lw, cy - TextH(c, 0.78f) * 0.5f, label, on ? Accent : TextLow, 0.78f, 2f);
    }

    private static void DrawSlider(Canvas c, float right, float cy, MenuRow row, bool sel, int key)
    {
        float v = row.Get!();
        float range = row.Max - row.Min;
        float t = range <= 0f ? 0f : Math.Clamp((v - row.Min) / range, 0f, 1f);
        float zero = row.Min < 0f ? Math.Clamp(-row.Min / range, 0f, 1f) : 0f;
        float at = Approach(key + 2, t, 20f);

        float valW = S(76);
        float trackW = S(200);
        float tx = right - valW - trackW;
        float th = S(4);

        Rect(c, tx, cy - th * 0.5f, trackW, th, White, 0.12f);
        float a0 = Math.Min(zero, at);
        float a1 = Math.Max(zero, at);
        Rect(c, tx + a0 * trackW, cy - th * 0.5f, (a1 - a0) * trackW, th, Accent, sel ? 1f : 0.7f);
        if (row.Min < 0f)
            Rect(c, tx + zero * trackW - S(1), cy - S(7), S(2), S(14), White, 0.3f);

        float kw = S(6);
        Rect(c, tx + at * trackW - kw * 0.5f, cy - S(9), kw, S(18), sel ? TextHi : TextMid);

        string text = row.Format!(v);
        float vw = TextW(c, text, 0.95f);
        TextMiddle(c, right - vw, cy, text, sel ? TextHi : TextMid, 0.95f);
    }

    private static void DrawChoice(Canvas c, float right, float cy, string value, bool sel)
    {
        Rgba arrow = sel ? Accent : TextLow;
        float aw = TextW(c, ">", 0.95f);
        TextMiddle(c, right - aw, cy, ">", arrow, 0.95f);
        float vw = SpacedW(c, value, 0.85f, 2f);
        float vx = right - aw - S(16) - vw;
        Spaced(c, vx, cy - TextH(c, 0.85f) * 0.5f, value, sel ? TextHi : TextMid, 0.85f, 2f);
        TextMiddle(c, vx - S(16) - TextW(c, "<", 0.95f), cy, "<", arrow, 0.95f);
    }

    private static void DrawElementRow(Canvas c, float right, float cy, int element, bool sel, int key, float labelEnd)
    {
        HudElement e = Elements[element];

        if (e.Level < 1f || e.Want)
        {
            float tag = Approach(key + 6, 1f - e.Level, 14f);
            Spaced(c, labelEnd + S(14), cy - TextH(c, 0.62f) * 0.5f, "HIDDEN", Accent, 0.62f, 2f, 0.4f + 0.6f * tag);
        }

        for (int k = 0; k < CtxCount; k++)
        {
            bool on = (e.Mask & (1 << k)) != 0;
            float t = Approach(key + 3 + k, on ? 1f : 0f, 18f);
            float b = S(20);
            float bx = ColumnX(right, k) - b * 0.5f;
            float by = cy - b * 0.5f;

            Rect(c, bx, by, b, b, Black, 0.35f);
            Frame(c, bx, by, b, b, S(1), Mix(TextLow, Accent, t), 0.9f);
            float inner = (b - S(8)) * t;
            Rect(c, bx + (b - inner) * 0.5f, by + (b - inner) * 0.5f, inner, inner, Accent);

            if (sel && _cell == k)
                Frame(c, bx - S(5), by - S(5), b + S(10), b + S(10), S(2), TextHi, 0.85f);
        }
    }

    private static void DrawAction(Canvas c, float right, float cy, bool sel)
    {
        if (_flash > 0f)
            _flash = Math.Max(0f, _flash - _dt * 0.8f);

        string text = _flash > 0f ? "DONE" : "ENTER";
        float tw = SpacedW(c, text, 0.72f, 2f) + S(20);
        float bh = S(22);
        Rgba col = _flash > 0f ? Accent : sel ? TextHi : TextLow;
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

        string title = "CINEMATIC HUD";
        string state = _enabled ? "ON" : "OFF";
        Rgba col = _enabled ? Accent : TextLow;
        float tw = SpacedW(c, title, 0.85f, 3f);
        float sw = SpacedW(c, state, 0.85f, 3f);
        float w = tw + sw + S(70);
        float h = S(40);
        float x = (c.ClipX - w) * 0.5f;
        float y = S(150) - S(10) * (1f - _ga);

        Rect(c, x + S(4), y + S(5), w, h, Black, 0.3f);
        Rect(c, x, y, w, h, Panel);
        Rect(c, x, y, S(3), h, col);
        Rect(c, x, y, w, S(1), col, 0.5f);
        float ty = y + h * 0.5f - TextH(c, 0.85f) * 0.5f;
        Spaced(c, x + S(22), ty, title, TextHi, 0.85f, 3f);
        Rect(c, x + S(22) + tw + S(13), y + h * 0.5f - S(3), S(6), S(6), col);
        Spaced(c, x + S(22) + tw + S(30), ty, state, col, 0.85f, 3f);
    }
}
