using System;

namespace VanillaProfiler.Aggregation
{
    /// <summary>
    /// Owns a drained metrics sample until report building/log sinks finish reading it.
    /// Disposing returns the borrowed dictionaries to MetricsAggregator.
    /// </summary>
    public sealed class MetricsLease : IDisposable
    {
        private MetricsAggregator m_Owner;

        internal MetricsLease(MetricsAggregator owner, MetricsSample sample)
        {
            m_Owner = owner;
            Sample = sample;
        }

        public MetricsSample Sample { get; private set; }

        public void Dispose()
        {
            if (m_Owner == null) return;
            m_Owner.Recycle(Sample);
            Sample = null;
            m_Owner = null;
        }
    }
}
