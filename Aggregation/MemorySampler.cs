using System;
using UnityProfiler = UnityEngine.Profiling.Profiler;

namespace VanillaProfiler.Aggregation
{
    /// <summary>
    /// Captures managed/Mono/native memory totals against a baseline taken on the first sample.
    /// Computes live managed heap growth per report interval. This is not an allocation-rate counter:
    /// short-lived allocations that are collected between reports are intentionally invisible.
    /// SRP: only memory metrics — no logging, no formatting.
    /// </summary>
    public sealed class MemorySampler
    {
        private long m_BaselineManaged;
        private long m_BaselineMonoHeap;
        private long m_BaselineNativeAlloc;
        private long m_BaselineNativeReserved;
        private bool m_BaselineCaptured;

        private long m_LastReportManagedBytes;
        private bool m_GrowthBaselineCaptured;

        public long BaselineManaged => m_BaselineManaged;
        public long BaselineMonoHeap => m_BaselineMonoHeap;
        public long BaselineNativeAlloc => m_BaselineNativeAlloc;
        public long BaselineNativeReserved => m_BaselineNativeReserved;

        private const double BYTES_PER_MB = 1024.0 * 1024.0;

        public MemorySample Sample(float reportIntervalSeconds)
        {
            long managed = GC.GetTotalMemory(forceFullCollection: false);
            long mono = UnityProfiler.GetMonoHeapSizeLong();
            long nativeAlloc = UnityProfiler.GetTotalAllocatedMemoryLong();
            long nativeReserved = UnityProfiler.GetTotalReservedMemoryLong();

            double managedGrowthRate;
            if (!m_GrowthBaselineCaptured)
            {
                m_LastReportManagedBytes = managed;
                m_GrowthBaselineCaptured = true;
                managedGrowthRate = 0;
            }
            else
            {
                long managedDelta = managed - m_LastReportManagedBytes;
                managedGrowthRate = reportIntervalSeconds > 0
                    ? managedDelta / BYTES_PER_MB / reportIntervalSeconds
                    : 0;
                m_LastReportManagedBytes = managed;
            }

            bool baselineJustCaptured = false;
            if (!m_BaselineCaptured)
            {
                m_BaselineManaged = managed;
                m_BaselineMonoHeap = mono;
                m_BaselineNativeAlloc = nativeAlloc;
                m_BaselineNativeReserved = nativeReserved;
                m_BaselineCaptured = true;
                baselineJustCaptured = true;
            }

            return new MemorySample
            {
                ManagedBytes = managed,
                MonoHeapBytes = mono,
                NativeAllocBytes = nativeAlloc,
                NativeReservedBytes = nativeReserved,
                ManagedDelta = managed - m_BaselineManaged,
                MonoHeapDelta = mono - m_BaselineMonoHeap,
                NativeAllocDelta = nativeAlloc - m_BaselineNativeAlloc,
                NativeReservedDelta = nativeReserved - m_BaselineNativeReserved,
                ManagedGrowthMBperSec = managedGrowthRate,
                BaselineJustCaptured = baselineJustCaptured,
            };
        }

        public void ResetBaseline()
        {
            m_BaselineCaptured = false;
            m_GrowthBaselineCaptured = false;
            m_LastReportManagedBytes = 0;
        }
    }
}
