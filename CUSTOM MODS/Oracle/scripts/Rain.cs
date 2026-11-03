using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BmSDK;
using BmSDK.AkAudio;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace CeZeY.OracleScript;

public partial class Oracle
{
    private const double RainRefresh = 2.0;
    private const string RainDensityParam = "Ww_Parameters.RainDensity";

    private sealed record RainLook(string Key, string Label, float Density, float Width, float Wet, bool Sound);

    private static readonly RainLook[] RainLooks =
    {
        new("vanilla", "VANILLA", 1f, 1f, 1f, true),
        new("off", "NO RAIN", 0f, 1f, 0f, false),
        new("drizzle", "DRIZZLE", 0.3f, 0.8f, 0.7f, true),
        new("heavy", "HEAVY", 1.9f, 1.15f, 1.3f, true),
        new("storm", "STORM", 3.2f, 1.4f, 1.6f, true),
    };

    private readonly record struct RainState(float Density, float Width, float Box, AkEvent? Bed, float Wet, float Damp, float Dark, float Wake);

    private static readonly PropertyInfo[] SurfaceRainSounds = typeof(PhysicalMaterial).GetProperties()
        .Where(p => p.Name.StartsWith("SurfaceRainSound", StringComparison.Ordinal) && p.PropertyType == typeof(AkEvent) && p.CanWrite)
        .ToArray();

    private static readonly Dictionary<string, RainState> _rainOriginals = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _rainMuted = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, AkEvent?[]> _rainSurfaces = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _rainEmitters = new(StringComparer.Ordinal);
    private static AkParameterName? _rainParam;
    private static bool _rainParamSet;
    private static float _rainResync = -1f;
    private static int _rainLook;
    private static float _rainWet = 1f;
    private static float _rainBox = 1f;
    private static long _rainRefreshed;
    private static bool _rainDirty;

    private static void BuildRain()
    {
        var keys = new string[RainLooks.Length];
        var labels = new string[RainLooks.Length];
        for (int i = 0; i < RainLooks.Length; i++)
        {
            keys[i] = RainLooks[i].Key;
            labels[i] = RainLooks[i].Label;
        }

        Choice("rain", "RAIN", "Changes the rain: none, a drizzle, heavy rain or a storm. NO RAIN also dries the streets and silences the rain. Works together with the sky & fog looks and post processing.",
            keys, labels, i => { _rainLook = Math.Clamp(i, 0, RainLooks.Length - 1); _rainDirty = true; });
        Slider("rain_wetness", "WET STREETS", "How wet and shiny the streets, rooftops and suits look. 0% = completely dry.", 1f, 0f, 2f, 0.1f, 1.6f, Percent,
            v => { _rainWet = v; _rainDirty = true; });
        Slider("rain_area", "RAIN AREA", "Size of the rain box around you. Smaller packs the drops close around the camera, bigger spreads them further out and thinner.", 1f, 0.25f, 3f, 0.25f, 2f, Percent,
            v => { _rainBox = v; _rainDirty = true; });
    }

    private static void ForgetRain()
    {
        try
        {
            if (_rainParamSet || _rainSurfaces.Count > 0)
            {
                _rainEmitters.Clear();
                RestoreRainAudio();
            }
        }
        catch (Exception ex)
        {
            Log("Rain audio restore failed: " + ex.Message);
        }

        _rainOriginals.Clear();
        _rainMuted.Clear();
        _rainSurfaces.Clear();
        _rainEmitters.Clear();
        _rainParam = null;
        _rainParamSet = false;
        _rainDirty = true;
    }

    private static bool RainChanged => _rainOriginals.Count > 0 || _rainSurfaces.Count > 0 || _rainEmitters.Count > 0 || _rainParamSet;

    private static void RainTick(float dt)
    {
        if (_rainResync >= 0f)
        {
            _rainResync -= dt;
            if (_rainResync < 0f)
                ResyncRainParam();
        }

        bool vanilla = _rainLook == 0 && MathF.Abs(_rainWet - 1f) < 0.001f && MathF.Abs(_rainBox - 1f) < 0.001f;
        if (vanilla)
        {
            if (RainChanged)
                RestoreRain();
            return;
        }

        if (!_rainDirty && Stopwatch.GetElapsedTime(_rainRefreshed).TotalSeconds < RainRefresh)
            return;

        _rainDirty = false;
        _rainRefreshed = Stopwatch.GetTimestamp();
        RainLook look = RainLooks[_rainLook];
        float wet = look.Wet * _rainWet;
        float damp = Math.Min(wet, 1f);
        bool dry = wet < 0.001f;

        foreach (RainVolume v in Game.FindObjects<RainVolume>())
        {
            if (v == null || !v.IsValid || v.IsClassDefaultObject)
                continue;

            RRainComponent rc = v.RainComponent;
            RainMapComponent map = v.RainMap;
            if (rc == null || !rc.IsValid)
                continue;

            bool hasMap = map != null && map.IsValid;
            string key = v.GetPathName(null);
            ref RRainComponent.FRockRainSettings s = ref rc.RainSettings;
            if (!_rainOriginals.TryGetValue(key, out RainState o))
            {
                o = hasMap
                    ? new RainState(s.Density, s.Width, rc.BoxSize, s.RainSoundBed, map.WetnessGlobalScale, map.MinimumDampAmount, map.Darkening, map.RainWakeAmount)
                    : new RainState(s.Density, s.Width, rc.BoxSize, s.RainSoundBed, 1f, 0f, 0f, 0f);
                _rainOriginals[key] = o;
            }

            s.Density = o.Density * look.Density;
            s.Width = o.Width * look.Width;
            rc.BoxSize = o.Box * _rainBox;
            if (hasMap)
            {
                map.WetnessGlobalScale = o.Wet * wet;
                map.MinimumDampAmount = o.Damp * damp;
                map.Darkening = o.Dark * damp;
                map.RainWakeAmount = o.Wake * Math.Min(look.Density, 1f);
            }

            bool muted = _rainMuted.Contains(key);
            if (!look.Sound && !muted)
            {
                rc.DisableRainSound();
                s.RainSoundBed = null;
                _rainMuted.Add(key);
            }
            else if (look.Sound && muted)
            {
                s.RainSoundBed = o.Bed;
                rc.EnableRainSound();
                _rainMuted.Remove(key);
            }

            v.SetRainSettings();
        }

        if (look.Sound)
            RestoreRainAudio();
        else
            MuteRainAudio();

        if (dry)
            DryPawns();
    }

    private static void MuteRainAudio()
    {
        _rainParam ??= Game.FindObject<AkParameterName>(RainDensityParam);
        if (_rainParam != null && _rainParam.IsValid)
        {
            AkWwise.SetGlobalAudioParameter(_rainParam, 0f);
            _rainParamSet = true;
        }

        foreach (PhysicalMaterial m in Game.FindObjects<PhysicalMaterial>())
        {
            if (m == null || !m.IsValid || m.IsClassDefaultObject)
                continue;

            string key = m.GetPathName(null);
            if (_rainSurfaces.ContainsKey(key))
                continue;

            var saved = new AkEvent?[SurfaceRainSounds.Length];
            bool any = false;
            for (int i = 0; i < SurfaceRainSounds.Length; i++)
            {
                saved[i] = SurfaceRainSounds[i].GetValue(m) as AkEvent;
                if (saved[i] == null)
                    continue;
                any = true;
                SurfaceRainSounds[i].SetValue(m, null);
            }

            if (any)
                _rainSurfaces[key] = saved;
        }

        foreach (AkEmitter e in Game.FindObjects<AkEmitter>())
        {
            if (e == null || !e.IsValid || e.IsClassDefaultObject || !e.bEmitterEnabled || !PlaysRain(e))
                continue;

            e.DisableEmitter();
            _rainEmitters.Add(e.GetPathName(null));
        }
    }

    private static void ResyncRainParam()
    {
        if (_rainParamSet || RainLooks[_rainLook].Density <= 0f)
            return;

        _rainParam ??= Game.FindObject<AkParameterName>(RainDensityParam);
        if (_rainParam != null && _rainParam.IsValid)
            AkWwise.SetGlobalAudioParameter(_rainParam, AkSoundDirector.LocationRainDensity(AkWwise.GetActiveListenerPosition()));
    }

    private static bool PlaysRain(AkEmitter e)
    {
        var events = e.EmitterEvents;
        for (int i = 0; i < events.Count; i++)
        {
            AkEvent ev = events[i];
            if (ev != null && ev.IsValid && ev.Name.ToString().Contains("Rain", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static void RestoreRainAudio()
    {
        if (_rainParamSet)
        {
            _rainParamSet = false;
            _rainResync = 1f;
        }

        if (_rainSurfaces.Count > 0)
        {
            foreach (PhysicalMaterial m in Game.FindObjects<PhysicalMaterial>())
            {
                if (m == null || !m.IsValid || m.IsClassDefaultObject || !_rainSurfaces.TryGetValue(m.GetPathName(null), out AkEvent?[]? saved))
                    continue;

                for (int i = 0; i < SurfaceRainSounds.Length; i++)
                {
                    if (saved[i] != null && saved[i]!.IsValid)
                        SurfaceRainSounds[i].SetValue(m, saved[i]);
                }
            }

            _rainSurfaces.Clear();
        }

        if (_rainEmitters.Count > 0)
        {
            foreach (AkEmitter e in Game.FindObjects<AkEmitter>())
            {
                if (e != null && e.IsValid && !e.IsClassDefaultObject && !e.bEmitterEnabled && _rainEmitters.Contains(e.GetPathName(null)))
                    e.EnableEmitter();
            }

            _rainEmitters.Clear();
        }
    }

    private static void DryPawns()
    {
        foreach (RPawnCharacter p in Game.FindObjects<RPawnCharacter>())
        {
            if (p == null || !p.IsValid || p.IsClassDefaultObject)
                continue;
            if (p.WetnessAccumulationAlpha > 0f)
                p.WetnessAccumulationAlpha = 0f;
            if (p.RainAccumulationAlpha > 0f)
                p.RainAccumulationAlpha = 0f;
        }

        if (Game.GetPlayerController(0)?.Pawn is RVehicle car && car.IsValid)
        {
            car.WetnessAccumulationAlpha = 0f;
            car.RainAccumulationAlpha = 0f;
        }
    }

    private static void RestoreRain()
    {
        foreach (RainVolume v in Game.FindObjects<RainVolume>())
        {
            if (v == null || !v.IsValid || v.IsClassDefaultObject)
                continue;

            string key = v.GetPathName(null);
            RRainComponent rc = v.RainComponent;
            if (!_rainOriginals.TryGetValue(key, out RainState o) || rc == null || !rc.IsValid)
                continue;

            ref RRainComponent.FRockRainSettings s = ref rc.RainSettings;
            s.Density = o.Density;
            s.Width = o.Width;
            rc.BoxSize = o.Box;
            RainMapComponent map = v.RainMap;
            if (map != null && map.IsValid)
            {
                map.WetnessGlobalScale = o.Wet;
                map.MinimumDampAmount = o.Damp;
                map.Darkening = o.Dark;
                map.RainWakeAmount = o.Wake;
            }
            if (_rainMuted.Remove(key))
            {
                s.RainSoundBed = o.Bed;
                rc.EnableRainSound();
            }
            v.SetRainSettings();
        }

        RestoreRainAudio();
        _rainOriginals.Clear();
        _rainMuted.Clear();
    }
}
