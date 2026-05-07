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
        /// <remarks>
        /// Every public field on ProfilerSettings must be listed here. Missing a field means
        /// SettingsPanel.Apply (which builds <c>merged = Clone(SettingsStore.Current)</c>
        /// and only re-applies dirty fields) will silently revert that field to its
        /// compile-time default on every Apply. SyncPointThresholdMs and ProfileVanillaSystems
        /// were the original casualties — kept the list exhaustive after that bug.
        /// </remarks>
        public static ProfilerSettings Clone(ProfilerSettings src) => new()
        {
            ReportIntervalSec = src.ReportIntervalSec,
            DefaultMode = src.DefaultMode,
            Anchor = src.Anchor,
            SparklineWidth = src.SparklineWidth,
            SpikeScreenshots = src.SpikeScreenshots,
            SpikeThresholdMs = src.SpikeThresholdMs,
            SyncPointThresholdMs = src.SyncPointThresholdMs,
            SettingsPanelHotkey = src.SettingsPanelHotkey,
            ProfileVanillaSystems = src.ProfileVanillaSystems,
            UiScale = src.UiScale,
            PanelX = src.PanelX,
            PanelY = src.PanelY,
            SettingsX = src.SettingsX,
            SettingsY = src.SettingsY,
        };
    }
}
