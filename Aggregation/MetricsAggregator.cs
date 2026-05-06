using System.Collections.Generic;
using System.Diagnostics;

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

        private Dictionary<string, PhaseData> m_Phases = new();
        private Dictionary<string, PhaseData> m_VanillaSystems = new();
        private Dictionary<string, PhaseData> m_ModSystems = new();
        private Dictionary<string, PhaseData> m_ModAggregate = new();

        private Dictionary<string, PhaseData> m_SparePhases = new();
        private Dictionary<string, PhaseData> m_SpareVanillaSystems = new();
        private Dictionary<string, PhaseData> m_SpareModSystems = new();
        private Dictionary<string, PhaseData> m_SpareModAggregate = new();

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
            Add(m_Phases, name, ticks);
        }

        public void RecordSystem(string name, long ticks, bool isVanilla, string modName)
        {
            var dict = isVanilla ? m_VanillaSystems : m_ModSystems;
            Add(dict, name, ticks);
            if (!isVanilla && !string.IsNullOrEmpty(modName))
                Add(m_ModAggregate, modName, ticks);
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

            ResetCounters();
            m_Phases = TakeSpare(ref m_SparePhases);
            m_VanillaSystems = TakeSpare(ref m_SpareVanillaSystems);
            m_ModSystems = TakeSpare(ref m_SpareModSystems);
            m_ModAggregate = TakeSpare(ref m_SpareModAggregate);

            var sample = new MetricsSample
            {
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
            };
            return new MetricsLease(this, sample);
        }

        internal void Recycle(MetricsSample sample)
        {
            if (sample == null) return;
            StoreSpare(ref m_SparePhases, sample.Phases);
            StoreSpare(ref m_SpareVanillaSystems, sample.VanillaSystems);
            StoreSpare(ref m_SpareModSystems, sample.ModSystems);
            StoreSpare(ref m_SpareModAggregate, sample.ModAggregate);
            sample.Phases = null;
            sample.VanillaSystems = null;
            sample.ModSystems = null;
            sample.ModAggregate = null;
        }

        public void Reset()
        {
            ResetCounters();
            m_Phases.Clear();
            m_VanillaSystems.Clear();
            m_ModSystems.Clear();
            m_ModAggregate.Clear();
            m_SparePhases?.Clear();
            m_SpareVanillaSystems?.Clear();
            m_SpareModSystems?.Clear();
            m_SpareModAggregate?.Clear();
        }

        private static void Add(Dictionary<string, PhaseData> dict, string key, long ticks)
        {
            if (!dict.TryGetValue(key, out var data))
            {
                data = new PhaseData();
                dict[key] = data;
            }
            data.TotalTicks += ticks;
            data.CallCount++;
            if (ticks > data.MaxTicks) data.MaxTicks = ticks;
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

        private static Dictionary<string, PhaseData> TakeSpare(ref Dictionary<string, PhaseData> spare)
        {
            var dict = spare ?? new Dictionary<string, PhaseData>();
            spare = null;
            dict.Clear();
            return dict;
        }

        private static void StoreSpare(ref Dictionary<string, PhaseData> spare, Dictionary<string, PhaseData> dict)
        {
            if (dict == null || spare != null) return;
            dict.Clear();
            spare = dict;
        }
    }
}
