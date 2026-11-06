#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace BatmanRobinStaff;

internal static class StaffProp
{
    private const string Package = "Playable_Robin0_SF";
    private const string MeshPath = "Robin.Mesh.Robin_Staff_V2_Right";
    private const string MovementSet = "Anim_Robin_Movement.Code.RB_Movement";
    private const string PutAwayName = "Combat_Stand_Idle_PutAway_02";
    private const string CompPrefix = "BRStaff_";
    private const string HandBone = "Gundummy";
    private const string LeftHandBone = "Gundummy02";

    private const float MirrorMargin = 6f;

    private const float FistX = 10.1f;
    private const float FistY = -2f;

    private const float OwnPoseFrom = 11.5f;
    private const float OwnPoseFull = 15f;

    private static readonly Vector3 HandOffset = new Vector3(35.354f, -127.311f, -22.224f);
    private static readonly Rotator HandRot = new Rotator(17.628f, -130.6f, -76.74f);

    public static AnimSequence? PutAway;
    public static AkEvent? ExtendEvent, RetractEvent;

    private static SkeletalMesh? mesh;
    private static bool assetsTried;
    private static SkeletalMeshComponent? comp, compLeft;
    private static IntPtr compPawn;
    private static bool shown;
    private static bool leftHand;

    public static bool Ready => mesh != null && mesh.IsValid;

    public static void EnsureAssets()
    {
        if (assetsTried)
        {
            return;
        }

        assetsTried = true;

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            Game.LoadPackage(Package);
        }
        catch (Exception exception)
        {
            ModLog.Warn("Props: loading Robin's package failed: " + Util.Unwrap(exception));
        }

        mesh = Game.FindObject<SkeletalMesh>(MeshPath);

        if (mesh != null)
        {
            mesh.AddToRoot();

            foreach (MaterialInterface material in mesh.Materials)
            {
                material?.AddToRoot();
            }
        }

        AnimSet? movement = Game.FindObject<AnimSet>(MovementSet);

        if (movement != null)
        {
            movement.AddToRoot();

            foreach (AnimSequence sequence in movement.Sequences)
            {
                if (sequence != null && sequence.SequenceName.ToString() == PutAwayName)
                {
                    PutAway = sequence;
                }
            }
        }

        FName extendName = new FName("Play_Staff_EXTEND");
        FName retractName = new FName("Play_Staff_RETRACT");

        foreach (AkEvent ev in Game.FindObjects<AkEvent>())
        {
            if (ev.Name == extendName && ExtendEvent == null)
            {
                ExtendEvent = ev;
                ev.AddToRoot();
            }
            else if (ev.Name == retractName && RetractEvent == null)
            {
                RetractEvent = ev;
                ev.AddToRoot();
            }
        }

        foreach (AkBank bank in Game.FindObjects<AkBank>())
        {
            if (bank.Name.ToString() == "CHR_RobinStaff")
            {
                bank.AddToRoot();
            }
        }

        ModLog.Info("Props: assets in " + watch.ElapsedMilliseconds + " ms (staff " + (mesh != null ? "ok" : "MISSING") + ", put-away animation " + (PutAway != null ? "ok" : "MISSING") + ", sounds " + (ExtendEvent != null && RetractEvent != null ? "ok" : "MISSING") + ").");
    }

    public static void RemoveLeftovers(RPawnPlayer pawn)
    {
        if (comp == null && pawn.Mesh is SkeletalMeshComponent body && body.IsValid)
        {
            RemoveStale(body);
        }
    }

    public static void Forget()
    {
        comp = null;
        compLeft = null;
        compPawn = IntPtr.Zero;
        shown = false;
        leftHand = false;
    }

    public static void Show(RPawnPlayer pawn, bool show)
    {
        if (!show && comp == null)
        {
            return;
        }

        SkeletalMeshComponent? body = pawn.Mesh;

        if (body == null || !body.IsValid || mesh == null)
        {
            return;
        }

        if (compPawn != pawn.Ptr || comp == null || !comp.IsValid)
        {
            RemoveStale(body);
            comp = Create(pawn, body, "R", HandBone);
            compLeft = Create(pawn, body, "L", LeftHandBone);
            compLeft.SetHidden(true);
            compPawn = pawn.Ptr;
            shown = true;
            leftHand = false;
            ModLog.Info("Props: staff attached to Batman's " + HandBone + " and " + LeftHandBone + " bones.");
        }

        if (show != shown)
        {
            comp.SetHidden(!show || leftHand);
            compLeft?.SetHidden(!show || !leftHand);
            shown = show;
        }
    }

    private static SkeletalMeshComponent Create(RPawnPlayer pawn, SkeletalMeshComponent body, string side, string bone)
    {
        SkeletalMeshComponent c = GameObject.ConstructObject<SkeletalMeshComponent>(pawn, CompPrefix + side + Environment.TickCount, (GameObject.EObjectFlags)0, null!);

        c.SetSkeletalMesh(mesh, false);
        c.SetLightingChannels(body.LightingChannels);
        body.AttachComponent(c, new FName(bone), Vector3.Zero, new Rotator(0, 0, 0), Vector3.One);
        c.SetTranslation(HandOffset);
        c.SetRotation(HandRot);

        return c;
    }

    private static readonly FName RightHandName = new FName("Bip01_R_Hand");
    private static readonly FName LeftHandName = new FName("Bip01_L_Hand");
    private static readonly FName RightGripName = new FName(HandBone);
    private static readonly FName LeftGripName = new FName(LeftHandBone);

    public static void Tick(RPawnPlayer pawn)
    {
        if (!shown || comp == null || compLeft == null || compPawn != pawn.Ptr || pawn.Mesh is not SkeletalMeshComponent body)
        {
            return;
        }

        bool left = leftHand;

        if (pawn.CombatMove == null)
        {
            left = false;
        }
        else
        {
            float right = Vector3.Distance(body.GetBoneLocation(RightGripName, 0), body.GetBoneLocation(RightHandName, 0));
            float other = Vector3.Distance(body.GetBoneLocation(LeftGripName, 0), body.GetBoneLocation(LeftHandName, 0));

            if (other > right + MirrorMargin)
            {
                left = true;
            }
            else if (right > other + MirrorMargin)
            {
                left = false;
            }
        }

        if (left != leftHand)
        {
            leftHand = left;
            comp.SetHidden(left);
            compLeft.SetHidden(!left);
        }

        if (left)
        {
            FitToFist(compLeft, body, LeftGripName, LeftHandName);
        }
        else
        {
            FitToFist(comp, body, RightGripName, RightHandName);
        }
    }

    private static void FitToFist(SkeletalMeshComponent staff, SkeletalMeshComponent body, FName grip, FName hand)
    {
        Vector3 hx = body.GetBoneAxis(hand, GameObject.EAxis.AXIS_X);
        Vector3 hy = body.GetBoneAxis(hand, GameObject.EAxis.AXIS_Y);
        Vector3 hz = body.GetBoneAxis(hand, GameObject.EAxis.AXIS_Z);
        Vector3 gx = body.GetBoneAxis(grip, GameObject.EAxis.AXIS_X);
        Vector3 gy = body.GetBoneAxis(grip, GameObject.EAxis.AXIS_Y);
        Vector3 gz = body.GetBoneAxis(grip, GameObject.EAxis.AXIS_Z);

        if (hx.LengthSquared() < 0.5f || hy.LengthSquared() < 0.5f || hz.LengthSquared() < 0.5f || gx.LengthSquared() < 0.5f || gy.LengthSquared() < 0.5f || gz.LengthSquared() < 0.5f)
        {
            return;
        }

        Vector3 offset = body.GetBoneLocation(grip, 0) - body.GetBoneLocation(hand, 0);
        float x = Vector3.Dot(offset, hx);
        float y = Vector3.Dot(offset, hy);
        float z = Vector3.Dot(offset, hz);
        float ownPose = Math.Clamp((x - OwnPoseFrom) / (OwnPoseFull - OwnPoseFrom), 0f, 1f);
        Vector3 move = hx * (FistX - x) + hy * (FistY - y) - hz * (z * ownPose);

        staff.SetTranslation(HandOffset + new Vector3(Vector3.Dot(move, gx), Vector3.Dot(move, gy), Vector3.Dot(move, gz)));
    }

    public static void Detach(RPawnPlayer pawn)
    {
        try
        {
            if (comp != null && comp.IsValid && compPawn == pawn.Ptr && pawn.Mesh != null && pawn.Mesh.IsValid)
            {
                pawn.Mesh.DetachComponent(comp);

                if (compLeft != null && compLeft.IsValid)
                {
                    pawn.Mesh.DetachComponent(compLeft);
                }
            }
        }
        catch
        {
        }

        Forget();
    }

    private static void RemoveStale(SkeletalMeshComponent body)
    {
        List<ActorComponent> stale = new List<ActorComponent>();

        foreach (var attachment in body.Attachments)
        {
            if (attachment.Component != null && attachment.Component.GetPathName(null).Contains(CompPrefix))
            {
                stale.Add(attachment.Component);
            }
        }

        foreach (ActorComponent component in stale)
        {
            body.DetachComponent(component);
        }

        if (stale.Count > 0)
        {
            ModLog.Info("Props: removed " + stale.Count + " leftover staff component(s) from a script reload.");
        }
    }

    public static string Report(RPawnPlayer pawn)
    {
        string s = "staff " + (comp == null ? "not attached" : shown ? "shown in the " + (leftHand ? "left" : "right") + " hand" : "hidden");
        SkeletalMeshComponent? body = pawn.Mesh;

        if (body != null && comp != null)
        {
            Vector3 hand = body.GetBoneLocation(new FName("Bip01_R_Hand"), 0);
            Vector3 grip = body.GetBoneLocation(new FName(HandBone), 0);
            Vector3 handL = body.GetBoneLocation(new FName("Bip01_L_Hand"), 0);
            Vector3 gripL = body.GetBoneLocation(new FName("Gundummy02"), 0);

            s += ", grip to right hand " + Vector3.Distance(hand, grip).ToString("0.0") + ", left " + Vector3.Distance(handL, gripL).ToString("0.0");

            SkeletalMeshComponent active = leftHand && compLeft != null ? compLeft : comp;
            FName handName = leftHand ? LeftHandName : RightHandName;
            Vector3 centre = active.GetBoneLocation(new FName("R_Staff_Hlpr_Skn_Mid_03"), 0) - body.GetBoneLocation(handName, 0);
            Vector3 ax = body.GetBoneAxis(handName, GameObject.EAxis.AXIS_X);
            Vector3 ay = body.GetBoneAxis(handName, GameObject.EAxis.AXIS_Y);
            Vector3 az = body.GetBoneAxis(handName, GameObject.EAxis.AXIS_Z);

            s += ", staff centre in hand " + Vector3.Dot(centre, ax).ToString("0.0") + "," + Vector3.Dot(centre, ay).ToString("0.0") + "," + Vector3.Dot(centre, az).ToString("0.0");
        }

        return s;
    }
}
