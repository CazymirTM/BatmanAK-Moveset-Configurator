#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.BmScript;
using BmSDK.Engine;
using BmSDK.Framework;

namespace BatmanNightwingSticks;

internal static class SticksFlight
{
    private const float CatchRadius = 250f;
    private const float AimConeLateral = 220f;
    private const float AimConeDegrees = 14f;
    private const float QuickConeDegrees = 80f;
    private const float QuickRange = 2000f;
    private const string CatchBone = "Bip01_R_Hand";
    private const string CatchAnim = "Ricochet_Stand_Catch";

    private static float maxRange = 2500f;
    private static float extraTargetDistance = 900f;
    private static int maxTargets = 4;
    private static bool tuned;

    private static readonly Dictionary<IntPtr, RNightwingSticksBounceProjectile> flying = new Dictionary<IntPtr, RNightwingSticksBounceProjectile>();
    private static readonly HashSet<IntPtr> returning = new HashSet<IntPtr>();
    private static IntPtr lastProjectile = IntPtr.Zero;

    private static List<RPawnVillain> villains = new List<RPawnVillain>();
    private static DateTime nextVillainScan = DateTime.MinValue;
    private static DateTime nextProjectileScan = DateTime.MinValue;
    private static DateTime catchOverlayEndsAt = DateTime.MinValue;

    public static bool InFlight => flying.Count > 0;

    private static readonly TimeSpan ThrowWindow = TimeSpan.FromSeconds(1.2);
    private static readonly TimeSpan SameThrowWindow = TimeSpan.FromSeconds(0.05);
    private static DateTime throwStartedAt = DateTime.MinValue;

    public static bool Busy(RInventoryGadget sticks) =>
        InFlight || DateTime.UtcNow - throwStartedAt < ThrowWindow || sticks.GetStateName().ToString() == "Firing";

    public static void Reset()
    {
        quickAnimsSet = false;
        flying.Clear();
        returning.Clear();
        lastProjectile = IntPtr.Zero;
        villains.Clear();
        nextVillainScan = DateTime.MinValue;
        catchOverlayEndsAt = DateTime.MinValue;
    }

    public static void Tick(RPawnPlayer? pawn)
    {
        if (pawn == null || Sticks.Gadget is not RNightwingSticksBounceThrow sticks || !sticks.IsValid)
        {
            if (flying.Count > 0)
            {
                Reset();
            }

            return;
        }

        Tune(sticks);
        LimitThrows(sticks);

        DateTime now = DateTime.UtcNow;

        if (now >= nextVillainScan)
        {
            nextVillainScan = now + TimeSpan.FromSeconds(0.4);
            ScanVillains(pawn);
        }

        if (Sticks.HeldBy(out _, out _) && sticks.bAiming)
        {
            Util.TryCall("aim bounce points", () => WriteAimPoints(pawn, sticks));
        }

        Discover(sticks, now);

        foreach (RNightwingSticksBounceProjectile projectile in flying.Values.ToList())
        {
            Util.TryCall("stick flight", () => Steer(pawn, projectile));
        }

        if (catchOverlayEndsAt != DateTime.MinValue && now >= catchOverlayEndsAt)
        {
            catchOverlayEndsAt = DateTime.MinValue;
            Util.TryCall("StopWeaponOverlay", pawn.StopWeaponOverlay);
        }
    }

    private static void LimitThrows(RNightwingSticksBounceThrow sticks)
    {
        if (sticks.MaxNumQuickBatarangs != 1 || sticks.bCanComboThrow || sticks.bSuperComboThrow)
        {
            ModLog.Info("Flight: one stick per throw (quick throws " + sticks.MaxNumQuickBatarangs + " -> 1, combo throw off).");
            sticks.MaxNumQuickBatarangs = 1;
            sticks.bCanComboThrow = false;
            sticks.bSuperComboThrow = false;
        }

        if (!quickAnimsSet)
        {
            quickAnimsSet = true;
            Util.TryCall("quick throw anims", () => UseRicochetQuickThrow(sticks));
        }

        if (sticks.QueuedBatarangs > 0 && Busy(sticks))
        {
            sticks.QueuedBatarangs = 0;
        }
    }

    private static bool quickAnimsSet;
    private const string QuickThrowAnim = "Ricochet_Stand_Throw";

    private static void UseRicochetQuickThrow(RNightwingSticksBounceThrow sticks)
    {
        if (SticksAnims.FindSequence(QuickThrowAnim) == null)
        {
            ModLog.Info("Flight: " + QuickThrowAnim + " not loaded; quickfire keeps the game's throw animation.");

            return;
        }

        FName ricochet = new FName(QuickThrowAnim);
        TArray<RBatarang.FBatarangThrowDirection> directions = sticks.ThrowDirectionTypes_9.ThrowDirections;
        int replaced = 0;

        for (int index = 0; index < directions.Count; index++)
        {
            TArray<FName> anims = directions[index].ThrowAnims;

            for (int n = 0; n < anims.Count; n++)
            {
                if (anims[n].ToString() != QuickThrowAnim)
                {
                    anims[n] = ricochet;
                    replaced++;
                }
            }
        }

        ModLog.Info("Flight: standing quickfire uses Nightwing's " + QuickThrowAnim + " (" + replaced + " batarang throw anim(s) replaced).");
    }

    private static int refusedQuickfires;

    private static bool RefuseQuickfire(RInventoryGadget gadget, string source)
    {
        if (!Sticks.IsSticks(gadget))
        {
            return false;
        }

        if (!InFlight && DateTime.UtcNow - throwStartedAt < SameThrowWindow)
        {
            return false;
        }

        if (Busy(gadget))
        {
            if (refusedQuickfires++ % 10 == 0)
            {
                ModLog.Info("Flight: quickfire ignored (" + source + "), a stick is already " + (InFlight ? "in flight" : "being thrown") + ".");
            }

            return true;
        }

        throwStartedAt = DateTime.UtcNow;
        Util.TryCall("quickfire target", PrepareQuickfire);

        return false;
    }

    private static List<Bounce>? quickChain;
    private static DateTime quickChainAt = DateTime.MinValue;

    private static void PrepareQuickfire()
    {
        RPawnPlayer? pawn = Sticks.Batman();

        if (pawn == null || pawn.Controller is not RPlayerController controller)
        {
            return;
        }

        ScanVillains(pawn);
        controller.GetPlayerViewPoint(out Vector3 eye, out Rotator view);

        Vector3 flat = view.ToDirection() with { Z = 0 };
        Vector3 direction = flat.LengthSquared() > 0.001f ? Vector3.Normalize(flat) : Vector3.UnitX;
        Vector3 chest = pawn.Location + new Vector3(0f, 0f, 40f);
        List<Bounce> chain = Chain(pawn, chest, direction, quick: true);

        quickChain = chain;
        quickChainAt = DateTime.UtcNow;

        if (chain.Count > 0 && chain[0].Target != null)
        {
            Vector3 to = chain[0].Location - pawn.Location;
            float yaw = MathF.Atan2(to.Y, to.X) * 180f / MathF.PI;

            pawn.SetRotation(new Rotator(0f, yaw, 0f));
        }

        ModLog.Info("Flight: quickfire at " + (chain.Count > 0 && chain[0].Target != null ? chain.Count + " target(s)" : "nothing in front (wall)") + ".");
    }

    [Redirect(typeof(RInventoryGadget), nameof(RInventoryGadget.TryQuickFire))]
    private static void TryQuickFireRedirect(RInventoryGadget self)
    {
        if (RefuseQuickfire(self, "key"))
        {
            return;
        }

        self.TryQuickFire();
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.FireQuickGadget))]
    private static bool FireQuickGadgetRedirect(RPlayerController self, RInventoryGadget quickGadget, bool overrideCombatMove)
    {
        if (quickGadget != null && RefuseQuickfire(quickGadget, "combat"))
        {
            return false;
        }

        return self.FireQuickGadget(quickGadget!, overrideCombatMove);
    }

    private static void Tune(RNightwingSticksBounceThrow sticks)
    {
        if (tuned)
        {
            return;
        }

        tuned = true;

        if (sticks.BatarangRange > 0)
        {
            maxRange = sticks.BatarangRange;
        }

        try
        {
            RPawnPlayerNightwing nightwing = RPawnPlayerNightwing.DefaultObject;

            if (nightwing.RicochetExtraTargetMaxDist > 0)
            {
                extraTargetDistance = nightwing.RicochetExtraTargetMaxDist;
            }

            if (nightwing.RicochetMaxExtraTargets > 0)
            {
                maxTargets = nightwing.RicochetMaxExtraTargets + 1;
            }

            ModLog.Info(
                "Flight: Nightwing's ricochet tuning: range " + nightwing.RicochetMaxRange + ", bounces " + nightwing.RicochetMaxBounces + ", extra targets " + nightwing.RicochetMaxExtraTargets +
                " within " + nightwing.RicochetExtraTargetMaxDist + ", angle " + nightwing.RicochetMaxAngle + "; using range " + maxRange + ", " + maxTargets + " targets, chain distance " + extraTargetDistance + "."
            );
        }
        catch (Exception exception)
        {
            ModLog.Once("tune-failed", "Flight: reading Nightwing's ricochet tuning failed: " + Util.Unwrap(exception));
        }
    }

    private static void ScanVillains(RPawnPlayer pawn)
    {
        villains = Game.FindObjects<RPawnVillain>().Where(villain => IsTarget(villain) && Vector3.Distance(villain.Location, pawn.Location) < maxRange + extraTargetDistance && Standing(villain)).ToList();
    }

    private static bool IsTarget(RPawnVillain? villain) =>
        villain != null && villain.IsValid && !villain.bDeleteMe && !villain.bHidden && villain.Health > 0 && !villain.bGoodAsDead;

    private static bool Standing(RPawnVillain villain)
    {
        try
        {
            return villain.Physics != Actor.EPhysics.PHYS_RigidBody && !villain.IsFloorStrikeable();
        }
        catch
        {
            return true;
        }
    }

    private struct Bounce
    {
        public Vector3 Location;
        public Vector3 Normal;
        public Pawn? Target;
    }

    private static List<Bounce> Chain(RPawnPlayer pawn, Vector3 eye, Vector3 direction, bool quick = false)
    {
        List<Bounce> chain = new List<Bounce>();
        List<RPawnVillain> live = villains.Where(IsTarget).ToList();
        RPawnVillain? first = null;
        float best = float.MaxValue;

        foreach (RPawnVillain villain in live)
        {
            Vector3 offset = villain.Location - eye;
            float along = Vector3.Dot(offset, direction);

            if (along <= 0 || along > (quick ? QuickRange : maxRange))
            {
                continue;
            }

            float lateral = (offset - direction * along).Length();
            float degrees = MathF.Atan2(lateral, along) * 180f / MathF.PI;

            bool inCone = quick ? degrees <= QuickConeDegrees : lateral <= AimConeLateral || degrees <= AimConeDegrees;

            if (!inCone || !Clear(pawn, eye, villain.Location))
            {
                continue;
            }

            float score = quick ? along / maxRange * 60f + degrees * 0.5f : degrees + along / maxRange;

            if (score < best)
            {
                best = score;
                first = villain;
            }
        }

        if (first != null)
        {
            Add(chain, first, eye);

            while (chain.Count < maxTargets)
            {
                Vector3 from = chain[^1].Location;
                RPawnVillain? next = live
                    .Where(villain => !chain.Any(bounce => bounce.Target?.Ptr == villain.Ptr) && Vector3.Distance(villain.Location, from) < extraTargetDistance && Clear(pawn, from, villain.Location))
                    .OrderBy(villain => Vector3.Distance(villain.Location, from))
                    .FirstOrDefault();

                if (next == null)
                {
                    break;
                }

                Add(chain, next, from);
            }

            return chain;
        }

        Vector3 end = eye + direction * maxRange;
        Actor? hit = pawn.Trace(out Vector3 hitLocation, out Vector3 hitNormal, end, eye, Component.ECollisionFilter.COF_TraceWorld, Vector3.Zero, out _, 0);

        chain.Add(hit != null ? new Bounce { Location = hitLocation + hitNormal * 10f, Normal = hitNormal } : new Bounce { Location = end, Normal = -direction });

        return chain;
    }

    private static void Add(List<Bounce> chain, RPawnVillain villain, Vector3 from)
    {
        Vector3 back = from - villain.Location;

        chain.Add(new Bounce { Location = villain.Location, Normal = back.LengthSquared() > 1f ? Vector3.Normalize(back) : Vector3.UnitZ, Target = villain });
    }

    private static bool Clear(RPawnPlayer pawn, Vector3 from, Vector3 to)
    {
        try
        {
            return pawn.Trace(out _, out _, to, from, Component.ECollisionFilter.COF_TraceWorld, Vector3.Zero, out _, 0) == null;
        }
        catch
        {
            return true;
        }
    }

    private static void Write(TArray<RPawnPlayerNightwingBase.FNwStickBouncePoint> points, List<Bounce> chain)
    {
        points.Clear();

        foreach (Bounce bounce in chain)
        {
            RPawnPlayerNightwingBase.FNwStickBouncePoint point = default;

            point.Location = bounce.Location;
            point.Normal = bounce.Normal;
            point.Pawn = bounce.Target!;
            points.Add(point);
        }
    }

    private static string lastAimSummary = "";
    private static List<Bounce>? aimChain;
    private static int hudCalls;

    [Redirect(typeof(RNightwingSticksBounceThrow), nameof(RNightwingSticksBounceThrow.DrawAimingHUD))]
    private static void DrawAimingHUDRedirect(RNightwingSticksBounceThrow self)
    {
        if (Sticks.IsSticks(self) && aimChain != null && self.bAiming)
        {
            try
            {
                Write(self.BouncePoints, aimChain);

                if (hudCalls++ == 0)
                {
                    ModLog.Info("Flight: the aim HUD now draws Batman's " + aimChain.Count + " bounce point(s).");
                }
            }
            catch (Exception exception)
            {
                ModLog.Once("hud-points", "Flight: feeding the aim HUD failed: " + Util.Unwrap(exception));
            }
        }

        self.DrawAimingHUD();

        if (Sticks.IsSticks(self) && aimChain != null && self.bAiming)
        {
            Util.TryCall("bounce markers", () => DrawMarkers(aimChain));
        }
    }

    private const string MarkerAnim = "batarang_lockon";
    private const float ScreenAspect = 16f / 9f;
    private static RHudModuleTargets? targetsModule;

    private static void DrawMarkers(List<Bounce> chain)
    {
        if (targetsModule == null || !targetsModule.IsValid)
        {
            targetsModule = Game.FindObjects<RHudModuleTargets>().FirstOrDefault(module => module != null && module.IsValid && !module.Name.ToString().StartsWith("Default__", StringComparison.Ordinal));
        }

        RPlayerController? controller = Game.GetPlayerController(0);

        if (targetsModule == null || controller == null)
        {
            return;
        }

        controller.GetPlayerViewPoint(out Vector3 eye, out Rotator view);

        float fov = 80f;

        try { fov = controller.PlayerCamera?.GetFOVAngle() ?? 80f; } catch { }

        float tanX = MathF.Tan(Math.Clamp(fov, 10f, 170f) * 0.5f * MathF.PI / 180f);
        float tanY = tanX / ScreenAspect;
        Vector3 forward = Vector3.Normalize(view.ToDirection());
        Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, forward));
        Vector3 up = Vector3.Cross(forward, right);

        for (int index = 0; index < chain.Count; index++)
        {
            if (chain[index].Target is not Pawn target || !target.IsValid)
            {
                continue;
            }

            Vector3 offset = target.Location + new Vector3(0f, 0f, 30f) - eye;
            float depth = Vector3.Dot(offset, forward);

            if (depth <= 1f)
            {
                continue;
            }

            float x = Vector3.Dot(offset, right) / depth / tanX * 0.5f;
            float y = -Vector3.Dot(offset, up) / depth / tanY * 0.5f;

            if (MathF.Abs(x) > 0.5f || MathF.Abs(y) > 0.5f)
            {
                continue;
            }

            targetsModule.TargetCursorScreenSpace(new FString("NwStickBounce" + index), new FString(MarkerAnim), x, y, 0, -1, -1, new FString(""), false, -1f);
        }
    }

    private static void WriteAimPoints(RPawnPlayer pawn, RNightwingSticksBounceThrow sticks)
    {
        if (pawn.Controller is not RPlayerController controller)
        {
            return;
        }

        controller.GetPlayerViewPoint(out Vector3 eye, out Rotator view);

        List<Bounce> chain = Chain(pawn, eye, Vector3.Normalize(view.ToDirection()));

        aimChain = chain;
        Write(sticks.BouncePoints, chain);

        string summary = chain.Count + (chain[0].Target != null ? " target(s)" : " wall point");

        if (summary != lastAimSummary)
        {
            lastAimSummary = summary;
            ModLog.Info("Flight: aiming at " + summary + ".");
        }
    }

    private static void Discover(RNightwingSticksBounceThrow sticks, DateTime now)
    {
        RBatarangProjectile? last = sticks.LastProjectile;

        if (last is RNightwingSticksBounceProjectile projectile && projectile.IsValid && !projectile.bDeleteMe && projectile.Ptr != lastProjectile)
        {
            lastProjectile = projectile.Ptr;
            Track(projectile);
        }

        if (now < nextProjectileScan || (!Sticks.HeldBy(out _, out _) && flying.Count == 0))
        {
            return;
        }

        nextProjectileScan = now + TimeSpan.FromSeconds(0.5);

        RPawnPlayer? batman = Sticks.Batman();

        foreach (RNightwingSticksBounceProjectile found in Game.FindObjects<RNightwingSticksBounceProjectile>())
        {
            if (found != null && found.IsValid && !found.bDeleteMe && batman != null && found.Owner?.Ptr == batman.Ptr && !flying.ContainsKey(found.Ptr))
            {
                Track(found);
            }
        }
    }

    private static void Track(RNightwingSticksBounceProjectile projectile)
    {
        RPawnPlayer? pawn = Sticks.Batman();

        if (pawn == null || projectile.Owner?.Ptr != pawn.Ptr)
        {
            return;
        }

        flying[projectile.Ptr] = projectile;

        TArray<RPawnPlayerNightwingBase.FNwStickBouncePoint> points = projectile.BouncePoints;
        bool valid = points.Count > 0;

        for (int index = 0; index < points.Count; index++)
        {
            RPawnPlayerNightwingBase.FNwStickBouncePoint point = points[index];

            if ((point.Pawn == null || !point.Pawn.IsValid) && Vector3.Distance(point.Location, pawn.Location) > maxRange * 1.5f)
            {
                valid = false;
            }
        }

        bool quickfire = quickChain != null && DateTime.UtcNow - quickChainAt < TimeSpan.FromSeconds(2) && quickChain.Count > 0;

        if (valid && !quickfire)
        {
            ModLog.Info("Flight: " + projectile.Name + " thrown at " + points.Count + " point(s) (" + projectile.GetStateName() + ").");

            return;
        }

        Vector3 eye = pawn.Location;
        Vector3 direction = Vector3.Normalize(pawn.Rotation.ToDirection() with { Z = 0 });

        if (pawn.Controller is RPlayerController controller)
        {
            controller.GetPlayerViewPoint(out eye, out Rotator view);
            direction = Vector3.Normalize(view.ToDirection());
        }

        List<Bounce> chain = quickfire ? quickChain! : Chain(pawn, eye, direction);

        quickChain = null;
        Write(points, chain);
        projectile.NextBouncePoint = 0;
        projectile.NextTargetLocation = chain[0].Location;

        ModLog.Info("Flight: " + projectile.Name + (quickfire ? " quickfired at " : " had no usable bounce points for Batman; sent it at ") + string.Join(",", chain.Select(bounce => bounce.Target?.Name.ToString() ?? "wall")) + ".");
    }

    private static void Steer(RPawnPlayer pawn, RNightwingSticksBounceProjectile projectile)
    {
        if (!projectile.IsValid || projectile.bDeleteMe)
        {
            Forget(projectile.Ptr);

            return;
        }

        string state = projectile.GetStateName().ToString();
        Vector3 hand = pawn.Mesh.GetBoneLocation(new FName(CatchBone));
        float distance = Vector3.Distance(hand, projectile.Location);
        bool coming = returning.Contains(projectile.Ptr);

        if (!coming && state != "ReturnToNightwing")
        {
            if (distance > maxRange * 3f)
            {
                ModLog.Info("Flight: " + projectile.Name + " strayed " + distance.ToString("0") + " away; removed.");
                Forget(projectile.Ptr);
                projectile.Destroy();
            }

            return;
        }

        TArray<RPawnPlayerNightwingBase.FNwStickBouncePoint> points = projectile.BouncePoints;

        if (!coming)
        {
            returning.Add(projectile.Ptr);

            RPawnPlayerNightwingBase.FNwStickBouncePoint home = default;

            home.Location = hand;
            home.Normal = projectile.Location - hand == Vector3.Zero ? Vector3.UnitZ : Vector3.Normalize(projectile.Location - hand);
            points.Add(home);
            projectile.NextBouncePoint = points.Count - 1;
            projectile.NextTargetLocation = hand;
            projectile.GotoState(new FName("Homing"));

            ModLog.Info("Flight: " + projectile.Name + " coming back to Batman (" + distance.ToString("0") + " away, " + projectile.KnockedDown.Count + " hit).");
        }
        else if (points.Count > 0)
        {
            RPawnPlayerNightwingBase.FNwStickBouncePoint home = points[points.Count - 1];

            home.Location = hand;
            points[points.Count - 1] = home;
            projectile.NextBouncePoint = points.Count - 1;
            projectile.NextTargetLocation = hand;
        }

        if (distance < CatchRadius || (coming && state == "ReturnToNightwing" && distance < CatchRadius * 3f))
        {
            Catch(pawn, projectile);
        }
        else if (distance > maxRange * 3f)
        {
            ModLog.Info("Flight: " + projectile.Name + " could not find its way back; removed.");
            Forget(projectile.Ptr);
            projectile.Destroy();
        }
    }

    private static void Catch(RPawnPlayer pawn, RNightwingSticksBounceProjectile projectile)
    {
        ModLog.Info("Flight: Batman caught " + projectile.Name + ".");
        Forget(projectile.Ptr);
        projectile.Destroy();

        if (Sticks.HeldBy(out _, out _))
        {
            PlayCatch(pawn);
        }
    }

    private static void Forget(IntPtr projectile)
    {
        flying.Remove(projectile);
        returning.Remove(projectile);
    }

    private static void PlayCatch(RPawnPlayer pawn)
    {
        AnimSequence? sequence = SticksAnims.FindSequence(CatchAnim);

        if (sequence == null)
        {
            ModLog.Once("nocatch", "Flight: catch animation " + CatchAnim + " not found.");

            return;
        }

        RAnimConfig.FCustomAnimConfig config = default;

        config.UpperBodyAnim = sequence.SequenceName;
        config.CustomUpperBodyAnim = sequence;
        config.ModelspaceUpperBody = false;

        try
        {
            pawn.StopWeaponOverlay();
            pawn.PlayCustomWeaponOverlay(config);
            catchOverlayEndsAt = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Clamp(sequence.SequenceLength, 0.3f, 1.2f));
        }
        catch (Exception exception)
        {
            ModLog.Once("catch-failed", "Flight: catch animation failed: " + Util.Unwrap(exception));
        }
    }

    [Redirect(typeof(RNightwingSticksBounceThrow), nameof(RNightwingSticksBounceThrow.CanThrowGadget))]
    private static bool CanThrowGadgetRedirect(RNightwingSticksBounceThrow self)
    {
        if (Sticks.IsSticks(self) && InFlight)
        {
            return false;
        }

        return self.CanThrowGadget();
    }
}
