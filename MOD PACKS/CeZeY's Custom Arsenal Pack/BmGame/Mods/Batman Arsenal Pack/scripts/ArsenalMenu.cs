#nullable enable

using System;
using ArkhamModMenu.Client;
using BmSDK;
using BmSDK.Framework;

namespace BatmanArsenalPack;

// On/off switches for the whole pack and its two weapons, shown in Arkham Mod Menu (pause menu > MODS) and saved
// in this folder's Settings.ini (editable by hand when the menu isn't installed). Every part reads these flags:
// its main loop uninstalls itself when switched off, and every one of its hooks hands straight back to the game.
public static class Arsenal
{
    private static ModMenu? menu;

    private static bool pack = true, guns = true, sniper = true, zipKick = true;

    /// <summary>The whole pack (guns, sniper, Dead Zone Aim, Batclaw Zip Kick).</summary>
    public static bool Pack => pack;

    /// <summary>Red Hood's guns.</summary>
    public static bool Guns => pack && guns;

    /// <summary>The sniper rifle mode on the Disruptor (with its laser).</summary>
    public static bool Sniper => pack && sniper;

    /// <summary>The Batclaw Zip Kick (double tap + hold the Batclaw key).</summary>
    public static bool ZipKick => pack && zipKick;

    private static readonly string[] YesNo = { "yes=YES", "no=NO" };

    public static void Init(Mod mod)
    {
        menu = new ModMenu(mod, "batmanarsenalpack", "Batman Arsenal Pack")
            .Title("ARSENAL", "PACK")
            .Choice("PackActive", "PACK ACTIVE", "yes", YesNo,
                "Turns the whole Arsenal Pack on or off: Red Hood's guns, the sniper rifle, the Batclaw Zip Kick and Dead Zone Aim. NO = vanilla Batman.")
            .Choice("GunsActive", "GUNS ACTIVE", "yes", YesNo,
                "Red Hood's dual handguns (hold 4, they share the Remote Electrical Charge slot). NO gives the slot back to the REC gun.")
            .Choice("SniperActive", "SNIPER ACTIVE", "yes", YesNo,
                "The Arkham Knight's sniper rifle with its red laser (hold 7, it shares the Disruptor slot). NO leaves the Disruptor as normal.")
            .Choice("ZipKickActive", "ZIP KICK ACTIVE", "yes", YesNo,
                "Batclaw Zip Kick: double tap the Batclaw key and hold the second tap to fly at a thug and kick him. NO = the Batclaw works as normal.")
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

        bool p = menu.Bool("PackActive"), g = menu.Bool("GunsActive"), s = menu.Bool("SniperActive"), z = menu.Bool("ZipKickActive");

        if (p != pack || g != guns || s != sniper || z != zipKick || !logged)
        {
            logged = true;

            try
            {
                Debug.Log("[Arsenal] Pack " + (p ? "ACTIVE" : "OFF") + ", guns " + (p && g ? "ACTIVE" : "OFF") + ", sniper " + (p && s ? "ACTIVE" : "OFF") + ", zip kick " + (p && z ? "ACTIVE" : "OFF") + ".");
            }
            catch
            {
            }
        }

        pack = p;
        guns = g;
        sniper = s;
        zipKick = z;
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
