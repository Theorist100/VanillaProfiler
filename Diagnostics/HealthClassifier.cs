using System;

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
        RenderBound,
        SimBound,
        MemoryBound,
    }

    /// <summary>Confidence that the render phase is the bottleneck. Drives advice depth.</summary>
    public enum RenderBoundSeverity
    {
        None = 0,    // not render-bound, or signal too weak to act on
        Mild,        // render is heavier than sim but within normal range
        Severe,      // multiple signals point to a CPU-side render lock
    }

    /// <summary>
    /// Player-facing summary of the latest configured report window.
    /// Hints carry short, actionable strings — overlays render them verbatim.
    /// </summary>
    public sealed class HealthReport
    {
        public HealthLevel FpsLevel;
        public HealthLevel StutterLevel;
        public HealthLevel MemoryLevel;
        public HealthLevel GrowthLevel;
        public HealthLevel Overall;
        public BottleneckKind Bottleneck;
        public string MemoryHint;       // "Stable" | "Growing" | "LEAK SUSPECTED: +120 MB over 30s"
        public string BottleneckHint;   // short actionable advice

        // Per-frame averages of the two main phases. Diagnosis uses absolute ms to
        // pick how detailed the advice should be.
        public double RenderPhaseMs;
        public double SimPhaseMs;

        // Multi-signal score for render-bound severity. Computed only when the
        // bottleneck is RenderBound; higher value unlocks more specific advice.
        public RenderBoundSeverity RenderSeverity;

        // Number of signals that fired ("render heavy", "sim quiet", "stutter pattern",
        // "vanilla-render-system in top"). Exposed for the support file so triagers
        // see the breakdown, not just the verdict.
        public int RenderSignalScore;
    }

    public static class HealthClassifier
    {
        private const double FPS_GOOD = 50.0;
        private const double FPS_POOR = 30.0;

        private const double FRAME_GOOD_MS = 33.0;
        private const double FRAME_POOR_MS = 50.0;

        private const double MEM_DELTA_OK_MB = 50.0;
        private const double MEM_DELTA_POOR_MB = 200.0;

        private const double GROWTH_OK_MB_PER_S = 1.0;
        private const double GROWTH_POOR_MB_PER_S = 5.0;

        private const int SPIKES_OK = 1;
        private const int SPIKES_POOR = 5;

        public static HealthReport Classify(
            OverlaySnapshot snap,
            MemoryHistory mem,
            double simPhaseMs,
            double renderPhaseMs)
        {
            var report = new HealthReport();
            if (snap == null) return report;

            report.FpsLevel = ClassifyFps(snap.AvgFps);
            report.StutterLevel = ClassifyStutter(snap.MaxFrameMs, snap.Spikes30fps, snap.WindowSeconds);
            report.MemoryLevel = ClassifyMemory(snap.ManagedDeltaMB, mem);
            report.GrowthLevel = ClassifyGrowthRate(snap.ManagedGrowthMBperSec);
            report.Overall = Worst(report.FpsLevel, report.StutterLevel, report.MemoryLevel, report.GrowthLevel);

            report.MemoryHint = BuildMemoryHint(mem);
            report.RenderPhaseMs = renderPhaseMs;
            report.SimPhaseMs = simPhaseMs;
            (report.Bottleneck, report.BottleneckHint) = ClassifyBottleneck(
                snap.AvgFrameMs, simPhaseMs, renderPhaseMs, snap.ManagedGrowthMBperSec);

            if (report.Bottleneck == BottleneckKind.RenderBound)
            {
                int score = ScoreRenderBoundSignals(snap, simPhaseMs, renderPhaseMs);
                report.RenderSignalScore = score;
                if (score >= RENDER_SEVERE_SCORE) report.RenderSeverity = RenderBoundSeverity.Severe;
                else if (score >= RENDER_MILD_SCORE) report.RenderSeverity = RenderBoundSeverity.Mild;
            }

            return report;
        }

        // Multi-signal score for "this looks like a CPU-side rendering bottleneck".
        // Each independent symptom adds 1 point; we report Severe only when at least
        // 3 of 5 fire so a single noisy measurement can't trigger NVIDIA-specific advice.
        private const double RENDER_HEAVY_MS = 50.0;
        private const double RENDER_MEDIUM_MS = 30.0;
        private const double RENDER_DOMINANCE_HIGH = 0.70;
        private const double RENDER_DOMINANCE_MEDIUM = 0.55;
        private const double STUTTER_RATIO = 2.5;
        private const double SIM_QUIET_FRACTION = 0.20;
        private const int RENDER_SEVERE_SCORE = 3;
        private const int RENDER_MILD_SCORE = 2;

        private static int ScoreRenderBoundSignals(OverlaySnapshot snap, double simMs, double renderMs)
        {
            int score = 0;
            double frameMs = snap.AvgFrameMs;
            if (frameMs <= 0) return 0;

            // 1) Absolute render time is far above the 5-25 ms norm.
            if (renderMs >= RENDER_HEAVY_MS) score += 2;
            else if (renderMs >= RENDER_MEDIUM_MS) score += 1;

            // 2) Render phase dominates the frame budget.
            double renderShare = renderMs / frameMs;
            if (renderShare >= RENDER_DOMINANCE_HIGH) score += 1;
            else if (renderShare >= RENDER_DOMINANCE_MEDIUM) score += 0;

            // 3) Sim is comparatively quiet — confirms it's not a heavy gameplay mod.
            if (frameMs > 0 && simMs / frameMs < SIM_QUIET_FRACTION) score += 1;

            // 4) Frame pacing irregular — max far above avg often correlates with
            //    CPU locking GPU (the pre-rendered-frames-=-1 signature).
            if (snap.MaxFrameMs >= snap.AvgFrameMs * STUTTER_RATIO) score += 1;

            // 5) Top vanilla system is render-side (BatchInstanceSystem etc.) and
            //    consuming meaningful time — this is engine rendering, not a mod.
            if (HasRenderHeavyVanillaTop(snap)) score += 1;

            return score;
        }

        private static readonly string[] s_RenderHeavyVanillaSystems =
        {
            "BatchInstanceSystem",
            "BatchDataSystem",
            "PreCullingSystem",
            "CullingSystem",
            "RenderingSystem",
            "LightSystem",
            "ObjectInterpolateSystem",
        };

        private static bool HasRenderHeavyVanillaTop(OverlaySnapshot snap)
        {
            if (snap.TopVanillaSystems == null || snap.TopVanillaSystems.Length == 0) return false;
            var top = snap.TopVanillaSystems[0];
            if (top.TotalMs < 30.0) return false;
            foreach (var name in s_RenderHeavyVanillaSystems)
                if (top.Name == name) return true;
            return false;
        }

        private static HealthLevel ClassifyFps(double avgFps)
        {
            if (avgFps >= FPS_GOOD) return HealthLevel.Good;
            if (avgFps >= FPS_POOR) return HealthLevel.Ok;
            return HealthLevel.Poor;
        }

        private static HealthLevel ClassifyStutter(double maxFrameMs, int spikes30, float windowSeconds)
        {
            double window = windowSeconds;
            if (window <= 0 || double.IsNaN(window) || double.IsInfinity(window))
                window = 5.0;

            double spikesPer5s = spikes30 * 5.0 / window;
            if (maxFrameMs >= FRAME_POOR_MS || spikesPer5s > SPIKES_POOR) return HealthLevel.Poor;
            if (maxFrameMs >= FRAME_GOOD_MS || spikesPer5s >= SPIKES_OK) return HealthLevel.Ok;
            return HealthLevel.Good;
        }

        private static HealthLevel ClassifyMemory(double deltaMB, MemoryHistory mem)
        {
            if (mem != null && mem.LeakSuspected) return HealthLevel.Poor;
            if (deltaMB < MEM_DELTA_OK_MB) return HealthLevel.Good;
            if (deltaMB < MEM_DELTA_POOR_MB) return HealthLevel.Ok;
            return HealthLevel.Poor;
        }

        private static HealthLevel ClassifyGrowthRate(double mbPerSec)
        {
            double growth = Math.Max(0, mbPerSec);
            if (growth < GROWTH_OK_MB_PER_S) return HealthLevel.Good;
            if (growth < GROWTH_POOR_MB_PER_S) return HealthLevel.Ok;
            return HealthLevel.Poor;
        }

        private static HealthLevel Worst(params HealthLevel[] levels)
        {
            HealthLevel result = HealthLevel.Good;
            foreach (var l in levels)
                if (l > result) result = l;
            return result;
        }

        private static string BuildMemoryHint(MemoryHistory mem)
        {
            if (mem == null) return "Stable";
            if (mem.LeakSuspected)
                return Inv($"LEAK SUSPECTED: {Delta(mem.TotalGrownMB)} MB over {mem.WindowSeconds}s");
            if (mem.GrowthMBperSec > 0.2)
                return Inv($"Growing (+{mem.GrowthMBperSec:F1} MB/s)");
            return "Stable";
        }

        private static string Delta(double mb) => mb >= 0 ? Inv($"+{mb:F0}") : Inv($"{mb:F0}");

        // A subsystem is considered the bottleneck once it consumes 60% of the frame budget.
        private const double BOTTLENECK_FRAME_SHARE = 0.6;
        private const double MEMORY_BOUND_MB_PER_S = 10.0;

        private static (BottleneckKind, string) ClassifyBottleneck(
            double frameMs, double simMs, double renderMs, double managedGrowthMBperSec)
        {
            if (Math.Max(0, managedGrowthMBperSec) > MEMORY_BOUND_MB_PER_S)
                return (BottleneckKind.MemoryBound, "Managed memory growing fast — restart recommended");

            if (frameMs <= 0)
                return (BottleneckKind.Balanced, "Collecting data...");

            double simShare = simMs / frameMs;
            double renderShare = renderMs / frameMs;

            if (renderShare > BOTTLENECK_FRAME_SHARE && renderShare > simShare)
                return (BottleneckKind.RenderBound, "GPU/render bound — try lowering graphics");

            if (simShare > BOTTLENECK_FRAME_SHARE)
                return (BottleneckKind.SimBound, "Simulation heavy — large city or heavy mod");

            return (BottleneckKind.Balanced, "Frame budget balanced");
        }

        private static string Inv(FormattableString value) => FormattableString.Invariant(value);
    }
}
