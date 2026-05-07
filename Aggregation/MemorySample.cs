namespace VanillaProfiler.Aggregation
{
    /// <summary>Snapshot of memory totals + delta from baseline + managed heap growth rate.</summary>
    public sealed class MemorySample
    {
        public long ManagedBytes;
        public long MonoHeapBytes;
        public long NativeAllocBytes;
        public long NativeReservedBytes;

        // Extra categories from Unity's ProfilerRecorder (ProfilerCategory.Memory).
        // Zero when the recorder is unavailable on a given platform/build.
        public long GfxUsedBytes;
        public long AudioUsedBytes;
        public long VideoUsedBytes;
        public long SystemUsedBytes;

        public long ManagedDelta;
        public long MonoHeapDelta;
        public long NativeAllocDelta;
        public long NativeReservedDelta;
        public long GfxUsedDelta;
        public long AudioUsedDelta;
        public long VideoUsedDelta;

        public double ManagedGrowthMBperSec;
        public bool BaselineJustCaptured;

        // Per-frame averages from ProfilerCategory.Render (nanoseconds, raw).
        public long MainThreadCpuNs;
        public long RenderThreadCpuNs;
        public long GpuFrameTimeNs;

        // Aggregate job worker time per frame from ProfilerCategory.Internal markers,
        // when exposed by the build. Zero when no marker is available.
        // No per-system attribution — this is a "total work on workers this frame"
        // honest stand-in for what Unity Profiler shows on its worker timeline.
        public long JobWorkerTimeNs;
        public long JobWorkerWaitNs;   // Time main thread spent waiting on workers (real sync cost).
    }
}
