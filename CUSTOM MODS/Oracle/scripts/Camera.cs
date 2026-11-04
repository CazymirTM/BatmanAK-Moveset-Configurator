using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace CeZeY.OracleScript;

public partial class Oracle
{
    private const double CamRecheck = 1.0;
    private const string CamStoreKey = "CeZeY.Oracle.CameraStore";
    private const int CatFoot = 0;
    private const int CatGlide = 1;
    private const int CatCar = 2;
    private const int CatAim = 3;

    private delegate ref R3rdPersonCamera.FFreeCameraConfig CamCfgRef(R3rdPersonCamera cam);
    private delegate ref Vector3 CamVecRef(R3rdPersonCamera cam);

    private readonly record struct CamSet(float Fov, float Height, float Shoulder, float Pull, float Pivot, float Aim,
        float GlideFov, float GlideKick, float GlideDist, float CarFov, float CarKick, float CarDist, bool Steady, bool NoShake,
        float GlideUp, float GlideSide, float SitSig, bool SitOn);

    private static readonly CamSet VanillaCam = new(0f, 0f, 1f, 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f, 1f, false, false, 0f, 0f, 0f, false);

    private sealed record CamPreset(string Key, string Label, float Fov, float Height, float Shoulder, float Pull, float Pivot, float GlideFov, float GlideDist, float CarFov, float CarDist,
        float GlideKick = 1f, float CarKick = 1f);

    private static readonly CamPreset[] CamPresets =
    {
        new("pick", "CHOOSE", 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 1f),
        new("vanilla", "VANILLA", 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 1f),
        new("wide", "WIDE", 10f, 0f, 1f, 40f, 0f, 5f, 1.1f, 5f, 1.1f),
        new("ultra", "ULTRA WIDE", 20f, 0f, 1f, 30f, 0f, 10f, 1.1f, 10f, 1.1f),
        new("cinematic", "CINEMATIC", -5f, 15f, 0.8f, 120f, 10f, 0f, 1.3f, 0f, 1.25f),
        new("shoulder", "OVER SHOULDER", 0f, -5f, 1.3f, -40f, 0f, 0f, 0.9f, 0f, 1f),
        new("centred", "CENTRED", 5f, 10f, 0f, 60f, 0f, 0f, 1f, 0f, 1f),
        new("action", "ACTION", 15f, -10f, 1.15f, -30f, -5f, 10f, 0.9f, 10f, 0.95f, 1.75f, 1.75f),
        new("tactical", "TACTICAL", 10f, 80f, 0.5f, 220f, 50f, 5f, 1.6f, 5f, 1.5f, 0.5f, 0.75f),
        new("noir", "NOIR", -12f, -20f, 1.25f, 260f, -10f, -5f, 1.4f, -5f, 1.35f, 0.5f, 0.5f),
    };


    private static readonly string[] FootFovs =
    {
        "WalkStateCameraFOVAngle", "WalkCanisterStateCameraFOVAngle", "AlternativeWalkCamFOV", "RunStateCameraFOVAngle", "BoostRunFOVAngle",
        "StealthStateCameraFOVAngle", "OverworldStealthStateCameraFOVAngle", "TunnelStateCameraFOVAngle", "FallingStateCameraFOVAngle",
        "CorridorCombatStateCameraFOVAngle", "HighCombatStateCameraFOVAngle", "NightwingBossFightCameraFOVAngle", "LookStateCameraFOVAngle",
        "GrappleCameraFOVAngle", "CoverRightCameraFOVAngle", "AlcoveCameraFOVAngle", "SilentPredCameraFOVAngle", "SilentPredOverworldCameraFOVAngle",
        "PerchLedgeCameraFOVAngle", "PerchLedgeSwingCameraFOVAngle", "HangCameraFOVAngle", "ZipLineCameraFOVAngle", "HarpoonPullCameraFOVAngle",
        "SwingingFromAboveCameraFOVAngle", "CeilingClimbCamFOVAngle", "RaftPullFOVAngle", "FearTakedownFOVAngle", "LookDownVantageCamFOVAngle",
        "SlideStateCameraFOVAngle", "CornerStateFOVAngle", "OmnitronCornerStateFOVAngle", "RHDCornerStateFOVAngle", "VoiceSynthCornerStateFOVAngle",
        "WallClimbStartCameraFOVAngle", "PounceCameraFOVAngle", "UsingSpotlightStateCameraFOVAngle", "ForensicsScanStateCameraFOVAngle",
        "HelicopterCameraFOVAngle", "ScarecrowTakedownCameraFOVAngle", "PairedAnimationStateCameraFOVAngle", "DialogueStateCameraFOVAngle",
        "ThugCameraFOVAngle", "JokerFPSFov", "CopSittingAtBarFov", "CustomCameraFOV"
    };

    private static readonly string[] GlideFovs = { "GlidingCameraFOVAngle", "BoostGlidingCameraFOVAngle", "EdgeSwingCameraFOVAngle" };

    private static readonly string[] CarFovs =
    {
        "DrivingCamFOV", "DrivingCamFOV1", "DrivingCamFOV2", "BoostingCamFOV", "BoostingCamFOV1", "BoostingCamFOV2", "BattleModeCamFOV",
        "BattleModeWinchCamFOV", "AntiPersonelBattleModeCamFOV", "ChasingCamFOV", "RidingCarCamFOV", "DiveToBatmobilePickupFOVAngle", "BatmobileConversationCamFOV"
    };

    private static readonly string[] AimFovs =
    {
        "BatarangAimCameraFOVAngle", "BatTrackerAimCameraFOVAngle", "FreezeJammerAimCameraFOVAngle", "GrappleAimCameraFOVAngle", "HarpoonAimCameraFOVAngle",
        "JokerGunAimCameraFOVAngle", "JokerGunZoomAimCameraFOVAngle", "JokerGunZoomRoom3AimCameraFOVAngle", "LineLauncherAimCameraFOVAngle",
        "MagneticBlastAimCameraFOVAngle", "OmnitronAimCameraFOVAngle", "RHDAimCameraFOVAngle", "DisruptorSniperFOVAngle", "DisruptorSniperZoomedFOVAngle",
        "ZoomStateCameraFOVAngle", "ThugZoomCameraFOVAngle", "ZoomFOV", "ZoomedFOVAngle", "ResonatorFOVAngle", "ForensicsFilterStateCameraFOVAngle",
        "CopCameraZoomFOV", "JokerFPSZoomFov", "GauntletCameraFOVAngle", "ExtraZoomFOV", "ForceZoomFOVAngle", "CustomCameraZoomFOV",
        "RHDPasswordCrackFOVAngle", "RHDPasswordCrack43FOVAngle"
    };

    private static readonly HashSet<string> AimStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "Batarang", "LineLauncherAim", "HarpoonAim", "MagneticBlastAim", "RHDAim"
    };

    private static readonly string[] GlideKicks = { "GlidingCameraExtraFOVAngle" };
    private static readonly string[] CarKicks = { "DrivingCamMaxExtraFOV", "DrivingCamMaxExtraFOV1", "DrivingCamMaxExtraFOV2" };
    private static readonly string[] SwayFloats = { "PelvisSway", "DefaultPelvisSway", "RunStatePelvisSway", "CurrentPelvisSway" };
    private static readonly string[] GlideOffsets = { "GlidingCameraOffset", "BoostGlidingCameraOffset" };

    private static readonly string[] FootCfgs =
    {
        "WalkCamConfig", "WalkCanisterCamConfig", "AgilityWalkCamConfig", "AlternativeWalkCamConfig", "RunCamConfig", "RooftopRunCamConfig",
        "StealthCamConfig", "CombatWalkCamConfig", "CorridorCombatCamConfig", "HighCombatCamConfig", "DemonTrialsCamConfig", "RailingCamConfig",
        "GrateCameraConfig", "LedgeSwingCamConfig", "GrappleCamConfig", "HqGrappleStartCamConfig", "PerchLedgeCamConfig", "SilentPredatorCamConfig",
        "VantageClimbCamConfig", "IntroCamConfig", "FloorAttackCamConfig", "ThugCamConfig", "StandingHoldCamConfig", "StandingGrabCamConfig", "FearTakedownCamConfig",
        "GrappleRescueCamConfig", "CapeChuteConfig", "LedgeHangCamConfig", "LedgeHangKickedByThugCamConfig", "LedgeHangTakedownStartCamConfig", "LedgeHangTakedownCamConfig",
        "QuickTakedownCamConfig", "GenericFarFreeCamConfig", "HangPointCamConfig", "StealthWallCamConfig", "CrouchUnderObstacleCamConfig", "SwingingCamConfig",
        "WallClimbCamConfig", "WallClimbStartCamConfig", "PounceCamConfig", "SwingingFromAboveCamConfig", "TakedownFromAboveCamConfig", "PerchLedgeSwingCamConfig",
        "NightwingBossFightCamConfig", "LookDownCamConfig", "HighGrateCameraConfig", "SlideCameraConfig", "CeilingClimbCamConfig", "CeilingToLedgeHangCamConfig",
        "CeilingToCatwalkCamConfig", "DeepFallLandCamConfig", "ScarerowBossCamConfig", "GauntletCamConfig", "GauntletPerchCamConfig"
    };

    private static readonly string[] CarCfgs =
    {
        "DrivingCamConfig", "DrivingCamConfig1", "DrivingCamConfig2", "BoostingCamConfig", "BoostingCamConfig1", "BoostingCamConfig2",
        "BattleModeCamConfig", "BattleModeWinchCamConfig", "BattleModeAbseilingCamConfig", "FleeingCamConfig", "BarrelRollCamConfig", "ChasingCamConfig",
        "GlideInBatmobileCamConfig", "RidingCarCamConfig", "BatmobileConversationCamConfig", "DiveToBatmobilePickupCamConfig"
    };

    private sealed class CamStore
    {
        public nint Ptr;
        public bool Live;
        public float[] Applied = Array.Empty<float>();
        public readonly Dictionary<string, float> FloatOrig = new(StringComparer.Ordinal);
        public readonly Dictionary<string, float> FloatWritten = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Vector3> VecOrig = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Vector3> VecWritten = new(StringComparer.Ordinal);
        public readonly Dictionary<string, R3rdPersonCamera.FFreeCameraConfig> CfgOrig = new(StringComparer.Ordinal);
        public readonly Dictionary<string, R3rdPersonCamera.FFreeCameraConfig> CfgWritten = new(StringComparer.Ordinal);
        public bool? SwayOrig;
    }

    private static readonly Dictionary<string, PropertyInfo> _camProps = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, CamCfgRef> _camCfgs = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, CamVecRef> _camVecs = new(StringComparer.Ordinal);
    private static CamStore _cam = new();
    private static CamSet _camApplied = VanillaCam;
    private static long _camChecked;
    private static string _camState = "";
    private static int _camStateCat;
    private static bool _camStoreLoaded;
    private static string _aimState = "";
    private static bool _aimOn;
    private static float _aimBase;
    private static float _aimWritten;
    private static float _aimPrevFov;

    private static Feature? _fovWorld, _camHeight, _camShoulder, _camPull, _camPivot, _fovAim;
    private static Feature? _glideFov, _glideKick, _glideDist, _carFov, _carKick, _carDist;
    private static Feature? _camSteady, _camNoShake, _fovReadout, _glideUp, _glideSide;

    private static string Deg(float v) => MathF.Abs(v) < 0.01f ? "DEFAULT" : (v > 0 ? "+" : "") + v.ToString("0", CultureInfo.InvariantCulture);

    private static string Units(float v) => MathF.Abs(v) < 0.01f ? "DEFAULT" : (v > 0 ? "+" : "") + v.ToString("0", CultureInfo.InvariantCulture);

    private static string Side(float v)
    {
        if (MathF.Abs(v) < 0.01f)
            return "CENTRED";
        string pct = (MathF.Abs(v) * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
        return v < 0f ? "LEFT " + pct : "RIGHT " + pct;
    }

    private static void BuildCamera()
    {
        Group("SIT & FOV");
        _fovWorld = Slider("fov_world", "FIELD OF VIEW", "Widens or narrows the camera everywhere: walking, running, fights, predator, gliding and the Batmobile. Value is added to every camera's own FOV, so the game's zoom moments still work.",
            0f, -20f, 40f, 1f, 15f, Deg, _ => { });
        _camHeight = Slider("cam_height", "CAMERA HEIGHT", "Raises or lowers where the camera sits behind your character when on foot.",
            0f, -60f, 120f, 5f, 30f, Units, _ => { });
        _camShoulder = Slider("cam_shoulder", "SHOULDER OFFSET", "How far the camera sits to the side. CENTRED puts your character in the middle, negative values swap to the left shoulder.",
            1f, -1.5f, 2f, 0.1f, 0f, Side, _ => { });
        _fovAim = Slider("fov_aim", "AIM & ZOOM FOV", "Field of view while aiming (batarang, line launcher, remote claw, hacking device, disruptor) and when zooming with Z. Lower = more zoom, higher = wider.",
            1f, 0.5f, 1.6f, 0.05f, 1.3f, Percent, _ => { });

        BuildSituations();

        Group("PIVOT & PULL");
        _camPull = Slider("cam_distance", "CAMERA DISTANCE", "Pulls the camera further back from your character on foot, or closer in with negative values.",
            0f, -60f, 400f, 10f, 150f, Units, _ => { });
        _camPivot = Slider("cam_pivot", "PIVOT HEIGHT", "Moves the point the camera orbits around and looks at up or down. Higher looks over your character's head.",
            0f, -50f, 100f, 5f, 30f, Units, _ => { });

        Group("GLIDING");
        _glideFov = Slider("glide_fov", "GLIDE FOV", "Extra field of view while gliding and dive-bombing, on top of FIELD OF VIEW.",
            0f, -20f, 40f, 1f, 15f, Deg, _ => { });
        _glideKick = Slider("glide_kick", "DIVE SPEED KICK", "How much the view widens as you pick up speed while gliding. 0% keeps it steady.",
            1f, 0f, 3f, 0.25f, 2f, Percent, _ => { });
        _glideDist = Slider("glide_distance", "GLIDE CAMERA DISTANCE", "How far behind you the camera trails while gliding.",
            1f, 0.5f, 2.5f, 0.1f, 1.5f, Times, _ => { });
        _glideUp = Slider("glide_up", "GLIDE CAMERA HEIGHT", "Raises or lowers the camera while gliding.",
            0f, -200f, 200f, 10f, 60f, Units, _ => { });
        _glideSide = Slider("glide_side", "GLIDE CAMERA SIDE", "Moves the camera to the side while gliding. Negative is left, positive is right.",
            0f, -200f, 200f, 10f, 60f, Units, _ => { });

        Group("BATMOBILE");
        _carFov = Slider("car_fov", "BATMOBILE FOV", "Extra field of view while driving and in battle mode, on top of FIELD OF VIEW.",
            0f, -20f, 40f, 1f, 15f, Deg, _ => { });
        _carKick = Slider("car_kick", "SPEED FOV KICK", "How much the view widens at high speed and when boosting.",
            1f, 0f, 3f, 0.25f, 2f, Percent, _ => { });
        _carDist = Slider("car_distance", "BATMOBILE CAMERA DISTANCE", "How far behind the Batmobile the camera sits, in car and battle mode.",
            1f, 0.5f, 2.5f, 0.1f, 1.4f, Times, _ => { });

        Group("TOOLS");
        var keys = new string[CamPresets.Length];
        var labels = new string[CamPresets.Length];
        for (int i = 0; i < CamPresets.Length; i++)
        {
            keys[i] = CamPresets[i].Key;
            labels[i] = CamPresets[i].Label;
        }

        Feature preset = Choice("fov_preset", "CAMERA PRESET", "Sets every camera slider at once. Tweak them afterwards as you like. VANILLA puts everything back to the game's camera.",
            keys, labels, ApplyCamPreset);
        preset.Pref = true;
        _fovReadout = Toggle("fov_readout", "FOV READOUT", "Shows the live field of view (on screen and the game's own value), the camera mode and the camera distance in the bottom left corner.", _ => { });
        _fovReadout.Pref = true;
        _camSteady = Toggle("cam_steady", "STEADY CAMERA", "Stops the camera swaying with your character's hips when walking and running.", _ => { });
        _camNoShake = Toggle("cam_no_shake", "NO SCREEN SHAKE", "Turns off camera shakes from explosions, landings, gliding and the Batmobile.", _ => { });
        Action("cam_reset", "RESET CAMERA", "Puts every camera and FOV setting on this page back to the game's default.", ResetCameraFeatures);
        BuildCameraSlots();
    }

    private static void ApplyCamPreset(int i)
    {
        if (i <= 0 || i >= CamPresets.Length)
            return;

        CamPreset p = CamPresets[i];
        SetCamFeature(_fovWorld, p.Fov);
        SetCamFeature(_camHeight, p.Height);
        SetCamFeature(_camShoulder, p.Shoulder);
        SetCamFeature(_camPull, p.Pull);
        SetCamFeature(_camPivot, p.Pivot);
        SetCamFeature(_glideFov, p.GlideFov);
        SetCamFeature(_glideDist, p.GlideDist);
        SetCamFeature(_carFov, p.CarFov);
        SetCamFeature(_carDist, p.CarDist);
        SetCamFeature(_glideKick, p.GlideKick);
        SetCamFeature(_carKick, p.CarKick);
        if (i == 1)
        {
            SetCamFeature(_fovAim, 1f);
            SetCamFeature(_glideUp, 0f);
            SetCamFeature(_glideSide, 0f);
            SetCamFeature(_camBlend, 1f);
            ResetSituations();
        }
    }

    private static void SetCamFeature(Feature? f, float v)
    {
        if (f == null)
            return;
        v = Math.Clamp(v, f.Min, f.Max);
        if (MathF.Abs(f.Value - v) < 0.0001f)
            return;
        f.Value = v;
        SaveFeature(f);
    }

    private static void ResetCameraFeatures()
    {
        foreach (Feature? f in new[] { _fovWorld, _camHeight, _camShoulder, _camPull, _camPivot, _fovAim, _glideFov, _glideKick, _glideDist, _carFov, _carKick, _carDist, _glideUp, _glideSide, _camBlend })
            SetCamFeature(f, f?.Default ?? 0f);
        ResetSituations();

        foreach (Feature? f in new[] { _camSteady, _camNoShake })
        {
            if (f != null && f.On)
            {
                f.On = false;
                SaveFeature(f);
            }
        }

        if (FeatureById.TryGetValue("fov_preset", out Feature? preset) && preset.Choice != 0)
        {
            preset.Choice = 0;
            SaveFeature(preset);
        }
    }

    private static CamSet WantedCam() => new(
        _fovWorld?.Value ?? 0f, _camHeight?.Value ?? 0f, _camShoulder?.Value ?? 1f, _camPull?.Value ?? 0f, _camPivot?.Value ?? 0f, _fovAim?.Value ?? 1f,
        _glideFov?.Value ?? 0f, _glideKick?.Value ?? 1f, _glideDist?.Value ?? 1f, _carFov?.Value ?? 0f, _carKick?.Value ?? 1f, _carDist?.Value ?? 1f,
        _camSteady?.On ?? false, _camNoShake?.On ?? false, _glideUp?.Value ?? 0f, _glideSide?.Value ?? 0f, SitSignature(out bool sitOn), sitOn);

    private static float[] Pack(CamSet s) => new[]
    {
        s.Fov, s.Height, s.Shoulder, s.Pull, s.Pivot, s.Aim, s.GlideFov, s.GlideKick, s.GlideDist, s.CarFov, s.CarKick, s.CarDist, s.Steady ? 1f : 0f, s.NoShake ? 1f : 0f, s.GlideUp, s.GlideSide, s.SitSig, s.SitOn ? 1f : 0f
    };

    private static CamSet Unpack(float[] a) => a.Length < 18 ? VanillaCam : new(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10], a[11], a[12] > 0.5f, a[13] > 0.5f, a[14], a[15], a[16], a[17] > 0.5f);

    private static void LoadCamStore()
    {
        if (_camStoreLoaded)
            return;
        _camStoreLoaded = true;

        if (AppDomain.CurrentDomain.GetData(CamStoreKey) is object[] saved && saved.Length == 11)
        {
            try
            {
                _cam.Ptr = (nint)saved[0];
                _cam.Live = (bool)saved[1];
                _cam.Applied = (float[])saved[2];
                Copy((Dictionary<string, float>)saved[3], _cam.FloatOrig);
                Copy((Dictionary<string, float>)saved[4], _cam.FloatWritten);
                Copy((Dictionary<string, Vector3>)saved[5], _cam.VecOrig);
                Copy((Dictionary<string, Vector3>)saved[6], _cam.VecWritten);
                Copy((Dictionary<string, R3rdPersonCamera.FFreeCameraConfig>)saved[7], _cam.CfgOrig);
                Copy((Dictionary<string, R3rdPersonCamera.FFreeCameraConfig>)saved[8], _cam.CfgWritten);
                _cam.SwayOrig = (bool?)saved[9];
                _camApplied = Unpack(_cam.Applied);
            }
            catch
            {
                _cam = new CamStore();
                _camApplied = VanillaCam;
            }
        }
    }

    private static void Copy<T>(Dictionary<string, T> from, Dictionary<string, T> to)
    {
        foreach (KeyValuePair<string, T> kv in from)
            to[kv.Key] = kv.Value;
    }

    private static void SaveCamStore()
    {
        _cam.Applied = Pack(_camApplied);
        AppDomain.CurrentDomain.SetData(CamStoreKey, new object[]
        {
            _cam.Ptr, _cam.Live, _cam.Applied,
            new Dictionary<string, float>(_cam.FloatOrig), new Dictionary<string, float>(_cam.FloatWritten),
            new Dictionary<string, Vector3>(_cam.VecOrig), new Dictionary<string, Vector3>(_cam.VecWritten),
            new Dictionary<string, R3rdPersonCamera.FFreeCameraConfig>(_cam.CfgOrig), new Dictionary<string, R3rdPersonCamera.FFreeCameraConfig>(_cam.CfgWritten),
            _cam.SwayOrig, 0
        });
    }

    private static R3rdPersonCamera? PlayerCam()
    {
        RPlayerController pc = Game.GetPlayerController(0);
        if (pc == null || !pc.IsValid)
            return null;
        return pc.PlayerCamera is R3rdPersonCamera cam && cam.IsValid && !cam.IsClassDefaultObject ? cam : null;
    }

    private static void CameraTick()
    {
        LoadCamStore();
        R3rdPersonCamera? cam = PlayerCam();
        if (cam == null)
            return;

        if (cam.Ptr != _cam.Ptr)
        {
            ForgetCamera();
            _cam.Ptr = cam.Ptr;
        }

        CamSet want = WantedCam();
        if (want.NoShake)
            ClearShakes(cam);

        AimTick(cam, want.Aim);
        BlendTick(cam);

        long now = Stopwatch.GetTimestamp();
        bool recheck = Stopwatch.GetElapsedTime(_camChecked, now).TotalSeconds >= CamRecheck;
        if (want == _camApplied && !recheck)
            return;

        _camChecked = now;
        if (want == VanillaCam && _camApplied == VanillaCam && !_cam.Live)
            return;

        ApplyCamera(cam, want);
    }

    private static bool IsAimState(string state)
    {
        if (state.Length == 0 || state.Equals("Walk", StringComparison.OrdinalIgnoreCase))
            return false;
        if (AimStates.Contains(state) || state.Contains("Aim", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            RInventoryGadget gadget = Game.GetPlayerController(0)?.GetCurrentGadget();
            return gadget != null && gadget.IsValid && state.Equals(gadget.GetGadgetCamera(false).ToString(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void AimTick(R3rdPersonCamera cam, float aim)
    {
        if (!_aimOn && MathF.Abs(aim - 1f) < 0.001f)
        {
            _aimState = "";
            return;
        }

        string state = CamStateName(cam);
        float fov = cam.CameraFOVAngle;
        if (state != _aimState)
        {
            if (_aimOn)
            {
                if (MathF.Abs(fov - _aimWritten) < 0.01f)
                {
                    cam.CameraFOVAngle = _aimBase;
                    fov = _aimBase;
                }
                _aimOn = false;
            }

            bool inherits = _aimState.Length > 0 && MathF.Abs(fov - _aimPrevFov) < 0.01f;
            _aimState = state;
            if (inherits && fov > 0f && MathF.Abs(aim - 1f) >= 0.001f && IsAimState(state))
            {
                _aimOn = true;
                _aimBase = fov;
                _aimWritten = -1f;
            }
        }

        if (_aimOn)
        {
            if (_aimWritten >= 0f && MathF.Abs(fov - _aimWritten) > 0.01f)
                _aimBase = fov;

            float next = Math.Clamp(_aimBase * aim, 3f, 150f);
            if (MathF.Abs(next - fov) > 0.01f)
                cam.CameraFOVAngle = next;
            _aimWritten = next;

            if (MathF.Abs(aim - 1f) < 0.001f)
            {
                cam.CameraFOVAngle = _aimBase;
                _aimOn = false;
            }
        }

        _aimPrevFov = cam.CameraFOVAngle;
    }

    private static void ForgetCamera()
    {
        _cam = new CamStore();
        _camApplied = VanillaCam;
        _camState = "";
        _aimState = "";
        _aimOn = false;
        ForgetSituationFov();
        SaveCamStore();
    }

    private static void ApplyCamera(R3rdPersonCamera cam, CamSet s)
    {
        CamSet old = _camApplied;

        foreach (string n in FootFovs)
            CamFloat(cam, n, v => v <= 0f ? v : Math.Clamp(v + s.Fov, 5f, 150f));
        foreach (string n in GlideFovs)
            CamFloat(cam, n, v => v <= 0f ? v : Math.Clamp(v + s.Fov + s.GlideFov, 5f, 150f));
        foreach (string n in CarFovs)
            CamFloat(cam, n, v => v <= 0f ? v : Math.Clamp(v + s.Fov + s.CarFov, 5f, 150f));
        foreach (string n in AimFovs)
            CamFloat(cam, n, v => v <= 0f ? v : Math.Clamp(v * s.Aim, 3f, 150f));
        foreach (string n in GlideKicks)
            CamFloat(cam, n, v => v * s.GlideKick);
        foreach (string n in CarKicks)
            CamFloat(cam, n, v => v * s.CarKick);
        foreach (string n in SwayFloats)
            CamFloat(cam, n, v => s.Steady ? 0f : v);
        foreach (string n in GlideOffsets)
            CamVec(cam, n, v => new Vector3(v.X * s.GlideDist, v.Y * s.GlideDist + s.GlideSide, v.Z * s.GlideDist + s.GlideUp));

        bool swayNow = cam.bSwayWithPelvis;
        if (s.Steady)
        {
            _cam.SwayOrig ??= swayNow;
            if (swayNow)
                cam.bSwayWithPelvis = false;
        }
        else if (_cam.SwayOrig is bool sway)
        {
            if (swayNow != sway)
                cam.bSwayWithPelvis = sway;
            _cam.SwayOrig = null;
        }

        ApplyShake(cam, s.NoShake);

        R3rdPersonCamera.FFreeCameraConfig current = cam.CurrentCameraConfig;
        string? liveCfg = null;
        R3rdPersonCamera.FFreeCameraConfig liveNew = default;
        foreach (string n in FootCfgs)
        {
            if (CamConfig(cam, n, c => SitConfig(FootConfig(c, s), n, s), current, out R3rdPersonCamera.FFreeCameraConfig made) && liveCfg == null)
            {
                liveCfg = n;
                liveNew = made;
            }
        }

        foreach (string n in CarCfgs)
        {
            if (CamConfig(cam, n, c => CarConfig(c, s), current, out R3rdPersonCamera.FFreeCameraConfig made) && liveCfg == null)
            {
                liveCfg = n;
                liveNew = made;
            }
        }

        if (liveCfg != null)
            cam.SetFreeCameraConfig(liveNew);

        if (old != s)
            LiveFov(cam, old, s);

        _camApplied = s;
        _cam.Live = s != VanillaCam;
        SaveCamStore();
    }

    private static int CamCategory(string state)
    {
        if (state.Contains("Glid", StringComparison.OrdinalIgnoreCase) || state.Contains("Dive", StringComparison.OrdinalIgnoreCase))
            return CatGlide;
        if (AimStates.Contains(state) || state.Contains("Aim", StringComparison.OrdinalIgnoreCase) || state.Contains("Zoom", StringComparison.OrdinalIgnoreCase) || state.Contains("Sniper", StringComparison.OrdinalIgnoreCase))
            return CatAim;
        if (state.Contains("Driv", StringComparison.OrdinalIgnoreCase) || state.Contains("Batmobile", StringComparison.OrdinalIgnoreCase)
            || state.Contains("Battle", StringComparison.OrdinalIgnoreCase) || state.Contains("Vehicle", StringComparison.OrdinalIgnoreCase))
            return CatCar;
        return CatFoot;
    }

    private static string CamStateName(R3rdPersonCamera cam)
    {
        try
        {
            return cam.GetStateName().ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static float FovFor(int cat, float vanilla, CamSet s) => cat switch
    {
        CatGlide => vanilla + s.Fov + s.GlideFov,
        CatCar => vanilla + s.Fov + s.CarFov,
        CatAim => vanilla * s.Aim,
        _ => vanilla + s.Fov
    };

    private static float FovBack(int cat, float shown, CamSet s) => cat switch
    {
        CatGlide => shown - s.Fov - s.GlideFov,
        CatCar => shown - s.Fov - s.CarFov,
        CatAim => s.Aim > 0.01f ? shown / s.Aim : shown,
        _ => shown - s.Fov
    };

    private static void LiveFov(R3rdPersonCamera cam, CamSet old, CamSet s)
    {
        if (_aimOn)
        {
            _aimBase = FovFor(CatFoot, FovBack(CatFoot, _aimBase, old), s);
            return;
        }

        int cat = CamCategory(CamStateName(cam));
        float shown = cam.CameraFOVAngle;
        if (shown <= 0f)
            return;

        float next = Math.Clamp(FovFor(cat, FovBack(cat, shown, old), s), 3f, 150f);
        if (MathF.Abs(next - shown) > 0.01f)
            cam.CameraFOVAngle = next;
    }

    private static Vector3 SitMove(Vector3 v, CamSet s) => new(v.X, v.Y * s.Shoulder, v.Z + s.Height);

    private static R3rdPersonCamera.FFreeCameraConfig FootConfig(R3rdPersonCamera.FFreeCameraConfig c, CamSet s)
    {
        c.StateFreeCameraSitOffsetMin = SitMove(c.StateFreeCameraSitOffsetMin, s);
        c.StateFreeCameraSitOffsetMax = SitMove(c.StateFreeCameraSitOffsetMax, s);
        if (c.UseStateFreeCameraSitOffsetUp)
            c.StateFreeCameraSitOffsetUp = SitMove(c.StateFreeCameraSitOffsetUp, s);
        if (c.bUseSeparate43Settings)
        {
            c.StateFreeCamera43SitOffsetMin = SitMove(c.StateFreeCamera43SitOffsetMin, s);
            c.StateFreeCamera43SitOffsetMax = SitMove(c.StateFreeCamera43SitOffsetMax, s);
            c.StateFreeCamera43SitOffsetLookUp = SitMove(c.StateFreeCamera43SitOffsetLookUp, s);
        }

        Vector3 pivot = c.CameraPivotOffset;
        c.CameraPivotOffset = new Vector3(pivot.X, pivot.Y * s.Shoulder, pivot.Z + s.Pivot);
        c.MinFreeCameraDistance = MathF.Max(5f, c.MinFreeCameraDistance + s.Pull);
        c.MaxFreeCameraDistance = MathF.Max(5f, c.MaxFreeCameraDistance + s.Pull);
        return c;
    }

    private static R3rdPersonCamera.FFreeCameraConfig CarConfig(R3rdPersonCamera.FFreeCameraConfig c, CamSet s)
    {
        float k = s.CarDist;
        c.StateFreeCameraSitOffsetMin *= k;
        c.StateFreeCameraSitOffsetMax *= k;
        if (c.UseStateFreeCameraSitOffsetUp)
            c.StateFreeCameraSitOffsetUp *= k;
        c.MinFreeCameraDistance *= k;
        c.MaxFreeCameraDistance *= k;
        return c;
    }

    private static bool SameCfg(in R3rdPersonCamera.FFreeCameraConfig a, in R3rdPersonCamera.FFreeCameraConfig b)
    {
        return Near(a.StateFreeCameraSitOffsetMin, b.StateFreeCameraSitOffsetMin) && Near(a.StateFreeCameraSitOffsetMax, b.StateFreeCameraSitOffsetMax)
            && Near(a.StateFreeCameraSitOffsetUp, b.StateFreeCameraSitOffsetUp) && Near(a.CameraPivotOffset, b.CameraPivotOffset)
            && Near(a.StateFreeCamera43SitOffsetMin, b.StateFreeCamera43SitOffsetMin) && Near(a.StateFreeCamera43SitOffsetMax, b.StateFreeCamera43SitOffsetMax)
            && Near(a.StateFreeCamera43SitOffsetLookUp, b.StateFreeCamera43SitOffsetLookUp)
            && MathF.Abs(a.MinFreeCameraDistance - b.MinFreeCameraDistance) < 0.01f && MathF.Abs(a.MaxFreeCameraDistance - b.MaxFreeCameraDistance) < 0.01f
            && MathF.Abs(a.maxPitch - b.maxPitch) < 0.5f && MathF.Abs(a.ShortCamSpringConst - b.ShortCamSpringConst) < 0.01f;
    }

    private static bool Near(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) < 0.0001f;

    private static bool CamConfig(R3rdPersonCamera cam, string name, Func<R3rdPersonCamera.FFreeCameraConfig, R3rdPersonCamera.FFreeCameraConfig> map,
        in R3rdPersonCamera.FFreeCameraConfig current, out R3rdPersonCamera.FFreeCameraConfig made)
    {
        made = default;
        CamCfgRef? get = CfgRef(name);
        if (get == null)
            return false;

        ref R3rdPersonCamera.FFreeCameraConfig slot = ref get(cam);
        R3rdPersonCamera.FFreeCameraConfig now = slot;
        if (!_cam.CfgWritten.TryGetValue(name, out R3rdPersonCamera.FFreeCameraConfig written) || !SameCfg(now, written))
        {
            _cam.CfgOrig[name] = now;
            written = now;
        }

        made = map(_cam.CfgOrig[name]);
        bool changed = !SameCfg(now, made);
        if (changed)
            slot = made;
        _cam.CfgWritten[name] = made;

        return changed && SameCfg(current, written);
    }

    private static CamCfgRef? CfgRef(string name)
    {
        if (_camCfgs.TryGetValue(name, out CamCfgRef? d))
            return d;

        MethodInfo? m = typeof(R3rdPersonCamera).GetProperty(name)?.GetGetMethod();
        d = m == null ? null : (CamCfgRef)Delegate.CreateDelegate(typeof(CamCfgRef), m);
        if (d != null)
            _camCfgs[name] = d;
        else
            LogThrottled("Camera config missing: " + name);
        return d;
    }

    private static void CamFloat(R3rdPersonCamera cam, string name, Func<float, float> map)
    {
        if (!_camProps.TryGetValue(name, out PropertyInfo? p))
        {
            p = typeof(R3rdPersonCamera).GetProperty(name);
            if (p == null || p.PropertyType != typeof(float) || !p.CanWrite)
            {
                LogThrottled("Camera value missing: " + name);
                return;
            }
            _camProps[name] = p;
        }

        float now = (float)p.GetValue(cam)!;
        if (!_cam.FloatWritten.TryGetValue(name, out float written) || MathF.Abs(now - written) > 0.001f)
            _cam.FloatOrig[name] = now;

        float v = map(_cam.FloatOrig[name]);
        if (MathF.Abs(now - v) > 0.0001f)
            p.SetValue(cam, v);
        _cam.FloatWritten[name] = v;
    }

    private static void CamVec(R3rdPersonCamera cam, string name, Func<Vector3, Vector3> map)
    {
        if (!_camVecs.TryGetValue(name, out CamVecRef? get))
        {
            MethodInfo? m = typeof(R3rdPersonCamera).GetProperty(name)?.GetGetMethod();
            if (m == null || m.ReturnType != typeof(Vector3).MakeByRefType())
            {
                LogThrottled("Camera offset missing: " + name);
                return;
            }
            get = (CamVecRef)Delegate.CreateDelegate(typeof(CamVecRef), m);
            _camVecs[name] = get;
        }

        ref Vector3 slot = ref get(cam);
        Vector3 now = slot;
        if (!_cam.VecWritten.TryGetValue(name, out Vector3 written) || !Near(now, written))
            _cam.VecOrig[name] = now;

        Vector3 v = map(_cam.VecOrig[name]);
        if (!Near(now, v))
            slot = v;
        _cam.VecWritten[name] = v;
    }

    private static void ApplyShake(R3rdPersonCamera cam, bool off)
    {
        CameraModifier m = cam.CameraShakeCamMod;
        if (m == null || !m.IsValid)
            return;
        if (off && !m.bDisabled)
            m.DisableModifier(true);
        else if (!off && _cam.Live && _camApplied.NoShake && m.bDisabled)
            m.EnableModifier();
    }

    private static void ClearShakes(R3rdPersonCamera cam)
    {
        RBMScreenShakeModifier shake = cam.ScreenShakeModifier;
        if (shake != null && shake.IsValid && shake.Shakes.Count > 0)
            shake.Reset();
    }

    private static void RestoreCamera()
    {
        try
        {
            R3rdPersonCamera? cam = PlayerCam();
            if (cam != null && cam.Ptr == _cam.Ptr)
                RestoreBlend(cam);
            if (cam != null && cam.Ptr == _cam.Ptr && (_cam.Live || _camApplied != VanillaCam))
                ApplyCamera(cam, VanillaCam);
        }
        catch (Exception ex)
        {
            Log("Camera restore failed: " + ex.Message);
        }
    }

    [Redirect(typeof(RPawnPlayerCombat), nameof(RPawnPlayerCombat.SetCombatFOV))]
    private static void SetCombatFovRedirect(RPawnPlayerCombat self, float minFov, int minEnemies, float maxFov, int maxEnemies, int enemies)
    {
        float add = _cam.Ptr != 0 ? _camApplied.Fov : 0f;
        if (add != 0f)
        {
            minFov = minFov > 0f ? Math.Clamp(minFov + add, 5f, 150f) : minFov;
            maxFov = maxFov > 0f ? Math.Clamp(maxFov + add, 5f, 150f) : maxFov;
        }

        self.SetCombatFOV(minFov, minEnemies, maxFov, maxEnemies, enemies);
    }

    private static bool FovReadoutVisible => _fovReadout != null && _fovReadout.On && _ready;

    private static void DrawFovReadout(Canvas c)
    {
        R3rdPersonCamera? cam = PlayerCam();
        if (cam == null)
            return;

        float real = cam.CameraCache.POV.FOV;
        float game = cam.FinalCameraFOVAngle;
        Pawn? pawn = Game.GetPlayerController(0)?.Pawn;
        float dist = pawn != null && pawn.IsValid ? Vector3.Distance(cam.CameraCache.POV.Location, pawn.Location) : 0f;
        string state = CamStateName(cam);
        if (state != _camState)
        {
            _camState = state;
            _camStateCat = CamCategory(state);
        }

        string mode = _camStateCat switch { CatGlide => "GLIDE", CatCar => "BATMOBILE", CatAim => "AIM", _ => "ON FOOT" };
        string line1 = "FOV " + real.ToString("0.0", CultureInfo.InvariantCulture) + "  ON SCREEN";
        string line2 = "GAME " + game.ToString("0.0", CultureInfo.InvariantCulture) + "  " + mode + "  " + state.ToUpperInvariant();
        string line3 = "DISTANCE " + dist.ToString("0", CultureInfo.InvariantCulture) + "  " + SitReadout();

        _ga = 1f;
        float scale = 0.66f;
        float lh = S(20);
        float w = Math.Max(SpacedW(c, line2, scale, 1.5f), SpacedW(c, line1, 0.8f, 2f)) + S(32);
        float h = S(14) + lh * 3f + S(8);
        float x = S(24);
        float y = c.ClipY - h - S(120);
        Rect(c, x, y, w, h, Panel, 0.75f);
        Rect(c, x, y, S(2), h, Accent, 0.9f);
        float ly = y + S(10);
        Spaced(c, x + S(14), ly + lh * 0.5f - TextH(c, 0.8f) * 0.5f, line1, Accent, 0.8f, 2f);
        ly += lh;
        Spaced(c, x + S(14), ly + lh * 0.5f - TextH(c, scale) * 0.5f, line2, TextMid, scale, 1.5f);
        ly += lh;
        Spaced(c, x + S(14), ly + lh * 0.5f - TextH(c, scale) * 0.5f, line3, TextMid, scale, 1.5f);
    }
}
