using System;

namespace VanillaProfiler.Diagnostics
{
    /// <summary>
    /// Immutable persisted user preferences for the overlay and profiler core.
    /// Runtime code receives instances by value/reference and cannot mutate the
    /// global settings object behind SettingsStore.
    /// </summary>
    [Serializable]
    public sealed class ProfilerSettings
    {
        public ProfilerSettings(
            float reportIntervalSec = 5.0f,
            int defaultMode = 0,
            int anchor = 0,
            int sparklineWidth = 60,
            bool spikeScreenshots = false,
            float spikeThresholdMs = 100.0f,
            float syncPointThresholdMs = 0.5f,
            bool settingsPanelHotkey = true,
            bool profileVanillaSystems = false,
            bool hideHintBadge = true,
            float uiScale = 0f,
            float panelX = -1f,
            float panelY = -1f,
            float settingsX = -1f,
            float settingsY = -1f)
        {
            ReportIntervalSec = reportIntervalSec;
            DefaultMode = defaultMode;
            Anchor = anchor;
            SparklineWidth = sparklineWidth;
            SpikeScreenshots = spikeScreenshots;
            SpikeThresholdMs = spikeThresholdMs;
            SyncPointThresholdMs = syncPointThresholdMs;
            SettingsPanelHotkey = settingsPanelHotkey;
            ProfileVanillaSystems = profileVanillaSystems;
            HideHintBadge = hideHintBadge;
            UiScale = uiScale;
            PanelX = panelX;
            PanelY = panelY;
            SettingsX = settingsX;
            SettingsY = settingsY;
        }

        public float ReportIntervalSec { get; }
        public int DefaultMode { get; }
        public int Anchor { get; }
        public int SparklineWidth { get; }
        public bool SpikeScreenshots { get; }
        public float SpikeThresholdMs { get; }
        public float SyncPointThresholdMs { get; }
        public bool SettingsPanelHotkey { get; }
        public bool ProfileVanillaSystems { get; }
        public bool HideHintBadge { get; }
        public float UiScale { get; }
        public float PanelX { get; }
        public float PanelY { get; }
        public float SettingsX { get; }
        public float SettingsY { get; }

        public ProfilerSettings With(
            float? reportIntervalSec = null,
            int? defaultMode = null,
            int? anchor = null,
            int? sparklineWidth = null,
            bool? spikeScreenshots = null,
            float? spikeThresholdMs = null,
            float? syncPointThresholdMs = null,
            bool? settingsPanelHotkey = null,
            bool? profileVanillaSystems = null,
            bool? hideHintBadge = null,
            float? uiScale = null,
            float? panelX = null,
            float? panelY = null,
            float? settingsX = null,
            float? settingsY = null)
            => new ProfilerSettings(
                reportIntervalSec ?? ReportIntervalSec,
                defaultMode ?? DefaultMode,
                anchor ?? Anchor,
                sparklineWidth ?? SparklineWidth,
                spikeScreenshots ?? SpikeScreenshots,
                spikeThresholdMs ?? SpikeThresholdMs,
                syncPointThresholdMs ?? SyncPointThresholdMs,
                settingsPanelHotkey ?? SettingsPanelHotkey,
                profileVanillaSystems ?? ProfileVanillaSystems,
                hideHintBadge ?? HideHintBadge,
                uiScale ?? UiScale,
                panelX ?? PanelX,
                panelY ?? PanelY,
                settingsX ?? SettingsX,
                settingsY ?? SettingsY).Normalize();

        public ProfilerSettings Normalize() => Normalize(out _);

        public ProfilerSettings Normalize(out bool changed)
        {
            changed = false;
            float reportIntervalSec = ClampFloat(ReportIntervalSec, 1f, 60f, 5f, ref changed);
            int defaultMode = ClampInt(DefaultMode, 0, 5, 0, ref changed);
            int anchor = ClampInt(Anchor, 0, 3, 0, ref changed);
            int sparklineWidth = ClampInt(SparklineWidth, 10, 60, 60, ref changed);
            float spikeThresholdMs = ClampFloat(SpikeThresholdMs, 33f, 1000f, 100f, ref changed);
            float syncPointThresholdMs = ClampFloat(SyncPointThresholdMs, 0.05f, 10f, 0.5f, ref changed);
            float uiScale = NormalizeUiScale(UiScale, ref changed);
            float panelX = NormalizePosition(PanelX, ref changed);
            float panelY = NormalizePosition(PanelY, ref changed);
            float settingsX = NormalizePosition(SettingsX, ref changed);
            float settingsY = NormalizePosition(SettingsY, ref changed);

            if (!changed) return this;
            return new ProfilerSettings(
                reportIntervalSec,
                defaultMode,
                anchor,
                sparklineWidth,
                SpikeScreenshots,
                spikeThresholdMs,
                syncPointThresholdMs,
                SettingsPanelHotkey,
                ProfileVanillaSystems,
                HideHintBadge,
                uiScale,
                panelX,
                panelY,
                settingsX,
                settingsY);
        }

        private static float ClampFloat(float value, float min, float max, float fallback, ref bool changed)
        {
            float next = value;
            if (float.IsNaN(next) || float.IsInfinity(next)) next = fallback;
            if (next < min) next = min;
            if (next > max) next = max;
            if (next.Equals(value)) return next;
            changed = true;
            return next;
        }

        private static int ClampInt(int value, int min, int max, int fallback, ref bool changed)
        {
            int next = value;
            if (next < min || next > max) next = fallback;
            if (next == value) return next;
            changed = true;
            return next;
        }

        private static float NormalizeUiScale(float value, ref bool changed)
        {
            float next = value;
            if (float.IsNaN(next) || float.IsInfinity(next)) next = 0f;
            if (next > 0f)
            {
                if (next < 0.75f) next = 0.75f;
                if (next > 3f) next = 3f;
            }
            else if (next < 0f)
            {
                next = 0f;
            }
            if (next.Equals(value)) return next;
            changed = true;
            return next;
        }

        private static float NormalizePosition(float value, ref bool changed)
        {
            if (!float.IsNaN(value) && !float.IsInfinity(value)) return value;
            changed = true;
            return -1f;
        }
    }
}
