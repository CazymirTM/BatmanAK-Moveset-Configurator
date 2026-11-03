#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace BatmanDeathstrokeSniper;

internal static class SniperProps
{
    private const string CompPrefix = "BDSniper_";

    public static SkeletalMesh? SniperMesh;
    public static SkeletalMesh? ArkhamRifle;

    private const string ArkhamPackage = "JokerBoss_B1_Cine_Ch9r_PreEnd";
    private const string ArkhamMeshPath = "Red_Hood_Weaponry.Mesh.Red_Hood_Weaponry_SNIPER";
    private static bool arkhamTried;

    public static SkeletalMesh? RifleSkel => SniperConfig.C.ArkhamRifle && ArkhamRifle != null && ArkhamRifle.IsValid ? ArkhamRifle : SniperMesh;
    public static AkEvent? FireEvent, ReloadEvent, OneShotEvent, DryFireEvent, DisDryEvent, LaserEvent;
    public static ParticleSystem? LaserPS;
    public static ParticleSystem? MuzzlePS, RicochetPS;
    public static float MuzzleScaleFix = 1f;

    private static ParticleSystem? ThugSniperFlash()
    {
        foreach (string path in new[] { "BmScript.Default__RBMWeaponSniper", "BmGame.Default__RBMWeaponSniperBase" })
        {
            try
            {
                if (Game.FindObject<RBMWeaponRanged>(path) is RBMWeaponRanged w && w.MuzzleFlash is ParticleSystemComponent psc && psc.Template is ParticleSystem ps)
                {
                    ps.AddToRoot();
                    ModLog.Info("Props: muzzle flash from the thug sniper rifle: " + ps.GetPathName(null) + " (socket " + w.MuzzleFlashSocket + ").");

                    return ps;
                }

                ModLog.Info("Props: no muzzle flash on " + path + ".");
            }
            catch (Exception exception)
            {
                ModLog.Info("Props: reading " + path + " failed: " + Util.Unwrap(exception));
            }
        }

        return null;
    }

    private static bool assetsLoaded;

    private static SkeletalMesh? origMesh;
    private static IntPtr origFor;
    private static bool swapped;

    private const string MeshKey = "BatmanSniper.DisruptorMesh";
    private static float nextRestoreWarn;

    private static SkeletalMesh? FindDisruptorMesh(RDisruptorSniper dis)
    {
        try
        {
            if (dis.ObjectArchetype is RDisruptorSniper arch && arch.Mesh?.SkeletalMesh is SkeletalMesh am && !IsRifleMesh(am))
            {
                ModLog.Once("arch-mesh", "Props: the Disruptor model from its archetype: " + am.GetPathName(null) + ".");

                return am;
            }

            if (AppDomain.CurrentDomain.GetData(MeshKey) is string path && path.Length > 0)
            {
                SkeletalMesh? byPath = Game.FindObject<SkeletalMesh>(path);

                if (byPath != null)
                {
                    return byPath;
                }
            }

            foreach (SkeletalMesh sm in Game.FindObjects<SkeletalMesh>())
            {
                string n = sm.GetPathName(null);

                if (n.IndexOf("Disruptor", StringComparison.OrdinalIgnoreCase) >= 0 && n.IndexOf("Drone", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    ModLog.Info("Props: found the Disruptor model by name: " + n + ".");
                    try { AppDomain.CurrentDomain.SetData(MeshKey, n); } catch { }

                    return sm;
                }
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("find-mesh", "Props: searching the Disruptor model failed: " + Util.Unwrap(exception));
        }

        return null;
    }

    private static PrimitiveComponent? back;
    private static bool backShown;
    private static IntPtr backMesh;
    private static Vector3 prevPos, vel, acc, sway, swayVel;
    private static bool hasPrev;

    private static IntPtr compPawn;

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

    public static void EnsureAssets()
    {
        if (assetsLoaded)
        {
            return;
        }

        assetsLoaded = true;

        try
        {
            Game.LoadPackage("StreamedWeapon_CombatSniper_SF.upk");
        }
        catch (Exception exception)
        {
            ModLog.Warn("Props: loading the sniper rifle package failed: " + Util.Unwrap(exception));
        }

        SniperMesh = Keep<SkeletalMesh>("Sniper_Rifle.Mesh.Sniper_Rifle_3P");
        FireEvent = Keep<AkEvent>("Ww_WPN_SniperRifle.Play_SniperRifleFire");
        ReloadEvent = Keep<AkEvent>("Ww_WPN_SniperRifle.Play_SniperRifleReload");
        Keep<AkBank>("Ww_WPN_SniperRifle.WPN_SniperRifle");
        OneShotEvent = Keep<AkEvent>("Ww_WPN_EnemyGuns.Play_Enemy_Machine_Gun_OneShot");
        DryFireEvent = Keep<AkEvent>("Ww_WPN_EnemyGuns.Play_Enemy_DryFire");
        DisDryEvent = Keep<AkEvent>("ww_chr_batmandisruptor.Play_Disruptor_DryFire");
        LaserEvent = Keep<AkEvent>("Ww_WPN_SniperRifle.Play_SniperRifleLaser");
        LaserPS = Keep<ParticleSystem>("VFX_Weapon.FX_SniperBeam_p");
        MuzzlePS = Keep<ParticleSystem>("PFX_RedHood.Emmiter.RedHood_GUNS_muzzleflash");
        RicochetPS = Keep<ParticleSystem>("PFX_RedHood.Emitter.VFX_BulletRicochet");

        MuzzleScaleFix = 1f;

        if (MuzzlePS == null)
        {
            MuzzlePS = ThugSniperFlash();

            if (MuzzlePS != null)
            {
                MuzzleScaleFix = 0.5f;
            }
        }

        if (MuzzlePS == null)
        {
            MuzzlePS = Keep<ParticleSystem>("VFX_Weapon.Particles.MasterMuzzleFire") ?? Keep<ParticleSystem>("VFX_Weapon.Particles.Muzzle_Flash_MASTER");

            if (MuzzlePS != null)
            {
                MuzzleScaleFix = 0.3f;
            }
        }

        LoadArkham();

        ModLog.Info("Props: assets loaded (sniper mesh " + (SniperMesh != null ? "ok" : "MISSING") + ", Arkham Knight's rifle " + (ArkhamRifle != null ? "ok" : "not available") + ", laser " + (LaserPS != null ? "ok" : "MISSING") +
            ", fire sound " + (FireEvent != null ? "ok" : "MISSING") + ", muzzle flash " + (MuzzlePS != null ? "ok" : "MISSING") + ").");
    }

    private static void LoadArkham()
    {
        if (!SniperConfig.C.ArkhamRifle || (ArkhamRifle != null && ArkhamRifle.IsValid))
        {
            return;
        }

        ArkhamRifle = Game.FindObject<SkeletalMesh>(ArkhamMeshPath);

        if (ArkhamRifle == null && !arkhamTried)
        {
            arkhamTried = true;

            try
            {
                string file = System.IO.Path.Combine(BmSDK.Framework.FileUtils.GetGamePath(), "CookedPCConsole", ArkhamPackage + ".upk");

                if (!System.IO.File.Exists(file))
                {
                    ModLog.Info("Props: " + ArkhamPackage + ".upk not found; using the militia sniper rifle model instead.");

                    return;
                }

                // The Arkham Knight's rifle only ships inside two story levels; this cinematic package is the small one (24 MB, loads in ~0.15 s).
                Package? p = Game.LoadPackage(ArkhamPackage);

                ModLog.Info("Props: LoadPackage(" + ArkhamPackage + ") -> " + (p != null && p.IsValid ? "loaded" : "null") + ".");
            }
            catch (Exception exception)
            {
                ModLog.Warn("Props: loading the Arkham Knight's rifle failed (falling back to the militia rifle): " + Util.Unwrap(exception));
            }

            ArkhamRifle = Game.FindObject<SkeletalMesh>(ArkhamMeshPath);
        }

        if (ArkhamRifle != null)
        {
            ArkhamRifle.AddToRoot();

            foreach (MaterialInterface mat in ArkhamRifle.Materials)
            {
                mat?.AddToRoot();
            }
        }
    }

    public static void Reset()
    {
        origMesh = null;
        origFor = IntPtr.Zero;
        swapped = false;
        back = null;
        backShown = false;
        hasPrev = false;
        compPawn = IntPtr.Zero;
    }

    public static void Deactivate(RPawnPlayer pawn, RDisruptorSniper dis)
    {
        UpdateGadgetMesh(dis, false);

        try
        {
            if (back != null && back.IsValid && pawn.Mesh != null && pawn.Mesh.IsValid)
            {
                pawn.Mesh.DetachComponent(back);
            }
        }
        catch
        {
        }

        Reset();
    }

    private static bool IsRifleMesh(SkeletalMesh? sm) => sm != null && sm.IsValid && (sm.Name.ToString() == "Sniper_Rifle_3P" || sm.Name.ToString() == "Red_Hood_Weaponry_SNIPER");

    private static bool SameMesh(SkeletalMesh? a, SkeletalMesh? b) => a != null && b != null && a.Ptr == b.Ptr;

    public static void Update(RPawnPlayer pawn, RDisruptorSniper dis, bool sniper, float dt)
    {
        SkeletalMeshComponent? body = pawn.Mesh;

        if (body == null)
        {
            return;
        }

        if (compPawn != pawn.Ptr)
        {
            Reset();
            compPawn = pawn.Ptr;
            RemoveStale(body);
        }

        if (sniper || SniperConfig.C.BackShow)
        {
            EnsureAssets();
            LoadArkham();
        }

        UpdateGadgetMesh(dis, sniper);
        UpdateBack(pawn, body, dis, sniper, dt);
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

        foreach (ActorComponent c in stale)
        {
            body.DetachComponent(c);
        }

        if (stale.Count > 0)
        {
            ModLog.Info("Props: removed " + stale.Count + " leftover rifle component(s) from a script reload.");
        }
    }

    private static void UpdateGadgetMesh(RDisruptorSniper dis, bool sniper)
    {
        SkeletalMeshComponent? m = dis.Mesh;

        if (m == null)
        {
            return;
        }

        SniperConfig.Settings c = SniperConfig.C;

        SkeletalMesh? rifle = RifleSkel;

        if (sniper && rifle != null)
        {
            if (!SameMesh(m.SkeletalMesh, rifle))
            {
                if (origMesh == null || origFor != dis.Ptr)
                {
                    origMesh = m.SkeletalMesh;
                    origFor = dis.Ptr;

                    if (origMesh != null)
                    {
                        try { AppDomain.CurrentDomain.SetData(MeshKey, origMesh.GetPathName(null)); } catch { }
                    }
                }

                if (origMesh != null && IsRifleMesh(origMesh))
                {
                    origMesh = null;
                }

                m.SetSkeletalMesh(rifle, false);
                swapped = true;
                ModLog.Info("Props: Disruptor model swapped for the sniper rifle.");
            }

            m.SetTranslation(c.ArkhamRifle && SameMesh(rifle, ArkhamRifle) ? c.AkHandOffset : c.HandOffset);
            m.SetRotation(c.ArkhamRifle && SameMesh(rifle, ArkhamRifle) ? c.AkHandRot : c.HandRot);

            if (dis.Mesh2 != null && !dis.Mesh2.HiddenGame)
            {
                dis.Mesh2.SetHidden(true);
            }
        }
        else if (!sniper && (swapped || IsRifleMesh(m.SkeletalMesh)))
        {
            SkeletalMesh? restore = origMesh != null && origFor == dis.Ptr ? origMesh : FindDisruptorMesh(dis);

            if (restore == null)
            {
                if (Util.Now >= nextRestoreWarn)
                {
                    nextRestoreWarn = Util.Now + 30f;
                    ModLog.Warn("Props: the Disruptor's own model is unknown (lost by a script reload); it comes back after a checkpoint or save reload.");
                }

                return;
            }

            m.SetSkeletalMesh(restore, false);

            m.SetTranslation(Vector3.Zero);
            m.SetRotation(new Rotator(0, 0, 0));

            if (dis.Mesh2 != null)
            {
                dis.Mesh2.SetHidden(false);
            }

            if (Sniper.InHand(dis) && m.HiddenGame)
            {
                m.SetHidden(false);
            }

            swapped = false;
            ModLog.Info("Props: Disruptor model restored.");
        }
    }

    private static void UpdateBack(RPawnPlayer pawn, SkeletalMeshComponent body, RDisruptorSniper dis, bool sniper, float dt)
    {
        SniperConfig.Settings c = SniperConfig.C;
        bool show = c.BackShow && !(sniper && Sniper.InHand(dis));
        SkeletalMesh? sk = RifleSkel;
        IntPtr want = sk != null ? sk.Ptr : IntPtr.Zero;

        if (back != null && backMesh != want)
        {
            body.DetachComponent(back);
            back = null;
        }

        if (back == null)
        {
            if (!show || sk == null)
            {
                return;
            }

            var kc = GameObject.ConstructObject<SkeletalMeshComponent>(pawn, CompPrefix + "Back_" + Environment.TickCount, (GameObject.EObjectFlags)0, null!);

            kc.SetSkeletalMesh(sk, false);
            back = kc;
            backMesh = want;
            back.SetLightingChannels(body.LightingChannels);
            body.AttachComponent(back, new FName(c.BackBone), Vector3.Zero, new Rotator(0, 0, 0), Vector3.One);
            backShown = true;
            hasPrev = false;
            sway = swayVel = Vector3.Zero;
            ModLog.Info("Props: sniper rifle on the back (" + c.BackBone + ").");
        }

        if (show != backShown)
        {
            back.SetHidden(!show);
            backShown = show;
            hasPrev = false;
        }

        if (!show)
        {
            return;
        }

        if (c.BackAuto)
        {
            AutoPose(pawn, body, c);
        }

        Sway(body, c, dt);
    }

    public static float AutoBack = 24f, AutoUp = -8f, AutoRight = 6f, AutoSide = 0.6f;

    private static void AutoPose(RPawnPlayer pawn, SkeletalMeshComponent m, SniperConfig.Settings c)
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
        Vector3 offW = -fwd * AutoBack + up * AutoUp + right * AutoRight;
        Vector3 xW = Vector3.Normalize(up + right * AutoSide);
        Vector3 zW = -fwd;

        zW = Vector3.Normalize(zW - xW * Vector3.Dot(zW, xW));

        Vector3 yW = Vector3.Cross(zW, xW);

        c.BackOffset = ToLocal(offW, xb, yb, zb);
        c.BackRot = FromBasis(ToLocal(xW, xb, yb, zb), ToLocal(yW, xb, yb, zb), ToLocal(zW, xb, yb, zb));
        c.BackAuto = false;
        SniperConfig.Save();
        ModLog.Info("Props: fitted the rifle across Batman's back: offset " + Util.V(c.BackOffset) + ", rotation " + Util.R(c.BackRot) + " (saved).");
    }

    private const float SwayK = 70f, SwayD = 4.5f, SwayGain = 0.00018f, SwayMax = 0.35f;

    private static void Sway(SkeletalMeshComponent m, SniperConfig.Settings c, float dt)
    {
        Vector3 pos = m.GetBoneLocation(new FName(c.BackBone), 0);

        if (hasPrev && dt > 0.0001f)
        {
            Vector3 v = (pos - prevPos) / dt;

            if (v.Length() > 4000f)
            {
                vel = acc = Vector3.Zero;
            }
            else
            {
                Vector3 vs = Vector3.Lerp(vel, v, 0.35f);

                acc = Vector3.Lerp(acc, (vs - vel) / dt, 0.3f);
                vel = vs;
            }
        }

        prevPos = pos;
        hasPrev = true;

        Vector3 a = acc;

        if (a.Length() > 4000f)
        {
            a = Vector3.Normalize(a) * 4000f;
        }

        Vector3 target = Vector3.Cross(a, -Vector3.UnitZ) * SwayGain;

        if (target.Length() > SwayMax)
        {
            target = Vector3.Normalize(target) * SwayMax;
        }

        swayVel += (-SwayK * (sway - target) - SwayD * swayVel) * dt;
        sway += swayVel * dt;

        if (sway.Length() > SwayMax)
        {
            sway = Vector3.Normalize(sway) * SwayMax;
        }

        Vector3 xb = BoneAxis(m, c.BackBone, GameObject.EAxis.AXIS_X);
        Vector3 yb = BoneAxis(m, c.BackBone, GameObject.EAxis.AXIS_Y);
        Vector3 zb = BoneAxis(m, c.BackBone, GameObject.EAxis.AXIS_Z);

        if (xb == Vector3.Zero)
        {
            back!.SetTranslation(BackOff(c));
            back.SetRotation(BackRotFor(c));

            return;
        }

        Basis(BackRotFor(c), out Vector3 lx, out Vector3 ly, out Vector3 lz);

        Vector3 wx = Rodrigues(ToWorld(lx, xb, yb, zb), sway);
        Vector3 wy = Rodrigues(ToWorld(ly, xb, yb, zb), sway);
        Vector3 wz = Rodrigues(ToWorld(lz, xb, yb, zb), sway);

        back!.SetTranslation(BackOff(c));
        back.SetRotation(FromBasis(ToLocal(wx, xb, yb, zb), ToLocal(wy, xb, yb, zb), ToLocal(wz, xb, yb, zb)));
    }

    public static string Report(RPawnPlayer pawn, RDisruptorSniper dis)
    {
        string s = "mesh " + (dis.Mesh?.SkeletalMesh != null ? dis.Mesh.SkeletalMesh.Name.ToString() : "none") + (dis.Mesh != null && dis.Mesh.HiddenGame ? " (hidden)" : "") +
            ", back " + (back == null ? "none" : backShown ? "shown" : "hidden");

        if (back != null && pawn.Mesh != null)
        {
            s += " (" + Vector3.Distance(back.GetPosition(), pawn.Mesh.GetBoneLocation(new FName(SniperConfig.C.BackBone), 0)).ToString("0.0", Util.Inv) + " u from the bone)";
        }

        return s;
    }

    private static bool AkOnBack => ArkhamRifle != null && backMesh == ArkhamRifle.Ptr;

    private static Vector3 BackOff(SniperConfig.Settings c) => AkOnBack ? c.AkBackOffset : c.BackOffset;

    private static Rotator BackRotFor(SniperConfig.Settings c) => AkOnBack ? c.AkBackRot : c.BackRot;

    private static Vector3 BoneAxis(SkeletalMeshComponent m, string bone, GameObject.EAxis ax)
    {
        Vector3 v = m.GetBoneAxis(new FName(bone), ax);

        return v == Vector3.Zero ? v : Vector3.Normalize(v);
    }

    private static void Basis(Rotator r, out Vector3 x, out Vector3 y, out Vector3 z)
    {
        double P = r.Pitch * Math.PI / 180, Yw = r.Yaw * Math.PI / 180, Rl = r.Roll * Math.PI / 180;
        float SP = (float)Math.Sin(P), CP = (float)Math.Cos(P), SY = (float)Math.Sin(Yw), CY = (float)Math.Cos(Yw), SR = (float)Math.Sin(Rl), CR = (float)Math.Cos(Rl);

        x = new Vector3(CP * CY, CP * SY, SP);
        y = new Vector3(SR * SP * CY - CR * SY, SR * SP * SY + CR * CY, -SR * CP);
        z = new Vector3(-(CR * SP * CY + SR * SY), CY * SR - CR * SP * SY, CR * CP);
    }

    private static Rotator FromBasis(Vector3 x, Vector3 y, Vector3 z)
    {
        double pitch = Math.Atan2(x.Z, Math.Sqrt(x.X * x.X + x.Y * x.Y));
        double yaw = Math.Atan2(x.Y, x.X);
        var sy = new Vector3((float)-Math.Sin(yaw), (float)Math.Cos(yaw), 0);
        double roll = Math.Atan2(Vector3.Dot(z, sy), Vector3.Dot(y, sy));
        const double k = 180 / Math.PI;

        return new Rotator((float)(pitch * k), (float)(yaw * k), (float)(roll * k));
    }

    private static Vector3 Rodrigues(Vector3 v, Vector3 axisAngle)
    {
        float a = axisAngle.Length();

        if (a < 1e-5f)
        {
            return v;
        }

        Vector3 k = axisAngle / a;
        float c = (float)Math.Cos(a), s = (float)Math.Sin(a);

        return v * c + Vector3.Cross(k, v) * s + k * Vector3.Dot(k, v) * (1 - c);
    }

    private static Vector3 ToLocal(Vector3 w, Vector3 xb, Vector3 yb, Vector3 zb) => new Vector3(Vector3.Dot(w, xb), Vector3.Dot(w, yb), Vector3.Dot(w, zb));

    private static Vector3 ToWorld(Vector3 l, Vector3 xb, Vector3 yb, Vector3 zb) => xb * l.X + yb * l.Y + zb * l.Z;
}
