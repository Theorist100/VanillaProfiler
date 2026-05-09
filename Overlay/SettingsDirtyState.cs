using VanillaProfiler.Diagnostics;

namespace VanillaProfiler.Overlay
{
    /// <summary>
    /// Tracks which fields were edited in SettingsPanel and knows how to merge
    /// the local draft into the current immutable settings object.
    /// </summary>
    internal sealed class SettingsDirtyState
    {
        public bool ReportInterval { get; set; }
        public bool DefaultMode { get; set; }
        public bool Anchor { get; set; }
        public bool SparklineWidth { get; set; }
        public bool SpikeScreenshots { get; set; }
        public bool SpikeThreshold { get; set; }
        public bool UiScale { get; set; }
        public bool ProfileVanilla { get; set; }
        public bool HideHintBadge { get; set; }

        public void SyncLiveSettings(SettingsDraft draft, ProfilerSettings live)
        {
            if (!Anchor)
                draft.Anchor = live.Anchor;
            if (!SpikeScreenshots)
                draft.SpikeScreenshots = live.SpikeScreenshots;
            if (!ProfileVanilla)
                draft.ProfileVanillaSystems = live.ProfileVanillaSystems;
        }

        public ProfilerSettings Merge(ProfilerSettings live, SettingsDraft draft)
            => live.With(
                reportIntervalSec: ReportInterval ? draft.ReportIntervalSec : null,
                defaultMode: DefaultMode ? draft.DefaultMode : null,
                anchor: Anchor ? draft.Anchor : null,
                sparklineWidth: SparklineWidth ? draft.SparklineWidth : null,
                spikeScreenshots: SpikeScreenshots ? draft.SpikeScreenshots : null,
                spikeThresholdMs: SpikeThreshold ? draft.SpikeThresholdMs : null,
                uiScale: UiScale ? draft.UiScale : null,
                profileVanillaSystems: ProfileVanilla ? draft.ProfileVanillaSystems : null,
                hideHintBadge: HideHintBadge ? draft.HideHintBadge : null);

        public void Clear()
        {
            ReportInterval = false;
            DefaultMode = false;
            Anchor = false;
            SparklineWidth = false;
            SpikeScreenshots = false;
            SpikeThreshold = false;
            UiScale = false;
            ProfileVanilla = false;
            HideHintBadge = false;
        }

        public void MarkAll()
        {
            ReportInterval = true;
            DefaultMode = true;
            Anchor = true;
            SparklineWidth = true;
            SpikeScreenshots = true;
            SpikeThreshold = true;
            UiScale = true;
            ProfileVanilla = true;
            HideHintBadge = true;
        }
    }
}
