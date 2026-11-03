#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace BatmanNightwingSticks;

[Script]
public class BatmanNightwingSticksScript : Script
{
    private static readonly Keys ReportKey = Keys.F10;

    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(1);
    private const int MaxInstallAttempts = 5;

    private IntPtr trackedPawn = IntPtr.Zero;
    private DateTime nextInstallAttempt = DateTime.MinValue;
    private DateTime nextMaintenance = DateTime.MinValue;
    private int installAttempts;
    private bool installed;
    private bool gaveUp;
    private bool tickFailureLogged;

    public override void OnEnterGame()
    {
        ResetTracking();
        ModLog.Info("Loaded. " + ReportKey + " = state dump.");
    }

    public override void OnUnload()
    {
        SticksWheel.RestoreSmoke();
        SticksAnims.Release();
        SticksProps.Detach();
        ResetTracking();
    }

    public override void OnKeyDown(Keys key)
    {
        if (key == ReportKey)
        {
            Util.TryCall("report", Report);
        }
    }

    public override void OnTick()
    {
        try
        {
            Update();
        }
        catch (Exception exception)
        {
            if (!tickFailureLogged)
            {
                tickFailureLogged = true;
                ModLog.Warn("Tick failed (further failures are not logged): " + Util.Unwrap(exception));
            }
        }
    }

    private void Update()
    {
        RPlayerController? controller = Game.GetPlayerController(0);
        RPawnPlayer? pawn = controller?.Pawn as RPawnPlayer;

        if (controller == null || pawn == null || !pawn.IsValid || !Sticks.IsBatman(pawn))
        {
            if (trackedPawn != IntPtr.Zero)
            {
                ResetTracking();
            }

            SticksProps.Tick(null);
            SticksFlight.Tick(null);

            return;
        }

        DateTime now = DateTime.UtcNow;

        if (pawn.Ptr != trackedPawn)
        {
            ResetTracking();
            trackedPawn = pawn.Ptr;
            nextInstallAttempt = now + SettleTime;
            nextMaintenance = now + SettleTime + MaintenanceInterval;

            ModLog.Info("Batman pawn detected (" + Util.ClassName(pawn) + "); installing in " + SettleTime.TotalSeconds + "s.");

            return;
        }

        if (!installed)
        {
            if (gaveUp || now < nextInstallAttempt)
            {
                return;
            }

            installAttempts++;
            nextInstallAttempt = now + RetryInterval;

            if (TryInstall(controller, pawn))
            {
                installed = true;
            }
            else if (installAttempts >= MaxInstallAttempts)
            {
                gaveUp = true;
                ModLog.Warn("Giving up after " + installAttempts + " attempt(s). Press " + ReportKey + " for a state dump.");
            }

            return;
        }

        SticksAnims.Tick(pawn);
        SticksFlight.Tick(pawn);
        SticksProps.Tick(pawn);
        SticksMode.Tick(controller);
        SticksIcons.Tick(controller, SticksWheel.SlotIndex);
        SticksWheel.Tick(controller);

        if (now >= nextMaintenance)
        {
            nextMaintenance = now + MaintenanceInterval;
            Maintain(controller, pawn);
        }
    }

    private void ResetTracking()
    {
        trackedPawn = IntPtr.Zero;
        nextInstallAttempt = DateTime.MinValue;
        nextMaintenance = DateTime.MinValue;
        installAttempts = 0;
        installed = false;
        gaveUp = false;
        Sticks.Gadget = null;
        Sticks.Blast = null;
        SticksMode.Reset();

        SticksAnims.Reset();
        SticksIcons.Reset();
        SticksProps.Reset();
        SticksFlight.Reset();
    }

    private bool TryInstall(RPlayerController controller, RPawnPlayer pawn)
    {
        RInventoryManager? inventory = pawn.InvManager as RInventoryManager;

        if (inventory == null || !inventory.IsValid)
        {
            ModLog.Info("Inventory manager not ready yet (attempt " + installAttempts + ").");

            return false;
        }

        if (!SticksWheel.WheelReady(inventory))
        {
            installAttempts--;
            ModLog.Once("wheel-wait:" + pawn.Ptr, "Waiting for the game to set up Batman's gadget wheel before installing.");

            return false;
        }

        WarnIfGunsMod(inventory);

        Class? sticksClass = LoadSticksClass();

        if (sticksClass == null)
        {
            return false;
        }

        RInventoryGadget? sticks = FindSticks(inventory);

        if (sticks == null)
        {
            if (!CheckBones(pawn, sticksClass))
            {
                return false;
            }

            try
            {
                pawn.GiveGadget(sticksClass, true, false);
            }
            catch (Exception exception)
            {
                ModLog.Warn("GiveGadget(" + Sticks.ClassName + ") failed: " + Util.Unwrap(exception));
            }

            sticks = FindSticks(inventory);

            if (sticks == null)
            {
                ModLog.Warn("GiveGadget did not produce the sticks (attempt " + installAttempts + ").");

                return false;
            }

            Util.TryCall("NotifyAddInventory", () => controller.NotifyAddInventory(sticks));
            ModLog.Info("Nightwing's sticks granted to Batman through RPawnPlayer.GiveGadget.");
        }

        Sticks.Gadget = sticks;
        Configure(pawn, sticks);
        InstallBlast(controller, pawn, inventory, sticks);

        SticksAnims.Install(pawn, sticks);
        SticksWheel.PlaceOnWheel(controller, inventory);

        ModLog.Info("Batman now has Nightwing's sticks (in the " + Sticks.ReplacedGadget + " wheel slot).");
        DumpState(pawn, inventory);

        return true;
    }

    private static void WarnIfGunsMod(RInventoryManager inventory)
    {
        try
        {
            RInventoryGadget? guns = inventory.GetGadgetByName(new FName("MDualHandgunsRh"), true);

            if (guns != null && guns.IsValid)
            {
                ModLog.Once("guns-mod", "WARNING: Batman Red Hood Guns is installed. It is NOT compatible with Batman Nightwing Sticks (they share animation names and wheel hooks). Remove one of them and restart the game.");
            }
        }
        catch
        {
        }
    }

    private static void InstallBlast(RPlayerController controller, RPawnPlayer pawn, RInventoryManager inventory, RInventoryGadget sticks)
    {
        try
        {
            Class? blastClass = Game.FindObject<Class>(Sticks.BlastClassPath);

            if (blastClass == null)
            {
                ModLog.Once("noblast", "Class " + Sticks.BlastClassPath + " not found; the electromagnetic blast is unavailable.");

                return;
            }

            RInventoryGadget? blast = inventory.GetGadgetByName(new FName(Sticks.BlastClassName), true);

            if (blast == null || !blast.IsValid)
            {
                pawn.GiveGadget(blastClass, true, false);
                blast = inventory.GetGadgetByName(new FName(Sticks.BlastClassName), true);

                if (blast == null || !blast.IsValid)
                {
                    ModLog.Warn("GiveGadget did not produce Nightwing's electromagnetic blast.");

                    return;
                }

                Util.TryCall("NotifyAddInventory", () => controller.NotifyAddInventory(blast));
                ModLog.Info("Nightwing's electromagnetic blast granted to Batman (hold the sticks' key for 1 s to switch).");
            }

            Sticks.Blast = blast;
            Configure(pawn, blast);
            SyncBlastSlot(sticks, blast);
        }
        catch (Exception exception)
        {
            ModLog.Warn("Installing the electromagnetic blast failed: " + Util.Unwrap(exception));
        }
    }

    private static void SyncBlastSlot(RInventoryGadget sticks, RInventoryGadget blast)
    {
        if (blast.HudSlot != sticks.HudSlot) blast.HudSlot = sticks.HudSlot;
        if (blast.HudSlotPC != sticks.HudSlotPC) blast.HudSlotPC = sticks.HudSlotPC;
    }

    private static readonly string[] KeepAlive =
    {
        Sticks.ClassPath,
        "BmScript.RNightwingSticksBounceProjectile",
        "BmScript.RNightwingSticksBounceCamera",
        "Player_Anim_Config.Nightwing_Anim_Config",
        "Player_Anim_Config.PoseConfig_NwGadgets",
        "Anim_Nightwing_Gadget.Code.NW_Ricochet",
        "Anim_Nightwing_Gadget.Code.NW_Electromagnetic_Blast",
        "BmScript.RNightwingSticks",
        "Nightwing.Mesh.Weapons_NightWing_Sticks",
        "Nightwing.Mesh.Nightwing_Stick_Anims",
    };

    private static Class? LoadSticksClass()
    {
        List<string> missing = KeepAlive.Where(path => Game.FindObject<GameObject>(path) == null).ToList();

        if (missing.Count > 0)
        {
            foreach (string name in Sticks.Packages)
            {
                try
                {
                    Package? package = Game.LoadPackage(name);

                    ModLog.Info("LoadPackage(" + name + ") -> " + (package != null && package.IsValid ? "loaded" : "null") + " (was missing: " + string.Join(", ", missing) + ").");
                }
                catch (Exception exception)
                {
                    ModLog.Warn("LoadPackage(" + name + ") failed: " + Util.Unwrap(exception));
                }
            }
        }

        foreach (string path in KeepAlive)
        {
            GameObject? keep = Game.FindObject<GameObject>(path);

            if (keep == null)
            {
                ModLog.Once("keep:" + path, "Not found after loading the package: " + path + ".");
            }
            else
            {
                keep.AddToRoot();
            }
        }

        Class? sticksClass = Game.FindObject<Class>(Sticks.ClassPath);

        if (sticksClass == null)
        {
            ModLog.Once("noclass", "Class " + Sticks.ClassPath + " not found.");
        }

        return sticksClass;
    }

    private static RInventoryGadget? FindSticks(RInventoryManager inventory)
    {
        try
        {
            RInventoryGadget? gadget = inventory.GetGadgetByName(new FName(Sticks.ClassName), true);

            return gadget != null && gadget.IsValid ? gadget : null;
        }
        catch (Exception exception)
        {
            ModLog.Warn("GetGadgetByName failed: " + Util.Unwrap(exception));

            return null;
        }
    }

    private static bool CheckBones(RPawnPlayer pawn, Class sticksClass)
    {
        SkeletalMeshComponent? mesh = pawn.Mesh;
        RInventoryGadget? defaults = sticksClass.DefaultObject as RInventoryGadget;

        if (mesh == null || !mesh.IsValid || defaults == null)
        {
            return false;
        }

        List<string> report = new List<string>();

        foreach ((string label, FName bone) in new[] { ("Equip", defaults.EquipBoneName), ("Equip2", defaults.EquipBoneName2), ("Holster", defaults.UnequipBoneName) })
        {
            if (Util.IsNone(bone))
            {
                continue;
            }

            bool found = false;

            try { found = mesh.MatchRefBone(bone) >= 0 || mesh.GetSocketByName(bone) != null; } catch { }

            report.Add(label + "=" + bone + (found ? "" : " MISSING"));
        }

        ModLog.Info("Stick attachment points on Batman (" + mesh.SkeletalMesh?.Name + "): " + string.Join(", ", report) + ".");

        return true;
    }

    private const int NotInGadgetList = 255;

    private static void Configure(RPawnPlayer pawn, RInventoryGadget sticks)
    {
        try
        {
            if (!sticks.bSelectable) sticks.bSelectable = true;
            if (!pawn.bCanUseGadgetSelect) pawn.bCanUseGadgetSelect = true;

            int listIndex = sticks.GadgetListEnum;

            if (listIndex != NotInGadgetList)
            {
                sticks.GadgetListEnum = NotInGadgetList;
                ModLog.Info("Sticks taken out of Batman's gadget list (GadgetListEnum " + listIndex + " -> " + NotInGadgetList + ").");
            }

            RestoreGadgetList(pawn, sticks);
        }
        catch (Exception exception)
        {
            ModLog.Warn("Configuring the sticks failed: " + Util.Unwrap(exception));
        }
    }

    private static void RestoreGadgetList(RPawnPlayer pawn, RInventoryGadget sticks)
    {
        var list = pawn.GadgetList;

        for (int index = 0; index < 32; index++)
        {
            RInventoryGadget? entry = list[index];

            if (entry == null || !entry.IsValid || entry.Ptr != sticks.Ptr)
            {
                continue;
            }

            RInventoryGadget? owner = null;

            foreach (RInventoryGadget gadget in Game.FindObjects<RInventoryGadget>())
            {
                if (gadget != null && gadget.IsValid && gadget.Ptr != sticks.Ptr && gadget.Owner?.Ptr == pawn.Ptr && gadget.GadgetListEnum == index)
                {
                    owner = gadget;
                }
            }

            list[index] = owner!;
            ModLog.Info("Batman's GadgetList[" + index + "] given back to " + (owner != null ? Util.ClassName(owner) : "<none>") + " (the sticks had taken it).");
        }
    }

    private void Maintain(RPlayerController controller, RPawnPlayer pawn)
    {
        RInventoryGadget? sticks = Sticks.Gadget;
        RInventoryManager? inventory = pawn.InvManager as RInventoryManager;

        if (sticks == null || !sticks.IsValid || inventory == null || !inventory.IsValid)
        {
            ModLog.Warn("The sticks are gone; reinstalling.");
            installed = false;
            gaveUp = false;
            installAttempts = 0;
            nextInstallAttempt = DateTime.UtcNow + RetryInterval;

            return;
        }

        Configure(pawn, sticks);

        if (Sticks.Blast != null && Sticks.Blast.IsValid)
        {
            Configure(pawn, Sticks.Blast);
            SyncBlastSlot(sticks, Sticks.Blast);
        }
        else
        {
            InstallBlast(controller, pawn, inventory, sticks);
        }

        SticksWheel.Maintain(controller, inventory);
        SticksAnims.Maintain(pawn, sticks);
    }

    private void Report()
    {
        RPawnPlayer? pawn = Game.GetPlayerController(0)?.Pawn as RPawnPlayer;

        if (pawn == null)
        {
            ModLog.Info("Report: no player pawn.");

            return;
        }

        ModLog.Info("Report: pawn=" + Util.ClassName(pawn) + ", batman=" + Sticks.IsBatman(pawn) + ", installed=" + installed + ", gaveUp=" + gaveUp + ".");

        if (pawn.InvManager is RInventoryManager inventory && inventory.IsValid)
        {
            DumpState(pawn, inventory);
        }
    }

    private static void DumpState(RPawnPlayer pawn, RInventoryManager inventory)
    {
        try
        {
            TArray<FName> names = inventory.BM2SelectableGadgets;
            RInventoryGadget? current = inventory.CurrentGadget;
            RInventoryGadget? sticks = Sticks.Gadget;

            ModLog.Info(
                "State: BM2=[" + string.Join(",", Enumerable.Range(0, names.Count).Select(index => index + ":" + names[index])) + "]" +
                ", current=" + Util.ClassName(current) +
                (sticks != null && sticks.IsValid
                    ? ", sticks state=" + sticks.GadgetState + "/" + sticks.GetStateName() + ", aiming=" + sticks.bAiming + ", HudSlot=" + sticks.HudSlot +
                      ", GadgetListEnum=" + sticks.GadgetListEnum + ", bones=" + sticks.EquipBoneName + "/" + sticks.UnequipBoneName
                    : ", sticks=<none>") + "."
            );
        }
        catch (Exception exception)
        {
            ModLog.Warn("State dump failed: " + Util.Unwrap(exception));
        }
    }
}
