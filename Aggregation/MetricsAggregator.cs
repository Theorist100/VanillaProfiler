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

        private MetricsBuckets m_Buckets = MetricsBuckets.CreateNew();
        private readonly MetricsBucketPool m_BucketPool = new();

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

        public void RecordPhase(PhaseMeasurement measurement)
        {
            Add(m_Buckets.Phases, measurement.Name, measurement.Ticks, measurement.Ticks, measurement.Ticks, m_SyncPointTickThreshold);
        }

        public void RecordSystem(ProfiledSystemMeasurement measurement)
        {
            var identity = measurement.Identity;
            var dict = identity.IsVanilla ? m_Buckets.VanillaSystems : m_Buckets.ModSystems;
            Add(dict, identity.Name, measurement.SelfTicks, measurement.InclusiveTicks, measurement.SelfTicks, m_SyncPointTickThreshold);
            if (!identity.IsVanilla && !string.IsNullOrEmpty(identity.ModName))
                Add(m_Buckets.ModAggregate, identity.ModName, measurement.SelfTicks, measurement.InclusiveTicks, measurement.SelfTicks, m_SyncPointTickThreshold);
        }

        /// <summary>
        /// Records a SystemBase.Update measurement for a vanilla system whose
        /// OnUpdate is currently patched by a foreign Harmony prefix. Routes
        /// to a dedicated bucket so the Patched vanilla systems overlay
        /// section can show total ms regardless of <c>ProfileVanillaSystems</c>,
        /// without double-counting against <c>VanillaSystems</c>.
        /// </summary>
        public void RecordPatchedVanilla(ProfiledSystemMeasurement measurement)
        {
            Add(m_Buckets.PatchedVanillaSystems, measurement.Identity.Name, measurement.SelfTicks, measurement.InclusiveTicks, measurement.InclusiveTicks, m_SyncPointTickThreshold);
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
            MetricsBuckets drainedBuckets = m_Buckets;

            ResetCounters();
            m_Buckets = m_BucketPool.Rent();

            var sample = drainedBuckets.ToSample(m_WindowContext);
            sample.FrameTimeSum = frameTimeSum;
            sample.FrameTimeMax = frameTimeMax;
            sample.FrameCount = frameCount;
            sample.SimTickCount = simTickCount;
            sample.Spikes30 = spikes30;
            sample.Spikes20 = spikes20;
            return new MetricsLease(this, sample);
        }

        internal void Recycle(MetricsSample sample)
        {
            m_BucketPool.Return(sample);
            sample.Buckets = MetricsBucketSnapshot.Empty;
            sample.WindowContext = ReportWindowContext.Empty;
        }

        public void Reset()
        {
            ResetCounters();
            m_Buckets.Clear();
            m_BucketPool.Clear();
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

    }
}
