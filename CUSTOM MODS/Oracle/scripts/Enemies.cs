using System;
using System.Collections.Generic;
using System.Globalization;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace CeZeY.OracleScript;

public partial class Oracle
{
    private const double EnemyApplyGap = 1.0;
    private const string EnemyTablesKey = "CeZeY.Oracle.EnemyTables";

    private sealed record TableRule(Feature F, int[] Rows);

    private sealed record ManagerRule(Feature F, Func<RBMCombatManager, bool> Get, Action<RBMCombatManager, bool> Set);

    private sealed record PawnRule(string Key, Func<bool> Active, Func<float, float> Map, Func<RPawnVillain, float> Get, Action<RPawnVillain, float> Set);

    private static readonly List<TableRule> _tableRules = new();
    private static readonly List<ManagerRule> _managerRules = new();
    private static readonly List<PawnRule> _pawnRules = new();
    private static readonly Dictionary<string, bool> _managerOrig = new(StringComparer.Ordinal);
    private static readonly Dictionary<(nint Ptr, string Key), (float Orig, float Written)> _pawnOrig = new();
    private static Feature? _enemyChapter;
    private static float _chapterOrig = float.NaN;
    private static long _enemyApplied;
    private static bool _enemyDirty;

    private static Dictionary<string, (float Easy, float Hard)> TableOriginals
    {
        get
        {
            if (AppDomain.CurrentDomain.GetData(EnemyTablesKey) is Dictionary<string, (float, float)> d)
                return d;

            var made = new Dictionary<string, (float, float)>(StringComparer.Ordinal);
            AppDomain.CurrentDomain.SetData(EnemyTablesKey, made);
            return made;
        }
    }

    private static string Chapter(float v) => v < 0.5f ? "NORMAL" : "CHAPTER " + v.ToString("0", CultureInfo.InvariantCulture);

    private static void EnemyTable(string id, string label, string help, float min, float max, float step, float hot, params RBMCombatManager.EDifficultyType[] rows)
    {
        Feature f = Slider(id, label, help, 1f, min, max, step, hot, Times, _ => _enemyDirty = true);
        int[] idx = new int[rows.Length];
        for (int i = 0; i < rows.Length; i++)
            idx[i] = (int)rows[i];
        _tableRules.Add(new TableRule(f, idx));
    }

    private static void EnemyManager(string id, string label, string help, Func<RBMCombatManager, bool> get, Action<RBMCombatManager, bool> set)
    {
        Feature f = Toggle(id, label, help, _ => _enemyDirty = true);
        _managerRules.Add(new ManagerRule(f, get, set));
    }

    private static void EnemyPawnToggle(string id, string label, string help, Func<RPawnVillain, bool> get, Action<RPawnVillain, bool> set)
    {
        Feature f = Toggle(id, label, help, _ => _enemyDirty = true);
        _pawnRules.Add(new PawnRule(id, () => f.On, _ => 1f, v => get(v) ? 1f : 0f, (v, x) => set(v, x > 0.5f)));
    }

    private static void EnemyPawnSlider(string id, string label, string help, float min, float max, float step, float hot, Func<RPawnVillain, float> get, Action<RPawnVillain, float> set)
    {
        Feature f = Slider(id, label, help, 1f, min, max, step, hot, Times, _ => _enemyDirty = true);
        _pawnRules.Add(new PawnRule(id, () => MathF.Abs(f.Value - 1f) > 0.0001f, o => o * f.Value, get, set));
    }

    private static void BuildEnemies()
    {
        _tableRules.Clear();
        _managerRules.Clear();
        _pawnRules.Clear();

        Group("AGGRESSION");
        EnemyTable("enemy_attack_gap", "TIME BETWEEN ATTACKS", "How long the game waits before sending in the next attacker. Lower means enemies attack far more often.",
            0.1f, 3f, 0.1f, 0.3f, RBMCombatManager.EDifficultyType.EDT_TimeBeforeNextStrike);
        EnemyTable("enemy_windup", "ATTACK WIND-UP", "How long enemy attacks take to land, which is also your window to counter. Lower means faster attacks.",
            0.3f, 3f, 0.05f, 0.5f,
            RBMCombatManager.EDifficultyType.EDT_StrikeDurationScale,
            RBMCombatManager.EDifficultyType.EDT_BladeDurationScale,
            RBMCombatManager.EDifficultyType.EDT_BruteDurationScale);
        EnemyPawnToggle("enemy_full_on", "EVERYONE ATTACKS AT ONCE", "Enemies stop waiting for their turn and all pile on together. Brutal: expect several times the normal damage.",
            v => v.bFullOnAttack, (v, on) => v.bFullOnAttack = on);
        EnemyPawnToggle("enemy_no_simul", "NO DOUBLE OR TRIPLE ATTACKS", "Enemies stop teaming up for simultaneous attacks.",
            v => v.bDisableSimultaneousAttacks, (v, on) => v.bDisableSimultaneousAttacks = on);

        Group("TOUGHNESS");
        EnemyTable("enemy_damage", "DAMAGE THEY DEAL", "Scales the damage of every enemy attack: fists, weapons, guns, ninjas and brutes.",
            0f, 5f, 0.1f, 2f,
            RBMCombatManager.EDifficultyType.EDT_DamageUnarmed,
            RBMCombatManager.EDifficultyType.EDT_DamagePipe,
            RBMCombatManager.EDifficultyType.EDT_DamageBox,
            RBMCombatManager.EDifficultyType.EDT_DamageStunStickRepel,
            RBMCombatManager.EDifficultyType.EDT_DamageStunStickAttack,
            RBMCombatManager.EDifficultyType.EDT_DamageKnife,
            RBMCombatManager.EDifficultyType.EDT_DamageCombatGun,
            RBMCombatManager.EDifficultyType.EDT_DamageNinja,
            RBMCombatManager.EDifficultyType.EDT_DamageNinjaCombo,
            RBMCombatManager.EDifficultyType.EDT_BruteDamageUnarmed,
            RBMCombatManager.EDifficultyType.EDT_BruteDamageKnives,
            RBMCombatManager.EDifficultyType.EDT_BruteDamageShield,
            RBMCombatManager.EDifficultyType.EDT_BruteDamageStunFist,
            RBMCombatManager.EDifficultyType.EDT_BruteDamageThrowThug);
        EnemyPawnSlider("enemy_stun_time", "TIME KNOCKED DOWN", "How long enemies stay down after being knocked over.",
            0.1f, 5f, 0.1f, 2.5f, v => v.RagdollStunTimeMultiplier, (v, x) => v.RagdollStunTimeMultiplier = x);
        EnemyPawnSlider("enemy_hit_force", "HIT FORCE", "How far enemies fly when they are knocked over.",
            0.25f, 5f, 0.25f, 2.5f, v => v.RagdollForceMultiplier, (v, x) => v.RagdollForceMultiplier = x);
        Feature down = Choice("enemy_knockdown", "KNOCKDOWNS", "ALWAYS drops an enemy with every hit. NEVER keeps them on their feet.",
            new[] { "vanilla", "always", "never" }, new[] { "VANILLA", "ALWAYS", "NEVER" }, _ => _enemyDirty = true);
        _pawnRules.Add(new PawnRule("always_down", () => down.Choice == 1, _ => 1f, v => v.bAlwaysRagdollWhenHit ? 1f : 0f, (v, x) => v.bAlwaysRagdollWhenHit = x > 0.5f));
        _pawnRules.Add(new PawnRule("never_down", () => down.Choice == 2, _ => 1f, v => v.bNeverRagdollWhenHit ? 1f : 0f, (v, x) => v.bNeverRagdollWhenHit = x > 0.5f));

        Group("ATTACK TYPES");
        EnemyManager("enemy_no_grab", "NO GRABS", "Enemies stop grabbing you.", m => m.bDisableGrabAttack, (m, on) => m.bDisableGrabAttack = on);
        EnemyManager("enemy_no_charge", "NO CHARGE ATTACKS", "Enemies stop charging at you.", m => m.bDisableChargeAttack, (m, on) => m.bDisableChargeAttack = on);
        EnemyManager("enemy_no_fury", "NO FURY ATTACKS", "Combat experts stop their fury attack.", m => m.bDisableCombatExpertFuryAttack, (m, on) => m.bDisableCombatExpertFuryAttack = on);
        EnemyManager("enemy_no_ninja", "NINJAS CAN'T COUNTER", "Ninja enemies stop countering your strikes.", m => m.bDisableNinjaCounter, (m, on) => m.bDisableNinjaCounter = on);
        EnemyManager("enemy_no_revive", "NO MEDIC REVIVES", "Medics stop reviving knocked out enemies.", m => m.bDisableMedicRevives, (m, on) => m.bDisableMedicRevives = on);
        EnemyManager("enemy_no_buff", "NO MEDIC CHARGE-UP", "Medics stop electrifying other enemies.", m => m.bDisableMedicChargeUpThug, (m, on) => m.bDisableMedicChargeUpThug = on);

        Group("DIFFICULTY");
        EnemyManager("enemy_ignore_dda", "IGNORE DYNAMIC DIFFICULTY", "Turns off the hidden adjustment that makes fights easier when you are struggling.", m => m.bKismetIgnoreDDA, (m, on) => m.bKismetIgnoreDDA = on);
        _enemyChapter = Slider("enemy_chapter", "FORCE STORY CHAPTER", "Enemies fight as if you were at this point in the story. 1 is early, 12 is late. NORMAL uses your real progress.",
            0f, 0f, 12f, 1f, 12f, Chapter, _ => _enemyDirty = true);
    }

    private static void EnemyTick()
    {
        long now = Stopwatch.GetTimestamp();
        if (!_enemyDirty && Stopwatch.GetElapsedTime(_enemyApplied, now).TotalSeconds < EnemyApplyGap)
            return;

        _enemyApplied = now;
        _enemyDirty = false;

        RBMCombatManager? cm = GameInfo()?.CombatManager;
        if (cm == null || !cm.IsValid)
            return;

        foreach (TableRule r in _tableRules)
            ApplyTable(cm, r.Rows, r.F.Value);
        foreach (ManagerRule r in _managerRules)
            ApplyManager(cm, r);
        ApplyChapter(cm);
        ApplyPawnRules();
    }

    private static void ApplyTable(RBMCombatManager cm, int[] rows, float mult)
    {
        ApplyTable(cm.DifficultyProgressionDefines_Easy, 0, rows, mult);
        ApplyTable(cm.DifficultyProgressionDefines_Normal, 1, rows, mult);
        ApplyTable(cm.DifficultyProgressionDefines_Hard, 2, rows, mult);
    }

    private static void ApplyTable(TArray<RBMCombatManager.FDifficultyProgressionDefine> table, int tableIndex, int[] rows, float mult)
    {
        Dictionary<string, (float Easy, float Hard)> originals = TableOriginals;
        bool normal = MathF.Abs(mult - 1f) < 0.0001f;
        foreach (int i in rows)
        {
            if (i >= table.Count)
                continue;

            string key = tableIndex + ":" + i;
            RBMCombatManager.FDifficultyProgressionDefine row = table[i];
            if (!originals.TryGetValue(key, out (float Easy, float Hard) orig))
            {
                if (normal)
                    continue;
                orig = (row.EasyValue, row.HardValue);
                originals[key] = orig;
            }

            float easy = orig.Easy * mult;
            float hard = orig.Hard * mult;
            if (MathF.Abs(row.EasyValue - easy) > 0.0001f || MathF.Abs(row.HardValue - hard) > 0.0001f)
            {
                row.EasyValue = easy;
                row.HardValue = hard;
                table[i] = row;
            }
        }
    }

    private static void ApplyManager(RBMCombatManager cm, ManagerRule r)
    {
        string id = r.F.Id;
        if (r.F.On)
        {
            if (!_managerOrig.ContainsKey(id))
                _managerOrig[id] = r.Get(cm);
            if (!r.Get(cm))
                r.Set(cm, true);
        }
        else if (_managerOrig.Remove(id, out bool orig) && r.Get(cm) != orig)
        {
            r.Set(cm, orig);
        }
    }

    private static void ApplyChapter(RBMCombatManager cm)
    {
        if (_enemyChapter == null)
            return;

        float want = MathF.Round(_enemyChapter.Value);
        if (want <= 0f)
        {
            if (!float.IsNaN(_chapterOrig))
            {
                cm.SetOverrideChapter(_chapterOrig);
                _chapterOrig = float.NaN;
            }
            return;
        }

        if (float.IsNaN(_chapterOrig))
            _chapterOrig = cm.OverrideChapter;
        if (MathF.Abs(cm.OverrideChapter - want) > 0.01f)
            cm.SetOverrideChapter(want);
    }

    private static void ApplyPawnRules()
    {
        bool any = _pawnOrig.Count > 0;
        foreach (PawnRule r in _pawnRules)
            any |= r.Active();
        if (!any)
            return;

        foreach (RPawnVillain v in Game.FindObjects<RPawnVillain>())
        {
            if (v == null || !v.IsValid || v.IsClassDefaultObject || v.bDeleteMe)
                continue;

            foreach (PawnRule r in _pawnRules)
                ApplyPawnRule(v, r);
        }
    }

    private static void ApplyPawnRule(RPawnVillain v, PawnRule r)
    {
        var key = (v.Ptr, r.Key);
        bool known = _pawnOrig.TryGetValue(key, out (float Orig, float Written) entry);
        float now = r.Get(v);

        if (!r.Active())
        {
            if (known)
            {
                if (MathF.Abs(now - entry.Written) < 0.0001f)
                    r.Set(v, entry.Orig);
                _pawnOrig.Remove(key);
            }
            return;
        }

        if (!known || MathF.Abs(now - entry.Written) > 0.0001f)
            entry.Orig = now;

        float want = r.Map(entry.Orig);
        if (MathF.Abs(now - want) > 0.0001f)
            r.Set(v, want);
        _pawnOrig[key] = (entry.Orig, want);
    }

    private static void ForgetEnemies()
    {
        _managerOrig.Clear();
        _pawnOrig.Clear();
        _chapterOrig = float.NaN;
        _enemyDirty = true;
    }

    private static void RestoreEnemies()
    {
        try
        {
            RBMCombatManager? cm = GameInfo()?.CombatManager;
            if (cm != null && cm.IsValid)
            {
                foreach (TableRule r in _tableRules)
                    ApplyTable(cm, r.Rows, 1f);
                foreach (ManagerRule r in _managerRules)
                {
                    if (_managerOrig.TryGetValue(r.F.Id, out bool orig) && r.Get(cm) != orig)
                        r.Set(cm, orig);
                }
                if (!float.IsNaN(_chapterOrig))
                    cm.SetOverrideChapter(_chapterOrig);
            }

            if (_pawnOrig.Count > 0)
            {
                foreach (RPawnVillain v in Game.FindObjects<RPawnVillain>())
                {
                    if (v == null || !v.IsValid || v.IsClassDefaultObject)
                        continue;

                    foreach (PawnRule r in _pawnRules)
                    {
                        if (_pawnOrig.TryGetValue((v.Ptr, r.Key), out (float Orig, float Written) e) && MathF.Abs(r.Get(v) - e.Written) < 0.0001f)
                            r.Set(v, e.Orig);
                    }
                }
            }

        }
        catch (Exception ex)
        {
            Log("Enemy restore failed: " + ex.Message);
        }

        _managerOrig.Clear();
        _pawnOrig.Clear();
        _chapterOrig = float.NaN;
    }
}
