using System;
using System.Diagnostics;
using VanillaProfiler.Diagnostics;

namespace VanillaProfiler
{
    /// <summary>
    /// Immutable state captured for one reporting window. Hot-path routing and
    /// report attachment both read this same instance so old metrics are never
    /// labelled with freshly scanned settings or Harmony replacement state.
    /// </summary>
    public sealed class ReportWindowContext
    {
        public static readonly ReportWindowContext Empty = new(
            0,
            SystemReplacementDetector.ReplacementSnapshot.Empty,
            SettingsStore.Snapshot);

        public ReportWindowContext(
            long windowId,
            SystemReplacementDetector.ReplacementSnapshot replacements,
            ProfilerSettingsSnapshot settings)
        {
            WindowId = windowId;
            Replacements = replacements ?? throw new ArgumentNullException(nameof(replacements));
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            SyncPointThresholdTicks = ComputeSyncPointTicks(settings.Settings.SyncPointThresholdMs);
        }

        public long WindowId { get; }
        public SystemReplacementDetector.ReplacementSnapshot Replacements { get; }
        public ProfilerSettingsSnapshot Settings { get; }
        public long SyncPointThresholdTicks { get; }

        private static long ComputeSyncPointTicks(float thresholdMs)
        {
            long ticks = (long)(Stopwatch.Frequency * (thresholdMs / 1000.0));
            return ticks > 0 ? ticks : 1;
        }
    }
}
