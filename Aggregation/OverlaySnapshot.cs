namespace VanillaProfiler
{
    /// <summary>
    /// Immutable view of the last reporting window. Read by overlay, exporter, and tests.
    /// Built by ReportBuilder — never mutated after construction.
    /// </summary>
    public sealed class OverlaySnapshot
    {
        public double AvgFps;
        public float WindowSeconds;
        public double MinFps;
        public double AvgFrameMs;
        public double MaxFrameMs;
        public double SimTicksPerSec;
        public double ManagedGrowthMBperSec;
        public int Spikes30fps;
        public int Spikes20fps;
        public (string Name, double TotalMs)[] TopVanillaSystems;
        public (string Name, double TotalMs)[] TopModSystems;
        public (string ModName, double TotalMs)[] TopMods;
        public double ManagedMB;
        public double ManagedDeltaMB;

        // Profiler's own measured cost over the window. Always populated so the
        // overlay can advertise "we cost X ms / Y%" up front and players can see
        // for themselves that the profiler isn't the bottleneck.
        public double ProfilerSelfMs;
        public double ProfilerSelfPercent;

        // Extra memory + CPU categories sourced from Unity's ProfilerRecorder
        // (Gfx/Audio used; main + render thread frame time). 0 when the platform
        // doesn't expose the marker — caller should branch on > 0 to render.
        public double GfxUsedMB;
        public double AudioUsedMB;
        public double MainThreadCpuMs;
        public double RenderThreadCpuMs;
        public double GpuFrameTimeMs;
    }
}
