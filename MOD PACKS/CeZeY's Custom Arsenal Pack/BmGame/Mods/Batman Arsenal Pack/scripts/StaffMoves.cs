#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace BatmanRobinStaff;

internal static unsafe class StaffMoves
{
    private struct Header
    {
        public IntPtr Data;
        public int Count;
        public int Max;
    }

    private sealed class Slot
    {
        public string Name = "";
        public int Offset = -1;
        public Header Robin;
        public Header Batman;
        public string Value = "";
        public int Expected;
    }

    private static readonly string[] ListNames = { "AutoStrikeList", "CounterList", "SimultaneousCounterList", "TakedownList" };
    private const string RobinCombatSet = "Anim_Robin_Combat.Code.RB_Combat";
    private const string RobinStrikeSet = "dlc_anim_robin_combat.Code.RB_Attack";
    private const string RobinCombatName = "RB_Combat";
    private const long WeaponConfigCheckMs = 500;

    private static readonly List<Slot> slots = new List<Slot>();
    private static readonly HashSet<long> rootedLists = new HashSet<long>();
    private static readonly Dictionary<string, AnimSet?> sets = new Dictionary<string, AnimSet?>(StringComparer.Ordinal);

    private static bool dataRead;
    private static bool built;
    private static IntPtr activePawn = IntPtr.Zero;
    private static AnimSet? savedCombat, savedStrike;
    private static RPawnPlayerConfig? savedConfig;
    private static long nextWeaponConfigCheck;

    public static bool RobinActive => activePawn != IntPtr.Zero;

    private static Header Read(RPawnPlayer pawn, Slot slot) => *(Header*)(pawn.Ptr + slot.Offset);

    private static void Write(RPawnPlayer pawn, Slot slot, Header header) => *(Header*)(pawn.Ptr + slot.Offset) = header;

    private static void FindOffsets(RPawnPlayer pawn)
    {
        if (slots.Count > 0)
        {
            return;
        }

        foreach (string name in ListNames)
        {
            Slot slot = new Slot { Name = name };

            foreach (Struct level in pawn.Class.EnumerateSupersAndSelf())
            {
                foreach (Field field in level.EnumerateFields())
                {
                    if (field is Property property && field.Name.ToString() == name)
                    {
                        slot.Offset = property.Offset;
                    }
                }
            }

            if (slot.Offset <= 0)
            {
                throw new InvalidOperationException("Batman has no " + name + " list");
            }

            slots.Add(slot);
        }
    }

    private static void ReadData()
    {
        if (dataRead)
        {
            return;
        }

        string path = Path.Combine(StaffConfig.Dir, "RobinMoves.txt");
        Dictionary<string, List<string>> lists = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;

        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                current = new List<string>();
                lists[line.Substring(1, line.Length - 2)] = current;
            }
            else if (line.StartsWith("(") && current != null)
            {
                current.Add(line);
            }
        }

        Regex setRef = new Regex("AnimSet'([^']+)'", RegexOptions.CultureInvariant);
        int dropped = 0;

        foreach (Slot slot in slots)
        {
            StringBuilder value = new StringBuilder("(");
            int kept = 0;

            foreach (string entry in lists.TryGetValue(slot.Name, out List<string>? entries) ? entries : new List<string>())
            {
                bool ok = true;

                foreach (Match match in setRef.Matches(entry))
                {
                    if (FindSet(match.Groups[1].Value) == null)
                    {
                        ok = false;
                    }
                }

                if (!ok)
                {
                    dropped++;

                    continue;
                }

                value.Append(kept++ == 0 ? "" : ",").Append(entry);
            }

            slot.Value = value.Append(')').ToString();
            slot.Expected = kept;
            ModLog.Info("Moves: Robin's " + slot.Name + " has " + kept + " moves.");
        }

        if (dropped > 0)
        {
            ModLog.Warn("Moves: " + dropped + " of Robin's moves were left out because their animation set is not loaded.");
        }

        dataRead = true;
    }

    private static AnimSet? FindSet(string path)
    {
        if (!sets.TryGetValue(path, out AnimSet? set))
        {
            set = Game.FindObject<AnimSet>(path);
            set?.AddToRoot();
            sets[path] = set;

            if (set == null)
            {
                ModLog.Warn("Moves: animation set " + path + " is not loaded.");
            }
        }

        return set;
    }

    private static string EnginePath(GameObject target)
    {
        List<GameObject> chain = new List<GameObject>();

        for (GameObject? o = target; o != null && o.IsValid; o = o.Outer)
        {
            chain.Insert(0, o);
        }

        StringBuilder path = new StringBuilder(chain[0].Name.ToString());

        for (int i = 1; i < chain.Count; i++)
        {
            bool colon = chain[i - 1] is not Package && i >= 2 && chain[i - 2] is Package;

            path.Append(colon ? ':' : '.').Append(chain[i].Name.ToString());
        }

        return path.ToString();
    }

    private static bool Build(RPlayerController controller, RPawnPlayer pawn)
    {
        if (built)
        {
            return true;
        }

        FindOffsets(pawn);
        ReadData();

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        string target = EnginePath(pawn);
        bool ok = true;

        foreach (Slot slot in slots)
        {
            Header batman = Read(pawn, slot);

            try
            {
                Write(pawn, slot, default);
                controller.ConsoleCommand(new FString("set " + target + " " + slot.Name + " " + slot.Value), false);
                slot.Robin = Read(pawn, slot);
            }
            finally
            {
                Write(pawn, slot, batman);
            }

            if (slot.Robin.Count != slot.Expected)
            {
                ModLog.Warn("Moves: importing Robin's " + slot.Name + " gave " + slot.Robin.Count + " of " + slot.Expected + " moves.");
            }

            if (slot.Robin.Count <= 0)
            {
                ok = false;
            }
        }

        built = ok;
        ModLog.Info("Moves: Robin's lists built in " + watch.ElapsedMilliseconds + " ms: " + string.Join(", ", slots.Select(s => s.Name + " " + s.Robin.Count)) + ".");

        return ok;
    }

    private static void RootBatmanSets(RPawnPlayer pawn)
    {
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        HashSet<IntPtr> seen = new HashSet<IntPtr>();

        void Keep(AnimSet? set)
        {
            if (set != null && seen.Add(set.Ptr))
            {
                set.AddToRoot();
            }
        }

        foreach (Slot slot in slots)
        {
            Header batman = Read(pawn, slot);

            if (batman.Data == IntPtr.Zero || !rootedLists.Add(batman.Data.ToInt64() ^ ((long)batman.Count << 40)))
            {
                continue;
            }

            switch (slot.Name)
            {
                case "AutoStrikeList":
                    foreach (var entry in pawn.AutoStrikeList)
                    {
                        Keep(entry.StrikeAnimset);
                        Keep(entry.HitReactionAnimSet);
                    }

                    break;
                case "CounterList":
                    foreach (var entry in pawn.CounterList)
                    {
                        Keep(entry.CounterAnimset);
                        Keep(entry.CounterTargetAnimset);
                    }

                    break;
                case "SimultaneousCounterList":
                    foreach (var entry in pawn.SimultaneousCounterList)
                    {
                        Keep(entry.CounterAnimset);
                        Keep(entry.CounterTargetAnimset);
                    }

                    break;
                case "TakedownList":
                    foreach (var entry in pawn.TakedownList)
                    {
                        Keep(entry.TakedownAnimset);
                        Keep(entry.TakedownTargetAnimset);
                    }

                    break;
            }
        }

        if (seen.Count > 0)
        {
            ModLog.Info("Moves: kept Batman's " + seen.Count + " animation sets loaded while the staff is out (" + watch.ElapsedMilliseconds + " ms).");
        }
    }

    public static bool Apply(RPlayerController controller, RPawnPlayer pawn)
    {
        if (RobinActive)
        {
            return true;
        }

        if (!Build(controller, pawn))
        {
            return false;
        }

        RootBatmanSets(pawn);

        foreach (Slot slot in slots)
        {
            slot.Batman = Read(pawn, slot);
            Write(pawn, slot, slot.Robin);
        }

        activePawn = pawn.Ptr;

        RPawnPlayerConfig? config = pawn.AnimationConfig;

        if (config != null && config.IsValid)
        {
            savedConfig = config;
            savedCombat = config.CombatAnimset;
            savedStrike = config.StrikeAnimset;
            savedCombat?.AddToRoot();
            savedStrike?.AddToRoot();

            AnimSet? combat = FindSet(RobinCombatSet);
            AnimSet? strike = FindSet(RobinStrikeSet);

            if (combat != null)
            {
                config.CombatAnimset = combat;
            }

            if (strike != null)
            {
                config.StrikeAnimset = strike;
            }
        }

        ModLog.Info("Moves: Batman now fights with Robin's moves (" + string.Join(", ", slots.Select(s => s.Name + " " + s.Batman.Count + " -> " + s.Robin.Count)) + ").");

        return true;
    }

    public static void Restore(RPawnPlayer pawn)
    {
        if (!RobinActive)
        {
            return;
        }

        if (activePawn != pawn.Ptr)
        {
            Adopt(pawn);

            return;
        }

        foreach (Slot slot in slots)
        {
            Write(pawn, slot, slot.Batman);
        }

        RestoreConfig();
        activePawn = IntPtr.Zero;
        ModLog.Info("Moves: Batman's own moves are back (" + string.Join(", ", slots.Select(s => s.Name + " " + s.Batman.Count)) + ").");
    }

    private static void RestoreConfig()
    {
        RPawnPlayerConfig? config = savedConfig;

        if (config != null && config.IsValid)
        {
            if (savedCombat != null)
            {
                config.CombatAnimset = savedCombat;
            }

            if (savedStrike != null)
            {
                config.StrikeAnimset = savedStrike;
            }
        }

        savedConfig = null;
    }

    public static void RepairWeaponConfig(RPawnPlayer pawn)
    {
        long now = Environment.TickCount64;

        if (now < nextWeaponConfigCheck)
        {
            return;
        }

        nextWeaponConfigCheck = now + WeaponConfigCheckMs;

        AnimSet? batmanCombat = RobinActive ? savedCombat : pawn.AnimationConfig?.CombatAnimset;

        if (batmanCombat == null || !batmanCombat.IsValid || batmanCombat.Name.ToString() == RobinCombatName)
        {
            return;
        }

        int replaced = 0;
        HashSet<IntPtr> seen = new HashSet<IntPtr>();
        Stack<RWeaponConfig?> pending = new Stack<RWeaponConfig?>();

        pending.Push(pawn.UnarmedWeaponConfig);
        pending.Push(pawn.OverrideUnarmedWeaponConfig);

        while (pending.Count > 0)
        {
            RWeaponConfig? config = pending.Pop();

            if (config == null || !config.IsValid || !seen.Add(config.Ptr))
            {
                continue;
            }

            TArray<AnimSet> animSets = config.AnimSets;

            for (int index = 0; index < animSets.Count; index++)
            {
                AnimSet? set = animSets[index];

                if (set != null && set.IsValid && set.Name.ToString() == RobinCombatName)
                {
                    animSets[index] = batmanCombat;
                    replaced++;
                }
            }

            TArray<RWeaponConfig> extends = config.Extends;

            for (int index = 0; index < extends.Count; index++)
            {
                pending.Push(extends[index]);
            }
        }

        if (replaced > 0)
        {
            ModLog.Info("Moves: Batman's weapon config was built while the staff was out; put " + batmanCombat.Name + " back in " + replaced + " slots so his evades have their animations.");
        }
    }

    public static void Heal(RPawnPlayer pawn)
    {
        if (!RobinActive || activePawn != pawn.Ptr)
        {
            return;
        }

        bool changed = false;

        foreach (Slot slot in slots)
        {
            Header now = Read(pawn, slot);

            changed |= now.Data != slot.Robin.Data || now.Count != slot.Robin.Count;
        }

        if (!changed)
        {
            return;
        }

        foreach (Slot slot in slots)
        {
            Header now = Read(pawn, slot);

            if (now.Data == slot.Robin.Data && now.Count == slot.Robin.Count)
            {
                Write(pawn, slot, slot.Batman);
            }
        }

        savedConfig = null;
        built = false;
        activePawn = IntPtr.Zero;
        ModLog.Info("Moves: Batman's lists were rewritten from outside (moveset injector); taking them as his moveset and rebuilding Robin's.");
    }

    public static void Adopt(RPawnPlayer pawn)
    {
        if (RobinActive && activePawn != pawn.Ptr)
        {
            RestoreConfig();
            built = false;
            activePawn = IntPtr.Zero;
        }
    }

    public static string Report(RPawnPlayer pawn)
    {
        if (slots.Count == 0)
        {
            return "moves not touched yet";
        }

        return (RobinActive ? "ROBIN" : "BATMAN") + " moves, " + string.Join(", ", slots.Select(s => s.Name + " " + Read(pawn, s).Count));
    }
}
