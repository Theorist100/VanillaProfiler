using System.Collections.Generic;

namespace VanillaProfiler.Aggregation
{
    /// <summary>
    /// Single-threaded accumulator for per-frame, per-phase and per-system metrics.
    /// Unity Entities and Harmony call the profiler on the main thread; readers go through Drain().
    /// </summary>
    public sealed class MetricsAggregator
    {
        private long m_FrameTimeSum;
        private long m_FrameTimeMax;
        private int m_FrameCount;
        private int m_SimTickCount;
        private int m_Spikes30;
        private int m_Spikes20;

        private ReportWindowContext m_WindowContext = ReportWindowContext.Empty;
        private long m_SyncPointTickThreshold;

        private Dictionary<string, PhaseData> m_Phases = new();
        private Dictionary<string, PhaseData> m_VanillaSystems = new();
        private Dictionary<string, PhaseData> m_ModSystems = new();
        private Dictionary<string, PhaseData> m_ModAggregate = new();
        private Dictionary<string, PhaseData> m_PatchedVanillaSystems = new();

        private Dictionary<string, PhaseData>? m_SparePhases = new();
        private Dictionary<string, PhaseData>? m_SpareVanillaSystems = new();
        private Dictionary<string, PhaseData>? m_SpareModSystems = new();
        private Dictionary<string, PhaseData>? m_SpareModAggregate = new();
        private Dictionary<string, PhaseData>? m_SparePatchedVanillaSystems = new();

        public MetricsAggregator(ReportWindowContext initialWindow)
        {
            StartWindow(initialWindow);
        }

        public void StartWindow(ReportWindowContext context)
        {
            m_WindowContext = context ?? ReportWindowContext.Empty;
            m_SyncPointTickThreshold = m_WindowContext.SyncPointThresholdTicks;
        }

        public void RecordSimTick()
        {
            m_SimTickCount++;
        }

        public void RecordFrame(long deltaTicks, double frameMs, double spike30Ms, double spike20Ms)
        {
            m_FrameTimeSum += deltaTicks;
            m_FrameCount++;
            if (deltaTicks > m_FrameTimeMax) m_FrameTimeMax = deltaTicks;

            if (frameMs > spike20Ms) { m_Spikes20++; m_Spikes30++; }
            else if (frameMs > spike30Ms) m_Spikes30++;
        }

        public void RecordPhase(string name, long ticks)
        {
            Add(m_Phases, name, ticks, ticks, ticks, m_SyncPointTickThreshold);
        }

        public void RecordSystem(string name, long selfTicks, long inclusiveTicks, bool isVanilla, string? modName)
        {
            var dict = isVanilla ? m_VanillaSystems : m_ModSystems;
            Add(dict, name, selfTicks, inclusiveTicks, selfTicks, m_SyncPointTickThreshold);
            if (!isVanilla && !string.IsNullOrEmpty(modName))
                Add(m_ModAggregate, modName!, selfTicks, inclusiveTicks, selfTicks, m_SyncPointTickThreshold);
        }

        /// <summary>
        /// Records a SystemBase.Update measurement for a vanilla system whose
        /// OnUpdate is currently patched by a foreign Harmony prefix. Routes
        /// to a dedicated bucket so the Patched vanilla systems overlay
        /// section can show total ms regardless of <c>ProfileVanillaSystems</c>,
        /// without double-counting against <c>VanillaSystems</c>.
        /// </summary>
        public void RecordPatchedVanilla(string name, long selfTicks, long inclusiveTicks)
        {
            Add(m_PatchedVanillaSystems, name, selfTicks, inclusiveTicks, inclusiveTicks, m_SyncPointTickThreshold);
        }

        /// <summary>Swaps out accumulated state; dispose the lease to return buffers.</summary>
        public MetricsLease Drain()
        {
            long frameTimeSum = m_FrameTimeSum;
            long frameTimeMax = m_FrameTimeMax;
            int frameCount = m_FrameCount;
            int simTickCount = m_SimTickCount;
            int spikes30 = m_Spikes30;
            int spikes20 = m_Spikes20;
            Dictionary<string, PhaseData> phases = m_Phases;
            Dictionary<string, PhaseData> vanillaSystems = m_VanillaSystems;
            Dictionary<string, PhaseData> modSystems = m_ModSystems;
            Dictionary<string, PhaseData> modAggregate = m_ModAggregate;
            Dictionary<string, PhaseData> patchedVanillaSystems = m_PatchedVanillaSystems;

            ResetCounters();
            m_Phases = TakeSpare(ref m_SparePhases);
            m_VanillaSystems = TakeSpare(ref m_SpareVanillaSystems);
            m_ModSystems = TakeSpare(ref m_SpareModSystems);
            m_ModAggregate = TakeSpare(ref m_SpareModAggregate);
            m_PatchedVanillaSystems = TakeSpare(ref m_SparePatchedVanillaSystems);

            var sample = new MetricsSample
            {
                WindowContext = m_WindowContext,
                FrameTimeSum = frameTimeSum,
                FrameTimeMax = frameTimeMax,
                FrameCount = frameCount,
                SimTickCount = simTickCount,
                Spikes30 = spikes30,
                Spikes20 = spikes20,
                Phases = phases,
                VanillaSystems = vanillaSystems,
                ModSystems = modSystems,
                ModAggregate = modAggregate,
                PatchedVanillaSystems = patchedVanillaSystems,
            };
            return new MetricsLease(this, sample);
        }

        internal void Recycle(MetricsSample sample)
        {
            StoreSpare(ref m_SparePhases, sample.Phases);
            StoreSpare(ref m_SpareVanillaSystems, sample.VanillaSystems);
            StoreSpare(ref m_SpareModSystems, sample.ModSystems);
            StoreSpare(ref m_SpareModAggregate, sample.ModAggregate);
            StoreSpare(ref m_SparePatchedVanillaSystems, sample.PatchedVanillaSystems);
            sample.Phases = MetricsSample.EmptyPhaseData;
            sample.VanillaSystems = MetricsSample.EmptyPhaseData;
            sample.ModSystems = MetricsSample.EmptyPhaseData;
            sample.ModAggregate = MetricsSample.EmptyPhaseData;
            sample.PatchedVanillaSystems = MetricsSample.EmptyPhaseData;
            sample.WindowContext = ReportWindowContext.Empty;
        }

        public void Reset()
        {
            ResetCounters();
            m_Phases.Clear();
            m_VanillaSystems.Clear();
            m_ModSystems.Clear();
            m_ModAggregate.Clear();
            m_PatchedVanillaSystems.Clear();
            m_SparePhases?.Clear();
            m_SpareVanillaSystems?.Clear();
            m_SpareModSystems?.Clear();
            m_SpareModAggregate?.Clear();
            m_SparePatchedVanillaSystems?.Clear();
        }

        private static void Add(
            Dictionary<string, PhaseData> dict,
            string key,
            long selfTicks,
            long inclusiveTicks,
            long syncSuspectTicks,
            long syncPointTickThreshold)
        {
            if (!dict.TryGetValue(key, out var data))
            {
                data = new PhaseData();
                dict[key] = data;
            }
            data.SelfTicks += selfTicks;
            data.InclusiveTicks += inclusiveTicks;
            data.CallCount++;
            if (inclusiveTicks > data.MaxTicks) data.MaxTicks = inclusiveTicks;
            if (syncSuspectTicks >= syncPointTickThreshold) data.SyncPointSuspectCount++;
        }

        private void ResetCounters()
        {
            m_FrameTimeSum = 0;
            m_FrameTimeMax = 0;
            m_FrameCount = 0;
            m_SimTickCount = 0;
            m_Spikes30 = 0;
            m_Spikes20 = 0;
        }

        private static Dictionary<string, PhaseData> TakeSpare(ref Dictionary<string, PhaseData>? spare)
        {
            var dict = spare ?? new Dictionary<string, PhaseData>();
            spare = null;
            dict.Clear();
            return dict;
        }

        private static void StoreSpare(ref Dictionary<string, PhaseData>? spare, IReadOnlyDictionary<string, PhaseData> sample)
        {
            if (sample is not Dictionary<string, PhaseData> dict || spare != null) return;
            dict.Clear();
            spare = dict;
        }
    }
}
