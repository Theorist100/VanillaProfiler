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

            ModLog.Info(
                "ProfilerRecorder validity: " +
                $"Video={m_VideoMemoryRecorder.Valid} " +
                $"Audio={m_AudioUsedRecorder.Valid} " +
                $"System={m_SystemUsedRecorder.Valid} " +
                $"Main={m_MainThreadRecorder.Valid} " +
                $"Render={m_RenderThreadRecorder.Valid} " +
                $"GPU={m_GpuFrameTimeRecorder.Valid}");
        }

        public void Dispose()
        {
            if (m_VideoMemoryRecorder.Valid) m_VideoMemoryRecorder.Dispose();
            if (m_AudioUsedRecorder.Valid) m_AudioUsedRecorder.Dispose();
            if (m_SystemUsedRecorder.Valid) m_SystemUsedRecorder.Dispose();
            if (m_MainThreadRecorder.Valid) m_MainThreadRecorder.Dispose();
            if (m_RenderThreadRecorder.Valid) m_RenderThreadRecorder.Dispose();
            if (m_GpuFrameTimeRecorder.Valid) m_GpuFrameTimeRecorder.Dispose();
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
            };
        }

        public void ResetBaseline()
        {
            m_BaselineCaptured = false;
            m_GrowthBaselineCaptured = false;
            m_LastReportManagedBytes = 0;
        }

        private static long ReadValid(ProfilerRecorder r) => r.Valid ? r.LastValue : 0;

        private static long AverageValid(ProfilerRecorder r)
        {
            if (!r.Valid || r.Capacity == 0) return 0;
            int count = r.Count;
            if (count == 0) return 0;
            // Allocates a small array each call. The sampler runs once per report
            // window (~5s), not on the hot path, so the GC noise is negligible.
            var samples = new System.Collections.Generic.List<ProfilerRecorderSample>(count);
            r.CopyTo(samples);
            long sum = 0;
            for (int i = 0; i < samples.Count; i++)
                sum += samples[i].Value;
            return sum / samples.Count;
        }
    }
}
