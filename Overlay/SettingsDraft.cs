using VanillaProfiler.Diagnostics;

namespace VanillaProfiler.Overlay
{
    /// <summary>
    /// Pure helpers for working with a <see cref="ProfilerSettings"/> draft —
    /// the Clone-Apply pattern used by SettingsPanel. Kept as static utilities so
    /// the panel itself doesn't have to host trivial copy logic.
    /// </summary>
    public static class SettingsDraft
    {
        /// <summary>Deep-copy a settings instance so edits don't mutate the live one.</summary>
        public static ProfilerSettings Clone(ProfilerSettings src) => new()
        {
            ReportIntervalSec = src.ReportIntervalSec,
            DefaultMode = src.DefaultMode,
            Anchor = src.Anchor,
            SparklineWidth = src.SparklineWidth,
            SpikeScreenshots = src.SpikeScreenshots,
            SpikeThresholdMs = src.SpikeThresholdMs,
            SettingsPanelHotkey = src.SettingsPanelHotkey,
            UiScale = src.UiScale,
            PanelX = src.PanelX,
            PanelY = src.PanelY,
            SettingsX = src.SettingsX,
            SettingsY = src.SettingsY,
        };
    }
}
