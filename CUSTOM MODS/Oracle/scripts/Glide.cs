using System;
using System.Globalization;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace CeZeY.OracleScript;

public partial class Oracle
{
    private const string GlidePitchKey = "CeZeY.Oracle.GlideMaxPitch";
    private const float GlideLevelSink = -45f;
    private const float GlideDivePitch = -5f;
    private const float GlideFloor = -1500f;
    private const float RotUnitsPerDegree = 65536f / 360f;

    private static Feature? _infGlide;
    private static Feature? _glideClimb;
    private static nint _glidePc;
    private static nint _glidePawn;
    private static float _glideHoldZ = float.NaN;
    private static float _glideLastSpeed = -1f;

    private static string Degrees(float v) => v.ToString("0", CultureInfo.InvariantCulture) + " DEG";

    private static void BuildGlide()
    {
        Group("GLIDING");
        _infGlide = Fast(Toggle("infinite_glide", "INFINITE GLIDE", "Glide as long as you hold glide. Look up to climb, look ahead to fly level, look down to dive. Batman keeps his normal glide speed and pose.",
            on => { if (!on) RestoreGlide(); }), InfiniteGlideTick);
        _glideClimb = Slider("glide_climb", "GLIDE CLIMB ANGLE", "How steeply Batman climbs when you look up during INFINITE GLIDE. Higher climbs faster.",
            22f, 8f, 40f, 1f, 30f, Degrees, _ => { });
        _glideClimb.Pref = true;
    }

    private static int GlideVanillaPitch(RPlayerController pc)
    {
        if (AppDomain.CurrentDomain.GetData(GlidePitchKey) is int saved)
            return saved;

        int now = pc.CapeGlideMaxPitch;
        AppDomain.CurrentDomain.SetData(GlidePitchKey, now);
        return now;
    }

    private static void InfiniteGlideTick()
    {
        RPlayerController pc = Game.GetPlayerController(0);
        if (pc == null || !pc.IsValid)
            return;

        if (pc.Ptr != _glidePc)
        {
            _glidePc = pc.Ptr;
            ResetGlideHold();
        }

        GlideVanillaPitch(pc);
        int want = (int)MathF.Round((_glideClimb?.Value ?? 22f) * RotUnitsPerDegree);
        if (pc.CapeGlideMaxPitch != want)
            pc.CapeGlideMaxPitch = want;

        if (pc.Pawn is not RPawnPlayer p || !p.IsValid || !p.bGliding || p.Physics != Actor.EPhysics.PHYS_Flying)
        {
            ResetGlideHold();
            return;
        }

        if (p.Ptr != _glidePawn)
        {
            _glidePawn = p.Ptr;
            ResetGlideHold();
        }

        float pitch = pc.GlideRotator.Pitch;
        float speed = pc.CapeGlideSpeed;
        if (pitch > GlideDivePitch && _glideLastSpeed > 0f && speed > _glideLastSpeed + 0.01f)
        {
            speed = MathF.Max(_glideLastSpeed, pc.CapeGlideMinSpeed);
            pc.CapeGlideSpeed = speed;
        }
        _glideLastSpeed = speed;

        Vector3 at = p.Location;
        float vz = p.Velocity.Z;
        bool level = vz < 0f && vz > GlideLevelSink && pitch > GlideDivePitch && at.Z > GlideFloor;
        if (!level || float.IsNaN(_glideHoldZ) || at.Z > _glideHoldZ)
        {
            _glideHoldZ = at.Z;
            return;
        }

        if (at.Z < _glideHoldZ - 0.1f)
            p.SetLocation(new Vector3(at.X, at.Y, _glideHoldZ));
    }

    private static void ResetGlideHold()
    {
        _glideHoldZ = float.NaN;
        _glideLastSpeed = -1f;
    }

    private static void RestoreGlide()
    {
        ResetGlideHold();
        try
        {
            RPlayerController pc = Game.GetPlayerController(0);
            if (pc != null && pc.IsValid && AppDomain.CurrentDomain.GetData(GlidePitchKey) is int vanilla && pc.CapeGlideMaxPitch != vanilla)
                pc.CapeGlideMaxPitch = vanilla;
        }
        catch (Exception ex)
        {
            Log("Glide restore failed: " + ex.Message);
        }
    }
}
