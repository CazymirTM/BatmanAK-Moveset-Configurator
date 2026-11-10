#nullable enable

using System;
using System.IO;
using BmSDK;
using BmSDK.Engine;
using BmSDK.Framework;

namespace BatmanRedHoodGuns;

internal static class GunsModel
{
    public const string PackageName = "BatmanArsenalGun_SF";
    public const string MeshName = "BMAR_Deagle1_Skin";
    private const string MeshPath = "BMAR_Deagle1_Mesh.Mesh." + MeshName;
    public const string TexturePrefix = "BMAR_Deagle1.Maps.BMAR_Deagle1_";
    private const string RedHoodMeshName = "RH_GUN";

    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private static SkeletalMesh? custom;
    private static DateTime nextTry = DateTime.MinValue;
    private static bool packageLoaded;
    private static bool? lastWanted;

    public static bool Wanted => GunsConfig.C.CustomModel;

    public static bool Active => Wanted && custom != null && custom.IsValid;

    public static bool PackageInstalled()
    {
        try
        {
            return File.Exists(Path.Combine(FileUtils.GetGamePath(), "CookedPCConsole", PackageName + ".upk"));
        }
        catch
        {
            return false;
        }
    }

    public static void Tick()
    {
        bool wanted = Wanted;

        if (lastWanted.HasValue && lastWanted.Value != wanted)
        {
            ModLog.Info("Model: switching to " + (wanted ? "the custom model" : "Red Hood's pistols") + ".");
            GunsAim.DetachPistols();
        }

        lastWanted = wanted;
    }

    public static SkeletalMesh? Pick(SkeletalMesh? redHood)
    {
        if (!Wanted)
        {
            return redHood;
        }

        return CustomMesh() ?? redHood;
    }

    public static bool IsPistolMesh(SkeletalMesh? mesh)
    {
        if (mesh == null || !mesh.IsValid)
        {
            return false;
        }

        string name = mesh.Name.ToString();

        return name == RedHoodMeshName || name == MeshName;
    }

    private static DateTime nextScale = DateTime.MinValue;

    public static void ApplyScale(BmSDK.BmGame.RPawnPlayer pawn)
    {
        DateTime now = DateTime.UtcNow;

        if (now < nextScale)
        {
            return;
        }

        nextScale = now + TimeSpan.FromSeconds(0.2);

        SkeletalMeshComponent? body = pawn.Mesh;

        if (body == null || !body.IsValid)
        {
            return;
        }

        var attachments = body.Attachments;

        for (int index = 0; index < attachments.Count; index++)
        {
            if (attachments[index].Component is not SkeletalMeshComponent prop || !prop.IsValid || !IsCustom(prop.SkeletalMesh))
            {
                continue;
            }

            if (prop.BoundsType != RSkeletalMeshComponent_Export.ESkeletalMeshComponentBoundsType.SMCBT_ReferencePose)
            {
                prop.BoundsType = RSkeletalMeshComponent_Export.ESkeletalMeshComponentBoundsType.SMCBT_ReferencePose;
            }

            float wanted = GunsConfig.C.CustomScale;

            if (MathF.Abs(prop.Scale - wanted) > 0.001f)
            {
                prop.SetScale(wanted);
            }
        }
    }

    public static bool IsCustom(SkeletalMesh? mesh) => mesh != null && mesh.IsValid && mesh.Name.ToString() == MeshName;

    public static SkeletalMesh? CustomMesh()
    {
        if (custom != null && custom.IsValid)
        {
            return custom;
        }

        DateTime now = DateTime.UtcNow;

        if (now < nextTry)
        {
            return null;
        }

        nextTry = now + RetryInterval;

        try
        {
            SkeletalMesh? mesh = Game.FindObject<SkeletalMesh>(MeshPath);

            if (mesh == null && !packageLoaded)
            {
                if (!PackageInstalled())
                {
                    ModLog.Once("model-missing", "Model: " + PackageName + ".upk is not in BmGame\\CookedPCConsole, using Red Hood's pistols.");

                    return null;
                }

                packageLoaded = true;
                Game.LoadPackage(PackageName);
                mesh = Game.FindObject<SkeletalMesh>(MeshPath);
            }

            if (mesh == null || !mesh.IsValid)
            {
                ModLog.Once("model-nomesh", "Model: " + MeshPath + " not found in " + PackageName + ", using Red Hood's pistols.");

                return null;
            }

            mesh.AddToRoot();

            var bounds = mesh.Bounds;

            bounds.Origin = new System.Numerics.Vector3(5.2f, 0f, 0.9f);
            bounds.BoxExtent = new System.Numerics.Vector3(22f, 6f, 14f);
            bounds.SphereRadius = 27f;
            mesh.Bounds = bounds;

            foreach (string suffix in new[] { "D", "N", "R" })
            {
                Texture? texture = Game.FindObject<Texture>(TexturePrefix + suffix);
                texture?.AddToRoot();
            }

            custom = mesh;
            ModLog.Info("Model: loaded the custom pistol " + MeshName + " from " + PackageName + ".");

            return custom;
        }
        catch (Exception exception)
        {
            ModLog.Once("model-load", "Model: loading the custom pistol failed: " + Util.Unwrap(exception));

            return null;
        }
    }
}
