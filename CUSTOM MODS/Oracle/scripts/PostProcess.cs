using System;
using System.Collections.Generic;
using BmSDK;
using BmSDK.Engine;
using BmSDK.Framework;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace CeZeY.OracleScript;

public partial class Oracle
{
    private const double PostProcessRefresh = 2.0;

    private readonly record struct EffectState(bool UseWorld, bool Show, bool LightingOnly);

    private static readonly Dictionary<string, EffectState> _effectOriginals = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, bool> _volumeOriginals = new(StringComparer.Ordinal);
    private static int _ppMode;
    private static long _ppRefreshed;

    private static void ApplyPostProcess(int mode)
    {
        _ppMode = Math.Clamp(mode, 0, 2);
        bool atmo = _ppMode != 0 && AtmoWanted();
        _ppWithAtmo = atmo;
        SetGradeOff(atmo);
        WritePostProcess();
    }

    private static void PostProcessTick()
    {
        if (_ppMode == 0 || Stopwatch.GetElapsedTime(_ppRefreshed).TotalSeconds < PostProcessRefresh)
            return;

        WritePostProcess();
    }

    private static void WritePostProcess()
    {
        _ppRefreshed = Stopwatch.GetTimestamp();
        if (_ppMode == 0)
        {
            RestorePostProcess();
            return;
        }

        bool atmo = _ppWithAtmo;
        bool lightingOnly = _ppMode == 2;
        foreach (PostProcessEffect e in Game.FindObjects<PostProcessEffect>())
        {
            if (e == null || !e.IsValid || e.IsClassDefaultObject)
                continue;

            string key = e.GetPathName(null);
            if (!_effectOriginals.ContainsKey(key))
                _effectOriginals[key] = new EffectState(e.bUseWorldSettings, e.bShowInGame, e.bAffectsLightingOnly);

            bool rock = e is RockAtmos || e is RockOn;
            bool useWorld = atmo && (rock || e is UberPostProcessEffect);
            bool light = lightingOnly && !(atmo && rock);
            if (e.bUseWorldSettings != useWorld)
                e.bUseWorldSettings = useWorld;
            if (!e.bShowInGame)
                e.bShowInGame = true;
            if (e.bAffectsLightingOnly != light)
                e.bAffectsLightingOnly = light;
        }

        foreach (PostProcessVolume v in Game.FindObjects<PostProcessVolume>())
        {
            if (v == null || !v.IsValid || v.IsClassDefaultObject)
                continue;

            string key = v.GetPathName(null);
            if (!_volumeOriginals.ContainsKey(key))
                _volumeOriginals[key] = v.bEnabled;
            if (v.bEnabled != atmo)
                v.bEnabled = atmo;
        }
    }

    private static void RestorePostProcess()
    {
        if (_effectOriginals.Count == 0 && _volumeOriginals.Count == 0)
            return;

        int restored = 0;
        foreach (PostProcessEffect e in Game.FindObjects<PostProcessEffect>())
        {
            if (e == null || !e.IsValid || e.IsClassDefaultObject || !_effectOriginals.TryGetValue(e.GetPathName(null), out EffectState o))
                continue;

            e.bUseWorldSettings = o.UseWorld;
            e.bShowInGame = o.Show;
            e.bAffectsLightingOnly = o.LightingOnly;
            restored++;
        }

        foreach (PostProcessVolume v in Game.FindObjects<PostProcessVolume>())
        {
            if (v != null && v.IsValid && !v.IsClassDefaultObject && _volumeOriginals.TryGetValue(v.GetPathName(null), out bool on))
                v.bEnabled = on;
        }

        _effectOriginals.Clear();
        _volumeOriginals.Clear();
        Log("Post processing back to the game's own settings (" + restored + " effects).");
    }
}
