using System;
using System.Collections.Generic;
using System.Globalization;
using BmSDK;
using BmSDK.Engine;
using BmSDK.Framework;
using FColor = BmSDK.GameObject.FColor;
using FLinearColor = BmSDK.GameObject.FLinearColor;
using PPSettings = BmSDK.Engine.PostProcessVolume.FPostProcessSettings;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace CeZeY.OracleScript;

public partial class Oracle
{
    private const string AtmoOriginalsKey = "CeZeY.Oracle.PostProcessFields";
    private const string DefaultSettingsKey = "__WorldDefault__";
    private const double AtmoScanGap = 1.0;
    private const float AtmoBlendRate = 5f;
    private const int CustomAtmo = 7;

    private readonly record struct AtmoLook(float R, float G, float B, float Strength, float Fog, float Desat)
    {
        public static readonly AtmoLook None = new(1f, 1f, 1f, 0f, 1f, 0f);

        public bool IsNone => Strength < 0.001f && MathF.Abs(Fog - 1f) < 0.001f && Desat < 0.001f;

        public static AtmoLook Lerp(AtmoLook a, AtmoLook b, float t) => new(
            a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t,
            a.Strength + (b.Strength - a.Strength) * t, a.Fog + (b.Fog - a.Fog) * t, a.Desat + (b.Desat - a.Desat) * t);

        public float Distance(AtmoLook o) => Math.Max(Math.Max(MathF.Abs(R - o.R), Math.Max(MathF.Abs(G - o.G), MathF.Abs(B - o.B))),
            Math.Max(MathF.Abs(Strength - o.Strength), Math.Max(MathF.Abs(Fog - o.Fog), MathF.Abs(Desat - o.Desat))));
    }

    private sealed record AtmoPreset(string Key, string Label, float R, float G, float B, float Desat);

    private static readonly AtmoPreset[] AtmoPresets =
    {
        new("vanilla", "VANILLA", 1f, 1f, 1f, 0f),
        new("blood_moon", "BLOOD MOON", 1f, 0.1f, 0.06f, 0f),
        new("fear_toxin", "FEAR TOXIN", 0.3f, 1f, 0.25f, 0f),
        new("arctic", "ARCTIC", 0.45f, 0.85f, 1f, 0.3f),
        new("golden_hour", "GOLDEN HOUR", 1f, 0.5f, 0.06f, 0f),
        new("joker", "JOKER", 0.65f, 0.25f, 1f, 0f),
        new("noir", "NOIR", 1f, 1f, 1f, 0.9f),
        new("custom", "CUSTOM", 1f, 1f, 1f, 0f),
    };

    private static Feature? _atmoChoice;
    private static Feature? _atmoHue;
    private static Feature? _atmoStrength;
    private static Feature? _atmoFog;
    private static Feature? _atmoDesat;

    private static AtmoLook _atmoNow = AtmoLook.None;
    private static AtmoLook _atmoApplied = AtmoLook.None;
    private static readonly List<(PostProcessVolume Vol, string Key)> _ppVolumes = new();
    private static readonly HashSet<string> _ppTouched = new();
    private static long _ppScanned;
    private static bool _atmoForce;
    private static bool _gradeOff;
    private static bool _ppWithAtmo;

    private static Dictionary<string, double[]> PostProcessOriginals
    {
        get
        {
            if (AppDomain.CurrentDomain.GetData(AtmoOriginalsKey) is Dictionary<string, double[]> d)
                return d;

            var made = new Dictionary<string, double[]>();
            AppDomain.CurrentDomain.SetData(AtmoOriginalsKey, made);
            return made;
        }
    }

    private struct LookFields
    {
        public FColor Gradient, H1, H2, D1, D2;
        public FLinearColor High, Mid, Shadow;
        public float Desat, H1Den, H2Den, D1Den, D2Den, Haze;
        public nint Lut;
    }

    private static double PackColor(FColor c) => c.R | (c.G << 8) | (c.B << 16) | ((long)c.A << 24);

    private static FColor UnpackColor(double d)
    {
        long v = (long)d;
        return new FColor { R = (byte)v, G = (byte)(v >> 8), B = (byte)(v >> 16), A = (byte)(v >> 24) };
    }

    private static LookFields Read(ref PPSettings st)
    {
        Texture lut = st.ColorGrading_LookupTable;
        return new LookFields
        {
            Gradient = st.AtmosGlobal_Gradient_Colour, H1 = st.AtmosH1_Colour, H2 = st.AtmosH2_Colour, D1 = st.AtmosD1_Colour, D2 = st.AtmosD2_Colour,
            High = st.Scene_HighLights, Mid = st.Scene_MidTones, Shadow = st.Scene_Shadows,
            Desat = st.Scene_Desaturation, H1Den = st.AtmosH1_Density, H2Den = st.AtmosH2_Density, D1Den = st.AtmosD1_Density, D2Den = st.AtmosD2_Density,
            Haze = st.AtmosHazeWeight, Lut = Remember(lut)
        };
    }

    private static nint Remember(Texture? lut)
    {
        if (lut == null || !lut.IsValid)
            return 0;
        _luts[lut.Ptr] = lut;
        return lut.Ptr;
    }

    private static double[] Pack(LookFields f) => new double[]
    {
        PackColor(f.Gradient), PackColor(f.H1), PackColor(f.H2), PackColor(f.D1), PackColor(f.D2),
        f.High.R, f.High.G, f.High.B, f.High.A, f.Mid.R, f.Mid.G, f.Mid.B, f.Mid.A, f.Shadow.R, f.Shadow.G, f.Shadow.B, f.Shadow.A,
        f.Desat, f.H1Den, f.H2Den, f.D1Den, f.D2Den, f.Haze, f.Lut
    };

    private static LookFields Unpack(double[] a) => new LookFields
    {
        Gradient = UnpackColor(a[0]), H1 = UnpackColor(a[1]), H2 = UnpackColor(a[2]), D1 = UnpackColor(a[3]), D2 = UnpackColor(a[4]),
        High = new FLinearColor { R = (float)a[5], G = (float)a[6], B = (float)a[7], A = (float)a[8] },
        Mid = new FLinearColor { R = (float)a[9], G = (float)a[10], B = (float)a[11], A = (float)a[12] },
        Shadow = new FLinearColor { R = (float)a[13], G = (float)a[14], B = (float)a[15], A = (float)a[16] },
        Desat = (float)a[17], H1Den = (float)a[18], H2Den = (float)a[19], D1Den = (float)a[20], D2Den = (float)a[21], Haze = (float)a[22],
        Lut = (nint)(long)a[23]
    };

    private static void Write(ref PPSettings st, LookFields f)
    {
        st.AtmosGlobal_Gradient_Colour = f.Gradient;
        st.AtmosH1_Colour = f.H1;
        st.AtmosH2_Colour = f.H2;
        st.AtmosD1_Colour = f.D1;
        st.AtmosD2_Colour = f.D2;
        st.Scene_HighLights = f.High;
        st.Scene_MidTones = f.Mid;
        st.Scene_Shadows = f.Shadow;
        st.Scene_Desaturation = f.Desat;
        st.AtmosH1_Density = f.H1Den;
        st.AtmosH2_Density = f.H2Den;
        st.AtmosD1_Density = f.D1Den;
        st.AtmosD2_Density = f.D2Den;
        st.AtmosHazeWeight = f.Haze;

        Texture current = st.ColorGrading_LookupTable;
        nint now = current != null && current.IsValid ? current.Ptr : 0;
        if (now != f.Lut)
        {
            Texture? lut = f.Lut == 0 ? null : LutFor(f.Lut);
            if (f.Lut == 0 || lut != null)
                st.ColorGrading_LookupTable = lut!;
        }
    }

    private static readonly Dictionary<nint, Texture> _luts = new();

    private static Texture? LutFor(nint ptr)
    {
        if (_luts.TryGetValue(ptr, out Texture? t) && t.IsValid)
            return t;

        foreach (Texture tex in Game.FindObjects<Texture>())
        {
            if (tex != null && tex.Ptr == ptr)
            {
                _luts[ptr] = tex;
                return tex;
            }
        }

        return null;
    }

    private static void BuildAtmosphere()
    {
        var keys = new string[AtmoPresets.Length];
        var labels = new string[AtmoPresets.Length];
        for (int i = 0; i < AtmoPresets.Length; i++)
        {
            keys[i] = AtmoPresets[i].Key;
            labels[i] = AtmoPresets[i].Label;
        }

        _atmoChoice = Choice("atmosphere", "SKY & FOG LOOK", "Recolours the sky, fog and lighting. CUSTOM uses the hue below.", keys, labels, _ => _atmoForce = true);
        _atmoHue = Slider("atmo_hue", "CUSTOM HUE", "Colour for the CUSTOM look. Moving it switches the look to CUSTOM.", 200f, 0f, 360f, 5f, 0f,
            v => v.ToString("0", CultureInfo.InvariantCulture) + "°", _ => PickCustomAtmo());
        _atmoStrength = Slider("atmo_strength", "LOOK STRENGTH", "How strongly the chosen look tints the world.", 0.6f, 0.1f, 1f, 0.05f, 1f, Percent, _ => _atmoForce = true);
        _atmoFog = Slider("atmo_fog", "FOG DENSITY", "Thins out or thickens the city's fog and haze. Works with any look.", 1f, 0f, 3f, 0.1f, 2f, Times, _ => _atmoForce = true);
        _atmoDesat = Slider("atmo_desat", "DESATURATION", "Drains colour from the whole picture. Works with any look.", 0f, 0f, 1f, 0.05f, 0.6f, Percent, _ => _atmoForce = true);
    }

    private static void PickCustomAtmo()
    {
        _atmoForce = true;
        if (_atmoChoice == null || _atmoChoice.Choice == CustomAtmo)
            return;

        _atmoChoice.Choice = CustomAtmo;
        SaveFeature(_atmoChoice);
    }

    private static (float R, float G, float B) HueColor(float hue)
    {
        float h = ((hue % 360f) + 360f) % 360f / 60f;
        float f = h - MathF.Floor(h);
        float s = 0.85f;
        float p = 1f - s;
        float q = 1f - s * f;
        float t = 1f - s * (1f - f);
        return ((int)MathF.Floor(h) % 6) switch
        {
            0 => (1f, t, p),
            1 => (q, 1f, p),
            2 => (p, 1f, t),
            3 => (p, q, 1f),
            4 => (t, p, 1f),
            _ => (1f, p, q),
        };
    }

    private static AtmoLook AtmoTarget()
    {
        if (_atmoChoice == null)
            return AtmoLook.None;

        float fog = _atmoFog!.Value;
        float desat = _atmoDesat!.Value;
        int pick = _atmoChoice.Choice;
        if (pick == 0)
            return new AtmoLook(1f, 1f, 1f, 0f, fog, desat);

        float k = _atmoStrength!.Value;
        AtmoPreset preset = AtmoPresets[pick];
        (float r, float g, float b) = pick == CustomAtmo ? HueColor(_atmoHue!.Value) : (preset.R, preset.G, preset.B);
        return new AtmoLook(r, g, b, k, fog, Math.Clamp(desat + preset.Desat * k, 0f, 1f));
    }

    private static Rgb3 AtmoSwatch(int pick)
    {
        if (pick == CustomAtmo && _atmoHue != null)
        {
            var c = HueColor(_atmoHue.Value);
            return new Rgb3(c.R, c.G, c.B);
        }

        AtmoPreset p = AtmoPresets[Math.Clamp(pick, 0, AtmoPresets.Length - 1)];
        return pick == 0 ? new Rgb3(0.45f, 0.62f, 0.85f) : new Rgb3(p.R, p.G, p.B);
    }

    private readonly record struct Rgb3(float R, float G, float B);

    private static bool AtmoWanted() => !AtmoTarget().IsNone;

    private static void SetGradeOff(bool off)
    {
        if (_gradeOff == off)
            return;

        _gradeOff = off;
        _atmoForce = true;
    }

    private static void AtmoTick(float dt)
    {
        AtmoLook target = AtmoTarget();
        if (FeatureById.TryGetValue("post_process", out Feature? pp) && pp.Choice != 0 && _ppWithAtmo != !target.IsNone)
            ApplyPostProcess(pp.Choice);

        float t = dt <= 0f ? 1f : 1f - MathF.Exp(-AtmoBlendRate * dt);
        _atmoNow = AtmoLook.Lerp(_atmoNow, target, t);
        if (_atmoNow.Distance(target) < 0.002f)
            _atmoNow = target;

        bool rescan = Stopwatch.GetElapsedTime(_ppScanned).TotalSeconds >= AtmoScanGap;
        bool changed = _atmoForce || _atmoNow.Distance(_atmoApplied) > 0.0005f;
        if (!changed && !rescan)
            return;

        if (_atmoNow.IsNone && _ppTouched.Count == 0 && PostProcessOriginals.Count == 0 && !_atmoForce)
        {
            _atmoApplied = _atmoNow;
            return;
        }

        if (rescan || _ppVolumes.Count == 0)
            ScanVolumes();

        _atmoForce = false;
        _atmoApplied = _atmoNow;
        Dictionary<string, double[]> originals = PostProcessOriginals;

        foreach ((PostProcessVolume vol, string key) in _ppVolumes)
        {
            if (!vol.IsValid)
                continue;
            if (!changed && _ppTouched.Contains(key))
                continue;
            ApplyLook(ref vol.Settings, key, originals);
        }

        WorldInfo wi = Game.GetWorldInfo();
        if (wi != null && wi.IsValid && (changed || !_ppTouched.Contains(DefaultSettingsKey)))
            ApplyLook(ref wi.DefaultPostProcessSettings, DefaultSettingsKey, originals);
    }

    private static void ScanVolumes()
    {
        _ppScanned = Stopwatch.GetTimestamp();
        _ppVolumes.Clear();
        foreach (PostProcessVolume v in Game.FindObjects<PostProcessVolume>())
        {
            if (v != null && v.IsValid && !v.IsClassDefaultObject)
                _ppVolumes.Add((v, v.GetPathName(null)));
        }

        if (_ppTouched.Count > 4 * _ppVolumes.Count + 64)
            _ppTouched.Clear();
    }

    private static void ApplyLook(ref PPSettings st, string key, Dictionary<string, double[]> originals)
    {
        if (_atmoNow.IsNone && !_gradeOff)
        {
            if (originals.TryGetValue(key, out double[]? back))
            {
                Write(ref st, Unpack(back));
                originals.Remove(key);
            }

            _ppTouched.Remove(key);
            return;
        }

        if (!originals.TryGetValue(key, out double[]? packed))
        {
            packed = Pack(Read(ref st));
            originals[key] = packed;
        }

        LookFields o = Unpack(packed);
        if (_gradeOff)
        {
            o.Lut = 0;
            o.High = new FLinearColor { R = 1f, G = 1f, B = 1f, A = 1f };
            o.Mid = new FLinearColor { R = 0.5f, G = 0.5f, B = 0.5f, A = 1f };
            o.Shadow = new FLinearColor { R = 0f, G = 0f, B = 0f, A = 1f };
            o.Desat = 0f;
        }

        AtmoLook l = _atmoNow;
        float k = l.Strength;
        float avg = Math.Max(0.05f, (l.R + l.G + l.B) / 3f);

        LookFields n = o;
        n.Gradient = TintColor(o.Gradient, l, k);
        n.H1 = TintColor(o.H1, l, k);
        n.H2 = TintColor(o.H2, l, k);
        n.D1 = TintColor(o.D1, l, k);
        n.D2 = TintColor(o.D2, l, k);
        n.High = Gain(Neutral(o.High, k), l, k * 0.5f, avg);
        n.Mid = Gain(o.Mid, l, k * 0.2f, avg);
        n.Shadow = new FLinearColor { R = o.Shadow.R + l.R * k * 0.025f, G = o.Shadow.G + l.G * k * 0.025f, B = o.Shadow.B + l.B * k * 0.025f, A = o.Shadow.A };
        n.Desat = Math.Clamp(o.Desat + l.Desat, 0f, 1f);
        n.H1Den = o.H1Den * l.Fog;
        n.H2Den = o.H2Den * l.Fog;
        n.D1Den = o.D1Den * l.Fog;
        n.D2Den = o.D2Den * l.Fog;
        n.Haze = Math.Clamp(o.Haze * l.Fog, 0f, 1f);
        Write(ref st, n);
        _ppTouched.Add(key);
    }

    private static FColor TintColor(FColor o, AtmoLook l, float k)
    {
        float m = Math.Max(o.R, Math.Max(o.G, o.B));
        float top = Math.Max(l.R, Math.Max(l.G, l.B));
        float s = top <= 0f ? 0f : m / top;
        static byte Ch(float from, float to, float k) => (byte)Math.Clamp(MathF.Round(from + (to - from) * k), 0f, 255f);
        return new FColor { R = Ch(o.R, l.R * s, k), G = Ch(o.G, l.G * s, k), B = Ch(o.B, l.B * s, k), A = o.A };
    }

    private static FLinearColor Neutral(FLinearColor o, float k)
    {
        float m = (o.R + o.G + o.B) / 3f;
        return new FLinearColor { R = o.R + (m - o.R) * k, G = o.G + (m - o.G) * k, B = o.B + (m - o.B) * k, A = o.A };
    }

    private static FLinearColor Gain(FLinearColor o, AtmoLook l, float amount, float avg)
    {
        float G(float c) => Math.Clamp(1f + amount * (c / avg - 1f), 0.35f, 2.2f);
        return new FLinearColor { R = o.R * G(l.R), G = o.G * G(l.G), B = o.B * G(l.B), A = o.A };
    }

    private static void ResetAtmosphereCache()
    {
        _ppVolumes.Clear();
        _ppTouched.Clear();
        _atmoForce = true;
        _ppScanned = 0;
    }
}
