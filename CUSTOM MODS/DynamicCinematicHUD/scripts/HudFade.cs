using System;
using System.Collections.Generic;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Framework;
using BmSDK.GFxUI;

namespace CeZeY.DynamicCinematicHudScript;

public partial class DynamicCinematicHUD
{
    private const float Stagger = 0.04f;
    private const int SearchIntervalTicks = 30;
    private const int RefreshIntervalTicks = 120;
    private const int ClipRefreshTicks = 60;
    private const int EnforceTicks = 6;

    private sealed class HudElement
    {
        public string Key = "";
        public string Label = "";
        public string Help = "";
        public int Mask;
        public int DefaultMask;
        public Func<RGFxMovieModularHud, RHudModule?>? Get;
        public float Level = 1f;
        public bool Want;
        public float Delay;
        public bool Touched;
        public GFxObject? Clip;
        public nint ClipOwner;
        public int ClipAge;
    }

    private static HudElement MakeElement(string key, string label, int mask, Func<RGFxMovieModularHud, RHudModule?>? get, string help)
    {
        return new HudElement { Key = key, Label = label, Mask = mask, DefaultMask = mask, Get = get, Help = help };
    }

    private static readonly HudElement[] Elements =
    {
        MakeElement("entire_hud", "ENTIRE HUD", 3, null, "Every HUD layer at once, including button prompts."),
        MakeElement("health", "HEALTH BAR", 3, h => h.HealthBarModule, "Health meter in the top-left corner."),
        MakeElement("targets", "COMPASS + TARGETS", 2, h => h.TargetsModule, "Compass strip, waypoints and target markers."),
        MakeElement("radar", "LOCAL RADAR", 3, h => h.LocalRadarModule, "Mini-map radar."),
        MakeElement("objectives", "OBJECTIVES", 0, h => h.ObjectivesModule, "Mission objective panel."),
        MakeElement("story", "STORY MODE", 2, h => h.StoryModeModule, "Story progress and mission info."),
        MakeElement("alerts", "ALERTS + SURVEILLANCE", 0, h => h.AlertsAndSurveillanceModule, "Enemy alert and surveillance indicators."),
        MakeElement("room_name", "LOCATION NAME", 0, h => h.RoomNameModule, "Area name shown when entering a location."),
    };

    private static RGFxMovieModularHud? _hud;
    private static nint _hudPtr;
    private static int _hudSearchCooldown;
    private static int _hudRefreshTicks;
    private static readonly List<GFxObject> _rootClips = new();
    private static int _rootAge;
    private static bool _manualHidden;
    private static int _enforceTicks;

    private static float Ease(float t)
    {
        return 0.5f - 0.5f * MathF.Cos(MathF.PI * Math.Clamp(t, 0f, 1f));
    }

    private static void ResetFades()
    {
        _hud = null;
        _hudPtr = 0;
        _hudSearchCooldown = 0;
        _hudRefreshTicks = 0;
        _rootClips.Clear();
        _manualHidden = false;

        foreach (HudElement e in Elements)
        {
            e.Level = 1f;
            e.Want = false;
            e.Delay = 0f;
            e.Touched = false;
            e.Clip = null;
            e.ClipOwner = 0;
        }
    }

    private static void UpdateHud(float dt)
    {
        int active = _enabled ? _activeCtx : 0;

        bool any = false;
        foreach (HudElement e in Elements)
        {
            if ((e.Mask & active) != 0 || e.Touched || e.Level < 1f || e.Want)
                any = true;
        }

        if (!any)
            return;

        RGFxMovieModularHud? hud = ResolveHud();
        if (!IsUsable(hud) || !HudReady(hud!))
            return;

        if (hud!.Ptr != _hudPtr)
        {
            _hudPtr = hud.Ptr;
            _rootClips.Clear();
            _manualHidden = false;
            foreach (HudElement e in Elements)
            {
                e.Touched = false;
                e.Clip = null;
            }
        }

        bool entireWant = (Elements[0].Mask & active) != 0;
        for (int i = 1; i < Elements.Length; i++)
            StepModule(hud, Elements[i], !entireWant && (Elements[i].Mask & active) != 0, i, dt);

        StepEntire(hud, Elements[0], entireWant, dt);
    }

    private static void Advance(HudElement e, bool want, int order, float dt)
    {
        if (want != e.Want)
        {
            e.Want = want;
            e.Delay = order * Stagger;
        }

        if (e.Delay > 0f)
        {
            e.Delay -= dt;
            return;
        }

        float duration = want ? _fadeOut : _fadeIn;
        if (_instant || duration <= 0.01f)
            e.Level = want ? 0f : 1f;
        else
            e.Level = Math.Clamp(e.Level + (want ? -dt : dt) / duration, 0f, 1f);
    }

    private static void StepModule(RGFxMovieModularHud hud, HudElement e, bool want, int order, float dt)
    {
        Advance(e, want, order, dt);
        if (!e.Want && !e.Touched && e.Level >= 1f)
            return;

        RHudModule? m = e.Get?.Invoke(hud);
        bool ready = false;
        if (m != null && m.IsValid)
        {
            try { ready = m.IsReadyToCallFunctions(); } catch { }
        }

        if (!ready)
        {
            if (!e.Want)
            {
                e.Level = 1f;
                e.Touched = false;
                e.Clip = null;
            }

            return;
        }

        GFxObject? clip = ModuleClip(e, m!);
        if (clip == null)
            return;

        if (!e.Touched)
        {
            e.Touched = true;
            if (_diag)
                Log("FADE " + e.Key);
        }

        if (!e.Want && e.Level >= 1f)
        {
            clip.SetFloat(new FString("alpha"), 1f);
            e.Touched = false;
            e.Clip = null;
            if (_diag)
                Log("SHOW " + e.Key);
            return;
        }

        clip.SetFloat(new FString("alpha"), Ease(e.Level));
    }

    private static GFxObject? ModuleClip(HudElement e, RHudModule m)
    {
        if (e.Clip != null && e.ClipOwner == m.Ptr && ++e.ClipAge < ClipRefreshTicks && e.Clip.IsValid)
            return e.Clip;

        e.Clip = null;
        e.ClipOwner = m.Ptr;
        e.ClipAge = 0;

        RGFxMovie? movie = m.GfxParentMovie;
        if (movie == null || !movie.IsValid)
            return null;

        string root = m.LocalRootString.ToString() ?? "";
        if (root.Length == 0)
            return null;

        GFxObject? clip = movie.GetVariableObject(new FString(root));
        if (clip != null && clip.IsValid)
            e.Clip = clip;

        return e.Clip;
    }

    private static void StepEntire(RGFxMovieModularHud hud, HudElement e, bool want, float dt)
    {
        Advance(e, want, 0, dt);
        if (!e.Want && !e.Touched && e.Level >= 1f)
            return;

        if (!e.Touched)
        {
            e.Touched = true;
            CollectRoots(hud);
            if (_diag)
                Log("FADE entire_hud roots=" + _rootClips.Count);
        }
        else if (++_rootAge >= ClipRefreshTicks)
        {
            CollectRoots(hud);
        }

        if (e.Level <= 0f)
        {
            if (!_manualHidden)
            {
                SetRootsAlpha(0f);
                SetManualVisibility(hud, false);
                _manualHidden = true;
                _enforceTicks = 0;
                LogHudFlags("HIDE entire_hud", hud);
            }
            else if (++_enforceTicks >= EnforceTicks)
            {
                _enforceTicks = 0;
                bool manual = true;
                try { manual = hud.bHudVisibilityManual; } catch { }
                if (manual)
                    SetManualVisibility(hud, false);
            }

            return;
        }

        if (_manualHidden)
        {
            SetRootsAlpha(0f);
            SetManualVisibility(hud, true);
            _manualHidden = false;
            LogHudFlags("SHOW entire_hud", hud);
        }

        if (!e.Want && e.Level >= 1f)
        {
            RestoreRoots();
            e.Touched = false;
            return;
        }

        SetRootsAlpha(Ease(e.Level));
    }

    private static IEnumerable<RGFxMovieModularHudBase?> HudMovies(RGFxMovieModularHud hud)
    {
        yield return hud;
        yield return hud.TargetsEtcGfxMovie;
        yield return hud.LocalRadarGfxMovie;
        yield return hud.BatmobileGfxMovie;

        foreach (var disposable in hud.DisposableGfxMovies)
            yield return disposable;
    }

    private static void CollectRoots(RGFxMovieModularHud hud)
    {
        _rootAge = 0;
        _rootClips.Clear();

        foreach (RGFxMovieModularHudBase? movie in HudMovies(hud))
        {
            if (movie == null || !movie.IsValid)
                continue;

            try
            {
                GFxObject? root = movie.GetVariableObject(new FString("root"));
                if (root == null || !root.IsValid)
                    continue;

                _rootClips.Add(root);
            }
            catch
            {
            }
        }
    }

    private static void SetRootsAlpha(float level)
    {
        foreach (GFxObject clip in _rootClips)
        {
            try
            {
                if (clip.IsValid)
                    clip.SetFloat(new FString("alpha"), level);
            }
            catch
            {
            }
        }
    }

    private static void RestoreRoots()
    {
        SetRootsAlpha(1f);
        _rootClips.Clear();
    }

    private static void RestoreHudNow()
    {
        RGFxMovieModularHud? hud = IsUsable(_hud) ? _hud : FindHud();

        for (int i = 1; i < Elements.Length; i++)
        {
            HudElement e = Elements[i];
            if (e.Touched && e.Clip != null && e.Clip.IsValid)
                e.Clip.SetFloat(new FString("alpha"), 1f);
        }

        if (Elements[0].Touched)
        {
            if (_manualHidden && IsUsable(hud))
                SetManualVisibility(hud!, true);
            RestoreRoots();
        }

        ResetFades();
    }

    private static void SetManualVisibility(RGFxMovieModularHud hud, bool visible)
    {
        foreach (RGFxMovieModularHudBase? movie in HudMovies(hud))
        {
            if (movie == null || !movie.IsValid)
                continue;

            try
            {
                movie.ManuallySetHudVisibility(visible);
            }
            catch
            {
            }
        }
    }

    private static void LogHudFlags(string tag, RGFxMovieModularHud hud)
    {
        if (!_diag)
            return;

        try
        {
            Log(tag + ": manual=" + hud.bHudVisibilityManual + " game=" + hud.bHudVisibilityGame +
                " priorityHidden=" + hud.bIsPriorityHidden + " visibleLast=" + hud.bHUDVisibleLast);
        }
        catch (Exception ex)
        {
            Log(tag + ": could not read flags: " + ex.Message);
        }
    }

    private static RGFxMovieModularHud? ResolveHud()
    {
        if (!IsUsable(_hud))
        {
            if (--_hudSearchCooldown > 0)
                return null;

            _hud = FindHud();
            _hudSearchCooldown = SearchIntervalTicks;
            _hudRefreshTicks = 0;
        }
        else if (++_hudRefreshTicks >= RefreshIntervalTicks)
        {
            RGFxMovieModularHud? found = FindHud();
            if (found != null)
                _hud = found;
            _hudRefreshTicks = 0;
        }

        return _hud;
    }

    private static bool IsUsable(GameObject? obj) => obj != null && obj.IsValid;

    private static RGFxMovieModularHud? FindHud()
    {
        try
        {
            RGFxMovieModularHud? fallback = null;
            foreach (RGFxMovieModularHud h in Game.FindObjects<RGFxMovieModularHud>())
            {
                try
                {
                    if (h == null || !h.IsValid || h.Ptr == 0 || h.IsClassDefaultObject)
                        continue;

                    RHudModuleHealthBar? hb = h.HealthBarModule;
                    if (hb == null || !hb.IsValid)
                        continue;

                    RGFxMovieModularHud? owner = hb.ModularHudMovie;
                    if (owner != null && owner.IsValid && owner.Ptr == h.Ptr)
                        return h;

                    fallback ??= h;
                }
                catch
                {
                }
            }

            return fallback;
        }
        catch
        {
            return null;
        }
    }

    private static bool HudReady(RGFxMovieModularHud hud)
    {
        try
        {
            if (!hud.bCommonElementsInitialised)
                return false;

            RHudModuleHealthBar? hb = hud.HealthBarModule;
            return hb != null && hb.IsValid;
        }
        catch
        {
            return false;
        }
    }
}
