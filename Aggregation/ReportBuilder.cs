using System;
using System.Collections.Generic;
using System.Diagnostics;
using VanillaProfiler.Diagnostics;

namespace VanillaProfiler.Aggregation
{
    /// <summary>
    /// Pure data composer — turns raw samples into an OverlaySnapshot + HealthReport.
    /// No I/O, no locks, no statics. Easy to test in isolation.
    /// </summary>
    public sealed class ReportBuilder
    {
        private const double MS_PER_SEC = 1000.0;
        private const double BYTES_PER_MB = 1024.0 * 1024.0;
        private const int TOP_N = 5;
        private const string SIM_PHASE_KEY = "UpdateSystem.GameSimulation";
        private const string RENDER_PHASE_KEY = "UpdateSystem.Rendering";

        // Reused sort buffer. Build runs once per report (~5s) but the buffer is
        // reused across windows so a steady-state mod doesn't keep allocating
        // List<KeyValuePair> instances.
        private readonly List<KeyValuePair<string, PhaseData>> m_SortBuffer = new();

        public (OverlaySnapshot snapshot, HealthReport health) Build(
            MetricsSample metrics, MemorySample memory, MemoryHistory history)
        {
            var snapshot = BuildSnapshot(metrics, memory);
            double simPhaseMs = PhaseMs(metrics.Buckets.Phases, SIM_PHASE_KEY, metrics.FrameCount);
            double renderPhaseMs = PhaseMs(metrics.Buckets.Phases, RENDER_PHASE_KEY, metrics.FrameCount);
            var health = HealthClassifier.Classify(snapshot, history, simPhaseMs, renderPhaseMs);
            return (snapshot, health);
        }

        private OverlaySnapshot BuildSnapshot(MetricsSample m, MemorySample mem)
        {
            double avgFrameMs = 0, maxFrameMs = 0, avgFps = 0, minFps = 0, simPerSec = 0;
            if (m.FrameCount > 0)
            {
                avgFrameMs = m.FrameTimeSum * MS_PER_SEC / Stopwatch.Frequency / m.FrameCount;
                maxFrameMs = m.FrameTimeMax * MS_PER_SEC / Stopwatch.Frequency;
                avgFps = avgFrameMs > 0 ? MS_PER_SEC / avgFrameMs : 0;
                minFps = maxFrameMs > 0 ? MS_PER_SEC / maxFrameMs : 0;
                simPerSec = m.FrameTimeSum > 0
                    ? m.SimTickCount * (double)Stopwatch.Frequency / m.FrameTimeSum
                    : 0;
            }

            (double profilerMs, double profilerPct) = ComputeProfilerSelfCost(m, avgFrameMs);

            var snapshot = new OverlaySnapshot
            {
                AvgFps = avgFps,
                WindowSeconds = m.ElapsedSec,
                MinFps = minFps,
                AvgFrameMs = avgFrameMs,
                MaxFrameMs = maxFrameMs,
                SimTicksPerSec = simPerSec,
                ManagedGrowthMBperSec = mem.ManagedGrowthMBperSec,
                Spikes30fps = m.Spikes30,
                Spikes20fps = m.Spikes20,
                TopVanillaSystems = BuildTopSystems(m.Buckets.VanillaSystems, TOP_N),
                TopModSystems = BuildTopSystems(m.Buckets.ModSystems, TOP_N),
                TopMods = BuildTopMods(m.Buckets.ModAggregate, TOP_N),
                ManagedMB = mem.ManagedBytes / BYTES_PER_MB,
                ManagedDeltaMB = mem.ManagedDelta / BYTES_PER_MB,
                ProfilerSelfMs = profilerMs,
                ProfilerSelfPercent = profilerPct,
            };
            FillEngineCounters(snapshot, mem);
            return snapshot;
        }

        private static void FillEngineCounters(OverlaySnapshot snapshot, MemorySample mem)
        {
            snapshot.GfxUsedMB = mem.GfxUsedBytes / BYTES_PER_MB;
            snapshot.AudioUsedMB = mem.AudioUsedBytes / BYTES_PER_MB;
            snapshot.MainThreadCpuMs = mem.MainThreadCpuNs / 1_000_000.0;
            snapshot.RenderThreadCpuMs = mem.RenderThreadCpuNs / 1_000_000.0;
            snapshot.GpuFrameTimeMs = mem.GpuFrameTimeNs / 1_000_000.0;
            snapshot.PresentWaitMs = mem.PresentWaitNs / 1_000_000.0;
            snapshot.GfxUsedAvailable = mem.GfxUsedAvailable;
            snapshot.AudioUsedAvailable = mem.AudioUsedAvailable;
            snapshot.MainThreadCpuAvailable = mem.MainThreadCpuAvailable;
            snapshot.RenderThreadCpuAvailable = mem.RenderThreadCpuAvailable;
            snapshot.GpuFrameTimeAvailable = mem.GpuFrameTimeAvailable;
            snapshot.PresentWaitAvailable = mem.PresentWaitAvailable;
            FillRenderCounters(snapshot, mem);
        }

        private static void FillRenderCounters(OverlaySnapshot snapshot, MemorySample mem)
        {
            snapshot.DrawCalls = mem.DrawCallsCount;
            snapshot.SetPassCalls = mem.SetPassCallsCount;
            snapshot.Triangles = mem.TrianglesCount;
            snapshot.Vertices = mem.VerticesCount;
            snapshot.ShadowCasters = mem.ShadowCastersCount;
            snapshot.DrawCallsAvailable = mem.DrawCallsAvailable;
            snapshot.SetPassCallsAvailable = mem.SetPassCallsAvailable;
            snapshot.TrianglesAvailable = mem.TrianglesAvailable;
            snapshot.VerticesAvailable = mem.VerticesAvailable;
            snapshot.ShadowCastersAvailable = mem.ShadowCastersAvailable;
            FillMemoryCounters(snapshot, mem);
        }

        private static void FillMemoryCounters(OverlaySnapshot snapshot, MemorySample mem)
        {
            snapshot.UsedBuffersMB = mem.UsedBuffersBytes / BYTES_PER_MB;
            snapshot.UsedBuffersCount = mem.UsedBuffersCount;
            snapshot.RenderTexturesMB = mem.RenderTexturesBytes / BYTES_PER_MB;
            snapshot.UsedBuffersBytesAvailable = mem.UsedBuffersBytesAvailable;
            snapshot.UsedBuffersCountAvailable = mem.UsedBuffersCountAvailable;
            snapshot.RenderTexturesBytesAvailable = mem.RenderTexturesBytesAvailable;
            snapshot.GcCollectStallMs = mem.GcCollectTotalNs / 1_000_000.0;
            snapshot.GcCollectCount = mem.GcCollectCount;
            snapshot.GcCollectAvailable = mem.GcCollectAvailable;
            snapshot.AppResidentMB = mem.AppResidentBytes / BYTES_PER_MB;
            snapshot.SystemUsedAvailable = mem.SystemUsedAvailable;
            snapshot.AppResidentAvailable = mem.AppResidentAvailable;
        }

        private const string PROFILER_MOD_NAME = "VanillaProfiler";

        private static (double ms, double pct) ComputeProfilerSelfCost(MetricsSample m, double avgFrameMs)
        {
            if (m.FrameCount <= 0) return (0, 0);
            if (!m.Buckets.ModAggregate.TryGetValue(PROFILER_MOD_NAME, out var phase)) return (0, 0);
            double totalMs = phase.TotalTicks * MS_PER_SEC / Stopwatch.Frequency;
            double perFrameMs = totalMs / m.FrameCount;
            double pct = avgFrameMs > 0 ? (perFrameMs / avgFrameMs) * 100.0 : 0;
            return (perFrameMs, pct);
        }

        private SystemCostRow[] BuildTopSystems(IReadOnlyDictionary<string, PhaseData> systems, int n)
            => BuildTop(systems, n, static (name, ms) => new SystemCostRow(name, ms));

        private ModCostRow[] BuildTopMods(IReadOnlyDictionary<string, PhaseData> systems, int n)
            => BuildTop(systems, n, static (name, ms) => new ModCostRow(name, ms));

        private TRow[] BuildTop<TRow>(
            IReadOnlyDictionary<string, PhaseData> systems,
            int n,
            Func<string, double, TRow> create)
        {
            m_SortBuffer.Clear();
            foreach (var kvp in systems) m_SortBuffer.Add(kvp);
            m_SortBuffer.Sort(static (a, b) => b.Value.TotalTicks.CompareTo(a.Value.TotalTicks));

            // Walk in order, count how many will pass the 0.1ms cutoff up to n,
            // then allocate the exact-size result array. Avoids the List → ToArray
            // double allocation.
            int take = 0;
            for (int i = 0; i < m_SortBuffer.Count && take < n; i++)
            {
                double ms = m_SortBuffer[i].Value.TotalTicks * MS_PER_SEC / Stopwatch.Frequency;
                if (ms < 0.1) break;
                take++;
            }

            if (take == 0) return Array.Empty<TRow>();

            var result = new TRow[take];
            for (int i = 0; i < take; i++)
            {
                var kvp = m_SortBuffer[i];
                double ms = kvp.Value.TotalTicks * MS_PER_SEC / Stopwatch.Frequency;
                result[i] = create(kvp.Key, ms);
            }
            return result;
        }

        private static double PhaseMs(IReadOnlyDictionary<string, PhaseData> phases, string phaseKey, int frameCount)
        {
            if (frameCount <= 0) return 0;
            return phases.TryGetValue(phaseKey, out var data)
                ? data.TotalTicks * MS_PER_SEC / Stopwatch.Frequency / frameCount
                : 0;
        }
    }
}
