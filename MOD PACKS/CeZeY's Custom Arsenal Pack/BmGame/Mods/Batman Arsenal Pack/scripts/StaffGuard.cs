using System;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace BatmanRobinStaff;

internal static class StaffGuard
{
    private const float MoveLimit = 8f;
    private const float DeadMoveLimit = 0.5f;
    private const long CounterProtectMs = 350;

    private static IntPtr watchedMove = IntPtr.Zero;
    private static float moveTime;
    private static float deadMoveTime;
    private static IntPtr trackedCounter = IntPtr.Zero;
    private static long counterStart;

    [Redirect(typeof(RCombatMove_VillainSimultaneousAttack), nameof(RCombatMove_VillainSimultaneousAttack.DoThrowCounterDir))]
    private static int DoThrowCounterDirRedirect(RCombatMove_VillainSimultaneousAttack self, Vector3 direction)
    {
        int result = self.DoThrowCounterDir(direction);

        if (result < 0 || !StaffMoves.RobinActive)
        {
            return result;
        }

        ModLog.Info("Counters: Robin has no throw double counter, playing the normal one.");

        return -1;
    }

    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.CanDoSpecialMove))]
    private static bool CanDoSpecialRedirect(RPlayerControllerCombat self) => !CounterJustStarted(self.Pawn, "special") && self.CanDoSpecialMove();

    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.CanDoPrimaryGroupCombatMove))]
    private static bool CanDoPrimaryRedirect(RPlayerControllerCombat self, bool flag) => !CounterJustStarted(self.Pawn, "primary") && self.CanDoPrimaryGroupCombatMove(flag);

    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.CanDoSecondaryGroupCombatMove))]
    private static bool CanDoSecondaryRedirect(RPlayerControllerCombat self, bool flag) => !CounterJustStarted(self.Pawn, "secondary") && self.CanDoSecondaryGroupCombatMove(flag);

    private static bool CounterJustStarted(Pawn? pawn, string kind)
    {
        if (pawn is not RPawnPlayer player || player.CombatMove is not RCombatMove_BatmanCounter counter)
        {
            return false;
        }

        if (counter.Ptr == trackedCounter && Environment.TickCount64 - counterStart >= CounterProtectMs)
        {
            return false;
        }

        ModLog.Once("counter-protect:" + kind, "Counters: kept the counter that had just started; refused a " + kind + " combo move.");

        return true;
    }

    public static void Track(RPawnPlayer pawn)
    {
        if (pawn.CombatMove is not RCombatMove_BatmanCounter counter)
        {
            trackedCounter = IntPtr.Zero;
        }
        else if (counter.Ptr != trackedCounter)
        {
            trackedCounter = counter.Ptr;
            counterStart = Environment.TickCount64;
        }
    }

    public static void Tick(RPlayerController controller, RPawnPlayer pawn, float dt)
    {
        RCombatMove? move = pawn.CombatMove;

        if (move is not (RCombatMove_BatmanCounter or RCombatMove_VillainSimultaneousAttack))
        {
            watchedMove = IntPtr.Zero;
            moveTime = 0f;
            deadMoveTime = 0f;

            return;
        }

        if (move.Ptr != watchedMove)
        {
            watchedMove = move.Ptr;
            moveTime = 0f;
            deadMoveTime = 0f;
        }

        moveTime += dt;
        deadMoveTime = move.GetStateName().ToString() == "None" ? deadMoveTime + dt : 0f;

        if (deadMoveTime >= DeadMoveLimit || moveTime >= MoveLimit)
        {
            ModLog.Info("Guard: " + move.Class.Name + " held Batman for " + moveTime.ToString("0.0") + " s, ending it.");
            pawn.CombatMoveFinished(move);
            watchedMove = IntPtr.Zero;
            moveTime = 0f;
            deadMoveTime = 0f;
        }
    }
}
