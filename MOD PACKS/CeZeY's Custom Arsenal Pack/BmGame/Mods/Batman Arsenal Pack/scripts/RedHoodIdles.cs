using System;
using System.Collections.Generic;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

[Script]
public class RedHoodIdles : Script
{
    private const string BatmanConfigPath = "Player_Anim_Config.Batman_Anim_Config";

    private const string RedHoodCombatPath = "DLC0_Anim_RedHood_Combat.Code.RH_Combat";

    private static RPawnPlayerConfig? batmanConfig;
    private static AnimSet? redHoodCombat;
    private static readonly HashSet<IntPtr> patched = new HashSet<IntPtr>();

    [Redirect(typeof(RPawnPlayerCombat), nameof(RPawnPlayerCombat.GetBeforeAndAfterCombatAnimset))]
    private static AnimSet? BeforeAndAfterCombatRedirect(RPawnPlayerCombat self)
    {
        AnimSet? original = self.GetBeforeAndAfterCombatAnimset();

        if (self.Class.Name.ToString().StartsWith("RPawnPlayerBm", StringComparison.Ordinal) && !BatmanRobinStaff.StaffMoves.RobinActive && redHoodCombat != null && redHoodCombat.IsValid)
        {
            return redHoodCombat;
        }

        return original;
    }

    public override void OnTick()
    {
        try
        {
            if (redHoodCombat == null || !redHoodCombat.IsValid)
            {
                redHoodCombat = Game.FindObject<AnimSet>(RedHoodCombatPath);
                redHoodCombat?.AddToRoot();
            }

            if (batmanConfig == null || !batmanConfig.IsValid)
            {
                batmanConfig = Game.FindObject<RPawnPlayerConfig>(BatmanConfigPath);
                patched.Clear();
            }

            if (batmanConfig == null)
            {
                return;
            }

            TArray<RPoseConfig> configs = batmanConfig.PoseConfigs;

            for (int index = 0; index < configs.Count; index++)
            {
                RPoseConfig? config = configs[index];

                if (config != null && config.IsValid && !patched.Contains(config.Ptr))
                {
                    Patch(config, batmanConfig);
                }
            }
        }
        catch (Exception exception)
        {
            Debug.Log("[RedHoodIdles] " + exception.Message);
        }
    }

    private static void Patch(RPoseConfig config, RPawnPlayerConfig batman)
    {
        AnimSet? stand = batman.DefaultIdleOverlayAnimSet;
        AnimSet? cover = batman.CoverIdleOverlayAnimSet;
        AnimSet? dangle = batman.DangleIdleOverlayAnimSet;
        AnimSet? hang = batman.HangIdleOverlayAnimSet;

        if (stand == null || !stand.IsValid || !stand.Name.ToString().StartsWith("RH_", StringComparison.Ordinal))
        {
            return;
        }

        int changed = 0;
        TArray<RPoseConfig.FPose> poses = config.Poses;

        for (int index = 0; index < poses.Count; index++)
        {
            RPoseConfig.FPose pose = poses[index];
            RPoseConfig.FRandomIdleOverlays overlays = pose.RandomIdleOverlays;
            AnimSet? replacement = overlays.AnimSet == null ? null : overlays.AnimSet.Name.ToString() switch
            {
                "Stagg_B3_Anim_BM_SC_Idles" or "BM_Stand_Idle_Overlay" => stand,
                "BM_Cover_Idle_Overlay" => cover,
                "BM_Dangle_Idle_Overlay" => dangle,
                "BM_Hang_Idle_Overlay" => hang,
                _ => null
            };
            bool dropAdditive = replacement != null && overlays.AdditiveAnimSet != null && overlays.AdditiveAnimSet.Name.ToString() == "BM_Stand_Idle_Additive_Overlay";

            if (replacement == null || !replacement.IsValid)
            {
                continue;
            }

            overlays.AnimSet = replacement;

            if (dropAdditive)
            {
                overlays.AdditiveAnimSet = null;
            }

            pose.RandomIdleOverlays = overlays;
            poses[index] = pose;
            changed++;
        }

        patched.Add(config.Ptr);

        if (changed > 0)
        {
            Debug.Log("[RedHoodIdles] " + config.Name + ": " + changed + " idle fidget lists now use Red Hood's.");
        }
    }
}
