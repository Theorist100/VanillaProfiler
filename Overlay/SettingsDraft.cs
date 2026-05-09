using VanillaProfiler.Diagnostics;

namespace VanillaProfiler.Overlay
{
    /// <summary>
    /// Mutable form state for SettingsPanel. The persisted ProfilerSettings object
    /// stays immutable; this draft absorbs IMGUI edits until Apply builds a new
    /// ProfilerSettings instance.
    /// </summary>
    public sealed class SettingsDraft
    {
        public float ReportIntervalSec;
        public int DefaultMode;
        public int Anchor;
        public int SparklineWidth;
        public bool SpikeScreenshots;
        public float SpikeThresholdMs;
        public float SyncPointThresholdMs;
        public bool SettingsPanelHotkey;
        public bool ProfileVanillaSystems;
        public bool HideHintBadge;
        public float UiScale;
        public float PanelX;
        public float PanelY;
        public float SettingsX;
        public float SettingsY;

        public SettingsDraft(ProfilerSettings settings)
        {
            ReportIntervalSec = settings.ReportIntervalSec;
            DefaultMode = settings.DefaultMode;
            Anchor = settings.Anchor;
            SparklineWidth = settings.SparklineWidth;
            SpikeScreenshots = settings.SpikeScreenshots;
            SpikeThresholdMs = settings.SpikeThresholdMs;
            SyncPointThresholdMs = settings.SyncPointThresholdMs;
            SettingsPanelHotkey = settings.SettingsPanelHotkey;
            ProfileVanillaSystems = settings.ProfileVanillaSystems;
            HideHintBadge = settings.HideHintBadge;
            UiScale = settings.UiScale;
            PanelX = settings.PanelX;
            PanelY = settings.PanelY;
            SettingsX = settings.SettingsX;
            SettingsY = settings.SettingsY;
        }

        public ProfilerSettings ToSettings()
            => new(
                ReportIntervalSec,
                DefaultMode,
                Anchor,
                SparklineWidth,
                SpikeScreenshots,
                SpikeThresholdMs,
                SyncPointThresholdMs,
                SettingsPanelHotkey,
                ProfileVanillaSystems,
                HideHintBadge,
                UiScale,
                PanelX,
                PanelY,
                SettingsX,
                SettingsY);
    }
}
