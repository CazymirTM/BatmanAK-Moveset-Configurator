#nullable enable

using System;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.BmScript;
using BmSDK.Engine;
using BmSDK.Framework;

namespace BatmanArsenalZipKick;

[Script]
public class BatZipKickScript : Script
{
    private const double DoubleTapMs = 600;
    private const double HoldMs = 220;
    private const double PendingMs = 3000;
    private const double SecondTapWaitMs = 200;
    private const string ZipConfigClass = "RSpecialMoveConfig_RobinBatClawAttack";
    private const string ZipConfigName = "BatClawAttack";
    private const string ZipAnimSetName = "RH_Zip_Kick";
    private const string ZipPackage = "Playable_DLCRedHood_SF";

    private static DateTime lastClawSelect = DateTime.MinValue;
    private static DateTime pressAt = DateTime.MinValue;
    private static bool pressActive;
    private static bool pressFollowUp;
    private static int pressGadget = -1;
    private static int queuedFire;
    private static DateTime queuedUntil = DateTime.MinValue;
    private static DateTime pendingNormalAt = DateTime.MinValue;
    private static bool swallowRelease;
    private static DateTime swallowUntil = DateTime.MinValue;

    private static RHarpoonGun? swapGun;
    private static RSpecialMoveConfig? savedThugMove;
    private static DateTime pendingUntil = DateTime.MinValue;

    private static RSpecialMoveConfig_RobinBatClawAttack? zipConfig;
    private static AnimSet? zipAnims;
    private static DateTime nextSetup = DateTime.MinValue;
    private static bool loadedPackage;
    private static int kicks;
    private static RPawnVillain? deferTarget;
    private static RHarpoonGun? deferGun;
    private static Vector3 deferPos0;
    private static Vector3 deferPos1;
    private static Vector3 deferPos2;
    private static RWeaponConfig? animConfig;
    private static int animIndex = -1;
    private static AnimSet? animSaved;
    private static DateTime animRestoreAt = DateTime.MinValue;

    private static void Log(string message)
    {
        try { Debug.Log("[BatZipKick] " + message); } catch { }
    }

    private static string N(GameObject? o) => o == null || !o.IsValid ? "None" : o.GetPathName();

    public override void OnEnterGame()
    {
        pressActive = false;
        pendingNormalAt = DateTime.MinValue;
        swallowRelease = false;
        deferTarget = null;
        animConfig = null;
        animIndex = -1;
        animSaved = null;
        Restore("entering game");
        nextSetup = DateTime.UtcNow.AddSeconds(5);
    }

    private static RPawnPlayer? Batman()
    {
        RPawnPlayer? pawn = Game.GetPlayerController(0)?.Pawn as RPawnPlayer;

        return pawn != null && pawn.IsValid && BatmanRedHoodGuns.Guns.IsBatman(pawn) ? pawn : null;
    }

    private static bool packOff;

    public override void OnTick()
    {
        if (!BatmanArsenalPack.Arsenal.ZipKick)
        {
            if (!packOff)
            {
                packOff = true;
                pressActive = false;
                pendingNormalAt = DateTime.MinValue;
                swallowRelease = false;
                deferTarget = null;
                Util(() => RestoreAnims("zip kick turned off"));
                Util(() => Restore("zip kick turned off"));
                Log("zip kick turned off in the Arsenal Pack menu; the Batclaw is back to normal.");
            }

            return;
        }

        packOff = false;

        DateTime now = DateTime.UtcNow;

        if (swapGun != null && now > pendingUntil)
        {
            Restore("no claw hit");
        }

        if (swallowRelease && now > swallowUntil)
        {
            swallowRelease = false;
        }

        if (now >= nextSetup)
        {
            nextSetup = now.AddSeconds(2);
            Util(() => Setup());
        }

        if (pendingNormalAt != DateTime.MinValue && now >= pendingNormalAt)
        {
            pendingNormalAt = DateTime.MinValue;
            Queue(1);
        }

        if (animConfig != null && now > animRestoreAt)
        {
            RestoreAnims("timeout");
        }

        if (deferTarget != null)
        {
            RPawnVillain target = deferTarget;
            RHarpoonGun? gun = deferGun;
            deferTarget = null;
            deferGun = null;
            Util(() => StartZipAt(gun, target));
        }

        if (queuedFire != 0)
        {
            Util(() => TryQueuedFire(now));
        }

        if (!pressActive || (now - pressAt).TotalMilliseconds < HoldMs)
        {
            return;
        }

        pressActive = false;
        lastClawSelect = DateTime.MinValue;

        Queue(pressFollowUp ? 2 : 1);
    }

    private static void Util(Action action)
    {
        try { action(); }
        catch (Exception exception) { Log("error: " + exception.Message); }
    }

    private static void Setup()
    {
        RPawnPlayer? pawn = Batman();

        if (pawn == null)
        {
            return;
        }

        if (zipConfig == null || !zipConfig.IsValid)
        {
            zipConfig = FindZipConfig();

            if (zipConfig == null && !loadedPackage)
            {
                loadedPackage = true;
                Game.LoadPackage(ZipPackage);
                zipConfig = FindZipConfig();
                Log("loaded " + ZipPackage + " for the Zip Kick move -> " + (zipConfig != null));
            }

            if (zipConfig != null)
            {
                zipConfig.AddToRoot();
                Log("Zip Kick move: " + N(zipConfig) + " (player anims " + N(zipConfig.PlayerAnimset) + ", victim anims " + N(zipConfig.VictimReactionAnimSet) + ").");
            }
        }

        if (zipAnims == null || !zipAnims.IsValid)
        {
            FName wanted = new FName(ZipAnimSetName);

            foreach (AnimSet set in Game.FindObjects<AnimSet>())
            {
                if (set != null && set.IsValid && set.Name == wanted)
                {
                    zipAnims = set;
                    set.AddToRoot();
                    Log("Zip Kick anims: " + N(set) + ".");
                    break;
                }
            }
        }

        if (zipAnims != null && zipAnims.IsValid && animConfig == null)
        {
            RemoveAppended(pawn, zipAnims);
        }
    }

    private static RSpecialMoveConfig_RobinBatClawAttack? FindZipConfig()
    {
        try
        {
            FName wanted = new FName(ZipConfigName);

            foreach (RSpecialMoveConfig config in Game.FindObjects<RSpecialMoveConfig>())
            {
                if (config != null && config.IsValid && config.Name == wanted && config.Class.Name.ToString() == ZipConfigClass)
                {
                    return config as RSpecialMoveConfig_RobinBatClawAttack;
                }
            }
        }
        catch (Exception exception)
        {
            Log("searching the Zip Kick move failed: " + exception.Message);
        }

        return null;
    }

    private static void RemoveAppended(RPawnPlayer pawn, AnimSet set)
    {
        RWeaponConfig? config = pawn.UnarmedWeaponConfig;
        SkeletalMeshComponent? mesh = pawn.Mesh;

        if (config != null && config.IsValid)
        {
            for (int index = config.AnimSets.Count - 1; index >= 0; index--)
            {
                if (config.AnimSets[index] != null && config.AnimSets[index].Ptr == set.Ptr)
                {
                    config.AnimSets.RemoveAt(index);
                }
            }
        }

        if (mesh != null && mesh.IsValid)
        {
            for (int index = mesh.AnimSets.Count - 1; index >= 0; index--)
            {
                if (mesh.AnimSets[index] != null && mesh.AnimSets[index].Ptr == set.Ptr)
                {
                    mesh.AnimSets.RemoveAt(index);
                }
            }
        }
    }

    private static void PutZipAnims(RPawnPlayer pawn)
    {
        RestoreAnims("before a new kick");

        RWeaponConfig? config = pawn.UnarmedWeaponConfig;

        if (config == null || !config.IsValid || zipAnims == null || !zipAnims.IsValid)
        {
            Log("no weapon config or zip anims; kick without anim swap.");

            return;
        }

        TArray<AnimSet> sets = config.AnimSets;

        if (Contains(sets, zipAnims))
        {
            return;
        }

        FName clawSet = new FName("BM_BatClaw");

        for (int index = 0; index < sets.Count; index++)
        {
            if (sets[index] != null && sets[index].Name == clawSet)
            {
                animConfig = config;
                animIndex = index;
                animSaved = sets[index];
                animRestoreAt = DateTime.UtcNow.AddSeconds(5);
                sets[index] = zipAnims;
                Log("Zip Kick anims swapped into slot " + index + " (BM_BatClaw) for the kick.");

                return;
            }
        }

        Log("BM_BatClaw not in Batman's anims; kick without anim swap.");
    }

    private static void RestoreAnims(string why)
    {
        if (animConfig == null)
        {
            return;
        }

        try
        {
            if (animConfig.IsValid && animIndex >= 0 && animIndex < animConfig.AnimSets.Count)
            {
                animConfig.AnimSets[animIndex] = animSaved;
            }
        }
        catch (Exception exception)
        {
            Log("restoring the Batclaw anims failed: " + exception.Message);
        }

        Log("Batclaw anims restored (" + why + ").");
        animConfig = null;
        animIndex = -1;
        animSaved = null;
    }

    private static bool Contains(TArray<AnimSet> sets, AnimSet set)
    {
        for (int index = 0; index < sets.Count; index++)
        {
            if (sets[index] != null && sets[index].Ptr == set.Ptr)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsClawSelect(RPlayerController self, int gadget, out RPawnPlayer? pawn)
    {
        pawn = Batman();

        if (pawn == null || pawn.HarpoonGun == null || !pawn.HarpoonGun.IsValid)
        {
            return false;
        }

        int wheel = self.PCGadgetIndexToWheelIndex(gadget);
        RInventoryManager? inventory = pawn.InvManager as RInventoryManager;

        if (inventory == null || wheel < 0 || wheel >= inventory.BM2SelectableGadgets.Count)
        {
            return false;
        }

        return inventory.BM2SelectableGadgets[wheel] == pawn.HarpoonGun.Class.Name;
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.SelectGadget))]
    private static void SelectGadgetRedirect(RPlayerController self, int gadget)
    {
        if (!BatmanArsenalPack.Arsenal.ZipKick)
        {
            self.SelectGadget(gadget);
            return;
        }

        try
        {
            if (IsClawSelect(self, gadget, out RPawnPlayer? pawn) && pawn != null)
            {
                DateTime now = DateTime.UtcNow;
                RInventoryGadget? current = (pawn.InvManager as RInventoryManager)?.CurrentGadget;
                bool clawInHand = current != null && current.IsValid && pawn.HarpoonGun != null && current.Ptr == pawn.HarpoonGun.Ptr;
                bool followUp = (now - lastClawSelect).TotalMilliseconds <= DoubleTapMs || pendingNormalAt != DateTime.MinValue;

                pendingNormalAt = DateTime.MinValue;
                lastClawSelect = now;

                if ((clawInHand || followUp) && zipConfig != null && zipConfig.IsValid && swapGun == null)
                {
                    pressActive = true;
                    pressFollowUp = followUp;
                    pressGadget = gadget;
                    pressAt = now;

                    return;
                }
            }
        }
        catch (Exception exception)
        {
            Log("select check failed: " + exception.Message);
        }

        self.SelectGadget(gadget);
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.SelectGadgetReleased))]
    private static void SelectGadgetReleasedRedirect(RPlayerController self)
    {
        if (!BatmanArsenalPack.Arsenal.ZipKick)
        {
            self.SelectGadgetReleased();
            return;
        }

        if (pressActive)
        {
            pressActive = false;

            if (pressFollowUp)
            {
                Queue(1);
            }
            else
            {
                pendingNormalAt = DateTime.UtcNow.AddMilliseconds(SecondTapWaitMs);
            }

            return;
        }

        if (swallowRelease)
        {
            swallowRelease = false;

            return;
        }

        self.SelectGadgetReleased();
    }

    private static void Queue(int kind)
    {
        queuedFire = kind;
        queuedUntil = DateTime.UtcNow.AddMilliseconds(1200);
    }

    private static void TryQueuedFire(DateTime now)
    {
        RPawnPlayer? pawn = Batman();
        RHarpoonGun? gun = pawn?.HarpoonGun;

        if (pawn == null || gun == null || !gun.IsValid || now > queuedUntil)
        {
            queuedFire = 0;

            return;
        }

        if (!gun.CanQuickFire())
        {
            return;
        }

        int kind = queuedFire;
        queuedFire = 0;

        if (kind == 2)
        {
            FireZipKick();
        }
        else if (Game.GetPlayerController(0) is RPlayerController pc)
        {
            pc.FireQuickGadget(gun, false);
        }
    }

    private static void FireZipKick()
    {
        RPawnPlayer? pawn = Batman();

        if (pawn == null || Game.GetPlayerController(0) is not RPlayerController pc)
        {
            return;
        }

        RHarpoonGun? gun = pawn.HarpoonGun;

        if (gun == null || !gun.IsValid)
        {
            Log("Batman has no Batclaw.");

            return;
        }

        if (zipConfig == null || !zipConfig.IsValid)
        {
            Setup();
        }

        if (zipConfig == null || !zipConfig.IsValid)
        {
            Log("no Zip Kick move loaded; normal Batclaw.");

            return;
        }

        Restore("new shot");

        try { pc.ClearTimer(new FName("KeyboardQuickGadgetTimer"), null); } catch { }

        swapGun = gun;
        savedThugMove = gun.HarpoonThugMove;
        pendingUntil = DateTime.UtcNow.AddMilliseconds(PendingMs);
        Log("double tap + hold: Zip Kick shot.");
        pc.FireQuickGadget(gun, false);
    }

    private static void Restore(string why)
    {
        if (swapGun == null)
        {
            return;
        }

        try
        {
            if (swapGun.IsValid)
            {
                swapGun.HarpoonThugMove = savedThugMove;
            }
        }
        catch (Exception exception)
        {
            Log("restoring the claw failed: " + exception.Message);
        }

        swapGun = null;
        savedThugMove = null;
        pendingUntil = DateTime.MinValue;
    }

    private static bool IsSwapGun(RHarpoonGun self) => swapGun != null && swapGun.IsValid && self.Ptr == swapGun.Ptr;

    [Redirect(typeof(RHarpoonGun), nameof(RHarpoonGun.BatclawThugMove))]
    private static bool BatclawThugMoveRedirect(RHarpoonGun self)
    {
        if (!BatmanArsenalPack.Arsenal.ZipKick)
        {
            return self.BatclawThugMove();
        }

        if (IsSwapGun(self) && self.HarpoonTarget is RPawnVillain && zipConfig != null && zipConfig.IsValid)
        {
            Log("claw locked " + N(self.HarpoonTarget) + "; firing the claw shot.");

            return false;
        }

        return self.BatclawThugMove();
    }

    [Redirect(typeof(RHarpoonGun), nameof(RHarpoonGun.StartPullMove))]
    private static void StartPullMoveRedirect(RHarpoonGun self)
    {
        if (!BatmanArsenalPack.Arsenal.ZipKick)
        {
            self.StartPullMove();
            return;
        }

        if (!IsSwapGun(self) || self.HarpoonTarget is not RPawnVillain target || zipConfig == null || !zipConfig.IsValid || Batman() is not RPawnPlayer pawn)
        {
            if (IsSwapGun(self))
            {
                Restore("claw hit no thug");
            }

            self.StartPullMove();

            return;
        }

        try
        {
            PutZipAnims(pawn);
            deferTarget = target;
            deferGun = self;
            deferPos0 = self.HarpoonTargetPos_0;
            deferPos1 = self.HarpoonTargetPos_1;
            deferPos2 = self.HarpoonTargetPos_2;
        }
        catch (Exception exception)
        {
            Log("Zip Kick start failed: " + exception.Message);
            RestoreAnims("start failed");
        }
        finally
        {
            Restore("claw arrived");
        }
    }

    private static void StartZipAt(RHarpoonGun? gun, RPawnVillain target)
    {
        RPawnPlayer? pawn = Batman();

        if (pawn == null || !target.IsValid || target.Health <= 0 || zipConfig == null || !zipConfig.IsValid || Game.GetPlayerController(0) is not RPlayerController pc)
        {
            RestoreAnims("no kick");

            return;
        }

        kicks++;
        Log("Zip Kick #" + kicks + " at " + N(target) + ", distance " + Vector3.Distance(target.Location, pawn.Location).ToString("0") + ".");

        REnvironmentCheckTicker.FEnvironmentSpecialMoveLocator locator = new REnvironmentCheckTicker.FEnvironmentSpecialMoveLocator();
        locator.ActorInstance = target;
        locator.ExtraVectorInfo_0 = deferPos0;
        locator.ExtraVectorInfo_1 = deferPos1;
        locator.ExtraVectorInfo_2 = deferPos2;

        if (gun != null && gun.IsValid)
        {
            gun.bAiming = false;
        }

        zipConfig.TriggerSpecialMove(pc, locator, false);
    }

    [Redirect(typeof(RSpecialMoveInstance_RobinBatClawAttack), nameof(RSpecialMoveInstance_RobinBatClawAttack.FinishSpecialMove))]
    private static void ZipFinishRedirect(RSpecialMoveInstance_RobinBatClawAttack self)
    {
        if (!BatmanArsenalPack.Arsenal.ZipKick)
        {
            self.FinishSpecialMove();
            return;
        }

        try { self.FinishSpecialMove(); }
        finally { RestoreAnims("kick finished"); }
    }

    [Redirect(typeof(RSpecialMoveInstance_RobinBatClawAttack), nameof(RSpecialMoveInstance_RobinBatClawAttack.CancelSpecialMove))]
    private static void ZipCancelRedirect(RSpecialMoveInstance_RobinBatClawAttack self, RSpecialMoveConfig next)
    {
        if (!BatmanArsenalPack.Arsenal.ZipKick)
        {
            self.CancelSpecialMove(next);
            return;
        }

        try { self.CancelSpecialMove(next); }
        finally { RestoreAnims("kick cancelled"); }
    }

    [Redirect(typeof(RSpecialMoveInstance_RobinBatClawAttack), nameof(RSpecialMoveInstance_RobinBatClawAttack.TakeDownVillain))]
    private static void TakeDownVillainRedirect(RSpecialMoveInstance_RobinBatClawAttack self)
    {
        if (!BatmanArsenalPack.Arsenal.ZipKick)
        {
            self.TakeDownVillain();
            return;
        }

        RPawnPlayer? pawn = self.PlayerPawn as RPawnPlayer;
        RHarpoonGun? gun = pawn?.HarpoonGun;

        if (pawn == null || gun == null || !BatmanRedHoodGuns.Guns.IsBatman(pawn) || gun.Class.Name.ToString() == "RRobinBatClaw")
        {
            self.TakeDownVillain();

            return;
        }

        try
        {
            if (self.TargetPawn is not RPawnVillain villain || self.VillainBlocked)
            {
                return;
            }

            int damage = villain.Health <= 2 ? 1000 : 2;
            bool shortKick = (int)self.KickAnimType == 0;
            Vector3 velocity = pawn.Velocity;
            Vector3 direction = velocity.LengthSquared() > 1f ? Vector3.Normalize(velocity) : pawn.Rotation.ToDirection();

            RPawnCombat.FDamageInfo info = new RPawnCombat.FDamageInfo();
            info.Attacker = pawn;
            info.DamageAmount = damage;
            info.BoneName = new FName("Bip01_Spine2");
            info.Impulse = (direction + new Vector3(0f, 0f, 0.8f)) * 3000f;

            if (shortKick)
            {
                info.DamageType = RDmgType_Ragdoll.StaticClass();
            }
            else
            {
                info.DamageType = RDmgType_ZipKick.StaticClass();
                info.bWillKnockOverOtherPawns = true;
            }

            info.bForceQuickMotor = true;
            info.StunTime = 5f;

            int before = villain.Health;
            var result = villain.DamagedBy(info);
            Log("Zip Kick hit " + N(villain) + " (" + (shortKick ? "short" : "flying") + "), HP " + before + " -> " + villain.Health + ", " + result + ".");

            int code = (int)result;

            if ((code == 1 || code == 2) && Game.GetPlayerController(0) is RPlayerControllerCombat combat && combat.IsInCombat(false))
            {
                try
                {
                    combat.IncCombo(villain, 1, 0, false);
                    combat.MovePerformed((RGameInfo.EComboMoveType)36, villain, false, 0, 0);
                }
                catch
                {
                }
            }

            try { pawn.PlaySound_Impact(villain, true, damage > 999, false, true, true, false, false, false, false, false); } catch { }
        }
        catch (Exception exception)
        {
            Log("Zip Kick hit failed: " + exception.Message);
        }
    }
}
