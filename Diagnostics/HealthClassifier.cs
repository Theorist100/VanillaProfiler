using System;
using System.Collections.Generic;

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
        public HealthLevel FpsLevel;
        public HealthLevel StutterLevel;
        public HealthLevel MemoryLevel;
        public HealthLevel GrowthLevel;
        public HealthLevel Overall;
        public BottleneckKind Bottleneck;
        public string MemoryHint = "Stable";       // "Stable" | "Growing" | "LEAK SUSPECTED: +120 MB over 30s"
        public string BottleneckHint = "Collecting data...";   // short actionable advice

        // Per-frame averages of the two main phases. Diagnosis uses absolute ms to
        // pick how detailed the advice should be.
        public double RenderPhaseMs;
        public double SimPhaseMs;
        public RenderCause RenderCause;

        // Multi-signal score for render-related severity. Computed only when the
        // bottleneck is render-related; higher value unlocks more specific advice.
        public RenderSeverityLevel RenderSeverity;

        // Number of signals that fired ("render heavy", "sim quiet", "stutter pattern",
        // "vanilla-render-system in top"). Exposed for the support file so triagers
        // see the breakdown, not just the verdict.
        public int RenderSignalScore;

        // Direct CPU/GPU classification when ProfilerRecorder data is present.
        // GpuBusyPercent ≈ how much of the frame the GPU was busy. It is descriptive
        // only; present-wait is the true GPU-bound signal.
        public double GpuBusyPercent;
        public bool GpuUnderutilizedByCpuRender;     // CPU render >> GPU → classic pre-rendered frames symptom
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
            report.FpsLevel = ClassifyFps(snap.AvgFps);
            report.StutterLevel = ClassifyStutter(snap.MaxFrameMs, snap.Spikes30fps, snap.WindowSeconds);
            report.MemoryLevel = ClassifyMemory(snap.ManagedDeltaMB, mem);
            report.GrowthLevel = ClassifyGrowthRate(snap.ManagedGrowthMBperSec);
            report.Overall = Worst(report.FpsLevel, report.StutterLevel, report.MemoryLevel, report.GrowthLevel);

            report.MemoryHint = BuildMemoryHint(mem);
            report.RenderPhaseMs = renderPhaseMs;
            report.SimPhaseMs = simPhaseMs;

            // GPU vs CPU bound — only meaningful when ProfilerRecorder gave us
            // GPU + CPU thread numbers. Otherwise the legacy phase-only path runs.
            ClassifyThreadBalance(snap, report);

            (report.Bottleneck, report.RenderCause, report.BottleneckHint) = ClassifyBottleneck(
                snap, simPhaseMs, renderPhaseMs, report);

            if (report.RenderCause != RenderCause.None)
            {
                int score = ScoreRenderSignals(snap, simPhaseMs, renderPhaseMs, report);
                report.RenderSignalScore = score;
                if (score >= RENDER_SEVERE_SCORE) report.RenderSeverity = RenderSeverityLevel.Severe;
                else if (score >= RENDER_MILD_SCORE) report.RenderSeverity = RenderSeverityLevel.Mild;
            }

            return report;
        }

        // The frame is gated by whichever thread (CPU main / CPU render / GPU) takes
        // the longest. GPU underutilization fires when CPU render dwarfs GPU — that's
        // the literal pre-rendered-frames-=-1 signature.
        private const double THREAD_GAP_MS = 5.0;

        private static void ClassifyThreadBalance(OverlaySnapshot snap, HealthReport report)
        {
            double gpu = snap.GpuFrameTimeMs;
            double cpuMain = snap.MainThreadCpuMs;
            double cpuRender = snap.RenderThreadCpuMs;
            double frameMs = snap.AvgFrameMs;
            if (gpu <= 0 || frameMs <= 0) return;

            report.GpuBusyPercent = Math.Min(100.0, gpu / frameMs * 100.0);

            // Underutilization: CPU render thread is doing more work than the GPU
            // takes to consume it, with a meaningful gap. Strong driver-bound signal.
            bool mainCompatible = cpuMain <= 0 || cpuMain >= gpu;
            report.GpuUnderutilizedByCpuRender = cpuRender > gpu + THREAD_GAP_MS && mainCompatible;
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

        private static int ScoreRenderSignals(OverlaySnapshot snap, double simMs, double renderMs, HealthReport report)
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
            else if (renderShare >= RENDER_DOMINANCE_MEDIUM) score += 1;

            // 3) Sim is comparatively quiet — confirms it's not a heavy gameplay mod.
            if (frameMs > 0 && simMs / frameMs < SIM_QUIET_FRACTION) score += 1;

            // 4) Frame pacing irregular — max far above avg often correlates with
            //    CPU locking GPU (the pre-rendered-frames-=-1 signature).
            if (snap.MaxFrameMs >= snap.AvgFrameMs * STUTTER_RATIO) score += 1;

            // 5) Top vanilla system is render-side (BatchInstanceSystem etc.) and
            //    consuming meaningful time — this is engine rendering, not a mod.
            if (HasRenderHeavyVanillaTop(snap)) score += 1;

            // 6) Direct GPU underutilization signal (only when ProfilerRecorder gave
            //    us the GPU number). This is the gold-standard pre-rendered-frames
            //    detector and worth two points on its own.
            if (report.GpuUnderutilizedByCpuRender) score += 2;

            return score;
        }

        private static readonly HashSet<string> s_RenderHeavyVanillaSystems = new(StringComparer.Ordinal)
        {
            "Game.Rendering.BatchInstanceSystem",
            "Game.Rendering.BatchDataSystem",
            "Game.Rendering.PreCullingSystem",
            "Game.Rendering.CullingSystem",
            "Game.Rendering.RenderingSystem",
            "Game.Rendering.LightSystem",
            "Game.Rendering.ObjectInterpolateSystem",
        };

        private static bool HasRenderHeavyVanillaTop(OverlaySnapshot snap)
        {
            if (snap.TopVanillaSystems.Count == 0) return false;
            var top = snap.TopVanillaSystems[0];
            if (top.TotalMs < 30.0) return false;
            return s_RenderHeavyVanillaSystems.Contains(top.Name);
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
            bool hasPattern = spikesPer5s >= SPIKES_OK;
            if (spikesPer5s >= SPIKES_POOR) return HealthLevel.Poor;
            if (hasPattern && maxFrameMs >= FRAME_POOR_MS) return HealthLevel.Poor;
            if (hasPattern || maxFrameMs >= FRAME_GOOD_MS) return HealthLevel.Ok;
            return HealthLevel.Good;
        }

        private static HealthLevel ClassifyMemory(double deltaMB, MemoryHistory mem)
        {
            // NaN compares false against every threshold and would fall through to Poor.
            // Treat as Good — the most charitable interpretation when we have no signal.
            if (double.IsNaN(deltaMB) || double.IsInfinity(deltaMB)) return HealthLevel.Good;
            if (mem.LeakSuspected) return HealthLevel.Poor;
            if (deltaMB < MEM_DELTA_OK_MB) return HealthLevel.Good;
            if (deltaMB < MEM_DELTA_POOR_MB) return HealthLevel.Ok;
            return HealthLevel.Poor;
        }

        private static HealthLevel ClassifyGrowthRate(double mbPerSec)
        {
            if (double.IsNaN(mbPerSec) || double.IsInfinity(mbPerSec)) return HealthLevel.Good;
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

        private static (BottleneckKind, RenderCause, string) ClassifyBottleneck(
            OverlaySnapshot snap, double simMs, double renderMs, HealthReport report)
        {
            if (Math.Max(0, snap.ManagedGrowthMBperSec) > MEMORY_BOUND_MB_PER_S)
                return (BottleneckKind.MemoryBound, RenderCause.None, "Managed memory growing fast — restart recommended");

            double frameMs = snap.AvgFrameMs;
            if (frameMs <= 0)
                return (BottleneckKind.Balanced, RenderCause.None, "Collecting data...");

            double simShareDenom = simMs / frameMs;
            double renderShare = renderMs / frameMs;
            // PresentWait > 30% of frame is the real GPU-bound signal: CPU main thread
            // sitting idle waiting on the swapchain. GpuBusyPercent (gpu/frameMs) only
            // tells you what fraction of the frame the GPU was busy — at 98% with a 0.05ms
            // PresentWait, CPU and GPU are pipelined nicely and "lower graphics quality"
            // is the wrong advice. CS2 release exposes Gfx.WaitForPresentOnGfxThread
            // (verified in MarkerEnumerator dump) so this signal is always available.
            double presentShare = snap.PresentWaitMs / frameMs;
            const double GPU_BOUND_PRESENT_SHARE = 0.30;

            // Direct path: ProfilerRecorder gave us GPU + CPU thread numbers, so
            // we can name the actual gating thread instead of guessing from phases.
            if (report.GpuBusyPercent > 0)
                return ClassifyWithThreadCounters(
                    snap, report, simShareDenom, renderShare, presentShare, GPU_BOUND_PRESENT_SHARE);

            // Legacy path: only phase data available. Same logic as before.
            if (renderShare > BOTTLENECK_FRAME_SHARE && renderShare > simShareDenom)
                return (BottleneckKind.CpuRenderBound, RenderCause.CpuRenderSubmission,
                    "Render submission heavy — try lowering graphics and draw distance");

            if (simShareDenom > BOTTLENECK_FRAME_SHARE)
                return (BottleneckKind.SimBound, RenderCause.None, "Simulation heavy — large city or heavy mod");

            return (BottleneckKind.Balanced, RenderCause.None, "Frame budget balanced");
        }

        private static (BottleneckKind, RenderCause, string) ClassifyWithThreadCounters(
            OverlaySnapshot snap,
            HealthReport report,
            double simShare,
            double renderShare,
            double presentShare,
            double gpuBoundPresentShare)
        {
            // True GPU-bound: CPU stalls measurably on present.
            if (snap.PresentWaitAvailable && presentShare > gpuBoundPresentShare)
                return (BottleneckKind.GpuBound, RenderCause.PresentWait,
                    $"GPU bound — CPU waits {presentShare * 100:F0}% of frame. Lower graphics quality.");

            if (report.GpuUnderutilizedByCpuRender)
                return (BottleneckKind.CpuRenderBound, RenderCause.GpuUnderutilizedByCpuRender,
                    $"CPU render is gating GPU ({report.GpuBusyPercent:F0}% GPU active) — driver-side stall");

            if (simShare > BOTTLENECK_FRAME_SHARE)
                return (BottleneckKind.SimBound, RenderCause.None, "Simulation heavy — large city or heavy mod");

            if (renderShare > BOTTLENECK_FRAME_SHARE && renderShare > simShare)
                return (BottleneckKind.CpuRenderBound, RenderCause.CpuRenderSubmission,
                    "CPU render submission heavy — many draw calls or driver overhead");

            if (report.GpuBusyPercent > 70)
                return (BottleneckKind.Balanced, RenderCause.None,
                    $"GPU {report.GpuBusyPercent:F0}% busy, no CPU stall — CPU-paced");

            return (BottleneckKind.Balanced, RenderCause.None, "Frame budget balanced");
        }

        private static string Inv(FormattableString value) => FormattableString.Invariant(value);
    }
}
