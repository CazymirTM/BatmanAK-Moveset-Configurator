#nullable enable

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;
using Canvas = BmSDK.Engine.Canvas;
using FLinearColor = BmSDK.GameObject.FLinearColor;
using GameViewportClient = BmSDK.Engine.GameViewportClient;

namespace BatmanDeathstrokeSniper;

internal static class SniperMode
{
    private static readonly string[] KeyNames = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "=" };
    private static readonly TimeSpan StuckKeyTimeout = TimeSpan.FromSeconds(8);

    public static string KeyLabel = "7";

    private static bool keyDown;
    private static DateTime keyDownAt = DateTime.MinValue;
    private static bool switchedThisPress;
    private static bool swallowRelease;

    public static bool Installed;

    public static void Reset()
    {
        keyDown = false;
        switchedThisPress = false;
        swallowRelease = false;
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

        if (!switchedThisPress && held.TotalSeconds >= SniperConfig.C.SwitchHold)
        {
            switchedThisPress = true;
            Util.TryCall("switch mode", () => Toggle(controller, equip: true));
        }
    }

    public static void Toggle(RPlayerController controller, bool equip)
    {
        RPawnPlayer? pawn = Sniper.Batman();
        RDisruptorSniper? dis = Sniper.Dis(pawn);

        if (pawn == null || dis == null || pawn.InvManager is not RInventoryManager inventory || !inventory.IsValid)
        {
            ModLog.Info("Mode: Batman has no Disruptor yet; nothing to switch.");

            return;
        }

        bool toSniper = !Sniper.Selected;

        SniperConfig.SetSniperSelected(toSniper);

        if (!toSniper)
        {
            SniperAmmo.RestoreDisruptor(dis, pawn);
        }

        if (equip && !Util.Same(inventory.CurrentGadget, dis))
        {
            bool ok = inventory.SetCurrentGadget(dis, true);

            Util.TryCall("GadgetsUpdated", controller.GadgetsUpdated);
            ModLog.Info("Mode: equipped the " + (toSniper ? "sniper rifle" : "Disruptor") + " (selected=" + ok + ").");
        }

        ModLog.Info("Mode: the Disruptor slot now holds the " + (toSniper ? "sniper rifle" : "Disruptor") + ".");
        ShowSlotToast();
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private static bool ReloadKeyDown()
    {
        try
        {
            return (GetAsyncKeyState(0x52) & 0x8000) != 0;
        }
        catch
        {
            return false;
        }
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.SelectGadget))]
    private static void SelectGadgetRedirect(RPlayerController self, int gadget)
    {
        if (!BatmanArsenalPack.Arsenal.Sniper)
        {
            self.SelectGadget(gadget);
            return;
        }

        try
        {
            if (Installed && Sniper.Selected && ReloadKeyDown() && Sniper.Held(out RPawnPlayer? holder, out RDisruptorSniper? dis) && holder != null)
            {
                swallowRelease = true;
                ModLog.Once("reload-key-bound", "Mode: R also selects gadget " + (gadget + 1) + " in this keymap; while the sniper rifle is held it only reloads.");
                SniperAmmo.StartReload(holder, dis, "R");

                return;
            }

            if (Installed)
            {
                RPawnPlayer? pawn = Sniper.Batman();
                int wheel = pawn != null ? self.PCGadgetIndexToWheelIndex(gadget) : -1;
                int disSlot = Sniper.WheelIndex(pawn);

                if (wheel >= 0 && wheel == disSlot)
                {
                    keyDown = true;
                    keyDownAt = DateTime.UtcNow;
                    switchedThisPress = false;

                    if (gadget >= 0 && gadget < KeyNames.Length)
                    {
                        KeyLabel = KeyNames[gadget];
                    }
                }
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("select-failed", "Mode: key handling failed: " + Util.Unwrap(exception));
        }

        self.SelectGadget(gadget);
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.SelectGadgetReleased))]
    private static void SelectGadgetReleasedRedirect(RPlayerController self)
    {
        if (!BatmanArsenalPack.Arsenal.Sniper)
        {
            self.SelectGadgetReleased();
            return;
        }

        if (swallowRelease)
        {
            swallowRelease = false;

            return;
        }

        bool switched = keyDown && switchedThisPress;

        keyDown = false;
        switchedThisPress = false;

        if (switched)
        {
            try { self.KeyboardQuickGadget = -1; } catch { }
        }

        self.SelectGadgetReleased();
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.FireQuickGadgetType))]
    private static void FireQuickGadgetTypeRedirect(RPlayerController self, RPawnPlayer.EQuickGadgetType type, bool overrideCombatMove)
    {
        if (!BatmanArsenalPack.Arsenal.Sniper)
        {
            self.FireQuickGadgetType(type, overrideCombatMove);
            return;
        }

        if (SniperPad.BlockGameQuickfire())
        {
            return;
        }

        self.FireQuickGadgetType(type, overrideCombatMove);
    }

    private static string SwitchHint => SniperPad.UsingPad && SniperConfig.C.PadSwitch != 0
        ? SniperPad.ButtonName(SniperConfig.C.PadSwitch) + " ON THE GADGET WHEEL"
        : "HOLD " + KeyLabel;

    private static readonly (byte r, byte g, byte b) OrangeAccent = (240, 125, 25);
    private static readonly (byte r, byte g, byte b) BlueAccent = (90, 170, 255);

    private static readonly (byte r, byte g, byte b) RedAccent = (225, 30, 30);
    private static readonly (byte r, byte g, byte b) GreyAccent = (170, 175, 185);

    public static void ToggleLaser()
    {
        SniperConfig.Settings c = SniperConfig.C;

        c.Laser = c.Laser > 0 ? 0 : 1;
        SniperConfig.Save();
        ModLog.Info("Laser: toggled " + (c.Laser > 0 ? "ON" : "OFF") + " (" + c.LaserKey + ").");

        string key = c.LaserKey.ToString().ToUpperInvariant();

        if (c.Laser > 0)
        {
            ShowToast("LASER SIGHT ON", "RED LASER WHILE THE SNIPER RIFLE IS AIMED  |  " + key + " TO TURN IT OFF", RedAccent);
        }
        else
        {
            ShowToast("LASER SIGHT OFF", "NO LASER WHEN AIMING  |  " + key + " TO TURN IT ON", GreyAccent);
        }
    }

    public static void ShowSlotToast()
    {
        if (Sniper.Selected)
        {
            ShowToast(SniperConfig.C.ArkhamRifle ? "ARKHAM KNIGHT'S SNIPER RIFLE" : "SNIPER RIFLE", SniperAmmo.MagSize + "-ROUND MAGAZINE  |  R TO RELOAD  |  " + SniperConfig.C.LaserKey.ToString().ToUpperInvariant() + " LASER  |  " + SwitchHint + " FOR THE DISRUPTOR", OrangeAccent);
        }
        else
        {
            ShowToast("DISRUPTOR", "WEAPON SABOTAGE  |  " + SwitchHint + " FOR THE SNIPER RIFLE", BlueAccent);
        }
    }

    private static float toastLeft;
    private const float ToastTime = 1.8f;
    private static long lastDraw;
    private static Texture2D? white;
    private static Font? font;
    private static string toastTitle = "";
    private static string toastSub = "";
    private static (byte r, byte g, byte b) toastAccent = OrangeAccent;

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
        if (!BatmanArsenalPack.Arsenal.Sniper)
        {
            self.PostRender(canvas);
            return;
        }

        self.PostRender(canvas);

        try
        {
            DrawToast(canvas);
        }
        catch
        {
        }

        try
        {
            if (Installed)
            {
                SniperPad.Frame();
                SniperPad.DrawWheelHint(canvas);
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("pad-frame", "Pad: frame failed: " + Util.Unwrap(exception));
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
            float titleScale = 1.55f * s;
            float subScale = 0.8f * s;

            c.TextSize(toastTitle, out float titleW, out float titleH, titleScale, titleScale);
            c.TextSize(toastSub, out float subW, out float subH, subScale, subScale);

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
            Text(c, x + (w - titleW) * 0.5f, y + padY, toastTitle, 245, 240, 240, alpha, titleScale);
            Text(c, x + (w - subW) * 0.5f, y + padY + titleH + 4f * s, toastSub, accent.r, accent.g, accent.b, 0.9f * alpha, subScale);
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

        var color = new FLinearColor { R = Linear(r), G = Linear(g), B = Linear(b), A = alpha };

        c.DrawTile(white!, MathF.Round(w), MathF.Round(h), 0f, 0f, 32f, 32f, color, false, EngineTypes.EBlendMode.BLEND_Translucent);
    }

    internal static void Text(Canvas c, float x, float y, string text, byte r, byte g, byte b, float alpha, float scale)
    {
        c.SetDrawColor(r, g, b, (byte)Math.Clamp(255f * alpha, 0f, 255f));
        c.SetPos(MathF.Round(x), MathF.Round(y), 0f);
        c.DrawText(text, false, scale, scale, out _);
    }
}
