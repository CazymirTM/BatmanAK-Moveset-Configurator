#nullable enable

using System;
using System.Collections.Generic;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;
using FLinearColor = BmSDK.GameObject.FLinearColor;

namespace BatmanRedHoodGuns;

internal static unsafe class GunsColor
{
    private const string PaintParent = "VEHICLE_Generic_Van_Small.Generic_Van.Generic_Van_BASE_SILVER_NORAIN_INST";
    private const string RedHoodTexturePrefix = "DLC0_Redhood_Gun.Texture.Red_Hood_Weaponry_";
    private const string WhiteMask = "Character_Shared.Maps.White";
    private const string FlatNormal = "EngineMaterials.DefaultNormal";
    private const string DiffuseParam = "Diffuse RGB";
    private const string NormalParam = "Normal RGB";
    private const string MaskParam = "Masks R=Paint, G=Roughness, B=Reflectivity, A=metal (no paint)";
    private const string PaintParam = "Paint Color";
    private const string RedHoodPrefix = "BatmanGunsPaint_";
    private const string CustomPrefix = "BatmanGunsCustom_";

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(0.2);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(3);

    private sealed class Paint
    {
        public MaterialInstanceConstant? Material;
        public int AppliedColor = -1;
        public int AppliedMask = -1;
        public DateTime NextBuild = DateTime.MinValue;
        public bool Searched;
    }

    private static readonly Paint redHood = new Paint();
    private static readonly Paint customPaint = new Paint();
    private static DateTime nextTick = DateTime.MinValue;
    private static string lastReport = "";

    public static void Tick(RPawnPlayer pawn)
    {
        DateTime now = DateTime.UtcNow;

        if (now < nextTick)
        {
            return;
        }

        nextTick = now + Interval;

        bool colored = GunsConfig.C.GunColor;
        int color = GunsConfig.C.ColorR << 16 | GunsConfig.C.ColorG << 8 | GunsConfig.C.ColorB;
        int painted = 0, restored = 0;

        foreach (SkeletalMeshComponent pistol in Pistols(pawn))
        {
            bool isCustom = GunsModel.IsCustom(pistol.SkeletalMesh);
            MaterialInstanceConstant? wanted = isCustom ? CustomMaterial(now, colored) : colored ? RedHoodMaterial(now) : null;
            MaterialInterface? current = pistol.GetMaterial(0);

            if (wanted == null)
            {
                if (current != null && IsOurs(current))
                {
                    pistol.SetMaterial(0, null!);
                    restored++;
                }

                continue;
            }

            Paint paint = isCustom ? customPaint : redHood;

            if (paint.AppliedColor != color && SetColor(wanted))
            {
                paint.AppliedColor = color;
            }

            if (current == null || current.Ptr != wanted.Ptr)
            {
                pistol.SetMaterial(0, wanted);
                painted++;
            }
        }

        string report = painted > 0 || restored > 0 ? "painted " + painted + ", restored " + restored + (colored ? ", colour " + GunsConfig.C.ColorR + "," + GunsConfig.C.ColorG + "," + GunsConfig.C.ColorB : ", no colour") : lastReport;

        if (report != lastReport)
        {
            lastReport = report;
            ModLog.Info("Color: " + report + ".");
        }
    }

    public static void Forget()
    {
        lastReport = "";
    }

    private static bool IsOurs(MaterialInterface material)
    {
        string name = material.Name.ToString();

        return name.StartsWith(RedHoodPrefix, StringComparison.Ordinal) || name.StartsWith(CustomPrefix, StringComparison.Ordinal);
    }

    private static List<SkeletalMeshComponent> Pistols(RPawnPlayer pawn)
    {
        List<SkeletalMeshComponent> list = new List<SkeletalMeshComponent>();
        SkeletalMeshComponent? body = pawn.Mesh;

        if (body != null && body.IsValid)
        {
            var attachments = body.Attachments;

            for (int index = 0; index < attachments.Count; index++)
            {
                if (attachments[index].Component is SkeletalMeshComponent prop && IsPistol(prop))
                {
                    list.Add(prop);
                }
            }
        }

        RInventoryGadget? gun = Guns.Gun;

        if (gun != null && gun.IsValid && gun.Owner != null && gun.Owner.Ptr == pawn.Ptr)
        {
            foreach (MeshComponent? mesh in new[] { gun.Mesh, gun.Mesh2 })
            {
                if (mesh is SkeletalMeshComponent prop && IsPistol(prop) && !list.Exists(p => p.Ptr == prop.Ptr))
                {
                    list.Add(prop);
                }
            }
        }

        return list;
    }

    private static bool IsPistol(SkeletalMeshComponent prop) =>
        prop.IsValid && GunsModel.IsPistolMesh(prop.SkeletalMesh) && prop.GetNumElements() > 0;

    private static MaterialInstanceConstant? RedHoodMaterial(DateTime now) =>
        Build(redHood, RedHoodPrefix, now, RedHoodTexturePrefix + "D", RedHoodTexturePrefix + "N", WhiteMask);

    private static MaterialInstanceConstant? CustomMaterial(DateTime now, bool colored)
    {
        MaterialInstanceConstant? material = Build(customPaint, CustomPrefix, now, GunsModel.TexturePrefix + "D", FlatNormal, GunsModel.TexturePrefix + "N");

        if (material == null)
        {
            return null;
        }

        int mask = colored ? 1 : 0;

        if (customPaint.AppliedMask != mask)
        {
            Texture? texture = Game.FindObject<Texture>(GunsModel.TexturePrefix + (colored ? "R" : "N"));

            if (texture != null)
            {
                material.SetTextureParameterValue(new FName(MaskParam), texture);
                customPaint.AppliedMask = mask;
            }
        }

        return material;
    }

    private static MaterialInstanceConstant? Build(Paint paint, string prefix, DateTime now, string diffusePath, string normalPath, string maskPath)
    {
        if (paint.Material != null && paint.Material.IsValid)
        {
            return paint.Material;
        }

        if (now < paint.NextBuild)
        {
            return null;
        }

        paint.NextBuild = now + RetryInterval;

        try
        {
            if (!paint.Searched)
            {
                paint.Searched = true;

                foreach (MaterialInstanceConstant previous in Game.FindObjects<MaterialInstanceConstant>())
                {
                    if (previous != null && previous.IsValid && previous.Name.ToString().StartsWith(prefix, StringComparison.Ordinal))
                    {
                        paint.Material = previous;
                        paint.AppliedColor = -1;
                        paint.AppliedMask = -1;
                        ModLog.Info("Color: reusing the material " + previous.Name + " from before the reload.");

                        return paint.Material;
                    }
                }
            }

            MaterialInterface? parent = Game.FindObject<MaterialInterface>(PaintParent);
            Texture? diffuse = Game.FindObject<Texture>(diffusePath);
            Texture? normal = Game.FindObject<Texture>(normalPath);
            Texture? mask = Game.FindObject<Texture>(maskPath);

            if (parent == null || diffuse == null || normal == null || mask == null)
            {
                ModLog.Once("color-missing-" + prefix, "Color: waiting for assets for " + prefix + " (paint " + (parent != null) + ", diffuse " + (diffuse != null) + ", normal " + (normal != null) + ", mask " + (mask != null) + ").");

                return null;
            }

            MaterialInstanceConstant created = GameObject.ConstructObject<MaterialInstanceConstant>(null!, prefix + Environment.TickCount, (GameObject.EObjectFlags)0, null!);

            created.AddToRoot();
            created.SetParent(parent);
            created.SetTextureParameterValue(new FName(DiffuseParam), diffuse);
            created.SetTextureParameterValue(new FName(NormalParam), normal);
            created.SetTextureParameterValue(new FName(MaskParam), mask);

            paint.Material = created;
            paint.AppliedColor = -1;
            paint.AppliedMask = -1;
            ModLog.Info("Color: built the material " + created.Name + " on " + parent.Name + ".");

            return paint.Material;
        }
        catch (Exception exception)
        {
            ModLog.Once("color-build-" + prefix, "Color: building the material failed: " + Util.Unwrap(exception));

            return null;
        }
    }

    private static bool SetColor(MaterialInstanceConstant material)
    {
        FLinearColor color = new FLinearColor
        {
            R = Linear(GunsConfig.C.ColorR),
            G = Linear(GunsConfig.C.ColorG),
            B = Linear(GunsConfig.C.ColorB),
            A = 1f,
        };

        return UCall.Call(material, "SetVectorParameterValue", frame =>
        {
            MarshalUtil.ToUnmanaged(new FName(PaintParam), (void*)frame["ParameterName"]);
            MarshalUtil.ToUnmanaged(color, (void*)frame["Value"]);
        });
    }

    private static float Linear(byte value) => MathF.Pow(value / 255f, 2.2f);
}
