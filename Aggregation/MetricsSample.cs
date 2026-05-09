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
        public IReadOnlyDictionary<string, PhaseData> Phases = new Dictionary<string, PhaseData>();
        public IReadOnlyDictionary<string, PhaseData> VanillaSystems = new Dictionary<string, PhaseData>();
        public IReadOnlyDictionary<string, PhaseData> ModSystems = new Dictionary<string, PhaseData>();
        public IReadOnlyDictionary<string, PhaseData> ModAggregate = new Dictionary<string, PhaseData>();

        // Vanilla systems whose OnUpdate currently has a foreign Harmony
        // prefix. Populated unconditionally (independent of the
        // ProfileVanillaSystems setting) so the Patched vanilla systems
        // section can show total Update ms — that elapsed time blends the
        // mod's prefix and the vanilla original, but the total is honest.
        // Disjoint from VanillaSystems so totals are not double-counted.
        public IReadOnlyDictionary<string, PhaseData> PatchedVanillaSystems = new Dictionary<string, PhaseData>();
    }
}
