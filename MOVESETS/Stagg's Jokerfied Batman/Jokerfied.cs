using BmSDK;
using BmSDK.BmGame;
using BmSDK.BmScript;

[Script]
public class MakeBatmanJokerfied : Script
{
    bool forceSlowMo = false;
    long lastCheck = 0;
    long lastGrabCheck = 0;
    static bool forceThrow = true;
    // Plain values only: we never keep game objects between frames, because the
    // game can free them (for example when a fight ends) and touching a freed
    // object crashes the game.
    static bool grabActive = false;
    static string activeGrabName = "";
    static System.Numerics.Vector3 grabVillainPos;

    // Grab logging state
    static string lastForcedFor = "";
    string trackedName = "";
    long trackedStart = 0;
    bool trackedCounterLogged = false;

    // Auto-arm state
    string lastPawnName = null;
    bool armPending = false;
    long armAt = 0;
    const long ArmDelayMs = 5500;

    // The folder this script file lives in (filled in by the compiler).
    static string GetScriptFolder([System.Runtime.CompilerServices.CallerFilePath] string file = "")
    {
        try
        {
            if (!string.IsNullOrEmpty(file))
                return System.IO.Path.GetDirectoryName(file);
        }
        catch
        {
        }
        return null;
    }

    static string ResolveLogPath()
    {
        string folder = GetScriptFolder();
        if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder))
            folder = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop);
        return System.IO.Path.Combine(folder, "BmSDK_log.txt");
    }

    static readonly string logPath = ResolveLogPath();

    // Writes a line to BmSDK_log.txt next to this script (falls back to your Desktop)
    static void Log(string message)
    {
        Debug.Log(message);
        try
        {
            System.IO.File.AppendAllText(
                logPath,
                System.DateTime.Now.ToString("HH:mm:ss") + "  " + message + "\n"
            );
        }
        catch
        {
            // ignore file errors so the script never crashes the game
        }
    }

    // Force throw counters on group attacks: if the game found no stick-direction
    // target, pick the first villain that hasn't been thrown yet.
    [Redirect(typeof(RCombatMove_VillainSimultaneousAttack), nameof(RCombatMove_VillainSimultaneousAttack.DoThrowCounterDir))]
    static int ForceThrowRedirect(RCombatMove_VillainSimultaneousAttack self, System.Numerics.Vector3 testDir)
    {
        int result = self.DoThrowCounterDir(testDir);
        if (result != -1 || !forceThrow)
            return result;

        var villains = self.VillainList.AsSpan();
        for (int i = 0; i < villains.Length; i++)
        {
            if (!villains[i].bCriticaled)
                return i;
        }
        return -1;
    }

    static string lastPipeSpoofLogged = "";

    // Parses an enum value by name without having to spell out the enum's type.
    static T ParseLike<T>(T sample, string name)
    {
        return (T)System.Enum.Parse(typeof(T), name);
    }

    // The game only allows throw counters on groups whose behaviour type is the plain
    // thug type. Pipe thug groups fail that check, so DoThrowCounterDir is never even
    // asked. Before the game checks, report pipe groups as plain thugs.
    // Returns true if the group was a pipe group.
    static bool AllowPipeThrow(RCombatMove_VillainSimultaneousAttack self)
    {
        if (!forceThrow)
            return false;

        var info = self.SCInfo;
        if (info.BehaviourType.ToString() != "SCBT_ThugPipe")
            return false;

        info.BehaviourType = ParseLike(info.BehaviourType, "SCBT_Thug");
        self.SCInfo = info;

        if (lastPipeSpoofLogged != self.Name)
        {
            lastPipeSpoofLogged = self.Name;
            Log("Group " + self.Name + ": pipe group reported as thug so the throw check passes");
        }
        return true;
    }

    // The throw decision is made at the start of the counter...
    [Redirect(typeof(RCombatMove_VillainSimultaneousAttack), nameof(RCombatMove_VillainSimultaneousAttack.StartCounter))]
    static void PipeStartCounter(RCombatMove_VillainSimultaneousAttack self, float timeInitiatedByPlayer)
    {
        bool pipe = AllowPipeThrow(self);

        self.StartCounter(timeInitiatedByPlayer);

        if (pipe)
            Log("Group " + self.Name + " pipe StartCounter -> bThrow=" + self.bThrow);
    }

    // ...and checked again for every villain countered, so it has to be applied here too
    // (the game replaces the behaviour type right after the first check).
    [Redirect(typeof(RCombatMove_VillainSimultaneousAttack), nameof(RCombatMove_VillainSimultaneousAttack.IncCountered))]
    static void PipeIncCountered(RCombatMove_VillainSimultaneousAttack self)
    {
        AllowPipeThrow(self);
        self.IncCountered();
    }

    // Force throw counters on single-thug grabs: while Batman is in a thug grab,
    // report the stick as pointing at the thug so the throw check passes.
    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.InputHeading))]
    static System.Numerics.Vector3 ForceGrabHeading(RPlayerControllerCombat self, bool bFlag)
    {
        var result = self.InputHeading(bFlag);
        if (!forceThrow)
            return result;

        var player = Game.GetPlayerPawn() as RPawnPlayer;
        if (player == null)
            return result;

        // The active grab is tracked in OnTick (searching here would run every frame).
        // We can't rely on player.CombatMove: it only becomes the grab once the thug
        // has actually grabbed Batman, so an early counter would miss it.
        if (!grabActive)
            return result;

        var toThug = grabVillainPos - player.Location;
        if (toThug.LengthSquared() < 0.01f)
            return result;

        if (activeGrabName != lastForcedFor)
        {
            lastForcedFor = activeGrabName;
            Log("Grab " + activeGrabName + ": forced heading toward thug (your real stick input length was "
                + result.Length().ToString("0.00") + ")");
        }

        return System.Numerics.Vector3.Normalize(toThug);
    }

    // Player controller flags, movement speed and attack value
    static void ApplyJokerfiedFlags(RPawnPlayer batman)
    {
        var pc = batman.PlayerController;
        if (pc != null)
        {
            pc.bJokerfiedBatman = false;
            pc.bAllowInteractionMidCombat = true;

            if (pc.ModularHud != null && pc.ModularHud.HealthBarModule != null)
                pc.ModularHud.HealthBarModule.bJokerfied = true;
        }

        batman.MovementSpeedModifier = 1.0f;
        batman.JokerfiedAttacks = 99.0f;
    }

    // Spawn and give the Jokerfied weapon
    static void GiveJokerfiedWeapon(RPawnPlayer batman)
    {
        var weapon = batman.Spawn(
            RBMWeaponJokerfiedBatman.StaticClass(),
            batman,
            default(FName),
            batman.Location,
            batman.Rotation,
            null,
            true
        ) as RBMWeaponJokerfiedBatman;

        if (weapon != null)
            batman.InvManager.AddInventory(weapon);
    }

    // Everything the mod needs, applied automatically after joining a save
    void ArmMod(RPawnPlayer batman)
    {
        ApplyJokerfiedFlags(batman);
        GiveJokerfiedWeapon(batman);
        forceSlowMo = true;
        Log("Mod armed (Jokerfied flags, weapon, slow-mo counters)");
    }

    public override void OnTick()
    {
        long now = System.Environment.TickCount64;

        RPawnPlayer player = null;
        try
        {
            player = Game.GetPlayerPawn() as RPawnPlayer;
        }
        catch
        {
            // no player yet (menu or loading screen)
        }

        // Detect joining a save: the player pawn appears or changes
        string pawnName = player != null ? player.Name : null;
        if (pawnName != lastPawnName)
        {
            lastPawnName = pawnName;
            if (pawnName != null)
            {
                armPending = true;
                armAt = now + ArmDelayMs;
                Log("Player detected, arming in " + (ArmDelayMs / 1000.0) + "s");
            }
            else
            {
                armPending = false;
            }
        }

        if (armPending && player != null && now >= armAt)
        {
            armPending = false;
            ArmMod(player);
        }

        // Every 50ms: make grabs and group attacks "critical" and make sure the batsuit counts as upgraded
        if (forceThrow && player != null && now - lastGrabCheck >= 50)
        {
            lastGrabCheck = now;

            if (!player.bUpgradedBatsuit)
                player.bUpgradedBatsuit = true;

            bool foundActive = false;
            string foundName = "";
            System.Numerics.Vector3 foundPos = default(System.Numerics.Vector3);
            bool foundCritical = false;
            bool trackedStillExists = false;

            foreach (var grab in Game.FindObjects<RCombatMove_VillainUnarmedGrab>())
            {
                if (!grab.bCritical)
                    grab.bCritical = true;

                // Log when the tracked grab gets countered
                if (grab.Name == trackedName)
                {
                    trackedStillExists = true;
                    if (!trackedCounterLogged && grab.bCountered)
                    {
                        trackedCounterLogged = true;
                        Log("Grab countered: " + trackedName + " after " + (now - trackedStart)
                            + "ms | bCritical=" + grab.bCritical
                            + " | forced heading used=" + (lastForcedFor == trackedName));
                    }
                }

                // Is this the grab that is happening right now?
                if (!grab.bCountered && grab.Villain != null)
                {
                    var villainMove = grab.Villain.CombatMove;
                    if (villainMove != null && villainMove.Name == grab.Name)
                    {
                        foundActive = true;
                        foundName = grab.Name;
                        foundPos = grab.Villain.Location;
                        foundCritical = grab.bCritical;
                    }
                }
            }

            if (!trackedStillExists)
                trackedName = "";

            grabActive = foundActive;
            activeGrabName = foundName;
            grabVillainPos = foundPos;

            // Log when a new grab becomes active
            if (foundActive && foundName != trackedName)
            {
                trackedName = foundName;
                trackedStart = now;
                trackedCounterLogged = false;
                Log("Grab started: " + foundName + " | bCritical=" + foundCritical
                    + " | bUpgradedBatsuit=" + player.bUpgradedBatsuit
                    + " | forceThrow=" + forceThrow);
            }
        }

        if (!forceSlowMo)
            return;

        if (now - lastCheck < 50)
            return;
        lastCheck = now;

        foreach (var move in Game.FindObjects<RCombatMove_BatmanCounter>())
        {
            if (!move.bSlowMo)
            {
                move.bSlowMo = true;
                Log("Fixed " + move.Name);
            }
        }
    }
}