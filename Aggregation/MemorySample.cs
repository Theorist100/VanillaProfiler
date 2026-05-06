namespace VanillaProfiler.Aggregation
{
    /// <summary>Snapshot of memory totals + delta from baseline + managed heap growth rate.</summary>
    public sealed class MemorySample
    {
        public long ManagedBytes;
        public long MonoHeapBytes;
        public long NativeAllocBytes;
        public long NativeReservedBytes;

        public long ManagedDelta;
        public long MonoHeapDelta;
        public long NativeAllocDelta;
        public long NativeReservedDelta;

        public double ManagedGrowthMBperSec;
        public bool BaselineJustCaptured;
    }
}
