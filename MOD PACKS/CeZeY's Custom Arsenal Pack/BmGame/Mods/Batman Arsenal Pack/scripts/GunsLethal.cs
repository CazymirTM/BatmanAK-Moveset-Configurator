#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;
using BmSDK.GFxUI;

namespace BatmanRedHoodGuns;

internal static class GunsLethal
{
    private static readonly HashSet<IntPtr> killed = new HashSet<IntPtr>();
    private static readonly List<RPawnVillain> killedList = new List<RPawnVillain>();

    public static bool IsKilled(RPawnVillain villain) => villain != null && villain.IsValid && killed.Contains(villain.Ptr);

    private static bool IsGunDamage(Class? damageType)
    {
        string name = damageType != null && damageType.IsValid ? damageType.Name.ToString() : "";

        return name.StartsWith("MDmgType_Handgun", StringComparison.Ordinal);
    }

    private static bool ByBatman(GameObject? attacker)
    {
        RPawnPlayer? batman = Guns.Batman();

        if (batman == null || attacker == null || !attacker.IsValid)
        {
            return false;
        }

        return attacker.Ptr == batman.Ptr || (batman.Controller != null && attacker.Ptr == batman.Controller.Ptr);
    }

    private static void MarkKilled(RPawnVillain villain)
    {
        if (villain == null || !villain.IsValid || !killed.Add(villain.Ptr))
        {
            return;
        }

        killedList.Add(villain);

        if (DebugScan)
        {
            ModLog.Info("Lethal: killed " + villain.Ptr.ToString("X") + ".");
        }

        killedList.RemoveAll(v => !v.IsValid);
        ModLog.Info("Lethal: " + Util.ClassName(villain) + " killed by the " + (SniperLink.IsSniperVictim(villain) ? "sniper rifle" : "guns") + " (" + killedList.Count + " dead).");
    }

    [Redirect(typeof(RPawnVillain), nameof(RPawnVillain.TakeDamage))]
    private static void TakeDamageRedirect(RPawnVillain self, int damage, Controller instigatedBy, Vector3 hitLocation, Vector3 momentum, Class damageType, Actor.FTraceHitInfo hitInfo, Actor damageCauser)
    {
        if (!(BatmanArsenalPack.Arsenal.Guns || BatmanArsenalPack.Arsenal.Sniper))
        {
            self.TakeDamage(damage, instigatedBy, hitLocation, momentum, damageType, hitInfo, damageCauser);
            return;
        }

        bool lethal = false;

        try
        {
            lethal = IsGunDamage(damageType) && ByBatman(instigatedBy) && (Guns.Gun != null || SniperLink.IsSniperVictim(self));

            if (lethal)
            {
                damage = Math.Max(damage, Math.Max(self.Health, 1) + 1000);
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("lethal-damage-failed", "Lethal: checking a hit failed: " + Util.Unwrap(exception));
        }

        self.TakeDamage(damage, instigatedBy, hitLocation, momentum, damageType, hitInfo, damageCauser);

        if (!lethal)
        {
            return;
        }

        try
        {
            MarkKilled(self);

            if (self.Health > 0 && !self.bDiedAlready)
            {
                RPawnCombat.FDamageInfo info = default;
                Vector3 push = momentum.LengthSquared() > 1f ? Vector3.Normalize(momentum) * 900f : Vector3.Zero;

                info.Attacker = Guns.Batman();
                info.DamageAmount = 1000f;
                info.DamageType = damageType;
                info.Impulse = push;
                info.RagdollForce = push;
                info.RagdollForceDuration = 0.15f;
                info.bHeavyHit = true;
                info.bCritical = true;
                self.PlayRagdoll(info, RGameInfo.RagdollStunType.RAGSTUN_Generic, true);
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("lethal-finish-failed", "Lethal: finishing a hit failed: " + Util.Unwrap(exception));
        }
    }

    [Redirect(typeof(RPawnVillain), nameof(RPawnVillain.PlayRagdoll))]
    private static void PlayRagdollRedirect(RPawnVillain self, RPawnCombat.FDamageInfo info, RGameInfo.RagdollStunType stunType, bool kill)
    {
        if (!(BatmanArsenalPack.Arsenal.Guns || BatmanArsenalPack.Arsenal.Sniper))
        {
            self.PlayRagdoll(info, stunType, kill);
            return;
        }

        bool lethal = false;

        try
        {
            lethal = IsGunDamage(info.DamageType) && ByBatman(info.Attacker) && (Guns.Gun != null || SniperLink.IsSniperVictim(self));
        }
        catch
        {
        }

        self.PlayRagdoll(info, stunType, kill || lethal);

        if (lethal)
        {
            MarkKilled(self);
        }
    }

    [Redirect(typeof(RPawnVillain), nameof(RPawnVillain.WakeFromDead))]
    private static void WakeFromDeadRedirect(RPawnVillain self, RPawnCharacter newGetUpMaster, RAnimUtil_PosePlayer.FTransitionId newGetUpMasterAnimID, AnimSet newGetUpAnimset, FName newGetUpAnimName, FName getUpMovementStance, bool doAnim, bool animImmediate)
    {
        if (!(BatmanArsenalPack.Arsenal.Guns || BatmanArsenalPack.Arsenal.Sniper))
        {
            self.WakeFromDead(newGetUpMaster, newGetUpMasterAnimID, newGetUpAnimset, newGetUpAnimName, getUpMovementStance, doAnim, animImmediate);
            return;
        }

        if (IsKilled(self))
        {
            ModLog.Once("nowake:" + self.Ptr, "Lethal: " + Util.ClassName(self) + " was shot dead; he stays down.");

            return;
        }

        self.WakeFromDead(newGetUpMaster, newGetUpMasterAnimID, newGetUpAnimset, newGetUpAnimName, getUpMovementStance, doAnim, animImmediate);
    }

    [Redirect(typeof(RPawnVillain), nameof(RPawnVillain.CanBeRevivedByMedic))]
    private static bool CanBeRevivedByMedicRedirect(RPawnVillain self, RPawnVillain newMedic, RPawnCombat newTarget)
    {
        if (!(BatmanArsenalPack.Arsenal.Guns || BatmanArsenalPack.Arsenal.Sniper))
        {
            return self.CanBeRevivedByMedic(newMedic, newTarget);
        }

        if (newTarget is RPawnVillain target && IsKilled(target))
        {
            return false;
        }

        return self.CanBeRevivedByMedic(newMedic, newTarget);
    }

    private const int StateUnknown = 10;
    private const int StateUnconscious = 3;
    private const float ScreenAspect = 16f / 9f;

    private const float BodyMatchDistance = 0.22f;

    private static string? deceasedText;
    private static string labelPath = "";
    private static string originalLabel = "";
    private static int nextClipScan = 0;
    private static bool shownDead;

    private const int LastClip = 2000000;
    private const long ScanTicksPerFrame = TimeSpan.TicksPerMillisecond * 4;
    private static IntPtr lastScanned = IntPtr.Zero;
    private static bool DebugScan = false;

    [Redirect(typeof(RHudModuleDetectiveMode), nameof(RHudModuleDetectiveMode.SetNPCDetail))]
    private static void SetNPCDetailRedirect(RHudModuleDetectiveMode self, float locationX, float locationY, float locationDepth, int physicalState, int weapon, int extraInfo, bool armoured, FString optionalName)
    {
        if (!(BatmanArsenalPack.Arsenal.Guns || BatmanArsenalPack.Arsenal.Sniper))
        {
            self.SetNPCDetail(locationX, locationY, locationDepth, physicalState, weapon, extraInfo, armoured, new FString(optionalName.ToString() ?? ""));
            return;
        }

        bool dead = false;

        try
        {
            dead = killedList.Count > 0 && ScannedVillainIsKilled(locationX, locationY, physicalState);
        }
        catch (Exception exception)
        {
            ModLog.Once("scan-failed", "Lethal: matching the scanned thug failed: " + Util.Unwrap(exception));
        }

        self.SetNPCDetail(locationX, locationY, locationDepth, dead ? StateUnknown : physicalState, weapon, extraInfo, armoured, new FString(optionalName.ToString() ?? ""));

        try
        {
            if (dead)
            {
                ShowDeceased(self);
            }
            else if (killedList.Count > 0 && labelPath.Length == 0 && self.ModularHudMovie is GFxMoviePlayer scanning && scanning.IsValid)
            {
                FindLabel(scanning, AttachPath(self));
            }
            else if (shownDead)
            {
                RestoreLabel(self);
            }
        }
        catch (Exception exception)
        {
            ModLog.Once("deceased-failed", "Lethal: showing DECEASED failed: " + Util.Unwrap(exception));
        }
    }

    private static bool ScannedVillainIsKilled(float x, float y, int physicalState)
    {
        RPlayerController? controller = Game.GetPlayerController(0);

        if (controller == null || physicalState != StateUnconscious)
        {
            return false;
        }

        controller.GetPlayerViewPoint(out Vector3 eye, out Rotator view);

        float fov = controller.PlayerCamera?.GetFOVAngle() ?? 80f;

        if (float.IsNaN(fov) || fov < 1f)
        {
            fov = 80f;
        }

        float tanX = MathF.Tan(fov * 0.5f * MathF.PI / 180f);
        float tanY = tanX / ScreenAspect;
        Vector3 forward = Vector3.Normalize(view.ToDirection());
        Vector3 right = Vector3.Normalize(new Vector3(-forward.Y, forward.X, 0f));
        Vector3 up = Vector3.Cross(right, forward);

        if (up.Z < 0f)
        {
            up = -up;
        }

        float best = BodyMatchDistance;
        RPawnVillain? match = null;

        foreach (RPawnVillain villain in Game.FindObjects<RPawnVillain>())
        {
            if (villain == null || !villain.IsValid)
            {
                continue;
            }

            Vector3 point = villain.Location;

            try
            {
                if (villain.Mesh != null && villain.Mesh.IsValid)
                {
                    point = villain.Mesh.Bounds.Origin;
                }
            }
            catch
            {
            }

            Vector3 d = point - eye;
            float ahead = Vector3.Dot(d, forward);

            if (ahead < 50f || ahead > 6000f)
            {
                continue;
            }

            float sx = Vector3.Dot(d, right) / ahead / tanX * 0.5f;
            float sy = -Vector3.Dot(d, up) / ahead / tanY * 0.5f;
            float dist = MathF.Sqrt((sx - x) * (sx - x) + (sy - y) * (sy - y));

            if (IsStanding(villain))
            {
                if (killed.Remove(villain.Ptr))
                {
                    killedList.RemoveAll(v => !v.IsValid || v.Ptr == villain.Ptr);
                }

                continue;
            }

            if (dist < best)
            {
                best = dist;
                match = villain;
            }
        }

        bool dead = match != null && IsKilled(match);

        IntPtr matched = match?.Ptr ?? IntPtr.Zero;

        if (DebugScan && matched != lastScanned)
        {
            lastScanned = matched;
            ModLog.Info("Lethal: scanning " + (match != null ? Util.ClassName(match) + " " + match.Ptr.ToString("X") : "nobody") + " at " + x.ToString("0.000") + "," + y.ToString("0.000") + " (off " + best.ToString("0.000") + "), state " + physicalState + ", killed by the guns: " + dead + ".");
        }

        return dead;
    }

    private static bool IsStanding(RPawnVillain villain)
    {
        try
        {
            return villain.Health > 0 && !villain.bDiedAlready && villain.IsAliveAndWell();
        }
        catch
        {
            return false;
        }
    }

    private static string UnconsciousText()
    {
        try
        {
            return GameObject.Localize(new FString("Detective"), new FString("Unconscious"), new FString("GFxUI")).ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string DeceasedText()
    {
        if (deceasedText != null)
        {
            return deceasedText;
        }

        string word = "DECEASED";
        string prefix = "Condition: ";

        try
        {
            string localized = GameObject.Localize(new FString("Map"), new FString("Deceased"), new FString("GFxUI")).ToString() ?? "";

            if (localized.Length > 0 && !localized.Contains('?'))
            {
                word = localized;
            }

            string unconscious = GameObject.Localize(new FString("Detective"), new FString("Unconscious"), new FString("GFxUI")).ToString() ?? "";
            int colon = unconscious.IndexOf(": ", StringComparison.Ordinal);

            if (colon > 0)
            {
                prefix = unconscious.Substring(0, colon + 2);
            }
        }
        catch
        {
        }

        deceasedText = prefix + word;

        return deceasedText;
    }

    private static string AttachPath(RHudModuleDetectiveMode module) => module.LocalRootString + ".NPCInfoPanel.NPCInfo.HeartRate.Attach";

    private static string Get(GFxMoviePlayer movie, string path) => movie.GetVariableString(new FString(path)).ToString() ?? "";

    private static void ShowDeceased(RHudModuleDetectiveMode module)
    {
        GFxMoviePlayer? movie = module.ModularHudMovie;

        if (movie == null || !movie.IsValid)
        {
            return;
        }

        string text = DeceasedText();

        if (labelPath.Length > 0)
        {
            string current = Get(movie, labelPath);

            if (current == text)
            {
                shownDead = true;

                return;
            }

            if (current.Length > 0)
            {
                movie.SetVariableString(new FString(labelPath), new FString(text));
                shownDead = true;

                return;
            }

            labelPath = "";
            nextClipScan = 0;
        }

        if (!FindLabel(movie, AttachPath(module)))
        {
            return;
        }

        movie.SetVariableString(new FString(labelPath), new FString(text));
        shownDead = true;
        ModLog.Once("deceased-shown", "Lethal: Detective Mode shows '" + text + "' for thugs the guns killed (label in " + labelPath + ").");
    }

    private static void RestoreLabel(RHudModuleDetectiveMode module)
    {
        shownDead = false;

        GFxMoviePlayer? movie = module.ModularHudMovie;

        if (movie == null || !movie.IsValid || labelPath.Length == 0)
        {
            return;
        }

        if (originalLabel.Length == 0)
        {
            originalLabel = UnconsciousText();
        }

        if (Get(movie, labelPath) == DeceasedText())
        {
            movie.SetVariableString(new FString(labelPath), new FString(originalLabel));
        }
    }

    private static bool FindLabel(GFxMoviePlayer movie, string attach)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();

        while (TimeSpan.FromSeconds((System.Diagnostics.Stopwatch.GetTimestamp() - started) / (double)System.Diagnostics.Stopwatch.Frequency).Ticks < ScanTicksPerFrame)
        {
            for (int step = 0; step < 16; step++)
            {
                int i = nextClipScan;

                nextClipScan = i >= LastClip ? 0 : i + 1;

                string clip = attach + ".instance" + i;

                if (Get(movie, clip + ".Text.text").Length == 0)
                {
                    continue;
                }

                for (int j = i + 1; j < i + 64; j++)
                {
                    string label = clip + ".instance" + j + ".text";
                    string value = Get(movie, label);

                    if (value.Length > 0)
                    {
                        nextClipScan = i;
                        labelPath = label;

                        if (value != DeceasedText() && !value.Contains('?'))
                        {
                            originalLabel = value;
                        }

                        return true;
                    }
                }
            }
        }

        return false;
    }
}
