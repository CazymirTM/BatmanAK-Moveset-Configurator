#nullable enable

using System;
using System.Diagnostics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;
using BmSDK.GFxUI;
using Canvas = BmSDK.Engine.Canvas;
using FLinearColor = BmSDK.GameObject.FLinearColor;
using GameViewportClient = BmSDK.Engine.GameViewportClient;

namespace BatmanAssaultRifle;

internal static class RifleMode
{
    private static readonly string[] KeyNames = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "=" };
    private static readonly TimeSpan StuckKeyTimeout = TimeSpan.FromSeconds(8);

    public static string KeyLabel = "1";
    public static bool Installed;

    private static bool keyDown;
    private static DateTime keyDownAt = DateTime.MinValue;
    private static bool switchedThisPress;
    private static bool swallowRelease;

    public const string ThugLabel = "ASSAULT RIFLE";
    public const string M4Label = "M4 CARBINE";
    public static string RifleLabel => RifleProps.M4Active ? M4Label : ThugLabel;
    public static bool IsRifleLabel(string t) => t == ThugLabel || t == M4Label;
    private const string FirePrompt = "Fire Assault Rifle";
    private const string CornerLabelPath = "GrandLeftParent.LeftParent.GadgetName.NameText.text";
    private const string WheelTitlePath = "GrandRightParent.RightParent.WheelMovie.SelectedGadgetName.Child.NameText.text";

    private static string? labelRoot;
    private static IntPtr labelMovie = IntPtr.Zero;
    private static bool labelChanged = true;
    private static string? batarangLabel;

    public static void Reset()
    {
        keyDown = false;
        switchedThisPress = false;
        swallowRelease = false;
        labelChanged = true;
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

        if (!switchedThisPress && held.TotalSeconds >= RifleConfig.C.SwitchHold)
        {
            switchedThisPress = true;
            Util.TryCall("switch mode", () => Toggle(controller, equip: true));
        }
    }

    public static void Toggle(RPlayerController controller, bool equip)
    {
        RPawnPlayer? pawn = Rifle.Batman();
        RBatarang? bat = Rifle.Bat(pawn);

        if (pawn == null || bat == null || pawn.InvManager is not RInventoryManager inventory || !inventory.IsValid)
        {
            ModLog.Info("Mode: Batman has no Batarang yet; nothing to switch.");

            return;
        }

        bool toRifle = !Rifle.Selected;

        RifleConfig.SetRifleSelected(toRifle);

        if (!toRifle)
        {
            RifleAmmo.Cancel("cancelled: back to the Batarang");
        }

        if (equip && !Util.Same(inventory.CurrentGadget, bat))
        {
            bool ok = inventory.SetCurrentGadget(bat, true);

            Util.TryCall("GadgetsUpdated", controller.GadgetsUpdated);
            ModLog.Info("Mode: equipped the " + (toRifle ? "assault rifle" : "Batarang") + " (selected=" + ok + ").");
        }

        if (Util.Same(inventory.CurrentGadget, bat) && controller.RadialGadgetSelect is RGFxMovieRadialGadgetSelect corner && corner.IsValid && !corner.bWheelOpen)
        {
            int slot = Rifle.WheelIndex(pawn);

            Util.TryCall("ShowCurrentGadget", () => corner.ShowCurrentGadget(slot));
        }

        RifleIcon.CornerWanted = toRifle && Util.Same(inventory.CurrentGadget, bat);
        Util.TryCall("icon swap", () => RifleIcon.Frame(toRifle && Installed));

        labelChanged = true;
        ModLog.Info("Mode: the Batarang slot now holds the " + (toRifle ? "assault rifle" : "Batarang") + ".");
        ShowSlotToast();
    }

    private static bool FindLabelRoot(RGFxMovieRadialGadgetSelect movie)
    {
        if (labelRoot != null && labelMovie == movie.Ptr)
        {
            return true;
        }

        foreach (string candidate in new[] { "", "root.", "_root.", "root1." })
        {
            GFxObject? found = movie.GetVariableObject(new FString(candidate + "GrandLeftParent"));

            if (found != null && found.IsValid)
            {
                labelRoot = candidate;
                labelMovie = movie.Ptr;

                return true;
            }
        }

        return false;
    }

    private static string BatarangTitle()
    {
        batarangLabel ??= GameObject.Localize(new FString("GadgetSelect"), new FString(Rifle.BatarangClass), new FString("GFxUI")).ToString() ?? "";

        return batarangLabel;
    }

    public static void UpdateLabel(RPlayerController controller, bool rifleHeld)
    {
        if (!rifleHeld && !labelChanged)
        {
            return;
        }

        try
        {
            if (controller.RadialGadgetSelect is not RGFxMovieRadialGadgetSelect movie || !movie.IsValid || !FindLabelRoot(movie))
            {
                return;
            }

            string path = labelRoot + CornerLabelPath;
            string text = movie.GetVariableString(new FString(path)).ToString() ?? "";

            if (rifleHeld)
            {
                if (text != RifleLabel)
                {
                    movie.SetVariableString(new FString(path), new FString(RifleLabel));
                }

                labelChanged = true;
            }
            else
            {
                if (IsRifleLabel(text) && BatarangTitle().Length > 0)
                {
                    movie.SetVariableString(new FString(path), new FString(BatarangTitle()));
                }

                labelChanged = false;
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("label-failed", "Mode: setting the corner label failed: " + Util.Unwrap(exception));
        }
    }

    public static void UpdateWheelTitle()
    {
        if (Game.GetPlayerController(0) is not RPlayerController controller || !controller.IsValid ||
            controller.RadialGadgetSelect is not RGFxMovieRadialGadgetSelect movie || !movie.IsValid || !movie.bWheelOpen || !FindLabelRoot(movie))
        {
            return;
        }

        string bt = BatarangTitle();

        if (bt.Length == 0)
        {
            return;
        }

        string path = labelRoot + WheelTitlePath;
        string title = movie.GetVariableString(new FString(path)).ToString() ?? "";
        string? wanted = null;

        if (BatmanArsenalPack.Arsenal.Rifle && Rifle.Selected)
        {
            if (title == bt || (IsRifleLabel(title) && title != RifleLabel))
            {
                wanted = RifleLabel;
            }
        }
        else if (IsRifleLabel(title))
        {
            wanted = bt;
        }

        if (wanted != null && wanted != title)
        {
            movie.SetVariableString(new FString(path), new FString(wanted));
        }
    }

    [Redirect(typeof(RBatarang), nameof(RBatarang.GetProjectilePrompt))]
    private static void GetProjectilePromptRedirect(RBatarang self, RHUDPrompt prompt, bool flag)
    {
        try
        {
            if (BatmanArsenalPack.Arsenal.Rifle && Installed && Rifle.Selected && Rifle.IsBatmansBatarang(self, out _))
            {
                prompt.AddLine(new FString(FirePrompt), RHUDPrompt.EControlIcon.CI_UseGadget, false, false, false, false);

                return;
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("prompt-failed", "Mode: setting the fire prompt failed: " + Util.Unwrap(exception));
        }

        self.GetProjectilePrompt(prompt, flag);
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.SelectGadget))]
    private static void SelectGadgetRedirect(RPlayerController self, int gadget)
    {
        if (!BatmanArsenalPack.Arsenal.Rifle)
        {
            self.SelectGadget(gadget);
            return;
        }

        try
        {
            if (Installed && Util.KeyDown(0x52) && Rifle.HeldBy(out RPawnPlayer? holder, out RBatarang? bat) && holder != null)
            {
                swallowRelease = true;
                ModLog.Once("reload-key-bound", "Mode: R also selects gadget " + (gadget + 1) + " in this keymap; while the assault rifle is held it only reloads.");
                RifleAmmo.StartReload(holder, bat, "R");

                return;
            }

            if (Installed)
            {
                RPawnPlayer? pawn = Rifle.Batman();
                int wheel = pawn != null ? self.PCGadgetIndexToWheelIndex(gadget) : -1;
                int slot = Rifle.WheelIndex(pawn);

                if (wheel >= 0 && wheel == slot)
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
        if (!BatmanArsenalPack.Arsenal.Rifle)
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
        if (BatmanArsenalPack.Arsenal.Rifle && RiflePad.BlockGameQuickfire())
        {
            return;
        }

        self.FireQuickGadgetType(type, overrideCombatMove);
    }

    private static string SwitchHint => RiflePad.UsingPad && RifleConfig.C.PadSwitch != 0
        ? RiflePad.ButtonName(RifleConfig.C.PadSwitch) + " ON THE GADGET WHEEL"
        : "HOLD " + KeyLabel;

    private static readonly (byte r, byte g, byte b) OliveAccent = (150, 170, 70);
    private static readonly (byte r, byte g, byte b) BlueAccent = (90, 170, 255);

    public static void ShowSlotToast()
    {
        if (Rifle.Selected)
        {
            ShowToast(RifleLabel, RifleAmmo.MagSize + "-ROUND MAGAZINE  |  FULL AUTO  |  R TO RELOAD  |  " + SwitchHint + " FOR THE BATARANG", OliveAccent);
        }
        else
        {
            ShowToast("BATARANG", "THROW AND STUN  |  " + SwitchHint + " FOR THE " + RifleLabel, BlueAccent);
        }
    }

    private static float toastLeft;
    private const float ToastTime = 1.8f;
    private static long lastDraw;
    private static Texture2D? white;
    private static Font? font;
    private static string toastTitle = "";
    private static string toastSub = "";
    private static (byte r, byte g, byte b) toastAccent = OliveAccent;

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

        try { WheelPages.Frame(); } catch (Exception exception) { ModLog.Once("wheel-pages", "Wheel: pages failed: " + Util.Unwrap(exception)); }

        if (!BatmanArsenalPack.Arsenal.Rifle)
        {
            try { UpdateWheelTitle(); } catch { }

            return;
        }

        long now = Stopwatch.GetTimestamp();
        float dt = lastDraw == 0 ? 0f : Math.Clamp((float)Stopwatch.GetElapsedTime(lastDraw, now).TotalSeconds, 0f, 0.1f);

        lastDraw = now;

        try
        {
            DrawToast(canvas, dt);
        }
        catch
        {
        }

        try
        {
            RifleIcon.Frame(Installed && Rifle.Selected);

            if (Installed)
            {
                UpdateWheelTitle();
                RiflePad.Frame();
                RiflePad.DrawWheelHint(canvas);
                RifleHud.Draw(canvas, dt);
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("post-render", "Mode: frame failed: " + Util.Unwrap(exception));
        }
    }

    private static void DrawToast(Canvas? c, float dt)
    {
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

internal static class RifleHud
{
    private const float FadeTime = 0.15f;
    private static float alpha;
    private static readonly (byte r, byte g, byte b) Accent = (150, 170, 70);
    private static readonly (byte r, byte g, byte b) Warn = (225, 45, 40);

    public static bool Show;

    public static void Draw(Canvas? c, float dt)
    {
        alpha = Show ? MathF.Min(1f, alpha + dt / FadeTime) : MathF.Max(0f, alpha - dt / FadeTime);

        if (alpha <= 0f || c == null || !RifleMode.LoadResources())
        {
            return;
        }

        float s = Math.Clamp(c.ClipY / 1080f, 0.75f, 2.5f);
        float a = RifleMode.EaseOut(alpha);

        float rx = c.ClipX * 0.5f;
        float ry = c.ClipY * 0.5f;

        if (RifleReticle.Offset(out float ox, out float oy))
        {
            rx += ox * c.ClipX;
            ry += oy * c.ClipY;
        }

        float x = Math.Clamp(rx + 138f * s, 10f * s, c.ClipX - 150f * s);
        float y = Math.Clamp(ry + 8f * s, 10f * s, c.ClipY - 70f * s);
        Font? oldFont = c.Font;

        if (RifleMode.HudFont != null)
        {
            c.Font = RifleMode.HudFont;
        }

        try
        {
            int ammo = RifleAmmo.Mag;
            int clip = Math.Max(1, RifleAmmo.MagSize);
            bool reloading = RifleAmmo.Reloading;
            bool low = !reloading && ammo <= Math.Max(1, clip / 5);
            int perRow = Math.Min(clip, 40);
            float tickW = 2.5f * s;
            float tickH = 10f * s;
            float gap = 1.5f * s;
            float rowW = perRow * tickW + (perRow - 1) * gap;
            string count = reloading ? "--" : ammo.ToString();
            string max = "/" + clip;
            float bigScale = 2.0f * s;
            float smallScale = 0.95f * s;

            c.TextSize(count, out float countW, out float countH, bigScale, bigScale);
            c.TextSize(max, out float maxW, out float maxH, smallScale, smallScale);

            float padX = 10f * s;
            float padY = 7f * s;
            float rowY = y + countH + 4f * s;
            float panelW = Math.Max(rowW, countW + 4f * s + maxW) + padX * 2f;
            float panelH = countH + 4f * s + tickH + padY * 2f;
            (byte r, byte g, byte b) col = low || reloading ? Warn : ((byte)240, (byte)240, (byte)240);

            RifleMode.Rect(c, x - padX, y - padY, panelW, panelH, 10, 8, 10, 0.45f * a);
            RifleMode.Rect(c, x - padX, y - padY, 3f * s, panelH, Accent.r, Accent.g, Accent.b, a);
            RifleMode.Text(c, x, y, count, col.r, col.g, col.b, a, bigScale);
            RifleMode.Text(c, x + countW + 4f * s, y + countH - maxH - 2f * s, max, 170, 175, 185, 0.9f * a, smallScale);

            if (reloading)
            {
                float pulse = 0.6f + 0.4f * MathF.Sin((float)Stopwatch.GetTimestamp() / Stopwatch.Frequency * 9f);
                string label = "RELOADING";
                float labelScale = 0.9f * s;

                c.TextSize(label, out float labelW, out float labelH, labelScale, labelScale);
                RifleMode.Text(c, x + (rowW - labelW) * 0.5f, rowY + (tickH - labelH) * 0.5f - 2f * s, label, Warn.r, Warn.g, Warn.b, pulse * a, labelScale);
                RifleMode.Rect(c, x, rowY + tickH - 2f * s, rowW, 2f * s, 60, 60, 66, 0.8f * a);
                RifleMode.Rect(c, x, rowY + tickH - 2f * s, rowW * RifleAmmo.Progress, 2f * s, Accent.r, Accent.g, Accent.b, a);

                return;
            }

            int shown = (int)MathF.Round(ammo * perRow / (float)clip);

            for (int i = 0; i < perRow; i++)
            {
                float tx = x + i * (tickW + gap);

                if (i < shown)
                {
                    RifleMode.Rect(c, tx, rowY, tickW, tickH, low ? Warn.r : (byte)235, low ? Warn.g : (byte)235, low ? Warn.b : (byte)235, a);
                }
                else
                {
                    RifleMode.Rect(c, tx, rowY, tickW, tickH, 120, 120, 128, 0.25f * a);
                }
            }
        }
        finally
        {
            c.Font = oldFont;
        }
    }
}
