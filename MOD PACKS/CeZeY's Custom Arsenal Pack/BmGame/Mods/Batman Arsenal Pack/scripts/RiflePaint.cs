#nullable enable

using System;
using BmSDK;
using BmSDK.Engine;
using BmSDK.Framework;
using FLinearColor = BmSDK.GameObject.FLinearColor;

namespace BatmanAssaultRifle;

internal static unsafe class RiflePaint
{
    private const string PaintParent = "VEHICLE_Generic_Van_Small.Generic_Van.Generic_Van_BASE_SILVER_NORAIN_INST";
    private const string DiffuseParam = "Diffuse RGB";
    private const string NormalParam = "Normal RGB";
    private const string MaskParam = "Masks R=Paint, G=Roughness, B=Reflectivity, A=metal (no paint)";
    private const string PaintParam = "Paint Color";
    private const string Prefix = "BatmanM4Paint_";

    private static MaterialInstanceConstant? material;
    private static DateTime nextBuild = DateTime.MinValue;
    private static bool searched;
    private static int appliedColor = -1;
    private static string appliedMask = "";
    private static string appliedDiffuse = "";
    private static readonly (byte R, byte G, byte B) Gold = (255, 190, 70);
    private static readonly (byte R, byte G, byte B) White = (255, 255, 255);

    public static string State => material == null ? "none" : "mask " + Short(appliedMask) + ", diffuse " + Short(appliedDiffuse) + ", colour #" + appliedColor.ToString("X6");

    private static string Short(string p) => p.Length == 0 ? "-" : p.Substring(p.LastIndexOf('.') + 1);

    public static void Apply(SkeletalMeshComponent comp, bool m4)
    {
        try
        {
            if (!comp.IsValid || comp.GetNumElements() <= 0)
            {
                return;
            }

            MaterialInterface? current = comp.GetMaterial(0);

            if (!m4)
            {
                if (current != null && current.Name.ToString().StartsWith(Prefix, StringComparison.Ordinal))
                {
                    comp.SetMaterial(0, null!);
                }

                return;
            }

            MaterialInstanceConstant? wanted = Material();

            if (wanted == null)
            {
                return;
            }

            RifleConfig.Settings c = RifleConfig.C;
            Pick(c, out string mask, out string diffuse, out byte cr, out byte cg, out byte cb);

            if (appliedMask != mask && Game.FindObject<Texture>(mask) is Texture maskTexture)
            {
                wanted.SetTextureParameterValue(new FName(MaskParam), maskTexture);
                appliedMask = mask;
            }

            if (appliedDiffuse != diffuse && Game.FindObject<Texture>(diffuse) is Texture diffuseTexture)
            {
                wanted.SetTextureParameterValue(new FName(DiffuseParam), diffuseTexture);
                appliedDiffuse = diffuse;
                ModLog.Info("Paint: M4 look paint " + (c.RiflePaint ? "on" : "off") + ", camo " + c.RifleCamo + " (" + State + ").");
            }

            int color = cr << 16 | cg << 8 | cb;

            if (appliedColor != color && SetColor(wanted, cr, cg, cb))
            {
                appliedColor = color;
            }

            if (current == null || current.Ptr != wanted.Ptr)
            {
                comp.SetMaterial(0, wanted);
                ModLog.Info("Paint: the M4 material is on " + comp.Name + ".");
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("m4paint-apply", "Paint: applying the M4 material failed: " + Util.Unwrap(exception));
        }
    }

    private static MaterialInstanceConstant? Material()
    {
        if (material != null && material.IsValid)
        {
            return material;
        }

        DateTime now = DateTime.UtcNow;

        if (now < nextBuild)
        {
            return null;
        }

        nextBuild = now + TimeSpan.FromSeconds(3);

        if (!searched)
        {
            searched = true;

            foreach (MaterialInstanceConstant previous in Game.FindObjects<MaterialInstanceConstant>())
            {
                if (previous != null && previous.IsValid && previous.Name.ToString().StartsWith(Prefix, StringComparison.Ordinal))
                {
                    material = previous;
                    appliedColor = -1;
                    appliedMask = "";
                    appliedDiffuse = "";
                    ModLog.Info("Paint: reusing the M4 material " + previous.Name + " from before the reload.");

                    return material;
                }
            }
        }

        MaterialInterface? parent = Game.FindObject<MaterialInterface>(PaintParent);
        Texture? diffuse = Game.FindObject<Texture>(RifleProps.M4Diffuse);
        Texture? normal = Game.FindObject<Texture>(RifleProps.M4Normal);
        Texture? mask = Game.FindObject<Texture>(RifleProps.M4AllMask);

        if (parent == null || diffuse == null || normal == null || mask == null)
        {
            ModLog.Once("m4paint-missing", "Paint: waiting for the M4 assets (paint " + (parent != null) + ", diffuse " + (diffuse != null) + ", normal " + (normal != null) + ", mask " + (mask != null) + ").");

            return null;
        }

        MaterialInstanceConstant created = GameObject.ConstructObject<MaterialInstanceConstant>(null!, Prefix + Environment.TickCount, (GameObject.EObjectFlags)0, null!);

        created.AddToRoot();
        created.SetParent(parent);
        created.SetTextureParameterValue(new FName(DiffuseParam), diffuse);
        created.SetTextureParameterValue(new FName(NormalParam), normal);
        created.SetTextureParameterValue(new FName(MaskParam), mask);

        material = created;
        appliedColor = -1;
        appliedMask = RifleProps.M4AllMask;
        appliedDiffuse = RifleProps.M4Diffuse;
        ModLog.Info("Paint: built the M4 material " + created.Name + " on " + parent.Name + ".");

        return material;
    }

    private static void Pick(RifleConfig.Settings c, out string mask, out string diffuse, out byte r, out byte g, out byte b)
    {
        (r, g, b) = (c.ColorR, c.ColorG, c.ColorB);

        switch (c.RifleCamo)
        {
            case "colored":
                mask = c.RiflePaint ? RifleProps.M4AllMask : RifleProps.M4CamoMask;
                diffuse = c.RiflePaint ? RifleProps.M4PaintDiffuse : RifleProps.M4CamoDiffuse;
                break;
            case "gold":
                if (c.RiflePaint)
                {
                    mask = RifleProps.M4BodyMask;
                    diffuse = RifleProps.M4PaintDiffuse;
                }
                else
                {
                    mask = RifleProps.M4CamoMask;
                    diffuse = RifleProps.M4CamoDiffuse;
                    (r, g, b) = Gold;
                }

                break;
            default:
                mask = RifleProps.M4AllMask;

                if (c.RiflePaint)
                {
                    diffuse = RifleProps.M4PaintDiffuse;
                }
                else
                {
                    diffuse = RifleProps.M4Diffuse;
                    (r, g, b) = White;
                }

                break;
        }
    }

    private static bool SetColor(MaterialInstanceConstant target, byte cr, byte cg, byte cb)
    {
        FLinearColor color = new FLinearColor
        {
            R = MathF.Pow(cr / 255f, 2.2f),
            G = MathF.Pow(cg / 255f, 2.2f),
            B = MathF.Pow(cb / 255f, 2.2f),
            A = 1f,
        };

        return BatmanRedHoodGuns.UCall.Call(target, "SetVectorParameterValue", frame =>
        {
            MarshalUtil.ToUnmanaged(new FName(PaintParam), (void*)frame["ParameterName"]);
            MarshalUtil.ToUnmanaged(color, (void*)frame["Value"]);
        });
    }
}
