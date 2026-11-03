using System;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.BmScript;
using BmSDK.Framework;

namespace CeZeY.OracleScript;

public partial class Oracle
{
    private const int NgppButtonId = 79;
    private const string NgppBootKey = "CeZeY.Oracle.NgppAtBoot";
    private const string NgppLabel = "NEW GAME ++";

    private static bool _ngppSetting;
    private static bool _counterAskLogged;

    private static bool NgppThisSession => AppDomain.CurrentDomain.GetData(NgppBootKey) is bool on && on;

    private static void SnapshotNgpp()
    {
        if (AppDomain.CurrentDomain.GetData(NgppBootKey) == null)
            AppDomain.CurrentDomain.SetData(NgppBootKey, _ngppSetting);
    }

    [Redirect(typeof(RGFxMovieUI), nameof(RGFxMovieUI.IsNewGamePlusUnlocked))]
    private static bool IsNewGamePlusUnlockedRedirect(RGFxMovieUI self)
    {
        bool unlocked = self.IsNewGamePlusUnlocked();

        try
        {
            if (unlocked && NgppThisSession && self is RGFxMovieUI_MainMenu && self.IsNewGamePlusStarted())
            {
                self.UC_AddButton(NgppButtonId, new FString(NgppLabel), true, false, false, new FString(""));
                Log("Added NEW GAME ++ to the main menu.");
            }
        }
        catch (Exception ex)
        {
            Log("Could not add NEW GAME ++: " + ex.Message);
        }

        return unlocked;
    }

    [Redirect(typeof(RGFxMovieUI_MainMenu), nameof(RGFxMovieUI_MainMenu.XI_OnClicked))]
    private static void MainMenuClickedRedirect(RGFxMovieUI_MainMenu self, int id)
    {
        if (id != NgppButtonId || !NgppThisSession)
        {
            self.XI_OnClicked(id);
            return;
        }

        Log("NEW GAME ++ picked: starting a fresh New Game + run.");
        self.GotoNewGamePlus();
    }

    [Redirect(typeof(RBMCombatManager), nameof(RBMCombatManager.CanShowCounterIcons))]
    private static bool CanShowCounterIconsRedirect(RBMCombatManager self)
    {
        bool shown = self.CanShowCounterIcons();
        bool forced = !shown && FeatureById.TryGetValue("counter_icons", out Feature? f) && f.On && (self.DisableCounterIconSequenceAction == null || !self.DisableCounterIconSequenceAction.IsValid);

        if (!_counterAskLogged)
        {
            _counterAskLogged = true;
            Log("Counter icons: game says " + (shown ? "show" : "hide") + (forced ? ", Oracle shows them anyway." : "."));
        }

        return shown || forced;
    }
}
