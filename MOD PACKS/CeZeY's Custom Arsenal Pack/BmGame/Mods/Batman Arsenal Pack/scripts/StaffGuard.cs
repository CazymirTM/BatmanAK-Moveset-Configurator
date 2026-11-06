using BmSDK;
using BmSDK.BmGame;

namespace BatmanRobinStaff;

internal static class StaffGuard
{
    private const float MoveLimit = 8f;
    private const float InputLimit = 5f;
    private const float RecentCounter = 15f;

    private static IntPtr watchedMove = IntPtr.Zero;
    private static float moveTime;
    private static float inputTime;
    private static float sinceCounter = float.MaxValue;

    public static void Tick(RPlayerController controller, RPawnPlayer pawn, float dt)
    {
        RCombatMove? move = pawn.CombatMove;
        bool counter = move is RCombatMove_BatmanCounter or RCombatMove_VillainSimultaneousAttack;

        sinceCounter = counter ? 0f : sinceCounter + dt;

        if (counter && move != null)
        {
            if (move.Ptr != watchedMove)
            {
                watchedMove = move.Ptr;
                moveTime = 0f;
            }

            moveTime += dt;

            if (moveTime >= MoveLimit)
            {
                ModLog.Info("Guard: " + move.Class.Name + " ran " + moveTime.ToString("0.0") + " s, ending it.");
                pawn.CombatMoveFinished(move);
                watchedMove = IntPtr.Zero;
                moveTime = 0f;
            }
        }
        else
        {
            watchedMove = IntPtr.Zero;
            moveTime = 0f;
        }

        if (sinceCounter > RecentCounter || pawn.CurrentSpecialMove != null || pawn.IsInBatmobile() || controller.bCinematicMode)
        {
            inputTime = 0f;

            return;
        }

        inputTime = controller.IsMoveInputIgnored() || controller.IsLookInputIgnored() ? inputTime + dt : 0f;

        if (inputTime >= InputLimit)
        {
            ModLog.Info("Guard: input stayed locked after a counter, releasing it.");

            for (int i = 0; i < 8 && controller.IsMoveInputIgnored(); i++)
            {
                controller.IgnoreMoveInput(false);
            }

            for (int i = 0; i < 8 && controller.IsLookInputIgnored(); i++)
            {
                controller.IgnoreLookInput(false);
            }

            inputTime = 0f;
        }
    }
}
