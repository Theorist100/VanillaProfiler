using System;

namespace VanillaProfiler.Aggregation
{
    /// <summary>
    /// Captures managed/Mono/native memory + Gfx/Audio/Video totals and main/render
    /// thread CPU times against a baseline taken on the first sample. Computes live
    /// managed heap growth per report interval. This is not an allocation-rate counter:
    /// short-lived allocations that are collected between reports are intentionally
    /// invisible.
    ///
    /// Hybrid sourcing: legacy UnityEngine.Profiling.Profiler for managed/Mono/native
    /// (proven-stable on CS2), Unity.Profiling.ProfilerRecorder for the categories
    /// CS2's own dev panel exposes (Gfx/Audio/Video memory, render-thread CPU).
    /// SRP: only memory and platform timing — no logging, no formatting.
    /// </summary>
    public sealed class MemorySampler : IDisposable
    {
        private long m_BaselineManaged;
        private long m_BaselineMonoHeap;
        private long m_BaselineNativeAlloc;
        private long m_BaselineNativeReserved;
        private long m_BaselineGfxUsed;
        private long m_BaselineAudioUsed;
        private bool m_BaselineCaptured;
        private bool m_GfxBaselineCaptured;

        // GC delta tracking. ProfilerRecorder ring gives total contents, not per-window
        // events — without these deltas the GC count looks like it grows monotonically
        // forever (or saturates at capacity). System.GC.CollectionCount(gen) is truly
        // cumulative and gives an exact per-window delta when subtracted between samples.
        private int m_PrevGen0Count;
        private int m_PrevGen1Count;
        private int m_PrevGen2Count;
        private long m_PrevGcStallSumNs;
        private bool m_GcBaselineCaptured;

        private long m_LastReportManagedBytes;
        private bool m_GrowthBaselineCaptured;

        private readonly MemoryRecorderSet m_Recorders = new();

        public long BaselineManaged => m_BaselineManaged;
        public long BaselineMonoHeap => m_BaselineMonoHeap;
        public long BaselineNativeAlloc => m_BaselineNativeAlloc;
        public long BaselineNativeReserved => m_BaselineNativeReserved;

        private const double BYTES_PER_MB = 1024.0 * 1024.0;

        public MemorySampler()
        {
            // Marker names verified against the runtime enumeration of available
            // counters in CS2's ProfilerRecorderHandle.GetAvailable output. "Gfx
            // Used Memory" does not exist on this build — we use "Video Used Memory"
            // (Memory category) for GPU bytes and "GPU Frame Time" (Render category)
            // for actual hardware time. CPU render thread is a distinct counter
            // from the main thread one, despite the similar prefix.
            //
            // Frame-timing capacity sized for ~5s of 60fps frames (300 samples). The
            // ProfilerRecorder ring overwrites oldest when full, so AverageValid reads
            // a sliding window of roughly the last 5 seconds — close to the default
            // ReportIntervalSec of 5s. Was 15 (~250ms) which gave reports a misleading
            // micro-window snapshot inconsistent with the surrounding 5-second cadence.
            m_Recorders.Start();

            // Speculative job-worker recorders were dropped: MarkerEnumerator confirmed
            // CS2 release strips JobsParallelFor.Execute / WaitForJobGroupID. Adding
            // them back only makes sense if Paradox ships a development build later.

            ModLog.Info(m_Recorders.BuildValiditySummary());
        }

        public void Dispose()
        {
            m_Recorders.Dispose();
        }

        public void ResetSession()
        {
            m_Recorders.Reset();
            ResetBaseline();
        }

        public MemorySample Sample(float reportIntervalSeconds)
        {
            var raw = m_Recorders.ReadRawMemory();
            var timing = m_Recorders.ReadTimingCounters();
            var render = m_Recorders.ReadRenderCounters();
            var gc = CaptureGcDelta();
            double managedGrowthRate = UpdateManagedGrowth(raw.Managed, reportIntervalSeconds);
            bool baselineJustCaptured = CaptureBaselines(raw);
            return new MemorySample
            {
                ManagedBytes = raw.Managed,
                MonoHeapBytes = raw.Mono,
                NativeAllocBytes = raw.NativeAlloc,
                NativeReservedBytes = raw.NativeReserved,
                GfxUsedBytes = raw.GpuMemory.Value,
                AudioUsedBytes = raw.Audio.Value,
                SystemUsedBytes = raw.System.Value,
                GfxUsedAvailable = raw.GpuMemory.Available,
                AudioUsedAvailable = raw.Audio.Available,
                SystemUsedAvailable = raw.System.Available,
                ManagedDelta = raw.Managed - m_BaselineManaged,
                MonoHeapDelta = raw.Mono - m_BaselineMonoHeap,
                NativeAllocDelta = raw.NativeAlloc - m_BaselineNativeAlloc,
                NativeReservedDelta = raw.NativeReserved - m_BaselineNativeReserved,
                GfxUsedDelta = m_GfxBaselineCaptured ? raw.GpuMemory.Value - m_BaselineGfxUsed : 0,
                AudioUsedDelta = raw.Audio.Value - m_BaselineAudioUsed,
                ManagedGrowthMBperSec = managedGrowthRate,
                BaselineJustCaptured = baselineJustCaptured,
                MainThreadCpuNs = timing.MainThread.Value,
                RenderThreadCpuNs = timing.RenderThread.Value,
                GpuFrameTimeNs = timing.GpuFrame.Value,
                PresentWaitNs = timing.PresentWait.Value,
                MainThreadCpuAvailable = timing.MainThread.Available,
                RenderThreadCpuAvailable = timing.RenderThread.Available,
                GpuFrameTimeAvailable = timing.GpuFrame.Available,
                PresentWaitAvailable = timing.PresentWait.Available,
                DrawCallsCount = render.DrawCalls.Value,
                SetPassCallsCount = render.SetPass.Value,
                TrianglesCount = render.Triangles.Value,
                VerticesCount = render.Vertices.Value,
                ShadowCastersCount = render.ShadowCasters.Value,
                UsedBuffersBytes = render.BuffersBytes.Value,
                UsedBuffersCount = render.BuffersCount.Value,
                RenderTexturesBytes = render.RenderTexturesBytes.Value,
                DrawCallsAvailable = render.DrawCalls.Available,
                SetPassCallsAvailable = render.SetPass.Available,
                TrianglesAvailable = render.Triangles.Available,
                VerticesAvailable = render.Vertices.Available,
                ShadowCastersAvailable = render.ShadowCasters.Available,
                UsedBuffersBytesAvailable = render.BuffersBytes.Available,
                UsedBuffersCountAvailable = render.BuffersCount.Available,
                RenderTexturesBytesAvailable = render.RenderTexturesBytes.Available,
                GcCollectTotalNs = gc.TotalNs,
                GcCollectCount = gc.Count,
                GcCollectAvailable = gc.Available,
                AppResidentBytes = raw.AppResident.Value,
                AppResidentAvailable = raw.AppResident.Available,
            };
        }

        private (long TotalNs, long Count, bool Available) CaptureGcDelta()
        {
            int curGen0 = GC.CollectionCount(0);
            int curGen1 = GC.CollectionCount(1);
            int curGen2 = GC.CollectionCount(2);
            var recorder = m_Recorders.ReadGcCollect();
            long curGcStallSumNs = recorder.TotalNs;
            long gcTotalNs = 0;
            long gcCount = 0;
            if (m_GcBaselineCaptured)
            {
                gcCount = (curGen0 - m_PrevGen0Count)
                        + (curGen1 - m_PrevGen1Count)
                        + (curGen2 - m_PrevGen2Count);
                long stallDelta = curGcStallSumNs - m_PrevGcStallSumNs;
                if (stallDelta > 0) gcTotalNs = stallDelta;
            }
            m_PrevGen0Count = curGen0;
            m_PrevGen1Count = curGen1;
            m_PrevGen2Count = curGen2;
            m_PrevGcStallSumNs = curGcStallSumNs;
            m_GcBaselineCaptured = true;
            return (gcTotalNs, gcCount, recorder.Available);
        }

        private double UpdateManagedGrowth(long managed, float reportIntervalSeconds)
        {
            if (!m_GrowthBaselineCaptured)
            {
                m_LastReportManagedBytes = managed;
                m_GrowthBaselineCaptured = true;
                return 0;
            }

            long managedDelta = managed - m_LastReportManagedBytes;
            m_LastReportManagedBytes = managed;
            return reportIntervalSeconds > 0 ? managedDelta / BYTES_PER_MB / reportIntervalSeconds : 0;
        }

        private bool CaptureBaselines(RawMemoryCounters raw)
        {
            bool baselineJustCaptured = CaptureMemoryBaseline(raw);
            if (!m_GfxBaselineCaptured && raw.GpuMemory.Value > 0)
            {
                m_BaselineGfxUsed = raw.GpuMemory.Value;
                m_GfxBaselineCaptured = true;
            }
            return baselineJustCaptured;
        }

        private bool CaptureMemoryBaseline(RawMemoryCounters raw)
        {
            if (m_BaselineCaptured) return false;
            m_BaselineManaged = raw.Managed;
            m_BaselineMonoHeap = raw.Mono;
            m_BaselineNativeAlloc = raw.NativeAlloc;
            m_BaselineNativeReserved = raw.NativeReserved;
            m_BaselineAudioUsed = raw.Audio.Value;
            m_BaselineCaptured = true;
            return true;
        }

        public void ResetBaseline()
        {
            m_BaselineCaptured = false;
            m_GfxBaselineCaptured = false;
            m_GrowthBaselineCaptured = false;
            m_LastReportManagedBytes = 0;
            m_GcBaselineCaptured = false;
            m_PrevGen0Count = 0;
            m_PrevGen1Count = 0;
            m_PrevGen2Count = 0;
            m_PrevGcStallSumNs = 0;
        }

    }
}
