#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace BatmanDeathstrokeSniper;

internal static class SniperFire
{
    public static readonly string[] TrailTypes =
    {
        "",
        "VFX_Gadgets.FX_Trail_BatarangTrail_p|m",
        "VFX_Gadgets.Batarang.Batarang_Trail_UPGR|m",
        "VFX_Gadgets.fx_BatclawTrail_p|m",
        "DLC_VFX_Combat.Particles.VFX__RHood_Bullet_Trail_NEW|s",
        "VFX_Weapon.Particles.TracerFire|v",
        "VFX_Weapon.Particles.SentryGunBulletTrails|v",
        "VFX_Vehicles.Particles.Heli_Bullet_Trail_P|v",
        "VFX_Weapon.Turret.TurretBulletTrail|v",
        "VFX_Weapon.Particles.Shotgun_Tracer|v",
        "FFX_Combat.Particles.Trails.SloMo_Bullet_Trail|m",
        "VFX_Gadgets.DroneDisruptor.bullettrail|v",
        "VFX_Weapon.FX_SniperBeam_p|s",
        "VFX_Weapon.Particles.BulletBy|s",
    };

    private const float BulletSpeed = 40000f;

    private sealed class Bullet { public Emitter E = null!; public Vector3 From, To; public float T, Dur; }
    private static readonly List<Bullet> bullets = new List<Bullet>();
    private static readonly Dictionary<string, ParticleSystem?> trailCache = new Dictionary<string, ParticleSystem?>();
    private static int shotSeq;
    private static float nextNoise;
    private static int destroyLogs;

    public static void Reset()
    {
        flashes.Clear();
        checks.Clear();
        bullets.Clear();
        trailCache.Clear();
    }

    [Redirect(typeof(RDisruptorSniper), nameof(RDisruptorSniper.AimedFireDD))]
    private static int AimedFireDDRedirect(RDisruptorSniper self, RInventoryGadget.FHighlightedMesh target, Vector3 hitLoc)
    {
        if (!BatmanArsenalPack.Arsenal.Sniper)
        {
            return self.AimedFireDD(target, hitLoc);
        }

        RPawnPlayer? pawn = null;
        bool ours = false;

        try
        {
            ours = Sniper.Selected && Sniper.IsBatmansDisruptor(self, out pawn);
        }
        catch
        {
        }

        if (!ours || pawn == null)
        {
            return self.AimedFireDD(target, hitLoc);
        }

        try
        {
            if (!SniperAmmo.CanShoot(self, pawn))
            {
                return 0;
            }

            SniperAmmo.Consume();
            FireSniper(self, pawn, target);
            SignalShot(pawn);
        }
        catch (Exception exception)
        {
            ModLog.Warn("Sniper shot failed: " + Util.Unwrap(exception));
        }

        return 1 | 2;
    }

    [Redirect(typeof(RDisruptorSniper), nameof(RDisruptorSniper.StartDisruptorViewMode))]
    private static void StartDisruptorViewModeRedirect(RDisruptorSniper self)
    {
        if (!BatmanArsenalPack.Arsenal.Sniper)
        {
            self.StartDisruptorViewMode();
            return;
        }

        bool skip = false;

        try
        {
            skip = Sniper.Selected && SniperConfig.C.NoFilter && Sniper.IsBatmansDisruptor(self, out _);
        }
        catch
        {
        }

        if (!skip)
        {
            self.StartDisruptorViewMode();
        }
    }

    public static void KillFilter(RDisruptorSniper dis)
    {
        if (SniperConfig.C.NoFilter && dis.bPostProcessIsTurnedOn)
        {
            dis.KillDisruptorViewMode();
            ModLog.Once("filter-killed", "Fire: the Disruptor's black-and-white view was turned off for the sniper rifle.");
        }
    }

    private static void SignalShot(RPawnPlayer pawn)
    {
        try
        {
            AppDomain.CurrentDomain.SetData("DZAim.Shot", "sniper|" + (++shotSeq));
        }
        catch
        {
        }

        GunNoise(pawn);
    }

    private static void GunNoise(RPawnPlayer pawn)
    {
        int mode = SniperConfig.C.GunNoise;

        if (mode == 0 || Util.Now < nextNoise)
        {
            return;
        }

        nextNoise = Util.Now + 0.4f;

        try
        {
            if (Game.GetGameInfo() is not RGameInfo gi)
            {
                return;
            }

            if (mode == 2)
            {
                gi.AlertAllNearbyThugsToBM(pawn.Location);
            }
            else
            {
                gi.AIManager?.CreatePlayerNoise(pawn, 5000f);
                gi.AIManager?.TellBehavioursOfGadgetNoise(pawn.Location);
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("noise-failed", "Fire: gunshot noise failed: " + Util.Unwrap(exception));
        }
    }

    private static Vector3 Chest(Pawn v)
    {
        Vector3 c = v.Mesh?.GetBoneLocation(new FName("Bip01_Spine2"), 0) ?? Vector3.Zero;

        return c == Vector3.Zero ? v.Location : c;
    }

    private struct Hit { public bool Ok; public Vector3 Loc, Norm; public Actor? A; }

    private static Hit TraceHit(Actor tracer, Vector3 start, Vector3 end)
    {
        Actor? a = tracer.Trace(out Vector3 hl, out Vector3 hn, end, start, BmSDK.Component.ECollisionFilter.COF_TraceWorldActors, Vector3.Zero, out Actor.FTraceHitInfo _, 0);

        return a == null ? new Hit { Ok = false, Loc = end } : new Hit { Ok = true, Loc = hl, Norm = hn, A = a };
    }

    private static void FireSniper(RDisruptorSniper g, RPawnPlayer pawn, RInventoryGadget.FHighlightedMesh target)
    {
        SniperConfig.Settings c = SniperConfig.C;

        SniperProps.EnsureAssets();

        Rotator rot = g.GetAutoTargetRotation();
        Vector3 start = g.GetAutoTargetOrigin(rot);
        Vector3 dir = rot.ToDirection();

        RPawnVillain? victim = target.TargetActor as RPawnVillain;

        if (victim != null && !victim.IsAliveAndWell())
        {
            victim = null;
        }

        if (victim == null)
        {
            float best = float.MaxValue;

            for (Pawn? p = pawn.WorldInfo.PawnList; p != null; p = p.NextPawn)
            {
                if (p is not RPawnVillain v || !v.IsAliveAndWell())
                {
                    continue;
                }

                Vector3 to = Chest(v) - start;
                float along = Vector3.Dot(to, dir);

                if (along <= 0 || along > c.Range)
                {
                    continue;
                }

                float perp = (to - dir * along).Length();

                if (perp > c.AimRadius || along >= best)
                {
                    continue;
                }

                if (!pawn.FastTrace(Chest(v), start, Vector3.Zero, false))
                {
                    continue;
                }

                best = along;
                victim = v;
            }
        }

        Hit h = victim != null ? new Hit { Ok = false, Loc = Chest(victim) } : TraceHit(pawn, start, start + dir * c.Range);
        Vector3 end = h.Loc;
        Vector3 muzzle = Muzzle(g, pawn);

        ModLog.Info("Fire: shot from " + Util.V(start) + ", victim " + (victim != null ? Util.ClassName(victim) + " " + victim.Name : "none") + ", ammo " + SniperAmmo.Mag + "/" + SniperAmmo.MagSize + ".");

        AkEvent? fire = SniperProps.FireEvent ?? SniperProps.OneShotEvent;

        if (fire != null)
        {
            g.PlayGadgetSound(fire, false);
        }

        Emitter? flash = SpawnFx(pawn, SniperProps.MuzzlePS, muzzle, end - muzzle, c.MuzzleScale * SniperProps.MuzzleScaleFix);

        if (flash != null)
        {
            flashes.Add(new Flash { E = flash, G = g, P = pawn, Dir = Vector3.Normalize(end - muzzle) });
        }
        SparkFx(pawn, muzzle, end - muzzle);
        TrailFx(pawn, muzzle, end);

        if (h.Ok)
        {
            SparkFx(pawn, h.Loc, h.Norm);
        }
        else if (victim != null)
        {
            SparkFx(pawn, end, -dir);
        }

        if (victim != null)
        {
            Damage(pawn, victim, Vector3.Normalize(end - start));
        }

        try
        {
            DestroyHit(pawn, victim != null ? TraceHit(pawn, start, end) : h);
        }
        catch (Exception exception)
        {
            ModLog.Once("destroy-failed", "Fire: breaking objects failed: " + Util.Unwrap(exception));
        }

        try
        {
            HitDrones(pawn, start, dir, victim != null ? (end - start).Length() : c.Range, c.AimRadius);
        }
        catch (Exception exception)
        {
            ModLog.Once("drones-failed", "Fire: drone hit failed: " + Util.Unwrap(exception));
        }
    }

    internal static Vector3 Muzzle(RDisruptorSniper g, Pawn pawn)
    {
        SkeletalMeshComponent? m = g.Mesh;

        if (m != null)
        {
            m.GetSocketWorldLocationAndRotation(new FName("DummyFlare"), out Vector3 fl, out Rotator _, 0);

            if (fl != Vector3.Zero)
            {
                return fl;
            }

            m.GetSocketWorldLocationAndRotation(new FName("Muzzle"), out Vector3 mz, out Rotator _, 0);

            if (mz != Vector3.Zero)
            {
                return mz;
            }
        }

        return pawn.Location + new Vector3(0, 0, 40);
    }

    private static void Damage(RPawnPlayer pawn, RPawnVillain v, Vector3 dir)
    {
        SniperConfig.Settings c = SniperConfig.C;
        MDmgType_HandgunGadget def = MDmgType_HandgunGadget.DefaultObject;
        int hp = v.Health;

        var di = new RPawnCombat.FDamageInfo();

        di.DamageAmount = def.StrikeDamage * c.DamageMul;
        di.DamageType = MDmgType_HandgunGadget.StaticClass();
        di.StunTime = def.StunTime;
        di.Attacker = pawn;
        di.Impulse = dir * c.Impulse;
        di.BoneName = new FName("Bip01_Spine2");

        MarkVictim(v);
        SniperFear.Watch(pawn, v);

        object res = v.DamagedBy(di);

        checks.Add((v, Util.Now + 1.2f));

        ModLog.Info("Fire: hit " + Util.ClassName(v) + " hp " + hp + " -> " + v.Health + ", damage " + di.DamageAmount + " (" + res + ").");
    }

    private const string VictimsKey = "BatmanSniper.Victims";

    private static void MarkVictim(RPawnVillain v)
    {
        try
        {
            long now = Environment.TickCount64;
            var keep = new List<string> { v.Ptr.ToInt64() + "@" + now };

            if (AppDomain.CurrentDomain.GetData(VictimsKey) is string old)
            {
                foreach (string entry in old.Split(';'))
                {
                    int at = entry.IndexOf('@');

                    if (at > 0 && long.TryParse(entry.Substring(at + 1), out long t) && now - t <= 6000 && keep.Count < 32)
                    {
                        keep.Add(entry);
                    }
                }
            }

            AppDomain.CurrentDomain.SetData(VictimsKey, string.Join(";", keep));
        }
        catch
        {
        }
    }

    private static Emitter? SpawnFx(Actor ctx, ParticleSystem? ps, Vector3 at, Vector3 dir, float scale)
    {
        if (ps == null || scale <= 0f)
        {
            return null;
        }

        Emitter? em = ctx.Spawn(REmitter.StaticClass(), ctx, default, at, Rotator.FromDirection(dir), null!, true) as Emitter;

        if (em == null)
        {
            return null;
        }

        if (scale != 1f)
        {
            em.SetDrawScale(scale);
        }

        em.SetTemplate(ps, true);

        return em;
    }

    private static void SparkFx(Actor ctx, Vector3 at, Vector3 dir)
    {
        if (SniperConfig.C.SparkScale > 0f && dir != Vector3.Zero)
        {
            SpawnFx(ctx, SniperProps.RicochetPS, at, dir, SniperConfig.C.SparkScale);
        }
    }

    private static ParticleSystem? TrailPS(string path)
    {
        if (!trailCache.TryGetValue(path, out ParticleSystem? ps) || ps == null || !ps.IsValid)
        {
            ps = Game.FindObject<ParticleSystem>(path);
            ps?.AddToRoot();
            trailCache[path] = ps;
        }

        return ps;
    }

    private static void TrailFx(Actor ctx, Vector3 from, Vector3 to)
    {
        int type = SniperConfig.C.Trail;

        if (type <= 0 || type >= TrailTypes.Length)
        {
            return;
        }

        string[] parts = TrailTypes[type].Split('|');
        float sc = SniperConfig.C.TrailScale;
        Emitter? em = SpawnFx(ctx, TrailPS(parts[0]), from, to - from, sc);

        if (em == null)
        {
            return;
        }

        float dist = Vector3.Distance(from, to);

        if (parts[1] == "s")
        {
            em.SetFloatParameter(new FName("BeamLength"), dist / sc);
        }
        else if (parts[1] == "v")
        {
            bullets.Add(new Bullet { E = em, From = from, To = from, Dur = 0.15f });
        }
        else
        {
            bullets.Add(new Bullet { E = em, From = from, To = to, Dur = Math.Max(0.02f, dist / BulletSpeed) });
        }
    }

    private static readonly List<(RPawnVillain v, float at)> checks = new List<(RPawnVillain v, float at)>();

    public static void TickChecks()
    {
        for (int i = checks.Count - 1; i >= 0; i--)
        {
            if (Util.Now < checks[i].at)
            {
                continue;
            }

            RPawnVillain v = checks[i].v;

            checks.RemoveAt(i);

            try
            {
                if (v.IsValid)
                {
                    ModLog.Info("Fire: 1 s after the hit " + v.Name + " has hp " + v.Health + ", alive and well " + v.IsAliveAndWell() + ", physics " + v.Physics + ".");
                }
            }
            catch
            {
            }
        }
    }

    private const float FlashLife = 0.1f;

    private sealed class Flash { public Emitter E = null!; public RDisruptorSniper G = null!; public Pawn P = null!; public Vector3 Dir; public float T; }
    private static readonly List<Flash> flashes = new List<Flash>();

    private static void TickFlashes(float dt)
    {
        for (int i = flashes.Count - 1; i >= 0; i--)
        {
            Flash f = flashes[i];

            f.T += dt;

            try
            {
                if (f.T >= FlashLife || !f.G.IsValid || !f.P.IsValid)
                {
                    f.E.ParticleSystemComponent?.DeactivateSystem();
                    f.E.Destroy();
                    flashes.RemoveAt(i);

                    continue;
                }

                f.E.SetLocation(Muzzle(f.G, f.P));
            }
            catch
            {
                flashes.RemoveAt(i);
            }
        }
    }

    public static void TickBullets(float dt)
    {
        TickChecks();
        TickFlashes(dt);

        for (int i = bullets.Count - 1; i >= 0; i--)
        {
            Bullet b = bullets[i];

            b.T += dt;

            float k = Math.Min(1f, b.T / b.Dur);

            try
            {
                b.E.SetLocation(Vector3.Lerp(b.From, b.To, k));

                if (k >= 1f)
                {
                    b.E.ParticleSystemComponent?.DeactivateSystem();
                }
            }
            catch
            {
                k = 1f;
            }

            if (k >= 1f)
            {
                bullets.RemoveAt(i);
            }
        }
    }

    private static void HitDrones(RPawnPlayer pawn, Vector3 start, Vector3 dir, float range, float radius)
    {
        if (!SniperConfig.C.HitDrones)
        {
            return;
        }

        var hits = new List<BmSDK.BmScript.RPredatorDroneMini>();

        for (Pawn? p = pawn.WorldInfo.PawnList; p != null; p = p.NextPawn)
        {
            if (p is not BmSDK.BmScript.RPredatorDroneMini d || d.Health <= 0)
            {
                continue;
            }

            Vector3 to = d.Location - start;
            float along = Vector3.Dot(to, dir);

            if (along <= 0 || along > range || (to - dir * along).Length() > radius)
            {
                continue;
            }

            if (!pawn.FastTrace(d.Location, start, Vector3.Zero, false))
            {
                continue;
            }

            hits.Add(d);
        }

        foreach (BmSDK.BmScript.RPredatorDroneMini d in hits)
        {
            d.TakeDamageFromWeapon(1000, pawn.Controller, d.Location, -dir, dir * 1000f, MDmgType_HandgunGadget.StaticClass(), default, pawn, false);
            ModLog.Info("Fire: drone hit and destroyed.");
        }
    }

    private static void DestroyHit(RPawnPlayer pawn, Hit h)
    {
        if (!SniperConfig.C.DestroyObjects || !h.Ok || h.A == null)
        {
            return;
        }

        Actor a = h.A;
        Controller? pc = pawn.Controller;
        string what;

        if (a is RDestructibleProp_FractureWall fw)
        {
            fw.LastBreakInstigator = pawn;
            fw.BreakOffPartsInRadius(h.Loc, 60f, 1000f, true, pawn, true, Vector3.Zero);
            what = "fracture wall";
        }
        else if (a is FracturedStaticMeshActor fsm)
        {
            fsm.LastBreakInstigator = pawn;
            fsm.BreakOffPartsInRadius(h.Loc, 40f, 1000f, true, pawn, true, h.Norm * 100f);
            what = "glass / fractured mesh";
        }
        else if (a is RDestructibleProp dp)
        {
            Class dt = PickDamageType(dp);
            float dmg = Math.Max(1000f, dp.WeaponDamageThreshold + 1f);

            dp.TakeRadiusDamage(pc!, dmg, 60f, dt, 8000f, h.Loc, true, pawn, 1f);
            what = "destructible prop";
        }
        else if (a is ApexDestructibleActor apex)
        {
            apex.TakeRadiusDamage(pc!, 1000f, 60f, RDmgType_BatmobileCannon.StaticClass(), 5000f, h.Loc, true, pawn, 1f);
            what = "Apex actor";
        }
        else if (a is RSmashableProp sp)
        {
            sp.BreakProp();
            what = "smashable prop";
        }
        else
        {
            return;
        }

        if (destroyLogs++ < 30)
        {
            ModLog.Info("Fire: broke " + what + " " + Util.ClassName(a) + ".");
        }
    }

    private static Class PickDamageType(RDestructibleProp dp)
    {
        Class cannon = RDmgType_BatmobileCannon.StaticClass();
        TArray<Class> vul = dp.VulnerableDamageTypes;

        if (vul.Count == 0)
        {
            return cannon;
        }

        for (int i = 0; i < vul.Count; i++)
        {
            if (GameObject.ClassIsChildOf(cannon, vul[i]))
            {
                return cannon;
            }
        }

        return vul[0];
    }
}
