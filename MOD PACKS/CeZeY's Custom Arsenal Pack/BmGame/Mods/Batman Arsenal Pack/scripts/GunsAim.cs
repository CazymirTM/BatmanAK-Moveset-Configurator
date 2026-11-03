#nullable enable

using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using System.Numerics;
using System.Reflection;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace BatmanRedHoodGuns;

internal static class GunsAim
{
    public static float AimFov = 58f;
    private const float FovBlendTime = 0.25f;

    private static readonly TimeSpan AimHoldTime = TimeSpan.FromSeconds(0.25);

    private static readonly TimeSpan StrikeInterval = TimeSpan.FromSeconds(0.35);
    private static readonly TimeSpan StrikeContact = TimeSpan.FromSeconds(0.11);

    private static readonly TimeSpan StrikeLength = TimeSpan.FromSeconds(0.3);

    private const int MaxQueuedStrikes = 2;

    private const float ShotRange = 4000f;
    private const float ShotMargin = 40f;

    private const float LaunchForce = 900f;
    private const float LaunchLift = 250f;

    private static readonly string[] StrikeAnims = { "Far_Beatdown_01", "Far_Beatdown_02", "Far_Beatdown_03", "Far_Beatdown_04" };

    private static bool Trace = false;

    private static string lastPoseState = "";
    private static bool fovOverridden;
    private static bool wasAiming;
    private static DateTime aimSince = DateTime.MinValue;
    private static DateTime lastStrike = DateTime.MinValue;
    private static int queuedStrikes;
    private static RPawnVillain? pendingTarget;
    private static Vector3 pendingAim;
    private static DateTime pendingHitAt = DateTime.MinValue;
    private static DateTime strikeEndsAt = DateTime.MinValue;
    private static int strikeIndex;
    private static int strikes;
    private static int kills;

    public static void Reset()
    {
        pistolPawn = IntPtr.Zero;
        pistolRight = null;
        pistolLeft = null;
        holsterPawn = IntPtr.Zero;
        holsterRight = null;
        holsterLeft = null;
        lastGunState = "";
        quickfireStart = DateTime.MinValue;
        raisedByMod = false;
        fovOverridden = false;
        wasAiming = false;
        aimSince = DateTime.MinValue;
        strikeIndex = 0;
        queuedStrikes = 0;
        pendingTarget = null;
        pendingHitAt = DateTime.MinValue;
        strikeEndsAt = DateTime.MinValue;
    }

    public static void Tick(RPawnPlayer? pawn)
    {
        RPlayerController? controller = Game.GetPlayerController(0);

        UpdateFov(controller);
        UpdatePistols(pawn, Guns.Gun);

        if (pawn == null || !Guns.HeldBy(out _, out _))
        {
            if (wasAiming)
            {
                wasAiming = false;
                RestoreFov(controller);
            }

            if (pawn != null)
            {
                GunsHud.Tick(pawn, false, DateTime.UtcNow);
            }

            return;
        }

        RInventoryGadget gun = Guns.Gun!;

        GunsAnims.Sample(pawn, gun);

        DateTime now = DateTime.UtcNow;

        TrackQuickfire(gun, now);

        if (!IsAiming(gun))
        {
            aimSince = DateTime.MinValue;
        }
        else if (aimSince == DateTime.MinValue)
        {
            aimSince = now;
        }

        bool aiming = aimSince != DateTime.MinValue && now - aimSince >= AimHoldTime;

        ApplyPendingHit(pawn, now);
        EndStrike(pawn, now);
        GunsHud.Tick(pawn, aiming, now);

        if (!aiming)
        {
            queuedStrikes = 0;
        }
        else if (queuedStrikes > 0 && now - lastStrike >= StrikeInterval && controller != null)
        {
            queuedStrikes--;
            Strike(controller, pawn, gun);
        }

        if (Trace)
        {
            TArray<FName> idle = pawn.PoseIdleStances;
            string poseState = "pose=" + pawn.Pose.MovementStance + "/" + pawn.Pose.WeaponStance + "/" + pawn.Pose.IdleStance + " weapon=" + Util.ClassName(pawn.Pose.Weapon) + " desired=" + pawn.DesiredPoseForReplication + " idle=[" + string.Join(",", Enumerable.Range(0, idle.Count).Select(i => idle[i].ToString())) + "] type=" + pawn.CurrentPoseType +
                " gun=" + gun.GetStateName() + "/" + gun.GadgetState + " aiming=" + aiming + " fov=" + (controller?.PlayerCamera?.GetFOVAngle() ?? 0).ToString("0");

            if (poseState != lastPoseState)
            {
                lastPoseState = poseState;
                ModLog.Info("Trace: " + poseState);
            }
        }

        if (aiming != wasAiming)
        {
            wasAiming = aiming;
            GunsHud.OnAimChanged(aiming);

            if (Trace)
            {
                ModLog.Info(
                    "Aim: " + (aiming ? "raised" : "lowered") + ", gun state=" + gun.GetStateName() + "/" + gun.GadgetState +
                    ", camera=" + Util.ClassName(controller?.PlayerCamera) + " fov=" + (controller?.PlayerCamera?.GetFOVAngle() ?? 0).ToString("0.0") +
                    ", gadget camera='" + GunsAnims.GadgetCamera(gun) + "', pose='" + GunsAnims.PrimedPose(gun) + "'."
                );
            }

            if (aiming && Trace)
            {
                try
                {
                    bool hasPose = controller!.GetGadgetPose(out FName poseName, out _, out _, out _);
                    bool usePose = false;

                    UCall.Call(gun, "UseGadgetPose", read: frame => { if (frame.Return is Property ret) usePose = BmSDK.Framework.MarshalUtil.ToManaged<bool>(frame.Buffer + ret.Offset); });

                    ModLog.Info("Trace: GetGadgetPose=" + hasPose + " '" + poseName + "', gun UseGadgetPose=" + usePose + ", CanAimGadget=" + controller?.CanAimGadget(gun) + ".");
                }
                catch (Exception exception)
                {
                    ModLog.Info("Trace: pose query failed: " + Util.Unwrap(exception));
                }
            }

            if (aiming)
            {
                ApplyFov(controller);
                RaiseGuns(pawn, gun);
            }
            else
            {
                RestoreFov(controller);
                LowerGuns(pawn, gun);
            }
        }
    }

    private static readonly FName GunStance = new FName("StandingHandguns");
    private static readonly FName GunWeaponStance = new FName("Relaxed");
    private static readonly FName StandStance = new FName("Standing");
    private static bool raisedByMod;

    private static void RaiseGuns(RPawnPlayer pawn, RInventoryGadget gun)
    {
        try
        {
            FName pose = GunsAnims.PrimedPose(gun);

            if (Util.IsNone(pose))
            {
                pose = GunStance;
            }

            if (pawn.Pose.MovementStance.ToString() != pose.ToString())
            {
                RAnimUtil_PosePlayer.FTransitionId id = pawn.ChangePose(pose, GunWeaponStance);

                raisedByMod = true;

                if (Trace)
                {
                    ModLog.Info("Aim: ChangePose('" + pose + "', '" + GunWeaponStance + "') -> transition " + id.Id + ".");
                }
            }

            if (gun.GadgetState != RInventoryGadget.GadgetEquipState.GS_AttachetToHand)
            {
                UCall.Call(gun, "AttachToHand");
            }

            if (Trace)
            {
                ModLog.Info("Aim: gun meshes: " + DescribeMesh("Mesh", gun.Mesh) + "; " + DescribeMesh("Mesh2", gun.Mesh2) + ".");
                ModLog.Info("Aim: raised the guns into '" + pose + "' (pose now " + pawn.Pose.MovementStance + "/" + pawn.Pose.WeaponStance + ", guns " + gun.GadgetState + ").");
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("raise-failed", "Aim: raising the guns failed: " + Util.Unwrap(exception));
        }
    }

    private static SkeletalMeshComponent? pistolRight;
    private static SkeletalMeshComponent? pistolLeft;
    private static IntPtr pistolPawn = IntPtr.Zero;

    private static void UpdatePistols(RPawnPlayer? pawn, RInventoryGadget? gun)
    {
        bool held = pawn != null && gun != null && gun.IsValid && Guns.IsGun((pawn.InvManager as RInventoryManager)?.CurrentGadget) &&
            gun.GadgetState == RInventoryGadget.GadgetEquipState.GS_AttachetToHand;
        bool show = held || (GunsConfig.C.AlwaysInHands && pawn != null && pawn.IsValid && gun != null && gun.IsValid && Guns.IsBatman(pawn) && !SniperLink.SniperInHands());

        if (show && (pawn!.Ptr != pistolPawn || pistolRight == null || !pistolRight.IsValid))
        {
            CreatePistols(pawn, gun!);
        }

        foreach (SkeletalMeshComponent? pistol in new[] { pistolRight, pistolLeft })
        {
            if (pistol != null && pistol.IsValid && pistol.HiddenGame == show)
            {
                pistol.SetHidden(!show);
            }
        }

        UpdateHolsters(pawn, gun, show);
        GunsGrip.Tick(pawn, held, show);
        SweepOrphans(pawn);
    }

    private static DateTime nextSweep = DateTime.MinValue;

    private static readonly string[] PropBones = { "Bip01_R_Hand", "Bip01_L_Hand", "Bip01_R_Thigh", "Bip01_L_Thigh" };

    private static void SweepOrphans(RPawnPlayer? pawn)
    {
        DateTime now = DateTime.UtcNow;

        if (now < nextSweep || pawn == null || !pawn.IsValid || pawn.bDeleteMe || !Guns.IsBatman(pawn) || Guns.Batman()?.Ptr != pawn.Ptr)
        {
            return;
        }

        nextSweep = now + TimeSpan.FromSeconds(1);

        try
        {
            SkeletalMeshComponent? body = pawn.Mesh;

            if (body == null || !body.IsValid)
            {
                return;
            }

            List<SkeletalMeshComponent> orphans = new List<SkeletalMeshComponent>();
            var attachments = body.Attachments;

            for (int index = 0; index < attachments.Count; index++)
            {
                string bone = attachments[index].BoneName.ToString();

                if (attachments[index].Component is SkeletalMeshComponent prop && prop.IsValid && prop.SkeletalMesh != null && prop.SkeletalMesh.Name.ToString() == "RH_GUN" &&
                    Array.IndexOf(PropBones, bone) >= 0 && !Same(prop, Guns.Gun?.Mesh) && !Same(prop, Guns.Gun?.Mesh2) &&
                    !Same(prop, pistolRight) && !Same(prop, pistolLeft) && !Same(prop, holsterRight) && !Same(prop, holsterLeft))
                {
                    orphans.Add(prop);
                }
            }

            foreach (SkeletalMeshComponent orphan in orphans)
            {
                orphan.SetHidden(true);
                body.DetachComponent(orphan);
            }

            if (orphans.Count > 0)
            {
                ModLog.Info("Aim: removed " + orphans.Count + " leftover pistol model(s) from Batman (left behind by a reload).");
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("sweep-failed", "Aim: removing leftover pistols failed: " + Util.Unwrap(exception));
        }
    }

    private static SkeletalMeshComponent? holsterRight;
    private static SkeletalMeshComponent? holsterLeft;
    private static IntPtr holsterPawn = IntPtr.Zero;

    public static Vector3 HolsterOffsetRight = new Vector3(16f, 0f, 10f);
    public static Vector3 HolsterOffsetLeft = new Vector3(16f, 0f, -10f);
    public static Rotator HolsterRotationRight = new Rotator(0f, 0f, -90f);
    public static Rotator HolsterRotationLeft = new Rotator(0f, 0f, -90f);

    private static void UpdateHolsters(RPawnPlayer? pawn, RInventoryGadget? gun, bool gunsInHand)
    {
        bool show = pawn != null && gun != null && gun.IsValid && !gunsInHand && pawn.IsValid && Guns.IsBatman(pawn);

        if (show && (pawn!.Ptr != holsterPawn || holsterRight == null || !holsterRight.IsValid))
        {
            CreateHolsters(pawn, gun!);
        }

        foreach (SkeletalMeshComponent? pistol in new[] { holsterRight, holsterLeft })
        {
            if (pistol != null && pistol.IsValid && pistol.HiddenGame == show)
            {
                pistol.SetHidden(!show);
            }
        }
    }

    private static void CreateHolsters(RPawnPlayer pawn, RInventoryGadget gun)
    {
        try
        {
            SkeletalMeshComponent? body = pawn.Mesh;
            SkeletalMesh? pistolMesh = gun.Mesh?.SkeletalMesh ?? Game.FindObject<SkeletalMesh>("DLC0_Redhood_Gun.Mesh.RH_GUN");

            if (body == null || !body.IsValid || pistolMesh == null || !pistolMesh.IsValid)
            {
                return;
            }

            DetachHolsters();
            holsterPawn = pawn.Ptr;
            holsterRight = AttachPistol(body, pistolMesh, "Bip01_R_Thigh", "Bip01_Pelvis", HolsterOffsetRight, HolsterRotationRight);
            holsterLeft = AttachPistol(body, pistolMesh, "Bip01_L_Thigh", "Bip01_Pelvis", HolsterOffsetLeft, HolsterRotationLeft);

            ModLog.Info("Aim: holstered pistols on Batman's thighs (right " + (holsterRight != null) + ", left " + (holsterLeft != null) + ").");
        }
        catch (Exception exception)
        {
            ModLog.Once("holster-failed", "Aim: attaching the holstered pistols failed: " + Util.Unwrap(exception));
        }
    }

    private static void DetachHolsters()
    {
        foreach (SkeletalMeshComponent? pistol in new[] { holsterRight, holsterLeft })
        {
            try
            {
                if (pistol != null && pistol.IsValid && pistol.AttachedToSkelComponent is SkeletalMeshComponent body && body.IsValid)
                {
                    body.DetachComponent(pistol);
                }
            }
            catch
            {
            }
        }

        holsterRight = null;
        holsterLeft = null;
        holsterPawn = IntPtr.Zero;
    }

    private static void CreatePistols(RPawnPlayer pawn, RInventoryGadget gun)
    {
        try
        {
            SkeletalMeshComponent? body = pawn.Mesh;
            SkeletalMesh? pistolMesh = gun.Mesh?.SkeletalMesh ?? Game.FindObject<SkeletalMesh>("DLC0_Redhood_Gun.Mesh.RH_GUN");

            if (body == null || !body.IsValid || pistolMesh == null || !pistolMesh.IsValid)
            {
                ModLog.Once("nopistol", "Aim: no pistol mesh to put in Batman's hands.");

                return;
            }

            pistolPawn = pawn.Ptr;
            pistolRight = AttachPistol(body, pistolMesh, "Bip01_R_Hand", Util.IsNone(gun.EquipBoneName) ? "Gundummy" : gun.EquipBoneName.ToString(), PistolOffsetRight, default);
            pistolLeft = AttachPistol(body, pistolMesh, "Bip01_L_Hand", "Gundummy02", PistolOffsetLeft, PistolRotationLeft);

            if (gun.Mesh != null && gun.Mesh.IsValid && !gun.Mesh.HiddenGame)
            {
                gun.Mesh.SetHidden(true);
            }

            ModLog.Info("Aim: pistols attached to Batman (" + pistolMesh.Name + ", right " + (pistolRight != null) + ", left " + (pistolLeft != null) + ").");
        }
        catch (Exception exception)
        {
            ModLog.Once("pistol-failed", "Aim: attaching the pistols failed: " + Util.Unwrap(exception));
        }
    }

    public static void DetachPistols()
    {
        foreach (SkeletalMeshComponent? pistol in new[] { pistolRight, pistolLeft })
        {
            try
            {
                if (pistol != null && pistol.IsValid && pistol.AttachedToSkelComponent is SkeletalMeshComponent body && body.IsValid)
                {
                    body.DetachComponent(pistol);
                }
            }
            catch
            {
            }
        }

        pistolRight = null;
        pistolLeft = null;
        pistolPawn = IntPtr.Zero;

        DetachHolsters();
    }

    public static Vector3 PistolOffsetRight = new Vector3(9f, -3f, 0f);
    public static Vector3 PistolOffsetLeft = new Vector3(9f, -3f, 0f);

    public static Rotator PistolRotationLeft = new Rotator(0f, 0f, 180f);

    private static bool TunePistols = false;

    private static void TunePistolKey(Keys key)
    {
        Vector3 step = key switch
        {
            Keys.NumPad4 => new Vector3(-1, 0, 0),
            Keys.NumPad6 => new Vector3(1, 0, 0),
            Keys.NumPad2 => new Vector3(0, -1, 0),
            Keys.NumPad8 => new Vector3(0, 1, 0),
            Keys.NumPad1 => new Vector3(0, 0, -1),
            Keys.NumPad7 => new Vector3(0, 0, 1),
            _ => Vector3.Zero,
        };

        if (step != Vector3.Zero)
        {
            PistolOffsetRight += step;
            PistolOffsetLeft += step;
            DetachPistols();
        }

        if (key == Keys.Add || key == Keys.Subtract)
        {
            float factor = key == Keys.Add ? 2f : 0.5f;

            GunsHud.CursorScaleX *= factor;
            GunsHud.CursorScaleY *= factor;
            ModLog.Info("Tune: cursor scale " + GunsHud.CursorScaleX + ", " + GunsHud.CursorScaleY + ".");
        }

        if (key == Keys.NumPad9 || key == Keys.Divide)
        {
            if (key == Keys.NumPad9)
            {
                HolsterRotationRight = new Rotator(0f, 0f, HolsterRotationRight.Roll + 90f);
            }
            else
            {
                HolsterRotationLeft = new Rotator(0f, 0f, HolsterRotationLeft.Roll + 90f);
            }

            DetachHolsters();
            ModLog.Info("Tune: holster roll right=" + HolsterRotationRight.Roll + " left=" + HolsterRotationLeft.Roll + ".");
        }

        if (key == Keys.Decimal)
        {
            GunsGrip.RightSign = -GunsGrip.RightSign;
            ModLog.Info("Tune: right grip bend sign " + GunsGrip.RightSign + ".");
        }

        if (key == Keys.NumPad0)
        {
            reloadVariant = (reloadVariant + 1) % ReloadVariants.Length;
            ModLog.Info("Tune: reload variant " + ReloadVariants[reloadVariant] + ".");
        }

        if (key == Keys.NumPad3 && Guns.Batman() is RPawnPlayer reloader)
        {
            PlayReload(reloader);
            ModLog.Info("Tune: played reload " + ReloadVariants[reloadVariant] + ".");
        }

        if (step != Vector3.Zero || key == Keys.NumPad5)
        {
            ModLog.Info("Tune: pistol offset right=" + PistolOffsetRight + " left=" + PistolOffsetLeft + ".");
        }

        if (key == Keys.NumPad5)
        {
            LogHandBones("R", "Gundummy");
            LogHandBones("L", "Gundummy02");
        }
    }

    private static void LogHandBones(string side, string gunBone)
    {
        try
        {
            SkeletalMeshComponent? body = Guns.Batman()?.Mesh;

            if (body == null)
            {
                return;
            }

            FName gun = new FName(gunBone);
            List<string> parts = new List<string>();
            List<Vector3> palm = new List<Vector3>();

            for (int index = 0; index < 400; index++)
            {
                string name;

                try { name = body.GetBoneName(index).ToString(); } catch { break; }

                if (string.IsNullOrEmpty(name) || name == "None")
                {
                    continue;
                }

                if (!name.Contains("_" + side + "_") && !name.StartsWith(gunBone, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!name.Contains("Hand") && !name.Contains("Finger") && !name.Contains("Gun") && !name.Contains("Forearm"))
                {
                    continue;
                }

                Vector3 world = body.GetBoneLocation(new FName(name));

                body.TransformToBoneSpace(gun, world, default, out Vector3 local, out _);
                parts.Add(name + "=" + local.ToString("0.0"));

                if (name.EndsWith("_" + side + "_Hand", StringComparison.Ordinal) || System.Text.RegularExpressions.Regex.IsMatch(name, "_" + side + "_Finger[1-4]$"))
                {
                    palm.Add(world);
                }
            }

            if (palm.Count > 0)
            {
                Vector3 centre = palm.Aggregate(Vector3.Zero, (sum, point) => sum + point) / palm.Count;

                body.TransformToBoneSpace(gun, centre, default, out Vector3 palmLocal, out _);
                parts.Add("PALM(" + palm.Count + ")=" + palmLocal.ToString("0.0"));
            }

            ModLog.Info("Tune: " + side + " hand in " + gunBone + " space: " + string.Join(", ", parts) + ".");
        }
        catch (Exception exception)
        {
            ModLog.Info("Tune: hand bone dump failed: " + Util.Unwrap(exception));
        }
    }

    private static SkeletalMeshComponent? AttachPistol(SkeletalMeshComponent body, SkeletalMesh mesh, string bone, string fallbackBone, Vector3 offset, Rotator rotation)
    {
        FName boneName = new FName(bone);

        if (body.MatchRefBone(boneName) < 0)
        {
            boneName = new FName(fallbackBone);
        }

        if (body.MatchRefBone(boneName) < 0)
        {
            return null;
        }

        SkeletalMeshComponent pistol = new SkeletalMeshComponent(body.Owner ?? (GameObject)body);

        pistol.SetSkeletalMesh(mesh);
        body.AttachComponent(pistol, boneName, offset, rotation);
        pistol.SetHidden(true);

        return pistol;
    }

    private static string DescribeMesh(string label, SkeletalMeshComponent? mesh)
    {
        if (mesh == null || !mesh.IsValid)
        {
            return label + "=<none>";
        }

        string where = "";

        try
        {
            SkeletalMeshComponent? body = Guns.Batman()?.Mesh;

            if (body != null)
            {
                where = " bounds=" + mesh.Bounds.Origin.ToString("0") + " r=" + mesh.Bounds.SphereRadius.ToString("0") + " scale=" + mesh.Scale + "/" + mesh.Scale3D +
                    " Gundummy=" + body.GetBoneLocation(new FName("Gundummy")).ToString("0") + " R_Hand=" + body.GetBoneLocation(new FName("Bip01_R_Hand")).ToString("0") +
                    " pawn=" + Guns.Batman()!.Location.ToString("0");
            }
        }
        catch (Exception exception)
        {
            where = " (" + Util.Unwrap(exception) + ")";
        }

        return label + "=" + mesh.SkeletalMesh?.GetPathName() + where + " hidden=" + mesh.HiddenGame + " ownerNoSee=" + mesh.bOwnerNoSee + " onlyOwnerSee=" + mesh.bOnlyOwnerSee + " attached=" + mesh.bAttached + " ignoreOwnerHidden=" + mesh.bIgnoreOwnerHidden + " gunActorHidden=" + Guns.Gun?.bHidden +
            " attachedTo=" + (mesh.AttachedToSkelComponent?.SkeletalMesh?.Name.ToString() ?? "none") + " owner=" + Util.ClassName(mesh.Owner);
    }

    private static void LowerGuns(RPawnPlayer pawn, RInventoryGadget gun)
    {
        if (!raisedByMod)
        {
            return;
        }

        raisedByMod = false;

        try
        {
            pawn.ChangePose(StandStance, GunWeaponStance);

            UCall.Call(gun, "AttachToBelt");
        }
        catch (Exception exception)
        {
            ModLog.Once("lower-failed", "Aim: lowering the guns failed: " + Util.Unwrap(exception));
        }
    }

    private static bool IsAiming(RInventoryGadget gun)
    {
        if (gun.bAiming)
        {
            return true;
        }

        string state = gun.GetStateName().ToString();

        return state == "Primed" || (state == "Firing" && wasAiming);
    }

    public static string AimCameraOverride = "";

    private static bool cameraHooked;

    public static void HookCamera(Class gunClass)
    {
        if (cameraHooked)
        {
            return;
        }

        try
        {
            Function? function = UCall.Find(gunClass.DefaultObject, "GetGadgetCamera");

            if (function == null || function.Outer == null || function.Outer.Ptr != gunClass.Ptr)
            {
                ModLog.Warn("Camera: the guns do not override GetGadgetCamera; nothing to hook.");

                return;
            }

            string path = function.GetPathName();
            Assembly sdk = typeof(Game).Assembly;
            Type manager = sdk.GetType("BmSDK.Framework.Redirection.RedirectManager", throwOnError: true)!;
            Type infoType = sdk.GetType("BmSDK.Framework.Redirection.GlobalRedirectorInfo", throwOnError: true)!;
            object global = manager.GetField("Global", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;
            IDictionary table = (IDictionary)global.GetType().GetField("_globalRedirsDict", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(global)!;
            MethodInfo method = typeof(GunsAim).GetMethod(nameof(GetGadgetCameraRedirect), BindingFlags.Static | BindingFlags.NonPublic)!;
            object info = Activator.CreateInstance(infoType, typeof(MThrowGadgetBase), true, method, typeof(GunsAim).Assembly)!;

            if (!table.Contains(path))
            {
                table[path] = Activator.CreateInstance(typeof(List<>).MakeGenericType(infoType));
            }

            ((IList)table[path]!).Add(info);
            manager.GetMethod("QueueConfigureFunction", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, new object[] { path });
            function.FunctionFlags |= Function.EFunctionFlags.FUNC_Defined;

            cameraHooked = true;
            ModLog.Info("Camera: hooked " + path + ".");
        }
        catch (Exception exception)
        {
            ModLog.Warn("Camera: hooking GetGadgetCamera failed (the guns keep Red Hood's first-person camera): " + Util.Unwrap(exception));
        }
    }

    private static FName GetGadgetCameraRedirect(MThrowGadgetBase self, bool inSoftCover, RInventoryGadget.CoverCornerType cornerType)
    {
        try
        {
            if (self.Owner is RPawnPlayer owner && owner.IsValid && Guns.IsBatman(owner))
            {
                FName camera = BatmanAimCamera(owner, inSoftCover, cornerType);

                if (!Util.IsNone(camera))
                {
                    ModLog.Once("cam:" + camera, "Camera: guns aim with Batman's '" + camera + "' camera (Red Hood's is first person).");

                    return camera;
                }
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("cam-failed", "Camera: picking Batman's aim camera failed: " + Util.Unwrap(exception));
        }

        FName original = FName.None;

        UCall.Call(self, "GetGadgetCamera",
            frame => { BmSDK.Framework.MarshalUtil.ToUnmanaged(inSoftCover, frame["InSoftCover"]); BmSDK.Framework.MarshalUtil.ToUnmanaged(cornerType, frame["CornerType"]); },
            frame => { if (frame.Return is Property ret) original = BmSDK.Framework.MarshalUtil.ToManaged<FName>(frame.Buffer + ret.Offset); });

        return original;
    }

    private static FName BatmanAimCamera(RPawnPlayer pawn, bool inSoftCover, RInventoryGadget.CoverCornerType cornerType)
    {
        if (AimCameraOverride.Length > 0)
        {
            return new FName(AimCameraOverride);
        }

        RInventoryGadget? batarang = (pawn.InvManager as RInventoryManager)?.GetGadgetByName(new FName("RBatarangBm"), true);

        if (batarang == null || !batarang.IsValid)
        {
            return FName.None;
        }

        FName camera = FName.None;

        UCall.Call(batarang, "GetGadgetCamera",
            frame => { BmSDK.Framework.MarshalUtil.ToUnmanaged(inSoftCover, frame["InSoftCover"]); BmSDK.Framework.MarshalUtil.ToUnmanaged(cornerType, frame["CornerType"]); },
            frame => { if (frame.Return is Property ret) camera = BmSDK.Framework.MarshalUtil.ToManaged<FName>(frame.Buffer + ret.Offset); });

        return camera;
    }

    private static float fovBlend;
    private static float baseFov = 86f;
    private static DateTime lastFovUpdate = DateTime.MinValue;

    public static float ActiveFov => fovBlend > 0f ? CurrentFov() : 0f;

    private static float CurrentFov()
    {
        float t = fovBlend * fovBlend * (3f - 2f * fovBlend);

        return baseFov + (AimFov - baseFov) * t;
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static void ApplyFov(RPlayerController? controller)
    {
        if (AimFov <= 0 || controller?.PlayerCamera is not R3rdPersonCamera camera || !camera.IsValid)
        {
            return;
        }

        if (!fovOverridden && fovBlend <= 0f)
        {
            float current = camera.GetFOVAngle();

            baseFov = IsFinite(current) && current > 1f && current < 170f ? current : 86f;
        }

        fovOverridden = true;
    }

    private static void RestoreFov(RPlayerController? controller)
    {
        fovOverridden = false;
    }

    private static void UpdateFov(RPlayerController? controller)
    {
        DateTime now = DateTime.UtcNow;
        float deltaTime = Math.Clamp((float)(now - lastFovUpdate).TotalSeconds, 0f, 0.1f);

        lastFovUpdate = now;

        float target = fovOverridden ? 1f : 0f;

        if (fovBlend == target && target == 0f)
        {
            return;
        }

        fovBlend = target > fovBlend ? MathF.Min(target, fovBlend + deltaTime / FovBlendTime) : MathF.Max(target, fovBlend - deltaTime / FovBlendTime);

        try
        {
            if (controller?.PlayerCamera is not R3rdPersonCamera camera || !camera.IsValid)
            {
                return;
            }

            if (fovBlend <= 0f)
            {
                camera.bOverrideCameraFOV = false;

                if (!IsFinite(camera.CurrentCameraFOVAngle) || !IsFinite(camera.FinalCameraFOVAngle))
                {
                    camera.CurrentCameraFOVAngle = baseFov;
                    camera.FinalCameraFOVAngle = baseFov;
                }

                return;
            }

            float fov = CurrentFov();

            camera.OverrideCameraFOVAngle = fov;
            camera.CurrentOverrideCameraFOVAngle = fov;
            camera.LastOverrideCameraFOVAngle = fov;
            camera.OverrideCameraFOVTime = 1f;
            camera.OverrideCameraFOVTimer = 1f;
            camera.bOverrideCameraFOV = true;

            if (!IsFinite(camera.CurrentCameraFOVAngle))
            {
                camera.CurrentCameraFOVAngle = fov;
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("fov-failed", "Aim: setting the aim FOV failed: " + Util.Unwrap(exception));
        }
    }

    public static void OnKeyDown(Keys key)
    {
        if (TunePistols)
        {
            TunePistolKey(key);
        }

        if (!Trace || key != Keys.LButton || !Guns.HeldBy(out _, out _))
        {
            return;
        }

        RInventoryGadget gun = Guns.Gun!;
        RPlayerController? controller = Game.GetPlayerController(0);

        ModLog.Info("Input: left click with the guns, state=" + gun.GetStateName() + ", aiming=" + gun.bAiming + ", aimPressed=" + controller?.bAimGadgetPressed + ".");
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.GadgetPressed))]
    private static void GadgetPressedRedirect(RPlayerController self)
    {
        if (!BatmanArsenalPack.Arsenal.Guns)
        {
            self.GadgetPressed();
            return;
        }

        bool handled = false;

        try
        {
            if (Guns.HeldBy(out RPawnPlayer? pawn, out _) && wasAiming)
            {
                handled = true;

                if (DateTime.UtcNow - lastStrike >= StrikeInterval)
                {
                    Strike(self, pawn!, Guns.Gun!);
                }
                else if (queuedStrikes < MaxQueuedStrikes)
                {
                    queuedStrikes++;
                }
            }
            else if (Trace && Guns.HeldBy(out _, out _))
            {
                ModLog.Info("Input: GadgetPressed with the guns, not aiming (state=" + Guns.Gun!.GetStateName() + "); the game handles it.");
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("strike-failed", "Aim: strike failed: " + Util.Unwrap(exception));
        }

        if (!handled)
        {
            self.GadgetPressed();
        }
    }

    private static void Strike(RPlayerController controller, RPawnPlayer pawn, RInventoryGadget gun)
    {
        DateTime now = DateTime.UtcNow;

        ApplyPendingHit(pawn, DateTime.MaxValue);

        if (!GunsHud.TryFire(pawn))
        {
            queuedStrikes = 0;

            return;
        }

        lastStrike = now;
        strikes++;

        string animName = StrikeAnims[strikeIndex];

        strikeIndex = (strikeIndex + 1) % StrikeAnims.Length;

        bool played = PlayStrikeAnim(pawn, animName);

        strikeEndsAt = played ? now + StrikeLength : DateTime.MinValue;

        UCall.Call(gun, strikes % 2 == 0 ? "SpawnFxLeftGun" : "SpawnFxRightGun");

        GunsDeadZone.ViewPoint(controller, out Vector3 eye, out Rotator view);

        Vector3 aim = Vector3.Normalize(view.ToDirection());
        RPawnVillain? target = FindTarget(pawn, gun, eye, aim);

        GunsDeadZone.OnShot();

        pendingTarget = target;
        pendingAim = aim;
        pendingHitAt = target != null ? now + StrikeContact : DateTime.MinValue;

        if (Trace)
        {
            ModLog.Info("Aim: strike " + strikes + " (" + animName + ", played=" + played + ", queued " + queuedStrikes + ") -> " + (target != null ? Util.ClassName(target) : "no target") + ".");
        }
    }

    public static bool IsAimed => wasAiming;

    private static readonly (double Time, bool Left)[] QuickfireFlashes =
    {
        (0.32, true), (0.41, false), (0.55, true), (0.66, false), (0.80, true), (0.93, false),
    };

    private static string lastGunState = "";
    private static bool DebugQuickfire = false;
    private static DateTime quickfireStart = DateTime.MinValue;
    private static int quickfireFlash;

    private static readonly Dictionary<string, ParticleSystem> flashTemplates = new Dictionary<string, ParticleSystem>();

    private static bool SpawnMuzzleFlash(RInventoryGadget gun, bool left)
    {
        RPawnPlayer? pawn = Guns.Batman();
        string path = left ? "PFX_RedHood.Emmiter.RedHood_GUNS_muzzleflash_Left" : "PFX_RedHood.Emmiter.RedHood_GUNS_muzzleflash";

        try
        {
            if (!flashTemplates.TryGetValue(path, out ParticleSystem? template) || !template.IsValid)
            {
                template = Game.FindObject<ParticleSystem>(path);

                if (template == null)
                {
                    ModLog.Once("noflash:" + path, "Aim: muzzle flash " + path + " not loaded.");

                    return false;
                }

                template.AddToRoot();
                flashTemplates[path] = template;
            }

            EmitterPool? pool = pawn?.WorldInfo?.MyEmitterPool;

            if (pawn == null || pool == null || !pool.IsValid)
            {
                return false;
            }

            pool.SpawnEmitterMeshAttachment(template, pawn.Mesh, new FName(left ? "Gundummy02" : "Gundummy"), false);

            return true;
        }
        catch (Exception exception)
        {
            ModLog.Once("flash-failed", "Aim: spawning a muzzle flash failed: " + Util.Unwrap(exception));

            return false;
        }
    }

    private static void TrackQuickfire(RInventoryGadget gun, DateTime now)
    {
        string state = gun.GetStateName().ToString();

        if (state == "Firing" && lastGunState != "Firing" && !wasAiming)
        {
            StartQuickfire(now);
        }

        lastGunState = state;

        if (quickfireStart == DateTime.MinValue)
        {
            return;
        }

        if (wasAiming)
        {
            quickfireStart = DateTime.MinValue;

            return;
        }

        double elapsed = (now - quickfireStart).TotalSeconds;

        while (quickfireFlash < QuickfireFlashes.Length && elapsed >= QuickfireFlashes[quickfireFlash].Time)
        {
            bool called = SpawnMuzzleFlash(gun, QuickfireFlashes[quickfireFlash].Left);

            GunsHud.OnQuickfire();

            if (Trace || DebugQuickfire)
            {
                ModLog.Info("Aim: quickfire flash " + quickfireFlash + " at " + elapsed.ToString("0.00") + " s (called " + called + ", gun state " + gun.GetStateName() + "/" + gun.GadgetState + ").");
            }

            quickfireFlash++;
        }

        if (quickfireFlash >= QuickfireFlashes.Length)
        {
            quickfireStart = DateTime.MinValue;
        }
    }

    public static void NotifyQuickfire()
    {
        if (!wasAiming)
        {
            StartQuickfire(DateTime.UtcNow);
        }
    }

    private static void StartQuickfire(DateTime now)
    {
        if (quickfireStart != DateTime.MinValue)
        {
            return;
        }

        quickfireStart = now;
        quickfireFlash = 0;
        ModLog.Once("quickfire-fx", "Aim: quickfire detected; playing its muzzle flashes and spending a round per shot.");
    }

    public static bool StrikeFinished(DateTime now) => strikeEndsAt == DateTime.MinValue && queuedStrikes == 0 && now - lastStrike >= StrikeInterval;

    public static RPawnVillain? TargetUnderReticle(RPawnPlayer pawn)
    {
        RPlayerController? controller = Game.GetPlayerController(0);
        RInventoryGadget? gun = Guns.Gun;

        if (controller == null || gun == null)
        {
            return null;
        }

        GunsDeadZone.ViewPoint(controller, out Vector3 eye, out Rotator view);

        bool trace = Trace;

        Trace = false;

        try
        {
            return FindTarget(pawn, gun, eye, Vector3.Normalize(view.ToDirection()));
        }
        finally
        {
            Trace = trace;
        }
    }

    private static readonly (string Anim, bool Modelspace)[] ReloadVariants =
    {
        ("Ricochet_Stand_In", false),
        ("Ricochet_Stand_Catch", false),
        ("Ricochet_Quick_Combo_Throw_Front", false),
        ("Ricochet_Stand_Out", false),
    };

    private static int reloadVariant;

    public static void PlayReload(RPawnPlayer pawn)
    {
        (string anim, bool modelspace) = ReloadVariants[reloadVariant];
        AnimSequence? sequence = FindSequence("Code.RH_HandGun", anim);

        if (sequence == null)
        {
            ModLog.Once("noreload:" + anim, "Aim: reload animation " + anim + " not found.");

            return;
        }

        RAnimConfig.FCustomAnimConfig config = default;

        config.UpperBodyAnim = sequence.SequenceName;
        config.CustomUpperBodyAnim = sequence;
        config.ModelspaceUpperBody = modelspace;

        try
        {
            strikeEndsAt = DateTime.MinValue;
            pawn.StopWeaponOverlay();
            pawn.PlayCustomWeaponOverlay(config);
        }
        catch (Exception exception)
        {
            ModLog.Once("reload-failed", "Aim: reload animation failed: " + Util.Unwrap(exception));
        }
    }

    private static void EndStrike(RPawnPlayer pawn, DateTime now)
    {
        if (strikeEndsAt == DateTime.MinValue || now < strikeEndsAt)
        {
            return;
        }

        strikeEndsAt = DateTime.MinValue;
        Util.TryCall("StopWeaponOverlay", pawn.StopWeaponOverlay);
    }

    private static void ApplyPendingHit(RPawnPlayer pawn, DateTime now)
    {
        if (pendingHitAt == DateTime.MinValue || now < pendingHitAt)
        {
            return;
        }

        RPawnVillain? target = pendingTarget;

        pendingTarget = null;
        pendingHitAt = DateTime.MinValue;

        if (target != null && target.IsValid && IsAlive(target))
        {
            Kill(pawn, target, pendingAim);
        }
    }

    private static bool PlayStrikeAnim(RPawnPlayer pawn, string animName)
    {
        AnimSequence? sequence = FindStrikeSequence(animName);

        if (sequence == null)
        {
            ModLog.Once("nostrike:" + animName, "Aim: strike animation " + animName + " not found (is RH_TG_Beatdown loaded?).");

            return false;
        }

        RAnimConfig.FCustomAnimConfig config = default;

        config.UpperBodyAnim = sequence.SequenceName;
        config.CustomUpperBodyAnim = sequence;
        config.ModelspaceUpperBody = true;

        try
        {
            if (strikeEndsAt != DateTime.MinValue)
            {
                strikeEndsAt = DateTime.MinValue;
                pawn.StopWeaponOverlay();
            }

            pawn.PlayCustomWeaponOverlay(config);

            return true;
        }
        catch (Exception exception)
        {
            ModLog.Once("overlay-failed", "Aim: PlayCustomWeaponOverlay failed: " + Util.Unwrap(exception));

            return false;
        }
    }

    private static readonly Dictionary<string, AnimSequence> sequences = new Dictionary<string, AnimSequence>();

    private static AnimSequence? FindStrikeSequence(string animName) => FindSequence("Code.RH_TG_Beatdown", animName);

    private static AnimSequence? FindSequence(string setName, string animName)
    {
        if (sequences.TryGetValue(animName, out AnimSequence? cached) && cached.IsValid)
        {
            return cached;
        }

        FName shortName = new FName(setName.Substring(setName.LastIndexOf('.') + 1));

        foreach (AnimSet set in Game.FindObjects<AnimSet>())
        {
            if (set == null || !set.IsValid || set.Name != shortName || Util.FullName(set) != setName)
            {
                continue;
            }

            AnimSequence? sequence = set.FindAnimSequence(new FName(animName));

            if (sequence != null && sequence.IsValid)
            {
                sequences[animName] = sequence;

                return sequence;
            }
        }

        return null;
    }

    private static RPawnVillain? FindTarget(RPawnPlayer pawn, RInventoryGadget gun, Vector3 eye, Vector3 aim)
    {
        try
        {
            if (gun is MThrowGadgetBase thrower && thrower.CurrentTarget is RPawnVillain locked && locked.IsValid && IsAlive(locked))
            {
                return locked;
            }
        }
        catch
        {
        }

        float bestScore = float.MaxValue;
        RPawnVillain? best = null;
        List<string> considered = new List<string>();

        foreach (RPawnVillain villain in Game.FindObjects<RPawnVillain>())
        {
            if (villain == null || !villain.IsValid)
            {
                continue;
            }

            float distance = Vector3.Distance(villain.Location, pawn.Location);

            if (distance > ShotRange)
            {
                continue;
            }

            float radius = 34f;
            float halfHeight = 90f;

            try
            {
                if (villain.CylinderComponent is CylinderComponent cylinder && cylinder.IsValid && cylinder.CollisionHeight > 0)
                {
                    radius = cylinder.CollisionRadius;
                    halfHeight = cylinder.CollisionHeight;
                }
            }
            catch
            {
            }

            float miss = float.MaxValue;
            float along = 0f;

            for (int step = 0; step <= 6; step++)
            {
                Vector3 point = villain.Location + new Vector3(0, 0, -halfHeight + 2f * halfHeight * step / 6f);
                Vector3 offset = point - eye;
                float forward = Vector3.Dot(offset, aim);

                if (forward <= 0f)
                {
                    continue;
                }

                float side = (offset - aim * forward).Length();

                if (side < miss)
                {
                    miss = side;
                    along = forward;
                }
            }

            bool alive = IsAlive(villain);
            bool hit = miss <= radius + ShotMargin;
            bool visible = true;

            if (hit && alive)
            {
                Vector3 chest = villain.Location + new Vector3(0, 0, halfHeight * 0.4f);
                Vector3 head = villain.Location + new Vector3(0, 0, halfHeight * 0.8f);

                try { visible = pawn.FastTrace(chest, eye, default, true) || pawn.FastTrace(head, eye, default, true); } catch { }
            }

            if (Trace && considered.Count < 6)
            {
                considered.Add(Util.ClassName(villain) + " d=" + distance.ToString("0") + " miss=" + (miss == float.MaxValue ? "behind" : miss.ToString("0")) +
                    " alive=" + alive + " (hp " + villain.Health + ") visible=" + visible);
            }

            if (!alive || !hit || !visible)
            {
                continue;
            }

            float score = miss / MathF.Max(along, 1f);

            if (score < bestScore)
            {
                bestScore = score;
                best = villain;
            }
        }

        if (best == null && Trace)
        {
            ModLog.Info("Aim: no target; nearby: " + (considered.Count == 0 ? "<none in range>" : string.Join("; ", considered)) + ".");
        }

        return best;
    }

    private static bool IsAlive(RPawnVillain villain)
    {
        try
        {
            return villain.Health > 0 && !villain.bDiedAlready && !villain.bGoodAsDead && !villain.bHidden;
        }
        catch
        {
            return false;
        }
    }

    private static void Kill(RPawnPlayer pawn, RPawnVillain villain, Vector3 aim)
    {
        Vector3 flat = Vector3.Normalize(new Vector3(aim.X, aim.Y, 0));
        Vector3 force = flat * LaunchForce + new Vector3(0, 0, LaunchLift);

        RPawnCombat.FDamageInfo info = default;

        info.Attacker = pawn;
        info.DamageAmount = 1000f;
        info.DamageType = MDmgType_HandgunShot.StaticClass();
        info.Impulse = force;
        info.RagdollForce = force;
        info.RagdollForceDuration = 0.15f;
        info.bHeavyHit = true;
        info.bCritical = true;

        try
        {
            villain.PlayRagdoll(info, RGameInfo.RagdollStunType.RAGSTUN_Generic, true);
        }
        catch (Exception exception)
        {
            ModLog.Once("ragdoll-failed", "Aim: PlayRagdoll failed: " + Util.Unwrap(exception));
        }

        kills++;

        if (Trace)
        {
            ModLog.Info("Aim: after the shot " + Util.ClassName(villain) + " health=" + villain.Health + ", died=" + villain.bDiedAlready + ", goodAsDead=" + villain.bGoodAsDead + ".");
        }
    }

    public static void Report()
    {
        RPlayerController? controller = Game.GetPlayerController(0);

        ModLog.Info(
            "Aim report: aiming=" + wasAiming + ", fovOverride=" + fovOverridden + ", camera=" + Util.ClassName(controller?.PlayerCamera) +
            " fov=" + (controller?.PlayerCamera?.GetFOVAngle() ?? 0).ToString("0.0") + ", strikes=" + strikes + ", kills=" + kills + ", " + GunsHud.Describe() + "."
        );
        ModLog.Info("Pistols: alwaysInHands=" + GunsConfig.C.AlwaysInHands + ", right " + DescribeProp(pistolRight, "Bip01_R_Hand") + ", left " + DescribeProp(pistolLeft, "Bip01_L_Hand") +
            ", holsters " + DescribeProp(holsterRight, "Bip01_R_Thigh") + " / " + DescribeProp(holsterLeft, "Bip01_L_Thigh") + ".");
        Util.TryCall("grip report", () => ModLog.Info("Grip: " + GunsGrip.Describe() + "."));
        ModLog.Info("Pistols on Batman's mesh: " + DescribeAttachedGuns() + "; gadget meshes " + DescribeMesh("Mesh", Guns.Gun?.Mesh) + "; " + DescribeMesh("Mesh2", Guns.Gun?.Mesh2) + ".");
    }

    private static string DescribeAttachedGuns()
    {
        try
        {
            SkeletalMeshComponent? body = Guns.Batman()?.Mesh;

            if (body == null || !body.IsValid)
            {
                return "<no mesh>";
            }

            List<string> parts = new List<string>();
            var attachments = body.Attachments;

            for (int index = 0; index < attachments.Count; index++)
            {
                if (attachments[index].Component is not SkeletalMeshComponent prop || !prop.IsValid || prop.SkeletalMesh == null || !prop.SkeletalMesh.Name.ToString().Contains("GUN"))
                {
                    continue;
                }

                string owner = Same(prop, pistolRight) ? "hand R" : Same(prop, pistolLeft) ? "hand L" : Same(prop, holsterRight) ? "holster R" : Same(prop, holsterLeft) ? "holster L" : "ORPHAN";

                parts.Add(owner + "@" + attachments[index].BoneName + (prop.HiddenGame ? " hidden" : " shown"));
            }

            return parts.Count + " [" + string.Join(", ", parts) + "]";
        }
        catch (Exception exception)
        {
            return "(" + Util.Unwrap(exception) + ")";
        }
    }

    private static bool Same(SkeletalMeshComponent prop, SkeletalMeshComponent? other) => other != null && other.IsValid && other.Ptr == prop.Ptr;

    private static string DescribeProp(SkeletalMeshComponent? prop, string bone)
    {
        try
        {
            if (prop == null || !prop.IsValid)
            {
                return "<none>";
            }

            SkeletalMeshComponent? body = Guns.Batman()?.Mesh;
            string distance = body != null ? Vector3.Distance(prop.Bounds.Origin, body.GetBoneLocation(new FName(bone))).ToString("0") : "?";

            return (prop.HiddenGame ? "hidden" : "shown") + " " + distance + "u from " + bone;
        }
        catch (Exception exception)
        {
            return "(" + Util.Unwrap(exception) + ")";
        }
    }
}
