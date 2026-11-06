#nullable enable

using System;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace BatmanDeathstrokeSniper;

internal static class SniperLaser
{
    private static ParticleSystem? ps;
    private static AkEvent? sound;
    private static Emitter? em;
    private static bool on;
    private static float shownSince = -1f;

    private const float DrawDelay = 0.15f;
    private const float AlignCos = 0.85f, KeepCos = 0.5f;
    private static float lastCos;
    private static float nextFind;

    private static ParticleSystem? Template()
    {
        if (ps != null && ps.IsValid)
        {
            return ps;
        }

        if (Util.Now < nextFind)
        {
            return null;
        }

        nextFind = Util.Now + 5f;
        SniperProps.EnsureAssets();
        ps = SniperProps.LaserPS;
        sound = SniperProps.LaserEvent;
        ModLog.Info("Laser: sight effect " + (ps != null ? "ok" : "MISSING") + ", sound " + (sound != null ? "ok" : "missing") + ".");

        return ps;
    }

    public static bool Aimed(RDisruptorSniper dis) => dis.bAiming && dis.GetStateName().ToString() == "Primed";

    public static void Tick(RPlayerController pc, RPawnPlayer pawn, RDisruptorSniper dis, bool show)
    {
        SniperConfig.Settings c = SniperConfig.C;

        if (!show || c.Laser <= 0)
        {
            shownSince = -1f;
            Off();

            return;
        }

        if (shownSince < 0f)
        {
            shownSince = Util.Now;
        }

        if (Util.Now - shownSince < DrawDelay)
        {
            return;
        }

        ParticleSystem? t = Template();

        if (t == null)
        {
            return;
        }

        Vector3 start = SniperFire.Muzzle(dis, pawn);

        pc.GetPlayerViewPoint(out Vector3 cam, out Rotator view);

        Vector3 dir = Vector3.Normalize(view.ToDirection());
        float skip = Math.Max(0f, Vector3.Dot(start - cam, dir));
        Vector3 from = cam + dir * skip;
        Vector3 end = from + dir * c.Range;
        Actor? hit = pawn.Trace(out Vector3 hl, out Vector3 _, end, from, BmSDK.Component.ECollisionFilter.COF_TraceWorldActors, Vector3.Zero, out Actor.FTraceHitInfo _, 0);

        if (hit != null)
        {
            end = hl;
        }

        Vector3 beam = end - start;
        float len = beam.Length();

        if (len < 1f)
        {
            Off();

            return;
        }

        Vector3 barrel = Barrel(dis);

        if (barrel != Vector3.Zero)
        {
            lastCos = Vector3.Dot(barrel, beam / len);

            if (lastCos < (on ? KeepCos : AlignCos))
            {
                Off();

                return;
            }
        }

        if (em == null || !em.IsValid)
        {
            em = pawn.Spawn(REmitter.StaticClass(), pawn, default, start, Rotator.FromDirection(beam), null!, true) as Emitter;

            if (em == null)
            {
                return;
            }

            em.SetTemplate(t, false);
            on = false;
        }

        if (Math.Abs(em.DrawScale - c.LaserScale) > 0.001f)
        {
            em.SetDrawScale(c.LaserScale);
        }

        em.SetLocation(start);
        em.SetRotation(Rotator.FromDirection(beam));

        ParticleSystemComponent? psc = em.ParticleSystemComponent;

        if (psc == null)
        {
            return;
        }

        psc.SetFloatParameter(new FName("BeamLength"), len / c.LaserScale);

        if (!on || !psc.bIsActive)
        {
            psc.ActivateSystem(true, false);

            if (em.bHidden)
            {
                em.SetHidden(false);
            }

            ModLog.Once("laser-on", "Laser: on (barrel/aim alignment " + lastCos.ToString("0.000", Util.Inv) + ", beam " + (len / 100f).ToString("0.0", Util.Inv) + " m).");

            if (!on && c.LaserSound && sound != null)
            {
                try { dis.PlayGadgetSound(sound, false); } catch { }
            }

            on = true;
        }
    }

    private static Vector3 Barrel(RDisruptorSniper dis)
    {
        try
        {
            SkeletalMeshComponent? m = dis.Mesh;

            if (m == null || m.HiddenGame)
            {
                return Vector3.Zero;
            }

            Vector3 x = m.GetBoneAxis(m.GetBoneName(0), GameObject.EAxis.AXIS_X);

            return x == Vector3.Zero ? x : Vector3.Normalize(x);
        }
        catch
        {
            return Vector3.Zero;
        }
    }

    public static void Off()
    {
        if (!on)
        {
            return;
        }

        on = false;

        try
        {
            if (em != null && em.IsValid)
            {
                em.ParticleSystemComponent?.DeactivateSystem();
                em.SetHidden(true);
            }
        }
        catch
        {
        }
    }

    public static void Reset()
    {
        em = null;
        on = false;
        shownSince = -1f;
    }

    public static void Destroy()
    {
        try
        {
            if (em != null && em.IsValid)
            {
                em.ParticleSystemComponent?.DeactivateSystem();
                em.Destroy();
            }
        }
        catch
        {
        }

        Reset();
    }
}
