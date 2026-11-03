using System;
using System.Globalization;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;
using BmSDK.GFxUI;
using FLinearColor = BmSDK.GameObject.FLinearColor;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace CeZeY.OracleScript;

public partial class Oracle
{
    private const float UnitsPerMeter = 100f;
    private const float BreakFallMargin = 600f;
    private const float FallHurtShare = 0.5f;
    private const float FallMaxHurt = 0.8f;
    private const double FallGiveUp = 6.0;
    private const string XpRootTail = ".LeftSideParent.HealthAndXPMaster.Health";
    private const string StaminaLabel = "STAMINA";
    private const float SprintDrain = 0.09f;
    private const float GlideDrain = 0.055f;
    private const float JumpCost = 0.07f;
    private const float GrappleCost = 0.1f;
    private const float StrikeCost = 0.045f;
    private const float FightDrain = 0.025f;
    private const float FightRecoverShare = 0.2f;
    private const float EvadeCost = 0.07f;
    private const float StaminaRecover = 0.14f;
    private const float RecoverDelay = 0.9f;
    private const float ExhaustedUntil = 0.35f;

    private static Feature? _fallDamage;
    private static Feature? _fallHeight;
    private static Feature? _stamina;
    private static Feature? _staminaDrain;
    private static Feature? _staminaRegen;

    private static nint _fallPawn;
    private static bool _falling;
    private static float _fallTop;
    private static long _fallOff;
    private static nint _killPawn;
    private static int _killStep;
    private static long _killTried;

    private static float _stam = 1f;
    private static bool _exhausted;
    private static long _stamTick;
    private static float _sinceDrain = 10f;
    private static nint _stamPawn;
    private static string _stamPcState = "";
    private static string _stamCamState = "";
    private static Actor.EPhysics _stamPhys;
    private static bool _setNoRun;
    private static bool _setNoGlide;
    private static bool _setNoJump;
    private static RGrappleGun? _setNoGrapple;
    private static long _grappleCharged;

    private static RGFxMovieModularHud? _stamHud;
    private static long _stamHudCheck;
    private static float _shownStam = -1f;
    private static int _stamTint = -1;
    private static float _gameXp = -1f;
    private static string _xpLabel = "";
    private static bool _staminaWriting;

    private static string Meters(float v) => v.ToString("0", CultureInfo.InvariantCulture) + " M";

    private static void BuildFun()
    {
        Group("FALLING");
        _fallDamage = Fast(Toggle("fall_damage", "FALL DAMAGE", "Long falls hurt Batman and falls from DEADLY HEIGHT kill him. His cape no longer slows him down at the last second. Gliding and grappling stay safe.",
            _ => ResetFall()), FallTick);
        _fallHeight = Slider("fall_height", "DEADLY HEIGHT", "Falling this far or more is fatal. Falls over half this height take away health, shorter falls are safe.",
            20f, 6f, 80f, 1f, 12f, Meters, _ => { });
        _fallHeight.Pref = true;

        Group("STAMINA");
        _stamina = Fast(Toggle("stamina", "STAMINA", "Turns the XP bar into a stamina bar. Sprinting, jumping, grappling, gliding, striking and dodging tire Batman out. Empty = no running, jumping, gliding, grappling, attacking or dodging until he gets his breath back. Countering always works.",
            on => { if (!on) StaminaOff(); }), StaminaTick);
        _staminaDrain = Slider("stamina_drain", "STAMINA DRAIN", "How fast stamina runs out.", 1f, 0.25f, 3f, 0.25f, 2f, Times, _ => { });
        _staminaDrain.Pref = true;
        _staminaRegen = Slider("stamina_regen", "STAMINA RECOVERY", "How fast stamina comes back while Batman walks or stands still.", 1f, 0.25f, 3f, 0.25f, 2f, Times, _ => { });
        _staminaRegen.Pref = true;
    }

    private static bool GodModeOn => FeatureById.TryGetValue("god_mode", out Feature? g) && g.On;

    private static void ResetFall()
    {
        _fallOff = 0;
        _falling = false;
        _fallTop = 0f;
    }

    private static bool PawnDead(RPawnPlayer p)
    {
        if (p.Health <= 0 || p.bDeleteMe)
            return true;
        string state = p.GetStateName().ToString() ?? "";
        return state.Contains("Dying", StringComparison.OrdinalIgnoreCase) || state.Contains("Dead", StringComparison.OrdinalIgnoreCase);
    }

    private static void KillTick()
    {
        if (_killPawn == 0)
            return;

        RPlayerController pc = Game.GetPlayerController(0);
        if (pc == null || !pc.IsValid || pc.Pawn is not RPawnPlayer p || !p.IsValid || p.Ptr != _killPawn || PawnDead(p))
        {
            _killPawn = 0;
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (_killStep > 0 && Stopwatch.GetElapsedTime(_killTried, now).TotalSeconds < 0.15)
            return;
        _killTried = now;

        Class fell = RDmgType_Fell.StaticClass();
        Vector3 at = p.Location;
        try
        {
            switch (_killStep++)
            {
                case 0:
                case 2:
                    p.Health = 0;
                    p.Died(pc, fell, at);
                    break;
                case 1:
                    Cheats().KillMe();
                    break;
                default:
                    Log("FALL DAMAGE: the game refused to kill Batman here.");
                    _killPawn = 0;
                    break;
            }
        }
        catch (Exception ex)
        {
            LogThrottled("Fall kill step failed: " + ex.Message);
        }
    }

    private static void FallTick()
    {
        KillTick();

        if (Game.GetPlayerController(0)?.Pawn is not RPawnPlayer p || !p.IsValid)
        {
            ResetFall();
            return;
        }

        if (p.Ptr != _fallPawn)
        {
            _fallPawn = p.Ptr;
            ResetFall();
        }

        bool free = p.Physics == Actor.EPhysics.PHYS_Falling && !p.bGliding;
        if (!free)
        {
            if (!_falling)
                return;

            if (p.Physics == Actor.EPhysics.PHYS_Walking)
            {
                float fell = _fallTop - p.Location.Z;
                ResetFall();
                if (_ready && _applied)
                    FallLanded(p, fell);
                return;
            }

            long now = Stopwatch.GetTimestamp();
            if (_fallOff == 0)
                _fallOff = now;
            string pcState = Game.GetPlayerController(0)?.GetStateName().ToString() ?? "";
            bool saved = p.bGliding || pcState.Contains("Grapple", StringComparison.OrdinalIgnoreCase) || pcState.Contains("Glide", StringComparison.OrdinalIgnoreCase)
                || pcState.Contains("Ledge", StringComparison.OrdinalIgnoreCase) || pcState.Contains("Hang", StringComparison.OrdinalIgnoreCase)
                || p.Physics == Actor.EPhysics.PHYS_Swimming || p.Physics == Actor.EPhysics.PHYS_None
                || Stopwatch.GetElapsedTime(_fallOff, now).TotalSeconds > FallGiveUp;
            if (saved)
                ResetFall();
            return;
        }

        _fallOff = 0;

        float z = p.Location.Z;
        if (!_falling)
        {
            _falling = true;
            _fallTop = z;
        }
        else if (z > _fallTop)
            _fallTop = z;
    }

    private static bool FallMoveRedirect(RPlayerController self, RSpecialMoveConfig sm)
    {
        if (_fallDamage == null || !_fallDamage.On || !_ready || !_applied || sm == null || !_falling)
            return false;
        if (self.Pawn is not RPawnPlayer p || !p.IsValid || p.Ptr != _fallPawn)
            return false;

        float deadly = (_fallHeight?.Value ?? 20f) * UnitsPerMeter;
        float fell = _fallTop - p.Location.Z;

        if (sm is RSpecialMoveConfig_CapeBreakFall)
            return fell + BreakFallMargin >= deadly * FallHurtShare;

        if (sm is not RSpecialMoveConfig_Land)
            return false;

        ResetFall();
        return FallLanded(p, fell);
    }

    private static bool FallLanded(RPawnPlayer p, float fell)
    {
        float deadly = (_fallHeight?.Value ?? 20f) * UnitsPerMeter;
        if (fell >= deadly && !GodModeOn)
        {
            Log("FALL DAMAGE: fell " + (fell / UnitsPerMeter).ToString("0.0", CultureInfo.InvariantCulture) + " m, fatal.");
            _killPawn = p.Ptr;
            _killStep = 0;
            KillTick();
            return true;
        }

        float hurtFrom = deadly * FallHurtShare;
        if (fell > hurtFrom && !GodModeOn)
        {
            float share = Math.Clamp((fell - hurtFrom) / MathF.Max(1f, deadly - hurtFrom), 0f, 1f);
            int damage = (int)MathF.Ceiling(p.HealthMax * FallMaxHurt * share);
            if (damage > 0 && p.Health > 0)
            {
                p.Health = Math.Max(1, p.Health - damage);
                Log("FALL DAMAGE: fell " + (fell / UnitsPerMeter).ToString("0.0", CultureInfo.InvariantCulture) + " m, -" + damage + " health.");
            }
        }

        return false;
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.TriggerSpecialMove))]
    private static void TriggerSpecialMoveRedirect(RPlayerController self, RSpecialMoveConfig SpecMove, REnvironmentCheckTicker.FEnvironmentSpecialMoveLocator Loc, bool ForceReplicateToClient)
    {
        bool skip = false;
        try
        {
            skip = FallMoveRedirect(self, SpecMove);
        }
        catch (Exception ex)
        {
            LogThrottled("Fall check failed: " + ex.Message);
        }

        if (!skip)
            self.TriggerSpecialMove(SpecMove, Loc, ForceReplicateToClient);
    }

    private static bool StaminaActive => _stamina != null && _stamina.On && _ready && _applied;

    private static bool TooTired => StaminaActive && _exhausted;

    private static void Spend(float cost)
    {
        if (!StaminaActive)
            return;
        _stam = Math.Clamp(_stam - cost * (_staminaDrain?.Value ?? 1f), 0f, 1f);
        _sinceDrain = 0f;
        if (_stam <= 0f && !_exhausted)
            SetExhausted(true);
    }

    private static void SetExhausted(bool on)
    {
        _exhausted = on;
        Log(on ? "STAMINA: exhausted." : "STAMINA: recovered.");
        if (on)
        {
            RPlayerController pc = Game.GetPlayerController(0);
            if (pc != null && pc.IsValid && pc.Pawn is RPawnPlayer p && p.IsValid && p.bGliding)
                pc.CancelCapeGlide();
        }
    }

    private static void StaminaTick()
    {
        long now = Stopwatch.GetTimestamp();
        float dt = _stamTick == 0 ? 0f : Math.Clamp((float)Stopwatch.GetElapsedTime(_stamTick, now).TotalSeconds, 0f, 0.25f);
        _stamTick = now;

        RPlayerController pc = Game.GetPlayerController(0);
        if (pc == null || !pc.IsValid || pc.Pawn is not RPawnPlayer p || !p.IsValid)
            return;

        if (p.Ptr != _stamPawn)
        {
            _stamPawn = p.Ptr;
            _stam = 1f;
            _exhausted = false;
            _stamPhys = p.Physics;
        }

        string pcState = pc.GetStateName().ToString() ?? "";
        string camState = pc.PlayerCamera is R3rdPersonCamera cam && cam.IsValid ? CamStateName(cam) : "";
        Vector3 v = p.Velocity;
        float hspd = MathF.Sqrt(v.X * v.X + v.Y * v.Y);

        bool grapple = pcState.Contains("Grapple", StringComparison.OrdinalIgnoreCase) || camState.Contains("Grapple", StringComparison.OrdinalIgnoreCase);
        bool wasGrapple = _stamPcState.Contains("Grapple", StringComparison.OrdinalIgnoreCase) || _stamCamState.Contains("Grapple", StringComparison.OrdinalIgnoreCase);
        if (grapple && !wasGrapple && Stopwatch.GetElapsedTime(_grappleCharged, now).TotalSeconds > 2.0)
            Spend(GrappleCost);

        if (p.Physics == Actor.EPhysics.PHYS_Falling && _stamPhys == Actor.EPhysics.PHYS_Walking && v.Z > 50f && !p.bGliding)
            Spend(JumpCost);

        bool sprint = pcState == "RunningMode" && hspd > 300f;
        bool fighting = camState.Contains("Combat", StringComparison.OrdinalIgnoreCase) || pcState.StartsWith("CombatMove", StringComparison.OrdinalIgnoreCase);
        float drain = (sprint ? SprintDrain : 0f) + (p.bGliding ? GlideDrain : 0f) + (pcState.StartsWith("CombatMove", StringComparison.OrdinalIgnoreCase) ? FightDrain : 0f);
        if (drain > 0f)
        {
            float before = _sinceDrain;
            Spend(drain * dt);
            if (!sprint && !p.bGliding)
                _sinceDrain = before + dt;
        }
        else
        {
            _sinceDrain += dt;
            bool resting = p.Physics == Actor.EPhysics.PHYS_Walking && !grapple;
            if (resting && _sinceDrain >= RecoverDelay)
                _stam = Math.Clamp(_stam + StaminaRecover * (fighting ? FightRecoverShare : 1f) * (_staminaRegen?.Value ?? 1f) * dt, 0f, 1f);
        }

        if (_exhausted && _stam >= ExhaustedUntil)
            SetExhausted(false);

        _stamPcState = pcState;
        _stamCamState = camState;
        _stamPhys = p.Physics;

        ApplyTiredFlags(pc, p, _exhausted);
        StaminaHud(now);
    }

    private static void ApplyTiredFlags(RPlayerController pc, RPawnPlayer p, bool tired)
    {
        if (tired)
        {
            if (!pc.bDissalowRun)
            {
                pc.bDissalowRun = true;
                _setNoRun = true;
            }
            if (!pc.bCapeGlideDisabled)
            {
                pc.bCapeGlideDisabled = true;
                _setNoGlide = true;
            }
            if (p.bCanJump || !pc.bDisableAutoJumping)
            {
                p.bCanJump = false;
                pc.bDisableAutoJumping = true;
                _setNoJump = true;
            }
            RGrappleGun gun = p.GrappleGun;
            if (gun != null && gun.IsValid && !gun.bGrappleGunDisabled)
            {
                gun.bGrappleGunDisabled = true;
                _setNoGrapple = gun;
            }
            return;
        }

        if (_setNoGrapple != null)
        {
            if (_setNoGrapple.IsValid)
                _setNoGrapple.bGrappleGunDisabled = false;
            _setNoGrapple = null;
        }

        if (_setNoRun)
        {
            pc.bDissalowRun = false;
            _setNoRun = false;
        }
        if (_setNoGlide)
        {
            pc.bCapeGlideDisabled = false;
            _setNoGlide = false;
        }
        if (_setNoJump)
        {
            p.bCanJump = true;
            pc.bDisableAutoJumping = false;
            _setNoJump = false;
        }
    }

    private static RHudModuleHealthBar? StaminaBarModule(long now)
    {
        if (_stamHud != null && _stamHud.IsValid && Stopwatch.GetElapsedTime(_stamHudCheck, now).TotalSeconds < 2.0)
            return _stamHud.HealthBarModule is { IsValid: true } hb0 ? hb0 : null;

        _stamHudCheck = now;
        RGFxMovieModularHud? found = null;
        foreach (RGFxMovieModularHud h in Game.FindObjects<RGFxMovieModularHud>())
        {
            if (h != null && h.IsValid && !h.IsClassDefaultObject && h.HealthBarModule is { IsValid: true })
            {
                found = h;
                break;
            }
        }

        if (found == null || _stamHud == null || found.Ptr != _stamHud.Ptr)
        {
            _shownStam = -1f;
            _stamTint = -1;
        }
        _stamHud = found;
        RHudModuleHealthBar? hb = found?.HealthBarModule;
        if (hb != null)
        {
            GFxObject? label = XpClip(hb, ".XpText.Text");
            if (label != null)
            {
                string text = label.GetString(new FString("text")).ToString() ?? "";
                if (text != StaminaLabel)
                {
                    if (text.Length > 0)
                        _xpLabel = text;
                    label.SetString(new FString("text"), new FString(StaminaLabel));
                    _stamTint = -1;
                }
            }
            if (!hb.bXpBarRequested)
                hb.SetXpBarVisible(true);
        }
        return hb;
    }

    private static GFxObject? XpClip(RHudModuleHealthBar hb, string tail)
    {
        GFxMoviePlayer movie = hb.GfxParentMovie;
        if (movie == null || !movie.IsValid)
            return null;
        GFxObject o = movie.GetVariableObject(new FString((hb.LocalRootString.ToString() ?? "") + XpRootTail + tail));
        return o != null && o.IsValid ? o : null;
    }

    private static void StaminaHud(long now)
    {
        RHudModuleHealthBar? hb = StaminaBarModule(now);
        if (hb == null)
            return;

        if (MathF.Abs(_stam - _shownStam) > 0.004f)
        {
            _staminaWriting = true;
            try
            {
                hb.SetXpLevel(_stam, true);
            }
            finally
            {
                _staminaWriting = false;
            }
            _shownStam = _stam;
        }

        int tint = _exhausted ? 2 : _stam < 0.25f ? 1 : 0;
        if (tint != _stamTint)
        {
            _stamTint = tint;
            TintXp(hb, tint);
        }
    }

    private static void TintXp(RHudModuleHealthBar hb, int tint)
    {
        var ct = new GFxObject.FASColorTransform
        {
            Multiply = tint switch
            {
                2 => new FLinearColor { R = 2f, G = 0.25f, B = 0.2f, A = 1f },
                1 => new FLinearColor { R = 1.5f, G = 0.95f, B = 0.2f, A = 1f },
                _ => new FLinearColor { R = 1f, G = 1f, B = 1f, A = 1f }
            },
            Add = new FLinearColor { R = 0f, G = 0f, B = 0f, A = 0f }
        };
        XpClip(hb, ".XPbar")?.SetColorTransform(ct);
        XpClip(hb, ".XpText")?.SetColorTransform(ct);
    }

    private static void StaminaOff()
    {
        try
        {
            RPlayerController pc = Game.GetPlayerController(0);
            if (pc != null && pc.IsValid && pc.Pawn is RPawnPlayer p && p.IsValid)
                ApplyTiredFlags(pc, p, false);

            RHudModuleHealthBar? hb = _stamHud != null && _stamHud.IsValid ? _stamHud.HealthBarModule : null;
            if (hb != null && hb.IsValid)
            {
                TintXp(hb, 0);
                if (_xpLabel.Length > 0)
                    XpClip(hb, ".XpText.Text")?.SetString(new FString("text"), new FString(_xpLabel));
                if (_gameXp >= 0f)
                    hb.SetXpLevel(_gameXp, true);
            }
        }
        catch (Exception ex)
        {
            Log("Stamina restore failed: " + ex.Message);
        }

        _stam = 1f;
        _exhausted = false;
        _shownStam = -1f;
        _stamTint = -1;
        _stamHud = null;
    }

    [Redirect(typeof(RHudModuleHealthBar), nameof(RHudModuleHealthBar.SetXpLevel))]
    private static void SetXpLevelRedirect(RHudModuleHealthBar self, float ValueProportion, bool Snap)
    {
        if (_stamina != null && _stamina.On && !_staminaWriting)
        {
            _gameXp = ValueProportion;
            return;
        }
        self.SetXpLevel(ValueProportion, Snap);
    }

    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.TryStrike))]
    private static bool TryStrikeRedirect(RPlayerControllerCombat self, float TimeInitiatedByPlayer)
    {
        if (TooTired)
            return false;
        bool ok = self.TryStrike(TimeInitiatedByPlayer);
        if (ok)
            Spend(StrikeCost);
        return ok;
    }

    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.TryAirStrike))]
    private static bool TryAirStrikeRedirect(RPlayerControllerCombat self, bool bOverrideCurrentMove)
    {
        if (TooTired)
            return false;
        bool ok = self.TryAirStrike(bOverrideCurrentMove);
        if (ok)
            Spend(StrikeCost);
        return ok;
    }

    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.TryFloorStrike))]
    private static bool TryFloorStrikeRedirect(RPlayerControllerCombat self, bool bOverrideCurrentMove)
    {
        if (TooTired)
            return false;
        bool ok = self.TryFloorStrike(bOverrideCurrentMove);
        if (ok)
            Spend(StrikeCost);
        return ok;
    }

    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.SpawnRunningStrike))]
    private static void SpawnRunningStrikeRedirect(RPlayerControllerCombat self)
    {
        if (TooTired)
            return;
        Spend(StrikeCost);
        self.SpawnRunningStrike();
    }

    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.TriggerQuickStrike))]
    private static void TriggerQuickStrikeRedirect(RPlayerControllerCombat self)
    {
        if (TooTired)
            return;
        self.TriggerQuickStrike();
    }

    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.SpawnEvade))]
    private static void SpawnEvadeRedirect(RPlayerControllerCombat self, bool bCanQueue)
    {
        if (TooTired)
            return;
        Spend(EvadeCost);
        self.SpawnEvade(bCanQueue);
    }

    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.TryToQueueEvade))]
    private static void TryToQueueEvadeRedirect(RPlayerControllerCombat self)
    {
        if (TooTired)
            return;
        self.TryToQueueEvade();
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.CanUseGrapple))]
    private static bool CanUseGrappleRedirect(RPlayerController self)
    {
        return !TooTired && self.CanUseGrapple();
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.GrapplePressed))]
    private static void GrapplePressedRedirect(RPlayerController self)
    {
        if (!TooTired)
            self.GrapplePressed();
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.FireGrapple))]
    private static void FireGrappleRedirect(RPlayerController self)
    {
        if (TooTired)
            return;
        if (StaminaActive)
        {
            Spend(GrappleCost);
            _grappleCharged = Stopwatch.GetTimestamp();
        }
        self.FireGrapple();
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.StartGrappleBoost))]
    private static void StartGrappleBoostRedirect(RPlayerController self)
    {
        if (!TooTired)
            self.StartGrappleBoost();
    }
}
