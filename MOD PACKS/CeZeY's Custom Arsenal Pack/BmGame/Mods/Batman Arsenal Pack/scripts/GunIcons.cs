#nullable enable

using System;
using System.Collections.Generic;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.GFxUI;

namespace BatmanRedHoodGuns;

internal static class GunIcons
{
    private const string RedHoodIcons = "Icons_RH";
    private const string BatmanIcons = "Icons_BM";
    private const string ClipName = "RedHoodGunsIcon";
    private const string CurrentGadgetPath = "GrandLeftParent.LeftParent.CurrentGadget.Instance";
    private const string CornerLabelPath = "GrandLeftParent.LeftParent.GadgetName.NameText.text";
    private static string GunsLabel => GunsModel.Active ? "MECH DEAGLES" : "DUAL HANDGUNS";

    private static string? root;
    private static IntPtr rootMovie = IntPtr.Zero;
    private static DateTime nextCheck = DateTime.MinValue;
    private static int lastAmmoField = -1;

    private static readonly Dictionary<string, GFxObject> clips = new Dictionary<string, GFxObject>(StringComparer.Ordinal);

    public static void Reset()
    {
        nextCheck = DateTime.MinValue;
    }

    private static string SlotPath(int slot) => "GrandRightParent.RightParent.WheelMovie.Gadget" + slot + ".Instance";

    public static void Apply(RGFxMovieRadialGadgetSelect movie, int slot)
    {
        try
        {
            if (slot < 0 || Guns.Batman() == null || !movie.IsValid || !FindRoot(movie))
            {
                return;
            }

            bool guns = GunsMode.GunsSelected;

            Show(movie, SlotPath(slot), guns);
            if (!BatmanRobinStaff.StaffIcons.OwnsCurrentIcon)
            {
                Show(movie, CurrentGadgetPath, guns && Guns.HeldBy(out _, out _));
            }
            else
            {
                Find(movie, CurrentGadgetPath + "." + ClipName)?.SetVisible(false);
            }

            if (guns)
            {
                SetAmmo(movie, slot);
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("icon-failed", "Icons: setting Red Hood's gun icon failed: " + Util.Unwrap(exception));
        }
    }

    public static void Off(RPlayerController controller, int slot)
    {
        try
        {
            if (controller.RadialGadgetSelect is RGFxMovieRadialGadgetSelect movie && movie.IsValid && FindRoot(movie))
            {
                if (slot >= 0)
                {
                    Show(movie, SlotPath(slot), false);
                }

                Show(movie, CurrentGadgetPath, false);
            }
        }
        catch
        {
        }

        clips.Clear();
    }

    private const string WheelTitlePath = "GrandRightParent.RightParent.WheelMovie.SelectedGadgetName.Child.NameText.text";
    private const string CustomLabel = "MECH DEAGLES";
    private static string? nativeWheelTitle;

    public static void UpdateWheelTitle()
    {
        if (Game.GetPlayerController(0) is not RPlayerController controller || !controller.IsValid ||
            controller.RadialGadgetSelect is not RGFxMovieRadialGadgetSelect movie || !movie.IsValid || !movie.bWheelOpen || !FindRoot(movie))
        {
            return;
        }

        nativeWheelTitle ??= GameObject.Localize(new FString("GadgetSelect"), new FString(Guns.ClassName), new FString("GFxUI")).ToString() ?? "";

        if (nativeWheelTitle.Length == 0)
        {
            return;
        }

        string path = root + WheelTitlePath;
        string title = movie.GetVariableString(new FString(path)).ToString() ?? "";
        string? wanted = null;

        if (GunsModel.Active)
        {
            if (title == nativeWheelTitle)
            {
                wanted = CustomLabel;
            }
        }
        else if (title == CustomLabel)
        {
            wanted = nativeWheelTitle;
        }

        if (wanted != null && wanted != title)
        {
            movie.SetVariableString(new FString(path), new FString(wanted));
        }
    }

    public static void Tick(RPlayerController controller, int slot)
    {
        if (root != null && GunsMode.GunsSelected && controller.RadialGadgetSelect is RGFxMovieRadialGadgetSelect corner && corner.Ptr == rootMovie && Guns.HeldBy(out _, out _))
        {
            corner.SetVariableBool(new FString(root + CurrentGadgetPath + "." + BatmanIcons + ".visible"), false);

            string label = root + CornerLabelPath;

            if ((corner.GetVariableString(new FString(label)).ToString() ?? "") != GunsLabel)
            {
                corner.SetVariableString(new FString(label), new FString(GunsLabel));
            }
        }

        DateTime now = DateTime.UtcNow;

        if (now < nextCheck && AmmoField() == lastAmmoField)
        {
            return;
        }

        lastAmmoField = AmmoField();

        nextCheck = now + TimeSpan.FromSeconds(0.25);

        if (controller.RadialGadgetSelect is RGFxMovieRadialGadgetSelect movie)
        {
            Apply(movie, slot);
        }
    }

    private static void Show(RGFxMovieRadialGadgetSelect movie, string path, bool show)
    {
        GFxObject? holder = Find(movie, path);

        if (holder == null)
        {
            ModLog.Once("noholder:" + path, "Icons: icon holder '" + path + "' not found in the wheel movie.");

            return;
        }

        if (!clips.TryGetValue(path, out GFxObject? clip) || !clip.IsValid)
        {
            clip = Find(movie, path + "." + ClipName);

            if (clip == null && show)
            {
                clip = holder.AttachMovie(new FString(RedHoodIcons), new FString(ClipName));

                if (clip == null || !clip.IsValid)
                {
                    ModLog.Once("attach:" + path, "Icons: could not attach " + RedHoodIcons + " in '" + path + "'.");

                    return;
                }

                clip.GotoAndStop(new FString(Guns.ClassName));
                ModLog.Info("Icons: Red Hood's gun icon attached in '" + path + "'.");
            }

            if (clip == null)
            {
                return;
            }

            clips[path] = clip;
        }

        clip.SetVisible(show);
        Find(movie, path + "." + BatmanIcons)?.SetVisible(!show);
    }

    private const int HudPips = 4;

    public static int AmmoField()
    {
        int ammo = GunsHud.ReloadingEmptyClip ? 0 : GunsHud.Ammo;
        int pips = ammo <= 0 ? 0 : Math.Max(1, ammo * HudPips / GunsHud.ClipSize);

        return HudPips * 10 + Math.Clamp(pips, 0, HudPips);
    }

    private static void SetAmmo(RGFxMovieRadialGadgetSelect movie, int slot)
    {
        TArray<GFxMoviePlayer.FASValue> args = new TArray<GFxMoviePlayer.FASValue>(3);

        foreach (int value in new[] { slot, 100, AmmoField() })
        {
            GFxMoviePlayer.FASValue arg = default;

            arg.Type = GFxMoviePlayer.ASType.AS_Number;
            arg.N = value;
            args.Add(arg);
        }

        movie.Invoke(new FString(root + "UC_SetGadgetParams"), args);
        ModLog.Once("ammo-hud", "Icons: guns' ammo counter shown on the wheel (" + AmmoField() + ").");
    }

    private static GFxObject? Find(RGFxMovieRadialGadgetSelect movie, string path)
    {
        GFxObject? found = movie.GetVariableObject(new FString(root + path));

        return found != null && found.IsValid ? found : null;
    }

    private static bool FindRoot(RGFxMovieRadialGadgetSelect movie)
    {
        if (root != null && rootMovie == movie.Ptr)
        {
            return true;
        }

        clips.Clear();

        foreach (string candidate in new[] { "", "root.", "_root.", "root1." })
        {
            GFxObject? found = movie.GetVariableObject(new FString(candidate + "GrandLeftParent"));

            if (found != null && found.IsValid)
            {
                root = candidate;
                rootMovie = movie.Ptr;
                ModLog.Info("Icons: wheel movie reached with path prefix '" + candidate + "'.");

                return true;
            }
        }

        ModLog.Once("icon-root", "Icons: could not reach the wheel movie's clips; keeping the default icon.");

        return false;
    }
}
