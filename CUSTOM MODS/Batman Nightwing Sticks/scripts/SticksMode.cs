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

namespace BatmanNightwingSticks;

internal static class SticksMode
{
    private static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StuckKeyTimeout = TimeSpan.FromSeconds(8);

    private static bool keyDown;
    private static DateTime keyDownAt = DateTime.MinValue;
    private static bool switchedThisPress;

    public static string ModeName => Sticks.BlastMode ? "ELECTROMAGNETIC BLAST" : "RICOCHET STICKS";

    public static void Reset()
    {
        keyDown = false;
        switchedThisPress = false;
    }

    public static void KeyPressed()
    {
        keyDown = true;
        keyDownAt = DateTime.UtcNow;
        switchedThisPress = false;
    }

    public static bool KeyReleased()
    {
        bool switched = switchedThisPress;

        keyDown = false;
        switchedThisPress = false;

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
            keyDown = false;

            return;
        }

        if (!switchedThisPress && held >= HoldTime)
        {
            switchedThisPress = true;
            Util.TryCall("switch mode", () => Toggle(controller));
        }
    }

    private static void Toggle(RPlayerController controller)
    {
        if (Sticks.Blast == null || !Sticks.Blast.IsValid)
        {
            ModLog.Info("Mode: the electromagnetic blast isn't installed; staying on the sticks.");

            return;
        }

        bool wasHeld = Sticks.HeldBy(out RPawnPlayer? pawn, out RInventoryManager? inventory);

        Sticks.BlastMode = !Sticks.BlastMode;

        if (wasHeld && inventory != null && Sticks.Active is RInventoryGadget active && active.IsValid)
        {
            bool selected = inventory.SetCurrentGadget(active, true);

            Util.TryCall("GadgetsUpdated", controller.GadgetsUpdated);
            ModLog.Info("Mode: switched to " + ModeName + " (now holding " + Util.ClassName(inventory.CurrentGadget) + ", selected=" + selected + ").");
        }
        else
        {
            ModLog.Info("Mode: switched to " + ModeName + " (it will be equipped next time the sticks are selected).");
        }

        ShowToast();
    }

    private static float toastLeft;
    private const float ToastTime = 1.8f;
    private static long lastDraw;
    private static Texture2D? white;
    private static Font? font;

    public static void ShowToast() => toastLeft = ToastTime;

    [Redirect(typeof(GameViewportClient), "PostRender")]
    private static void ViewportPostRenderRedirect(GameViewportClient self, Canvas canvas)
    {
        self.PostRender(canvas);

        try
        {
            DrawToast(canvas);
        }
        catch
        {
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

        if (white == null || !white.IsValid)
        {
            white = Game.FindObject<Texture2D>("EngineResources.WhiteSquareTexture");
        }

        if (font == null || !font.IsValid)
        {
            font = Game.FindObject<Font>("BmFonts.EngineSmallFont");
        }

        if (white == null)
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
            bool blast = Sticks.BlastMode;
            (byte r, byte g, byte b) accent = blast ? ((byte)120, (byte)200, (byte)255) : ((byte)90, (byte)170, (byte)255);

            string title = ModeName;
            string sub = blast ? "NIGHTWING ESCRIMA STICKS  |  HOLD 6 FOR RICOCHET" : "NIGHTWING ESCRIMA STICKS  |  HOLD 6 FOR BLAST";
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

            Rect(c, x, y, w, h, 6, 10, 16, 0.72f * alpha);
            Rect(c, x, y, w, 2f * s, accent.r, accent.g, accent.b, alpha);
            Rect(c, x, y + h - 2f * s, w, 2f * s, accent.r, accent.g, accent.b, 0.5f * alpha);
            Rect(c, x, y, 4f * s, h, accent.r, accent.g, accent.b, alpha);

            Text(c, x + (w - titleW) * 0.5f, y + padY, title, 235, 245, 255, alpha, titleScale);
            Text(c, x + (w - subW) * 0.5f, y + padY + titleH + 4f * s, sub, accent.r, accent.g, accent.b, 0.9f * alpha, subScale);
        }
        finally
        {
            c.Font = oldFont;
        }
    }

    private static float EaseOut(float t) => 1f - MathF.Pow(1f - Math.Clamp(t, 0f, 1f), 3f);

    private static float Linear(byte v) => MathF.Pow(v / 255f, 2.2f);

    private static void Rect(Canvas c, float x, float y, float w, float h, byte r, byte g, byte b, float alpha)
    {
        if (w <= 0f || h <= 0f)
        {
            return;
        }

        c.SetPos(MathF.Round(x), MathF.Round(y), 0f);

        FLinearColor color = new FLinearColor { R = Linear(r), G = Linear(g), B = Linear(b), A = alpha };

        c.DrawTile(white!, MathF.Round(w), MathF.Round(h), 0f, 0f, 32f, 32f, color, false, EngineTypes.EBlendMode.BLEND_Translucent);
    }

    private static void Text(Canvas c, float x, float y, string text, byte r, byte g, byte b, float alpha, float scale)
    {
        c.SetDrawColor(r, g, b, (byte)Math.Clamp(255f * alpha, 0f, 255f));
        c.SetPos(MathF.Round(x), MathF.Round(y), 0f);
        c.DrawText(text, false, scale, scale, out _);
    }
}
