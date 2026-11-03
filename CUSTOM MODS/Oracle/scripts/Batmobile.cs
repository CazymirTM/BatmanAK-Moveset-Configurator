using System;
using System.Collections.Generic;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace CeZeY.OracleScript;

public partial class Oracle
{
    private const double BatmobileRefresh = 1.0;

    private static RVehicleBatmobileBase? _batmobile;
    private static readonly List<RVehicleWeapon> _batWeapons = new();
    private static RBatmobileCannon? _batCannon;
    private static long _batFound;
    private static int _batMaxHealth;

    private static RVehicleBatmobileBase? Batmobile()
    {
        long now = Stopwatch.GetTimestamp();
        if (_batmobile != null && _batmobile.IsValid && Stopwatch.GetElapsedTime(_batFound, now).TotalSeconds < BatmobileRefresh)
            return _batmobile;

        _batFound = now;
        RVehicleBatmobileBase? found = null;
        if (Game.GetPlayerController(0)?.Pawn is RVehicleBatmobileBase driving && driving.IsValid)
            found = driving;
        else
        {
            foreach (RVehicleBatmobileBase b in Game.FindObjects<RVehicleBatmobileBase>())
            {
                if (b != null && b.IsValid && !b.IsClassDefaultObject && !b.bDeleteMe && b.GetPathName(null).Contains("PersistentLevel", StringComparison.Ordinal))
                {
                    found = b;
                    break;
                }
            }
        }

        if (found == null || _batmobile == null || found.Ptr != _batmobile.Ptr)
            _batMaxHealth = 0;
        _batmobile = found;

        _batWeapons.Clear();
        _batCannon = null;
        if (found != null)
        {
            foreach (RVehicleWeapon w in Game.FindObjects<RVehicleWeapon>())
            {
                if (w == null || !w.IsValid || w.IsClassDefaultObject || w.Owner == null || w.Owner.Ptr != found.Ptr)
                    continue;
                _batWeapons.Add(w);
                if (w is RBatmobileCannon c)
                    _batCannon = c;
            }
        }

        return found;
    }

    private static void BatmobileArmourTick()
    {
        RVehicleBatmobileBase? b = Batmobile();
        if (b == null || b.Health <= 0)
            return;

        var armour = b.ArmourLevels;
        int bonus = armour.Count == 0 ? 0 : armour[Math.Clamp(b.CurrentArmourLevel, 0, armour.Count - 1)];
        _batMaxHealth = Math.Max(_batMaxHealth, Math.Max(b.Health, b.HealthMax + bonus));
        if (b.Health < _batMaxHealth)
            b.Health = _batMaxHealth;
    }

    private static void BatmobilePowerTick()
    {
        RVehicleBatmobileBase? b = Batmobile();
        if (b == null)
            return;

        var levels = b.MaxWeaponGeneratorPower;
        if (levels.Count == 0)
            return;

        float max = levels[Math.Clamp(b.CurrentGeneratorLevel, 0, levels.Count - 1)];
        if (b.WeaponGeneratorPower < max)
            b.WeaponGeneratorPower = max;
    }

    private static void BatmobileCooldownTick()
    {
        if (Batmobile() == null)
            return;

        foreach (RVehicleWeapon w in _batWeapons)
        {
            if (!w.IsValid || w is RBatmobileCannon)
                continue;
            if (w.CoolDown > 0f)
                w.CoolDown = 0f;
            if (w.CurrentRechargeTime > 0f)
                w.CurrentRechargeTime = 0f;
        }
    }

    private static void BatmobileAccuracyTick()
    {
        if (Batmobile() == null || _batCannon == null || !_batCannon.IsValid)
            return;

        if (_batCannon.CurrentInaccuracyTime > 0f)
            _batCannon.CurrentInaccuracyTime = 0f;
        if (_batCannon.CurrentAccuracyZeroToOne < 1f)
            _batCannon.CurrentAccuracyZeroToOne = 1f;
        if (!_batCannon.bPerfectShot)
            _batCannon.bPerfectShot = true;
    }
}
