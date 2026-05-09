using System.Collections.Generic;

namespace VanillaProfiler.Aggregation
{
    internal sealed class MetricsBuckets
    {
        public MetricsBuckets(
            Dictionary<string, PhaseData> phases,
            Dictionary<string, PhaseData> vanillaSystems,
            Dictionary<string, PhaseData> modSystems,
            Dictionary<string, PhaseData> modAggregate,
            Dictionary<string, PhaseData> patchedVanillaSystems)
        {
            Phases = phases;
            VanillaSystems = vanillaSystems;
            ModSystems = modSystems;
            ModAggregate = modAggregate;
            PatchedVanillaSystems = patchedVanillaSystems;
        }

        public Dictionary<string, PhaseData> Phases { get; }
        public Dictionary<string, PhaseData> VanillaSystems { get; }
        public Dictionary<string, PhaseData> ModSystems { get; }
        public Dictionary<string, PhaseData> ModAggregate { get; }
        public Dictionary<string, PhaseData> PatchedVanillaSystems { get; }

        public static MetricsBuckets CreateNew()
        {
            return new MetricsBuckets(
                new Dictionary<string, PhaseData>(),
                new Dictionary<string, PhaseData>(),
                new Dictionary<string, PhaseData>(),
                new Dictionary<string, PhaseData>(),
                new Dictionary<string, PhaseData>());
        }

        public MetricsSample ToSample(ReportWindowContext windowContext)
        {
            return new MetricsSample
            {
                WindowContext = windowContext,
                Buckets = ToSnapshot(),
            };
        }

        public MetricsBucketSnapshot ToSnapshot()
        {
            return new MetricsBucketSnapshot(
                Phases,
                VanillaSystems,
                ModSystems,
                ModAggregate,
                PatchedVanillaSystems);
        }

        public void Clear()
        {
            Phases.Clear();
            VanillaSystems.Clear();
            ModSystems.Clear();
            ModAggregate.Clear();
            PatchedVanillaSystems.Clear();
        }
    }

    public sealed class MetricsBucketSnapshot
    {
        private static readonly IReadOnlyDictionary<string, PhaseData> EmptyPhaseData =
            new Dictionary<string, PhaseData>();

        internal static readonly MetricsBucketSnapshot Empty = new MetricsBucketSnapshot(
            EmptyPhaseData,
            EmptyPhaseData,
            EmptyPhaseData,
            EmptyPhaseData,
            EmptyPhaseData);

        public MetricsBucketSnapshot(
            IReadOnlyDictionary<string, PhaseData> phases,
            IReadOnlyDictionary<string, PhaseData> vanillaSystems,
            IReadOnlyDictionary<string, PhaseData> modSystems,
            IReadOnlyDictionary<string, PhaseData> modAggregate,
            IReadOnlyDictionary<string, PhaseData> patchedVanillaSystems)
        {
            Phases = phases ?? EmptyPhaseData;
            VanillaSystems = vanillaSystems ?? EmptyPhaseData;
            ModSystems = modSystems ?? EmptyPhaseData;
            ModAggregate = modAggregate ?? EmptyPhaseData;
            PatchedVanillaSystems = patchedVanillaSystems ?? EmptyPhaseData;
        }

        public IReadOnlyDictionary<string, PhaseData> Phases { get; }
        public IReadOnlyDictionary<string, PhaseData> VanillaSystems { get; }
        public IReadOnlyDictionary<string, PhaseData> ModSystems { get; }
        public IReadOnlyDictionary<string, PhaseData> ModAggregate { get; }
        public IReadOnlyDictionary<string, PhaseData> PatchedVanillaSystems { get; }
    }

    internal sealed class MetricsBucketPool
    {
        private MetricsBuckets? m_Spare;

        public MetricsBuckets Rent()
        {
            var buckets = m_Spare;
            if (buckets == null)
                return MetricsBuckets.CreateNew();

            m_Spare = null;
            buckets.Clear();
            return buckets;
        }

        public void Return(MetricsSample sample)
        {
            if (m_Spare != null) return;
            if (sample.Buckets.Phases is not Dictionary<string, PhaseData> phases) return;
            if (sample.Buckets.VanillaSystems is not Dictionary<string, PhaseData> vanillaSystems) return;
            if (sample.Buckets.ModSystems is not Dictionary<string, PhaseData> modSystems) return;
            if (sample.Buckets.ModAggregate is not Dictionary<string, PhaseData> modAggregate) return;
            if (sample.Buckets.PatchedVanillaSystems is not Dictionary<string, PhaseData> patchedVanillaSystems) return;

            var buckets = new MetricsBuckets(
                phases,
                vanillaSystems,
                modSystems,
                modAggregate,
                patchedVanillaSystems);
            buckets.Clear();
            m_Spare = buckets;
        }

        public void Clear()
        {
            m_Spare?.Clear();
        }
    }
}
