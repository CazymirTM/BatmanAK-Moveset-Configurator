#nullable enable

using System;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace BatmanAssaultRifle;

internal static class RifleAnim
{
    public enum Want { None, Aim, Fire, Reload }

    private const float ReloadAnimStart = 0.4f;
    private const float ReloadAnimSpan = 3.0f;

    private static Want playing = Want.None;
    private static int overlayId = -1;
    private static float loopT;
    private static int replays;
    private static float replayWindow;

    public static float ReloadT;
    public static bool Guard;
    public static float AimFrame = 0.15f;
    private const float MinT = 0.02f;
    private const float BlendOutSeconds = 0.22f;
    private static bool ownStop;
    private static int blocked;

    private static bool ShouldBlock(RPawnCharacter self, string what)
    {
        try
        {
            if (!Guard || ownStop || playing == Want.None || !BatmanArsenalPack.Arsenal.Rifle || Rifle.Batman() is not RPawnPlayer b || b.Ptr != self.Ptr)
            {
                return false;
            }

            blocked++;
            ModLog.Once("block-" + what, "Anim: kept the rifle pose when the game called " + what + " during the aim.");

            return true;
        }
        catch
        {
            return false;
        }
    }

    [Redirect(typeof(RPawnCharacter), nameof(RPawnCharacter.StopWeaponOverlay))]
    private static void StopWeaponOverlayRedirect(RPawnCharacter self)
    {
        if (ShouldBlock(self, "StopWeaponOverlay")) return;
        self.StopWeaponOverlay();
    }

    [Redirect(typeof(RPawnCharacter), nameof(RPawnCharacter.StopAllOverlays))]
    private static void StopAllOverlaysRedirect(RPawnCharacter self)
    {
        if (ShouldBlock(self, "StopAllOverlays")) return;
        self.StopAllOverlays();
    }

    [Redirect(typeof(RPawnCharacter), nameof(RPawnCharacter.StopAllOverlaysInstantly))]
    private static void StopAllOverlaysInstantlyRedirect(RPawnCharacter self)
    {
        if (ShouldBlock(self, "StopAllOverlaysInstantly")) return;
        self.StopAllOverlaysInstantly();
    }

    public static void Reset()
    {
        addId = -1;
        playing = Want.None;
        overlayId = -1;
        loopT = 0f;
        replays = 0;
    }

    private static AnimSequence? SeqFor(Want w) => w switch
    {
        Want.Aim => RifleProps.FireSeq ?? RifleProps.AimSeq,
        Want.Fire => RifleProps.FireSeq ?? RifleProps.AimSeq,
        Want.Reload => RifleConfig.C.ReloadAnim ? RifleProps.ReloadSeq : RifleProps.AimSeq,
        _ => null,
    };

    public static void Stop(RPawnPlayer pawn)
    {
        if (playing == Want.None)
        {
            return;
        }

        try
        {
            if (overlayId >= 0 && pawn.IsValid && Present(pawn))
            {
                ownStop = true;
                pawn.StopWeaponOverlay();
            }
        }
        catch
        {
        }
        finally
        {
            ownStop = false;
        }

        playing = Want.None;
        overlayId = -1;
    }

    private static bool Present(RPawnPlayer pawn)
    {
        RAnimNode_Pose? node = pawn.Anim;

        if (node == null)
        {
            return false;
        }

        var arr = node.WeaponOverlays.PlayingOverlays;

        int pose = node.PosePlayer.LastTransitionId.Id;

        for (int k = 0; k < arr.Count; k++)
        {
            if (arr[k].Id == overlayId && !arr[k].State.Stopping)
            {
                if (arr[k].PoseId.Id != pose)
                {
                    ModLog.Once("anim-repose", "Anim: replaying the rifle pose on the new pose transition.");

                    return false;
                }

                return true;
            }
        }

        return false;
    }

    public static float RaiseBlend = 0.01f;

    private static int turnCheckedId = -1;

    private static bool IsTurn(string anim) => anim.StartsWith("Turn_") || anim.Contains("_Turn_");

    private static void ShortenTurn(RPawnPlayer pawn)
    {
        try
        {
            RAnimNode_Pose? node = pawn.Anim;

            if (node == null)
            {
                return;
            }

            var id = node.PosePlayer.LastTransitionId;

            if (id.Id == turnCheckedId)
            {
                return;
            }

            if (!node.HasTransition(id) || node.GetTransitionNormalizedTime(id) >= 1f)
            {
                turnCheckedId = id.Id;

                return;
            }

            if (pawn.CombatMove != null)
            {
                turnCheckedId = id.Id;
                ModLog.Once("anim-combat-skip", "Anim: left a combat move's transition alone (" + Util.ClassName(pawn.CombatMove) + ").");

                return;
            }

            AnimSequence? dominant = node.GetTransitionDominantAnim(id);
            string name = dominant == null ? "" : dominant.SequenceName.ToString();

            if (dominant != null && !IsTurn(name))
            {
                turnCheckedId = id.Id;

                return;
            }

            string stance = node.GetMovementStance().ToString();

            if (stance != "StandingBatarang" && stance != "CrouchingBatarang")
            {
                return;
            }

            turnCheckedId = id.Id;

            if (dominant == null)
            {
                name = "raise into " + stance;
            }

            float duration = node.GetTransitionDuration(id);

            if (duration > RaiseBlend + 0.01f)
            {
                node.SetTransitionDuration(id, RaiseBlend);
                turns++;
                ModLog.Once("anim-turn-" + (dominant == null ? "raise" : "turn"), "Anim: cut the Batarang aim's " + name + " (" + duration.ToString("0.00") + " s) to " + RaiseBlend.ToString("0.00") + " s so the rifle hold stays.");
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("anim-turn-failed", "Anim: shortening a turn failed: " + Util.Unwrap(exception));
        }
    }

    private static int turns;

    private static void ShortenRaise(RPawnPlayer pawn, Want want, float dt)
    {
        if (want == Want.None)
        {
            return;
        }

        ShortenTurn(pawn);
    }

    public static void Update(RPawnPlayer pawn, Want want, float dt)
    {
        ShortenRaise(pawn, want, dt);
        if (want == Want.None)
        {
            Stop(pawn);

            return;
        }

        AnimSequence? seq = SeqFor(want);

        if (seq == null || seq.SequenceLength <= 0f)
        {
            Stop(pawn);

            return;
        }

        replayWindow -= dt;

        if (replayWindow <= 0f)
        {
            replayWindow = 1f;
            replays = 0;
        }

        bool present = overlayId >= 0 && Present(pawn);

        if (present && playing != want && (SeqFor(playing) == seq || Swap(pawn, seq)))
        {
            if (want != Want.Reload)
            {
                loopT = MinT;
            }

            playing = want;
        }
        else if (!present)
        {
            if (replays > 12)
            {
                ModLog.Once("anim-fight", "Anim: something keeps stopping the rifle pose; waiting.");

                return;
            }

            replays++;

            var cfg = new RAnimConfig.FCustomAnimConfig();

            cfg.CustomUpperBodyAnim = seq;
            cfg.ModelspaceUpperBody = true;

            try
            {
                overlayId = pawn.PlayCustomWeaponOverlay(cfg).AnimId;
            }
            catch (Exception exception)
            {
                ModLog.Once("anim-failed", "Anim: playing the rifle pose failed: " + Util.Unwrap(exception));
                overlayId = -1;

                return;
            }

            if (playing != want && want != Want.Reload)
            {
                loopT = MinT;
            }

            playing = want;
        }

        float len = seq.SequenceLength;
        float norm;

        if (want == Want.Reload && RifleConfig.C.ReloadAnim)
        {
            float span = Math.Max(0.5f, RifleConfig.C.ReloadTime);

            norm = Math.Clamp((ReloadAnimStart + ReloadT * (ReloadAnimSpan / span)) / len, MinT, Math.Clamp(1f - BlendOutSeconds / len, 0.5f, 0.97f));
        }
        else if (want == Want.Aim && RifleProps.FireSeq != null)
        {
            loopT = MinT;
            norm = AimFrame;

        }
        else
        {
            if (loopT < MinT)
            {
                loopT = MinT;
            }

            loopT += dt / len;

            float maxT = Math.Clamp(1f - BlendOutSeconds / len, MinT + 0.1f, 0.95f);

            if (loopT >= maxT)
            {
                loopT = MinT + (loopT - maxT);
            }

            norm = loopT;
        }

        SetTime(pawn, norm);
    }

    private static bool Swap(RPawnPlayer pawn, AnimSequence seq)
    {
        RAnimNode_Pose? node = pawn.Anim;

        if (node == null)
        {
            return false;
        }

        var arr = node.WeaponOverlays.PlayingOverlays;

        for (int k = 0; k < arr.Count; k++)
        {
            var e = arr[k];

            if (e.Id != overlayId)
            {
                continue;
            }

            var anim = e.CustomAnim;

            anim.CustomUpperBodyAnim = seq;
            e.CustomAnim = anim;
            arr[k] = e;

            return true;
        }

        return false;
    }

    private static void SetTime(RPawnPlayer pawn, float norm)
    {
        RAnimNode_Pose? node = pawn.Anim;

        if (node == null)
        {
            return;
        }

        var arr = node.WeaponOverlays.PlayingOverlays;

        for (int k = 0; k < arr.Count; k++)
        {
            var e = arr[k];

            if (e.Id != overlayId)
            {
                continue;
            }

            var st = e.State;
            var t = st.Time;

            t.Current = norm;
            t.Previous = norm;
            st.Time = t;
            e.State = st;
            arr[k] = e;

            return;
        }
    }

    private static int addId = -1;
    private static float addT;
    private static int addReplays;
    private static float addWindow;

    public static void UpdateRecoil(RPawnPlayer pawn, bool on, float dt)
    {
        AnimSequence? seq = RifleProps.FireSeq;

        if (!on || seq == null || seq.SequenceLength <= 0f)
        {
            StopRecoil(pawn);

            return;
        }

        addWindow -= dt;

        if (addWindow <= 0f)
        {
            addWindow = 1f;
            addReplays = 0;
        }

        if (addId < 0 || !RecoilPresent(pawn))
        {
            if (addReplays++ > 12)
            {
                return;
            }

            var cfg = new RAnimConfig.FCustomAdditiveAnimConfig();

            cfg.CustomAddAnim = seq;
            cfg.CustomSubtractAnim = seq;
            cfg.SubtractMode = RAnimConfig.EAdditiveSubtractMode.ASM_FirstFrame;

            try
            {
                addId = pawn.PlayCustomAdditiveOverlay(cfg).AnimId;
                addT = MinT;
            }
            catch (Exception exception)
            {
                ModLog.Once("recoil-failed", "Anim: playing the recoil layer failed: " + Util.Unwrap(exception));
                addId = -1;

                return;
            }
        }

        float len = seq.SequenceLength;
        float maxT = Math.Clamp(1f - BlendOutSeconds / len, MinT + 0.1f, 0.95f);

        addT += dt / len;

        if (addT >= maxT)
        {
            addT = MinT + (addT - maxT);
        }

        RAnimNode_Pose? node = pawn.Anim;

        if (node == null)
        {
            return;
        }

        var arr = node.AdditiveOverlays.PlayingOverlays;

        for (int k = 0; k < arr.Count; k++)
        {
            var e = arr[k];

            if (e.Id != addId)
            {
                continue;
            }

            var st = e.State;
            var t = st.Time;

            t.Current = addT;
            t.Previous = addT;
            st.Time = t;
            e.State = st;
            arr[k] = e;

            return;
        }
    }

    private static bool RecoilPresent(RPawnPlayer pawn)
    {
        RAnimNode_Pose? node = pawn.Anim;

        if (node == null)
        {
            return false;
        }

        var arr = node.AdditiveOverlays.PlayingOverlays;

        for (int k = 0; k < arr.Count; k++)
        {
            if (arr[k].Id == addId && !arr[k].State.Stopping)
            {
                return true;
            }
        }

        return false;
    }

    public static void StopRecoil(RPawnPlayer pawn)
    {
        if (addId < 0)
        {
            return;
        }

        try
        {
            if (pawn.IsValid && RecoilPresent(pawn))
            {
                pawn.StopAdditiveOverlay();
            }
        }
        catch
        {
        }

        addId = -1;
    }

    public static string Report() => playing + " (overlay " + overlayId + ", game stops blocked " + blocked + ", turns cut " + turns + ")";
}
