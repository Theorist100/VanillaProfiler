namespace VanillaProfiler.Aggregation
{
    /// <summary>Mutable accumulator for a single phase or system over one reporting window.</summary>
    public sealed class PhaseData
    {
        public long TotalTicks;
        public long MaxTicks;
        public int CallCount;
        // Number of individual calls that exceeded SyncPointThresholdMs. A non-zero
        // count means at least one Update() did real main-thread work (sync point,
        // structural change, ECB playback, or a synchronous foreach), as opposed to
        // pure scheduling overhead. Surfaced in reports as "[likely sync point]".
        public int SyncPointSuspectCount;
    }
}
