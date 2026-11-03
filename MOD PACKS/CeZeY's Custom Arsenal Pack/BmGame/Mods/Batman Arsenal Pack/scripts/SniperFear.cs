#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace BatmanDeathstrokeSniper;

internal static class SniperFear
{
    private const string RunAwayClass = "BmScript.RBMBehaviour_CityRunAway";
    private const string VehicleBehaviour = "RBMBehaviour_ControlledByVehicle";
    private const float HearRadius = 600f;
    private const float KillCheckFor = 1.5f;
    private const float RunDelay = 0.6f;
    private const int MaxWitnesses = 8;

    private sealed class Pending { public RPawnVillain Victim = null!; public RPawnPlayer Shooter = null!; public float Until; }
    private sealed class Scared { public RPawnVillain Pawn = null!; public RBMAIController Ctrl = null!; public float RunAt, CalmAt; public bool Running; }

    private static readonly List<Pending> pending = new List<Pending>();
    private static readonly List<Scared> scared = new List<Scared>();
    private static Class? runAway;
    private static bool runAwayMissing;

    public static void Reset()
    {
        pending.Clear();
        scared.Clear();
    }

    public static void Watch(RPawnPlayer shooter, RPawnVillain victim)
    {
        if (!SniperConfig.C.Fear)
        {
            return;
        }

        pending.Add(new Pending { Victim = victim, Shooter = shooter, Until = Util.Now + KillCheckFor });
    }

    public static void Tick()
    {
        float now = Util.Now;

        for (int i = pending.Count - 1; i >= 0; i--)
        {
            Pending p = pending[i];

            try
            {
                if (!p.Victim.IsValid || !p.Shooter.IsValid)
                {
                    pending.RemoveAt(i);
                    continue;
                }

                if (!p.Victim.IsAliveAndWell())
                {
                    pending.RemoveAt(i);
                    Frighten(p.Shooter, p.Victim);
                    continue;
                }
            }
            catch
            {
                pending.RemoveAt(i);
                continue;
            }

            if (now >= p.Until)
            {
                pending.RemoveAt(i);
                ModLog.Info("Fear: " + p.Victim.Name + " shrugged off the shot, nobody panics.");
            }
        }

        for (int i = scared.Count - 1; i >= 0; i--)
        {
            Scared s = scared[i];

            try
            {
                if (!s.Pawn.IsValid || !s.Ctrl.IsValid || !s.Pawn.IsAliveAndWell())
                {
                    scared.RemoveAt(i);
                    continue;
                }

                if (!s.Running && now >= s.RunAt)
                {
                    s.Running = true;

                    if (RunAway() is Class cls && !s.Ctrl.HasBehaviourOfClass(cls, false))
                    {
                        s.Ctrl.SpawnBehaviour(cls, null);
                    }
                }

                if (s.Running && now >= s.CalmAt)
                {
                    scared.RemoveAt(i);

                    if (RunAway() is Class cls && s.Ctrl.HasBehaviourOfClass(cls, false))
                    {
                        s.Ctrl.ClearBehavioursOfClass(cls, false);
                        ModLog.Info("Fear: " + s.Pawn.Name + " pulls himself together.");
                    }
                }
            }
            catch (Exception exception)
            {
                scared.RemoveAt(i);
                ModLog.Once("fear-tick", "Fear: updating a scared thug failed: " + Util.Unwrap(exception));
            }
        }
    }

    private static Class? RunAway()
    {
        if (runAway != null && runAway.IsValid)
        {
            return runAway;
        }

        runAway = Game.FindObject<Class>(RunAwayClass);

        if (runAway == null && !runAwayMissing)
        {
            runAwayMissing = true;
            ModLog.Warn("Fear: " + RunAwayClass + " is not loaded; thugs will only flinch.");
        }

        return runAway;
    }

    private static bool InVehicle(RBMAIController c)
    {
        TArray<RBMBehaviour> list = c.BehaviourList;

        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] != null && list[i].Class.Name.ToString() == VehicleBehaviour)
            {
                return true;
            }
        }

        return false;
    }

    private static Vector3 Head(Pawn p)
    {
        Vector3 h = p.Mesh?.GetBoneLocation(new FName("Bip01_Head"), 0) ?? Vector3.Zero;

        return h == Vector3.Zero ? p.Location + new Vector3(0, 0, 60) : h;
    }

    private static void Frighten(RPawnPlayer shooter, RPawnVillain victim)
    {
        SniperConfig.Settings c = SniperConfig.C;
        float radius = c.FearRadius;
        Vector3 at = victim.Location;
        Vector3 body = Head(victim);
        var witnesses = new List<(RPawnVillain v, RBMAIController ctrl, float d)>();

        for (Pawn? p = shooter.WorldInfo.PawnList; p != null; p = p.NextPawn)
        {
            if (p is not RPawnVillain v || v.Ptr == victim.Ptr || !v.IsAliveAndWell() || v.Controller is not RBMAIController ctrl || !ctrl.IsValid)
            {
                continue;
            }

            float d = Vector3.Distance(v.Location, at);

            if (d > radius || InVehicle(ctrl) || scared.Exists(s => s.Pawn.Ptr == v.Ptr))
            {
                continue;
            }

            if (d > HearRadius && !shooter.FastTrace(body, Head(v), Vector3.Zero, false))
            {
                continue;
            }

            witnesses.Add((v, ctrl, d));
        }

        if (witnesses.Count == 0)
        {
            ModLog.Info("Fear: " + victim.Name + " died with no one close enough to see it.");

            return;
        }

        witnesses.Sort((a, b) => a.d.CompareTo(b.d));

        if (witnesses.Count > MaxWitnesses)
        {
            witnesses.RemoveRange(MaxWitnesses, witnesses.Count - MaxWitnesses);
        }

        if (c.FearCower && Game.GetGameInfo() is RGameInfo gi && gi.CombatManager is RBMCombatManager cm)
        {
            try
            {
                cm.TriggerCombatCowerEvent(shooter, at, radius, Math.Min(radius, HearRadius));
            }
            catch (Exception exception)
            {
                ModLog.Once("fear-cower", "Fear: the cower reaction failed: " + Util.Unwrap(exception));
            }
        }

        float now = Util.Now;

        foreach (var (v, ctrl, d) in witnesses)
        {
            scared.Add(new Scared { Pawn = v, Ctrl = ctrl, RunAt = now + (c.FearCower ? RunDelay : 0f), CalmAt = now + c.FearTime });
        }

        ModLog.Info("Fear: " + witnesses.Count + " thug(s) saw " + victim.Name + " die and run for it (" + string.Join(", ", witnesses.ConvertAll(w => w.v.Name + " " + (w.d / 100f).ToString("0", Util.Inv) + " m")) + ").");
    }
}
