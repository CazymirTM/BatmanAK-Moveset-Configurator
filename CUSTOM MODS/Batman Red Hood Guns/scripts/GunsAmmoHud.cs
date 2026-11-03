#nullable enable

using System;
using System.Diagnostics;
using System.Linq;
using BmSDK;
using BmSDK.Engine;
using BmSDK.Framework;
using Canvas = BmSDK.Engine.Canvas;

namespace BatmanRedHoodGuns;

internal static class GunsAmmoHud
{
    private const float FadeTime = 0.15f;

    public static float OffsetX = 88f;
    public static float OffsetY = -30f;

    private static float alpha;
    private static long lastDraw;
    private static bool fontsLogged;

    private static readonly (byte r, byte g, byte b) Accent = (225, 45, 40);

    public static void Draw(Canvas? c)
    {
        long now = Stopwatch.GetTimestamp();
        float dt = lastDraw == 0 ? 0f : Math.Clamp((float)Stopwatch.GetElapsedTime(lastDraw, now).TotalSeconds, 0f, 0.1f);

        lastDraw = now;

        bool show = GunsAim.IsAimed && Guns.HeldBy(out _, out _);

        alpha = show ? MathF.Min(1f, alpha + dt / FadeTime) : MathF.Max(0f, alpha - dt / FadeTime);

        if (alpha <= 0f || c == null || !GunsMode.LoadResources())
        {
            return;
        }

        LogFonts();

        float s = Math.Clamp(c.ClipY / 1080f, 0.75f, 2.5f);
        float a = GunsMode.EaseOut(alpha);
        float x = (0.5f + GunsHud.ReticleX) * c.ClipX + OffsetX * s;
        float y = (0.5f + GunsHud.ReticleY) * c.ClipY + OffsetY * s;

        Font? oldFont = c.Font;

        if (GunsMode.HudFont != null)
        {
            c.Font = GunsMode.HudFont;
        }

        try
        {
            DrawCounter(c, x, y, s, a);
        }
        finally
        {
            c.Font = oldFont;
        }
    }

    private static void DrawCounter(Canvas c, float x, float y, float s, float a)
    {
        int ammo = GunsHud.Ammo;
        int clip = GunsHud.ClipSize;
        bool reloading = GunsHud.Reloading;
        bool low = !reloading && ammo <= clip / 4;

        float tickW = 4f * s;
        float tickH = 13f * s;
        float gap = 3f * s;
        float rowW = clip * tickW + (clip - 1) * gap;

        string count = reloading ? "--" : ammo.ToString();
        string max = "/" + clip;
        float bigScale = 2.1f * s;
        float smallScale = 1.0f * s;

        c.TextSize(count, out float countW, out float countH, bigScale, bigScale);
        c.TextSize(max, out float maxW, out float maxH, smallScale, smallScale);

        float padX = 10f * s;
        float padY = 7f * s;
        float rowY = y + countH + 4f * s;
        float panelW = Math.Max(rowW, countW + 4f * s + maxW) + padX * 2f;
        float panelH = countH + 4f * s + tickH + padY * 2f;

        GunsMode.Rect(c, x - padX, y - padY, panelW, panelH, 10, 8, 10, 0.45f * a);
        GunsMode.Rect(c, x - padX, y - padY, 3f * s, panelH, Accent.r, Accent.g, Accent.b, a);

        (byte r, byte g, byte b) countColour = low || reloading ? Accent : ((byte)240, (byte)240, (byte)240);

        GunsMode.Text(c, x, y, count, countColour.r, countColour.g, countColour.b, a, bigScale);
        GunsMode.Text(c, x + countW + 4f * s, y + countH - maxH - 2f * s, max, 170, 175, 185, 0.9f * a, smallScale);

        if (reloading)
        {
            float pulse = 0.6f + 0.4f * MathF.Sin((float)Stopwatch.GetTimestamp() / Stopwatch.Frequency * 9f);
            string label = "RELOADING";
            float labelScale = 0.95f * s;

            c.TextSize(label, out float labelW, out float labelH, labelScale, labelScale);
            GunsMode.Text(c, x + (rowW - labelW) * 0.5f, rowY + (tickH - labelH) * 0.5f - 2f * s, label, Accent.r, Accent.g, Accent.b, pulse * a, labelScale);
            GunsMode.Rect(c, x, rowY + tickH - 2f * s, rowW, 2f * s, 60, 60, 66, 0.8f * a);
            GunsMode.Rect(c, x, rowY + tickH - 2f * s, rowW * GunsHud.ReloadProgress, 2f * s, Accent.r, Accent.g, Accent.b, a);

            return;
        }

        for (int index = 0; index < clip; index++)
        {
            float tx = x + index * (tickW + gap);
            bool full = index < ammo;

            if (full)
            {
                GunsMode.Rect(c, tx, rowY, tickW, tickH, low ? Accent.r : (byte)235, low ? Accent.g : (byte)235, low ? Accent.b : (byte)235, a);
            }
            else
            {
                GunsMode.Rect(c, tx, rowY, tickW, tickH, 120, 120, 128, 0.25f * a);
            }
        }
    }

    private static void LogFonts()
    {
        if (fontsLogged)
        {
            return;
        }

        fontsLogged = true;

        try
        {
            ModLog.Info("Hud: fonts loaded: " + string.Join(", ", Game.FindObjects<Font>().Where(font => font != null && font.IsValid).Select(font => Util.FullName(font)).Distinct().Take(40)) + ".");
        }
        catch
        {
        }
    }
}
