using UnityEngine;
using VanillaProfiler.Diagnostics;

namespace VanillaProfiler.Overlay
{
    /// <summary>
    /// Polls F-keys with a Ctrl modifier to avoid colliding with vanilla CS2 bindings
    /// (F5 quicksave, F9 quickload, function keys for camera modes).
    /// All shortcuts: Ctrl + F-key. Translates input into semantic events.
    /// </summary>
    public sealed class OverlayInputHandler
    {
        public OverlayCommand Poll(bool settingsOpen)
        {
            if (!CtrlHeld) return OverlayCommand.None;

            if (SettingsStore.Snapshot.Settings.SettingsPanelHotkey && Input.GetKeyDown(KeyCode.F8))
                return OverlayCommand.ToggleSettings;

            if (settingsOpen && GUIUtility.keyboardControl != 0)
                return OverlayCommand.None;

            if (!settingsOpen && Input.GetKeyDown(KeyCode.F7))
                return OverlayCommand.ToggleScreenshots;
            if (Input.GetKeyDown(KeyCode.F9))
                return OverlayCommand.CycleMode;
            if (Input.GetKeyDown(KeyCode.F10))
                return OverlayCommand.ForceDump;
            if (Input.GetKeyDown(KeyCode.F11))
                return OverlayCommand.ExportReport;
            if (!settingsOpen && Input.GetKeyDown(KeyCode.F12))
                return OverlayCommand.CycleAnchor;

            return OverlayCommand.None;
        }

        private static bool CtrlHeld
            => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
    }
}
