#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace BatmanRedHoodGuns;

internal static class GunsGrip
{
    private const string ControlPrefix = "RHGunsGrip_";

    public static float KnuckleBend = 55f;
    public static float MiddleBend = 56f;
    public static float TipBend = 40f;

    public static float BendSign = -1f;
    public static float RightSign = -1f;

    public static float[] KnuckleTarget = { 85f, 81f, 75f, 68f };
    public static float[] MiddleTarget = { 90f, 90f, 79f, 70f };
    public static float MaxBend = 95f;
    public static float Gain = 0.35f;

    private const float BlendTime = 0.15f;

    public static bool StaffHand;

    private static readonly string[] Fingers = { "1", "2", "3", "4" };
    private static readonly string[] Joints = { "", "1", "2" };
    private static readonly string[] Sides = { "L", "R" };

    private sealed class Joint
    {
        public SkelControlSingleBone Control = null!;
        public string Side = "";
        public string Finger = "";
        public int Index;
        public float Applied;
    }

    private static readonly List<Joint> joints = new List<Joint>();
    private static IntPtr treePtr = IntPtr.Zero;
    private static DateTime lastTick = DateTime.MinValue;
    public static float CurlSignL = 1f;
    public static float CurlSignR = -1f;

    public static void Reset()
    {
        joints.Clear();
        treePtr = IntPtr.Zero;
    }

    public static void Tick(RPawnPlayer? pawn, bool gunsHeld, bool pistolsInHand)
    {
        DateTime now = DateTime.UtcNow;
        float deltaTime = Math.Clamp((float)(now - lastTick).TotalSeconds, 0f, 0.1f);

        lastTick = now;

        if (pawn == null || !pawn.IsValid)
        {
            return;
        }

        try
        {
            if (!pistolsInHand && !StaffHand && joints.Count == 0)
            {
                return;
            }

            if (!Install(pawn))
            {
                return;
            }

            SkeletalMeshComponent body = pawn.Mesh!;
            float blend = deltaTime / BlendTime;

            foreach (Joint joint in joints)
            {
                float target;

                if (!pistolsInHand)
                {
                    target = StaffHand && joint.Side == "R" ? (joint.Index == 2 ? TipShare * MiddleApplied(joint) : Adaptive(body, joint)) : 0f;
                }
                else if (gunsHeld)
                {
                    target = joint.Side == "L" ? FixedBend(joint.Index) : 0f;
                }
                else
                {
                    target = joint.Index == 2 ? TipShare * MiddleApplied(joint) : Adaptive(body, joint);
                }

                joint.Applied = MoveTowards(joint.Applied, target, MaxBend * blend);
                joint.Control.ControlStrength = joint.Applied != 0f ? 1f : 0f;
                joint.Control.StrengthTarget = joint.Control.ControlStrength;
                joint.Control.BoneRotation = new Rotator(0f, joint.Applied * (joint.Side == "L" ? BendSign : RightSign), 0f);
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("grip-failed", "Grip: closing the hands failed: " + Util.Unwrap(exception));
        }
    }

    public static float TipShare = 0.6f;

    private static float MiddleApplied(Joint tip)
    {
        foreach (Joint joint in joints)
        {
            if (joint.Side == tip.Side && joint.Finger == tip.Finger && joint.Index == 1)
            {
                return joint.Applied;
            }
        }

        return 0f;
    }

    private static float FixedBend(int index) => index == 0 ? KnuckleBend : index == 1 ? MiddleBend : TipBend;

    private static float MoveTowards(float value, float target, float step) =>
        value < target ? MathF.Min(target, value + step) : MathF.Max(target, value - step);

    private static float Adaptive(SkeletalMeshComponent body, Joint joint)
    {
        int measured = Math.Min(joint.Index, 1);
        float curl = Curl(body, joint.Side, joint.Finger, measured);

        if (float.IsNaN(curl))
        {
            return joint.Applied;
        }

        int finger = int.Parse(joint.Finger) - 1;
        float target = measured == 0 ? KnuckleTarget[finger] : MiddleTarget[finger];

        return Math.Clamp(joint.Applied + Gain * (target - curl), 0f, MaxBend);
    }

    private static Vector3 Bone(SkeletalMeshComponent body, string name) => body.GetBoneLocation(new FName(name));

    public static float Curl(SkeletalMeshComponent body, string side, string finger, int joint)
    {
        string root = "Bip01_" + side + "_Finger";
        Vector3 hand = Bone(body, "Bip01_" + side + "_Hand");
        Vector3 knuckle = Bone(body, root + finger);
        Vector3 middle = Bone(body, root + finger + "1");
        Vector3 tip = Bone(body, root + finger + "2");

        Vector3 a = joint == 0 ? knuckle - hand : middle - knuckle;
        Vector3 b = joint == 0 ? middle - knuckle : tip - middle;

        if (a.LengthSquared() < 1e-4f || b.LengthSquared() < 1e-4f)
        {
            return float.NaN;
        }

        a = Vector3.Normalize(a);
        b = Vector3.Normalize(b);

        float angle = MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1f, 1f)) * 180f / MathF.PI;
        Vector3 lateral = Bone(body, root + "1") - Bone(body, root + "4");

        if (lateral.LengthSquared() < 1e-4f)
        {
            return angle;
        }

        float sign = MathF.Sign(Vector3.Dot(Vector3.Cross(a, b), Vector3.Normalize(lateral)));

        float gripSign = side == "L" ? CurlSignL : CurlSignR;

        return angle * (sign == 0f ? 1f : sign * gripSign);
    }

    public static string Describe()
    {
        SkeletalMeshComponent? body = Guns.Batman()?.Mesh;

        if (body == null || !body.IsValid)
        {
            return "<no mesh>";
        }

        List<string> parts = new List<string>();

        foreach (string side in Sides)
        {
            foreach (string finger in Fingers)
            {
                float k = Curl(body, side, finger, 0);
                float m = Curl(body, side, finger, 1);
                string applied = string.Join("/", joints.Where(j => j.Side == side && j.Finger == finger).Select(j => j.Applied.ToString("0")));

                parts.Add(side + finger + " " + k.ToString("0") + "," + m.ToString("0") + " (+" + applied + ")");
            }
        }

        return string.Join("; ", parts);
    }

    private static bool Install(RPawnPlayer pawn)
    {
        SkeletalMeshComponent? body = pawn.Mesh;

        if (body == null || !body.IsValid || body.Animations is not AnimTree tree || !tree.IsValid)
        {
            return false;
        }

        if (tree.Ptr == treePtr && joints.Count > 0)
        {
            return true;
        }

        joints.Clear();
        treePtr = tree.Ptr;

        bool added = false;

        foreach (string side in Sides)
        {
            foreach (string finger in Fingers)
            {
                for (int index = 0; index < Joints.Length; index++)
                {
                    string bone = "Bip01_" + side + "_Finger" + finger + Joints[index];
                    FName boneName = new FName(bone);

                    if (body.MatchRefBone(boneName) < 0)
                    {
                        continue;
                    }

                    FName controlName = new FName(ControlPrefix + bone);
                    SkelControlSingleBone? control = tree.FindSkelControl(controlName) as SkelControlSingleBone;

                    if (control == null || !control.IsValid)
                    {
                        control = new SkelControlSingleBone(tree);
                        control.ControlName = controlName;
                        control.bApplyRotation = true;
                        control.bAddRotation = true;
                        control.BoneRotationSpace = SkelControlBase.EBoneControlSpace.BCS_BoneSpace;
                        control.ControlStrength = 0f;
                        control.StrengthTarget = 0f;
                        control.AddToRoot();

                        AnimTree.FSkelControlListHead head = default;

                        head.BoneName = boneName;
                        head.ControlHead = control;
                        tree.SkelControlLists.Add(head);
                        added = true;
                    }

                    joints.Add(new Joint { Control = control, Side = side, Finger = finger, Index = index });
                }
            }
        }

        if (added)
        {
            body.InitSkelControls();
        }

        ModLog.Info("Grip: " + joints.Count + " finger controls (both hands) " + (added ? "added to" : "found in") + " Batman's anim tree.");

        return joints.Count > 0;
    }
}
