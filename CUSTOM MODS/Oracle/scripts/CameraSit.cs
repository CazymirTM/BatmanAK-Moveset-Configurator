using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace CeZeY.OracleScript;

public partial class Oracle
{
    private const double SitDetectGap = 0.1;
    private const float SitFovMin = -40f;
    private const float SitFovMax = 40f;
    private const string SlotFolder = "camera_slots";

    private sealed class CamSit
    {
        public string Key = "";
        public string Label = "";
        public string[] States = Array.Empty<string>();
        public string[] Cfgs = Array.Empty<string>();
        public Feature? Fov, Back, Side, Up, Pivot;
    }

    private static readonly CamSit[] CamSits =
    {
        new CamSit { Key = "walk", Label = "WALK",
            States = new[] { "Walk", "WalkCanister", "AgilityWalk", "BruceWayneWalk" },
            Cfgs = new[] { "WalkCamConfig", "WalkCanisterCamConfig", "AgilityWalkCamConfig", "AlternativeWalkCamConfig" } },
        new CamSit { Key = "run", Label = "RUN",
            States = new[] { "Run", "RooftopRun" },
            Cfgs = new[] { "RunCamConfig", "RooftopRunCamConfig" } },
        new CamSit { Key = "stealth", Label = "STEALTH",
            States = new[] { "Stealth", "StealthWall", "CrouchUnderObstacle", "MirroredStealth", "CornerStealth", "CornerStealthMirrored", "StealthHold", "HideCam", "HideCamMirrored", "GrateStealthCamera", "GrateCamera", "HighGrateCamera", "TunnelCamera", "PerchLedgeCam" },
            Cfgs = new[] { "StealthCamConfig", "StealthWallCamConfig", "CrouchUnderObstacleCamConfig", "SilentPredatorCamConfig", "PerchLedgeCamConfig", "StandingHoldCamConfig", "GrateCameraConfig", "HighGrateCameraConfig" } },
        new CamSit { Key = "combat", Label = "COMBAT",
            States = new[] { "BrawlCombatCamera", "IndividualCombatCamera", "HighCombatCamera", "NightwingBossFightCamera", "DemonTrials", "FloorAttack" },
            Cfgs = new[] { "CombatWalkCamConfig", "CorridorCombatCamConfig", "HighCombatCamConfig", "NightwingBossFightCamConfig", "DemonTrialsCamConfig", "FloorAttackCamConfig" } },
        new CamSit { Key = "takedown", Label = "TAKEDOWNS",
            States = new[] { "PairedAnimationCamera", "QuickTakedownCamera", "FearTakedown", "StealthGrab", "TakedownFromAboveCam", "DualTakedownCamera", "LedgeHangTakedownCamera", "LedgeHangTakedownStartCamera" },
            Cfgs = new[] { "QuickTakedownCamConfig", "FearTakedownCamConfig", "StandingGrabCamConfig", "TakedownFromAboveCamConfig", "LedgeHangTakedownCamConfig", "LedgeHangTakedownStartCamConfig" } },
        new CamSit { Key = "grapple", Label = "GRAPPLE",
            States = new[] { "Grapple", "GrappleRescue", "HqGrapple" },
            Cfgs = new[] { "GrappleCamConfig", "GrappleRescueCamConfig", "HqGrappleStartCamConfig" } },
        new CamSit { Key = "swing", Label = "SWINGING",
            States = new[] { "SwingCam", "MirroredSwingCam", "SwingingFromAboveCam", "LedgeSwingCamera", "PerchLedgeSwingCam", "EdgeSwing" },
            Cfgs = new[] { "SwingingCamConfig", "SwingingFromAboveCamConfig", "LedgeSwingCamConfig", "PerchLedgeSwingCamConfig" } },
        new CamSit { Key = "railing", Label = "RAILING",
            States = new[] { "RailingStealth", "DiveOverRailingCamera" },
            Cfgs = new[] { "RailingCamConfig" } },
        new CamSit { Key = "climb", Label = "CLIMBING & LEDGES",
            States = new[] { "WallClimbCam", "WallClimbStartCam", "VantageClimbCam", "LedgeHangCamera", "LedgeHangCameraKickedByThug", "HangCamera", "CeilingClimbCam", "CeilingClimbTunnnelCam", "CeilingToLedgeHangCamera", "CeilingToCatwalkCamera", "CatwalkToCeilingCamera", "CatwalkToCeilingOverRailingCamera", "VaultCam", "SlideCamera", "PounceCam" },
            Cfgs = new[] { "WallClimbCamConfig", "WallClimbStartCamConfig", "VantageClimbCamConfig", "LedgeHangCamConfig", "LedgeHangKickedByThugCamConfig", "HangPointCamConfig", "CeilingClimbCamConfig", "CeilingToLedgeHangCamConfig", "CeilingToCatwalkCamConfig", "SlideCameraConfig", "PounceCamConfig" } },
        new CamSit { Key = "fall", Label = "FALLING",
            States = new[] { "FallCam", "CapeChuteCamera", "DeepFallLandCam", "ForceLookDownCamera" },
            Cfgs = new[] { "CapeChuteConfig", "DeepFallLandCamConfig", "LookDownCamConfig" } },
    };

    private static readonly Dictionary<string, CamSit> _sitByState = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, CamSit> _sitByCfg = new(StringComparer.Ordinal);
    private static readonly List<Feature> _sitFeatures = new();
    private static Feature? _sitEdit;
    private static Feature? _sitBlend;
    private static Feature? _camBlend;
    private static bool _blendOn;
    private static float _blendOrig;
    private static float _blendWritten;
    private static Feature? _camSlot;
    private static CamSit? _sitNow;
    private static string _sitNowState = "";
    private static long _sitDetected;
    private static long _sitFrame;
    private static float _povNow;
    private static float _povTarget;
    private static bool _povHasLast;
    private static float _povLastBase;
    private static float _povLastWritten;

    private static void BuildSituations()
    {
        _sitByState.Clear();
        _sitByCfg.Clear();
        _sitFeatures.Clear();

        Group("FOV BY SITUATION");
        foreach (CamSit s in CamSits)
        {
            foreach (string st in s.States)
                _sitByState[st] = s;
            foreach (string c in s.Cfgs)
                _sitByCfg[c] = s;

            s.Fov = Slider("sit_fov_" + s.Key, s.Label + " FOV", "Extra field of view only while you are in this situation, on top of FIELD OF VIEW. It eases in and out as the situation changes.",
                0f, SitFovMin, SitFovMax, 1f, 15f, Deg, _ => { });
        }

        _sitBlend = Slider("sit_blend", "SITUATION FOV BLEND", "How quickly the view eases to the next situation's FOV. Higher is snappier.",
            6f, 1f, 20f, 1f, 12f, v => v.ToString("0", CultureInfo.InvariantCulture), _ => { });
        _sitBlend.Pref = true;
        _camBlend = Slider("cam_blend", "CAMERA BLEND TIME", "How long the camera takes to glide to its new position when the situation changes. Lower is snappier and cuts the zoom-in when you stop running. Higher is floatier.",
            1f, 0.1f, 3f, 0.1f, 0.5f, Times, _ => { });

        Group("CAMERA BY SITUATION");
        var keys = new string[CamSits.Length];
        var labels = new string[CamSits.Length];
        for (int i = 0; i < CamSits.Length; i++)
        {
            keys[i] = CamSits[i].Key;
            labels[i] = CamSits[i].Label;
        }

        _sitEdit = Choice("sit_edit", "SITUATION TO EDIT", "Pick a situation, then tune its camera with the sliders below. Each situation keeps its own values.",
            keys, labels, _ => _pages = null);
        _sitEdit.Pref = true;

        for (int i = 0; i < CamSits.Length; i++)
        {
            CamSit s = CamSits[i];
            int index = i;
            s.Back = SitSlider(s, index, "back", "BACK", "Moves the camera further behind your character in this situation, or forward with negative values.", -200f, 300f);
            s.Side = SitSlider(s, index, "side", "SIDE", "Moves the camera sideways in this situation. Negative is left, positive is right.", -150f, 150f);
            s.Up = SitSlider(s, index, "up", "HEIGHT", "Raises or lowers the camera in this situation.", -100f, 150f);
            s.Pivot = SitSlider(s, index, "pivot", "PIVOT HEIGHT", "Moves the point the camera looks at up or down in this situation.", -100f, 100f);
        }
    }

    private static Feature SitSlider(CamSit s, int index, string part, string label, string help, float min, float max)
    {
        Feature f = Slider("sit_" + part + "_" + s.Key, s.Label + " " + label, help, 0f, min, max, 5f, 30f, Units, _ => { });
        f.Show = () => _sitEdit != null && _sitEdit.Choice == index;
        _sitFeatures.Add(f);
        return f;
    }

    private static float SitSignature(out bool on)
    {
        float sig = 0f;
        on = false;
        for (int i = 0; i < _sitFeatures.Count; i++)
        {
            float v = _sitFeatures[i].Value;
            if (v != 0f)
            {
                on = true;
                sig += v * (i + 1);
            }
        }
        return sig;
    }

    private static R3rdPersonCamera.FFreeCameraConfig SitConfig(R3rdPersonCamera.FFreeCameraConfig c, string cfgName, CamSet s)
    {
        if (!s.SitOn || !_sitByCfg.TryGetValue(cfgName, out CamSit? sit))
            return c;

        var d = new Vector3(-(sit.Back?.Value ?? 0f), sit.Side?.Value ?? 0f, sit.Up?.Value ?? 0f);
        c.StateFreeCameraSitOffsetMin += d;
        c.StateFreeCameraSitOffsetMax += d;
        if (c.UseStateFreeCameraSitOffsetUp)
            c.StateFreeCameraSitOffsetUp += d;
        if (c.bUseSeparate43Settings)
        {
            c.StateFreeCamera43SitOffsetMin += d;
            c.StateFreeCamera43SitOffsetMax += d;
            c.StateFreeCamera43SitOffsetLookUp += d;
        }

        c.CameraPivotOffset += new Vector3(0f, 0f, sit.Pivot?.Value ?? 0f);
        return c;
    }

    private static void ResetSituations()
    {
        foreach (CamSit s in CamSits)
        {
            SetCamFeature(s.Fov, 0f);
            SetCamFeature(s.Back, 0f);
            SetCamFeature(s.Side, 0f);
            SetCamFeature(s.Up, 0f);
            SetCamFeature(s.Pivot, 0f);
        }
    }

    private static void BlendTick(R3rdPersonCamera cam)
    {
        float k = _camBlend?.Value ?? 1f;
        bool want = MathF.Abs(k - 1f) > 0.001f;
        if (!want && !_blendOn)
            return;

        float now = cam.TransitionTime;
        if (!_blendOn || MathF.Abs(now - _blendWritten) > 0.001f)
            _blendOrig = now;

        float next = want ? _blendOrig * k : _blendOrig;
        if (MathF.Abs(now - next) > 0.001f)
            cam.TransitionTime = next;
        _blendWritten = next;
        _blendOn = want;
    }

    private static void RestoreBlend(R3rdPersonCamera cam)
    {
        if (!_blendOn)
            return;

        if (MathF.Abs(cam.TransitionTime - _blendWritten) < 0.001f)
            cam.TransitionTime = _blendOrig;
        _blendOn = false;
    }

    private static bool AnySitFov()
    {
        foreach (CamSit s in CamSits)
        {
            if (s.Fov != null && s.Fov.Value != 0f)
                return true;
        }
        return false;
    }

    private static string SitReadout()
    {
        string name = _sitNow?.Label ?? "OTHER";
        return MathF.Abs(_povNow) < 0.05f ? name : name + " " + (_povNow > 0f ? "+" : "") + _povNow.ToString("0.0", CultureInfo.InvariantCulture);
    }

    [Redirect(typeof(R3rdPersonCamera), nameof(R3rdPersonCamera.UpdateCamera))]
    private static void UpdateCameraRedirect(R3rdPersonCamera self, float deltaTime)
    {
        bool mine = self.Ptr == _cam.Ptr;
        if (mine && _povHasLast)
        {
            TakeBackPov(self);
            _povHasLast = false;
        }

        self.UpdateCamera(deltaTime);

        if (!mine)
            return;

        try
        {
            ApplySitFov(self);
        }
        catch (Exception ex)
        {
            LogThrottled("Situation FOV failed: " + ex.Message);
        }
    }

    private static void TakeBackPov(R3rdPersonCamera cam)
    {
        var pov = cam.CameraCache.POV;
        if (MathF.Abs(pov.FOV - _povLastWritten) < 0.01f)
        {
            pov.FOV = _povLastBase;
            cam.CameraCache.POV = pov;
        }
    }

    private static void ApplySitFov(R3rdPersonCamera cam)
    {
        long now = Stopwatch.GetTimestamp();
        float dt = _sitFrame == 0 ? 0f : Math.Clamp((float)Stopwatch.GetElapsedTime(_sitFrame, now).TotalSeconds, 0f, 0.1f);
        _sitFrame = now;

        bool any = AnySitFov();
        if (!any && MathF.Abs(_povNow) < 0.01f)
        {
            _povNow = 0f;
            if (!FovReadoutOn && !_menuOpen)
                return;
        }

        if (Stopwatch.GetElapsedTime(_sitDetected, now).TotalSeconds >= SitDetectGap)
        {
            _sitDetected = now;
            string state = cam.GetStateName().ToString() ?? "";
            if (state != _sitNowState)
            {
                _sitNowState = state;
                _sitByState.TryGetValue(state, out _sitNow);
            }
            _povTarget = any ? _sitNow?.Fov?.Value ?? 0f : 0f;
        }

        float rate = _sitBlend?.Value ?? 6f;
        float k = dt <= 0f ? 1f : 1f - MathF.Exp(-rate * dt);
        _povNow += (_povTarget - _povNow) * k;
        if (MathF.Abs(_povTarget - _povNow) < 0.01f)
            _povNow = _povTarget;
        if (MathF.Abs(_povNow) < 0.01f)
            return;

        var pov = cam.CameraCache.POV;
        float baseFov = pov.FOV;
        if (!float.IsFinite(baseFov) || baseFov < 10f || baseFov > 170f)
            return;

        float next = Math.Clamp(baseFov + _povNow, 10f, 150f);
        pov.FOV = next;
        cam.CameraCache.POV = pov;
        _povHasLast = true;
        _povLastBase = baseFov;
        _povLastWritten = next;
    }

    private static bool FovReadoutOn => _fovReadout != null && _fovReadout.On;

    private static void ForgetSituationFov()
    {
        _povHasLast = false;
        _povNow = 0f;
        _povTarget = 0f;
        _blendOn = false;
        _sitNow = null;
        _sitNowState = "";
    }

    private static void BuildCameraSlots()
    {
        _camSlot = Choice("cam_slot", "MY PRESET SLOT", "Which of your own camera presets SAVE and LOAD use. They are stored as text files in the mod's camera_slots folder, so you can share them.",
            new[] { "1", "2", "3" }, new[] { "SLOT 1", "SLOT 2", "SLOT 3" }, _ => { });
        _camSlot.Pref = true;
        Action("cam_slot_save", "SAVE TO SLOT", "Saves every setting on this page into the chosen slot.", SaveCamSlot);
        Action("cam_slot_load", "LOAD FROM SLOT", "Loads the chosen slot. Anything not saved in it goes back to the default.", LoadCamSlot);
    }

    private static string SlotPath() => Path.Combine(_modDir, SlotFolder, "slot" + ((_camSlot?.Choice ?? 0) + 1) + ".txt");

    private static bool IsCamSlotFeature(Feature f) => f.Tab == TabCamera && !f.Pref && (f.Kind == KindSlider || f.Kind == KindToggle);

    private static void SaveCamSlot()
    {
        var sb = new StringBuilder();
        foreach (Feature f in Features)
        {
            if (IsCamSlotFeature(f) && f.Active)
                sb.Append(f.Id).Append('=').Append((f.Kind == KindToggle ? (f.On ? 1f : 0f) : f.Value).ToString("0.0###", CultureInfo.InvariantCulture)).AppendLine();
        }

        string path = SlotPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sb.ToString());
        ShowNotice("CAMERA SLOT " + ((_camSlot?.Choice ?? 0) + 1), "SAVED", true);
    }

    private static void LoadCamSlot()
    {
        string path = SlotPath();
        if (!File.Exists(path))
        {
            ShowNotice("CAMERA SLOT " + ((_camSlot?.Choice ?? 0) + 1), "EMPTY", false);
            return;
        }

        var saved = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in File.ReadAllLines(path))
        {
            int eq = raw.IndexOf('=');
            if (eq > 0 && float.TryParse(raw.Substring(eq + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                saved[raw.Substring(0, eq).Trim()] = v;
        }

        foreach (Feature f in Features)
        {
            if (!IsCamSlotFeature(f))
                continue;

            bool has = saved.TryGetValue(f.Id, out float v);
            if (f.Kind == KindToggle)
            {
                bool on = has && v > 0.5f;
                if (f.On != on)
                {
                    f.On = on;
                    SaveFeature(f);
                }
            }
            else
            {
                SetCamFeature(f, has ? v : f.Default);
            }
        }

        ShowNotice("CAMERA SLOT " + ((_camSlot?.Choice ?? 0) + 1), "LOADED", true);
    }
}
