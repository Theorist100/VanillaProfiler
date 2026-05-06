using System;

namespace VanillaProfiler.Diagnostics
{
    /// <summary>
    /// Persisted user preferences for the overlay and profiler core.
    /// JSON file lives at persistentDataPath/VanillaProfiler/settings.json.
    /// Defaults match shipping behaviour, so a missing file is not an error.
    ///
    /// Threading: Current is read/written from the main thread only (Mod.OnLoad,
    /// SettingsPanel.Apply, Ctrl+F12 hotkey, Ctrl+F7 SpikeScreenshot toggle — all main).
    /// </summary>
    [Serializable]
    public sealed class ProfilerSettings
    {
        // Report cadence (seconds). Clamped on load to a sane range.
        public float ReportIntervalSec = 5.0f;

        // Default overlay mode at startup. 0=Status 1=Diagnosis 2=Details 3=Hidden
        public int DefaultMode = 0;

        // Default screen anchor. 0=TL 1=TR 2=BR 3=BL
        public int Anchor = 0;

        public int SparklineWidth = 60;

        public bool SpikeScreenshots = true;
        public float SpikeThresholdMs = 100.0f;

        public bool SettingsPanelHotkey = true;

        // Profile every vanilla SystemBase.Update via Harmony patch. Off by default
        // because patching ~300 vanilla systems adds measurable overhead; mod systems
        // are always profiled. Toggle when you specifically need vanilla breakdown.
        public bool ProfileVanillaSystems = false;

        // UI scaling. 0 = auto from screen height, otherwise explicit multiplier
        // (typical values 1.0/1.5/2.0). Clamped to [0.75, 3.0] on load.
        public float UiScale = 0f;

        // Manual drag positions in scaled (logical) screen coordinates.
        // -1 means "not set, fall back to Anchor preset". Updated when the user
        // drags a panel by its title bar.
        public float PanelX = -1f;
        public float PanelY = -1f;
        public float SettingsX = -1f;
        public float SettingsY = -1f;

        public bool Clamp()
        {
            bool changed = false;
            if (float.IsNaN(ReportIntervalSec) || float.IsInfinity(ReportIntervalSec))
                changed |= Set(ref ReportIntervalSec, 5f);
            if (ReportIntervalSec < 1f) changed |= Set(ref ReportIntervalSec, 1f);
            if (ReportIntervalSec > 60f) changed |= Set(ref ReportIntervalSec, 60f);
            if (DefaultMode < 0 || DefaultMode > 3) changed |= Set(ref DefaultMode, 0);
            if (Anchor < 0 || Anchor > 3) changed |= Set(ref Anchor, 0);
            if (SparklineWidth < 10) changed |= Set(ref SparklineWidth, 10);
            if (SparklineWidth > 60) changed |= Set(ref SparklineWidth, 60);
            if (float.IsNaN(SpikeThresholdMs) || float.IsInfinity(SpikeThresholdMs))
                changed |= Set(ref SpikeThresholdMs, 100f);
            if (SpikeThresholdMs < 33f) changed |= Set(ref SpikeThresholdMs, 33f);
            if (SpikeThresholdMs > 1000f) changed |= Set(ref SpikeThresholdMs, 1000f);

            if (float.IsNaN(UiScale) || float.IsInfinity(UiScale))
                changed |= Set(ref UiScale, 0f);
            // 0 (auto) is the only "off" sentinel; any positive value is a manual scale
            // and must be clamped into the supported range.
            if (UiScale > 0f)
            {
                if (UiScale < 0.75f) changed |= Set(ref UiScale, 0.75f);
                if (UiScale > 3f) changed |= Set(ref UiScale, 3f);
            }
            else if (UiScale < 0f)
            {
                changed |= Set(ref UiScale, 0f);
            }
            if (float.IsNaN(PanelX) || float.IsInfinity(PanelX)) changed |= Set(ref PanelX, -1f);
            if (float.IsNaN(PanelY) || float.IsInfinity(PanelY)) changed |= Set(ref PanelY, -1f);
            if (float.IsNaN(SettingsX) || float.IsInfinity(SettingsX)) changed |= Set(ref SettingsX, -1f);
            if (float.IsNaN(SettingsY) || float.IsInfinity(SettingsY)) changed |= Set(ref SettingsY, -1f);
            return changed;
        }

        private static bool Set(ref float field, float value)
        {
            if (field.Equals(value)) return false;
            field = value;
            return true;
        }

        private static bool Set(ref int field, int value)
        {
            if (field == value) return false;
            field = value;
            return true;
        }
    }
}
