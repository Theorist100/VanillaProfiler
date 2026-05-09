namespace VanillaProfiler.Aggregation
{
    /// <summary>Mutable accumulator for a single phase or system over one reporting window.</summary>
    public sealed class PhaseData
    {
        /// <summary>
        /// Exclusive/self cost. For phase buckets this is equal to InclusiveTicks.
        /// For SystemBase.Update buckets nested system updates are subtracted.
        /// </summary>
        public long SelfTicks;

        /// <summary>
        /// Full elapsed Update/phase cost, including nested SystemBase.Update calls.
        /// Kept so diagnostics can still explain total Update time where that is
        /// the honest signal (for example patched vanilla systems).
        /// </summary>
        public long InclusiveTicks;

        public long TotalTicks => SelfTicks;
        public long MaxTicks;
        public int CallCount;
        // Number of individual calls that exceeded SyncPointThresholdMs. A non-zero
        // count means at least one Update() did real main-thread work (sync point,
        // structural change, ECB playback, or a synchronous foreach), as opposed to
        // pure scheduling overhead. Surfaced in reports as "[likely sync point]".
        public int SyncPointSuspectCount;
    }
}
