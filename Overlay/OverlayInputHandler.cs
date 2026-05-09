using System;
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
        public event EventHandler? OnToggleSettings;       // Ctrl+F8
        public event EventHandler? OnCycleMode;            // Ctrl+F9
        public event EventHandler? OnForceDump;            // Ctrl+F10
        public event EventHandler? OnExportReport;         // Ctrl+F11
        public event EventHandler? OnCyclePosition;        // Ctrl+F12
        public event EventHandler? OnToggleScreenshots;    // Ctrl+F7

        public void Poll(bool settingsOpen)
        {
            if (!CtrlHeld) return;

            if (SettingsStore.Current.SettingsPanelHotkey && Input.GetKeyDown(KeyCode.F8))
            {
                OnToggleSettings?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (settingsOpen && GUIUtility.keyboardControl != 0) return;

            if (Input.GetKeyDown(KeyCode.F7)) OnToggleScreenshots?.Invoke(this, EventArgs.Empty);
            if (Input.GetKeyDown(KeyCode.F9)) OnCycleMode?.Invoke(this, EventArgs.Empty);
            if (Input.GetKeyDown(KeyCode.F10)) OnForceDump?.Invoke(this, EventArgs.Empty);
            if (Input.GetKeyDown(KeyCode.F11)) OnExportReport?.Invoke(this, EventArgs.Empty);
            if (Input.GetKeyDown(KeyCode.F12)) OnCyclePosition?.Invoke(this, EventArgs.Empty);
        }

        private static bool CtrlHeld
            => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
    }
}
