using System;

namespace VanillaProfiler.Diagnostics
{
    internal static class HealthLevelClassifier
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

        public static HealthLevel ClassifyFps(double avgFps)
        {
            if (avgFps >= FPS_GOOD) return HealthLevel.Good;
            if (avgFps >= FPS_POOR) return HealthLevel.Ok;
            return HealthLevel.Poor;
        }

        public static HealthLevel ClassifyStutter(double maxFrameMs, int spikes30, float windowSeconds)
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

        public static HealthLevel ClassifyMemory(double deltaMB, MemoryHistory mem)
        {
            // NaN compares false against every threshold and would fall through to Poor.
            // Treat as Good — the most charitable interpretation when we have no signal.
            if (double.IsNaN(deltaMB) || double.IsInfinity(deltaMB)) return HealthLevel.Good;
            if (mem.LeakSuspected) return HealthLevel.Poor;
            if (deltaMB < MEM_DELTA_OK_MB) return HealthLevel.Good;
            if (deltaMB < MEM_DELTA_POOR_MB) return HealthLevel.Ok;
            return HealthLevel.Poor;
        }

        public static HealthLevel ClassifyGrowthRate(double mbPerSec)
        {
            if (double.IsNaN(mbPerSec) || double.IsInfinity(mbPerSec)) return HealthLevel.Good;
            double growth = Math.Max(0, mbPerSec);
            if (growth < GROWTH_OK_MB_PER_S) return HealthLevel.Good;
            if (growth < GROWTH_POOR_MB_PER_S) return HealthLevel.Ok;
            return HealthLevel.Poor;
        }

        public static HealthLevel Worst(params HealthLevel[] levels)
        {
            HealthLevel result = HealthLevel.Good;
            foreach (var l in levels)
                if (l > result) result = l;
            return result;
        }

        public static string BuildMemoryHint(MemoryHistory mem)
        {
            if (mem.LeakSuspected)
                return Inv($"LEAK SUSPECTED: {Delta(mem.TotalGrownMB)} MB over {mem.WindowSeconds}s");
            if (mem.GrowthMBperSec > 0.2)
                return Inv($"Growing (+{mem.GrowthMBperSec:F1} MB/s)");
            return "Stable";
        }

        private static string Delta(double mb) => mb >= 0 ? Inv($"+{mb:F0}") : Inv($"{mb:F0}");
        private static string Inv(FormattableString value) => FormattableString.Invariant(value);
    }
}
