using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace CeZeY.OracleScript;

public partial class Oracle
{
    private const float QuickFadeOut = 0.35f;
    private const float QuickFadeIn = 0.6f;
    private const float SlowFadeOut = 1.8f;
    private const float SlowFadeIn = 1.4f;
    private const float SettleTime = 1.0f;
    private const float TeleportGiveUp = 60f;
    private const float TeleportWatchdog = 90f;
    private const float TeleportStepGap = 0.5f;
    private const float TeleportVerify = 0.6f;
    private const float TeleportStep = 1000f;
    private const float TeleportMinHop = 1500f;
    private const float TeleportScan = 40000f;

    private const int TpIdle = 0;
    private const int TpFadeOut = 1;
    private const int TpTravel = 2;
    private const int TpSettle = 3;
    private const int TpFadeIn = 4;

    private static int _tpPhase;
    private static float _tpTimer;
    private static float _tpLeft;
    private static float _tpTotal;
    private static int _tpHops;
    private static int _tpFails;
    private static float _tpTargetLoaded;
    private static string? _tpLastCell;
    private static int _tpIdleSteps;
    private static readonly List<Vector3> _tpBad = new();
    private static Vector3 _tpTarget;
    private static Vector3 _tpOrigin;
    private static Vector3 _tpSafe;
    private static Vector3 _tpLanded;
    private static bool _tpChecking;
    private static bool _tpFinal;
    private static nint _tpActor;
    private static string _tpResult = "";
    private static bool _tpOk;
    private static float _blackout;

    private static bool TeleportBusy => _tpPhase != TpIdle;

    private static bool _tpForceSlow;
    private static string _tpToastTitle = "TELEPORT";

    private static bool SlowFade => _tpForceSlow || (FeatureById.TryGetValue("teleport_fade", out Feature? f) && f.Choice == 1);

    private static float FadeOutTime => SlowFade ? SlowFadeOut : QuickFadeOut;

    private static float FadeInTime => SlowFade ? SlowFadeIn : QuickFadeIn;

    private const string CellFile = "cells.cache";
    private const float CellScanGap = 2f;
    private const float AdjacentRange = 13000f;
    private const float WaterWait = 3f;

    private readonly record struct Cell(string Name, Vector3 Min, Vector3 Max)
    {
        public bool Contains(Vector3 p) => p.X >= Min.X && p.X <= Max.X && p.Y >= Min.Y && p.Y <= Max.Y && p.Z >= Min.Z - 5000f && p.Z <= Max.Z + 5000f;

        public float Size => (Max.X - Min.X) * (Max.Y - Min.Y);

        public bool Touches(Cell o) =>
            Min.X <= o.Max.X + 1000f && o.Min.X <= Max.X + 1000f && Min.Y <= o.Max.Y + 1000f && o.Min.Y <= Max.Y + 1000f;

        public float DistanceXY(Vector3 p)
        {
            float dx = Math.Max(0f, Math.Max(Min.X - p.X, p.X - Max.X));
            float dy = Math.Max(0f, Math.Max(Min.Y - p.Y, p.Y - Max.Y));
            return MathF.Sqrt(dx * dx + dy * dy);
        }
    }

    private static readonly Dictionary<string, Cell> _cells = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _tpTried = new(StringComparer.OrdinalIgnoreCase);
    private static bool _cellsLoaded;
    private static float _cellScan;

    private const int VolumeListOffset = 3892;
    private const int VolumeStride = 112;
    private const int VolumeNameAt = 8;
    private const int VolumeFlagsAt = 36;
    private const int VolumeBoundsAt = 44;
    private const int VolumeLoaded = 1;
    private const int VolumeVisible = 2;
    private const int VolumeOverworld = 16;

    private static readonly Dictionary<string, int> _cellFlags = new(StringComparer.OrdinalIgnoreCase);
    private static string[] _volNames = Array.Empty<string>();
    private static nint _volData;
    private static bool _volSeeded;
    private static bool _volBroken;
    private static bool _volLive;

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, nint size, out nint read);

    private static bool IsCityCell(string name) => name.StartsWith("City", StringComparison.OrdinalIgnoreCase);

    private static bool ReadMem(nint at, byte[] buffer)
    {
        return at != 0 && ReadProcessMemory(GetCurrentProcess(), at, buffer, buffer.Length, out nint got) && got == buffer.Length;
    }

    private static string ReadFString(nint at)
    {
        var head = new byte[16];
        if (!ReadMem(at, head))
            return "";

        int count = BitConverter.ToInt32(head, 8);
        if (count <= 1 || count > 128)
            return "";

        var chars = new byte[(count - 1) * 2];
        return ReadMem((nint)BitConverter.ToInt64(head, 0), chars) ? Encoding.Unicode.GetString(chars) : "";
    }

    private static bool ReadVolumes(bool seed)
    {
        _volLive = false;
        if (_volBroken)
            return false;

        RGameInfo gi = Game.GetGameInfo();
        if (gi == null || !gi.IsValid)
            return false;

        var head = new byte[16];
        if (!ReadMem(gi.Ptr + VolumeListOffset, head))
            return false;

        nint data = (nint)BitConverter.ToInt64(head, 0);
        int count = BitConverter.ToInt32(head, 8);
        if (data == 0 || count <= 0 || count > 4096)
            return false;

        var raw = new byte[count * VolumeStride];
        if (!ReadMem(data, raw))
            return false;

        if (data != _volData || _volNames.Length != count)
        {
            var names = new string[count];
            int named = 0;
            for (int i = 0; i < count; i++)
            {
                names[i] = ReadFString(data + i * VolumeStride + VolumeNameAt);
                if (names[i].Length > 0 && char.IsLetter(names[i][0]))
                    named++;
            }

            if (named < count * 3 / 4)
            {
                _volBroken = true;
                Log("Teleport: the game's level list looks different than expected, using cells.cache only.");
                return false;
            }

            _volData = data;
            _volNames = names;
        }

        _cellFlags.Clear();
        int seeded = 0;
        for (int i = 0; i < count; i++)
        {
            string name = _volNames[i];
            if (name.Length == 0)
                continue;

            int at = i * VolumeStride;
            int flags = BitConverter.ToInt32(raw, at + VolumeFlagsAt);
            _cellFlags[name] = flags;
            if (!seed || (flags & VolumeOverworld) == 0 || !IsCityCell(name))
                continue;

            float B(int k) => MathF.Round(BitConverter.ToSingle(raw, at + VolumeBoundsAt + k * 4));
            var cell = new Cell(name, new Vector3(B(0), B(1), B(2)), new Vector3(B(3), B(4), B(5)));
            if (cell.Max.X <= cell.Min.X || cell.Max.Y <= cell.Min.Y)
                continue;
            if (_cells.TryGetValue(name, out Cell old) && old == cell)
                continue;

            _cells[name] = cell;
            seeded++;
        }

        _volLive = true;
        if (seed && seeded > 0)
        {
            SaveCells();
            Log("Teleport: updated " + seeded + " city cells from the game's level list (" + _cells.Count + " known).");
        }

        return true;
    }

    private static bool CellReady(Vector3 at)
    {
        if (!_volLive)
            return true;

        string? name = CachedCellAt(at);
        if (name == null || !_cellFlags.TryGetValue(name, out int flags))
            return true;

        return (flags & (VolumeLoaded | VolumeVisible)) == (VolumeLoaded | VolumeVisible);
    }

    private static void LoadCells()
    {
        _cellsLoaded = true;
        try
        {
            string path = Path.Combine(_modDir, CellFile);
            if (!File.Exists(path))
                return;

            foreach (string line in File.ReadAllLines(path))
            {
                string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length != 7)
                    continue;

                if (!IsCityCell(p[0]))
                    continue;

                float F(int i) => float.Parse(p[i], CultureInfo.InvariantCulture);
                _cells[p[0]] = new Cell(p[0], new Vector3(F(1), F(2), F(3)), new Vector3(F(4), F(5), F(6)));
            }
        }
        catch (Exception ex)
        {
            Log("Could not read " + CellFile + ": " + ex.Message);
        }
    }

    private static void SaveCells()
    {
        try
        {
            var lines = new List<string>();
            foreach (Cell c in _cells.Values)
            {
                lines.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1:0} {2:0} {3:0} {4:0} {5:0} {6:0}",
                    c.Name, c.Min.X, c.Min.Y, c.Min.Z, c.Max.X, c.Max.Y, c.Max.Z));
            }

            lines.Sort(StringComparer.OrdinalIgnoreCase);
            File.WriteAllLines(Path.Combine(_modDir, CellFile), lines);
        }
        catch (Exception ex)
        {
            Log("Could not write " + CellFile + ": " + ex.Message);
        }
    }

    private static void ScanCells(float dt)
    {
        if (!_cellsLoaded)
            LoadCells();

        _cellScan -= dt;
        if (_cellScan > 0f)
            return;

        _cellScan = CellScanGap;
        if (!_volSeeded && !_volBroken)
            _volSeeded = ReadVolumes(true);

        bool changed = false;
        foreach (RLevelVolumeOW v in Game.FindObjects<RLevelVolumeOW>())
        {
            if (v == null || !v.IsValid || v.IsClassDefaultObject)
                continue;

            string name = v.Level.ToString();
            if (name.Length == 0 || name == "None" || name.Contains(' ') || !IsCityCell(name) || _cells.ContainsKey(name))
                continue;

            v.GetComponentsBoundingBox(out GameObject.FBox box);
            if (box.IsValid == 0)
                continue;

            var cell = new Cell(name, box.Min, box.Max);

            _cells[name] = cell;
            changed = true;
        }

        if (changed)
            SaveCells();
    }

    private static string? CachedCellAt(Vector3 at)
    {
        Cell? best = null;
        foreach (Cell c in _cells.Values)
        {
            if (c.Contains(at) && (best == null || c.Size < best.Value.Size))
                best = c;
        }

        return best?.Name;
    }

    private static void StartTeleport()
    {
        if (TeleportBusy || CharacterBusy)
            throw new InvalidOperationException("already teleporting");
        if (!_ready || !_applied)
            throw new InvalidOperationException("the game is still loading");

        RPersistentData pd = Game.GetPersistentData();
        if (pd == null || !pd.IsValid || !pd.GetCustomWaypointEnabled())
            throw new InvalidOperationException("no waypoint is set, pick one on the map first");

        Pawn? mover = Game.GetPlayerController(0)?.Pawn;
        if (mover == null || !mover.IsValid)
            throw new InvalidOperationException("nothing to move");
        if (mover is not RPawnPlayer)
            throw new InvalidOperationException("get out of the Batmobile first");

        _tpTarget = pd.GetCustomWaypoint(false);
        _tpActor = mover.Ptr;
        _tpOrigin = mover.Location;
        _tpSafe = _tpOrigin;
        _tpHops = 0;
        _tpTried.Clear();
        _tpTargetLoaded = 0f;
        _tpLastCell = null;
        _tpIdleSteps = 0;
        _tpBad.Clear();
        _tpChecking = false;
        _tpFinal = false;
        _tpLeft = TeleportGiveUp;
        _tpTotal = 0f;
        _tpTimer = 0f;
        _tpResult = "";
        _tpReturnResult = "";
        _tpToastTitle = "TELEPORT";
        _tpPhase = TpFadeOut;
        CloseMenu();
        Log("Teleport to " + Fmt(_tpTarget) + " from " + Fmt(_tpOrigin) + ".");
    }

    private static void BeginReturn(Pawn mover, Vector3 target, string toastTitle, string toastResult)
    {
        _tpTarget = target;
        _tpActor = mover.Ptr;
        _tpOrigin = mover.Location;
        _tpSafe = _tpOrigin;
        _tpHops = 0;
        _tpTried.Clear();
        _tpTargetLoaded = 0f;
        _tpLastCell = null;
        _tpIdleSteps = 0;
        _tpBad.Clear();
        _tpChecking = false;
        _tpFinal = false;
        _tpLeft = TeleportGiveUp;
        _tpTotal = 0f;
        _tpTimer = TeleportStepGap;
        _tpResult = "";
        _tpToastTitle = toastTitle;
        _tpReturnResult = toastResult;
        _blackout = 1f;
        _tpPhase = TpTravel;
        Log("Returning the new character to " + Fmt(target) + ".");
    }

    private static string _tpReturnResult = "";

    private static string Fmt(Vector3 v) =>
        string.Format(CultureInfo.InvariantCulture, "{0:0}, {1:0}, {2:0}", v.X, v.Y, v.Z);

    private static void TeleportTick(float dt)
    {
        ScanCells(dt);
        if (_tpPhase == TpIdle)
            return;

        Pawn? mover = Game.GetPlayerController(0)?.Pawn;
        bool valid = mover != null && mover.IsValid && mover.Ptr == _tpActor;
        if (!valid && _tpPhase != TpFadeIn)
        {
            Log("Teleport cancelled: the player changed.");
            _tpResult = "";
            _tpPhase = TpFadeIn;
            _tpTimer = 0f;
        }

        _tpTimer += dt;
        _tpTotal += dt;
        if (_tpTotal > TeleportWatchdog && _tpPhase != TpFadeIn)
        {
            if (valid)
                Place(mover!, _tpSafe);
            Log("Teleport watchdog: stopped after " + (int)_tpTotal + " s in phase " + _tpPhase + ".");
            _tpResult = "AS CLOSE AS POSSIBLE";
            _tpOk = false;
            _tpPhase = TpFadeIn;
            _tpTimer = 0f;
        }

        switch (_tpPhase)
        {
            case TpFadeOut:
                _blackout = SlowFade ? EaseIn(_tpTimer / FadeOutTime) : Smooth(_tpTimer / FadeOutTime);
                if (_tpTimer >= FadeOutTime)
                {
                    _blackout = 1f;
                    _tpPhase = TpTravel;
                    _tpTimer = TeleportStepGap;
                    _tpOrigin = mover!.Location;
                    _tpSafe = _tpOrigin;
                }
                break;

            case TpTravel:
                _blackout = 1f;
                _tpLeft -= dt;
                if (_tpTimer >= (_tpChecking ? TeleportVerify : TeleportStepGap))
                {
                    _tpTimer = 0f;
                    TravelStep(mover!);
                }
                break;

            case TpSettle:
                _blackout = 1f;
                if (_tpTimer >= SettleTime)
                {
                    _tpPhase = TpFadeIn;
                    _tpTimer = 0f;
                }
                break;

            case TpFadeIn:
                _blackout = 1f - Smooth(_tpTimer / FadeInTime);
                if (_tpTimer >= FadeInTime)
                {
                    _blackout = 0f;
                    _tpPhase = TpIdle;
                    if (_tpReturnResult.Length > 0)
                        ShowToast(_tpToastTitle, _tpReturnResult, true);
                    else if (_tpResult.Length > 0)
                        ShowToast(_tpToastTitle, _tpResult, _tpOk);
                    _tpForceSlow = false;
                    _tpToastTitle = "TELEPORT";
                    _tpReturnResult = "";
                }
                break;
        }
    }

    private static float Smooth(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float EaseIn(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t;
    }

    private static void Finish(string result, bool ok)
    {
        _tpResult = result;
        _tpOk = ok;
        _tpPhase = ok ? TpSettle : TpFadeIn;
        _tpTimer = 0f;
    }

    private static void TravelStep(Pawn mover)
    {
        if (_tpChecking)
        {
            _tpChecking = false;
            if (mover.Location.Z < _tpLanded.Z - 150f)
            {
                Place(mover, _tpSafe);
                _tpFails++;
                _tpBad.Add(_tpLanded);
                Log("Teleport: ground at " + Fmt(_tpLanded) + " wasn't solid yet, back to " + Fmt(_tpSafe) + ".");
            }
            else
            {
                _tpSafe = mover.Location;
                bool switched = MakeCellCurrent(mover, _tpSafe);
                if (_tpFinal)
                {
                    Log("Teleported onto " + Fmt(_tpLanded) + " after " + _tpHops + " hops.");
                    Finish("DONE", true);
                    return;
                }

                if (switched)
                    return;

                float left = Vector2.Distance(new Vector2(_tpSafe.X, _tpSafe.Y), new Vector2(_tpTarget.X, _tpTarget.Y));
                if (left <= 2500f && !GroundAt(mover, _tpTarget.X, _tpTarget.Y, out _))
                {
                    Log("Teleport: waypoint has no ground, stopped on the nearest ground " + (int)left + " units away.");
                    Finish("NEAREST GROUND", true);
                    return;
                }
            }
        }

        if (_tpLeft <= 0f || _tpIdleSteps >= 40)
        {
            bool moved = Vector3.DistanceSquared(_tpSafe, _tpOrigin) > 100f * 100f;
            Log("Teleport: gave up, " + (moved ? "stopped as close as possible." : "no route to the waypoint."));
            Finish(moved ? "AS CLOSE AS POSSIBLE" : "NO GROUND THERE", false);
            return;
        }

        float lift = mover.GetCollisionHeight() + 40f;
        ReadVolumes(false);
        bool targetReady = CellReady(_tpTarget);
        if (GroundAt(mover, _tpTarget.X, _tpTarget.Y, out Vector3 hit) && CellReady(hit))
        {
            MakeCellCurrent(mover, hit);
            Land(mover, hit + new Vector3(0f, 0f, lift), true);
            return;
        }

        if (MakeCellCurrent(mover, _tpTarget))
            return;

        if (targetReady && TargetCellLoaded())
        {
            _tpTargetLoaded += TeleportStepGap;
            if (_tpTargetLoaded >= WaterWait && NearestGround(mover, out Vector3 shore))
            {
                Log("Teleport: waypoint is over water or a gap, landing on the nearest ground at " + Fmt(shore) + ".");
                Land(mover, shore + new Vector3(0f, 0f, lift), true);
                return;
            }
        }

        Vector3 from = mover.Location;
        float dist = Vector2.Distance(new Vector2(from.X, from.Y), new Vector2(_tpTarget.X, _tpTarget.Y));
        if (dist <= 2500f && !_tpChecking)
        {
            _tpTargetLoaded += TeleportStepGap;
            if (_tpTargetLoaded >= 1f)
            {
                Log("Teleport: already on the nearest ground, " + (int)dist + " units from the waypoint.");
                Finish(_tpHops > 0 ? "NEAREST GROUND" : "ALREADY THERE", true);
                return;
            }
        }
        for (float d = dist - TeleportStep; d >= TeleportMinHop; d -= TeleportStep)
        {
            float t = d / dist;
            float x = from.X + (_tpTarget.X - from.X) * t;
            float y = from.Y + (_tpTarget.Y - from.Y) * t;
            if (!GroundAt(mover, x, y, out Vector3 step) || IsBad(step) || !CellReady(step))
                continue;

            _tpHops++;
            _tpTried.Clear();
            _tpLastCell = null;
            Log("Teleport: hop " + _tpHops + " to " + Fmt(step) + ", " + (int)(dist - d) + " units from the waypoint.");
            Land(mover, step + new Vector3(0f, 0f, lift), false);
            return;
        }

        float stayFrom = dist - TeleportMinHop;
        if (targetReady && stayFrom > TeleportMinHop && ClosestGroundToTarget(mover, stayFrom, out Vector3 around))
        {
            _tpHops++;
            _tpTried.Clear();
            _tpLastCell = null;
            Log("Teleport: hop " + _tpHops + " around a gap to " + Fmt(around) + ".");
            Land(mover, around + new Vector3(0f, 0f, lift), false);
            return;
        }

        if (WalkCells(mover))
            _tpIdleSteps = 0;
        else
            _tpIdleSteps++;
    }

    private static bool IsBad(Vector3 p)
    {
        foreach (Vector3 b in _tpBad)
        {
            if (Vector2.DistanceSquared(new Vector2(b.X, b.Y), new Vector2(p.X, p.Y)) < 600f * 600f)
                return true;
        }

        return false;
    }

    private static bool NearPlayer(Pawn mover, Vector3 at)
    {
        string? name = CachedCellAt(at);
        if (name != null)
            return _cells[name].DistanceXY(mover.Location) <= AdjacentRange;
        return Vector2.Distance(new Vector2(at.X, at.Y), new Vector2(mover.Location.X, mover.Location.Y)) <= AdjacentRange + 12288f;
    }

    private static bool ClosestGroundToTarget(Pawn mover, float maxRadius, out Vector3 best)
    {
        best = default;
        for (float r = 1500f; r <= maxRadius; r += 1500f)
        {
            int steps = Math.Clamp((int)(r / 400f), 12, 160);
            float bestD = float.MaxValue;
            bool found = false;
            for (int i = 0; i < steps; i++)
            {
                float a = MathF.Tau * i / steps;
                if (!GroundAt(mover, _tpTarget.X + MathF.Cos(a) * r, _tpTarget.Y + MathF.Sin(a) * r, out Vector3 g) || IsBad(g) || !CellReady(g))
                    continue;
                float d = Vector2.Distance(new Vector2(g.X, g.Y), new Vector2(_tpTarget.X, _tpTarget.Y));
                if (d < bestD)
                {
                    bestD = d;
                    best = g;
                    found = true;
                }
            }

            if (found)
                return true;
        }

        return false;
    }

    private static bool TargetCellLoaded()
    {
        foreach (RLevelVolumeOW v in Game.FindObjects<RLevelVolumeOW>())
        {
            if (v != null && v.IsValid && !v.IsClassDefaultObject && v.EncompassesPoint(_tpTarget))
                return true;
        }

        return false;
    }

    private static bool NearestGround(Pawn mover, out Vector3 ground)
    {
        for (float r = 500f; r <= 9000f; r += 500f)
        {
            int steps = Math.Max(8, (int)(r / 250f));
            for (int i = 0; i < steps; i++)
            {
                float a = MathF.Tau * i / steps;
                if (GroundAt(mover, _tpTarget.X + MathF.Cos(a) * r, _tpTarget.Y + MathF.Sin(a) * r, out ground) && !IsBad(ground) && CellReady(ground))
                    return true;
            }
        }

        ground = default;
        return false;
    }

    private static bool MakeCellCurrent(Pawn mover, Vector3 at)
    {
        RGameRI ri = Game.GetGameRI();
        if (ri == null || !ri.IsValid)
            return false;

        foreach (RLevelVolumeOW v in Game.FindObjects<RLevelVolumeOW>())
        {
            if (v == null || !v.IsValid || v.IsClassDefaultObject || !v.EncompassesPoint(at))
                continue;

            string level = v.Level.ToString();
            if (level.Length == 0 || level == "None" || ri.LastStreamingLevel.ToString() == level)
                return false;

            if (v.Level.ToString() != level || !NearPlayer(mover, at))
                return false;
            ri.SetCurrentLevel(v.Level, mover, false);
            _tpIdleSteps = 0;
            _tpLastCell = level;
            Log("Teleport: streaming city cell " + level + ".");
            return true;
        }

        string? cached = CachedCellAt(at);
        if (cached == null || ri.LastStreamingLevel.ToString().Equals(cached, StringComparison.OrdinalIgnoreCase))
            return false;
        if (_cells[cached].DistanceXY(mover.Location) > AdjacentRange)
            return false;

        return SwitchCell(ri, mover, cached);
    }

    private static bool SwitchCell(RGameRI ri, Pawn mover, string name)
    {
        if (!_tpTried.Add(name))
            return false;

        if (_cells[name].DistanceXY(mover.Location) > AdjacentRange)
            return false;
        ri.SetCurrentLevel(new FName(name), mover, false);
        _tpIdleSteps = 0;
        _tpLastCell = name;
        Log("Teleport: streaming city cell " + name + " (remembered).");
        return true;
    }

    private static bool WalkCells(Pawn mover)
    {
        RGameRI ri = Game.GetGameRI();
        if (ri == null || !ri.IsValid)
            return false;

        string current = ri.LastStreamingLevel.ToString();
        Vector3 from = mover.Location;
        float dist = Vector3.Distance(from, _tpTarget);
        for (float d = 0f; d <= dist; d += 1500f)
        {
            Vector3 p = Vector3.Lerp(from, _tpTarget, dist <= 0f ? 1f : d / dist);
            string? name = CachedCellAt(p);
            if (name == null || name.Equals(current, StringComparison.OrdinalIgnoreCase) || _tpTried.Contains(name))
                continue;
            if (_cells[name].DistanceXY(from) > AdjacentRange)
                break;

            return SwitchCell(ri, mover, name);
        }

        return false;
    }

    private static bool GroundAt(Pawn mover, float x, float y, out Vector3 hit)
    {
        Vector3 top = new(x, y, Math.Max(_tpTarget.Z, mover.Location.Z) + TeleportScan);
        Vector3 bottom = new(x, y, Math.Min(_tpTarget.Z, mover.Location.Z) - TeleportScan);
        Actor? hitActor = mover.Trace(out hit, out _, bottom, top, Component.ECollisionFilter.COF_TraceWorld, Vector3.Zero, out _, 0);
        return hitActor != null && hit != Vector3.Zero;
    }

    private static void Land(Pawn mover, Vector3 at, bool final)
    {
        Place(mover, at);
        _tpIdleSteps = 0;
        _tpLanded = at;
        _tpChecking = true;
        _tpFinal = final;
    }

    private static void Place(Pawn mover, Vector3 at)
    {
        mover.SetLocation(at);
        mover.Velocity = Vector3.Zero;
    }
}
