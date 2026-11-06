#nullable enable

using System;
using System.Collections.Generic;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.GFxUI;

namespace BatmanRobinStaff;

internal static class StaffIcons
{
    private const string RobinIcons = "Icons_RB";
    private const string StaffFrame = "RRobinStaffShield";
    private const string BatmanIcons = "Icons_BM";
    private const string ClipName = "RobinStaffIcon";
    private const string CurrentGadgetPath = "GrandLeftParent.LeftParent.CurrentGadget.Instance";
    private const string CornerLabelPath = "GrandLeftParent.LeftParent.GadgetName.NameText.text";
    private const string StaffLabel = "ROBIN'S STAFF";
    private const string WheelTitlePath = "GrandRightParent.RightParent.WheelMovie.SelectedGadgetName.Child.NameText.text";
    private const string WheelTextPath = "GrandRightParent.RightParent.WheelMovie.SelectedGadgetName.Description.NameText.text";
    private const string WheelRowPath = "GrandRightParent.RightParent.WheelMovie.SelectedGadgetName.GadgetPrompts.PromptList.GadgetMove";
    private const int WheelRows = 3;
    private const string WheelText = "Robin's collapsible bo staff. While it is out, Batman fights with Robin's strikes, counters and takedowns.";

    private static string? root;
    private static IntPtr rootMovie = IntPtr.Zero;
    private static DateTime nextCheck = DateTime.MinValue;
    private static readonly Dictionary<string, GFxObject> clips = new Dictionary<string, GFxObject>(StringComparer.Ordinal);

    private static readonly HashSet<string> hidden = new HashSet<string>(StringComparer.Ordinal);

    public static bool OwnsCurrentIcon;

    private static bool labelChanged = true;
    private static bool lastHeld;
    private static string? shieldTitle, freezeTitle, freezeText;

    public static void Reset()
    {
        nextCheck = DateTime.MinValue;
        OwnsCurrentIcon = false;
        labelChanged = true;
        hidden.Clear();
    }

    private static string SlotPath(int slot) => "GrandRightParent.RightParent.WheelMovie.Gadget" + slot + ".Instance";

    public static bool Tick(RPlayerController controller, int slot, bool heldSlot)
    {
        DateTime now = DateTime.UtcNow;

        if ((now < nextCheck && heldSlot == lastHeld) || slot < 0)
        {
            return false;
        }

        lastHeld = heldSlot;

        nextCheck = now + TimeSpan.FromSeconds(0.25);

        try
        {
            if (controller.RadialGadgetSelect is not RGFxMovieRadialGadgetSelect movie || !movie.IsValid || !FindRoot(movie))
            {
                return false;
            }

            bool staff = StaffConfig.C.StaffSelected;

            OwnsCurrentIcon = staff && heldSlot;
            Show(movie, SlotPath(slot), staff);
            Show(movie, CurrentGadgetPath, OwnsCurrentIcon);

            if (OwnsCurrentIcon)
            {
                Find(movie, CurrentGadgetPath + ".RedHoodGunsIcon")?.SetVisible(false);
            }

            return true;
        }
        catch (Exception exception)
        {
            ModLog.Once("icon-failed", "Icons: setting the staff icon failed: " + Util.Unwrap(exception));

            return false;
        }
    }

    private static string Native(string key)
    {
        return GameObject.Localize(new FString("GadgetSelect"), new FString(key), new FString("GFxUI")).ToString() ?? "";
    }

    private static string ShieldTitle => shieldTitle ??= Native(StaffMode.DisplayClass);

    private static string FreezeTitle => freezeTitle ??= Native(Staff.FreezeClass);

    private static string FreezeText => freezeText ??= Native(Staff.FreezeClass + "Description");

    public static void UpdateLabel(RPlayerController controller, bool staffHeld)
    {
        if ((!staffHeld && !labelChanged) || root == null)
        {
            return;
        }

        try
        {
            if (controller.RadialGadgetSelect is not RGFxMovieRadialGadgetSelect movie || !movie.IsValid || movie.Ptr != rootMovie)
            {
                return;
            }

            if (staffHeld)
            {
                movie.SetVariableBool(new FString(root + CurrentGadgetPath + "." + BatmanIcons + ".visible"), false);
            }

            string path = root + CornerLabelPath;
            string text = movie.GetVariableString(new FString(path)).ToString() ?? "";

            if (staffHeld)
            {
                if (text != StaffLabel)
                {
                    movie.SetVariableString(new FString(path), new FString(StaffLabel));
                }

                labelChanged = true;
            }
            else
            {
                if (text == StaffLabel || text == ShieldTitle)
                {
                    movie.SetVariableString(new FString(path), new FString(FreezeTitle));
                }

                labelChanged = false;
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("label-failed", "Icons: setting the corner label failed: " + Util.Unwrap(exception));
        }
    }

    public static void UpdateWheelText(RGFxMovieRadialGadgetSelect movie, bool staffSelected)
    {
        if (!movie.bWheelOpen || root == null || movie.Ptr != rootMovie)
        {
            return;
        }

        string titlePath = root + WheelTitlePath;
        string textPath = root + WheelTextPath;
        string title = movie.GetVariableString(new FString(titlePath)).ToString() ?? "";

        if (title != StaffLabel && title != ShieldTitle)
        {
            return;
        }

        Put(movie, titlePath, staffSelected ? StaffLabel : FreezeTitle);
        Put(movie, textPath, staffSelected ? WheelText : FreezeText);

        if (staffSelected)
        {
            PutRows(movie);
        }
    }

    private static void PutRows(RGFxMovieRadialGadgetSelect movie)
    {
        string key = "(" + StaffMode.KeyLabel + ")";
        (string plain, string html)[] rows = StaffPad.UsingPad
            ? new[] { Row("(" + StaffPad.SwitchName + ")", "", " Switch to Freeze Blast") }
            : new[] { Row(key, "", " Draw or Put Away"), Row(key, "(Hold)", " Switch to Freeze Blast") };

        for (int index = 0; index < WheelRows; index++)
        {
            string path = root + WheelRowPath + (index + 1) + ".Text";
            string wanted = index < rows.Length ? rows[index].plain : "";

            if ((movie.GetVariableString(new FString(path + ".text")).ToString() ?? "") != wanted)
            {
                movie.SetVariableString(new FString(path + ".htmlText"), new FString(index < rows.Length ? rows[index].html : ""));
            }
        }
    }

    private static (string plain, string html) Row(string key, string hold, string text)
    {
        string html = Font("#A6B1FF", key);
        string plain = key;

        if (hold.Length > 0)
        {
            html += Font("#FFFFFF", " ") + Font("#E87B00", hold);
            plain += " " + hold;
        }

        return (plain + text, "<P ALIGN=\"LEFT\">" + html + Font("#FFFFFF", text) + "</P>");
    }

    private static string Font(string color, string text) => "<FONT FACE=\"$Game\" SIZE=\"16\" COLOR=\"" + color + "\" KERNING=\"0\">" + text + "</FONT>";

    private static void Put(RGFxMovieRadialGadgetSelect movie, string path, string wanted)
    {
        if ((movie.GetVariableString(new FString(path)).ToString() ?? "") != wanted)
        {
            movie.SetVariableString(new FString(path), new FString(wanted));
        }
    }

    public static void Off(RPlayerController controller, int slot)
    {
        OwnsCurrentIcon = false;
        UpdateLabel(controller, false);

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
        hidden.Clear();
    }

    private static void Show(RGFxMovieRadialGadgetSelect movie, string path, bool show)
    {
        if (show)
        {
            hidden.Remove(path);
        }
        else if (hidden.Contains(path))
        {
            return;
        }

        GFxObject? holder = Find(movie, path);

        if (holder == null)
        {
            return;
        }

        if (!clips.TryGetValue(path, out GFxObject? clip) || !clip.IsValid)
        {
            clip = Find(movie, path + "." + ClipName);

            if (clip == null && show)
            {
                clip = holder.AttachMovie(new FString(RobinIcons), new FString(ClipName));

                if (clip == null || !clip.IsValid)
                {
                    ModLog.Once("attach:" + path, "Icons: could not attach " + RobinIcons + " in '" + path + "'.");

                    return;
                }

                clip.GotoAndStop(new FString(StaffFrame));
                ModLog.Info("Icons: the staff icon attached in '" + path + "'.");
            }

            if (clip == null)
            {
                hidden.Add(path);

                return;
            }

            clips[path] = clip;
        }

        clip.SetVisible(show);

        if (show || path != CurrentGadgetPath || !BatmanRedHoodGuns.Guns.HeldBy(out _, out _))
        {
            Find(movie, path + "." + BatmanIcons)?.SetVisible(!show);
        }

        if (!show)
        {
            hidden.Add(path);
        }
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
        hidden.Clear();

        foreach (string candidate in new[] { "", "root.", "_root.", "root1." })
        {
            GFxObject? found = movie.GetVariableObject(new FString(candidate + "GrandLeftParent"));

            if (found != null && found.IsValid)
            {
                root = candidate;
                rootMovie = movie.Ptr;

                return true;
            }
        }

        return false;
    }
}
