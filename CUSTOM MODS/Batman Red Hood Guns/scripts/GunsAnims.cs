#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace BatmanRedHoodGuns;

internal static class GunsAnims
{
    private const string RedHoodAnimConfig = "WBGMDLC_Player_RedHood_Anim_Config.RedHood_Anim_Config";
    private const string RedHoodGadgetPoses = "PoseConfig_RhGadgets";
    private const string GunPoseConfigName = "PoseConfig_BmRedHoodGuns";

    public static readonly string[] BorrowedSets =
    {
        "Code.RH_HandGun",
        "Code.RH_TG_Beatdown",
    };

    private static readonly string[] RetiredSets = { "Code.RH_Gadget_Turns", "Code.RH_Attack_Far" };

    private static IntPtr preparedPawn = IntPtr.Zero;
    private static readonly HashSet<string> sampledStances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, AnimSet>? loadedAnimIndex;
    private static string lastSample = "";

    public static void Reset()
    {
        preparedPawn = IntPtr.Zero;
        sampledStances.Clear();
        loadedAnimIndex = null;
        lastSample = "";
    }

    public static void Install(RPawnPlayer pawn, RInventoryGadget gun)
    {
        try
        {
            preparedPawn = pawn.Ptr;

            AppendPoseConfigs(pawn);
            AddSets(pawn, FindSetsByName(BorrowedSets), "install");

            FName pose = PrimedPose(gun);
            FName camera = GadgetCamera(gun);

            LogPropBones(pawn);

            ModLog.Info("Anims: the guns' primed pose is '" + pose + "', gadget camera '" + camera + "'.");
            BorrowForStances(pawn, new[] { pose.ToString() });
        }
        catch (Exception exception)
        {
            ModLog.Warn("Anims: install failed: " + Util.Unwrap(exception));
        }
    }

    public static void Maintain(RPawnPlayer pawn, RInventoryGadget gun)
    {
        if (pawn.Ptr != preparedPawn)
        {
            Install(pawn, gun);

            return;
        }

        List<AnimSet> sets = FindSetsByName(BorrowedSets);
        RWeaponConfig? config = pawn.UnarmedWeaponConfig;

        if (config != null && config.IsValid && sets.Count > 0 && !Util.ContainsSet(config.AnimSets, sets[0]))
        {
            AppendPoseConfigs(pawn);
            AddSets(pawn, sets, "weapon config changed");
        }
    }

    public static void Sample(RPawnPlayer pawn, RInventoryGadget gun)
    {
        try
        {
            List<string> stances = new List<string> { pawn.DesiredPoseForReplication.ToString() };
            TArray<FName> idleStances = pawn.PoseIdleStances;

            for (int index = 0; index < idleStances.Count; index++)
            {
                stances.Add(idleStances[index].ToString());
            }

            string sample = gun.GadgetState + " | " + string.Join(", ", stances);

            if (sample == lastSample)
            {
                return;
            }

            lastSample = sample;

            List<string> gunStances = stances.Where(IsGunStance).ToList();

            if (gun.GadgetState == RInventoryGadget.GadgetEquipState.GS_AttachetToHand && gunStances.Count > 0)
            {
                ModLog.Info("Anims: pose sample " + sample + ".");
                BorrowForStances(pawn, gunStances);
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("sample-failed", "Anims: sampling failed: " + Util.Unwrap(exception));
        }
    }

    [Redirect(typeof(RInventoryGadget), nameof(RInventoryGadget.UseGadgetPose))]
    private static bool UseGadgetPoseRedirect(RInventoryGadget self)
    {
        bool original = self.UseGadgetPose();

        if (original || !Guns.IsGun(self) || preparedPawn == IntPtr.Zero)
        {
            return original;
        }

        if (self.Owner is RPawnPlayer owner && owner.IsValid && owner.Ptr == preparedPawn)
        {
            ModLog.Once("usepose", "Anims: the guns said UseGadgetPose=false for Batman; forcing the gun pose on.");

            return true;
        }

        return original;
    }

    private static bool IsGunStance(string stance) =>
        stance.IndexOf("Handgun", StringComparison.OrdinalIgnoreCase) >= 0 || stance.IndexOf("Ricochet", StringComparison.OrdinalIgnoreCase) >= 0;

    public static FName PrimedPose(RInventoryGadget gun)
    {
        FName result = FName.None;

        UCall.Call(gun, "GetPrimedPose", read: frame =>
        {
            if (frame.Return is Property ret)
            {
                result = BmSDK.Framework.MarshalUtil.ToManaged<FName>(frame.Buffer + ret.Offset);
            }
        });

        return result;
    }

    public static FName GadgetCamera(RInventoryGadget gun)
    {
        FName result = FName.None;

        UCall.Call(gun, "GetGadgetCamera", read: frame =>
        {
            if (frame.Return is Property ret)
            {
                result = BmSDK.Framework.MarshalUtil.ToManaged<FName>(frame.Buffer + ret.Offset);
            }
        });

        return result;
    }

    private static void LogPropBones(RPawnPlayer pawn)
    {
        SkeletalMeshComponent? mesh = pawn.Mesh;
        AnimSet? set = FindSetsByName(new[] { "Code.RH_HandGun" }).FirstOrDefault();

        if (mesh == null || !mesh.IsValid || set == null)
        {
            return;
        }

        TArray<FName> tracks = set.TrackBoneNames;
        List<string> found = new List<string>();

        for (int index = 0; index < tracks.Count; index++)
        {
            string bone = tracks[index].ToString();

            if (System.Text.RegularExpressions.Regex.IsMatch(bone, "gun|dummy|hand|prop|weapon|holster", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                found.Add(bone + (mesh.MatchRefBone(tracks[index]) >= 0 ? "" : "(missing)"));
            }
        }

        ModLog.Once("propbones", "Anims: RH_HandGun prop/hand bones: " + string.Join(", ", found) + ".");
    }

    private static void AppendPoseConfigs(RPawnPlayer pawn)
    {
        RPawnPlayerConfig? batman = pawn.AnimationConfig;
        RPoseConfig? gunPoses = GunPoseConfig();

        if (batman == null || !batman.IsValid || gunPoses == null)
        {
            ModLog.Warn("Anims: missing anim config (Batman " + (batman != null) + ", gun poses " + (gunPoses != null) + ").");

            return;
        }

        List<string> changes = new List<string>();

        PatchPoseList(batman.PoseConfigs, gunPoses, batman.Name.ToString(), changes);

        foreach (RWeaponConfig weaponConfig in PoseWeaponConfigs(pawn))
        {
            PatchPoseList(weaponConfig.PoseConfigs, gunPoses, weaponConfig.Name.ToString(), changes);
        }

        if (changes.Count > 0)
        {
            ModLog.Info("Anims: pose configs: " + string.Join(", ", changes) + ".");
        }
    }

    private static void PatchPoseList(TArray<RPoseConfig> list, RPoseConfig gunPoses, string owner, List<string> changes)
    {
        bool present = false;

        for (int index = list.Count - 1; index >= 0; index--)
        {
            RPoseConfig? existing = list[index];

            if (existing == null || !existing.IsValid)
            {
                continue;
            }

            if (existing.Ptr == gunPoses.Ptr)
            {
                present = true;
            }
            else if (existing.Name.ToString() == RedHoodGadgetPoses && IsRedHoodObject(existing))
            {
                list.RemoveAt(index);
                changes.Add("removed " + RedHoodGadgetPoses + " from " + owner);
            }
        }

        if (!present)
        {
            list.Insert(list.Count, gunPoses);
            changes.Add("added " + GunPoseConfigName + " to " + owner);
        }
    }

    private static bool IsRedHoodObject(GameObject gameObject) => gameObject.GetPathName().IndexOf("RedHood", StringComparison.OrdinalIgnoreCase) >= 0;

    private static RPoseConfig? gunPoseConfig;

    private static RPoseConfig? GunPoseConfig()
    {
        if (gunPoseConfig != null && gunPoseConfig.IsValid)
        {
            return gunPoseConfig;
        }

        RPawnPlayerConfig? redHood = Game.FindObject<RPawnPlayerConfig>(RedHoodAnimConfig);
        RPoseConfig? source = null;

        if (redHood != null && redHood.IsValid)
        {
            TArray<RPoseConfig> configs = redHood.PoseConfigs;

            for (int index = 0; index < configs.Count; index++)
            {
                if (configs[index] != null && configs[index].IsValid && configs[index].Name.ToString() == RedHoodGadgetPoses)
                {
                    source = configs[index];
                }
            }
        }

        if (source == null || source.Outer == null)
        {
            return null;
        }

        RPoseConfig copy = Game.FindObject<RPoseConfig>(source.Outer.Name + "." + GunPoseConfigName) ?? new RPoseConfig(source.Outer, GunPoseConfigName, 0, source);

        int poses = Compact(copy.Poses, pose => IsGunStance(pose.MovementStance.ToString()) || IsGunStance(pose.WeaponStance.ToString()) || IsGunStance(pose.IdleStance.ToString()));
        int transitions = Compact(copy.Transitions, transition =>
            IsGunStance(transition.MovementStanceA.ToString()) || IsGunStance(transition.WeaponStanceA.ToString()) || IsGunStance(transition.IdleStanceA.ToString()) ||
            IsGunStance(transition.MovementStanceB.ToString()) || IsGunStance(transition.WeaponStanceB.ToString()) || IsGunStance(transition.IdleStanceB.ToString()));

        copy.AddToRoot();
        gunPoseConfig = copy;

        ModLog.Info("Anims: built " + GunPoseConfigName + " from " + RedHoodGadgetPoses + ": " + poses + " gun poses, " + transitions + " transitions.");

        return copy;
    }

    private static unsafe int Compact<T>(TArray<T> array, Func<T, bool> keep)
    {
        int count = array.Count;
        bool[] keeps = new bool[count];

        for (int index = 0; index < count; index++)
        {
            keeps[index] = keep(array[index]);
        }

        byte* data = *(byte**)array.Ptr;
        int stride = array.Stride;
        int kept = 0;

        for (int index = 0; index < count; index++)
        {
            if (!keeps[index])
            {
                continue;
            }

            if (kept != index)
            {
                Buffer.MemoryCopy(data + index * stride, data + kept * stride, stride, stride);
            }

            kept++;
        }

        *(int*)((byte*)array.Ptr + IntPtr.Size) = kept;

        return kept;
    }

    private static List<AnimSet> FindSetsByName(IEnumerable<string> names)
    {
        List<AnimSet> found = new List<AnimSet>();

        foreach (AnimSet set in Game.FindObjects<AnimSet>())
        {
            if (set != null && set.IsValid && names.Contains(Util.FullName(set)) && set.GetPathName().IndexOf("RedHood", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                found.Add(set);
            }
        }

        return found;
    }

    private static float TrackMatchRatio(SkeletalMeshComponent mesh, AnimSet set)
    {
        TArray<FName> tracks = set.TrackBoneNames;
        int matched = 0;

        for (int index = 0; index < tracks.Count; index++)
        {
            if (mesh.MatchRefBone(tracks[index]) >= 0)
            {
                matched++;
            }
        }

        return tracks.Count == 0 ? 0f : matched / (float)tracks.Count;
    }

    private static List<RWeaponConfig> PoseWeaponConfigs(RPawnPlayer pawn)
    {
        List<RWeaponConfig> configs = new List<RWeaponConfig>();
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

            configs.Add(config);

            TArray<RWeaponConfig> extends = config.Extends;

            for (int index = 0; index < extends.Count; index++)
            {
                pending.Push(extends[index]);
            }
        }

        return configs;
    }

    private static void AddSets(RPawnPlayer pawn, List<AnimSet> sets, string reason)
    {
        SkeletalMeshComponent? mesh = pawn.Mesh;
        RPawnPlayerConfig? config = pawn.AnimationConfig;

        if (mesh == null || !mesh.IsValid || config == null || !config.IsValid || sets.Count == 0)
        {
            return;
        }

        List<RWeaponConfig> weaponConfigs = PoseWeaponConfigs(pawn);
        List<string> added = new List<string>();

        RemoveRetiredSets(mesh.AnimSets, added);
        RemoveRetiredSets(config.GadgetAnimSets, added);

        foreach (RWeaponConfig weaponConfig in weaponConfigs)
        {
            RemoveRetiredSets(weaponConfig.AnimSets, added);
        }

        foreach (AnimSet set in sets)
        {
            float ratio = TrackMatchRatio(mesh, set);

            if (ratio < 0.5f)
            {
                ModLog.Once("skel:" + Util.FullName(set), "Anims: skipping " + Util.FullName(set) + ", only " + (ratio * 100).ToString("0") + "% of its tracks are on Batman's skeleton.");

                continue;
            }

            if (!Util.ContainsSet(mesh.AnimSets, set)) mesh.AnimSets.Insert(mesh.AnimSets.Count, set);

            foreach (RWeaponConfig weaponConfig in weaponConfigs)
            {
                if (!Util.ContainsSet(weaponConfig.AnimSets, set)) weaponConfig.AnimSets.Insert(weaponConfig.AnimSets.Count, set);
            }

            set.AddToRoot();
            added.Add("added " + Util.FullName(set) + " (" + (ratio * 100).ToString("0") + "% tracks)");
        }

        Util.TryCall("UpdateAnimations", mesh.UpdateAnimations);

        ModLog.Info("Anims (" + reason + "): " + string.Join(", ", added) + " on Batman's mesh and weapon configs (" + string.Join(", ", weaponConfigs.Select(c => c.Name.ToString())) + ").");
    }

    private static void RemoveRetiredSets(TArray<AnimSet> sets, List<string> log)
    {
        for (int index = sets.Count - 1; index >= 0; index--)
        {
            AnimSet? set = sets[index];

            if (set != null && set.IsValid && RetiredSets.Contains(Util.FullName(set)) && IsRedHoodObject(set))
            {
                sets.RemoveAt(index);
                log.Add("removed " + Util.FullName(set));
            }
        }
    }

    private static bool HasAnimInConfig(RPawnPlayer pawn, string anim)
    {
        FName name = new FName(anim);

        foreach (RWeaponConfig weaponConfig in PoseWeaponConfigs(pawn))
        {
            TArray<AnimSet> sets = weaponConfig.AnimSets;

            for (int index = 0; index < sets.Count; index++)
            {
                AnimSequence? sequence = sets[index] != null && sets[index].IsValid ? sets[index].FindAnimSequence(name) : null;

                if (sequence != null && sequence.IsValid)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static AnimSet? FindLoadedSetFor(SkeletalMeshComponent mesh, string anim)
    {
        if (loadedAnimIndex == null)
        {
            loadedAnimIndex = new Dictionary<string, AnimSet>(StringComparer.OrdinalIgnoreCase);

            foreach (AnimSet set in Game.FindObjects<AnimSet>().OrderBy(set => set.GetPathName().IndexOf("RedHood", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1))
            {
                if (set == null || !set.IsValid || TrackMatchRatio(mesh, set) < 0.5f)
                {
                    continue;
                }

                TArray<AnimSequence> sequences = set.Sequences;

                for (int index = 0; index < sequences.Count; index++)
                {
                    AnimSequence? sequence = sequences[index];

                    if (sequence != null && sequence.IsValid)
                    {
                        loadedAnimIndex.TryAdd(sequence.SequenceName.ToString(), set);
                    }
                }
            }

            ModLog.Info("Anims: indexed " + loadedAnimIndex.Count + " loaded animations on Batman's skeleton.");
        }

        return loadedAnimIndex.TryGetValue(anim, out AnimSet? found) && found.IsValid ? found : null;
    }

    private static void BorrowForStances(RPawnPlayer pawn, IEnumerable<string> stances)
    {
        SkeletalMeshComponent? mesh = pawn.Mesh;
        RPawnPlayerConfig? config = pawn.AnimationConfig;

        if (mesh == null || !mesh.IsValid || config == null || !config.IsValid)
        {
            return;
        }

        List<string> fresh = stances.Where(stance => !Util.IsNone(new FName(stance)) && sampledStances.Add(stance)).ToList();

        if (fresh.Count == 0)
        {
            return;
        }

        bool Wanted(FName a, FName b, FName c) =>
            fresh.Any(stance => new[] { a, b, c }.Any(name => !Util.IsNone(name) && string.Equals(name.ToString(), stance, StringComparison.OrdinalIgnoreCase)));

        List<string> anims = new List<string>();
        int entries = 0;
        TArray<RPoseConfig> poseConfigs = config.PoseConfigs;

        for (int index = 0; index < poseConfigs.Count; index++)
        {
            RPoseConfig? poseConfig = poseConfigs[index];

            if (poseConfig == null || !poseConfig.IsValid)
            {
                continue;
            }

            TArray<RPoseConfig.FPose> poses = poseConfig.Poses;

            for (int poseIndex = 0; poseIndex < poses.Count; poseIndex++)
            {
                RPoseConfig.FPose pose = poses[poseIndex];

                if (Wanted(pose.MovementStance, pose.WeaponStance, pose.IdleStance))
                {
                    ModLog.Once("entry:" + poseConfig.Name + pose.MovementStance + pose.WeaponStance + pose.IdleStance,
                        "Anims: pose entry " + poseConfig.Name + " movement=" + pose.MovementStance + " weapon=" + pose.WeaponStance + " idle=" + pose.IdleStance +
                        " anims=" + pose.Idle.FullBodyAnim + "/" + pose.Idle.UpperBodyAnim + ".");
                    entries++;
                    AddPoseAnims(anims, pose);
                }
            }

            TArray<RPoseConfig.FTransition> transitions = poseConfig.Transitions;

            for (int transitionIndex = 0; transitionIndex < transitions.Count; transitionIndex++)
            {
                RPoseConfig.FTransition transition = transitions[transitionIndex];

                if (Wanted(transition.MovementStanceA, transition.WeaponStanceA, transition.IdleStanceA) ||
                    Wanted(transition.MovementStanceB, transition.WeaponStanceB, transition.IdleStanceB))
                {
                    AddConfig(anims, transition.A2B);
                    AddConfig(anims, transition.B2A);
                }
            }
        }

        List<string> missing = anims.Where(anim => !HasAnimInConfig(pawn, anim)).ToList();
        List<AnimSet> toAdd = new List<AnimSet>();
        List<string> notFound = new List<string>();

        foreach (string anim in missing)
        {
            AnimSet? source = FindLoadedSetFor(mesh, anim);

            if (source == null)
            {
                notFound.Add(anim);
            }
            else if (!toAdd.Any(set => set.Ptr == source.Ptr))
            {
                toAdd.Add(source);
            }
        }

        ModLog.Info(
            "Anims: stances [" + string.Join(", ", fresh) + "] -> " + entries + " pose entries, " + anims.Count + " anims, " + missing.Count + " missing" +
            (notFound.Count > 0 ? ", not in any loaded set: " + string.Join(", ", notFound) : "") + "."
        );

        if (toAdd.Count > 0)
        {
            AddSets(pawn, toAdd, "stances " + string.Join("/", fresh));
        }
    }

    private static void AddPoseAnims(List<string> anims, RPoseConfig.FPose pose)
    {
        AddConfig(anims, pose.Idle);
        AddName(anims, pose.AdditiveIdle.AddAnim);
        AddName(anims, pose.AdditiveIdle.SubtractAnim);

        TArray<RPoseConfig.FOverlayDescription> overlays = pose.Overlays;

        for (int index = 0; index < overlays.Count; index++)
        {
            AddConfig(anims, overlays[index].Anims);
        }

        TArray<RPoseConfig.FAdditiveDescription> additives = pose.AdditiveAnims;

        for (int index = 0; index < additives.Count; index++)
        {
            AddName(anims, additives[index].Add);
            AddName(anims, additives[index].Subtract);
        }

        RPoseConfig.FMovement movement = pose.Movement;
        TArray<RPoseConfig.FMovementAnim> moves = movement.Anims;

        for (int index = 0; index < moves.Count; index++)
        {
            AddConfig(anims, moves[index].Anims);
            AddConfig(anims, moves[index].LeftAnims);
            AddConfig(anims, moves[index].RightAnims);
        }

        AddConfig(anims, movement.Fall);
        AddConfig(anims, movement.Land);
    }

    private static void AddConfig(List<string> output, RAnimConfig.FAnimConfig config)
    {
        AddName(output, config.FullBodyAnim);
        AddName(output, config.UpperBodyAnim);

        try
        {
            RDirectionalAnimConfig? directions = config.Directions;

            if (directions != null && directions.IsValid)
            {
                foreach (PropertyInfo property in typeof(RDirectionalAnimConfig).GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (property.PropertyType == typeof(FName) && property.Name.StartsWith("Directions_", StringComparison.Ordinal))
                    {
                        AddName(output, (FName)property.GetValue(directions)!);
                    }
                }
            }
        }
        catch
        {
        }
    }

    private static void AddName(List<string> output, FName name)
    {
        if (!Util.IsNone(name) && !output.Contains(name.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            output.Add(name.ToString());
        }
    }
}
