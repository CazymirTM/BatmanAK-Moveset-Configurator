using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace CeZeY.OracleScript;

public partial class Oracle
{
    private const int KindToggle = 0;
    private const int KindSlider = 1;
    private const int KindChoice = 2;
    private const int KindAction = 3;

    private const int TabPlayer = 0;
    private const int TabCombat = 1;
    private const int TabEnemies = 2;
    private const int TabVisual = 3;
    private const int TabCamera = 4;
    private const int TabBatmobile = 5;
    private const int TabWorld = 6;
    private const int TabCharacter = 7;
    private const int TabMusic = 8;
    private const int TabFun = 9;

    private const float EnemyRange = 9000f;

    private readonly record struct Prop(string Cls, string Name, string On, string Off);

    private sealed class Feature
    {
        public string Id = "";
        public string Label = "";
        public string Help = "";
        public string Group = "";
        public int Tab;
        public int Kind;
        public bool On;
        public float Value;
        public float Default;
        public float Min;
        public float Max;
        public float Step;
        public float HotValue;
        public Func<float, string>? Format;
        public string[]? Choices;
        public string[]? ChoiceKeys;
        public int Choice;
        public Action<bool>? Toggle;
        public Action<float>? Slide;
        public Action<int>? Pick;
        public Action? Run;
        public Action? Maintain;
        public bool Permanent;
        public bool Console;
        public bool Fast;
        public bool Pref;
        public bool Confirm;

        public bool Active => !Pref && Kind switch
        {
            KindToggle => On,
            KindSlider => MathF.Abs(Value - Default) > 0.0001f,
            KindChoice => Choice != 0,
            _ => false
        };

        public string StateText() => Kind switch
        {
            KindToggle => On ? "ON" : "OFF",
            KindSlider => Format!(Value),
            KindChoice => Choices![Choice],
            _ => ""
        };
    }

    private static readonly List<Feature> Features = new();
    private static readonly Dictionary<string, Feature> FeatureById = new(StringComparer.OrdinalIgnoreCase);
    private static string _group = "";
    private static int _tab;

    private static Feature Add(Feature f)
    {
        f.Tab = _tab;
        f.Group = _group;
        Features.Add(f);
        FeatureById[f.Id] = f;
        return f;
    }

    private static void Tab(int tab) => _tab = tab;

    private static void Group(string name) => _group = name;

    private static Feature Toggle(string id, string label, string help, Action<bool> apply, Action? maintain = null)
    {
        return Add(new Feature { Id = id, Label = label, Help = help, Kind = KindToggle, Toggle = apply, Maintain = maintain });
    }

    private static Feature Props(string id, string label, string help, params Prop[] props)
    {
        Feature f = Toggle(id, label, help, on =>
        {
            foreach (Prop p in props)
            {
                if (on)
                    SetPropTracked(p.Cls, p.Name, p.On);
                else
                    RestoreProp(p.Cls, p.Name, p.Off);
            }
        });
        f.Console = true;
        return f;
    }

    private static Feature Fast(Feature f, Action tick)
    {
        f.Maintain = tick;
        f.Fast = true;
        return f;
    }

    private static Feature Flag(string id, string label, string help, string prop)
    {
        Feature f = Toggle(id, label, help, on => SetDebugFlag(prop, on));
        f.Maintain = () => SetDebugFlag(prop, true);
        return f;
    }

    private static Feature Slider(string id, string label, string help, float def, float min, float max, float step, float hot, Func<float, string> format, Action<float> apply, bool maintain = false)
    {
        Feature f = Add(new Feature
        {
            Id = id, Label = label, Help = help, Kind = KindSlider, Value = def, Default = def, Min = min, Max = max, Step = step, HotValue = hot,
            Format = format, Slide = apply
        });

        if (maintain)
            f.Maintain = () => apply(f.Value);
        return f;
    }

    private static Feature PropSlider(string id, string label, string help, string cls, string prop, float def, float min, float max, float step, float hot, Func<float, string> format)
    {
        Feature f = Slider(id, label, help, def, min, max, step, hot, format, v =>
        {
            if (MathF.Abs(v - def) < 0.0001f)
                RestoreProp(cls, prop, def.ToString("0.0#####", CultureInfo.InvariantCulture));
            else
                SetPropTracked(cls, prop, v.ToString("0.0#####", CultureInfo.InvariantCulture));
        });
        f.Console = true;
        return f;
    }

    private static Feature Choice(string id, string label, string help, string[] keys, string[] labels, Action<int> apply, bool console = false)
    {
        return Add(new Feature { Id = id, Label = label, Help = help, Kind = KindChoice, ChoiceKeys = keys, Choices = labels, Pick = apply, Console = console });
    }

    private static Feature Action(string id, string label, string help, Action run, bool permanent = false)
    {
        return Add(new Feature { Id = id, Label = label, Help = help, Kind = KindAction, Run = run, Permanent = permanent });
    }

    private static string Times(float v) => v.ToString("0.00", CultureInfo.InvariantCulture) + "x";

    private static string Percent(float v) => (v * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Secs(float v) => v.ToString("0", CultureInfo.InvariantCulture) + " s";

    private static RCheatManager Cheats()
    {
        RCheatManager cm = Game.GetCheatManager();
        if (cm == null || !cm.IsValid)
            throw new InvalidOperationException("cheat manager unavailable");
        return cm;
    }

    private static RPawnPlayer? Player()
    {
        if (Game.GetPlayerController(0)?.Pawn is RPawnPlayer owned && owned.IsValid)
            return owned;

        RPawnPlayer p = Game.GetPlayerPawn(0);
        return p != null && p.IsValid ? p : null;
    }

    private static RGameInfo? GameInfo()
    {
        RGameInfo g = Game.GetGameInfo();
        return g != null && g.IsValid ? g : null;
    }

    private static void BuildFeatures()
    {
        Features.Clear();
        FeatureById.Clear();

        Tab(TabPlayer);
        Group("SURVIVAL");
        Feature god = Toggle("god_mode", "GOD MODE", "You take no damage and your health stays full. Works for every playable character.", ApplyGod);
        god.Maintain = () => ApplyGod(true);
        Action("restore_health", "RESTORE HEALTH", "Refills your health right now.", () => Cheats().RestoreHealth());

        Group("MOVEMENT");
        Slider("move_speed", "RUN SPEED", "Multiplies how fast you walk and run.", 1f, 0.5f, 3f, 0.05f, 1.6f, Times, ApplyMoveSpeed, true);

        BuildGlide();

        Group("GADGETS");
        Props("gadget_no_cooldown", "NO GADGET COOLDOWN", "Gadgets can be equipped and used again while they recharge.",
            new Prop("RInventoryGadget", "bCanEquipWhileRecharging", "True", "False"));
        Props("gadget_anywhere", "GADGETS ANYWHERE", "Use gadgets from vents, corner cover, vantage points and silent predator.",
            new Prop("RInventoryGadget", "bUsableFromCrawlSpace", "True", "False"),
            new Prop("RInventoryGadget", "bUsableFromCornerCover", "True", "False"),
            new Prop("RInventoryGadget", "bUsableFromVantagePoints", "True", "False"),
            new Prop("RInventoryGadget", "bCanUseFromSilentPredator", "True", "False"));
        PropSlider("smoke_duration", "SMOKE PELLET DURATION", "How long Batman's smoke pellet cloud lasts.", "RSmokeScreenBm", "SmokeScreenDuration",
            10f, 5f, 60f, 5f, 30f, Secs);
        Action("give_gadgets", "GIVE ALL GADGETS", "Adds every gadget to the gadget wheel.", () => Cheats().DebugGiveAllGadgets());

        Group("STEALTH");
        Props("silent_running", "SILENT RUNNING", "Your footsteps make no noise.", new Prop("RPawnPlayer", "bSilentRunning", "True", "False"));
        Props("drone_invisible", "INVISIBLE TO DRONES", "Drones and sentry guns can't see you.", new Prop("RPawnPlayer", "bInvisibleToDrones", "True", "False"));

        Tab(TabCombat);
        Group("FIGHTING");
        Feature oneHit = Toggle("one_hit_ko", "ONE HIT KO", "Every enemy near you is left on 1 health, so any hit knocks them out.", _ => { });
        oneHit.Maintain = OneHitMaintain;
        Props("upgraded_batsuit", "UPGRADED BATSUIT", "Turns on the batsuit's combat upgrades.", new Prop("RPawnPlayerCombat", "bUpgradedBatsuit", "True", "False"));
        Flag("always_takedown", "ALWAYS TAKEDOWN READY", "Special combo takedowns are always ready, no combo needed.", "Debug_Combat_CanAlwaysTakedown");
        Flag("freeflow_focus", "FREEFLOW FOCUS ALWAYS", "Freeflow focus is always available in fights.", "Debug_Combat_AlwaysFreeflowFocusMode");
        Toggle("counter_icons", "COUNTER ICONS IN NG+", "Brings back the counter warning icons above attacking enemies in New Game + and Knightmare.", _ => { });

        Group("TAKEDOWNS");
        Flag("silent_takedowns", "SILENT COMBAT TAKEDOWNS", "Combat takedowns are silent, so nearby enemies don't hear them.", "Debug_Player_ForceSilentCombatTakedowns");
        Flag("env_takedowns", "ENVIRONMENT TAKEDOWNS", "Batman uses environmental takedowns whenever a wall or object is near.", "Debug_Combat_ForceEnvironmentUse");
        Props("predator_combos", "COMBO TAKEDOWNS IN PREDATOR", "Combo takedowns work in predator encounters too.",
            new Prop("RPawnPlayerCombat", "bCanDoComboTakedownOnPredator", "True", "False"));
        Props("double_shockwave", "DOUBLE SHOCKWAVE LANDING", "Shockwave landings trigger a second shockwave.",
            new Prop("RSpecialMoveConfig_ShockwaveLand", "bTriggerSecondaryShockwave", "True", "False"));

        Tab(TabEnemies);
        Group("CONTROL");
        Feature freeze = Toggle("freeze_enemies", "FREEZE ENEMIES", "Every enemy near you freezes in place and can't attack. Turn off to release them.", on => { if (!on) UnfreezeAll(); });
        Fast(freeze, FreezeMaintain);
        Slider("enemy_health", "ENEMY HEALTH", "Health of enemies that show up from now on. Low values make them go down fast.", 1f, 0.1f, 3f, 0.1f, 0.3f, Percent, ApplyEnemyHealth, true);
        Slider("enemy_size", "ENEMY SIZE", "Shrinks or grows every enemy near you. Purely visual.", 1f, 0.5f, 2f, 0.05f, 1.4f, Times, ApplyEnemySize, true);

        Group("BEHAVIOUR");
        Props("disarm_first_hit", "DISARM ON FIRST HIT", "Thugs drop their melee weapons as soon as they are hit.", new Prop("RBMWeapon", "DropOnFirstHit", "True", "False"));
        Props("force_riots", "FORCE RIOTS", "Street riots break out across the city.", new Prop("RPopulationManager", "bForceRiots", "True", "False"));
        Props("rioters_fight", "RIOTERS ALWAYS FIGHT", "Rioting thugs pick fights instead of just smashing things.", new Prop("RRiotBrain", "bRiotWantsToFight", "True", "False"));

        Group("INSTANT");
        Action("ko_all", "KO ALL ENEMIES", "Knocks out every enemy in the area.", () => Cheats().DebugKillAllEnemies());
        Action("ko_all_but_one", "KO ALL BUT ONE", "Leaves exactly one enemy standing.", () => Cheats().DebugKillAllButOneEnemyInRoom());
        Action("destroy_weapons", "DESTROY ALL WEAPONS", "Removes guns and melee weapons from the area.", () => Cheats().DebugDestroyAllWeapons());

        Tab(TabVisual);
        Group("SCREEN");
        Choice("post_process", "POST PROCESSING", "REDUCED strips the colour grading. OFF also flattens the lighting. Both work together with the sky & fog looks.",
            new[] { "vanilla", "reduced", "off" }, new[] { "VANILLA", "REDUCED", "OFF" }, ApplyPostProcess);

        Group("ATMOSPHERE");
        BuildAtmosphere();

        Group("CHARACTER");
        Feature cape = Toggle("hide_cape", "HIDE CAPE", "Hides the cape of Batman and other caped characters.", ApplyCape);
        cape.Maintain = () => ApplyCape(true);
        Slider("batman_size", "CHARACTER SIZE", "Shrinks or grows your character. Purely visual, the collision stays the same.", 1f, 0.6f, 1.6f, 0.05f, 1.2f, Times, ApplyBatmanSize, true);

        Group("WEATHER");
        BuildRain();

        Group("GUNS");
        Choice("jammed_blast", "JAMMED GUN EXPLOSION", "What a disruptor-jammed gun looks like when it blows up. TANK uses the drone tank explosion.",
            new[] { "vanilla", "tank" }, new[] { "VANILLA", "TANK" }, ApplyJammedBlast, true);

        Tab(TabCamera);
        BuildCamera();

        Tab(TabBatmobile);
        Group("DEFENCE");
        Fast(Toggle("batmobile_invincible", "INVINCIBLE BATMOBILE", "The Batmobile's armour stays full, so it can't be destroyed.", _ => { }), BatmobileArmourTick);

        Group("WEAPONS");
        Fast(Toggle("infinite_power", "INFINITE MISSILE POWER", "The weapon generator stays full, so the missile barrage is always ready.", _ => { }), BatmobilePowerTick);
        Fast(Toggle("no_cooldowns", "NO WEAPON COOLDOWNS", "Missiles, EMP, 60mm cannon and the other special weapons are ready again straight after firing.", _ => { }), BatmobileCooldownTick);
        Fast(Toggle("perfect_aim", "PERFECT ACCURACY", "The Vulcan gun never loses accuracy while firing.", _ => { }), BatmobileAccuracyTick);

        Group("RECOVERY");
        Action("rescue_batmobile", "RESCUE BATMOBILE", "Puts a stuck or flipped Batmobile back on its wheels.", () => Cheats().RescueBatmobile());
        Action("kill_tanks", "DESTROY ALL TANKS", "Destroys every drone tank nearby.", () => Cheats().KillHeavyTanks());

        Tab(TabWorld);
        Group("TRAVEL");
        Action("teleport_waypoint", "TELEPORT TO WAYPOINT", "Set a waypoint on the map (Tab), then run this. Works across islands: the screen fades to black while the city loads.", StartTeleport);
        Feature fade = Choice("teleport_fade", "TELEPORT FADE", "QUICK cuts to black at once. SLOW FADE dims the screen to black gently before the jump and fades back in slower.",
            new[] { "quick", "slow" }, new[] { "QUICK", "SLOW FADE" }, _ => { });
        fade.Pref = true;

        Group("TIME");
        Slider("game_speed", "GAME SPEED", "Speeds up or slows down the whole game.", 1f, 0.2f, 2f, 0.05f, 0.5f, Times, ApplyGameSpeed, true);

        Tab(TabCharacter);
        Group("SWITCH");
        BuildCharacters();

        Tab(TabMusic);
        Group("PLAYER");
        BuildMusic();

        Tab(TabFun);
        BuildFun();

        Tab(TabWorld);
        Group("PROGRESS // CHANGES YOUR SAVE");
        Action("upgrade_points", "+10 UPGRADE POINTS", "Adds 10 points to spend on upgrades.", () => GiveUpgradePoints(10), true);
        Action("unlock_upgrades", "UNLOCK ALL UPGRADES", "Unlocks every upgrade for Batman and the Batmobile.", () => Cheats().UnlockAllUpgrades(true), true);
        Action("riddler_map", "REVEAL RIDDLER MAP", "Reveals Riddler trophies and puzzles on the map.", () => Cheats().GiveRiddlerMap(), true);
        Action("unlock_all", "UNLOCK EVERYTHING", "The game's own unlock-all cheat: upgrades, gadgets, extras and more.", () => Cheats().UnlockAll(), true);
    }

    private static void ApplyGod(bool on)
    {
        RPlayerController pc = Game.GetPlayerController(0);
        if (pc != null && pc.IsValid && pc.bGodMode != on)
            pc.bGodMode = on;

        RCheatManager cm = Game.GetCheatManager();
        if (cm != null && cm.IsValid && cm.bIgnoreDamage != on)
            cm.bIgnoreDamage = on;

        RPawnPlayer? p = on ? Player() : null;
        if (p != null && p.Health > 0 && p.Health < p.HealthMax)
            p.Health = p.HealthMax;
    }

    private static void ApplyMoveSpeed(float v)
    {
        RPawnPlayer? p = Player();
        if (p != null && MathF.Abs(p.MovementSpeedModifier - v) > 0.001f)
            p.MovementSpeedModifier = v;
    }

    private static void ApplyBatmanSize(float v)
    {
        RPawnPlayer? p = Player();
        if (p != null && MathF.Abs(p.DrawScale - v) > 0.001f)
            p.SetDrawScale(v);
    }

    private static void ApplyCape(bool hidden)
    {
        if (Player() is RPawnPlayerBmBase bm)
        {
            RCapeComponent cape = bm.CapeComponent;
            if (cape != null && cape.IsValid && cape.IsCapeHiddenGame != hidden)
                cape.CapeSetHiddenGame(hidden);
        }
    }

    private static void ApplyGameSpeed(float v)
    {
        RGameInfo? g = GameInfo();
        if (g != null && MathF.Abs(g.DebugGameSpeed - v) > 0.001f)
            g.SetDebugGameSpeed(v);
    }

    private static void ApplyEnemyHealth(float v)
    {
        RBMCombatManager? cm = GameInfo()?.CombatManager;
        if (cm != null && cm.IsValid && MathF.Abs(cm.PawnHealthMultiplier - v) > 0.001f)
            cm.PawnHealthMultiplier = v;
    }

    private static IEnumerable<RPawnVillain> NearbyEnemies()
    {
        Pawn? p = Game.GetPlayerController(0)?.Pawn;
        if (p == null || !p.IsValid)
            yield break;

        Vector3 at = p.Location;
        foreach (RPawnVillain v in Game.FindObjects<RPawnVillain>())
        {
            if (v == null || !v.IsValid || v.IsClassDefaultObject || v.bDeleteMe)
                continue;
            if (Vector3.DistanceSquared(v.Location, at) > EnemyRange * EnemyRange)
                continue;
            yield return v;
        }
    }

    private static void OneHitMaintain()
    {
        foreach (RPawnVillain v in NearbyEnemies())
        {
            if (!v.bGoodAsDead && v.Health > 1)
                v.Health = 1;
        }
    }

    private static readonly HashSet<nint> _resized = new();

    private static readonly Dictionary<nint, Vector3> _frozenAt = new();
    private static readonly List<RPawnVillain> _freezeList = new();
    private static long _freezeScan;

    private static void FreezeMaintain()
    {
        SetDebugFlag("Debug_Combat_CanAttack", false);

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (System.Diagnostics.Stopwatch.GetElapsedTime(_freezeScan, now).TotalSeconds >= 0.5)
        {
            _freezeScan = now;
            _freezeList.Clear();
            _freezeList.AddRange(NearbyEnemies());
        }

        foreach (RPawnVillain v in _freezeList)
        {
            if (!v.IsValid)
                continue;

            if (v.bGoodAsDead || v.Health <= 0)
            {
                if (_frozenAt.Remove(v.Ptr))
                    SetFrozen(v, false);
                continue;
            }

            if (!_frozenAt.TryGetValue(v.Ptr, out Vector3 at) || Vector3.DistanceSquared(at, v.Location) > 2000f * 2000f)
            {
                _frozenAt[v.Ptr] = v.Location;
                SetFrozen(v, true);
                continue;
            }

            SetFrozen(v, true);
            if (Vector3.DistanceSquared(at, v.Location) > 16f)
                v.SetLocation(at);
        }
    }

    private static void SetFrozen(RPawnVillain v, bool frozen)
    {
        if (v.bTickIsDisabled != frozen)
            v.SetTickIsDisabled(frozen);

        Controller c = v.Controller;
        if (c != null && c.IsValid && c.bTickIsDisabled != frozen)
            c.SetTickIsDisabled(frozen);

        SkeletalMeshComponent mesh = v.Mesh;
        if (mesh != null && mesh.IsValid && mesh.bPauseAnims != frozen)
            mesh.bPauseAnims = frozen;

        if (frozen)
            v.Velocity = Vector3.Zero;
    }

    private static void UnfreezeAll()
    {
        foreach (RPawnVillain v in Game.FindObjects<RPawnVillain>())
        {
            if (v == null || !v.IsValid || v.IsClassDefaultObject || !_frozenAt.ContainsKey(v.Ptr))
                continue;

            SetFrozen(v, false);
        }

        _frozenAt.Clear();
        _freezeList.Clear();
        SetDebugFlag("Debug_Combat_CanAttack", true);
    }

    private static void ApplyEnemySize(float v)
    {
        bool reset = MathF.Abs(v - 1f) < 0.001f;
        foreach (RPawnVillain e in reset ? Game.FindObjects<RPawnVillain>() : NearbyEnemies())
        {
            if (e == null || !e.IsValid || e.IsClassDefaultObject)
                continue;
            if (reset && !_resized.Contains(e.Ptr))
                continue;
            if (MathF.Abs(e.DrawScale - v) > 0.001f)
                e.SetDrawScale(v);
            if (!reset)
                _resized.Add(e.Ptr);
        }

        if (reset)
            _resized.Clear();
    }

    private static RPersistentDebugData? _pdd;

    private static void SetDebugFlag(string prop, bool on)
    {
        if (_pdd == null || !_pdd.IsValid)
        {
            _pdd = null;
            foreach (RPersistentDebugData d in Game.FindObjects<RPersistentDebugData>())
            {
                if (d != null && d.IsValid && !d.IsClassDefaultObject)
                {
                    _pdd = d;
                    break;
                }
            }
        }

        if (_pdd == null)
            return;

        var pi = typeof(RPersistentDebugData).GetProperty(prop);
        if (pi != null && (bool)pi.GetValue(_pdd)! != on)
            pi.SetValue(_pdd, on);
    }

    private const string JammedCls = "RBMWeaponRanged";
    private const string JammedProp = "JammedExplosionVFX";

    private static void ApplyJammedBlast(int mode)
    {
        if (mode == 0)
        {
            RestoreProp(JammedCls, JammedProp, "ParticleSystem'VFX_Gadgets.Disruptor.Double_Jammed_Explosion'");
            return;
        }

        SetPropTracked(JammedCls, JammedProp, "ParticleSystem'VFX_Vehicles.Particles.Tank.HeavyTank_Explosion'");
        if (GetProp(JammedCls, JammedProp).Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            RestoreProp(JammedCls, JammedProp, "ParticleSystem'VFX_Gadgets.Disruptor.Double_Jammed_Explosion'");
            LogThrottled("Tank explosion effect isn't loaded here yet, kept the normal one. Pick TANK again near drone tanks.");
        }
    }

    private static void GiveUpgradePoints(int n)
    {
        RPersistentShared? ps = GameInfo()?.PersistentShared;
        if (ps == null || !ps.IsValid)
            throw new InvalidOperationException("no persistent data");
        ps.UpgradePointsAvailable += n;
    }
}
