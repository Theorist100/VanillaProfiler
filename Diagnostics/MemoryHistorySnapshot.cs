using System;
using System.Collections.Generic;

namespace VanillaProfiler.Diagnostics
{
    public sealed class MemoryHistorySnapshot
    {
        public static readonly MemoryHistorySnapshot Empty =
            new MemoryHistorySnapshot(0, 0, false, Array.Empty<MemorySamplePoint>());

        public MemoryHistorySnapshot(
            int windowSeconds,
            double growthMbPerSec,
            bool isLeaking,
            IReadOnlyList<MemorySamplePoint> samples)
        {
            WindowSeconds = windowSeconds;
            GrowthMbPerSec = growthMbPerSec;
            IsLeaking = isLeaking;
            Samples = samples ?? Array.Empty<MemorySamplePoint>();
        }

        public int WindowSeconds { get; }
        public double GrowthMbPerSec { get; }
        public bool IsLeaking { get; }
        public IReadOnlyList<MemorySamplePoint> Samples { get; }
    }

    public sealed class MemorySamplePoint
    {
        public MemorySamplePoint(long managedBytes, double seconds)
        {
            ManagedBytes = managedBytes;
            Seconds = seconds;
        }

        public long ManagedBytes { get; }
        public double Seconds { get; }
    }
}
