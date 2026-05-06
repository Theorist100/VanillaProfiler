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

        public (OverlaySnapshot snapshot, HealthReport health) Build(
            MetricsSample metrics, MemorySample memory, MemoryHistory history)
        {
            var snapshot = BuildSnapshot(metrics, memory);
            double simPhaseMs = SumPhaseMs(metrics.Phases, "GameSimulation", metrics.FrameCount);
            double renderPhaseMs = SumPhaseMs(metrics.Phases, "Rendering", metrics.FrameCount);
            var health = HealthClassifier.Classify(snapshot, history, simPhaseMs, renderPhaseMs);
            return (snapshot, health);
        }

        private static OverlaySnapshot BuildSnapshot(MetricsSample m, MemorySample mem)
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

            return new OverlaySnapshot
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
                TopVanillaSystems = BuildTop(m.VanillaSystems, TOP_N),
                TopModSystems = BuildTop(m.ModSystems, TOP_N),
                TopMods = BuildTop(m.ModAggregate, TOP_N),
                ManagedMB = mem.ManagedBytes / BYTES_PER_MB,
                ManagedDeltaMB = mem.ManagedDelta / BYTES_PER_MB,
            };
        }

        private static (string, double)[] BuildTop(Dictionary<string, PhaseData> systems, int n)
        {
            var sorted = new List<KeyValuePair<string, PhaseData>>(systems);
            sorted.Sort((a, b) => b.Value.TotalTicks.CompareTo(a.Value.TotalTicks));
            var result = new List<(string, double)>(n);
            foreach (var kvp in sorted)
            {
                if (result.Count >= n) break;
                double ms = kvp.Value.TotalTicks * MS_PER_SEC / Stopwatch.Frequency;
                if (ms < 0.1) break;
                result.Add((kvp.Key, ms));
            }
            return result.ToArray();
        }

        private static double SumPhaseMs(Dictionary<string, PhaseData> phases, string phaseName, int frameCount)
        {
            if (frameCount <= 0) return 0;

            string key = "UpdateSystem." + phaseName;
            double total = 0;
            foreach (var kvp in phases)
            {
                if (!string.Equals(kvp.Key, key, StringComparison.Ordinal)) continue;
                total += kvp.Value.TotalTicks * MS_PER_SEC / Stopwatch.Frequency;
            }
            return total / frameCount;
        }
    }
}
