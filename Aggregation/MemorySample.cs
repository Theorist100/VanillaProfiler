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
        public long SystemUsedBytes;

        public long ManagedDelta;
        public long MonoHeapDelta;
        public long NativeAllocDelta;
        public long NativeReservedDelta;
        public long GfxUsedDelta;
        public long AudioUsedDelta;

        public double ManagedGrowthMBperSec;
        public bool BaselineJustCaptured;

        // Per-frame averages from ProfilerCategory.Render (nanoseconds, raw).
        public long MainThreadCpuNs;
        public long RenderThreadCpuNs;
        public long GpuFrameTimeNs;
        // CPU main-thread time spent waiting on the GPU swapchain — direct GPU-bound
        // indicator. High PresentWait + low Main = GPU bottleneck, ECS optimisation
        // does nothing for FPS.
        public long PresentWaitNs;

        // Per-frame render counts from ProfilerCategory.Render. Counts (not bytes/ns).
        // Zero when the marker is stripped on this build.
        public long DrawCallsCount;
        public long SetPassCallsCount;
        public long TrianglesCount;
        public long VerticesCount;
        // Number of shadow-casting renderers visible this frame. Sudden jumps reveal
        // shadow-rendering load spikes (e.g. new prop pack with shadow-cast meshes).
        public long ShadowCastersCount;

        // GPU memory breakdown — splits the opaque "Gfx total" into buffers vs textures.
        // Used Buffers grows with mesh/data uploads; Render Textures grows with
        // framebuffer/RT allocation (overdraw, VFX trails).
        public long UsedBuffersBytes;
        public long UsedBuffersCount;
        public long RenderTexturesBytes;

        // GC.Collect total stall time over the report window + how many collections
        // occurred. Distinguishes GC-induced stutter from non-GC main-thread stalls.
        public long GcCollectTotalNs;
        public long GcCollectCount;

        // Process resident set (physical RAM actually held). Diverges from
        // System Used Memory when the OS pages parts out under pressure — catches
        // swap-thrash before a hard OOM crash.
        public long AppResidentBytes;
    }
}
