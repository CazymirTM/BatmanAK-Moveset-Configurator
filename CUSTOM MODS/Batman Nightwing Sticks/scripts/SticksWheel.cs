#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Framework;

namespace BatmanNightwingSticks;

internal static class SticksWheel
{
    private static int flashSlot = -1;

    private static int Slot
    {
        get
        {
            if (flashSlot >= 0)
            {
                return flashSlot;
            }

            RInventoryManager? inventory = Sticks.Batman()?.InvManager as RInventoryManager;

            return inventory != null && inventory.IsValid ? IndexOf(inventory.BM2SelectableGadgets, Sticks.ClassName) : -1;
        }
    }
    public static int SlotIndex => Slot;

    private static readonly List<string> slotClasses = new List<string>();

    private static bool LogSelections = true;

    public static void PlaceOnWheel(RPlayerController controller, RInventoryManager inventory)
    {
        RetireSmoke(inventory);

        int index = SwapInList(inventory.BM2SelectableGadgets);
        int pcIndex = SwapInList(inventory.PCSelectableGadgets);

        ModLog.Info("Wheel: sticks placed in BM2SelectableGadgets[" + index + "] (PC list index " + pcIndex + "), replacing " + Sticks.ReplacedGadget + ".");

        try
        {
            string[] keys = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "=" };

            ModLog.Once("keymap", "Wheel: number key -> wheel slot: " + string.Join(", ", keys.Select((key, n) => key + "->" + controller.PCGadgetIndexToWheelIndex(n))) + ".");
        }
        catch (Exception exception)
        {
            ModLog.Once("keymap-failed", "Wheel: reading the key map failed: " + Util.Unwrap(exception));
        }

        Refresh(controller);
    }

    public static bool WheelReady(RInventoryManager inventory)
    {
        TArray<FName> names = inventory.BM2SelectableGadgets;

        return names.Count > 0 && (IndexOf(names, Sticks.ReplacedGadget) >= 0 || IndexOf(names, Sticks.ClassName) >= 0);
    }

    public static void Maintain(RPlayerController controller, RInventoryManager inventory)
    {
        RetireSmoke(inventory);

        int index = IndexOf(inventory.BM2SelectableGadgets, Sticks.ClassName);

        if (index < 0)
        {
            if (IndexOf(inventory.BM2SelectableGadgets, Sticks.ReplacedGadget) >= 0)
            {
                ModLog.Info("Wheel: the sticks dropped off the wheel list; putting them back.");
                PlaceOnWheel(controller, inventory);
            }

            return;
        }

        RInventoryGadget? sticks = Sticks.Gadget;

        if (sticks != null && sticks.IsValid && sticks.HudSlot != index)
        {
            ModLog.Info("Wheel: the sticks' wheel slot was " + sticks.HudSlot + "; setting it to " + index + ".");
            sticks.HudSlot = (byte)index;
            Refresh(controller);

            return;
        }

        if (slotClasses.Contains(Sticks.ReplacedGadget) || (slotClasses.Count > 0 && !slotClasses.Contains(Sticks.ClassName)))
        {
            ModLog.Info("Wheel: the wheel still shows " + Sticks.ReplacedGadget + "; sending the sticks to it again.");
            Refresh(controller);
        }
    }

    private static RInventoryGadget? retiredSmoke;
    private static bool smokeWasSelectable;
    private static byte smokeHudSlot;

    private const byte RetiredHudSlot = 201;

    private static void RetireSmoke(RInventoryManager inventory)
    {
        RInventoryGadget? smoke = inventory.GetGadgetByName(new FName(Sticks.ReplacedGadget), true);

        if (smoke == null || !smoke.IsValid)
        {
            return;
        }

        if (retiredSmoke == null || !retiredSmoke.IsValid || retiredSmoke.Ptr != smoke.Ptr)
        {
            retiredSmoke = smoke;
            smokeWasSelectable = smoke.bSelectable;
            smokeHudSlot = smoke.HudSlot;
            ModLog.Info("Wheel: retiring " + Sticks.ReplacedGadget + " (HudSlot " + smoke.HudSlot + ", selectable " + smoke.bSelectable + ").");
        }

        if (smoke.bSelectable)
        {
            smoke.bSelectable = false;
        }

        if (smoke.HudSlot != RetiredHudSlot)
        {
            smoke.HudSlot = RetiredHudSlot;
        }

        RInventoryGadget? sticks = Sticks.Gadget;

        if (sticks != null && sticks.IsValid && sticks.HudSlotPC != smoke.HudSlotPC)
        {
            ModLog.Info("Wheel: sticks take the smoke pellet's PC key slot (" + sticks.HudSlotPC + " -> " + smoke.HudSlotPC + ").");
            sticks.HudSlotPC = smoke.HudSlotPC;
        }
    }

    public static void RestoreSmoke()
    {
        try
        {
            if (retiredSmoke != null && retiredSmoke.IsValid && Sticks.Batman() is RPawnPlayer pawn && !pawn.bDeleteMe && retiredSmoke.Owner?.Ptr == pawn.Ptr)
            {
                retiredSmoke.bSelectable = smokeWasSelectable;
                retiredSmoke.HudSlot = smokeHudSlot;
            }
        }
        catch
        {
        }

        retiredSmoke = null;
    }

    private static int SwapInList(TArray<FName> names)
    {
        int existing = IndexOf(names, Sticks.ClassName);

        if (existing >= 0)
        {
            return existing;
        }

        int smoke = IndexOf(names, Sticks.ReplacedGadget);

        if (smoke >= 0)
        {
            names[smoke] = new FName(Sticks.ClassName);
        }

        return smoke;
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
        string list = gadgetList.ToString() ?? "";

        try
        {
            string? patched = Patch(list);

            Remember(patched ?? list, maxSlots);
            ModLog.Once("flash:" + (patched ?? list), "Wheel: Flash list for '" + characterAcronym + "' (" + maxSlots + " slots): '" + list + "'" + (patched != null ? " -> '" + patched + "'" : "") + ", sticks in slot " + Slot + ".");

            if (patched != null)
            {
                self.SendGadgetsToFlashPrivate(characterAcronym, maxSlots, patched);
                SticksIcons.Apply(self, Slot);

                return;
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("flash-failed", "Wheel: patching the Flash list failed: " + Util.Unwrap(exception));
        }

        self.SendGadgetsToFlashPrivate(characterAcronym, maxSlots, gadgetList);
        SticksIcons.Apply(self, Slot);
    }

    private static string? Patch(string list)
    {
        if (Sticks.Batman() == null || Sticks.Gadget == null || !Sticks.Gadget.IsValid)
        {
            return null;
        }

        string[] entries = list.Split(',');

        if (Array.Exists(entries, entry => entry.StartsWith(Sticks.ClassName + "|", StringComparison.Ordinal)))
        {
            return null;
        }

        int smoke = Array.FindIndex(entries, entry => entry.StartsWith(Sticks.ReplacedGadget + "|", StringComparison.Ordinal));

        if (smoke < 0)
        {
            return null;
        }

        entries[smoke] = Sticks.ClassName + entries[smoke].Substring(entries[smoke].IndexOf('|'));

        return string.Join(",", entries);
    }

    private static void Remember(string list, int maxSlots)
    {
        slotClasses.Clear();
        flashSlot = -1;

        string[] entries = list.Split(',');

        for (int index = 0; index < Math.Min(maxSlots, entries.Length); index++)
        {
            string className = entries[index].Split('|')[0];

            slotClasses.Add(className);

            if (className == Sticks.ClassName)
            {
                flashSlot = index;
            }
        }
    }

    [Redirect(typeof(RGFxMovieRadialGadgetSelect), nameof(RGFxMovieRadialGadgetSelect.ManualSelectGadget))]
    private static void ManualSelectGadgetRedirect(RGFxMovieRadialGadgetSelect self, int gadgetIndex, bool selectedByScroll)
    {
        if (StraySelectAfterSticksPick(gadgetIndex, selectedByScroll))
        {
            ModLog.Info("Wheel: ignored a select of slot " + gadgetIndex + " right after the sticks were picked.");

            return;
        }

        if (selectedByScroll && scrollFrom >= 0 && Slot >= 0 && Sticks.Batman() != null && Passes(scrollFrom, gadgetIndex, scrollStep))
        {
            if (LogSelections)
            {
                ModLog.Info("Wheel: scroll " + scrollFrom + " -> " + gadgetIndex + " passes the sticks' slot " + Slot + "; stopping there.");
            }

            EnsureSelected(self, "mouse wheel");

            return;
        }

        if (gadgetIndex == Slot && Slot >= 0 && Sticks.Batman() != null)
        {
            EnsureSelected(self, selectedByScroll ? "mouse wheel" : "number key");

            return;
        }

        self.ManualSelectGadget(gadgetIndex, selectedByScroll);

        if (LogSelections && Sticks.Batman() != null)
        {
            ModLog.Info("Wheel: ManualSelectGadget(" + gadgetIndex + ", scroll=" + selectedByScroll + "), sticks slot " + Slot + ", now holding " + CurrentClass() + ".");
        }

        if (gadgetIndex == Slot && Slot >= 0)
        {
            EnsureSelected(self, selectedByScroll ? "mouse wheel" : "number key");
        }
    }

    [Redirect(typeof(RGFxMovieRadialGadgetSelect), nameof(RGFxMovieRadialGadgetSelect.PostCloseGadgetAction))]
    private static void PostCloseGadgetActionRedirect(RGFxMovieRadialGadgetSelect self)
    {
        int slot = self.SelectedGadgetIndex;

        if (StraySelectAfterSticksPick(slot, false))
        {
            ModLog.Info("Wheel: ignored a wheel close on slot " + slot + " right after the sticks were picked.");

            return;
        }

        if (slot == Slot && Slot >= 0 && Sticks.Batman() != null)
        {
            MarkSticksPick();
        }

        self.PostCloseGadgetAction();

        if (LogSelections && Sticks.Batman() != null)
        {
            ModLog.Info("Wheel: closed on slot " + slot + ", sticks slot " + Slot + ", now holding " + CurrentClass() + ".");
        }

        if (slot == Slot && Slot >= 0)
        {
            EnsureSelected(self, "gadget wheel");
        }
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.SelectGadget))]
    private static void SelectGadgetRedirect(RPlayerController self, int gadget)
    {
        int wheelIndex = -1;

        try
        {
            wheelIndex = Sticks.Batman() != null ? self.PCGadgetIndexToWheelIndex(gadget) : -1;
        }
        catch
        {
        }

        if (wheelIndex == Slot && Slot >= 0)
        {
            MarkSticksPick();
            SticksMode.KeyPressed();

            if (Sticks.HeldBy(out _, out _))
            {
                self.KeyboardQuickGadget = wheelIndex;
                quickfireKeyAt = DateTime.UtcNow;

                if (LogSelections)
                {
                    ModLog.Info("Wheel: sticks' key pressed while holding the sticks; quickfire on release.");
                }

                return;
            }
        }

        if (wheelIndex != Slot || Slot < 0)
        {
            string slotClass = wheelIndex >= 0 && wheelIndex < slotClasses.Count ? slotClasses[wheelIndex] : "";

            pendingKeySlot = slotClass.Length > 0 && slotClass != CurrentClass() ? wheelIndex : -1;
            lastKeySlot = wheelIndex;
            lastKeyAt = DateTime.UtcNow;
            pendingKeyClass = slotClass;
            self.SelectGadget(gadget);
        }

        try
        {
            if (Sticks.Batman() == null)
            {
                return;
            }

            if (LogSelections)
            {
                ModLog.Info("Wheel: SelectGadget(" + gadget + ") -> wheel slot " + wheelIndex + ", sticks slot " + Slot + ", now holding " + CurrentClass() + ".");
            }

            if (wheelIndex == Slot && Slot >= 0 && self.RadialGadgetSelect is RGFxMovieRadialGadgetSelect radial && radial.IsValid)
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

    private static int pendingKeySlot = -1;
    private static string pendingKeyClass = "";
    private static DateTime keyCheckAt = DateTime.MinValue;
    private static readonly TimeSpan KeyCheckDelay = TimeSpan.FromSeconds(0.35);

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
            if (slot < 0 || Sticks.Batman() == null || CurrentClass() == pendingKeyClass || controller.RadialGadgetSelect is not RGFxMovieRadialGadgetSelect radial || !radial.IsValid)
            {
                return;
            }

            sticksPickedAt = DateTime.MinValue;
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
        if (Sticks.IsOurs(quickGadget))
        {
            quickfireCalls++;
        }

        return self.FireQuickGadget(quickGadget, overrideCombatMove);
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.SelectGadgetReleased))]
    private static void SelectGadgetReleasedRedirect(RPlayerController self)
    {
        bool switched = SticksMode.KeyReleased();
        bool sticksQuickfire = !switched && DateTime.UtcNow - quickfireKeyAt < TimeSpan.FromSeconds(2) && Sticks.HeldBy(out _, out _);

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

        RInventoryGadget? active = Sticks.Active;

        if (!sticksQuickfire || active == null || !active.IsValid)
        {
            return;
        }

        bool byGame = quickfireCalls > 0;

        if (!byGame)
        {
            self.KeyboardQuickGadget = -1;
            UCall.Call(active, "TryQuickFire");
        }

        if (LogSelections)
        {
            ModLog.Info("Wheel: quickfire " + (byGame ? "by the game" : "by the mod (TryQuickFire)") + ", " + Util.ClassName(active) + " now " + active.GetStateName() + ".");
        }
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.GadgetSelectLeft))]
    private static void GadgetSelectLeftRedirect(RPlayerController self)
    {
        Scroll(self, -1, self.GadgetSelectLeft);
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.GadgetSelectRight))]
    private static void GadgetSelectRightRedirect(RPlayerController self)
    {
        Scroll(self, 1, self.GadgetSelectRight);
    }

    private static int scrollStep;
    private static int scrollFrom = -1;

    private static void Scroll(RPlayerController controller, int step, Action native)
    {
        string before = CurrentClass();

        if (Sticks.Batman() != null && StepOffSticks(controller, step))
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

        if (LogSelections && Sticks.Batman() != null)
        {
            ModLog.Info("Wheel: scroll " + (step < 0 ? "down" : "up") + " " + before + " -> " + CurrentClass() + ".");
        }
    }

    private static bool StepOffSticks(RPlayerController controller, int step)
    {
        if (Slot < 0 || slotClasses.Count == 0 || !Sticks.HeldBy(out _, out RInventoryManager? inventory))
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
            int slot = Wrap(Slot + step * tries, slotClasses.Count);
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
                ModLog.Info("Wheel: scroll off the sticks -> slot " + slot + " (" + className + "), now holding " + CurrentClass() + ".");
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
            if (slot == Slot)
            {
                return true;
            }
        }

        return false;
    }

    private static int Wrap(int value, int count) => ((value % count) + count) % count;

    private static DateTime sticksPickedAt = DateTime.MinValue;

    private static void MarkSticksPick() => sticksPickedAt = DateTime.UtcNow;

    private static readonly TimeSpan StrayWindow = TimeSpan.FromSeconds(0.8);
    private static int lastKeySlot = -1;
    private static DateTime lastKeyAt = DateTime.MinValue;

    private static bool StraySelectAfterSticksPick(int slot, bool selectedByScroll)
    {
        DateTime now = DateTime.UtcNow;

        if (selectedByScroll || Slot < 0 || slot == Slot || now - sticksPickedAt >= StrayWindow || Sticks.Batman() == null)
        {
            return false;
        }

        bool keyForThisSlot = lastKeySlot == slot && lastKeyAt > sticksPickedAt;

        return !keyForThisSlot;
    }

    private static string CurrentClass() => Util.ClassName((Sticks.Batman()?.InvManager as RInventoryManager)?.CurrentGadget);

    private static void EnsureSelected(RGFxMovieRadialGadgetSelect radial, string source)
    {
        MarkSticksPick();

        try
        {
            RPawnPlayer? pawn = Sticks.Batman();
            RInventoryManager? inventory = pawn?.InvManager as RInventoryManager;
            RInventoryGadget? sticks = Sticks.Active;

            if (pawn == null || inventory == null || !inventory.IsValid || sticks == null || !sticks.IsValid || (inventory.CurrentGadget != null && inventory.CurrentGadget.Ptr == sticks.Ptr))
            {
                return;
            }

            bool selected = inventory.SetCurrentGadget(sticks, true);

            radial.SelectedGadgetIndex = Slot;
            Util.TryCall("ShowCurrentGadget", () => radial.ShowCurrentGadget(Slot));
            (pawn.Controller as RPlayerController)?.GadgetsUpdated();

            ModLog.Info("Wheel: sticks selected from the " + source + " (slot " + Slot + "), selected=" + selected + ".");
        }
        catch (Exception exception)
        {
            ModLog.Once("select-failed", "Wheel: selecting the sticks failed: " + Util.Unwrap(exception));
        }
    }
}
