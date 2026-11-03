#nullable enable

using System;
using System.Collections.Generic;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.GFxUI;

namespace BatmanNightwingSticks;

internal static class SticksIcons
{
    private const string NightwingIcons = "Icons_NW";
    private const string BatmanIcons = "Icons_BM";
    private const string ClipName = "NightwingSticksIcon";
    private const string CurrentGadgetPath = "GrandLeftParent.LeftParent.CurrentGadget.Instance";

    private static string? root;
    private static IntPtr rootMovie = IntPtr.Zero;
    private static DateTime nextCheck = DateTime.MinValue;

    private static readonly Dictionary<string, GFxObject> clips = new Dictionary<string, GFxObject>(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> frames = new Dictionary<string, string>(StringComparer.Ordinal);

    public static void Reset()
    {
        nextCheck = DateTime.MinValue;
    }

    private static string SlotPath(int slot) => "GrandRightParent.RightParent.WheelMovie.Gadget" + slot + ".Instance";

    public static void Apply(RGFxMovieRadialGadgetSelect movie, int slot)
    {
        try
        {
            if (slot < 0 || Sticks.Batman() == null || !movie.IsValid || !FindRoot(movie))
            {
                return;
            }

            Show(movie, SlotPath(slot), true);
            Show(movie, CurrentGadgetPath, Sticks.HeldBy(out _, out _));
        }
        catch (Exception exception)
        {
            ModLog.Once("icon-failed", "Icons: setting Nightwing's sticks icon failed: " + Util.Unwrap(exception));
        }
    }

    public static void Tick(RPlayerController controller, int slot)
    {
        DateTime now = DateTime.UtcNow;

        if (now < nextCheck)
        {
            return;
        }

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
                clip = holder.AttachMovie(new FString(NightwingIcons), new FString(ClipName));

                if (clip == null || !clip.IsValid)
                {
                    ModLog.Once("attach:" + path, "Icons: could not attach " + NightwingIcons + " in '" + path + "'.");

                    return;
                }

                ModLog.Info("Icons: Nightwing's sticks icon attached in '" + path + "'.");
            }

            if (clip == null)
            {
                return;
            }

            clips[path] = clip;
        }

        string frame = Sticks.BlastMode && Sticks.Blast != null ? Sticks.BlastClassName : Sticks.ClassName;

        if (!frames.TryGetValue(path, out string? shown) || shown != frame)
        {
            clip.GotoAndStop(new FString(frame));
            frames[path] = frame;
        }

        clip.SetVisible(show);
        Find(movie, path + "." + BatmanIcons)?.SetVisible(!show);
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
        frames.Clear();

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
