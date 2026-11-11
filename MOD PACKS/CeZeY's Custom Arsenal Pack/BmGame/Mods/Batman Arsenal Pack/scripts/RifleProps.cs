#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace BatmanAssaultRifle;

internal static class RifleProps
{
    private const string CompPrefix = "BARifle_";
    private const string MeshPath = "Assault_Rifle.Mesh.Assault_Rifle_Mesh";
    private const string AnimSetPath = "Anim_Thug_Rifle.Code.TGRifle_Movement";
    private static readonly Vector3 MuzzleLocal = new Vector3(55.6f, 0f, 12f);
    private const float RifleCentre = 14f;

    public const string M4Package = "BatmanArsenalM4C_SF";
    public const string M4MeshName = "BMAR_Carbn01_Skin";
    private const string M4MeshPath = "BMAR_Carbn01_Mesh.Mesh." + M4MeshName;
    public const string M4Diffuse = "BMAR_Carbn01.Maps.BMAR_Carbn01_D";
    public const string M4Normal = "BMAR_Carbn01.Maps.BMAR_Carbn01_N";
    public const string M4AllMask = "BMAR_Carbn01.Maps.BMAR_Carbn01_R";
    public const string M4CamoMask = "BMAR_Carbn01.Maps.BMAR_Carbn01_Camo_R";
    public const string M4BodyMask = "GCPD_Cop.Maps.BMAR_Carbn01_PM_R";
    public const string M4CamoDiffuse = "GCPD_Cop.Maps.BMAR_Carbn01_CamDif";
    public const string M4PaintDiffuse = "Oracle.Maps.BMAR_Crb01_PD";
    private static readonly Vector3 M4MuzzleLocal = new Vector3(56.0f, 0f, 8.4f);

    public static SkeletalMesh? M4Mesh;
    private static bool m4Tried;

    public static bool M4Installed()
    {
        try
        {
            return System.IO.File.Exists(System.IO.Path.Combine(FileUtils.GetGamePath(), "CookedPCConsole", M4Package + ".upk"));
        }
        catch
        {
            return false;
        }
    }

    public static bool M4Active => RifleConfig.C.M4 && M4Mesh != null && M4Mesh.IsValid;

    public static SkeletalMesh? CurrentMesh => M4Active ? M4Mesh : RifleMesh;

    private static bool IsM4(SkeletalMesh? sm) => sm != null && M4Mesh != null && sm.Ptr == M4Mesh.Ptr;

    private static Vector3 ActiveMuzzleLocal(SkeletalMeshComponent m) => IsM4(m.SkeletalMesh) ? M4MuzzleLocal * m.Scale : MuzzleLocal;

    private static void LoadM4()
    {
        if (!RifleConfig.C.M4 || (M4Mesh != null && M4Mesh.IsValid))
        {
            return;
        }

        try
        {
            M4Mesh = Game.FindObject<SkeletalMesh>(M4MeshPath);

            if (M4Mesh == null && !m4Tried)
            {
                m4Tried = true;

                if (!M4Installed())
                {
                    ModLog.Info("Props: " + M4Package + ".upk is not in BmGame\\CookedPCConsole; using the militia rifle instead.");

                    return;
                }

                Package? p = Game.LoadPackage(M4Package);

                ModLog.Info("Props: LoadPackage(" + M4Package + ") -> " + (p != null && p.IsValid ? "loaded" : "null") + ".");
                M4Mesh = Game.FindObject<SkeletalMesh>(M4MeshPath);
            }

            if (M4Mesh == null)
            {
                return;
            }

            M4Mesh.AddToRoot();

            var bounds = M4Mesh.Bounds;

            ModLog.Info("Props: M4 bounds were origin " + Util.V(bounds.Origin) + ", extent " + Util.V(bounds.BoxExtent) + ", radius " + bounds.SphereRadius.ToString("0.0", Util.Inv) + ".");
            bounds.Origin = new Vector3(16.8f, 0f, 4.1f);
            bounds.BoxExtent = new Vector3(42f, 6f, 14f);
            bounds.SphereRadius = 46f;
            M4Mesh.Bounds = bounds;

            foreach (string path in new[] { M4Diffuse, M4Normal, M4AllMask, M4CamoMask, M4BodyMask, M4CamoDiffuse, M4PaintDiffuse })
            {
                Texture? texture = Game.FindObject<Texture>(path);

                if (texture == null)
                {
                    ModLog.Info("Props: M4 texture " + path + " not found.");
                }

                texture?.AddToRoot();
            }

            ModLog.Info("Props: loaded the M4 Carbine " + M4MeshName + " from " + M4Package + ".");
        }
        catch (Exception exception)
        {
            M4Mesh = null;
            ModLog.Warn("Props: loading the M4 failed (using the militia rifle): " + Util.Unwrap(exception));
        }
    }

    private static void ApplyLook(SkeletalMeshComponent comp)
    {
        SkeletalMesh? want = CurrentMesh;

        if (want == null)
        {
            return;
        }

        if (comp.SkeletalMesh == null || comp.SkeletalMesh.Ptr != want.Ptr)
        {
            comp.SetSkeletalMesh(want, false);
            ModLog.Info("Props: " + comp.Name + " now shows the " + (IsM4(want) ? "M4 Carbine" : "militia rifle") + ".");
        }

        bool m4 = IsM4(want);
        float scale = m4 ? RifleConfig.C.CustomScale : 1f;

        if (MathF.Abs(comp.Scale - scale) > 0.001f)
        {
            comp.SetScale(scale);
        }

        var bt = m4 ? RSkeletalMeshComponent_Export.ESkeletalMeshComponentBoundsType.SMCBT_ReferencePose : RSkeletalMeshComponent_Export.ESkeletalMeshComponentBoundsType.SMCBT_Automatic;

        if (comp.BoundsType != bt)
        {
            comp.BoundsType = bt;
        }

        RiflePaint.Apply(comp, m4);
    }

    public static SkeletalMesh? RifleMesh;
    public static AkEvent? ShotEvent, DryEvent, ReloadEvent;
    public static AkBank? GunBank;
    private static bool bankRequested, audioReady;
    private static float nextAudioCheck;
    private static int audioChecks;

    public static void EnsureAudio()
    {
        if (audioReady || Util.Now < nextAudioCheck)
        {
            return;
        }

        nextAudioCheck = Util.Now + 2f;
        audioChecks++;

        try
        {
            if (GunBank != null && GunBank.IsValid && !bankRequested)
            {
                bankRequested = true;
                bool ok = GunBank.LoadBank(false);

                GunBank.PreloadBankStreams(true, AkWwise.EAkIOStreamPriorities.AKIO_PRIORITY_DEFAULT);
                ModLog.Info("Props: asked Wwise to load the thug gun sound bank (" + ok + ", complete " + GunBank.IsBankLoadComplete() + ").");
            }

            bool all = true;
            string state = "";

            foreach (AkEvent? e in new[] { ShotEvent, DryEvent, ReloadEvent })
            {
                if (e == null || !e.IsValid)
                {
                    continue;
                }

                bool loaded = e.IsAssetBankLoaded();

                if (!loaded)
                {
                    e.RequestAssetBankLoad();
                    all = false;
                }

                state += e.Name + "=" + loaded + " ";
            }

            if (all || audioChecks >= 15)
            {
                audioReady = true;
                ModLog.Info("Props: rifle sounds " + (all ? "ready" : "still not reported loaded after 30 s") + " (" + state.Trim() + ", bank complete " + (GunBank != null && GunBank.IsValid && GunBank.IsBankLoadComplete()) + ").");
            }
            else if (audioChecks == 1)
            {
                ModLog.Info("Props: requested the rifle sounds' banks (" + state.Trim() + ").");
            }
        }
        catch (Exception exception)
        {
            audioReady = true;
            ModLog.Warn("Props: loading the rifle sounds failed: " + Util.Unwrap(exception));
        }
    }
    private const string AudioGunClass = "BmGame.RBMWeaponCombatRifleBase";
    private static RBMWeaponRanged? audioGun;
    private static RPawnPlayer? audioGunPawn;
    private static float nextAudioGunTry;

    public static void KeepAudioGun(RPawnPlayer pawn)
    {
        try
        {
            if (audioGun != null && audioGun.IsValid && audioGunPawn != null && audioGunPawn.Ptr == pawn.Ptr)
            {
                audioGun.SetLocation(pawn.Location + new Vector3(0f, 0f, 60f));

                return;
            }

            if (audioGun != null)
            {
                DropAudioGun(true);
            }

            if (Util.Now < nextAudioGunTry)
            {
                return;
            }

            nextAudioGunTry = Util.Now + 5f;

            Class? cls = Game.FindObject<Class>(AudioGunClass);

            if (cls == null)
            {
                ModLog.Once("audiogun-noclass", "Props: " + AudioGunClass + " not found; the shot sound may stay silent.");

                return;
            }

            audioGun = pawn.Spawn(cls, pawn, default, pawn.Location + new Vector3(0f, 0f, 60f), pawn.Rotation, null!, true) as RBMWeaponRanged;

            if (audioGun == null)
            {
                ModLog.Once("audiogun-spawn", "Props: spawning the thug rifle sound actor failed; the shot sound may stay silent.");

                return;
            }

            audioGunPawn = pawn;
            audioGun.SetHidden(true);
            audioGun.SetCollision(false, false, true);
            audioGun.SetTickIsDisabled(true);
            ModLog.Info("Props: spawned a hidden thug rifle (" + audioGun.Name + ") so the rifle's shot sound plays.");
        }
        catch (Exception exception)
        {
            audioGun = null;
            audioGunPawn = null;
            ModLog.Once("audiogun-failed", "Props: the thug rifle sound actor failed: " + Util.Unwrap(exception));
        }
    }

    public static void DropAudioGun(bool destroy)
    {
        RBMWeaponRanged? gun = audioGun;
        RPawnPlayer? owner = audioGunPawn;

        audioGun = null;
        audioGunPawn = null;

        if (!destroy || gun == null)
        {
            return;
        }

        try
        {
            if (owner != null && owner.IsValid && gun.IsValid)
            {
                gun.Destroy();
            }
        }
        catch
        {
        }
    }

    public static ParticleSystem? FlashPS, ImpactPS;
    public static float FlashScaleFix = 1f;
    public static AnimSequence? AimSeq, FireSeq, ReloadSeq;

    private static bool assetsTried;

    private static SkeletalMeshComponent? hand;
    private static SkeletalMeshComponent? back;
    private static bool handShown, backShown;
    private static IntPtr compPawn;
    private static bool batHidden;
    private static float pitchAdjust;

    public static SkeletalMeshComponent? Hand => hand != null && hand.IsValid ? hand : null;

    public static bool HandShown => handShown && Hand != null;

    public static void Reset()
    {
        hand = null;
        back = null;
        handShown = backShown = false;
        compPawn = IntPtr.Zero;
        rightAxis = -1;
        batHidden = false;
        pitchAdjust = 0f;
    }

    private static T? Keep<T>(string path) where T : GameObject
    {
        T? o = Game.FindObject<T>(path);

        o?.AddToRoot();

        if (o == null)
        {
            ModLog.Info("Props: asset " + path + " not found.");
        }

        return o;
    }

    private static AnimSequence? Seq(AnimSet? set, string name)
    {
        if (set == null)
        {
            return null;
        }

        foreach (AnimSequence s in set.Sequences)
        {
            if (s != null && s.SequenceName.ToString() == name)
            {
                s.AddToRoot();

                return s;
            }
        }

        ModLog.Info("Props: animation " + name + " not found.");

        return null;
    }

    public static bool EnsureAssets()
    {
        if (assetsTried)
        {
            return RifleMesh != null && RifleMesh.IsValid;
        }

        assetsTried = true;

        RifleMesh = Keep<SkeletalMesh>(MeshPath);

        if (RifleMesh?.Materials != null)
        {
            foreach (MaterialInterface mat in RifleMesh.Materials)
            {
                mat?.AddToRoot();
            }
        }

        ShotEvent = Keep<AkEvent>("Ww_WPN_EnemyGuns.Play_Enemy_Machine_Gun_OneShot");
        DryEvent = Keep<AkEvent>("Ww_WPN_EnemyGuns.Play_Enemy_DryFire");
        GunBank = Keep<AkBank>("Ww_WPN_EnemyGuns.WPN_EnemyGuns");
        ReloadEvent = FindReloadEvent();

        AnimSet? set = Keep<AnimSet>(AnimSetPath);

        AimSeq = Seq(set, "Stand_Aiming_Idle");
        FireSeq = Seq(set, "Stand_Aiming_Fire_Auto");
        ReloadSeq = Seq(set, "Reload");

        ImpactPS = Keep<ParticleSystem>("PFX_RedHood.Emitter.VFX_BulletRicochet");
        LoadFlash();

        ModLog.Info("Props: assets (rifle " + Ok(RifleMesh) + ", shot sound " + Ok(ShotEvent) + ", reload sound " + (ReloadEvent != null ? ReloadEvent.GetPathName(null) : "none") + ", flash " + (FlashPS != null ? FlashPS.GetPathName(null) : "none") +
            ", anims aim " + Ok(AimSeq) + " fire " + Ok(FireSeq) + " reload " + Ok(ReloadSeq) + ").");

        return RifleMesh != null;
    }

    private static string Ok(GameObject? o) => o != null ? "ok" : "MISSING";

    private static string flashFor = "";

    public static void LoadFlash()
    {
        string want = RifleConfig.C.MuzzleFx;

        if (want == flashFor && FlashPS != null)
        {
            return;
        }

        flashFor = want;
        FlashPS = null;
        FlashScaleFix = 1f;

        if (want == "redhood")
        {
            FlashPS = Game.FindObject<ParticleSystem>("PFX_RedHood.Emmiter.RedHood_GUNS_muzzleflash");
        }

        if (FlashPS == null)
        {
            try
            {
                if (Game.FindObject<RBMWeaponRanged>("BmGame.Default__RBMWeaponCombatRifle") is RBMWeaponRanged w && w.MuzzleFlash?.Template is ParticleSystem ps)
                {
                    FlashPS = ps;
                }
            }
            catch
            {
            }

            FlashPS ??= Game.FindObject<ParticleSystem>("VFX_Weapon.Particles.MasterMuzzleFire");
            FlashScaleFix = 0.6f;
        }

        FlashPS?.AddToRoot();
    }

    private static AkEvent? FindReloadEvent()
    {
        AkEvent? best = null;
        int bestScore = 0;

        try
        {
            foreach (AkEvent e in Game.FindObjects<AkEvent>())
            {
                string n = e.GetPathName(null);

                if (n.IndexOf("Reload", StringComparison.OrdinalIgnoreCase) < 0 || n.IndexOf("Play_", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                int score = 1;

                if (n.IndexOf("Enemy", StringComparison.OrdinalIgnoreCase) >= 0) score += 4;
                if (n.IndexOf("Rifle", StringComparison.OrdinalIgnoreCase) >= 0) score += 2;
                if (n.IndexOf("Machine", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Assault", StringComparison.OrdinalIgnoreCase) >= 0) score += 3;
                if (n.IndexOf("Sniper", StringComparison.OrdinalIgnoreCase) >= 0) score -= 1;

                ModLog.Info("Props: reload sound candidate " + n + " (" + score + ").");

                if (score > bestScore)
                {
                    bestScore = score;
                    best = e;
                }
            }
        }
        catch (Exception exception)
        {
            ModLog.Info("Props: scanning reload sounds failed: " + Util.Unwrap(exception));
        }

        best?.AddToRoot();

        return best;
    }

    public static Vector3 Muzzle(Pawn pawn)
    {
        SkeletalMeshComponent? m = Hand;

        if (m != null && handShown)
        {
            if (!IsM4(m.SkeletalMesh))
            {
                m.GetSocketWorldLocationAndRotation(new FName("DummyFlare"), out Vector3 fl, out Rotator _, 0);

                if (fl != Vector3.Zero)
                {
                    return fl;
                }
            }

            Vector3 ml = ActiveMuzzleLocal(m);

            Util.Basis(m.GetRotation(), out Vector3 x, out Vector3 y, out Vector3 z);

            return m.GetPosition() + x * ml.X + y * ml.Y + z * ml.Z;
        }

        Vector3 r = pawn.Mesh?.GetBoneLocation(new FName("Bip01_R_Hand"), 0) ?? Vector3.Zero;

        return r == Vector3.Zero ? pawn.Location + new Vector3(0, 0, 40) : r;
    }

    public static void Deactivate(RPawnPlayer pawn, RBatarang? bat)
    {
        try
        {
            SkeletalMeshComponent? body = pawn.Mesh;

            if (body != null && body.IsValid)
            {
                if (hand != null && hand.IsValid) body.DetachComponent(hand);
                if (back != null && back.IsValid) body.DetachComponent(back);
            }

            RestoreBat(bat);
        }
        catch
        {
        }

        Reset();
    }

    private static void RestoreBat(RBatarang? bat)
    {
        if (!batHidden || bat == null || !bat.IsValid)
        {
            batHidden = false;

            return;
        }

        batHidden = false;

        try
        {
            if (Convert.ToInt32(bat.GadgetState) == 1 && bat.Mesh != null && bat.Mesh.HiddenGame)
            {
                bat.Mesh.SetHidden(false);
            }
        }
        catch
        {
        }
    }

    public static void Update(RPawnPlayer pawn, RBatarang bat, bool rifleMode, bool inHand, Vector3? aimPoint, float dt)
    {
        SkeletalMeshComponent? body = pawn.Mesh;

        if (body == null || !body.IsValid)
        {
            return;
        }

        if (compPawn != pawn.Ptr)
        {
            Reset();
            compPawn = pawn.Ptr;
            RemoveStale(body);
        }

        RifleConfig.Settings c = RifleConfig.C;

        if ((rifleMode || c.BackShow) && !EnsureAssets())
        {
            return;
        }

        LoadFlash();
        EnsureAudio();
        LoadM4();

        if (rifleMode && inHand)
        {
            if (bat.Mesh != null && !bat.Mesh.HiddenGame)
            {
                bat.Mesh.SetHidden(true);
            }

            batHidden = true;
        }
        else if (batHidden)
        {
            RestoreBat(bat);
        }

        UpdateHand(pawn, body, rifleMode && inHand, aimPoint, dt);
        UpdateBack(pawn, body, c.BackShow && !(rifleMode && inHand));
    }

    private static void RemoveStale(SkeletalMeshComponent body)
    {
        var stale = new List<ActorComponent>();

        foreach (var a in body.Attachments)
        {
            if (a.Component != null && a.Component.GetPathName(null).Contains(CompPrefix))
            {
                stale.Add(a.Component);
            }
        }

        foreach (ActorComponent comp in stale)
        {
            body.DetachComponent(comp);
        }

        if (stale.Count > 0)
        {
            ModLog.Info("Props: removed " + stale.Count + " leftover rifle component(s) from a script reload.");
        }
    }

    private static SkeletalMeshComponent? Make(RPawnPlayer pawn, SkeletalMeshComponent body, string bone, string tag)
    {
        SkeletalMesh? mesh = CurrentMesh;

        if (mesh == null)
        {
            return null;
        }

        var comp = GameObject.ConstructObject<SkeletalMeshComponent>(pawn, CompPrefix + tag + "_" + Environment.TickCount, (GameObject.EObjectFlags)0, null!);

        comp.SetSkeletalMesh(mesh, false);
        comp.SetLightingChannels(body.LightingChannels);
        body.AttachComponent(comp, new FName(bone), Vector3.Zero, new Rotator(0, 0, 0), Vector3.One);

        return comp;
    }

    private static Vector3 BoneAxis(SkeletalMeshComponent m, string bone, GameObject.EAxis ax)
    {
        Vector3 v = m.GetBoneAxis(new FName(bone), ax);

        return v == Vector3.Zero ? v : Vector3.Normalize(v);
    }

    private static void UpdateHand(RPawnPlayer pawn, SkeletalMeshComponent body, bool show, Vector3? aimPoint, float dt)
    {
        RifleConfig.Settings c = RifleConfig.C;

        if (hand == null)
        {
            if (!show)
            {
                return;
            }

            hand = Make(pawn, body, c.HandBone, "Hand");

            if (hand == null)
            {
                return;
            }

            handShown = true;
            pitchAdjust = 0f;
            ModLog.Info("Props: rifle in Batman's hands (" + c.HandBone + ").");
        }

        if (show != handShown)
        {
            hand.SetHidden(!show);
            handShown = show;
            pitchAdjust = 0f;
        }

        if (!show)
        {
            return;
        }

        ApplyLook(hand);

        bool m4 = IsM4(hand.SkeletalMesh);
        Vector3 handOffset = m4 ? c.M4HandOffset : c.HandOffset;
        Rotator handRot = m4 ? c.M4HandRot : c.HandRot;
        Vector3 ml = ActiveMuzzleLocal(hand);

        hand.SetTranslation(handOffset);

        Vector3 xb = BoneAxis(body, c.HandBone, GameObject.EAxis.AXIS_X);
        Vector3 yb = BoneAxis(body, c.HandBone, GameObject.EAxis.AXIS_Y);
        Vector3 zb = BoneAxis(body, c.HandBone, GameObject.EAxis.AXIS_Z);

        if (xb == Vector3.Zero || c.PitchFix <= 0f || aimPoint == null)
        {
            pitchAdjust = 0f;
            hand.SetRotation(handRot);

            return;
        }

        Util.Basis(handRot, out Vector3 lx, out Vector3 ly, out Vector3 lz);

        Vector3 wx = Util.ToWorld(lx, xb, yb, zb);
        Vector3 wy = Util.ToWorld(ly, xb, yb, zb);
        Vector3 wz = Util.ToWorld(lz, xb, yb, zb);
        Vector3 grip = body.GetBoneLocation(new FName(c.HandBone), 0) + Util.ToWorld(handOffset, xb, yb, zb);
        Vector3 muzzle = grip + wx * ml.X + wz * ml.Z;
        Vector3 want = aimPoint.Value - muzzle;

        if (want.LengthSquared() < 400f * 400f)
        {
            want = Vector3.Normalize(want) * 400f;
        }

        float cur = MathF.Asin(Math.Clamp(wx.Z, -1f, 1f));
        float tgt = MathF.Asin(Math.Clamp(Vector3.Normalize(want).Z, -1f, 1f));
        float residual = Math.Clamp((tgt - cur) * c.PitchFix, -0.45f, 0.45f);

        pitchAdjust += (residual - pitchAdjust) * Math.Min(1f, dt * 12f);

        Vector3 axis = Vector3.Cross(wx, Vector3.UnitZ);

        if (axis.LengthSquared() < 1e-4f)
        {
            hand.SetRotation(handRot);

            return;
        }

        axis = Vector3.Normalize(axis);

        Vector3 k = axis * pitchAdjust;

        wx = Rodrigues(wx, k);
        wy = Rodrigues(wy, k);
        wz = Rodrigues(wz, k);

        hand.SetRotation(Util.FromBasis(Util.ToLocal(wx, xb, yb, zb), Util.ToLocal(wy, xb, yb, zb), Util.ToLocal(wz, xb, yb, zb)));
    }

    private static Vector3 Rodrigues(Vector3 v, Vector3 axisAngle)
    {
        float a = axisAngle.Length();

        if (a < 1e-5f)
        {
            return v;
        }

        Vector3 k = axisAngle / a;
        float cs = MathF.Cos(a), sn = MathF.Sin(a);

        return v * cs + Vector3.Cross(k, v) * sn + k * Vector3.Dot(k, v) * (1 - cs);
    }

    public static float AutoBack = 26f, AutoUp = 2f, AutoRight = 0f, AutoSide = 0.55f;

    private static void UpdateBack(RPawnPlayer pawn, SkeletalMeshComponent body, bool show)
    {
        RifleConfig.Settings c = RifleConfig.C;

        if (back == null)
        {
            if (!show)
            {
                return;
            }

            back = Make(pawn, body, c.BackBone, "Back");

            if (back == null)
            {
                return;
            }

            backShown = true;
            ModLog.Info("Props: rifle on the back (" + c.BackBone + ").");
        }

        if (show != backShown)
        {
            back.SetHidden(!show);
            backShown = show;
        }

        if (!show)
        {
            return;
        }

        if (c.BackAuto)
        {
            AutoPose(pawn, body, c);
        }

        ApplyLook(back);

        bool m4 = IsM4(back.SkeletalMesh);

        back.SetTranslation((m4 ? c.M4BackOffset : c.BackOffset) + RightShift(pawn, body, c));
        back.SetRotation(m4 ? c.M4BackRot : c.BackRot);
    }

    private static int rightAxis = -1;
    private static float rightSign = 1f;

    private static Vector3 RightShift(RPawnPlayer pawn, SkeletalMeshComponent m, RifleConfig.Settings c)
    {
        if (c.BackRight == 0f)
        {
            return Vector3.Zero;
        }

        if (rightAxis < 0)
        {
            Vector3[] axes =
            {
                BoneAxis(m, c.BackBone, GameObject.EAxis.AXIS_X),
                BoneAxis(m, c.BackBone, GameObject.EAxis.AXIS_Y),
                BoneAxis(m, c.BackBone, GameObject.EAxis.AXIS_Z),
            };

            if (axes[0] == Vector3.Zero)
            {
                return Vector3.Zero;
            }

            double yaw = pawn.Rotation.Yaw * Math.PI / 180;
            var right = new Vector3((float)-Math.Sin(yaw), (float)Math.Cos(yaw), 0);
            float best = 0f;

            for (int i = 0; i < 3; i++)
            {
                float d = Vector3.Dot(axes[i], right);

                if (MathF.Abs(d) > MathF.Abs(best))
                {
                    best = d;
                    rightAxis = i;
                }
            }

            rightSign = best >= 0f ? 1f : -1f;
            ModLog.Info("Props: Batman's right on " + c.BackBone + " = " + (rightSign > 0 ? "+" : "-") + "XYZ"[rightAxis] + " (match " + MathF.Abs(best).ToString("0.00", Util.Inv) + ").");
        }

        Vector3 unit = rightAxis == 0 ? Vector3.UnitX : rightAxis == 1 ? Vector3.UnitY : Vector3.UnitZ;

        return unit * rightSign * c.BackRight;
    }

    private static void AutoPose(RPawnPlayer pawn, SkeletalMeshComponent m, RifleConfig.Settings c)
    {
        Vector3 xb = BoneAxis(m, c.BackBone, GameObject.EAxis.AXIS_X);
        Vector3 yb = BoneAxis(m, c.BackBone, GameObject.EAxis.AXIS_Y);
        Vector3 zb = BoneAxis(m, c.BackBone, GameObject.EAxis.AXIS_Z);

        if (xb == Vector3.Zero)
        {
            return;
        }

        double yaw = pawn.Rotation.Yaw * Math.PI / 180;
        var fwd = new Vector3((float)Math.Cos(yaw), (float)Math.Sin(yaw), 0);
        var right = new Vector3((float)-Math.Sin(yaw), (float)Math.Cos(yaw), 0);
        Vector3 up = Vector3.UnitZ;
        Vector3 xW = Vector3.Normalize(up + right * AutoSide);
        Vector3 zW = -fwd;

        zW = Vector3.Normalize(zW - xW * Vector3.Dot(zW, xW));

        Vector3 yW = Vector3.Cross(zW, xW);
        Vector3 centre = -fwd * AutoBack + up * AutoUp + right * AutoRight;
        Vector3 offW = centre - xW * RifleCentre;

        c.BackOffset = Util.ToLocal(offW, xb, yb, zb);
        c.BackRot = Util.FromBasis(Util.ToLocal(xW, xb, yb, zb), Util.ToLocal(yW, xb, yb, zb), Util.ToLocal(zW, xb, yb, zb));
        c.BackAuto = false;
        RifleConfig.Save();
        ModLog.Info("Props: fitted the rifle across Batman's back: offset " + Util.V(c.BackOffset) + ", rotation " + Util.R(c.BackRot) + " (saved).");
    }

    public static string Report(RPawnPlayer pawn)
    {
        string s = "model " + (M4Active ? "M4" : "thug") + " (paint " + RiflePaint.State + "), hand " + (hand == null ? "none" : handShown ? "shown" : "hidden") + ", back " + (back == null ? "none" : backShown ? "shown" : "hidden") + ", pitch fix " + (pitchAdjust * 57.3f).ToString("0.0", Util.Inv) + " deg";

        if (hand != null && handShown && pawn.Mesh != null)
        {
            s += ", muzzle " + Util.V(Muzzle(pawn) - pawn.Location);
        }

        foreach (var (tag, comp) in new[] { ("hand", hand), ("back", back) })
        {
            if (comp == null || !comp.IsValid)
            {
                continue;
            }

            try
            {
                var b = comp.Bounds;

                s += " | " + tag + " mesh " + (comp.SkeletalMesh != null ? comp.SkeletalMesh.Name.ToString() : "none") + " hidden " + comp.HiddenGame + " scale " + comp.Scale.ToString("0.##", Util.Inv) +
                    " boundsType " + comp.BoundsType + " pos " + Util.V(comp.GetPosition() - pawn.Location) + " bounds " + Util.V(b.Origin - pawn.Location) + " ext " + Util.V(b.BoxExtent) +
                    " lastRender " + (pawn.WorldInfo != null ? (pawn.WorldInfo.TimeSeconds - comp.LastRenderTime).ToString("0.00", Util.Inv) : "?");
            }
            catch (Exception e)
            {
                s += " | " + tag + " report failed: " + e.Message;
            }
        }

        return s;
    }
}
