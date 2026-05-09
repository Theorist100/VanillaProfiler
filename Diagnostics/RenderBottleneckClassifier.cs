using System;
using System.Collections.Generic;

namespace VanillaProfiler.Diagnostics
{
    internal static class RenderBottleneckClassifier
    {
        // The frame is gated by whichever thread (CPU main / CPU render / GPU) takes
        // the longest. GPU underutilization fires when CPU render dwarfs GPU — that's
        // the literal pre-rendered-frames-=-1 signature.
        private const double THREAD_GAP_MS = 5.0;

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

        // A subsystem is considered the bottleneck once it consumes 60% of the frame budget.
        private const double BOTTLENECK_FRAME_SHARE = 0.6;
        private const double MEMORY_BOUND_MB_PER_S = 10.0;

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

        public static RenderBottleneckResult Classify(OverlaySnapshot snap, double simPhaseMs, double renderPhaseMs)
        {
            var threadBalance = ClassifyThreadBalance(snap);
            var bottleneck = ClassifyBottleneck(snap, simPhaseMs, renderPhaseMs, threadBalance);

            int score = 0;
            RenderSeverityLevel severity = RenderSeverityLevel.None;
            if (bottleneck.Cause != RenderCause.None)
            {
                score = ScoreRenderSignals(snap, simPhaseMs, renderPhaseMs, threadBalance);
                if (score >= RENDER_SEVERE_SCORE) severity = RenderSeverityLevel.Severe;
                else if (score >= RENDER_MILD_SCORE) severity = RenderSeverityLevel.Mild;
            }

            return new RenderBottleneckResult(
                bottleneck.Kind,
                bottleneck.Cause,
                bottleneck.Hint,
                renderPhaseMs,
                simPhaseMs,
                severity,
                score,
                threadBalance.GpuBusyPercent,
                threadBalance.GpuUnderutilizedByCpuRender);
        }

        private static ThreadBalanceResult ClassifyThreadBalance(OverlaySnapshot snap)
        {
            double gpu = snap.GpuFrameTimeMs;
            double cpuMain = snap.MainThreadCpuMs;
            double cpuRender = snap.RenderThreadCpuMs;
            double frameMs = snap.AvgFrameMs;
            if (gpu <= 0 || frameMs <= 0) return default;

            double gpuBusyPercent = Math.Min(100.0, gpu / frameMs * 100.0);

            // Underutilization: CPU render thread is doing more work than the GPU
            // takes to consume it, with a meaningful gap. Strong driver-bound signal.
            bool mainCompatible = cpuMain <= 0 || cpuMain >= gpu;
            bool gpuUnderutilizedByCpuRender = cpuRender > gpu + THREAD_GAP_MS && mainCompatible;
            return new ThreadBalanceResult(gpuBusyPercent, gpuUnderutilizedByCpuRender);
        }

        private static int ScoreRenderSignals(
            OverlaySnapshot snap,
            double simMs,
            double renderMs,
            ThreadBalanceResult threadBalance)
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
            if (threadBalance.GpuUnderutilizedByCpuRender) score += 2;

            return score;
        }

        private static bool HasRenderHeavyVanillaTop(OverlaySnapshot snap)
        {
            if (snap.TopVanillaSystems.Count == 0) return false;
            var top = snap.TopVanillaSystems[0];
            if (top.TotalMs < 30.0) return false;
            return s_RenderHeavyVanillaSystems.Contains(top.Name);
        }

        private static BottleneckDecision ClassifyBottleneck(
            OverlaySnapshot snap,
            double simMs,
            double renderMs,
            ThreadBalanceResult threadBalance)
        {
            if (Math.Max(0, snap.ManagedGrowthMBperSec) > MEMORY_BOUND_MB_PER_S)
                return new BottleneckDecision(BottleneckKind.MemoryBound, RenderCause.None, "Managed memory growing fast — restart recommended");

            double frameMs = snap.AvgFrameMs;
            if (frameMs <= 0)
                return new BottleneckDecision(BottleneckKind.Balanced, RenderCause.None, "Collecting data...");

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
            if (threadBalance.GpuBusyPercent > 0)
                return ClassifyWithThreadCounters(
                    snap, threadBalance, simShareDenom, renderShare, presentShare, GPU_BOUND_PRESENT_SHARE);

            // Legacy path: only phase data available. Same logic as before.
            if (renderShare > BOTTLENECK_FRAME_SHARE && renderShare > simShareDenom)
                return new BottleneckDecision(BottleneckKind.CpuRenderBound, RenderCause.CpuRenderSubmission,
                    "Render submission heavy — try lowering graphics and draw distance");

            if (simShareDenom > BOTTLENECK_FRAME_SHARE)
                return new BottleneckDecision(BottleneckKind.SimBound, RenderCause.None, "Simulation heavy — large city or heavy mod");

            return new BottleneckDecision(BottleneckKind.Balanced, RenderCause.None, "Frame budget balanced");
        }

        private static BottleneckDecision ClassifyWithThreadCounters(
            OverlaySnapshot snap,
            ThreadBalanceResult threadBalance,
            double simShare,
            double renderShare,
            double presentShare,
            double gpuBoundPresentShare)
        {
            // True GPU-bound: CPU stalls measurably on present.
            if (snap.PresentWaitAvailable && presentShare > gpuBoundPresentShare)
                return new BottleneckDecision(BottleneckKind.GpuBound, RenderCause.PresentWait,
                    $"GPU bound — CPU waits {presentShare * 100:F0}% of frame. Lower graphics quality.");

            if (threadBalance.GpuUnderutilizedByCpuRender)
                return new BottleneckDecision(BottleneckKind.CpuRenderBound, RenderCause.GpuUnderutilizedByCpuRender,
                    $"CPU render is gating GPU ({threadBalance.GpuBusyPercent:F0}% GPU active) — driver-side stall");

            if (simShare > BOTTLENECK_FRAME_SHARE)
                return new BottleneckDecision(BottleneckKind.SimBound, RenderCause.None, "Simulation heavy — large city or heavy mod");

            if (renderShare > BOTTLENECK_FRAME_SHARE && renderShare > simShare)
                return new BottleneckDecision(BottleneckKind.CpuRenderBound, RenderCause.CpuRenderSubmission,
                    "CPU render submission heavy — many draw calls or driver overhead");

            if (threadBalance.GpuBusyPercent > 70)
                return new BottleneckDecision(BottleneckKind.Balanced, RenderCause.None,
                    $"GPU {threadBalance.GpuBusyPercent:F0}% busy, no CPU stall — CPU-paced");

            return new BottleneckDecision(BottleneckKind.Balanced, RenderCause.None, "Frame budget balanced");
        }

#pragma warning disable CA1815
        private readonly struct ThreadBalanceResult
        {
            public ThreadBalanceResult(double gpuBusyPercent, bool gpuUnderutilizedByCpuRender)
            {
                GpuBusyPercent = gpuBusyPercent;
                GpuUnderutilizedByCpuRender = gpuUnderutilizedByCpuRender;
            }

            public double GpuBusyPercent { get; }
            public bool GpuUnderutilizedByCpuRender { get; }
        }

        private readonly struct BottleneckDecision
        {
            public BottleneckDecision(BottleneckKind kind, RenderCause cause, string hint)
            {
                Kind = kind;
                Cause = cause;
                Hint = hint;
            }

            public BottleneckKind Kind { get; }
            public RenderCause Cause { get; }
            public string Hint { get; }
        }
#pragma warning restore CA1815
    }
}
