#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Framework;

namespace BatmanRedHoodGuns;

internal static class GunsWheel
{
    private static int flashSlot = -1;

    private static int GunSlot
    {
        get
        {
            if (flashSlot >= 0)
            {
                return flashSlot;
            }

            RInventoryManager? inventory = Guns.Batman()?.InvManager as RInventoryManager;

            return inventory != null && inventory.IsValid ? IndexOf(inventory.BM2SelectableGadgets, Guns.ClassName) : -1;
        }
    }
    public static int GunSlotIndex => GunSlot;

    private static int replacedFlashSlot = -1;

    private static int SharedSlot
    {
        get
        {
            if (flashSlot >= 0)
            {
                return flashSlot;
            }

            if (replacedFlashSlot >= 0)
            {
                return replacedFlashSlot;
            }

            RInventoryManager? inventory = Guns.Batman()?.InvManager as RInventoryManager;

            if (inventory == null || !inventory.IsValid)
            {
                return -1;
            }

            int guns = IndexOf(inventory.BM2SelectableGadgets, Guns.ClassName);

            return guns >= 0 ? guns : IndexOf(inventory.BM2SelectableGadgets, Guns.ReplacedGadget);
        }
    }

    public static int SharedSlotIndex => SharedSlot;

    private static readonly List<string> slotClasses = new List<string>();

    private static bool LogSelections = true;

    private static readonly string[] KeyNames = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "=" };

    public static void PlaceOnWheel(RPlayerController controller, RInventoryManager inventory)
    {
        try
        {
            ModLog.Once("keymap", "Wheel: number key -> wheel slot: " + string.Join(", ", KeyNames.Select((key, n) => key + "->" + controller.PCGadgetIndexToWheelIndex(n))) + ".");
        }
        catch (Exception exception)
        {
            ModLog.Once("keymap-failed", "Wheel: reading the key map failed: " + Util.Unwrap(exception));
        }

        ApplyMode(controller, inventory, equip: false);
    }

    private static string previousGadget = "";

    public static void ApplyMode(RPlayerController controller, RInventoryManager inventory, bool equip)
    {
        RInventoryGadget? gun = Guns.Gun;

        if (gun == null || !gun.IsValid)
        {
            return;
        }

        flashSlot = -1;
        replacedFlashSlot = -1;

        if (GunsMode.GunsSelected)
        {
            RetireReplaced(inventory);

            int index = SwapInList(inventory.BM2SelectableGadgets, Guns.ReplacedGadget, Guns.ClassName);
            int pcIndex = SwapInList(inventory.PCSelectableGadgets, Guns.ReplacedGadget, Guns.ClassName);

            if (!gun.bSelectable)
            {
                gun.bSelectable = true;
            }

            if (index >= 0)
            {
                gun.HudSlot = (byte)index;
            }

            ModLog.Info("Wheel: guns placed in BM2SelectableGadgets[" + index + "] (PC list index " + pcIndex + "), in place of " + Guns.ReplacedGadget + ".");
            Refresh(controller);
            UpdateKeyLabel(controller);

            if (equip && controller.RadialGadgetSelect is RGFxMovieRadialGadgetSelect radial && radial.IsValid)
            {
                EnsureSelected(radial, "slot switch");
            }

            return;
        }

        bool held = Guns.IsGun(inventory.CurrentGadget);
        int replacedIndex = SwapInList(inventory.BM2SelectableGadgets, Guns.ClassName, Guns.ReplacedGadget);

        SwapInList(inventory.PCSelectableGadgets, Guns.ClassName, Guns.ReplacedGadget);
        RestoreReplaced();

        if (gun.bSelectable)
        {
            gun.bSelectable = false;
        }

        gun.HudSlot = RetiredHudSlot;
        gunPickedAt = DateTime.MinValue;

        ModLog.Info("Wheel: " + Guns.ReplacedGadget + " back in BM2SelectableGadgets[" + replacedIndex + "]; the guns are off the wheel.");
        Refresh(controller);
        UpdateKeyLabel(controller);

        if (equip && held)
        {
            PutAwayGuns(controller, inventory);
        }
    }

    // Arsenal Pack menu "GUNS ACTIVE = NO": the REC gun gets its slot back whatever the guns/REC selection is.
    public static void Deactivate(RPlayerController controller, RInventoryManager inventory)
    {
        RInventoryGadget? gun = Guns.Gun;
        bool held = Guns.IsGun(inventory.CurrentGadget);

        SwapInList(inventory.BM2SelectableGadgets, Guns.ClassName, Guns.ReplacedGadget);
        SwapInList(inventory.PCSelectableGadgets, Guns.ClassName, Guns.ReplacedGadget);
        RestoreReplaced();

        if (gun != null && gun.IsValid)
        {
            gun.bSelectable = false;
            gun.HudSlot = RetiredHudSlot;
        }

        flashSlot = -1;
        replacedFlashSlot = -1;
        gunPickedAt = DateTime.MinValue;

        if (held)
        {
            PutAwayGuns(controller, inventory);
        }

        Refresh(controller);
    }

    private static void PutAwayGuns(RPlayerController controller, RInventoryManager inventory)
    {
        try
        {
            RInventoryGadget? next = null;

            foreach (string className in new[] { Guns.ReplacedGadget, previousGadget, "RBatarangBm" })
            {
                if (className.Length == 0 || className == Guns.ClassName)
                {
                    continue;
                }

                RInventoryGadget? gadget = inventory.GetGadgetByName(new FName(className), true);

                if (gadget != null && gadget.IsValid && gadget.bSelectable)
                {
                    next = gadget;

                    break;
                }
            }

            if (next == null)
            {
                ModLog.Info("Wheel: no gadget to switch to; the guns stay in hand until another gadget is picked.");

                return;
            }

            bool selected = inventory.SetCurrentGadget(next, true);
            int slot = slotClasses.IndexOf(Util.ClassName(next));

            if (slot >= 0 && controller.RadialGadgetSelect is RGFxMovieRadialGadgetSelect radial && radial.IsValid)
            {
                radial.SelectedGadgetIndex = slot;
                Util.TryCall("ShowCurrentGadget", () => radial.ShowCurrentGadget(slot));
            }

            Util.TryCall("GadgetsUpdated", controller.GadgetsUpdated);
            ModLog.Info("Wheel: guns put away, now holding " + CurrentClass() + " (selected=" + selected + ").");
        }
        catch (Exception exception)
        {
            ModLog.Once("putaway-failed", "Wheel: putting the guns away failed: " + Util.Unwrap(exception));
        }
    }

    private static void UpdateKeyLabel(RPlayerController controller)
    {
        try
        {
            int slot = SharedSlot;

            for (int n = 0; n < KeyNames.Length; n++)
            {
                if (slot >= 0 && controller.PCGadgetIndexToWheelIndex(n) == slot)
                {
                    GunsMode.KeyLabel = KeyNames[n];

                    return;
                }
            }
        }
        catch
        {
        }
    }

    public static bool WheelReady(RInventoryManager inventory)
    {
        TArray<FName> names = inventory.BM2SelectableGadgets;

        return names.Count > 0 && (IndexOf(names, Guns.ReplacedGadget) >= 0 || IndexOf(names, Guns.ClassName) >= 0);
    }

    public static void Maintain(RPlayerController controller, RInventoryManager inventory)
    {
        if (!GunsMode.GunsSelected)
        {
            MaintainReplaced(controller, inventory);

            return;
        }

        RetireReplaced(inventory);

        int index = IndexOf(inventory.BM2SelectableGadgets, Guns.ClassName);

        if (index < 0)
        {
            if (IndexOf(inventory.BM2SelectableGadgets, Guns.ReplacedGadget) >= 0)
            {
                ModLog.Info("Wheel: the guns dropped off the wheel list; putting them back.");
                PlaceOnWheel(controller, inventory);
            }

            return;
        }

        RInventoryGadget? gun = Guns.Gun;

        if (gun != null && gun.IsValid && gun.HudSlot != index)
        {
            ModLog.Info("Wheel: the guns' wheel slot was " + gun.HudSlot + "; setting it to " + index + ".");
            gun.HudSlot = (byte)index;
            Refresh(controller);

            return;
        }

        if (slotClasses.Contains(Guns.ReplacedGadget) || (slotClasses.Count > 0 && !slotClasses.Contains(Guns.ClassName)))
        {
            ModLog.Info("Wheel: the wheel still shows " + Guns.ReplacedGadget + "; sending the guns to it again.");
            Refresh(controller);
        }
    }

    private static void MaintainReplaced(RPlayerController controller, RInventoryManager inventory)
    {
        RInventoryGadget? gun = Guns.Gun;

        if (IndexOf(inventory.BM2SelectableGadgets, Guns.ClassName) >= 0 || IndexOf(inventory.PCSelectableGadgets, Guns.ClassName) >= 0 ||
            (retiredGadget != null && retiredGadget.IsValid))
        {
            ModLog.Info("Wheel: the guns are still listed in the " + Guns.ReplacedGadget + " slot; giving it back.");
            ApplyMode(controller, inventory, equip: false);

            return;
        }

        if (gun != null && gun.IsValid && !Guns.IsGun(inventory.CurrentGadget))
        {
            if (gun.bSelectable)
            {
                gun.bSelectable = false;
            }

            if (gun.HudSlot != RetiredHudSlot)
            {
                gun.HudSlot = RetiredHudSlot;
            }
        }

        if (slotClasses.Contains(Guns.ClassName))
        {
            ModLog.Info("Wheel: the wheel still shows the guns; sending " + Guns.ReplacedGadget + " to it again.");
            Refresh(controller);
        }
    }

    private static RInventoryGadget? retiredGadget;
    private static bool replacedWasSelectable;
    private static byte replacedHudSlot;

    private const byte RetiredHudSlot = 200;

    private static void RetireReplaced(RInventoryManager inventory)
    {
        RInventoryGadget? replaced = inventory.GetGadgetByName(new FName(Guns.ReplacedGadget), true);

        if (replaced == null || !replaced.IsValid)
        {
            return;
        }

        if (retiredGadget == null || !retiredGadget.IsValid || retiredGadget.Ptr != replaced.Ptr)
        {
            retiredGadget = replaced;
            replacedWasSelectable = replaced.bSelectable;
            replacedHudSlot = replaced.HudSlot;
            ModLog.Info("Wheel: retiring " + Guns.ReplacedGadget + " (HudSlot " + replaced.HudSlot + ", selectable " + replaced.bSelectable + ").");
        }

        if (replaced.bSelectable)
        {
            replaced.bSelectable = false;
        }

        if (replaced.HudSlot != RetiredHudSlot)
        {
            replaced.HudSlot = RetiredHudSlot;
        }
    }

    public static void RestoreReplaced()
    {
        try
        {
            if (retiredGadget != null && retiredGadget.IsValid)
            {
                retiredGadget.bSelectable = replacedWasSelectable;
                retiredGadget.HudSlot = replacedHudSlot;
            }
        }
        catch
        {
        }

        retiredGadget = null;
    }

    private static int SwapInList(TArray<FName> names, string from, string to)
    {
        int existing = IndexOf(names, to);

        if (existing >= 0)
        {
            return existing;
        }

        int index = IndexOf(names, from);

        if (index >= 0)
        {
            names[index] = new FName(to);
        }

        return index;
    }

    private static int IndexOf(TArray<FName> names, string name)
    {
        for (int index = 0; index < names.Count; index++)
        {
            if (string.Equals(names[index].ToString(), name, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static void Refresh(RPlayerController controller)
    {
        Util.TryCall("GadgetsUpdated", controller.GadgetsUpdated);

        RGFxMovieRadialGadgetSelect? radial = controller.RadialGadgetSelect;

        if (radial != null && radial.IsValid)
        {
            Util.TryCall("SendGadgetsToFlash", radial.SendGadgetsToFlash);
        }
    }

    [Redirect(typeof(RGFxMovieRadialGadgetSelect), nameof(RGFxMovieRadialGadgetSelect.SendGadgetsToFlashPrivate))]
    private static void SendGadgetsToFlashPrivateRedirect(RGFxMovieRadialGadgetSelect self, FString characterAcronym, int maxSlots, FString gadgetList)
    {
        if (!BatmanArsenalPack.Arsenal.Guns)
        {
            self.SendGadgetsToFlashPrivate(new FString(characterAcronym.ToString() ?? ""), maxSlots, new FString(gadgetList.ToString() ?? ""));
            return;
        }

        string list = gadgetList.ToString() ?? "";
        string acronym = characterAcronym.ToString() ?? "";

        try
        {
            string? patched = Patch(list);

            Remember(patched ?? list, maxSlots);
            ModLog.Once("flash:" + (patched ?? list), "Wheel: Flash list for '" + characterAcronym + "' (" + maxSlots + " slots): '" + list + "'" + (patched != null ? " -> '" + patched + "'" : "") + ", guns in slot " + GunSlot + ".");

            if (patched != null)
            {
                self.SendGadgetsToFlashPrivate(new FString(acronym), maxSlots, new FString(patched));
                GunIcons.Apply(self, SharedSlot);

                return;
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("flash-failed", "Wheel: patching the Flash list failed: " + Util.Unwrap(exception));
        }

        self.SendGadgetsToFlashPrivate(new FString(acronym), maxSlots, new FString(list));
        GunIcons.Apply(self, SharedSlot);
    }

    private static string? Patch(string list)
    {
        if (!GunsMode.GunsSelected || Guns.Batman() == null || Guns.Gun == null || !Guns.Gun.IsValid)
        {
            return null;
        }

        string[] entries = list.Split(',');

        if (Array.Exists(entries, entry => entry.StartsWith(Guns.ClassName + "|", StringComparison.Ordinal)))
        {
            return null;
        }

        int replaced = Array.FindIndex(entries, entry => entry.StartsWith(Guns.ReplacedGadget + "|", StringComparison.Ordinal));

        if (replaced < 0)
        {
            return null;
        }

        entries[replaced] = Guns.ClassName + "|100|" + GunIcons.AmmoField();

        return string.Join(",", entries);
    }

    private static void Remember(string list, int maxSlots)
    {
        slotClasses.Clear();
        flashSlot = -1;
        replacedFlashSlot = -1;

        string[] entries = list.Split(',');

        for (int index = 0; index < Math.Min(maxSlots, entries.Length); index++)
        {
            string className = entries[index].Split('|')[0];

            slotClasses.Add(className);

            if (className == Guns.ClassName)
            {
                flashSlot = index;
            }
            else if (className == Guns.ReplacedGadget)
            {
                replacedFlashSlot = index;
            }
        }
    }

    [Redirect(typeof(RGFxMovieRadialGadgetSelect), nameof(RGFxMovieRadialGadgetSelect.ManualSelectGadget))]
    private static void ManualSelectGadgetRedirect(RGFxMovieRadialGadgetSelect self, int gadgetIndex, bool selectedByScroll)
    {
        if (!BatmanArsenalPack.Arsenal.Guns)
        {
            self.ManualSelectGadget(gadgetIndex, selectedByScroll);
            return;
        }

        if (StraySelectAfterGunPick(gadgetIndex, selectedByScroll))
        {
            ModLog.Info("Wheel: ignored a select of slot " + gadgetIndex + " right after the guns were picked.");

            return;
        }

        if (selectedByScroll && scrollFrom >= 0 && GunSlot >= 0 && Guns.Batman() != null && Passes(scrollFrom, gadgetIndex, scrollStep))
        {
            if (LogSelections)
            {
                ModLog.Info("Wheel: scroll " + scrollFrom + " -> " + gadgetIndex + " passes the guns' slot " + GunSlot + "; stopping there.");
            }

            EnsureSelected(self, "mouse wheel");

            return;
        }

        if (gadgetIndex == GunSlot && GunSlot >= 0 && Guns.Batman() != null)
        {
            EnsureSelected(self, selectedByScroll ? "mouse wheel" : "number key");

            return;
        }

        self.ManualSelectGadget(gadgetIndex, selectedByScroll);

        if (LogSelections && Guns.Batman() != null)
        {
            ModLog.Info("Wheel: ManualSelectGadget(" + gadgetIndex + ", scroll=" + selectedByScroll + "), guns slot " + GunSlot + ", now holding " + CurrentClass() + ".");
        }

        if (gadgetIndex == GunSlot && GunSlot >= 0)
        {
            EnsureSelected(self, selectedByScroll ? "mouse wheel" : "number key");
        }
    }

    [Redirect(typeof(RGFxMovieRadialGadgetSelect), nameof(RGFxMovieRadialGadgetSelect.PostCloseGadgetAction))]
    private static void PostCloseGadgetActionRedirect(RGFxMovieRadialGadgetSelect self)
    {
        if (!BatmanArsenalPack.Arsenal.Guns)
        {
            self.PostCloseGadgetAction();
            return;
        }

        int slot = self.SelectedGadgetIndex;

        if (StraySelectAfterGunPick(slot, false))
        {
            ModLog.Info("Wheel: ignored a wheel close on slot " + slot + " right after the guns were picked.");

            return;
        }

        if (slot == GunSlot && GunSlot >= 0 && Guns.Batman() != null)
        {
            MarkGunPick();
        }

        self.PostCloseGadgetAction();

        if (LogSelections && Guns.Batman() != null)
        {
            ModLog.Info("Wheel: closed on slot " + slot + ", guns slot " + GunSlot + ", now holding " + CurrentClass() + ".");
        }

        if (slot == GunSlot && GunSlot >= 0)
        {
            EnsureSelected(self, "gadget wheel");
        }
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.SelectGadget))]
    private static void SelectGadgetRedirect(RPlayerController self, int gadget)
    {
        if (!BatmanArsenalPack.Arsenal.Guns)
        {
            self.SelectGadget(gadget);
            return;
        }

        if (passThrough)
        {
            self.SelectGadget(gadget);

            return;
        }

        if ((ReloadKeyDown() || DateTime.UtcNow - reloadKeyAt < ReloadKeyWindow) && Guns.HeldBy(out RPawnPlayer? holder, out _) && holder != null)
        {
            reloadKeyAt = DateTime.MinValue;
            swallowRelease = true;
            ModLog.Once("reload-key-bound", "Wheel: the reload key also selects gadget " + gadget + " in this keymap; while the guns are held it only reloads.");
            Util.TryCall("manual reload", () => GunsHud.ManualReload(holder));

            return;
        }

        int wheelIndex = -1;

        try
        {
            wheelIndex = Guns.Batman() != null ? self.PCGadgetIndexToWheelIndex(gadget) : -1;
        }
        catch
        {
        }

        if (!GunsMode.GunsSelected && wheelIndex >= 0 && wheelIndex == SharedSlot && Guns.Gun != null && Guns.Gun.IsValid)
        {
            GunsMode.KeyPressed(gadget);

            if (LogSelections)
            {
                ModLog.Info("Wheel: " + Guns.ReplacedGadget + " key pressed; tap = " + Guns.ReplacedGadget + ", hold = switch to the guns.");
            }

            return;
        }

        if (wheelIndex == GunSlot && GunSlot >= 0)
        {
            MarkGunPick();
            GunsMode.KeyPressed(-1);

            if (Guns.HeldBy(out _, out _))
            {
                self.KeyboardQuickGadget = wheelIndex;
                quickfireKeyAt = DateTime.UtcNow;

                if (LogSelections)
                {
                    ModLog.Info("Wheel: guns' key pressed while holding the guns; quickfire on release.");
                }

                return;
            }
        }

        if (wheelIndex != GunSlot || GunSlot < 0)
        {
            string slotClass = wheelIndex >= 0 && wheelIndex < slotClasses.Count ? slotClasses[wheelIndex] : "";

            pendingKeySlot = slotClass.Length > 0 && slotClass != CurrentClass() && slotClass != BatmobileRemote ? wheelIndex : -1;
            lastKeySlot = wheelIndex;
            lastKeyAt = DateTime.UtcNow;
            pendingKeyClass = slotClass;
            self.SelectGadget(gadget);
        }

        try
        {
            if (Guns.Batman() == null)
            {
                return;
            }

            if (LogSelections)
            {
                ModLog.Info("Wheel: SelectGadget(" + gadget + ") -> wheel slot " + wheelIndex + ", guns slot " + GunSlot + ", now holding " + CurrentClass() + ".");
            }

            if (wheelIndex == GunSlot && GunSlot >= 0 && self.RadialGadgetSelect is RGFxMovieRadialGadgetSelect radial && radial.IsValid)
            {
                EnsureSelected(radial, "number key " + (gadget + 1));
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("selectkey-failed", "Wheel: number key handling failed: " + Util.Unwrap(exception));
        }
    }

    private static DateTime quickfireKeyAt = DateTime.MinValue;
    private static int quickfireCalls;
    private static bool passThrough;
    private static bool swallowRelease;
    private static DateTime reloadKeyAt = DateTime.MinValue;
    private static readonly TimeSpan ReloadKeyWindow = TimeSpan.FromSeconds(0.25);

    public static void ReloadKeyPressed() => reloadKeyAt = DateTime.UtcNow;

    private const int ReloadVirtualKey = 0x52;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private static bool ReloadKeyDown()
    {
        try
        {
            return (GetAsyncKeyState(ReloadVirtualKey) & 0x8000) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static int pendingKeySlot = -1;
    private static string pendingKeyClass = "";
    private static DateTime keyCheckAt = DateTime.MinValue;
    private static readonly TimeSpan KeyCheckDelay = TimeSpan.FromSeconds(0.35);

    private const string BatmobileRemote = "RBatmobileRemoteBm";

    public static void ResetKeys()
    {
        pendingKeySlot = -1;
        keyCheckAt = DateTime.MinValue;
    }

    public static void Tick(RPlayerController controller)
    {
        if (keyCheckAt == DateTime.MinValue || DateTime.UtcNow < keyCheckAt)
        {
            return;
        }

        keyCheckAt = DateTime.MinValue;

        int slot = pendingKeySlot;

        pendingKeySlot = -1;

        try
        {
            if (slot < 0 || Guns.Batman() == null || CurrentClass() == pendingKeyClass || controller.RadialGadgetSelect is not RGFxMovieRadialGadgetSelect radial || !radial.IsValid)
            {
                return;
            }

            gunPickedAt = DateTime.MinValue;
            radial.SelectedGadgetIndex = slot;
            radial.PostCloseGadgetAction();

            ModLog.Info("Wheel: the game did not equip " + pendingKeyClass + " from its key; selected it through the wheel (slot " + slot + ").");
        }
        catch (Exception exception)
        {
            ModLog.Once("keyfallback-failed", "Wheel: finishing a key selection failed: " + Util.Unwrap(exception));
        }
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.FireQuickGadget))]
    private static bool FireQuickGadgetRedirect(RPlayerController self, RInventoryGadget quickGadget, bool overrideCombatMove)
    {
        if (!BatmanArsenalPack.Arsenal.Guns)
        {
            return self.FireQuickGadget(quickGadget, overrideCombatMove);
        }

        if (Guns.IsGun(quickGadget))
        {
            quickfireCalls++;
            GunsAim.NotifyQuickfire();
        }
        else if (GunsPad.BlockGameQuickfire("FireQuickGadget", quickGadget))
        {
            return false;
        }

        return self.FireQuickGadget(quickGadget, overrideCombatMove);
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.SelectGadgetReleased))]
    private static void SelectGadgetReleasedRedirect(RPlayerController self)
    {
        if (!BatmanArsenalPack.Arsenal.Guns)
        {
            self.SelectGadgetReleased();
            return;
        }

        if (passThrough)
        {
            self.SelectGadgetReleased();

            return;
        }

        if (swallowRelease)
        {
            swallowRelease = false;

            return;
        }

        bool switched = GunsMode.KeyReleased(out int deferred);

        if (deferred >= 0)
        {
            if (switched)
            {
                return;
            }

            passThrough = true;

            try
            {
                self.SelectGadget(deferred);
                self.SelectGadgetReleased();
            }
            finally
            {
                passThrough = false;
            }

            if (LogSelections)
            {
                ModLog.Info("Wheel: " + Guns.ReplacedGadget + " key tapped; passed to the game (now holding " + CurrentClass() + ").");
            }

            return;
        }

        bool gunQuickfire = !switched && DateTime.UtcNow - quickfireKeyAt < TimeSpan.FromSeconds(2) && Guns.HeldBy(out _, out _);

        if (switched && quickfireKeyAt != DateTime.MinValue)
        {
            self.KeyboardQuickGadget = -1;
        }

        quickfireKeyAt = DateTime.MinValue;
        quickfireCalls = 0;
        self.SelectGadgetReleased();

        if (pendingKeySlot >= 0)
        {
            keyCheckAt = DateTime.UtcNow + KeyCheckDelay;
        }

        if (!gunQuickfire || Guns.Gun == null)
        {
            return;
        }

        bool byGame = quickfireCalls > 0;

        if (!byGame)
        {
            self.KeyboardQuickGadget = -1;
            UCall.Call(Guns.Gun, "TryQuickFire");
            GunsAim.NotifyQuickfire();
        }

        if (LogSelections)
        {
            ModLog.Info("Wheel: quickfire " + (byGame ? "by the game" : "by the mod (TryQuickFire)") + ", guns now " + Guns.Gun.GetStateName() + ".");
        }
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.GadgetSelectLeft))]
    private static void GadgetSelectLeftRedirect(RPlayerController self)
    {
        if (!BatmanArsenalPack.Arsenal.Guns)
        {
            self.GadgetSelectLeft();
            return;
        }

        Scroll(self, -1, self.GadgetSelectLeft);
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.GadgetSelectRight))]
    private static void GadgetSelectRightRedirect(RPlayerController self)
    {
        if (!BatmanArsenalPack.Arsenal.Guns)
        {
            self.GadgetSelectRight();
            return;
        }

        Scroll(self, 1, self.GadgetSelectRight);
    }

    private static int scrollStep;
    private static int scrollFrom = -1;

    private static void Scroll(RPlayerController controller, int step, Action native)
    {
        string before = CurrentClass();

        if (Guns.Batman() != null && StepOffGuns(controller, step))
        {
            return;
        }

        scrollStep = step;
        scrollFrom = slotClasses.IndexOf(before);

        try
        {
            native();
        }
        finally
        {
            scrollStep = 0;
            scrollFrom = -1;
        }

        if (LogSelections && Guns.Batman() != null)
        {
            ModLog.Info("Wheel: scroll " + (step < 0 ? "down" : "up") + " " + before + " -> " + CurrentClass() + ".");
        }
    }

    private static bool StepOffGuns(RPlayerController controller, int step)
    {
        if (GunSlot < 0 || slotClasses.Count == 0 || !Guns.HeldBy(out _, out RInventoryManager? inventory))
        {
            return false;
        }

        RGFxMovieRadialGadgetSelect? radial = controller.RadialGadgetSelect;

        if (radial == null || !radial.IsValid)
        {
            return false;
        }

        for (int tries = 1; tries < slotClasses.Count; tries++)
        {
            int slot = Wrap(GunSlot + step * tries, slotClasses.Count);
            string className = slotClasses[slot];

            if (className.Length == 0)
            {
                continue;
            }

            RInventoryGadget? gadget = inventory!.GetGadgetByName(new FName(className), true);

            if (gadget == null || !gadget.IsValid || !gadget.bSelectable)
            {
                continue;
            }

            radial.ManualSelectGadget(slot, true);

            if (LogSelections)
            {
                ModLog.Info("Wheel: scroll off the guns -> slot " + slot + " (" + className + "), now holding " + CurrentClass() + ".");
            }

            return true;
        }

        return false;
    }

    private static bool Passes(int from, int to, int step)
    {
        int count = slotClasses.Count;

        if (count == 0 || step == 0 || from == to)
        {
            return false;
        }

        for (int slot = Wrap(from + step, count); slot != to; slot = Wrap(slot + step, count))
        {
            if (slot == GunSlot)
            {
                return true;
            }
        }

        return false;
    }

    private static int Wrap(int value, int count) => ((value % count) + count) % count;

    private static DateTime gunPickedAt = DateTime.MinValue;

    private static void MarkGunPick() => gunPickedAt = DateTime.UtcNow;

    private static readonly TimeSpan StrayWindow = TimeSpan.FromSeconds(0.8);
    private static int lastKeySlot = -1;
    private static DateTime lastKeyAt = DateTime.MinValue;

    private static bool StraySelectAfterGunPick(int slot, bool selectedByScroll)
    {
        DateTime now = DateTime.UtcNow;

        if (selectedByScroll || GunSlot < 0 || slot == GunSlot || now - gunPickedAt >= StrayWindow || Guns.Batman() == null)
        {
            return false;
        }

        bool keyForThisSlot = lastKeySlot == slot && lastKeyAt > gunPickedAt;

        return !keyForThisSlot;
    }

    private static string CurrentClass() => Util.ClassName((Guns.Batman()?.InvManager as RInventoryManager)?.CurrentGadget);

    private static void EnsureSelected(RGFxMovieRadialGadgetSelect radial, string source)
    {
        MarkGunPick();

        try
        {
            RPawnPlayer? pawn = Guns.Batman();
            RInventoryManager? inventory = pawn?.InvManager as RInventoryManager;
            RInventoryGadget? gun = Guns.Gun;

            if (pawn == null || inventory == null || !inventory.IsValid || gun == null || !gun.IsValid || Guns.IsGun(inventory.CurrentGadget))
            {
                return;
            }

            previousGadget = CurrentClass();

            bool selected = inventory.SetCurrentGadget(gun, true);

            radial.SelectedGadgetIndex = GunSlot;
            Util.TryCall("ShowCurrentGadget", () => radial.ShowCurrentGadget(GunSlot));
            (pawn.Controller as RPlayerController)?.GadgetsUpdated();

            ModLog.Info("Wheel: guns selected from the " + source + " (slot " + GunSlot + "), selected=" + selected + ".");
        }
        catch (Exception exception)
        {
            ModLog.Once("select-failed", "Wheel: selecting the guns failed: " + Util.Unwrap(exception));
        }
    }
}
