#nullable enable

using System;
using System.Diagnostics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;
using Canvas = BmSDK.Engine.Canvas;
using FLinearColor = BmSDK.GameObject.FLinearColor;
using GameViewportClient = BmSDK.Engine.GameViewportClient;

namespace BatmanRedHoodGuns;

internal static class GunsMode
{
    private static readonly TimeSpan StuckKeyTimeout = TimeSpan.FromSeconds(8);

    private static bool keyDown;
    private static DateTime keyDownAt = DateTime.MinValue;
    private static bool switchedThisPress;
    private static int deferredGadget = -1;

    public static bool GunsSelected => GunsConfig.C.GunsSelected;

    public static string KeyLabel = "4";

    public static void Reset()
    {
        keyDown = false;
        switchedThisPress = false;
        deferredGadget = -1;
    }

    public static void KeyPressed(int deferred)
    {
        keyDown = true;
        keyDownAt = DateTime.UtcNow;
        switchedThisPress = false;
        deferredGadget = deferred;
    }

    public static bool KeyReleased(out int deferred)
    {
        bool switched = switchedThisPress;

        deferred = keyDown ? deferredGadget : -1;
        keyDown = false;
        switchedThisPress = false;
        deferredGadget = -1;

        return switched;
    }

    public static void Tick(RPlayerController controller)
    {
        if (!keyDown)
        {
            return;
        }

        TimeSpan held = DateTime.UtcNow - keyDownAt;

        if (held > StuckKeyTimeout)
        {
            Reset();

            return;
        }

        if (!switchedThisPress && held.TotalSeconds >= GunsConfig.C.SwitchHold)
        {
            switchedThisPress = true;
            Util.TryCall("switch slot", () => Toggle(controller));
        }
    }

    private static void Toggle(RPlayerController controller) => Toggle(controller, equip: true);

    public static void SwitchFromWheel(RPlayerController controller)
    {
        Toggle(controller, equip: false);
        ModLog.Info("Mode: switched from the gamepad on the gadget wheel; closing the wheel on this slot equips it.");
    }

    private static void Toggle(RPlayerController controller, bool equip)
    {
        if (Guns.Batman()?.InvManager is not RInventoryManager inventory || !inventory.IsValid || Guns.Gun == null || !Guns.Gun.IsValid)
        {
            return;
        }

        bool toGuns = !GunsSelected;

        GunsConfig.SetGunsSelected(toGuns);
        GunsWheel.ApplyMode(controller, inventory, equip);
        ModLog.Info("Mode: the " + Guns.ReplacedGadget + " slot now holds " + (toGuns ? "Red Hood's guns" : Guns.ReplacedGadget) + ".");
        ShowSlotToast();
    }

    public static void ToggleAlwaysInHands()
    {
        bool on = !GunsConfig.C.AlwaysInHands;

        GunsConfig.SetAlwaysInHands(on);
        ModLog.Info("Mode: pistols always in hand " + (on ? "ON" : "OFF") + ".");

        if (on)
        {
            ShowToast("GUNS ALWAYS IN HAND", "PISTOLS STAY DRAWN WITH EVERY GADGET  |  " + HandsHint + " TO HOLSTER", RedAccent);
        }
        else
        {
            ShowToast("GUNS HOLSTERED", "PISTOLS ONLY DRAWN WHEN SELECTED  |  " + HandsHint + " TO KEEP THEM DRAWN", GreyAccent);
        }
    }

    private static string SwitchHint => GunsPad.UsingPad && GunsConfig.C.PadSwitch != 0 ? GunsPad.ButtonName(GunsConfig.C.PadSwitch) + " ON THE GADGET WHEEL" : "HOLD " + KeyLabel;

    private static string HandsHint => GunsPad.UsingPad && GunsConfig.C.PadHands != 0 ? GunsPad.ButtonName(GunsConfig.C.PadHands) + " ON THE GADGET WHEEL" : "F5";

    private static readonly (byte r, byte g, byte b) RedAccent = (225, 45, 40);
    private static readonly (byte r, byte g, byte b) BlueAccent = (90, 170, 255);
    private static readonly (byte r, byte g, byte b) GreyAccent = (170, 175, 185);

    private static void ShowSlotToast()
    {
        if (GunsSelected)
        {
            ShowToast("RED HOOD'S GUNS", "DUAL HANDGUNS  |  " + SwitchHint + " FOR " + Guns.ReplacedTitle, RedAccent);
        }
        else
        {
            ShowToast(Guns.ReplacedTitle, "REMOTE ELECTRICAL CHARGE  |  " + SwitchHint + " FOR RED HOOD'S GUNS", BlueAccent);
        }
    }

    private static float toastLeft;
    private const float ToastTime = 1.8f;
    private static long lastDraw;
    private static Texture2D? white;
    private static Font? font;

    private static string toastTitle = "";
    private static string toastSub = "";
    private static (byte r, byte g, byte b) toastAccent = RedAccent;

    private static void ShowToast(string title, string sub, (byte r, byte g, byte b) accent)
    {
        toastTitle = title;
        toastSub = sub;
        toastAccent = accent;
        toastLeft = ToastTime;
    }

    [Redirect(typeof(GameViewportClient), "PostRender")]
    private static void ViewportPostRenderRedirect(GameViewportClient self, Canvas canvas)
    {
        self.PostRender(canvas);

        try
        {
            GunsAmmoHud.Draw(canvas);
        }
        catch (Exception exception)
        {
            ModLog.Once("ammo-draw", "Hud: drawing the ammo counter failed: " + Util.Unwrap(exception));
        }

        try
        {
            DrawToast(canvas);
        }
        catch
        {
        }

        try
        {
            GunsPad.Frame();
            GunsPad.DrawWheelHint(canvas);
        }
        catch (Exception exception)
        {
            ModLog.Once("pad-hint", "Pad: drawing the wheel hint failed: " + Util.Unwrap(exception));
        }
    }

    private static void DrawToast(Canvas? c)
    {
        long now = Stopwatch.GetTimestamp();
        float dt = lastDraw == 0 ? 0f : Math.Clamp((float)Stopwatch.GetElapsedTime(lastDraw, now).TotalSeconds, 0f, 0.1f);

        lastDraw = now;

        if (toastLeft <= 0f || c == null)
        {
            return;
        }

        toastLeft -= dt;

        if (!LoadResources())
        {
            return;
        }

        float age = ToastTime - toastLeft;
        float alpha = Math.Clamp(Math.Min(age / 0.15f, toastLeft / 0.35f), 0f, 1f);
        float slide = (1f - EaseOut(age / 0.25f)) * 18f;
        float s = Math.Clamp(c.ClipY / 1080f, 0.75f, 2.5f);

        Font? oldFont = c.Font;

        if (font != null)
        {
            c.Font = font;
        }

        try
        {
            (byte r, byte g, byte b) accent = toastAccent;

            string title = toastTitle;
            string sub = toastSub;
            float titleScale = 1.55f * s;
            float subScale = 0.8f * s;

            c.TextSize(title, out float titleW, out float titleH, titleScale, titleScale);
            c.TextSize(sub, out float subW, out float subH, subScale, subScale);

            float padX = 28f * s;
            float padY = 12f * s;
            float w = Math.Max(titleW, subW) + padX * 2f;
            float h = titleH + subH + padY * 2f + 4f * s;
            float x = (c.ClipX - w) * 0.5f;
            float y = c.ClipY * 0.68f + slide * s;

            Rect(c, x, y, w, h, 10, 8, 10, 0.72f * alpha);
            Rect(c, x, y, w, 2f * s, accent.r, accent.g, accent.b, alpha);
            Rect(c, x, y + h - 2f * s, w, 2f * s, accent.r, accent.g, accent.b, 0.5f * alpha);
            Rect(c, x, y, 4f * s, h, accent.r, accent.g, accent.b, alpha);

            Text(c, x + (w - titleW) * 0.5f, y + padY, title, 245, 240, 240, alpha, titleScale);
            Text(c, x + (w - subW) * 0.5f, y + padY + titleH + 4f * s, sub, accent.r, accent.g, accent.b, 0.9f * alpha, subScale);
        }
        finally
        {
            c.Font = oldFont;
        }
    }

    internal static Font? HudFont => font;

    internal static bool LoadResources()
    {
        if (white == null || !white.IsValid)
        {
            white = Game.FindObject<Texture2D>("EngineResources.WhiteSquareTexture");
        }

        if (font == null || !font.IsValid)
        {
            font = Game.FindObject<Font>("BmFonts.EngineSmallFont");
        }

        return white != null;
    }

    internal static float EaseOut(float t) => 1f - MathF.Pow(1f - Math.Clamp(t, 0f, 1f), 3f);

    private static float Linear(byte v) => MathF.Pow(v / 255f, 2.2f);

    internal static void Rect(Canvas c, float x, float y, float w, float h, byte r, byte g, byte b, float alpha)
    {
        if (w <= 0f || h <= 0f)
        {
            return;
        }

        c.SetPos(MathF.Round(x), MathF.Round(y), 0f);

        FLinearColor color = new FLinearColor { R = Linear(r), G = Linear(g), B = Linear(b), A = alpha };

        c.DrawTile(white!, MathF.Round(w), MathF.Round(h), 0f, 0f, 32f, 32f, color, false, EngineTypes.EBlendMode.BLEND_Translucent);
    }

    internal static void Text(Canvas c, float x, float y, string text, byte r, byte g, byte b, float alpha, float scale)
    {
        c.SetDrawColor(r, g, b, (byte)Math.Clamp(255f * alpha, 0f, 255f));
        c.SetPos(MathF.Round(x), MathF.Round(y), 0f);
        c.DrawText(text, false, scale, scale, out _);
    }
}
