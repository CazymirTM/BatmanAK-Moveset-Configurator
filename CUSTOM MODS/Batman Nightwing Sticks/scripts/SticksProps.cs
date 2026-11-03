#nullable enable

using System;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace BatmanNightwingSticks;

internal static class SticksProps
{
    private const string StickMeshPath = "Nightwing.Mesh.Weapons_NightWing_Sticks";
    private const string HolsterBone = "Bip01_Spine2";

    public static Vector3 HolsterCenter = new Vector3(10f, 18f, 0f);
    public static float HolsterAngle = 36f;
    public static float StickHalfLength = 32f;

    private static SkeletalMeshComponent? rightHand;
    private static SkeletalMeshComponent? leftHand;
    private static SkeletalMeshComponent? backLeft;
    private static SkeletalMeshComponent? backRight;
    private static IntPtr propsPawn = IntPtr.Zero;
    private static bool failed;

    public static void Reset()
    {
        Forget();
        failed = false;
    }

    private static void Forget()
    {
        rightHand = null;
        leftHand = null;
        backLeft = null;
        backRight = null;
        propsPawn = IntPtr.Zero;
    }

    public static void Tick(RPawnPlayer? pawn)
    {
        RInventoryGadget? sticks = Sticks.Gadget;

        if (pawn == null || !pawn.IsValid || sticks == null || !sticks.IsValid || failed)
        {
            return;
        }

        if (pawn.Ptr != propsPawn || rightHand == null || !rightHand.IsValid)
        {
            Create(pawn);
        }

        bool held = Sticks.HeldBy(out _, out _);
        RInventoryGadget active = Sticks.Active ?? sticks;
        bool blast = Sticks.IsBlast(active);
        bool inHand = held && (active.GadgetState == RInventoryGadget.GadgetEquipState.GS_AttachetToHand || active.bAiming);
        bool flying = SticksFlight.InFlight;
        bool bothHands = inHand && blast;

        Show(rightHand, inHand && !flying);
        Show(leftHand, bothHands);
        Show(backLeft, !bothHands);
        Show(backRight, !inHand && !flying);

        if (active.Mesh != null && active.Mesh.IsValid && !active.Mesh.HiddenGame)
        {
            active.Mesh.SetHidden(true);
        }

        if (sticks.Mesh != null && sticks.Mesh.IsValid && !sticks.Mesh.HiddenGame)
        {
            sticks.Mesh.SetHidden(true);
        }
    }

    public static void Detach()
    {
        RPawnPlayer? pawn = Sticks.Batman();

        if (pawn == null || pawn.bDeleteMe || pawn.Ptr != propsPawn || pawn.Mesh is not SkeletalMeshComponent owner || !owner.IsValid)
        {
            Forget();

            return;
        }

        foreach (SkeletalMeshComponent? stick in new[] { rightHand, leftHand, backLeft, backRight })
        {
            try
            {
                if (stick != null && stick.IsValid && stick.AttachedToSkelComponent is SkeletalMeshComponent body && body.Ptr == owner.Ptr)
                {
                    owner.DetachComponent(stick);
                }
            }
            catch
            {
            }
        }

        try
        {
            if (Sticks.Gadget?.Mesh is SkeletalMeshComponent mesh && mesh.IsValid)
            {
                mesh.SetHidden(false);
            }
        }
        catch
        {
        }

        Forget();
    }

    private static void Show(SkeletalMeshComponent? stick, bool show)
    {
        if (stick != null && stick.IsValid && stick.HiddenGame == show)
        {
            stick.SetHidden(!show);
        }
    }

    private static void Create(RPawnPlayer pawn)
    {
        try
        {
            Detach();

            SkeletalMeshComponent? body = pawn.Mesh;
            SkeletalMesh? mesh = Game.FindObject<SkeletalMesh>(StickMeshPath);

            if (body == null || !body.IsValid || mesh == null || !mesh.IsValid)
            {
                ModLog.Once("nostickmesh", "Props: Nightwing's stick mesh is not loaded; Batman keeps the gadget's own mesh.");
                failed = true;

                return;
            }

            string equip = Sticks.Gadget != null && !Util.IsNone(Sticks.Gadget.EquipBoneName) ? Sticks.Gadget.EquipBoneName.ToString() : "Gundummy";

            propsPawn = pawn.Ptr;
            rightHand = Attach(body, mesh, equip, "Bip01_R_Hand", Vector3.Zero, default);
            leftHand = Attach(body, mesh, "Gundummy02", "Bip01_L_Hand", Vector3.Zero, default);
            backLeft = Attach(body, mesh, HolsterBone, "Bip01_Spine1", BackRoot(HolsterAngle), new Rotator(HolsterAngle - 90f, 0f, 0f));
            backRight = Attach(body, mesh, HolsterBone, "Bip01_Spine1", BackRoot(-HolsterAngle), new Rotator(-HolsterAngle - 90f, 0f, 0f));

            ModLog.Info("Props: Nightwing's sticks attached to Batman (hand " + (rightHand != null) + ", back " + (backLeft != null) + "/" + (backRight != null) + ").");
        }
        catch (Exception exception)
        {
            failed = true;
            ModLog.Warn("Props: attaching the sticks failed: " + Util.Unwrap(exception));
        }
    }

    private static Vector3 BackRoot(float angle)
    {
        float radians = angle * MathF.PI / 180f;
        Vector3 along = new Vector3(MathF.Cos(radians), 0f, MathF.Sin(radians));

        return HolsterCenter - along * StickHalfLength;
    }

    private static SkeletalMeshComponent? Attach(SkeletalMeshComponent body, SkeletalMesh mesh, string bone, string fallbackBone, Vector3 offset, Rotator rotation)
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

        SkeletalMeshComponent stick = new SkeletalMeshComponent(body.Owner ?? (GameObject)body);

        stick.SetSkeletalMesh(mesh);
        body.AttachComponent(stick, boneName, offset, rotation);
        stick.SetHidden(true);

        return stick;
    }
}
