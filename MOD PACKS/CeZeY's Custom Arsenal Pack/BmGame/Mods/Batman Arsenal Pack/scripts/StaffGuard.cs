using System;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Framework;

namespace BatmanRobinStaff;

internal static class StaffGuard
{
    private const float MoveLimit = 8f;
    private const float DeadMoveLimit = 0.5f;

    private static IntPtr watchedMove = IntPtr.Zero;
    private static float moveTime;
    private static float deadMoveTime;

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
