#nullable enable

using System;
using System.Collections.Generic;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace BatmanAssaultRifle;

internal static class RifleStance
{
    private static readonly string[] Stances = { "StandingBatarang", "CrouchingBatarang" };
    private static readonly string[] BatStarts = { "Batarang_Stand_Start", "Batarang_Vantage_Start" };
    private const string RifleSetPath = "Anim_Thug_Rifle.Code.TGRifle_Movement";

    private const string BatIdleUpper = "None";
    private const string BatReplace = "Batarang_Stand_Replace";

    private const string RifleIdleUpper = "Stand_Aiming_Idle";
    private const string RifleStart = "None";
    private const string RifleReplace = "Stand_Aiming_Out";

    private static bool applied;
    private static float nextCheck;
    private static IntPtr setsPawn;
    private static AnimSet? rifleSet;

    public static bool Applied => applied;

    public static void Reset()
    {
        setsPawn = IntPtr.Zero;
        nextCheck = 0f;
    }

    public static void Sync(RPawnPlayer? pawn, bool want)
    {
        if (want && pawn != null && pawn.IsValid && setsPawn != pawn.Ptr)
        {
            if (AddSet(pawn))
            {
                setsPawn = pawn.Ptr;
                nextCheck = 0f;
            }
        }

        if (want == applied && Util.Now < nextCheck)
        {
            return;
        }

        nextCheck = Util.Now + 2f;

        if (pawn == null || !pawn.IsValid)
        {
            return;
        }

        int changed = Patch(pawn, want);

        if (want != applied || changed > 0)
        {
            ModLog.Info("Stance: Batarang aim pose " + (want ? "set to the rifle stance" : "restored") + " (" + changed + " entries changed).");
        }

        applied = want;
    }

    public static void Restore()
    {
        if (!applied)
        {
            return;
        }

        try
        {
            if (Rifle.Batman() is RPawnPlayer pawn)
            {
                Patch(pawn, false);
            }
            else
            {
                PatchConfigs(FindGadgetConfigs(), false);
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn("Stance: restoring the Batarang poses failed: " + Util.Unwrap(exception));
        }

        applied = false;
    }

    private static bool AddSet(RPawnPlayer pawn)
    {
        rifleSet ??= Game.FindObject<AnimSet>(RifleSetPath);

        if (rifleSet == null || !rifleSet.IsValid)
        {
            ModLog.Once("stance-noset", "Stance: " + RifleSetPath + " is not loaded; the Batarang aim keeps its own pose.");

            return false;
        }

        rifleSet.AddToRoot();

        SkeletalMeshComponent? mesh = pawn.Mesh;
        int added = 0;

        if (mesh != null && mesh.IsValid && !Contains(mesh.AnimSets, rifleSet))
        {
            mesh.AnimSets.Insert(mesh.AnimSets.Count, rifleSet);
            added++;
        }

        foreach (RWeaponConfig config in WeaponConfigs(pawn))
        {
            if (!Contains(config.AnimSets, rifleSet))
            {
                config.AnimSets.Insert(config.AnimSets.Count, rifleSet);
                added++;
            }
        }

        if (added > 0)
        {
            Util.TryCall("UpdateAnimations", () => mesh?.UpdateAnimations());
            ModLog.Info("Stance: added the rifle thugs' animations at the end of " + added + " anim set list(s).");
        }

        return true;
    }

    private static bool Contains(TArray<AnimSet> sets, AnimSet set)
    {
        for (int index = 0; index < sets.Count; index++)
        {
            if (sets[index] != null && sets[index].Ptr == set.Ptr)
            {
                return true;
            }
        }

        return false;
    }

    private static List<RWeaponConfig> WeaponConfigs(RPawnPlayer pawn)
    {
        var configs = new List<RWeaponConfig>();
        var seen = new HashSet<IntPtr>();
        var pending = new Stack<RWeaponConfig?>();

        pending.Push(pawn.UnarmedWeaponConfig);
        pending.Push(pawn.OverrideUnarmedWeaponConfig);

        while (pending.Count > 0)
        {
            RWeaponConfig? config = pending.Pop();

            if (config == null || !config.IsValid || !seen.Add(config.Ptr))
            {
                continue;
            }

            configs.Add(config);

            TArray<RWeaponConfig> extends = config.Extends;

            for (int index = 0; index < extends.Count; index++)
            {
                pending.Push(extends[index]);
            }
        }

        return configs;
    }

    private static List<RPoseConfig> FindGadgetConfigs(RPawnPlayer? pawn = null)
    {
        var result = new List<RPoseConfig>();
        var seen = new HashSet<IntPtr>();

        void Scan(TArray<RPoseConfig> list)
        {
            for (int index = 0; index < list.Count; index++)
            {
                RPoseConfig? config = list[index];

                if (config != null && config.IsValid && seen.Add(config.Ptr) && HasStance(config))
                {
                    result.Add(config);
                }
            }
        }

        if (pawn != null)
        {
            if (pawn.AnimationConfig is RPawnPlayerConfig animConfig && animConfig.IsValid)
            {
                Scan(animConfig.PoseConfigs);
            }

            foreach (RWeaponConfig config in WeaponConfigs(pawn))
            {
                Scan(config.PoseConfigs);
            }
        }

        return result;
    }

    private static bool HasStance(RPoseConfig config)
    {
        TArray<RPoseConfig.FPose> poses = config.Poses;

        for (int index = 0; index < poses.Count; index++)
        {
            if (Array.IndexOf(Stances, poses[index].MovementStance.ToString()) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static int Patch(RPawnPlayer pawn, bool on) => PatchConfigs(FindGadgetConfigs(pawn), on);

    private static int PatchConfigs(List<RPoseConfig> configs, bool on)
    {
        int changed = 0;

        foreach (RPoseConfig config in configs)
        {
            TArray<RPoseConfig.FPose> poses = config.Poses;

            for (int index = 0; index < poses.Count; index++)
            {
                RPoseConfig.FPose pose = poses[index];

                if (Array.IndexOf(Stances, pose.MovementStance.ToString()) < 0)
                {
                    continue;
                }

                RAnimConfig.FAnimConfig idle = pose.Idle;
                string want = on ? RifleIdleUpper : BatIdleUpper;

                if (idle.UpperBodyAnim.ToString() != want || idle.ModelspaceUpperBody != on)
                {
                    idle.UpperBodyAnim = new FName(want);
                    idle.ModelspaceUpperBody = on;
                    pose.Idle = idle;
                    poses[index] = pose;
                    changed++;
                }
            }

            TArray<RPoseConfig.FTransition> transitions = config.Transitions;

            for (int index = 0; index < transitions.Count; index++)
            {
                RPoseConfig.FTransition transition = transitions[index];
                int stanceIndex = Array.IndexOf(Stances, transition.MovementStanceB.ToString());
                bool toStance = stanceIndex >= 0;
                bool fromStance = transition.MovementStanceA.ToString() == Stances[0] && transition.MovementStanceB.ToString() == "Standing";
                RAnimConfig.FAnimConfig a2b = transition.A2B;
                string upper = a2b.UpperBodyAnim.ToString();
                string? want = null;

                if (toStance && (upper == BatStarts[stanceIndex] || upper == RifleStart) && a2b.FullBodyAnim.ToString() == "None" && transition.MovementStanceA.ToString() == "None")
                {
                    want = on ? RifleStart : BatStarts[stanceIndex];
                }
                else if (fromStance && (upper == BatReplace || upper == RifleReplace))
                {
                    want = on ? RifleReplace : BatReplace;
                }

                if (want == null || upper == want)
                {
                    continue;
                }

                a2b.UpperBodyAnim = new FName(want);
                transition.A2B = a2b;
                transitions[index] = transition;
                changed++;
            }
        }

        return changed;
    }
}
