namespace VanillaProfiler.Aggregation
{
    /// <summary>Mutable accumulator for a single phase or system over one reporting window.</summary>
    public sealed class PhaseData
    {
        public long TotalTicks;
        public long MaxTicks;
        public int CallCount;
    }
}
