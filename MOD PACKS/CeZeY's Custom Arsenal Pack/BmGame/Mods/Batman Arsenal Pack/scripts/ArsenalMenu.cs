#nullable enable

using System;
using ArkhamModMenu.Client;
using BmSDK;
using BmSDK.Framework;

namespace BatmanArsenalPack;

public static class Arsenal
{
    private static ModMenu? menu;

    private static bool pack = true, guns = true, sniper = true, zipKick = true, staff = true;

    public static bool Pack => pack;

    public static bool Guns => pack && guns;

    public static bool Sniper => pack && sniper;

    public static bool ZipKick => pack && zipKick;

    public static bool Staff => pack && staff;

    private static readonly string[] YesNo = { "yes=YES", "no=NO" };

    private static readonly string[] GunModels = { "redhood=RED HOOD", "custom=MECH DEAGLE" };

    private static readonly (string Key, string Label, byte R, byte G, byte B)[] Paints =
    {
        ("gold", "GOLD", 255, 180, 30),
        ("red", "RED", 200, 35, 35),
        ("crimson", "CRIMSON", 140, 10, 20),
        ("black", "BLACK", 20, 20, 22),
        ("white", "WHITE", 235, 235, 235),
        ("silver", "SILVER", 170, 175, 180),
        ("blue", "BLUE", 30, 80, 200),
        ("green", "GREEN", 30, 140, 60),
        ("purple", "PURPLE", 110, 40, 170),
        ("pink", "PINK", 230, 90, 160),
    };

    private sealed class SyncRow
    {
        public string Key = "";
        public Func<string> IniRaw = () => "";
        public Func<string, string> IniToMenu = raw => raw;
        public Func<string, string> MenuNorm = value => value;
        public Action<string> MenuToIni = _ => { };
        public string? SeenIni;
        public string? SeenMenu;
    }

    private static SyncRow[] rows = Array.Empty<SyncRow>();
    private static bool syncing;

    public static void Init(Mod mod)
    {
        menu = new ModMenu(mod, "batmanarsenalpack", "Batman Arsenal Pack")
            .Title("ARSENAL", "PACK")
            .Page("Gadgets")
            .Choice("PackActive", "PACK ACTIVE", "yes", YesNo,
                "Turns the whole Arsenal Pack on or off: Red Hood's guns, the sniper rifle, Robin's staff, the Batclaw Zip Kick and Dead Zone Aim. NO = none of these. The Console.txt moveset stays applied.")
            .Choice("GunsActive", "GUNS ACTIVE", "yes", YesNo,
                "Red Hood's dual handguns (hold 4, they share the Remote Electrical Charge slot). NO gives the slot back to the REC gun.")
            .Choice("SniperActive", "SNIPER ACTIVE", "yes", YesNo,
                "The Arkham Knight's sniper rifle with its red laser (hold 7, it shares the Disruptor slot). NO leaves the Disruptor as normal.")
            .Choice("ZipKickActive", "ZIP KICK ACTIVE", "yes", YesNo,
                "Batclaw Zip Kick: double tap the Batclaw key and hold the second tap to fly at a thug and kick him. NO = the Batclaw works as normal.")
            .Choice("StaffActive", "STAFF ACTIVE", "yes", YesNo,
                "Robin's staff (hold 5, it shares the Freeze Blast slot). While it is out, Batman fights with Robin's strikes, counters and takedowns. NO leaves the Freeze Blast as normal.")
            .Page("Guns")
            .Choice("GunModel", "GUN MODEL", "custom", GunModels,
                "Which pistols Batman uses, in his hands and in the thigh holsters. RED HOOD = Red Hood's own pistols. MECH DEAGLE = the Mech Deagle model by Long Nguyen (Sketchfab, CC BY 4.0), converted for Arkham Knight.")
            .Toggle("GunPaint", "GUN PAINT", false,
                "Paints Batman's pistols (in hand and in the holsters) in the PAINT COLOR below. OFF = the model's own colours.")
            .Choice("PaintColor", "PAINT COLOR", "red", PaintOptions(),
                "Colour used when GUN PAINT is ON. CUSTOM = the gun_rgb value you typed in BatmanGuns.ini (picking it keeps that value).")
            .Slider("GunSize", "GUN SIZE", 50, 200, 5, 130, "{0}%",
                "Size of the Mech Deagle, in hand and in the holsters. 100% = the size it was converted at. Red Hood's own pistols keep their size.")
            .Register();

        rows = BuildRows();

        menu.Changed += _ =>
        {
            Read();
            SyncRows();
        };
        Read();
    }

    public static void Poll()
    {
        menu?.Poll();
        SyncRows();
    }

    private static string[] PaintOptions()
    {
        string[] options = new string[Paints.Length + 1];

        for (int index = 0; index < Paints.Length; index++)
        {
            options[index] = Paints[index].Key + "=" + Paints[index].Label;
        }

        options[Paints.Length] = "custom=CUSTOM";

        return options;
    }

    private static SyncRow[] BuildRows()
    {
        var c = () => BatmanRedHoodGuns.GunsConfig.C;

        return new[]
        {
            new SyncRow
            {
                Key = "GunModel",
                IniRaw = () => c().CustomModel ? "custom" : "redhood",
                MenuNorm = v => v == "custom" ? "custom" : "redhood",
                MenuToIni = v => BatmanRedHoodGuns.GunsConfig.SetCustomModel(v == "custom"),
            },
            new SyncRow
            {
                Key = "GunPaint",
                IniRaw = () => c().GunColor ? "on" : "off",
                MenuNorm = v => v is "on" or "true" or "1" or "yes" ? "on" : "off",
                MenuToIni = v => BatmanRedHoodGuns.GunsConfig.SetGunColor(v == "on"),
            },
            new SyncRow
            {
                Key = "PaintColor",
                IniRaw = () => c().ColorR + "," + c().ColorG + "," + c().ColorB,
                IniToMenu = raw =>
                {
                    foreach (var paint in Paints)
                    {
                        if (raw == paint.R + "," + paint.G + "," + paint.B)
                        {
                            return paint.Key;
                        }
                    }

                    return "custom";
                },
                MenuNorm = v => Array.Exists(Paints, p => p.Key == v) ? v : "custom",
                MenuToIni = v =>
                {
                    foreach (var paint in Paints)
                    {
                        if (paint.Key == v)
                        {
                            BatmanRedHoodGuns.GunsConfig.SetGunRgb(paint.R, paint.G, paint.B);
                        }
                    }
                },
            },
            new SyncRow
            {
                Key = "GunSize",
                IniRaw = () => ((int)MathF.Round(c().CustomScale * 100f)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                MenuNorm = v => float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f)
                    ? ((int)MathF.Round(f)).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "100",
                MenuToIni = v => BatmanRedHoodGuns.GunsConfig.SetCustomScale(int.Parse(v, System.Globalization.CultureInfo.InvariantCulture) / 100f),
            },
        };
    }

    private static void SyncRows()
    {
        if (menu == null || syncing || !BatmanRedHoodGuns.GunsConfig.Loaded)
        {
            return;
        }

        syncing = true;

        try
        {
            foreach (SyncRow row in rows)
            {
                SyncOne(row);
            }
        }
        catch (Exception exception)
        {
            Log("Syncing the guns settings failed: " + exception.Message);
        }
        finally
        {
            syncing = false;
        }
    }

    private static void SyncOne(SyncRow row)
    {
        string raw = row.IniRaw();
        string current = row.MenuNorm(menu!.Str(row.Key).Trim().ToLowerInvariant());

        if (row.SeenIni == null)
        {
            row.SeenIni = raw;
            row.SeenMenu = row.IniToMenu(raw);

            if (current != row.SeenMenu)
            {
                menu.Set(row.Key, row.SeenMenu);
            }

            return;
        }

        if (current != row.SeenMenu)
        {
            row.SeenMenu = current;
            row.MenuToIni(current);
            row.SeenIni = row.IniRaw();
            Log(row.Key + " set to " + current + " from the mod menu.");

            return;
        }

        if (raw != row.SeenIni)
        {
            row.SeenIni = raw;
            row.SeenMenu = row.IniToMenu(raw);

            if (current != row.SeenMenu)
            {
                menu.Set(row.Key, row.SeenMenu);
            }
        }
    }

    private static void Log(string message)
    {
        try { Debug.Log("[Arsenal] " + message); } catch { }
    }

    private static void Read()
    {
        if (menu == null)
        {
            return;
        }

        bool p = menu.Bool("PackActive"), g = menu.Bool("GunsActive"), s = menu.Bool("SniperActive"), z = menu.Bool("ZipKickActive"), st = menu.Bool("StaffActive");

        if (p != pack || g != guns || s != sniper || z != zipKick || st != staff || !logged)
        {
            logged = true;

            try
            {
                Debug.Log("[Arsenal] Pack " + (p ? "ACTIVE" : "OFF") + ", guns " + (p && g ? "ACTIVE" : "OFF") + ", sniper " + (p && s ? "ACTIVE" : "OFF") + ", zip kick " + (p && z ? "ACTIVE" : "OFF") + ", staff " + (p && st ? "ACTIVE" : "OFF") + ".");
            }
            catch
            {
            }
        }

        pack = p;
        guns = g;
        sniper = s;
        zipKick = z;
        staff = st;
    }

    private static bool logged;
}

[Script]
public class ArsenalMenuScript : Script
{
    public override void Main() => Setup();

    public override void OnLoad() => Setup();

    private void Setup()
    {
        try
        {
            Arsenal.Init(Mod);
        }
        catch (Exception exception)
        {
            try { Debug.LogWarning("[Arsenal] Mod menu setup failed: " + exception.Message); } catch { }
        }
    }

    public override void OnTick() => Arsenal.Poll();
}
