#nullable enable

using System;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.GFxUI;

namespace BatmanAssaultRifle;

internal static class RifleIcon
{
    public const string Package = "BatmanArsenalIco_SF";
    private const string TexturePath = "BMAR_RIcon01.Maps.BMAR_RIcon01_D";
    private const string ClipName = "BmArRifleIcon";
    private const string NativeSet = "Icons_BM";
    private const string NativeFrame = "RBatarangBm";
    private const string CornerPath = "GrandLeftParent.LeftParent.CurrentGadget.Instance";
    private static string SlotPath(int slot) => "GrandRightParent.RightParent.WheelMovie.Gadget" + slot + ".Instance";

    private const int S = 58;
    private const float OffX = -(S + 2f);
    private const float OffY = -(S / 2f + 2f);
    private const int GlowColor = 0x2EC8FF;

    public static bool CornerWanted;
    public static int Slot = -1;

    private static Texture2D? texture;
    private static bool textureTried;
    private static string? root;
    private static IntPtr rootMovie = IntPtr.Zero;
    private static readonly Holder Corner = new Holder();
    private static readonly Holder Wheel = new Holder();

    private sealed class Holder
    {
        public string Path = "";
        public bool Owned;
        public bool Configured;
    }

    public static bool Ready => texture != null && texture.IsValid;

    public static void Load()
    {
        if (Ready || textureTried)
        {
            return;
        }

        textureTried = true;

        try
        {
            texture = Game.FindObject<Texture2D>(TexturePath);

            if (texture == null)
            {
                if (!System.IO.File.Exists(System.IO.Path.Combine(FileUtils.GetGamePath(), "CookedPCConsole", Package + ".upk")))
                {
                    ModLog.Info("Icon: " + Package + ".upk is not in BmGame\\CookedPCConsole; the rifle keeps the Batarang icon.");

                    return;
                }

                Game.LoadPackage(Package);
                texture = Game.FindObject<Texture2D>(TexturePath);
            }

            if (texture == null)
            {
                ModLog.Info("Icon: " + TexturePath + " not found in " + Package + "; the rifle keeps the Batarang icon.");

                return;
            }

            texture.AddToRoot();

            WheelPages.RootAll();
            ModLog.Info("Icon: loaded the rifle icon " + texture.SizeX + "x" + texture.SizeY + " from " + Package + ".");
        }
        catch (Exception exception)
        {
            ModLog.Once("icon-load", "Icon: loading the rifle icon failed: " + Util.Unwrap(exception));
        }
    }

    public static void Frame(bool want)
    {
        if (!Ready && (!want || textureTried))
        {
            return;
        }

        if (Game.GetPlayerController(0) is not RPlayerController controller || !controller.IsValid ||
            controller.RadialGadgetSelect is not RGFxMovieRadialGadgetSelect movie || !movie.IsValid || !FindRoot(movie))
        {
            return;
        }

        if (want)
        {
            Load();
        }

        bool ready = Ready;

        Corner.Path = CornerPath;
        Apply(movie, Corner, ready && want && CornerWanted);

        string slotPath = Slot >= 0 ? SlotPath(Slot) : "";

        if (Wheel.Path != slotPath)
        {
            if (Wheel.Owned && Wheel.Path.Length > 0)
            {
                Apply(movie, Wheel, false);
            }

            Wheel.Path = slotPath;
            Wheel.Owned = false;
            Wheel.Configured = false;
        }

        if (slotPath.Length > 0)
        {
            Apply(movie, Wheel, ready && want);
        }
    }

    public static void Release()
    {
        try
        {
            if (Game.GetPlayerController(0) is RPlayerController controller && controller.IsValid &&
                controller.RadialGadgetSelect is RGFxMovieRadialGadgetSelect movie && movie.IsValid && FindRoot(movie))
            {
                if (Corner.Owned)
                {
                    Apply(movie, Corner, false);
                }

                if (Wheel.Owned)
                {
                    Apply(movie, Wheel, false);
                }
            }
        }
        catch
        {
        }

        Forget();
    }

    public static void Forget()
    {
        Corner.Owned = Corner.Configured = false;
        Wheel.Owned = Wheel.Configured = false;
        Wheel.Path = "";
        root = null;
        rootMovie = IntPtr.Zero;
    }

    private static void Apply(RGFxMovieRadialGadgetSelect movie, Holder h, bool want)
    {
        string basePath = root + h.Path;
        string native = basePath + "." + NativeSet;
        string mine = basePath + "." + ClipName;

        bool own = want && (movie.GetVariableString(new FString(native + ".currentLabel")).ToString() ?? "") == NativeFrame;

        if (own)
        {
            bool present = movie.GetVariableNumber(new FString(mine + ".width")) > 0f;

            if (!present || !h.Configured)
            {
                if (!Attach(movie, basePath, present))
                {
                    return;
                }

                h.Configured = true;
                h.Owned = false;
            }

            if (!h.Owned)
            {
                movie.SetVariableBool(new FString(mine + ".visible"), true);
                h.Owned = true;
            }

            movie.SetVariableBool(new FString(native + ".visible"), false);

            return;
        }

        if (!h.Owned)
        {
            return;
        }

        h.Owned = false;
        movie.SetVariableBool(new FString(mine + ".visible"), false);

        if ((movie.GetVariableString(new FString(native + ".currentLabel")).ToString() ?? "") == NativeFrame)
        {
            movie.SetVariableBool(new FString(native + ".visible"), true);
        }
    }

    private static bool Attach(RGFxMovieRadialGadgetSelect movie, string holderPath, bool existing)
    {
        try
        {
            GFxObject? tf;

            if (existing)
            {
                tf = movie.GetVariableObject(new FString(holderPath + "." + ClipName));
            }
            else
            {
                GFxObject? holder = movie.GetVariableObject(new FString(holderPath));

                if (holder == null || !holder.IsValid)
                {
                    return false;
                }

                tf = holder.AttachMovie(new FString("flash.text.TextField"), new FString(ClipName));
            }

            if (tf == null || !tf.IsValid)
            {
                ModLog.Once("icon-attach", "Icon: could not create the icon field in '" + holderPath + "'.");

                return false;
            }

            tf.SetBool(new FString("selectable"), false);
            tf.SetBool(new FString("mouseEnabled"), false);
            tf.SetFloat(new FString("width"), S * 2 + 8);
            tf.SetFloat(new FString("height"), S + 8);
            tf.SetString(new FString("htmlText"), new FString("<img src='img://" + TexturePath + "' width='" + (S * 2) + "' height='" + S + "'>"));

            GFxObject.FASDisplayInfo d = tf.GetDisplayInfo();

            d.X = OffX;
            d.hasX = true;
            d.Y = OffY;
            d.hasY = true;
            tf.SetDisplayInfo(d);

            AddGlow(movie, tf);
            tf.SetVisible(false);
            ModLog.Info("Icon: rifle icon field " + (existing ? "set up again" : "created") + " in '" + holderPath + "'.");

            return true;
        }
        catch (Exception exception)
        {
            ModLog.Once("icon-attach-ex", "Icon: creating the icon field failed: " + Util.Unwrap(exception));

            return false;
        }
    }

    private static void AddGlow(RGFxMovieRadialGadgetSelect movie, GFxObject tf)
    {
        try
        {
            var args = new TArray<GFxMoviePlayer.FASValue>(6);

            foreach (float v in new[] { GlowColor, 0.9f, 10f, 10f, 2.2f, 2f })
            {
                GFxMoviePlayer.FASValue a = default;

                a.Type = GFxMoviePlayer.ASType.AS_Number;
                a.N = v;
                args.Add(a);
            }

            GFxObject? glow = movie.CreateObject(new FString("flash.filters.GlowFilter"), null, args);

            if (glow == null || !glow.IsValid)
            {
                return;
            }

            GFxObject? list = movie.CreateArray();

            if (list == null || !list.IsValid)
            {
                return;
            }

            list.SetElementObject(0, glow);
            tf.SetObject(new FString("filters"), list);
        }
        catch
        {
        }
    }

    private static bool FindRoot(RGFxMovieRadialGadgetSelect movie)
    {
        if (root != null && rootMovie == movie.Ptr)
        {
            return true;
        }

        if (rootMovie != IntPtr.Zero && rootMovie != movie.Ptr)
        {
            Corner.Owned = Corner.Configured = false;
            Wheel.Owned = Wheel.Configured = false;
            Wheel.Path = "";
        }

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
