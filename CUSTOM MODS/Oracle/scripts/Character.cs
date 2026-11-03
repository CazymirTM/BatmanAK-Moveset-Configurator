using System;
using System.Collections;
using System.Numerics;
using System.Reflection;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.BmScript;
using BmSDK.Engine;
using BmSDK.Framework;

namespace CeZeY.OracleScript;

public partial class Oracle
{
    private sealed record CharDef(string Key, string Label, string Packages, string PlayerClass, string Name, bool DiveBoost, bool InstantCar);

    private static readonly CharDef[] Characters =
    {
        new("batman", "BATMAN", "Playable_Batman_Std_SF", "Playable_Batman", "Batman", false, false),
        new("nightwing", "NIGHTWING", "Playable_Nightwing0_SF|Playable_Nightwing_SF", "Playable_Nightwing0", "Nightwing0", false, false),
        new("robin", "ROBIN", "Playable_Robin0_SF|Playable_Robin_SF", "Playable_Robin0", "Robin0", true, false),
        new("catwoman", "CATWOMAN", "Playable_Catwoman0_SF|Playable_Catwoman_SF", "Playable_Catwoman0", "Catwoman0", false, false),
        new("batgirl", "BATGIRL", "Playable_DLCBatgirl_SF", "Playable_DLCBatgirl", "DLCBatgirl", false, true),
        new("redhood", "RED HOOD", "Playable_DLCRedHood_SF", "Playable_DLCRedHood", "DLCRedHood", false, true),
        new("harley", "HARLEY QUINN", "Playable_DLCHarleyQuinn", "Playable_DLCHarleyQuinn", "DLCHarleyQuinn", false, true),
    };

    private const string CharacterKey = "CeZeY.Oracle.Character";
    private const string SpawnedKey = "CeZeY.Oracle.SpawnedClasses";
    private static bool _guardDone;
    private static string _chClass = "";
    private static bool _chProtect;
    private const int ChIdle = 0;
    private const int ChFadeOut = 1;
    private const int ChSpawning = 2;
    private const float SpawnSettle = 4f;
    private const float SpawnGiveUp = 12f;
    private const float BatmobileReach = 1400f;
    private const float BatmobileDoor = 900f;
    private static float _carFade;

    private static int _chPhase;
    private static float _chTimer;
    private static int _chTarget;
    private static nint _chOldPawn;
    private static bool _chRecovering;
    private static Feature? _chPick;
    private static float _saveToastLeft;
    private static float _chMaintain;
    private static float _gcLeft;
    private static bool _aliasesDone;
    private static RSeqAct_SwitchPlayerCharacter? _chAction;
    private static PlayerStart? _chStart;

    private static int _chCurrent
    {
        get => AppDomain.CurrentDomain.GetData(CharacterKey) is int i ? i : 0;
        set => AppDomain.CurrentDomain.SetData(CharacterKey, value);
    }

    private static bool CharacterBusy => _chPhase != ChIdle;

    private static bool PlayingOther => _chCurrent != 0;

    private static string SpawnedClasses
    {
        get => AppDomain.CurrentDomain.GetData(SpawnedKey) as string ?? ";";
        set => AppDomain.CurrentDomain.SetData(SpawnedKey, value);
    }

    private static bool IsBatman(Pawn? p) => p is RPawnPlayerBm;

    private static void BuildCharacters()
    {
        var keys = new string[Characters.Length];
        var labels = new string[Characters.Length];
        for (int i = 0; i < Characters.Length; i++)
        {
            keys[i] = Characters[i].Key;
            labels[i] = Characters[i].Label;
        }

        _chPick = Choice("play_as", "PLAY AS", "Pick a character, then run SWITCH CHARACTER. Batgirl, Red Hood and Harley Quinn need their story pack DLC.", keys, labels, _ => { });
        _chPick.Pref = true;
        Feature sw = Action("switch_character", "SWITCH CHARACTER", "Fades to black and swaps you to the character above, right where you stand. Saving pauses until you are Batman again.", StartCharacterSwitch);
        sw.Confirm = true;
        Action("back_to_batman", "BACK TO BATMAN", "Swaps back to Batman right where you stand. Saving works again after this.", () =>
        {
            if (_chPick != null)
                _chPick.Choice = 0;
            StartCharacterSwitch();
        });

        Group("BATMOBILE");
        Action("call_batmobile", "CALL / ENTER BATMOBILE", "Any character: gets in the Batmobile when it is close, otherwise calls it to you. As another character the game's own E key (LB on a controller) gets you in too.", BatmobileAction);
    }

    private static string ConfirmText(Feature f)
    {
        if (f.Id == "switch_character" && _chPick != null)
            return "SWITCH TO " + Characters[_chPick.Choice].Label + "?";
        if (f.Permanent)
            return "CHANGE YOUR SAVE: " + f.Label + "?";
        return f.Label + "?";
    }

    private static void StartCharacterSwitch()
    {
        int target = _chPick?.Choice ?? 0;
        if (CharacterBusy || TeleportBusy)
            throw new InvalidOperationException("already switching or teleporting");
        if (!_ready || !_applied)
            throw new InvalidOperationException("the game is still loading");

        RPlayerController pc = Game.GetPlayerController(0);
        if (pc == null || !pc.IsValid || pc.Pawn is not RPawnPlayer)
            throw new InvalidOperationException("get out of the Batmobile first");

        if (target == _chCurrent && (target != 0 || IsBatman(pc.Pawn)))
        {
            ShowNotice("CHARACTER", "ALREADY " + Characters[target].Label, false);
            return;
        }

        _chTarget = target;
        _chTimer = 0f;
        _chRecovering = false;
        _chProtect = false;
        _chPhase = ChFadeOut;
        CloseMenu();
        Log("Switching character to " + Characters[target].Label + ".");
    }

    private static void CharacterTick(float dt)
    {
        if (_saveToastLeft > 0f)
            _saveToastLeft -= dt;

        switch (_chPhase)
        {
            case ChFadeOut:
                _chTimer += dt;
                _blackout = EaseIn(_chTimer / SlowFadeOut);
                if (_chTimer < SlowFadeOut)
                    return;
                _blackout = 1f;
                if (!BeginGameSwitch(_chTarget))
                {
                    FailSwitch("COULD NOT SWITCH");
                    return;
                }

                _chTimer = 0f;
                _chPhase = ChSpawning;
                return;

            case ChSpawning:
                _chTimer += dt;
                _blackout = 1f;
                SpawnWait();
                return;
        }

        if (_carFade > 0f && !TeleportBusy)
        {
            _carFade = Math.Max(0f, _carFade - dt);
            _blackout = Smooth(_carFade / 0.45f);
        }

        if (PlayingOther)
            OtherCharacterTick(dt);

        if (_gcLeft > 0f)
        {
            _gcLeft -= dt;
            if (_gcLeft <= 0f)
                CollectAfterSwitch();
        }
    }

    private static bool BeginGameSwitch(int index)
    {
        CharDef def = Characters[index];
        RPlayerController pc = Game.GetPlayerController(0);
        WorldInfo wi = Game.GetWorldInfo();
        RGameInfo gi = Game.GetGameInfo();
        if (pc == null || !pc.IsValid || wi == null || gi == null)
            return false;

        Sequence seq = wi.GetGameSequence();
        if (seq == null || !seq.IsValid)
        {
            Log("Character switch: no game sequence.");
            return false;
        }

        RegisterPawnAliases();

        foreach (string package in def.Packages.Split('|'))
        {
            try
            {
                Game.LoadPackage(package);
            }
            catch (Exception ex)
            {
                Log("Package " + package + " not loaded: " + ex.Message);
            }
        }

        gi.LoadPC(def.PlayerClass);

        _chOldPawn = pc.Pawn?.Ptr ?? 0;
        _chStart = Game.SpawnActor<PlayerStart>(default, pc.Location, pc.Rotation);
        if (_chStart == null)
        {
            Log("Character switch: could not place a start point.");
            return false;
        }

        _chAction = new RSeqAct_SwitchPlayerCharacter(seq);
        _chAction.ParentSequence = seq;
        _chAction.CharacterName = def.Name;
        _chAction.PlayerStartPoint = _chStart;
        _chAction.SwitchToThisPawn = null;

        pc.PrepareForPlayerSwitch();
        _chAction.ForceActivateInput(0);
        Log("Character switch to " + def.Label + " sent to the game.");
        return true;
    }

    private static void SpawnWait()
    {
        Pawn? pawn = Game.GetPlayerController(0)?.Pawn;
        bool fresh = pawn != null && pawn.IsValid && pawn.Ptr != _chOldPawn;

        if (_chTimer >= SpawnSettle && fresh)
        {
            FinishSwitch(pawn!);
            return;
        }

        if (_chTimer < SpawnGiveUp)
            return;

        _chAction = null;
        _chStart = null;
        if (_chTarget != 0 && !_chRecovering)
        {
            Log("Character switch: " + Characters[_chTarget].Label + " did not appear, going back to Batman.");
            _chRecovering = true;
            _chTarget = 0;
            _chTimer = 0f;
            if (BeginGameSwitch(0))
                return;
        }

        _chCurrent = 0;
        FailSwitch(_chRecovering ? "NOT AVAILABLE" : "SWITCH FAILED");
    }

    private static void FinishSwitch(Pawn pawn)
    {
        CharDef def = Characters[_chTarget];
        _chAction = null;
        _chStart = null;
        _chPhase = ChIdle;
        _chCurrent = _chTarget;
        _chClass = _chTarget == 0 ? "" : pawn.Class.GetPathName(null);
        if (_chClass.Length > 0 && !SpawnedClasses.Contains(";" + _chClass + ";", StringComparison.OrdinalIgnoreCase))
            SpawnedClasses += _chClass + ";";
        _chMaintain = 0f;
        _gcLeft = 6f;

        RPlayerController pc = Game.GetPlayerController(0);
        if (_chTarget != 0 && pc != null && pc.IsValid)
            pc.bCanGetInBatmobile = true;

        _tpForceSlow = true;
        _tpToastTitle = _chProtect ? "SAVE PROTECTED" : _chRecovering ? "CHARACTER" : "NOW PLAYING AS";
        _tpReturnResult = _chProtect || _chRecovering ? "BACK TO BATMAN" : def.Label;
        _chProtect = false;
        _tpPhase = TpFadeIn;
        _tpTimer = 0f;
        if (_chPick != null)
            _chPick.Choice = _chTarget;
        Log("Now playing as " + def.Label + " (" + pawn.GetPathName(null) + ").");
    }

    private static void FailSwitch(string why)
    {
        Log("Character switch failed: " + why);
        _chPhase = ChIdle;
        _chAction = null;
        _chStart = null;
        _tpForceSlow = true;
        _tpToastTitle = "CHARACTER";
        _tpReturnResult = "";
        _tpResult = why;
        _tpOk = false;
        _tpPhase = TpFadeIn;
        _tpTimer = 0f;
    }

    private static void OtherCharacterTick(float dt)
    {
        RPlayerController pc = Game.GetPlayerController(0);
        if (pc == null || !pc.IsValid)
            return;

        _chMaintain -= dt;
        if (_chMaintain <= 0f)
        {
            _chMaintain = 1f;
            if (!pc.bCanGetInBatmobile)
                pc.bCanGetInBatmobile = true;
            if (Characters[_chCurrent].DiveBoost && pc.Pawn is RPawnPlayer p)
                SetBool(p, "bCanDiveBoost", true);
        }
    }

    private static bool TryEnterBatmobile(float reach)
    {
        RPlayerController pc = Game.GetPlayerController(0);
        if (pc == null || !pc.IsValid || pc.IsInBatmobile() || pc.IsBatmobileUnderRemoteControl() || pc.Pawn is not RPawnPlayer pawn)
            return false;

        RVehicleBatmobileBase? car = Batmobile();
        if (car == null || !car.IsValid || car.Driver != null || Vector3.Distance(car.Location, pawn.Location) > reach)
            return false;
        if (car is RVehicleBatmobile rb && !rb.CanEnterVehicle(pawn))
            return false;

        if (Characters[_chCurrent].InstantCar)
        {
            _carFade = 0.45f;
            car.GetInBatmobileInstant(pawn, false);
        }
        else
            car.GetInBatmobile(pawn);

        Log("Entered the Batmobile as " + Characters[_chCurrent].Label + (Characters[_chCurrent].InstantCar ? " (instant)." : "."));
        return true;
    }

    private static bool EntryKey(Keys key)
    {
        return key == Keys.E && EnterBatmobileNearby();
    }

    private static bool EnterBatmobileNearby()
    {
        if (!PlayingOther || _menuOpen || CharacterBusy)
            return false;

        try
        {
            return TryEnterBatmobile(BatmobileDoor);
        }
        catch (Exception ex)
        {
            Log("Batmobile entry failed: " + ex.Message);
            return false;
        }
    }

    private static void BatmobileAction()
    {
        RPlayerController pc = Game.GetPlayerController(0);
        if (pc == null || !pc.IsValid || pc.IsInBatmobile() || pc.IsBatmobileUnderRemoteControl())
            return;

        pc.bCanGetInBatmobile = true;
        if (TryEnterBatmobile(BatmobileReach))
            return;

        var mode = pc.CallBatmobile();
        bool coming = mode != RVehicleBatmobileBase.EBatmobilePickupMode.BPM_None;
        ShowNotice("BATMOBILE", coming ? "ON ITS WAY" : "CAN'T REACH YOU HERE", coming);
        Log("Batmobile called: " + mode + ".");
    }

    private static void SetBool(object target, string name, bool value)
    {
        PropertyInfo? pi = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (pi != null && pi.PropertyType == typeof(bool) && pi.CanWrite && (bool)pi.GetValue(target)! != value)
            pi.SetValue(target, value);
    }

    private static void CollectAfterSwitch()
    {
        RPlayerController pc = Game.GetPlayerController(0);
        if (pc == null || pc.Pawn == null || pc.IsInBatmobile() || pc.IsBatmobileUnderRemoteControl() || CharacterBusy || TeleportBusy)
            return;

        try
        {
            Game.GetWorldInfo().ForceGarbageCollection(true);
        }
        catch (Exception ex)
        {
            Log("Cleanup after switch failed: " + ex.Message);
        }
    }

    private static void RegisterPawnAliases()
    {
        if (_aliasesDone)
            return;
        _aliasesDone = true;

        try
        {
            Type? init = typeof(Game).Assembly.GetType("BmSDK.Framework.StaticInit", false);
            if (init?.GetField("_classPathToManagedTypeMap", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is not IDictionary map)
                return;

            Type[] types;
            try
            {
                types = typeof(Game).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = Array.FindAll(ex.Types, t => t != null)!;
            }

            foreach (Type t in types)
            {
                if (!t.Name.StartsWith("MPawnPlayer", StringComparison.Ordinal) || !t.Name.EndsWith("Base", StringComparison.Ordinal))
                    continue;
                string path = "BmScript." + t.Name.Substring(0, t.Name.Length - 4);
                if (!map.Contains(path))
                    map[path] = t;
            }
        }
        catch (Exception ex)
        {
            Log("Character class aliases failed: " + ex.Message);
        }
    }

    private static void RememberLeak()
    {
        if (PlayingOther)
            Log("Left the game as " + Characters[_chCurrent].Label + ", Batman comes back on the next load.");
        _guardDone = false;
    }

    private static bool GuardLoadedCharacter()
    {
        if (_guardDone || CharacterBusy || SpawnedClasses.Length <= 1)
            return false;

        Pawn? pawn = Game.GetPlayerController(0)?.Pawn;
        if (pawn == null || !pawn.IsValid || pawn.GetPathName(null).StartsWith("frontend.", StringComparison.OrdinalIgnoreCase))
            return false;

        _guardDone = true;
        RGameInfo gi = Game.GetGameInfo();
        if (gi == null || !gi.IsValid || !gi.IsCoreStory() || gi.IsChallengeMode() || IsBatman(pawn) || PlayingOther)
            return false;

        if (!SpawnedClasses.Contains(";" + pawn.Class.GetPathName(null) + ";", StringComparison.OrdinalIgnoreCase))
            return false;

        Log("Loaded the story as " + pawn.GetPathName(null) + " instead of Batman, switching back to keep the save safe.");
        _chTarget = 0;
        _chRecovering = false;
        _chProtect = true;
        _chTimer = SlowFadeOut;
        _blackout = 1f;
        _chPhase = ChFadeOut;
        return true;
    }

    private static void ResetCharacter()
    {
        _chPhase = ChIdle;
        _chCurrent = 0;
        _chClass = "";
        _chAction = null;
        _chStart = null;
        _gcLeft = 0f;
        if (_chPick != null)
            _chPick.Choice = 0;
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.SaveGame))]
    private static void SaveGameRedirect(RPlayerController self, FString name, bool a, bool b)
    {
        if (PlayingOther || CharacterBusy)
        {
            Log("Save skipped: playing as " + Characters[_chCurrent].Label + ".");
            if (_saveToastLeft <= 0f)
            {
                _saveToastLeft = 30f;
                ShowNotice("SAVING PAUSED", "SWITCH BACK TO BATMAN TO SAVE", false);
            }

            return;
        }

        self.SaveGame(new FString(name.ToString() ?? ""), a, b);
    }
}
