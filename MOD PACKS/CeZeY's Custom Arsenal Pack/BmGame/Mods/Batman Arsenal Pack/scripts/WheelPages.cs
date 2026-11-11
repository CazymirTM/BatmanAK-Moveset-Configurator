#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.GFxUI;
using BatmanDeathstrokeSniper;
using BatmanRedHoodGuns;

namespace BatmanAssaultRifle;

internal static class WheelPages
{
    public const string Package = "BatmanArsenalIco_SF";
    public const string RifleHolo = "BMAR_RIcon01.Maps.BMAR_RIcon01_H";
    private const string DeagleHolo = "BMAR_RIcon01.Maps.BMAR_RIc01_HGD";
    private const string RedHoodHolo = "BMAR_RIcon01.Maps.BMAR_RIcon01_HGunRH";
    private const string M170Holo = "GCPD_Cop.Maps.BMAR_RIc01_HSnM17";
    private const string ArkhamHolo = "GCPD_Cop.Maps.BMAR_RIcon01_HSnArk";
    private static readonly string[] AllHolos = { RifleHolo, DeagleHolo, RedHoodHolo, M170Holo, ArkhamHolo };

    private const string WheelPath = "GrandRightParent.RightParent.WheelMovie";
    private const string TitlePath = WheelPath + ".SelectedGadgetName.Child.NameText.text";
    private const string DescPath = WheelPath + ".SelectedGadgetName.Description.NameText";
    private const string RowPath = WheelPath + ".SelectedGadgetName.GadgetPrompts.PromptList.GadgetMove";
    private const string HoloParent = WheelPath + ".BackgroundImage";
    private const string NativeHolo = HoloParent + ".Attach";
    private const string HoloName = "BmArPageHolo";
    private const string OldHoloName = "BmArRifleHolo";
    private const int Rows = 5;
    private const float FadeIn = 0.25f;
    private const float HoloLift = 12f;

    private sealed class Page
    {
        public string Id = "";
        public Func<bool> Want = () => false;
        public Func<string> Label = () => "";
        public bool OwnsTitle;
        public Func<IEnumerable<string>> SlotTitles = () => Array.Empty<string>();
        public Func<string> Holo = () => "";
        public Func<int> HoloH = () => 172;
        public Func<string> Description = () => "";
        public Func<(string plain, string html)[]> RowList = () => Array.Empty<(string, string)>();
    }

    private static readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
    private static bool loadTried;
    private static Page? owner;
    private static string? nativeDesc;
    private static readonly string?[] nativeRows = new string?[Rows];
    private static long ownedSince;
    private static string? root;
    private static IntPtr rootMovie = IntPtr.Zero;
    private static string holoShown = "";
    private static bool holoConfigured;

    private static string Loc(string key) => GameObject.Localize(new FString("GadgetSelect"), new FString(key), new FString("GFxUI")).ToString() ?? "";

    private static string? batarangTitle, gunsTitle, recTitle, disruptorTitle;

    private static readonly Page[] Pages = BuildPages();

    private static Page[] BuildPages()
    {
        var rifle = new Page
        {
            Id = "rifle",
            Want = () => BatmanArsenalPack.Arsenal.Rifle && RifleMode.Installed && Rifle.Selected,
            Label = () => RifleMode.RifleLabel,
            SlotTitles = () => new[] { RifleMode.ThugLabel, RifleMode.M4Label, batarangTitle ??= Loc(Rifle.BatarangClass) },
            Holo = () => RifleHolo,
            HoloH = () => 172,
            Description = () => (RifleProps.M4Active ? "Militia-grade M4 carbine. " : "The militia's assault rifle. ") +
                "Full-auto fire from a " + RifleAmmo.MagSize + "-round magazine. Three body hits or one headshot drop a thug.",
            RowList = RifleRows,
        };

        var guns = new Page
        {
            Id = "guns",
            OwnsTitle = true,
            Want = () => BatmanArsenalPack.Arsenal.Guns && GunsPad.Installed && GunsConfig.C.GunsSelected,
            Label = () => GunsModel.Active ? "MECH DEAGLES" : (gunsTitle ??= Loc(Guns.ClassName)),
            SlotTitles = () => new[] { "MECH DEAGLES", "DUAL HANDGUNS", gunsTitle ??= Loc(Guns.ClassName), recTitle ??= Loc(Guns.ReplacedGadget), Guns.ReplacedTitle },
            Holo = () => GunsModel.Active ? DeagleHolo : RedHoodHolo,
            HoloH = () => 150,
            Description = () => (GunsModel.Active ? "Red Hood's twin pistols, rebuilt as Mech Deagles. " : "Red Hood's own pistols. ") +
                "Lethal aimed shots, quickfire in combat, " + GunsHud.ClipSize + "-round magazines.",
            RowList = GunsRows,
        };

        var sniper = new Page
        {
            Id = "sniper",
            Want = () => BatmanArsenalPack.Arsenal.Sniper && SniperMode.Installed && SniperConfig.C.SniperSelected,
            Label = () => SniperCustom ? "M170 VENDETTA" : "SNIPER RIFLE",
            SlotTitles = () => new[] { "M170 VENDETTA", "SNIPER RIFLE", disruptorTitle ??= Loc("RDisruptorSniperBm") },
            Holo = () => SniperCustom ? M170Holo : ArkhamHolo,
            HoloH = () => 172,
            Description = () => (SniperCustom ? "Long-range sniper rifle. " : "The Arkham Knight's own sniper rifle. ") +
                "One aimed shot drops a thug for good; witnesses flee. Laser sight, " + SniperConfig.C.Magazine + "-round magazine.",
            RowList = SniperRows,
        };

        return new[] { rifle, guns, sniper };
    }

    private static bool SniperCustom => SniperProps.IsCustom(SniperProps.RifleSkel);

    private static (string plain, string html)[] RifleRows()
    {
        if (RiflePad.UsingPad)
        {
            var list = new List<(string, string)> { Row(("(LT)", " Aim  "), ("(RT)", " Full Auto")) };

            if (RifleConfig.C.PadReload != 0)
            {
                list.Add(Row(("(LT)+(" + RiflePad.ButtonName(RifleConfig.C.PadReload) + ")", " Reload")));
            }

            if (RifleConfig.C.PadSwitch != 0)
            {
                list.Add(Row(("(" + RiflePad.ButtonName(RifleConfig.C.PadSwitch) + ")", " Switch to Batarang")));
            }

            return list.ToArray();
        }

        string key = "(" + RifleMode.KeyLabel + ")";

        return new[]
        {
            Row(("(RMB)", " Aim  "), ("(LMB)", " Full Auto")),
            Row(("(R)", " Reload")),
            Row((key + key, " Hip-Fire Burst")),
            HoldRow(key, " Switch to Batarang"),
        };
    }

    private static (string plain, string html)[] GunsRows()
    {
        if (GunsPad.UsingPad)
        {
            var c = GunsConfig.C;
            var list = new List<(string, string)> { Row(("(LT)", " Aim  "), ("(RT)", " Fire")) };

            if (c.PadReload != 0)
            {
                list.Add(Row(("(LT)+(" + GunsPad.ButtonName(c.PadReload) + ")", " Reload")));
            }

            if (c.PadSwitch != 0)
            {
                list.Add(Row(("(" + GunsPad.ButtonName(c.PadSwitch) + ")", " Switch to REC Gun")));
            }

            if (c.PadHands != 0)
            {
                list.Add(Row(("(" + GunsPad.ButtonName(c.PadHands) + ")", " Always in Hand")));
            }

            return list.ToArray();
        }

        string key = "(" + GunsMode.KeyLabel + ")";

        return new[]
        {
            Row(("(RMB)", " Aim  "), ("(LMB)", " Fire")),
            Row((key + key, " Quickfire")),
            Row(("(R)", " Reload")),
            Row(("(F5)", " Always in Hand")),
            HoldRow(key, " Switch to REC Gun"),
        };
    }

    private static (string plain, string html)[] SniperRows()
    {
        if (SniperPad.UsingPad)
        {
            var c = SniperConfig.C;
            var list = new List<(string, string)> { Row(("(LT)", " Aim  "), ("(RT)", " Fire")) };

            if (c.PadReload != 0)
            {
                list.Add(Row(("(LT)+(" + SniperPad.ButtonName(c.PadReload) + ")", " Reload")));
            }

            if (c.PadSwitch != 0)
            {
                list.Add(Row(("(" + SniperPad.ButtonName(c.PadSwitch) + ")", " Switch to Disruptor")));
            }

            return list.ToArray();
        }

        string key = "(" + SniperMode.KeyLabel + ")";

        return new[]
        {
            Row(("(RMB)", " Aim  "), ("(LMB)", " Fire")),
            Row(("(R)", " Reload")),
            Row(("(" + SniperConfig.C.LaserKey.ToString().ToUpperInvariant() + ")", " Laser Sight")),
            HoldRow(key, " Switch to Disruptor"),
        };
    }

    private static (string plain, string html) HoldRow(string key, string text)
    {
        string html = Font("#A6B1FF", key) + Font("#FFFFFF", " ") + Font("#E87B00", "(Hold)") + Font("#FFFFFF", text);

        return (key + " (Hold)" + text, "<P ALIGN=\"LEFT\">" + html + "</P>");
    }

    private static (string plain, string html) Row(params (string key, string text)[] parts)
    {
        string plain = "";
        string html = "";

        foreach ((string key, string text) in parts)
        {
            plain += key + text;
            html += Font("#A6B1FF", key);

            if (text.Length > 0)
            {
                html += Font("#FFFFFF", text);
            }
        }

        return (plain, "<P ALIGN=\"LEFT\">" + html + "</P>");
    }

    private static string Font(string color, string text) => "<FONT FACE=\"$Game\" SIZE=\"16\" COLOR=\"" + color + "\" KERNING=\"0\">" + text + "</FONT>";

    public static void RootAll()
    {
        foreach (string path in AllHolos)
        {
            if (textures.TryGetValue(path, out Texture2D? t) && t.IsValid)
            {
                continue;
            }

            Texture2D? found = Game.FindObject<Texture2D>(path);

            if (found != null)
            {
                found.AddToRoot();
                textures[path] = found;
            }
        }
    }

    private static Texture2D? Holo(string path)
    {
        if (textures.TryGetValue(path, out Texture2D? t) && t.IsValid)
        {
            return t;
        }

        RootAll();

        if (textures.TryGetValue(path, out t) && t.IsValid)
        {
            return t;
        }

        if (loadTried)
        {
            return null;
        }

        loadTried = true;

        try
        {
            if (!System.IO.File.Exists(System.IO.Path.Combine(FileUtils.GetGamePath(), "CookedPCConsole", Package + ".upk")))
            {
                ModLog.Info("Wheel: " + Package + ".upk is not in BmGame\\CookedPCConsole; native wheel holograms stay.");

                return null;
            }

            Game.LoadPackage(Package);
            RootAll();
            ModLog.Info("Wheel: hologram textures rooted: " + textures.Count + " of " + AllHolos.Length + ".");
        }
        catch (Exception exception)
        {
            ModLog.Once("pages-load", "Wheel: loading " + Package + " failed: " + Util.Unwrap(exception));
        }

        return textures.TryGetValue(path, out t) && t.IsValid ? t : null;
    }

    private const string CornerLabelPath = "GrandLeftParent.LeftParent.GadgetName.NameText.text";

    private static void FixRecCornerLabel(RGFxMovieRadialGadgetSelect movie)
    {
        if (BatmanArsenalPack.Arsenal.Guns && GunsConfig.C.GunsSelected)
        {
            return;
        }

        string path = root + CornerLabelPath;
        string label = movie.GetVariableString(new FString(path)).ToString() ?? "";

        if (label.Length == 0 || !(label == "MECH DEAGLES" || label == "DUAL HANDGUNS" || label == (gunsTitle ??= Loc(Guns.ClassName))))
        {
            return;
        }

        string rec = recTitle ??= Loc(Guns.ReplacedGadget);

        if (rec.Length > 0)
        {
            movie.SetVariableString(new FString(path), new FString(rec));
            ModLog.Once("rec-corner", "Wheel: corner label set back to " + rec + " (guns not selected).");
        }
    }

    public static void FixCornerNow()
    {
        if (Game.GetPlayerController(0) is RPlayerController pc && pc.IsValid &&
            pc.RadialGadgetSelect is RGFxMovieRadialGadgetSelect corner && corner.IsValid && FindRoot(corner))
        {
            FixRecCornerLabel(corner);
        }
    }

    public static void Frame()
    {
        if (Game.GetPlayerController(0) is RPlayerController pc && pc.IsValid &&
            pc.RadialGadgetSelect is RGFxMovieRadialGadgetSelect corner && corner.IsValid && FindRoot(corner))
        {
            FixRecCornerLabel(corner);
        }

        if (Game.GetPlayerController(0) is not RPlayerController controller || !controller.IsValid ||
            controller.RadialGadgetSelect is not RGFxMovieRadialGadgetSelect movie || !movie.IsValid || !movie.bWheelOpen || !FindRoot(movie))
        {
            return;
        }

        string title = movie.GetVariableString(new FString(root + TitlePath)).ToString() ?? "";
        Page? shown = null;

        foreach (Page page in Pages)
        {
            if (Contains(page.SlotTitles(), title))
            {
                shown = page;

                break;
            }
        }

        Page? wanted = shown != null && shown.Want() ? shown : null;

        if (owner != null && owner != wanted)
        {
            Drop(movie, restoreText: owner == shown);
        }

        if (wanted != null)
        {
            Own(movie, wanted);
        }
    }

    private static bool Contains(IEnumerable<string> titles, string title)
    {
        if (title.Length == 0)
        {
            return false;
        }

        foreach (string t in titles)
        {
            if (t.Length > 0 && t == title)
            {
                return true;
            }
        }

        return false;
    }

    public static void Release()
    {
        try
        {
            if (owner != null && Game.GetPlayerController(0) is RPlayerController controller && controller.IsValid &&
                controller.RadialGadgetSelect is RGFxMovieRadialGadgetSelect movie && movie.IsValid && FindRoot(movie))
            {
                string title = movie.GetVariableString(new FString(root + TitlePath)).ToString() ?? "";

                Drop(movie, Contains(owner.SlotTitles(), title));
            }
        }
        catch
        {
        }

        Forget();
    }

    public static void Forget()
    {
        owner = null;
        holoConfigured = false;
        holoShown = "";
        root = null;
        rootMovie = IntPtr.Zero;
    }

    private static void Own(RGFxMovieRadialGadgetSelect movie, Page page)
    {
        if (owner != page)
        {
            owner = page;
            ownedSince = Stopwatch.GetTimestamp();
            nativeDesc = movie.GetVariableString(new FString(root + DescPath + ".htmlText")).ToString();

            for (int i = 0; i < Rows; i++)
            {
                nativeRows[i] = movie.GetVariableString(new FString(root + RowPath + (i + 1) + ".Text.htmlText")).ToString();
            }

            ModLog.Once("page-" + page.Id, "Wheel: " + page.Id + " page shown (hologram " + page.Holo() + ").");
        }

        if (page.OwnsTitle)
        {
            Put(movie, root + TitlePath, page.Label());
        }

        Put(movie, root + DescPath + ".text", page.Description());
        PutRows(movie, page.RowList());
        ShowHolo(movie, page);
    }

    private static void Drop(RGFxMovieRadialGadgetSelect movie, bool restoreText)
    {
        owner = null;
        HideHolo(movie);

        if (!restoreText)
        {
            return;
        }

        if (nativeDesc != null)
        {
            movie.SetVariableString(new FString(root + DescPath + ".htmlText"), new FString(nativeDesc));
        }

        for (int i = 0; i < Rows; i++)
        {
            if (nativeRows[i] is string html)
            {
                movie.SetVariableString(new FString(root + RowPath + (i + 1) + ".Text.htmlText"), new FString(html));
            }
        }
    }

    private static void Put(RGFxMovieRadialGadgetSelect movie, string path, string wanted)
    {
        if ((movie.GetVariableString(new FString(path)).ToString() ?? "") != wanted)
        {
            movie.SetVariableString(new FString(path), new FString(wanted));
        }
    }

    private static void PutRows(RGFxMovieRadialGadgetSelect movie, (string plain, string html)[] rows)
    {
        for (int i = 0; i < Rows; i++)
        {
            string path = root + RowPath + (i + 1) + ".Text";
            string wanted = i < rows.Length ? rows[i].plain : "";

            if ((movie.GetVariableString(new FString(path + ".text")).ToString() ?? "") != wanted)
            {
                movie.SetVariableString(new FString(path + ".htmlText"), new FString(i < rows.Length ? rows[i].html : ""));
            }

            if (i < rows.Length)
            {
                movie.SetVariableBool(new FString(root + RowPath + (i + 1) + ".visible"), true);
            }
        }
    }

    private static void ShowHolo(RGFxMovieRadialGadgetSelect movie, Page page)
    {
        string path = page.Holo();
        Texture2D? tex = Holo(path);

        if (tex == null)
        {
            return;
        }

        string mine = root + HoloParent + "." + HoloName;
        string key = path + "|" + page.HoloH();

        if (movie.GetVariableNumber(new FString(mine + ".width")) <= 0f || !holoConfigured || holoShown != key)
        {
            if (!SetupHolo(movie, path, page.HoloH(), tex.SizeX == tex.SizeY))
            {
                return;
            }

            holoConfigured = true;
            holoShown = key;
        }

        float t = (float)Stopwatch.GetElapsedTime(ownedSince).TotalSeconds;

        movie.SetVariableNumber(new FString(mine + ".alpha"), Math.Clamp(t / FadeIn, 0f, 1f));
        movie.SetVariableBool(new FString(mine + ".visible"), true);
        movie.SetVariableBool(new FString(root + NativeHolo + ".visible"), false);
    }

    private static void HideHolo(RGFxMovieRadialGadgetSelect movie)
    {
        string mine = root + HoloParent + "." + HoloName;

        if (movie.GetVariableNumber(new FString(mine + ".width")) > 0f)
        {
            movie.SetVariableBool(new FString(mine + ".visible"), false);
        }

        movie.SetVariableBool(new FString(root + NativeHolo + ".visible"), true);
    }

    private static bool SetupHolo(RGFxMovieRadialGadgetSelect movie, string texture, int h, bool square)
    {
        try
        {
            string mine = root + HoloParent + "." + HoloName;
            bool existing = movie.GetVariableNumber(new FString(mine + ".width")) > 0f;
            GFxObject? tf;

            if (existing)
            {
                tf = movie.GetVariableObject(new FString(mine));
            }
            else
            {
                GFxObject? parent = movie.GetVariableObject(new FString(root + HoloParent));

                if (parent == null || !parent.IsValid)
                {
                    return false;
                }

                tf = parent.AttachMovie(new FString("flash.text.TextField"), new FString(HoloName));
            }

            if (tf == null || !tf.IsValid)
            {
                ModLog.Once("holo-attach", "Wheel: could not create the hologram field.");

                return false;
            }

            int w = h * 2;
            int ih = square ? w : h;
            float ax = (float)movie.GetVariableNumber(new FString(root + NativeHolo + ".x"));
            float ay = (float)movie.GetVariableNumber(new FString(root + NativeHolo + ".y"));

            tf.SetBool(new FString("selectable"), false);
            tf.SetBool(new FString("mouseEnabled"), false);
            tf.SetFloat(new FString("width"), w + 8);
            tf.SetFloat(new FString("height"), ih + 8);
            tf.SetString(new FString("htmlText"), new FString("<img src='img://" + texture + "' width='" + w + "' height='" + ih + "'>"));
            tf.SetString(new FString("blendMode"), new FString("add"));

            GFxObject.FASColorTransform ct = tf.GetColorTransform();

            ct.Multiply = new GameObject.FLinearColor { R = 0.42f, G = 0.76f, B = 1f, A = 1f };
            tf.SetColorTransform(ct);

            GFxObject.FASDisplayInfo d = tf.GetDisplayInfo();

            d.X = ax - h - 2f;
            d.hasX = true;
            d.Y = ay - ih / 2f - 2f - HoloLift;
            d.hasY = true;
            tf.SetDisplayInfo(d);
            ModLog.Info("Wheel: hologram " + texture + " " + (existing ? "swapped in" : "created") + " (" + w + "x" + ih + ").");

            return true;
        }
        catch (Exception exception)
        {
            ModLog.Once("holo-attach-ex", "Wheel: setting up the hologram failed: " + Util.Unwrap(exception));

            return false;
        }
    }

    private static bool FindRoot(RGFxMovieRadialGadgetSelect movie)
    {
        if (root != null && rootMovie == movie.Ptr)
        {
            return true;
        }

        owner = null;
        holoConfigured = false;
        holoShown = "";

        foreach (string candidate in new[] { "", "root.", "_root.", "root1." })
        {
            GFxObject? found = movie.GetVariableObject(new FString(candidate + "GrandLeftParent"));

            if (found != null && found.IsValid)
            {
                root = candidate;
                rootMovie = movie.Ptr;

                string old = root + HoloParent + "." + OldHoloName;

                if (movie.GetVariableNumber(new FString(old + ".width")) > 0f)
                {
                    movie.SetVariableBool(new FString(old + ".visible"), false);
                }

                return true;
            }
        }

        return false;
    }
}
