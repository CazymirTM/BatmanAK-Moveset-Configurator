#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace BatmanAssaultRifle;

internal static class RifleFire
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

    private const float BulletSpeed = 45000f;
    private const float FlashLife = 0.05f;
    private const string VictimsKey = "BatmanRifle.Victims";

    private sealed class Bullet { public Emitter E = null!; public Vector3 From, To; public float T, Dur; }
    private sealed class Flash { public Emitter E = null!; public Pawn P = null!; public float T; }

    private static readonly List<Bullet> bullets = new List<Bullet>();
    private static readonly List<Flash> flashes = new List<Flash>();
    private static readonly Dictionary<string, ParticleSystem?> trailCache = new Dictionary<string, ParticleSystem?>();
    private static readonly Dictionary<IntPtr, int> hits = new Dictionary<IntPtr, int>();
    private static readonly Dictionary<IntPtr, float> recentKills = new Dictionary<IntPtr, float>();

    private static bool Target(RPawnVillain? v)
    {
        if (v == null || !v.IsValid || !v.IsAliveAndWell())
        {
            return false;
        }

        return !(recentKills.TryGetValue(v.Ptr, out float t) && Util.Now - t < 10f);
    }
    private static readonly Random rng = new Random();

    private static int shotSeq;
    private static float nextNoise;
    private static float nextShotAt;
    private static float spread;
    private static bool pendingShot;
    private static int destroyLogs;
    private static Class? woundType;
    private static string woundTypeFor = "";

    public static bool Firing { get; private set; }
    public static int BurstLeft { get; private set; }
    private static float burstEndAt;
    private static RPawnVillain? burstTarget;
    private static Vector3 burstDir;

    public static bool Bursting => BurstLeft > 0 || Util.Now < burstEndAt;

    public static void Reset()
    {
        bullets.Clear();
        flashes.Clear();
        trailCache.Clear();
        hits.Clear();
        recentKills.Clear();
        Firing = false;
        BurstLeft = 0;
        burstEndAt = 0f;
        burstTarget = null;
        pendingShot = false;
        spread = 0f;
    }

    [Redirect(typeof(RBatarang), nameof(RBatarang.AimedFireBatarang))]
    private static bool AimedFireBatarangRedirect(RBatarang self)
    {
        if (!BatmanArsenalPack.Arsenal.Rifle)
        {
            return self.AimedFireBatarang();
        }

        RPawnPlayer? pawn = null;
        bool ours = false;

        try
        {
            ours = RifleMode.Installed && Rifle.Selected && Rifle.IsBatmansBatarang(self, out pawn);
        }
        catch
        {
        }

        if (!ours || pawn == null)
        {
            return self.AimedFireBatarang();
        }

        try
        {
            if (Rifle.Primed(self))
            {
                pendingShot = true;
            }
            else
            {
                StartBurst(pawn, self);
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn("Fire: trigger failed: " + Util.Unwrap(exception));
        }

        return false;
    }

    private static bool swallowedPress;

    private static bool AimedRifle()
    {
        try
        {
            return BatmanArsenalPack.Arsenal.Rifle && RifleMode.Installed && Rifle.HeldBy(out _, out RBatarang? bat) && bat != null && bat.bAiming;
        }
        catch
        {
            return false;
        }
    }

    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.GadgetOrQuickStrikePressed))]
    private static void GadgetOrQuickStrikePressedRedirect(RPlayerControllerCombat self)
    {
        if (AimedRifle())
        {
            pendingShot = true;
            swallowedPress = true;
            ModLog.Once("press-swallowed", "Fire: the fire button goes to the rifle (GadgetOrQuickStrikePressed), the Batarang never sees it.");

            return;
        }

        self.GadgetOrQuickStrikePressed();
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.PlayerTick))]
    private static void PlayerTickRedirect(RPlayerController self, float deltaTime)
    {
        try
        {
            if (self.PlayerInput is RPlayerInput input && (input.aQuickStrike != 0 || input.aRightTrigger != 0f) && AimedRifle())
            {
                ModLog.Once("input-cleared", "Fire: cleared the fire axis (" + input.aQuickStrike.ToString() + "/" + input.aRightTrigger.ToString() + ") so the Batarang cannot start a throw while the rifle is aimed.");
                input.aQuickStrike = 0;
                input.aRightTrigger = 0f;
            }
        }
        catch
        {
        }

        self.PlayerTick(deltaTime);
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.GadgetPressed))]
    private static void GadgetPressedRedirect(RPlayerController self)
    {
        if (AimedRifle())
        {
            pendingShot = true;
            swallowedPress = true;
            ModLog.Once("press-swallowed-2", "Fire: the fire button goes to the rifle (GadgetPressed), the Batarang never sees it.");

            return;
        }

        self.GadgetPressed();
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.GadgetReleased))]
    private static void GadgetReleasedRedirect(RPlayerController self)
    {
        if (swallowedPress)
        {
            swallowedPress = false;

            if (AimedRifle())
            {
                return;
            }
        }

        self.GadgetReleased();
    }

    private static void StartBurst(RPawnPlayer pawn, RBatarang bat)
    {
        int n = RifleConfig.C.Burst;

        if (n <= 0 || Bursting || RifleAmmo.Reloading)
        {
            return;
        }

        if (RifleAmmo.Mag <= 0)
        {
            RifleAmmo.CanShoot(bat);

            return;
        }

        burstTarget = null;

        try
        {
            if (bat.CurrentBatarangTarget is RPawnVillain v && Target(v))
            {
                burstTarget = v;
            }
        }
        catch
        {
        }

        burstTarget ??= NearestAhead(pawn, 2500f, 0.5f);

        double yaw = pawn.Rotation.Yaw * Math.PI / 180;

        burstDir = new Vector3((float)Math.Cos(yaw), (float)Math.Sin(yaw), 0f);

        if (burstTarget != null)
        {
            Vector3 to = burstTarget.Location - pawn.Location;

            to.Z = 0f;

            if (to.LengthSquared() > 1f)
            {
                burstDir = Vector3.Normalize(to);

                var r = pawn.Rotation;

                pawn.SetRotation(new Rotator(r.Pitch, MathF.Atan2(burstDir.Y, burstDir.X) * 57.29578f, r.Roll));
            }
        }

        BurstLeft = Math.Min(n, RifleAmmo.Mag);
        nextShotAt = Util.Now + 0.12f;
        burstEndAt = Util.Now + 0.12f + BurstLeft * Interval + 0.3f;
        ModLog.Info("Fire: quickfire burst of " + BurstLeft + (burstTarget != null ? " at " + burstTarget.Name : " ahead") + ".");
    }

    private static float Interval => 60f / Math.Max(60f, RifleConfig.C.Rpm);

    private static RPawnVillain? NearestAhead(RPawnPlayer pawn, float range, float minDot)
    {
        double yaw = pawn.Rotation.Yaw * Math.PI / 180;
        var fwd = new Vector3((float)Math.Cos(yaw), (float)Math.Sin(yaw), 0f);
        RPawnVillain? best = null;
        float bestD = range;

        for (Pawn? p = pawn.WorldInfo.PawnList; p != null; p = p.NextPawn)
        {
            if (p is not RPawnVillain v || !Target(v))
            {
                continue;
            }

            Vector3 to = v.Location - pawn.Location;
            float d = to.Length();

            if (d > bestD || d < 1f || Vector3.Dot(to / d, fwd) < minDot || !pawn.FastTrace(Chest(v), pawn.Location + new Vector3(0, 0, 50), Vector3.Zero, false))
            {
                continue;
            }

            best = v;
            bestD = d;
        }

        return best;
    }

    public static bool TriggerHeld()
    {
        bool mouse = Util.GameFocused() && Util.KeyDown(0x01);

        return mouse || RiflePad.TriggerHeld;
    }

    public static void Tick(RPlayerController controller, RPawnPlayer pawn, RBatarang bat, bool aimed, float dt)
    {
        RifleConfig.Settings c = RifleConfig.C;
        float now = Util.Now;

        if (!aimed)
        {
            pendingShot = false;
        }

        bool wantFire = aimed && (pendingShot || TriggerHeld()) && !RifleAmmo.Reloading;

        if (BurstLeft > 0)
        {
            if (RifleAmmo.Reloading || RifleAmmo.Mag <= 0)
            {
                BurstLeft = 0;
            }
            else if (now >= nextShotAt)
            {
                nextShotAt = now + Interval;
                BurstLeft--;
                Shoot(controller, pawn, bat, burst: true);
            }

            Firing = true;
            spread = Math.Max(0f, spread - c.SpreadRecover * dt * 0.25f);

            return;
        }

        if (wantFire)
        {
            if (!Firing)
            {
                nextShotAt = Math.Max(nextShotAt, now);
            }

            Firing = true;

            if (now >= nextShotAt)
            {
                if (RifleAmmo.CanShoot(bat))
                {
                    nextShotAt = now - nextShotAt < Interval ? nextShotAt + Interval : now + Interval;
                    Shoot(controller, pawn, bat, burst: false);
                }
                else
                {
                    nextShotAt = now + Interval;
                }

                pendingShot = false;
            }
        }
        else
        {
            Firing = false;
            spread = Math.Max(0f, spread - c.SpreadRecover * dt);
        }
    }

    private static Vector3 Chest(Pawn v)
    {
        Vector3 c = v.Mesh?.GetBoneLocation(new FName("Bip01_Spine2"), 0) ?? Vector3.Zero;

        return c == Vector3.Zero ? v.Location : c;
    }

    private struct Hit { public bool Ok; public Vector3 Loc, Norm; public Actor? A; public string Bone; }

    private static Hit TraceHit(Actor tracer, Vector3 start, Vector3 end)
    {
        Actor? a = tracer.Trace(out Vector3 hl, out Vector3 hn, end, start, BmSDK.Component.ECollisionFilter.COF_TraceWorldActors, Vector3.Zero, out Actor.FTraceHitInfo info, 0);
        string bone = "";

        try
        {
            bone = info.BoneName.ToString() ?? "";
        }
        catch
        {
        }

        return a == null ? new Hit { Ok = false, Loc = end, Bone = "" } : new Hit { Ok = true, Loc = hl, Norm = hn, A = a, Bone = bone };
    }

    public static Vector3? AimPoint(RBatarang bat, RPawnPlayer pawn)
    {
        try
        {
            Rotator rot = bat.GetAutoTargetRotation();
            Vector3 start = bat.GetAutoTargetOrigin(rot);
            Vector3 dir = rot.ToDirection();
            Hit h = TraceHit(pawn, start, start + dir * RifleConfig.C.Range);

            OnThug = h.Ok && h.A is RPawnVillain v && Target(v);

            return h.Loc;
        }
        catch
        {
            OnThug = false;

            return null;
        }
    }

    public static bool OnThug;

    private static Vector3 Spread(Vector3 dir, float degrees)
    {
        if (degrees <= 0f)
        {
            return dir;
        }

        Vector3 side = Vector3.Cross(dir, Vector3.UnitZ);

        if (side.LengthSquared() < 1e-4f)
        {
            side = Vector3.UnitY;
        }

        side = Vector3.Normalize(side);

        Vector3 up = Vector3.Normalize(Vector3.Cross(side, dir));
        float r = degrees * MathF.Sqrt((float)rng.NextDouble()) * MathF.PI / 180f;
        float a = (float)rng.NextDouble() * MathF.PI * 2f;

        return Vector3.Normalize(dir + (side * MathF.Cos(a) + up * MathF.Sin(a)) * MathF.Tan(r));
    }

    private static int aimLogs;

    private static void Shoot(RPlayerController controller, RPawnPlayer pawn, RBatarang bat, bool burst)
    {
        RifleConfig.Settings c = RifleConfig.C;

        RifleAmmo.Consume();

        Vector3 start;
        Vector3 dir;

        if (burst)
        {
            start = pawn.Location + new Vector3(0, 0, 50f);
            if (!Target(burstTarget))
            {
                burstTarget = NearestAhead(pawn, 2500f, 0.3f);
            }

            dir = burstTarget != null ? Vector3.Normalize(Chest(burstTarget) - start) : burstDir;
        }
        else
        {
            Rotator rot = bat.GetAutoTargetRotation();

            start = bat.GetAutoTargetOrigin(rot);
            dir = rot.ToDirection();

            if (aimLogs < 3)
            {
                aimLogs++;
                ModLog.Info("Fire: aim rotation " + Util.R(rot) + " vs controller " + Util.R(controller.Rotation) + ", origin " + Util.V(start - pawn.Location) + ".");
            }
        }

        float cone = Math.Min(c.SpreadMax, c.Spread + spread) * (burst ? 1.6f : 1f);

        dir = Spread(dir, cone);
        spread = Math.Min(c.SpreadMax, spread + c.SpreadPerShot);

        Hit h = TraceHit(pawn, start, start + dir * c.Range);
        RPawnVillain? victim = h.Ok ? h.A as RPawnVillain : null;
        bool head = victim != null && h.Bone.IndexOf("Head", StringComparison.OrdinalIgnoreCase) >= 0;

        if (victim != null && !Target(victim))
        {
            victim = null;
        }

        if (victim == null && c.AimAssist > 0f)
        {
            float maxAlong = (h.Loc - start).Length() + 30f;
            float best = float.MaxValue;

            for (Pawn? p = pawn.WorldInfo.PawnList; p != null; p = p.NextPawn)
            {
                if (p is not RPawnVillain v || !Target(v))
                {
                    continue;
                }

                Vector3 to = Chest(v) - start;
                float along = Vector3.Dot(to, dir);

                if (along <= 0 || along > maxAlong || along >= best)
                {
                    continue;
                }

                if ((to - dir * along).Length() > c.AimAssist + 20f)
                {
                    continue;
                }

                best = along;
                victim = v;
            }

            if (victim != null)
            {
                h = new Hit { Ok = false, Loc = start + dir * best, Bone = "" };
            }
        }

        Vector3 muzzle = RifleProps.Muzzle(pawn);
        Vector3 end = h.Loc;

        if (c.ShotSound && RifleProps.ShotEvent != null)
        {
            try { bat.PlayGadgetSound(RifleProps.ShotEvent, false); } catch { }
        }

        MuzzleFlash(pawn, muzzle, end - muzzle);
        TrailFx(pawn, muzzle, end);

        if (h.Ok && victim == null && h.Norm != Vector3.Zero)
        {
            SparkFx(pawn, h.Loc, h.Norm);
        }

        if (victim != null)
        {
            SparkFx(pawn, end, -dir);
            Damage(pawn, victim, dir, head);
        }
        else
        {
            try
            {
                DestroyHit(pawn, h);
            }
            catch (Exception exception)
            {
                ModLog.Once("destroy-failed", "Fire: breaking objects failed: " + Util.Unwrap(exception));
            }
        }

        try
        {
            HitDrones(pawn, start, dir, (end - start).Length());
        }
        catch (Exception exception)
        {
            ModLog.Once("drones-failed", "Fire: drone hit failed: " + Util.Unwrap(exception));
        }

        if (!burst)
        {
            try { AppDomain.CurrentDomain.SetData("DZAim.Shot", "auto|" + (++shotSeq)); } catch { }
        }

        GunNoise(pawn);
    }

    private static Class WoundType()
    {
        string name = RifleConfig.C.HitType;

        if (woundType != null && woundType.IsValid && woundTypeFor == name)
        {
            return woundType;
        }

        woundTypeFor = name;
        woundType = null;

        foreach (string pkg in new[] { "BmGame.", "BmScript.", "" })
        {
            try
            {
                woundType = Game.FindObject<Class>(pkg + name);
            }
            catch
            {
            }

            if (woundType != null)
            {
                break;
            }
        }

        if (woundType == null)
        {
            ModLog.Info("Fire: damage type " + name + " not found; using RDmgType_QuickBatarang.");
            woundType = RDmgType_QuickBatarang.StaticClass();
        }

        return woundType;
    }

    private static void Damage(RPawnPlayer pawn, RPawnVillain v, Vector3 dir, bool head)
    {
        RifleConfig.Settings c = RifleConfig.C;
        int count = 1;

        hits.TryGetValue(v.Ptr, out int prev);
        count = prev + 1;
        hits[v.Ptr] = count;

        if (hits.Count > 64)
        {
            hits.Clear();
            hits[v.Ptr] = count;
        }

        bool kill = count >= c.HitsToKill || (head && c.Headshots);
        int hp = v.Health;
        var di = new RPawnCombat.FDamageInfo();

        di.Attacker = pawn;
        di.Impulse = dir * c.Impulse;
        di.BoneName = new FName(head ? "Bip01_Head" : "Bip01_Spine2");

        if (kill)
        {
            MDmgType_HandgunGadget def = MDmgType_HandgunGadget.DefaultObject;

            di.DamageAmount = def.StrikeDamage * c.KillDamageMul;
            di.DamageType = MDmgType_HandgunGadget.StaticClass();
            di.StunTime = def.StunTime;
            MarkVictim(v);
            hits.Remove(v.Ptr);
            recentKills[v.Ptr] = Util.Now;

            if (recentKills.Count > 64)
            {
                recentKills.Clear();
                recentKills[v.Ptr] = Util.Now;
            }
        }
        else
        {
            di.DamageAmount = c.WoundDamage;
            di.DamageType = WoundType();
        }

        object res = v.DamagedBy(di);

        ModLog.Info("Fire: hit " + Util.ClassName(v) + " #" + count + (head ? " HEAD" : "") + (kill ? " (kill)" : " (wound)") + " hp " + hp + " -> " + v.Health + " (" + res + ").");
    }

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

    private static void GunNoise(RPawnPlayer pawn)
    {
        int mode = RifleConfig.C.GunNoise;

        if (mode == 0 || Util.Now < nextNoise)
        {
            return;
        }

        nextNoise = Util.Now + 0.6f;

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

    private static void MuzzleFlash(Pawn pawn, Vector3 muzzle, Vector3 dir)
    {
        Emitter? flash = SpawnFx(pawn, RifleProps.FlashPS, muzzle, dir, RifleConfig.C.MuzzleScale * RifleProps.FlashScaleFix);

        if (flash != null)
        {
            flashes.Add(new Flash { E = flash, P = pawn });
        }
    }

    private static void SparkFx(Actor ctx, Vector3 at, Vector3 dir)
    {
        if (RifleConfig.C.SparkScale > 0f && dir != Vector3.Zero)
        {
            Emitter? em = SpawnFx(ctx, RifleProps.ImpactPS, at, dir, RifleConfig.C.SparkScale);

            if (em != null)
            {
                bullets.Add(new Bullet { E = em, From = at, To = at, Dur = 0.3f });
            }
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
        int type = RifleConfig.C.Trail;

        if (type <= 0 || type >= TrailTypes.Length)
        {
            return;
        }

        string[] parts = TrailTypes[type].Split('|');
        float sc = RifleConfig.C.TrailScale;
        Emitter? em = SpawnFx(ctx, TrailPS(parts[0]), from, to - from, sc);

        if (em == null)
        {
            return;
        }

        float dist = Vector3.Distance(from, to);

        if (parts[1] == "s")
        {
            em.SetFloatParameter(new FName("BeamLength"), dist / sc);
            bullets.Add(new Bullet { E = em, From = from, To = from, Dur = 0.08f });
        }
        else if (parts[1] == "v")
        {
            bullets.Add(new Bullet { E = em, From = from, To = from, Dur = 0.12f });
        }
        else
        {
            bullets.Add(new Bullet { E = em, From = from, To = to, Dur = Math.Max(0.02f, dist / BulletSpeed) });
        }
    }

    public static void TickFx(float dt)
    {
        for (int i = flashes.Count - 1; i >= 0; i--)
        {
            Flash f = flashes[i];

            f.T += dt;

            try
            {
                if (f.T >= FlashLife || !f.P.IsValid)
                {
                    f.E.ParticleSystemComponent?.DeactivateSystem();
                    f.E.Destroy();
                    flashes.RemoveAt(i);

                    continue;
                }

                f.E.SetLocation(RifleProps.Muzzle(f.P));
            }
            catch
            {
                flashes.RemoveAt(i);
            }
        }

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
                    b.E.LifeSpan = 2f;
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

    private static void HitDrones(RPawnPlayer pawn, Vector3 start, Vector3 dir, float range)
    {
        if (!RifleConfig.C.HitDrones)
        {
            return;
        }

        float radius = Math.Max(30f, RifleConfig.C.AimAssist + 15f);
        BmSDK.BmScript.RPredatorDroneMini? hitDrone = null;
        float best = range + 50f;

        for (Pawn? p = pawn.WorldInfo.PawnList; p != null; p = p.NextPawn)
        {
            if (p is not BmSDK.BmScript.RPredatorDroneMini d || d.Health <= 0)
            {
                continue;
            }

            Vector3 to = d.Location - start;
            float along = Vector3.Dot(to, dir);

            if (along <= 0 || along > best || (to - dir * along).Length() > radius)
            {
                continue;
            }

            if (!pawn.FastTrace(d.Location, start, Vector3.Zero, false))
            {
                continue;
            }

            best = along;
            hitDrone = d;
        }

        if (hitDrone != null)
        {
            int dmg = (int)RifleConfig.C.DroneDamage;

            hitDrone.TakeDamageFromWeapon(dmg, pawn.Controller, hitDrone.Location, -dir, dir * 300f, MDmgType_HandgunGadget.StaticClass(), default, pawn, false);

            if (hitDrone.Health <= 0)
            {
                ModLog.Info("Fire: drone destroyed.");
            }
        }
    }

    private static void DestroyHit(RPawnPlayer pawn, Hit h)
    {
        if (!RifleConfig.C.DestroyObjects || !h.Ok || h.A == null)
        {
            return;
        }

        Actor a = h.A;
        Controller? pc = pawn.Controller;
        string what;

        if (a is RDestructibleProp_FractureWall fw)
        {
            fw.LastBreakInstigator = pawn;
            fw.BreakOffPartsInRadius(h.Loc, 30f, 600f, true, pawn, true, Vector3.Zero);
            what = "fracture wall";
        }
        else if (a is FracturedStaticMeshActor fsm)
        {
            fsm.LastBreakInstigator = pawn;
            fsm.BreakOffPartsInRadius(h.Loc, 25f, 600f, true, pawn, true, h.Norm * 100f);
            what = "glass / fractured mesh";
        }
        else if (a is RDestructibleProp dp)
        {
            Class dt = PickDamageType(dp);
            float dmg = Math.Max(1000f, dp.WeaponDamageThreshold + 1f);

            dp.TakeRadiusDamage(pc!, dmg, 40f, dt, 4000f, h.Loc, true, pawn, 1f);
            what = "destructible prop";
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

        if (destroyLogs++ < 20)
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
