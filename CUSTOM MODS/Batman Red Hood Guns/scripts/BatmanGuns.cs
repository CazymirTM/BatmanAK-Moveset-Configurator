#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace BatmanRedHoodGuns;

[Script]
public class BatmanRedHoodGunsScript : Script
{
    private static readonly Keys ReportKey = Keys.F10;
    private static readonly Keys AlwaysInHandsKey = Keys.F5;
    private static readonly Keys ReloadKey = Keys.R;

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

    public override void Main()
    {
        Util.TryCall("config", () => GunsConfig.Init(Mod.DirectoryPath));
    }

    public override void OnLoad()
    {
        Util.TryCall("config", () => GunsConfig.Init(Mod.DirectoryPath));
    }

    public override void OnEnterGame()
    {
        ResetTracking();
        ModLog.Info("Loaded. " + ReloadKey + " = reload, " + AlwaysInHandsKey + " = pistols always in hand, " + ReportKey + " = state dump.");
    }

    public override void OnUnload()
    {
        GunsWheel.RestoreReplaced();
        GunsAim.DetachPistols();
        ResetTracking();
    }

    public override void OnKeyDown(Keys key)
    {
        GunsPad.KeyboardUsed();

        if (key == ReportKey)
        {
            Util.TryCall("report", Report);
        }

        if (key == ReloadKey && Guns.HeldBy(out RPawnPlayer? holder, out _) && holder != null)
        {
            GunsWheel.ReloadKeyPressed();
            Util.TryCall("manual reload", () => GunsHud.ManualReload(holder));
        }

        if (key == AlwaysInHandsKey && Guns.Batman() != null && Guns.Gun != null && Guns.Gun.IsValid)
        {
            Util.TryCall("always in hand", GunsMode.ToggleAlwaysInHands);
        }

        GunsAim.OnKeyDown(key);
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
        GunsConfig.Tick();

        RPlayerController? controller = Game.GetPlayerController(0);
        RPawnPlayer? pawn = controller?.Pawn as RPawnPlayer;

        if (controller == null || pawn == null || !pawn.IsValid || !Guns.IsBatman(pawn))
        {
            if (trackedPawn != IntPtr.Zero)
            {
                ResetTracking();
            }

            GunsAim.Tick(null);

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

        GunsAim.Tick(pawn);
        GunIcons.Tick(controller, GunsWheel.SharedSlotIndex);
        GunsWheel.Tick(controller);
        GunsMode.Tick(controller);
        GunsPad.Installed = true;

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
        Guns.Gun = null;

        GunsAnims.Reset();
        GunsAim.Reset();
        GunsHud.Reset();
        GunIcons.Reset();
        GunsGrip.Reset();
        GunsDeadZone.Reset();
        GunsMode.Reset();
        GunsPad.Reset();
        GunsPad.Installed = false;
        GunsWheel.ResetKeys();
    }

    private bool TryInstall(RPlayerController controller, RPawnPlayer pawn)
    {
        RInventoryManager? inventory = pawn.InvManager as RInventoryManager;

        if (inventory == null || !inventory.IsValid)
        {
            ModLog.Info("Inventory manager not ready yet (attempt " + installAttempts + ").");

            return false;
        }

        if (!GunsWheel.WheelReady(inventory))
        {
            installAttempts--;
            ModLog.Once("wheel-wait:" + pawn.Ptr, "Waiting for the game to set up Batman's gadget wheel before installing.");

            return false;
        }

        Class? gunClass = LoadGunClass();

        if (gunClass == null)
        {
            return false;
        }

        RInventoryGadget? gun = FindGun(inventory);

        if (gun == null)
        {
            if (!CheckBones(pawn, gunClass))
            {
                return false;
            }

            try
            {
                pawn.GiveGadget(gunClass, true, false);
            }
            catch (Exception exception)
            {
                ModLog.Warn("GiveGadget(" + Guns.ClassName + ") failed: " + Util.Unwrap(exception));
            }

            gun = FindGun(inventory);

            if (gun == null)
            {
                ModLog.Warn("GiveGadget did not produce the guns (attempt " + installAttempts + ").");

                return false;
            }

            Util.TryCall("NotifyAddInventory", () => controller.NotifyAddInventory(gun));
            ModLog.Info("Red Hood's guns granted to Batman through RPawnPlayer.GiveGadget.");
        }

        Guns.Gun = gun;
        Configure(pawn, gun);

        GunsAnims.Install(pawn, gun);
        GunsWheel.PlaceOnWheel(controller, inventory);
        GunsAim.HookCamera(gunClass);

        ModLog.Info("Batman now has Red Hood's guns (sharing the " + Guns.ReplacedGadget + " wheel slot, now showing the " + (GunsMode.GunsSelected ? "guns" : Guns.ReplacedGadget) + ").");
        DumpState(pawn, inventory);

        return true;
    }

    private static readonly string[] KeepAlive =
    {
        Guns.ClassPath,
        "WBGMDLC_Player_RedHood_Anim_Config.RedHood_Anim_Config",
        "WBGMDLC_Player_RedHood_Anim_Config.PoseConfig_RhGadgets",
        "WBGMDLC_Redhood_Special_Moves.Gadget.HandGun",
        "WBGMDLC_Redhood_Special_Moves.Gadget.HandGunQuickFire",
        "DLC0_Anim_RedHood_Gadget.Code.RH_HandGun",
        "DLC0_Anim_RedHood_Movement.Code.RH_Gadget_Turns",
        "DLC0_Anim_RedHood_TG_Com.Code.RH_TG_Beatdown",
        "DLC0_Anim_RedHood_Combat.Code.RH_Attack_Far",
    };

    private static Class? LoadGunClass()
    {
        List<string> missing = KeepAlive.Where(path => Game.FindObject<GameObject>(path) == null).ToList();

        if (missing.Count > 0)
        {
            try
            {
                Package? package = Game.LoadPackage(Guns.Package);

                ModLog.Info("LoadPackage(" + Guns.Package + ") -> " + (package != null && package.IsValid ? "loaded" : "null") + " (was missing: " + string.Join(", ", missing) + ").");
            }
            catch (Exception exception)
            {
                ModLog.Warn("LoadPackage(" + Guns.Package + ") failed: " + Util.Unwrap(exception));
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

        Class? gunClass = Game.FindObject<Class>(Guns.ClassPath);

        if (gunClass == null)
        {
            ModLog.Once("noclass", "Class " + Guns.ClassPath + " not found. Is the Red Hood Story Pack installed?");
        }

        return gunClass;
    }

    private static RInventoryGadget? FindGun(RInventoryManager inventory)
    {
        try
        {
            RInventoryGadget? gadget = inventory.GetGadgetByName(new FName(Guns.ClassName), true);

            return gadget != null && gadget.IsValid ? gadget : null;
        }
        catch (Exception exception)
        {
            ModLog.Warn("GetGadgetByName failed: " + Util.Unwrap(exception));

            return null;
        }
    }

    private static bool CheckBones(RPawnPlayer pawn, Class gunClass)
    {
        SkeletalMeshComponent? mesh = pawn.Mesh;
        RInventoryGadget? defaults = gunClass.DefaultObject as RInventoryGadget;

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

        ModLog.Info("Gun attachment points on Batman (" + mesh.SkeletalMesh?.Name + "): " + string.Join(", ", report) + ".");

        return true;
    }

    private const int NotInGadgetList = 255;

    private static void Configure(RPawnPlayer pawn, RInventoryGadget gun)
    {
        try
        {
            if (gun.bSelectable != GunsMode.GunsSelected && !Guns.IsGun((pawn.InvManager as RInventoryManager)?.CurrentGadget)) gun.bSelectable = GunsMode.GunsSelected;
            if (!pawn.bCanUseGadgetSelect) pawn.bCanUseGadgetSelect = true;

            int listIndex = gun.GadgetListEnum;

            if (listIndex != NotInGadgetList)
            {
                gun.GadgetListEnum = NotInGadgetList;
                ModLog.Info("Guns taken out of Batman's gadget list (GadgetListEnum " + listIndex + " -> " + NotInGadgetList + ").");
            }

            RestoreGadgetList(pawn, gun);
        }
        catch (Exception exception)
        {
            ModLog.Warn("Configuring the guns failed: " + Util.Unwrap(exception));
        }
    }

    private static void RestoreGadgetList(RPawnPlayer pawn, RInventoryGadget gun)
    {
        var list = pawn.GadgetList;

        for (int index = 0; index < 32; index++)
        {
            RInventoryGadget? entry = list[index];

            if (entry == null || !entry.IsValid || entry.Ptr != gun.Ptr)
            {
                continue;
            }

            RInventoryGadget? owner = null;

            foreach (RInventoryGadget gadget in Game.FindObjects<RInventoryGadget>())
            {
                if (gadget != null && gadget.IsValid && gadget.Ptr != gun.Ptr && gadget.Owner?.Ptr == pawn.Ptr && gadget.GadgetListEnum == index)
                {
                    owner = gadget;
                }
            }

            list[index] = owner!;
            ModLog.Info("Batman's GadgetList[" + index + "] given back to " + (owner != null ? Util.ClassName(owner) : "<none>") + " (the guns had taken it).");
        }
    }

    private void Maintain(RPlayerController controller, RPawnPlayer pawn)
    {
        RInventoryGadget? gun = Guns.Gun;
        RInventoryManager? inventory = pawn.InvManager as RInventoryManager;

        if (gun == null || !gun.IsValid || inventory == null || !inventory.IsValid)
        {
            ModLog.Warn("The guns are gone; reinstalling.");
            installed = false;
            gaveUp = false;
            installAttempts = 0;
            nextInstallAttempt = DateTime.UtcNow + RetryInterval;

            return;
        }

        Configure(pawn, gun);
        GunsWheel.Maintain(controller, inventory);
        GunsAnims.Maintain(pawn, gun);
    }

    private void Report()
    {
        RPawnPlayer? pawn = Game.GetPlayerController(0)?.Pawn as RPawnPlayer;

        if (pawn == null)
        {
            ModLog.Info("Report: no player pawn.");

            return;
        }

        ModLog.Info("Report: pawn=" + Util.ClassName(pawn) + ", batman=" + Guns.IsBatman(pawn) + ", installed=" + installed + ", gaveUp=" + gaveUp + ".");

        if (pawn.InvManager is RInventoryManager inventory && inventory.IsValid)
        {
            DumpState(pawn, inventory);
        }

        GunsAim.Report();
        ModLog.Info("Report: slot=" + (GunsMode.GunsSelected ? "guns" : "rec") + ", shared wheel slot " + GunsWheel.SharedSlotIndex + ".");
    }

    private static void DumpState(RPawnPlayer pawn, RInventoryManager inventory)
    {
        try
        {
            TArray<FName> names = inventory.BM2SelectableGadgets;
            RInventoryGadget? current = inventory.CurrentGadget;
            RInventoryGadget? gun = Guns.Gun;

            ModLog.Info(
                "State: BM2=[" + string.Join(",", Enumerable.Range(0, names.Count).Select(index => index + ":" + names[index])) + "]" +
                ", current=" + Util.ClassName(current) +
                (gun != null && gun.IsValid
                    ? ", gun state=" + gun.GadgetState + "/" + gun.GetStateName() + ", aiming=" + gun.bAiming + ", HudSlot=" + gun.HudSlot +
                      ", GadgetListEnum=" + gun.GadgetListEnum + ", bones=" + gun.EquipBoneName + "/" + gun.UnequipBoneName
                    : ", gun=<none>") + "."
            );
        }
        catch (Exception exception)
        {
            ModLog.Warn("State dump failed: " + Util.Unwrap(exception));
        }
    }
}
