using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;


[Script]
public class VigilanteVenomizedUnbound : Script
{
    private static VigilanteVenomizedUnbound _instance;


    private const string CONSOLE_EXEC_COMMAND = "exec Console.txt";

    private const string BOOTSTRAP_PACKAGE = "Playable_DLCRedHood_SF";
    private const string BOOTSTRAP_CLASS = "Playable_DLCRedHood";
    private const string BOOTSTRAP_NAME = "DLCRedHood";

    private const string BATMAN_PACKAGE = "Playable_Batman_Std_SF";
    private const string BATMAN_CLASS = "Playable_Batman";
    private const string BATMAN_NAME = "Batman";


    private static readonly string[] BootstrapPackagesToRootAfterSwitch =
    {
        "Playable_DLCRedHood_Std_SF",
        "Playable_DLCRedHood_SF"
    };


    private static readonly string[] PreloadPlayers =
    {
        "Playable_Batman",
        "Playable_Joker",
        "Playable_JokerFPS",
        "Playable_DLCHarleyQuinn",
        "Playable_DLCBatGirl",
        "Playable_Robin0",
        "Playable_Catwoman0",
        "Playable_DLCRedHood",
        "Playable_Nightwing0",
        "Playable_Azrael"
    };


    private enum InitState
    {
        WaitingForBatman,
        WaitingAfterBootstrapSwitch,
        WaitingAfterBatmanSwitch,
        WaitingAfterPreload,
        ApplyingConsole,
        Ready
    }

    private static readonly TimeSpan NativeSwitchDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PreloadSettleDelay = TimeSpan.FromMilliseconds(1500);

    private string _logPath = "";
    private InitState _state = InitState.WaitingForBatman;
    private DateTime _switchReadyAt = DateTime.MinValue;
    private DateTime _preloadReadyAt = DateTime.MinValue;
    private DateTime _nextReadyCheck = DateTime.MinValue;
    private DateTime _nextDeferredLog = DateTime.MinValue;

    private bool _bootstrapDone = false;
    private bool _bootstrapInProgress = false;
    private bool _bootstrapStartedThisSession = false;
    private bool _playersPreloaded = false;
    private bool _consolePrimedDuringBootstrap = false;
    private nint _lastAppliedBatmanPtr = 0;

    private RSeqAct_SwitchPlayerCharacter _activeSwitch;
    private PlayerStart _activePlayerStart;

    public override void Main()
    {
        _instance = this;
        InitLog();
        Log("VigilanteVenomizedUnbound started.");
        Log("Console file: Binaries\\Console.txt (command: \"" + CONSOLE_EXEC_COMMAND + "\")");
        Log("Bootstrap character: " + BOOTSTRAP_NAME + " (" + BOOTSTRAP_CLASS + ")");
        Log("No manual challenge-map injecting is required. Everything below is automatic.");
        _state = InitState.WaitingForBatman;
    }

    public override void OnEnterGame()
    {
        if (_bootstrapInProgress)
        {
            Log("OnEnterGame during native character switch ignored. Current bootstrap continues.");
            return;
        }

        _lastAppliedBatmanPtr = 0;
        _nextReadyCheck = DateTime.MinValue;

        if (_bootstrapDone)
        {
            _state = InitState.ApplyingConsole;
            Log("OnEnterGame: bootstrap already completed this session. Re-applying Console.txt.");
        }
        else if (!_bootstrapStartedThisSession)
        {
            _state = InitState.WaitingForBatman;
            Log("OnEnterGame: one-time native bootstrap switch queued.");
        }
        else
        {
            Log("OnEnterGame: bootstrap already started this session. Duplicate bootstrap suppressed.");
        }
    }

    public override void OnTick()
    {
        try
        {
            var controller = Game.GetPlayerController();
            if (controller == null || !controller.IsValid)
                return;

            switch (_state)
            {
                case InitState.WaitingForBatman:
                    StartBootstrapWhenBatmanReady(controller);
                    break;

                case InitState.WaitingAfterBootstrapSwitch:
                    FinishBootstrapSwitch(controller);
                    break;

                case InitState.WaitingAfterBatmanSwitch:
                    WaitForBatmanReturn(controller);
                    break;

                case InitState.WaitingAfterPreload:
                    FinishPreloadThenReturnToBatman(controller);
                    break;

                case InitState.ApplyingConsole:
                    ApplyConsoleWhenBatmanReady(controller);
                    break;

                case InitState.Ready:
                    MonitorBatmanPawn(controller);
                    break;
            }
        }
        catch (Exception ex)
        {
            LogOncePerSecond("OnTick deferred safely: " + ex.Message);
        }
    }

    private void StartBootstrapWhenBatmanReady(RPlayerController controller)
    {
        if (_bootstrapInProgress || _bootstrapDone)
            return;

        RPawnPlayerBmBase batman = TryGetBatman();
        if (!IsUsableBatman(batman))
            return;

        if (batman.InvManager == null || !batman.InvManager.IsValid)
            return;

        _bootstrapInProgress = true;
        _bootstrapStartedThisSession = true;

        Log("Batman ready. Starting the single native switch to " + BOOTSTRAP_NAME + ".");

        if (!BeginCharacterSwitch(controller, BOOTSTRAP_PACKAGE, BOOTSTRAP_CLASS, BOOTSTRAP_NAME))
        {
            _bootstrapInProgress = false;
            _bootstrapStartedThisSession = false;
            LogOncePerSecond("Native bootstrap switch could not start yet.");
            return;
        }

        _switchReadyAt = DateTime.UtcNow + NativeSwitchDelay;
        _state = InitState.WaitingAfterBootstrapSwitch;
        Log("Bootstrap switch command sent. Waiting for native stabilization.");
    }

    private void FinishBootstrapSwitch(RPlayerController controller)
    {
        if (DateTime.UtcNow < _switchReadyAt)
            return;

        Log("Bootstrap stabilization window complete. " + BOOTSTRAP_NAME + " native pawn is active.");
        RootBootstrapPackagesNowLoaded();

        if (!_playersPreloaded)
        {
            Log("Preloading the original challenge player set while bootstrap character is active.");
            PreloadPlayerRoster();
            _playersPreloaded = true;
            _preloadReadyAt = DateTime.UtcNow + PreloadSettleDelay;
            _state = InitState.WaitingAfterPreload;
            Log("Preload sent. Waiting before bootstrap-side Console resolve.");
            return;
        }

        PrimeConsoleWhileBootstrapThenReturnBatman(controller);
    }

    private void FinishPreloadThenReturnToBatman(RPlayerController controller)
    {
        if (DateTime.UtcNow < _preloadReadyAt)
            return;

        Log("Player preload window complete.");
        PrimeConsoleWhileBootstrapThenReturnBatman(controller);
    }

    private void PrimeConsoleWhileBootstrapThenReturnBatman(RPlayerController controller)
    {
        if (!_consolePrimedDuringBootstrap)
        {
            Log("Resolving Binaries\\Console.txt while bootstrap character is active.");
            controller.ConsoleCommand(CONSOLE_EXEC_COMMAND, true);
            _consolePrimedDuringBootstrap = true;
            Log("Bootstrap-side Console resolve issued. Starting Batman return.");
        }

        if (!BeginCharacterSwitch(controller, BATMAN_PACKAGE, BATMAN_CLASS, BATMAN_NAME))
        {
            LogOncePerSecond("Switch back to Batman could not start yet.");
            return;
        }

        _switchReadyAt = DateTime.UtcNow + NativeSwitchDelay;
        _state = InitState.WaitingAfterBatmanSwitch;
        Log("Batman return command sent. Waiting before Batman-side Console apply.");
    }

    private void WaitForBatmanReturn(RPlayerController controller)
    {
        if (DateTime.UtcNow < _switchReadyAt)
            return;

        RPawnPlayerBmBase batman = TryGetBatman();
        if (!IsUsableBatman(batman))
            return;

        if (batman.InvManager == null || !batman.InvManager.IsValid)
            return;

        _bootstrapDone = true;
        _bootstrapInProgress = false;
        _state = InitState.ApplyingConsole;
        Log("Batman is live again. Bootstrap complete; applying Console.txt to Batman.");
        ApplyConsoleWhenBatmanReady(controller);
    }

    private void ApplyConsoleWhenBatmanReady(RPlayerController controller)
    {
        RPawnPlayerBmBase batman = TryGetBatman();
        if (!IsUsableBatman(batman))
            return;

        if (batman.InvManager == null || !batman.InvManager.IsValid)
            return;

        if (!_playersPreloaded)
        {
            Log("Preloading the original challenge player set.");
            PreloadPlayerRoster();
            _playersPreloaded = true;
            _preloadReadyAt = DateTime.UtcNow + PreloadSettleDelay;
            _state = InitState.WaitingAfterPreload;
            Log("Preload sent. Waiting before applying Console.txt.");
            return;
        }

        Log("Applying Binaries\\Console.txt to Batman Ptr=0x" + batman.Ptr.ToString("X"));
        controller.ConsoleCommand(CONSOLE_EXEC_COMMAND, true);

        _lastAppliedBatmanPtr = batman.Ptr;
        _state = InitState.Ready;
        _nextReadyCheck = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);

        Log("VigilanteVenomizedUnbound: Console.txt applied automatically. No manual steps were needed.");
    }

    private void MonitorBatmanPawn(RPlayerController controller)
    {
        if (DateTime.UtcNow < _nextReadyCheck)
            return;

        _nextReadyCheck = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);

        RPawnPlayerBmBase batman = TryGetBatman();
        if (!IsUsableBatman(batman))
            return;

        if (batman.Ptr != _lastAppliedBatmanPtr)
        {
            Log("Batman pawn instance changed. Re-applying Console.txt without repeating the bootstrap.");
            _state = InitState.ApplyingConsole;
        }
    }

    private bool BeginCharacterSwitch(
        RPlayerController controller,
        string packageName,
        string playerClassName,
        string characterName)
    {
        try
        {
            WorldInfo worldInfo = Game.GetWorldInfo();
            RGameInfo gameInfo = Game.GetGameInfo();

            if (worldInfo == null || !worldInfo.IsValid ||
                gameInfo == null || !gameInfo.IsValid)
                return false;

            Sequence gameSequence = worldInfo.GetGameSequence();
            if (gameSequence == null || !gameSequence.IsValid)
                return false;

            Game.LoadPackage(packageName);
            gameInfo.LoadPC(playerClassName);

            _activePlayerStart = Game.SpawnActor<PlayerStart>(
                Name: default,
                Location: controller.Location,
                Rotation: controller.Rotation
            );

            if (_activePlayerStart == null || !_activePlayerStart.IsValid)
                return false;

            _activeSwitch = new RSeqAct_SwitchPlayerCharacter(gameSequence);
            if (_activeSwitch == null || !_activeSwitch.IsValid)
                return false;

            _activeSwitch.ParentSequence = gameSequence;
            _activeSwitch.CharacterName = characterName;
            _activeSwitch.PlayerStartPoint = _activePlayerStart;
            _activeSwitch.SwitchToThisPawn = null;

            controller.PrepareForPlayerSwitch();
            _activeSwitch.ForceActivateInput(0);
            return true;
        }
        catch (Exception ex)
        {
            Log("Character switch error: " + ex.Message);
            return false;
        }
    }

    private void RootBootstrapPackagesNowLoaded()
    {
        int ok = 0;
        int fail = 0;

        foreach (string packageName in BootstrapPackagesToRootAfterSwitch)
        {
            try
            {
                var package = Game.LoadPackage(packageName);
                if (package == null || !package.IsValid)
                {
                    fail++;
                    Log("ROOT FAIL: " + packageName);
                    continue;
                }

                package.AddToRoot();
                ok++;
                Log("ROOT OK: " + packageName);
            }
            catch (Exception ex)
            {
                fail++;
                Log("ROOT FAIL: " + packageName + " | " + ex.Message);
            }
        }

        Log("Bootstrap playable-package root pass: OK=" + ok + " FAIL=" + fail);
    }

    private void PreloadPlayerRoster()
    {
        try
        {
            RGameInfo gameInfo = Game.GetGameInfo();
            if (gameInfo == null || !gameInfo.IsValid)
            {
                Log("Player preload skipped: RGameInfo unavailable.");
                return;
            }

            int ok = 0;
            int fail = 0;
            foreach (string playerClass in PreloadPlayers)
            {
                try
                {
                    gameInfo.LoadPC(playerClass);
                    ok++;
                    Log("PRELOAD PC: " + playerClass);
                }
                catch (Exception ex)
                {
                    fail++;
                    Log("PRELOAD FAIL: " + playerClass + " | " + ex.Message);
                }
            }

            Log("Original DLC_loading_phase player preload: OK=" + ok + " FAIL=" + fail);
        }
        catch (Exception ex)
        {
            Log("Player preload error: " + ex.Message);
        }
    }

    private static RPawnPlayerBmBase TryGetBatman()
    {
        try
        {
            RPawnPlayer pawn = Game.GetPlayerPawn(0);
            if (pawn == null || !pawn.IsValid || pawn.Ptr == 0 || pawn.IsClassDefaultObject)
                return null;

            return pawn as RPawnPlayerBmBase;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUsableBatman(RPawnPlayerBmBase pawn)
    {
        return pawn != null &&
               pawn.IsValid &&
               pawn.Ptr != 0 &&
               !pawn.IsClassDefaultObject;
    }

    private void LogOncePerSecond(string text)
    {
        if (DateTime.UtcNow < _nextDeferredLog)
            return;

        _nextDeferredLog = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        Log(text);
    }

    private void InitLog()
    {
        try
        {
            _logPath = Path.Combine(Mod.DirectoryPath, "VigilanteVenomizedUnbound.log");
            File.WriteAllText(
                _logPath,
                "VigilanteVenomizedUnbound " +
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                Environment.NewLine
            );
        }
        catch
        {
            _logPath = "";
        }
    }

    private void Log(string text)
    {
        Debug.Log("[VigilanteVenomizedUnbound] " + text);

        if (String.IsNullOrEmpty(_logPath))
            return;

        try
        {
            File.AppendAllText(
                _logPath,
                DateTime.Now.ToString("HH:mm:ss.fff") +
                " | " + text + Environment.NewLine
            );
        }
        catch
        {
        }
    }
}
