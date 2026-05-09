namespace VanillaProfiler.Diagnostics
{
    public enum HealthLevel
    {
        Unknown = 0,
        Good,
        Ok,
        Poor,
    }

    public enum BottleneckKind
    {
        Unknown = 0,
        Balanced,
        GpuBound,
        CpuRenderBound,
        SimBound,
        MemoryBound,
    }

    public enum RenderCause
    {
        None = 0,
        PresentWait,
        CpuRenderSubmission,
        GpuUnderutilizedByCpuRender,
    }

    /// <summary>Confidence that a render-related bottleneck is actionable. Drives advice depth.</summary>
    public enum RenderSeverityLevel
    {
        None = 0,    // no render cause, or signal too weak to act on
        Mild,        // render is heavier than sim but within normal range
        Severe,      // multiple signals point to a CPU-side render lock
    }

    /// <summary>
    /// Player-facing summary of the latest configured report window.
    /// Hints carry short, actionable strings — overlays render them verbatim.
    /// </summary>
    public sealed class HealthReport
    {
        public static readonly HealthReport Unknown = new HealthReport(
            HealthLevel.Unknown,
            HealthLevel.Unknown,
            HealthLevel.Unknown,
            HealthLevel.Unknown,
            HealthLevel.Unknown,
            "Stable",
            new RenderBottleneckResult(
                BottleneckKind.Unknown,
                RenderCause.None,
                "Collecting data...",
                0,
                0,
                RenderSeverityLevel.None,
                0,
                0,
                false));

        public HealthReport(
            HealthLevel fpsLevel,
            HealthLevel stutterLevel,
            HealthLevel memoryLevel,
            HealthLevel growthLevel,
            HealthLevel overall,
            string memoryHint,
            RenderBottleneckResult bottleneck)
        {
            FpsLevel = fpsLevel;
            StutterLevel = stutterLevel;
            MemoryLevel = memoryLevel;
            GrowthLevel = growthLevel;
            Overall = overall;
            MemoryHint = string.IsNullOrEmpty(memoryHint) ? "Stable" : memoryHint;
            Bottleneck = bottleneck.Bottleneck;
            BottleneckHint = string.IsNullOrEmpty(bottleneck.Hint) ? "Collecting data..." : bottleneck.Hint;
            RenderPhaseMs = bottleneck.RenderPhaseMs;
            SimPhaseMs = bottleneck.SimPhaseMs;
            RenderCause = bottleneck.RenderCause;
            RenderSeverity = bottleneck.RenderSeverity;
            RenderSignalScore = bottleneck.RenderSignalScore;
            GpuBusyPercent = bottleneck.GpuBusyPercent;
            GpuUnderutilizedByCpuRender = bottleneck.GpuUnderutilizedByCpuRender;
        }

        public HealthLevel FpsLevel { get; }
        public HealthLevel StutterLevel { get; }
        public HealthLevel MemoryLevel { get; }
        public HealthLevel GrowthLevel { get; }
        public HealthLevel Overall { get; }
        public BottleneckKind Bottleneck { get; }
        public string MemoryHint { get; }       // "Stable" | "Growing" | "LEAK SUSPECTED: +120 MB over 30s"
        public string BottleneckHint { get; }   // short actionable advice

        // Per-frame averages of the two main phases. Diagnosis uses absolute ms to
        // pick how detailed the advice should be.
        public double RenderPhaseMs { get; }
        public double SimPhaseMs { get; }
        public RenderCause RenderCause { get; }

        // Multi-signal score for render-related severity. Computed only when the
        // bottleneck is render-related; higher value unlocks more specific advice.
        public RenderSeverityLevel RenderSeverity { get; }

        // Number of signals that fired ("render heavy", "sim quiet", "stutter pattern",
        // "vanilla-render-system in top"). Exposed for the support file so triagers
        // see the breakdown, not just the verdict.
        public int RenderSignalScore { get; }

        // Direct CPU/GPU classification when ProfilerRecorder data is present.
        // GpuBusyPercent ≈ how much of the frame the GPU was busy. It is descriptive
        // only; present-wait is the true GPU-bound signal.
        public double GpuBusyPercent { get; }
        public bool GpuUnderutilizedByCpuRender { get; }     // CPU render >> GPU → classic pre-rendered frames symptom
    }

#pragma warning disable CA1815
    public readonly struct RenderBottleneckResult
    {
        public RenderBottleneckResult(
            BottleneckKind bottleneck,
            RenderCause renderCause,
            string hint,
            double renderPhaseMs,
            double simPhaseMs,
            RenderSeverityLevel renderSeverity,
            int renderSignalScore,
            double gpuBusyPercent,
            bool gpuUnderutilizedByCpuRender)
        {
            Bottleneck = bottleneck;
            RenderCause = renderCause;
            Hint = hint ?? string.Empty;
            RenderPhaseMs = renderPhaseMs;
            SimPhaseMs = simPhaseMs;
            RenderSeverity = renderSeverity;
            RenderSignalScore = renderSignalScore;
            GpuBusyPercent = gpuBusyPercent;
            GpuUnderutilizedByCpuRender = gpuUnderutilizedByCpuRender;
        }

        public BottleneckKind Bottleneck { get; }
        public RenderCause RenderCause { get; }
        public string Hint { get; }
        public double RenderPhaseMs { get; }
        public double SimPhaseMs { get; }
        public RenderSeverityLevel RenderSeverity { get; }
        public int RenderSignalScore { get; }
        public double GpuBusyPercent { get; }
        public bool GpuUnderutilizedByCpuRender { get; }
    }
#pragma warning restore CA1815
}
