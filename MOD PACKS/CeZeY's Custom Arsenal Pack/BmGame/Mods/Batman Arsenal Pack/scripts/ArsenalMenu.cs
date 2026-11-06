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

    public static void Init(Mod mod)
    {
        menu = new ModMenu(mod, "batmanarsenalpack", "Batman Arsenal Pack")
            .Title("ARSENAL", "PACK")
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
            .Register();

        menu.Changed += _ => Read();
        Read();
    }

    public static void Poll() => menu?.Poll();

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
