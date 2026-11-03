using System;
using System.Globalization;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Framework;

namespace CeZeY.DynamicCinematicHudScript;

public partial class DynamicCinematicHUD
{
    private static bool _camHasLast;
    private static float _lastBaseFov;
    private static float _lastWrittenFov;
    private static Vector3 _lastBaseLoc;
    private static Vector3 _lastWrittenLoc;
    private static float _curFov;
    private static float _curDist;
    private static float _curHeight;
    private static long _camLastMs;
    private static long _camErrors;
    private static long _camDiagNextMs;
    private static string _camLastError = "";

    [Redirect(typeof(R3rdPersonCamera), nameof(R3rdPersonCamera.UpdateCamera))]
    private static void CameraUpdateRedirect(R3rdPersonCamera self, float deltaTime)
    {
        bool mine = false;
        try { mine = IsMyCamera(self); } catch { }

        if (mine && _camHasLast)
        {
            try
            {
                UndoCamera(self);
            }
            catch (Exception ex)
            {
                _camErrors++;
                _camLastError = ex.Message;
            }

            _camHasLast = false;
        }

        self.UpdateCamera(deltaTime);

        if (!mine)
            return;

        try
        {
            ApplyCamera(self);
        }
        catch (Exception ex)
        {
            _camErrors++;
            _camLastError = ex.Message;
        }
    }

    private static bool IsMyCamera(R3rdPersonCamera cam)
    {
        if (_pc0Ptr == 0)
            return true;

        var owner = cam.PCOwner;
        return owner != null && owner.IsValid && owner.Ptr == _pc0Ptr;
    }

    private static void UndoCamera(R3rdPersonCamera cam)
    {
        var pov = cam.CameraCache.POV;
        bool changed = false;

        if (MathF.Abs(pov.FOV - _lastWrittenFov) < 0.01f)
        {
            pov.FOV = _lastBaseFov;
            changed = true;
        }

        if (Vector3.DistanceSquared(pov.Location, _lastWrittenLoc) < 0.01f)
        {
            pov.Location = _lastBaseLoc;
            changed = true;
        }

        if (changed)
            cam.CameraCache.POV = pov;
    }

    private static void UndoCameraOnUnload()
    {
        if (!_camHasLast)
            return;

        var pc = Game.GetPlayerController(0);
        if (pc.PlayerCamera is R3rdPersonCamera cam && cam.IsValid)
            UndoCamera(cam);

        _camHasLast = false;
    }

    private static void ApplyCamera(R3rdPersonCamera cam)
    {
        long now = Environment.TickCount64;
        float dt = _camLastMs == 0 ? 0f : Math.Clamp((now - _camLastMs) / 1000f, 0f, 0.1f);
        _camLastMs = now;

        bool inCutscene = _camCutLeft > 0f;
        bool active = _enabled && _camOn && !inCutscene;

        float tFov = active ? _fov : 0f;
        float tDist = active ? _camDist : 0f;
        float tHeight = active ? _camHeight : 0f;

        float rate = inCutscene ? 12f : Math.Max(0.1f, _blend);
        float k = dt <= 0f ? 1f : 1f - MathF.Exp(-rate * dt);
        _curFov += (tFov - _curFov) * k;
        _curDist += (tDist - _curDist) * k;
        _curHeight += (tHeight - _curHeight) * k;

        if (MathF.Abs(_curFov) < 0.01f && MathF.Abs(_curDist) < 0.5f && MathF.Abs(_curHeight) < 0.5f)
        {
            if (!active)
            {
                _curFov = 0f;
                _curDist = 0f;
                _curHeight = 0f;
            }

            return;
        }

        var pov = cam.CameraCache.POV;
        float baseFov = pov.FOV;

        if (!float.IsFinite(baseFov) || baseFov < 45f || baseFov > 170f)
            return;

        Vector3 baseLoc = pov.Location;
        Vector3 dir = pov.Rotation.ToDirection();

        float newFov = Math.Clamp(baseFov + _curFov, 45f, 140f);
        Vector3 newLoc = baseLoc - dir * _curDist + new Vector3(0f, 0f, _curHeight);

        pov.FOV = newFov;
        pov.Location = newLoc;
        cam.CameraCache.POV = pov;

        _camHasLast = true;
        _lastBaseFov = baseFov;
        _lastWrittenFov = newFov;
        _lastBaseLoc = baseLoc;
        _lastWrittenLoc = newLoc;

        if (_diag && now >= _camDiagNextMs)
        {
            _camDiagNextMs = now + 1000;
            Log("CAM base fov=" + baseFov.ToString("0.0", CultureInfo.InvariantCulture) +
                " written=" + newFov.ToString("0.0", CultureInfo.InvariantCulture) +
                " dist=" + _curDist.ToString("0", CultureInfo.InvariantCulture) +
                " height=" + _curHeight.ToString("0", CultureInfo.InvariantCulture) +
                " cutscene=" + inCutscene + " errors=" + _camErrors);
        }
    }
}
