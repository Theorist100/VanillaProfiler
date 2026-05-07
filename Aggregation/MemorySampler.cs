using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityProfiler = UnityEngine.Profiling.Profiler;

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

        private long m_LastReportManagedBytes;
        private bool m_GrowthBaselineCaptured;

        private ProfilerRecorder m_VideoMemoryRecorder;   // GPU memory (CS2's only working GPU memory marker)
        private ProfilerRecorder m_AudioUsedRecorder;
        private ProfilerRecorder m_SystemUsedRecorder;
        private ProfilerRecorder m_MainThreadRecorder;    // CPU Main Thread Frame Time (game logic)
        private ProfilerRecorder m_RenderThreadRecorder;  // CPU Render Thread Frame Time (render submission)
        private ProfilerRecorder m_GpuFrameTimeRecorder;  // GPU Frame Time (actual hardware time)
        // Aggregate worker-thread job stats — speculative, marker availability depends
        // on Unity build flags. We try several common names, surface whichever is
        // exposed, and silently report 0 when none are valid.
        private ProfilerRecorder m_WorkerTimeRecorder;    // sum of job execution time on workers (ns)
        private ProfilerRecorder m_WorkerWaitRecorder;    // main-thread wait on workers (ns) = real sync cost

        // Reusable buffer for ProfilerRecorder.CopyTo. The Unity API on this build
        // only accepts List<ProfilerRecorderSample>, so we keep one instance and
        // Clear() it before each fill — Clear preserves the underlying capacity, so
        // steady-state sampling allocates nothing once the list grows to the largest
        // recorder we read (capacity 15 for the Render-category recorders).
        private readonly List<ProfilerRecorderSample> m_SampleBuffer = new(16);

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
            m_VideoMemoryRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Video Used Memory");
            m_AudioUsedRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Audio Used Memory");
            m_SystemUsedRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "System Used Memory");
            m_MainThreadRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "CPU Main Thread Frame Time", 15);
            m_RenderThreadRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "CPU Render Thread Frame Time", 15);
            m_GpuFrameTimeRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GPU Frame Time", 15);

            // Speculative — these markers exist in development builds but are often
            // stripped in release. ProfilerRecorder is safe to start even when the
            // marker is absent: .Valid stays false and reads return 0.
            m_WorkerTimeRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "JobsParallelFor.Execute", 15);
            m_WorkerWaitRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "WaitForJobGroupID", 15);

            ModLog.Info(
                "ProfilerRecorder validity: " +
                $"Video={m_VideoMemoryRecorder.Valid} " +
                $"Audio={m_AudioUsedRecorder.Valid} " +
                $"System={m_SystemUsedRecorder.Valid} " +
                $"Main={m_MainThreadRecorder.Valid} " +
                $"Render={m_RenderThreadRecorder.Valid} " +
                $"GPU={m_GpuFrameTimeRecorder.Valid} " +
                $"WorkerExec={m_WorkerTimeRecorder.Valid} " +
                $"WorkerWait={m_WorkerWaitRecorder.Valid}");
        }

        public void Dispose()
        {
            if (m_VideoMemoryRecorder.Valid) m_VideoMemoryRecorder.Dispose();
            if (m_AudioUsedRecorder.Valid) m_AudioUsedRecorder.Dispose();
            if (m_SystemUsedRecorder.Valid) m_SystemUsedRecorder.Dispose();
            if (m_MainThreadRecorder.Valid) m_MainThreadRecorder.Dispose();
            if (m_RenderThreadRecorder.Valid) m_RenderThreadRecorder.Dispose();
            if (m_GpuFrameTimeRecorder.Valid) m_GpuFrameTimeRecorder.Dispose();
            if (m_WorkerTimeRecorder.Valid) m_WorkerTimeRecorder.Dispose();
            if (m_WorkerWaitRecorder.Valid) m_WorkerWaitRecorder.Dispose();
        }

        public MemorySample Sample(float reportIntervalSeconds)
        {
            long managed = GC.GetTotalMemory(forceFullCollection: false);
            long mono = UnityProfiler.GetMonoHeapSizeLong();
            long nativeAlloc = UnityProfiler.GetTotalAllocatedMemoryLong();
            long nativeReserved = UnityProfiler.GetTotalReservedMemoryLong();

            // GPU memory: Unity's "Gfx Used Memory" marker isn't registered on this
            // CS2 build (verified via ProfilerRecorderHandle.GetAvailable). Use the
            // legacy graphics-driver API which talks to the driver directly.
            long gpuMemory = UnityProfiler.GetAllocatedMemoryForGraphicsDriver();
            if (gpuMemory == 0) gpuMemory = ReadValid(m_VideoMemoryRecorder);
            long audio = ReadValid(m_AudioUsedRecorder);
            long system = ReadValid(m_SystemUsedRecorder);
            long mainThread = AverageValid(m_MainThreadRecorder);
            long renderThread = AverageValid(m_RenderThreadRecorder);
            long gpuFrame = AverageValid(m_GpuFrameTimeRecorder);
            long workerTime = AverageValid(m_WorkerTimeRecorder);
            long workerWait = AverageValid(m_WorkerWaitRecorder);

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
                m_BaselineGfxUsed = gpuMemory;
                m_BaselineAudioUsed = audio;
                m_BaselineCaptured = true;
                baselineJustCaptured = true;
            }

            return new MemorySample
            {
                ManagedBytes = managed,
                MonoHeapBytes = mono,
                NativeAllocBytes = nativeAlloc,
                NativeReservedBytes = nativeReserved,
                GfxUsedBytes = gpuMemory,
                AudioUsedBytes = audio,
                VideoUsedBytes = 0,
                SystemUsedBytes = system,
                ManagedDelta = managed - m_BaselineManaged,
                MonoHeapDelta = mono - m_BaselineMonoHeap,
                NativeAllocDelta = nativeAlloc - m_BaselineNativeAlloc,
                NativeReservedDelta = nativeReserved - m_BaselineNativeReserved,
                GfxUsedDelta = gpuMemory - m_BaselineGfxUsed,
                AudioUsedDelta = audio - m_BaselineAudioUsed,
                VideoUsedDelta = 0,
                ManagedGrowthMBperSec = managedGrowthRate,
                BaselineJustCaptured = baselineJustCaptured,
                MainThreadCpuNs = mainThread,
                RenderThreadCpuNs = renderThread,
                GpuFrameTimeNs = gpuFrame,
                JobWorkerTimeNs = workerTime,
                JobWorkerWaitNs = workerWait,
            };
        }

        public void ResetBaseline()
        {
            m_BaselineCaptured = false;
            m_GrowthBaselineCaptured = false;
            m_LastReportManagedBytes = 0;
        }

        private static long ReadValid(ProfilerRecorder r) => r.Valid ? r.LastValue : 0;

        private long AverageValid(ProfilerRecorder r)
        {
            if (!r.Valid || r.Capacity == 0) return 0;
            int count = r.Count;
            if (count == 0) return 0;
            m_SampleBuffer.Clear();
            r.CopyTo(m_SampleBuffer);
            long sum = 0;
            int n = m_SampleBuffer.Count;
            for (int i = 0; i < n; i++)
                sum += m_SampleBuffer[i].Value;
            return n > 0 ? sum / n : 0;
        }
    }
}
