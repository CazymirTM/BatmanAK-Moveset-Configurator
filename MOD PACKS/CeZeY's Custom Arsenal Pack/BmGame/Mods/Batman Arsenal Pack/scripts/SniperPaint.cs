#nullable enable

using System;
using BmSDK;
using BmSDK.Engine;
using BmSDK.Framework;
using FLinearColor = BmSDK.GameObject.FLinearColor;

namespace BatmanDeathstrokeSniper;

internal static unsafe class SniperPaint
{
    private const string PaintParent = "VEHICLE_Generic_Van_Small.Generic_Van.Generic_Van_BASE_SILVER_NORAIN_INST";
    private const string FlatNormal = "EngineMaterials.DefaultNormal";
    private const string DiffuseParam = "Diffuse RGB";
    private const string NormalParam = "Normal RGB";
    private const string MaskParam = "Masks R=Paint, G=Roughness, B=Reflectivity, A=metal (no paint)";
    private const string PaintParam = "Paint Color";
    private const string Prefix = "BatmanRiflePaint_";

    public static bool UseNormalMap = true;

    private static MaterialInstanceConstant? material;
    private static DateTime nextBuild = DateTime.MinValue;
    private static bool searched;
    private static int appliedColor = -1;
    private static string appliedMask = "";
    private static string appliedDiffuse = "";
    private static readonly (byte R, byte G, byte B) BodyBlack = (24, 24, 26);

    public static void Apply(SkeletalMeshComponent comp, bool custom)
    {
        try
        {
            if (!comp.IsValid || comp.GetNumElements() <= 0)
            {
                return;
            }

            MaterialInterface? current = comp.GetMaterial(0);

            if (!custom)
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

            SniperConfig.Settings c = SniperConfig.C;
            Pick(c, out string mask, out byte cr, out byte cg, out byte cb);

            if (appliedMask != mask)
            {
                Texture? texture = Game.FindObject<Texture>(mask);

                if (texture == null && mask != SniperProps.CustomTexturePrefix + "R")
                {
                    ModLog.Once("paint-mask-" + mask, "Paint: mask " + mask + " is not in the rifle package (old BatmanArsenalRfl_SF.upk?); camo settings need the new one.");
                    texture = Game.FindObject<Texture>(c.RifleColor ? SniperProps.CustomPaintMask : SniperProps.CustomTexturePrefix + "R");
                }

                if (texture != null)
                {
                    wanted.SetTextureParameterValue(new FName(MaskParam), texture);
                    appliedMask = mask;
                    ModLog.Info("Paint: custom rifle mask " + texture.Name + " (paint " + (c.RifleColor ? "on" : "off") + ", camo " + c.RifleCamo + ").");
                }
            }

            string diffusePath = c.RifleColor ? SniperProps.CustomPaintDiffuse : SniperProps.CustomTexturePrefix + "D";

            if (appliedDiffuse != diffusePath)
            {
                Texture? diffuseTexture = Game.FindObject<Texture>(diffusePath) ?? Game.FindObject<Texture>(SniperProps.CustomTexturePrefix + "D");

                if (diffuseTexture != null)
                {
                    wanted.SetTextureParameterValue(new FName(DiffuseParam), diffuseTexture);
                    appliedDiffuse = diffusePath;
                }
            }

            int color = cr << 16 | cg << 8 | cb;

            if (appliedColor != color && SetColor(wanted, cr, cg, cb))
            {
                appliedColor = color;
            }

            if (current == null || current.Ptr != wanted.Ptr)
            {
                comp.SetMaterial(0, wanted);
                ModLog.Info("Paint: the custom rifle material is on " + comp.Name + ".");
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("paint-apply", "Paint: applying the custom rifle material failed: " + Util.Unwrap(exception));
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
                    ModLog.Info("Paint: reusing the material " + previous.Name + " from before the reload.");

                    return material;
                }
            }
        }

        MaterialInterface? parent = Game.FindObject<MaterialInterface>(PaintParent);
        Texture? diffuse = Game.FindObject<Texture>(SniperProps.CustomTexturePrefix + "D");
        Texture? normal = (UseNormalMap ? Game.FindObject<Texture>(SniperProps.CustomTexturePrefix + "N") : null) ?? Game.FindObject<Texture>(FlatNormal);
        Texture? mask = Game.FindObject<Texture>(SniperProps.CustomTexturePrefix + "R");

        if (parent == null || diffuse == null || normal == null || mask == null)
        {
            ModLog.Once("paint-missing", "Paint: waiting for the custom rifle assets (paint " + (parent != null) + ", diffuse " + (diffuse != null) + ", normal " + (normal != null) + ", mask " + (mask != null) + ").");

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
        appliedMask = "";
        appliedDiffuse = SniperProps.CustomTexturePrefix + "D";
        ModLog.Info("Paint: built the custom rifle material " + created.Name + " on " + parent.Name + " (normal " + normal.Name + ").");

        return material;
    }

    private static void Pick(SniperConfig.Settings c, out string mask, out byte r, out byte g, out byte b)
    {
        (r, g, b) = (c.ColorR, c.ColorG, c.ColorB);

        switch (c.RifleCamo)
        {
            case "colored":
                mask = c.RifleColor ? SniperProps.CustomAllMask : SniperProps.CustomCamoMask;
                break;
            case "off":
                mask = SniperProps.CustomAllMask;

                if (!c.RifleColor)
                {
                    (r, g, b) = BodyBlack;
                }

                break;
            default:
                mask = c.RifleColor ? SniperProps.CustomPaintMask : SniperProps.CustomTexturePrefix + "R";
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
