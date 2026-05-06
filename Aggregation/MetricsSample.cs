using System.Collections.Generic;

namespace VanillaProfiler.Aggregation
{
    /// <summary>
    /// Result of an atomic Drain() — the data accumulated since the last drain.
    /// Dictionaries are borrowed from MetricsAggregator and returned when the owning
    /// MetricsLease is disposed.
    /// </summary>
    public sealed class MetricsSample
    {
        public long FrameTimeSum;
        public long FrameTimeMax;
        public float ElapsedSec;
        public int FrameCount;
        public int SimTickCount;
        public int Spikes30;
        public int Spikes20;
        public Dictionary<string, PhaseData> Phases;
        public Dictionary<string, PhaseData> VanillaSystems;
        public Dictionary<string, PhaseData> ModSystems;
        public Dictionary<string, PhaseData> ModAggregate;
    }
}
