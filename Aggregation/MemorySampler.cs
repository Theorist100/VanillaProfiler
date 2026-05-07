using System;
using System.Collections.Generic;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
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
        // 60fps × 5s default report interval = 300 samples. ProfilerRecorder ring
        // overwrites oldest, so AverageValid sees the last ~5 seconds of frame timing.
        private const int FRAME_TIMING_SAMPLE_COUNT = 300;

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

        private ProfilerRecorder m_VideoMemoryRecorder;   // GPU memory (CS2's only working GPU memory marker)
        private ProfilerRecorder m_AudioUsedRecorder;
        private ProfilerRecorder m_SystemUsedRecorder;
        private ProfilerRecorder m_MainThreadRecorder;    // CPU Main Thread Frame Time (game logic)
        private ProfilerRecorder m_RenderThreadRecorder;  // CPU Render Thread Frame Time (render submission)
        private ProfilerRecorder m_GpuFrameTimeRecorder;  // GPU Frame Time (actual hardware time)
        private ProfilerRecorder m_PresentWaitRecorder;   // Gfx.WaitForPresentOnGfxThread — GPU-bound signal
        // Per-frame Render counts — useful for diagnosing render-pipeline regressions
        // (DrawCalls jump after save load = culling/batching break, not a managed leak).
        // Marker availability follows the same Valid==false → reads 0 contract.
        private ProfilerRecorder m_DrawCallsRecorder;
        private ProfilerRecorder m_SetPassCallsRecorder;
        private ProfilerRecorder m_TrianglesRecorder;
        private ProfilerRecorder m_VerticesRecorder;
        private ProfilerRecorder m_ShadowCastersRecorder;  // Visible shadow-cast renderers
        // GPU memory breakdown — splits "Gfx total" into buffers vs render textures.
        private ProfilerRecorder m_UsedBuffersBytesRecorder;
        private ProfilerRecorder m_UsedBuffersCountRecorder;
        private ProfilerRecorder m_RenderTexturesBytesRecorder;
        // GC.Collect (TimeNanoseconds, fires once per collection) — sum across the
        // report window distinguishes GC-induced stutter from non-GC main stalls.
        private ProfilerRecorder m_GcCollectRecorder;
        // Process RSS — diverges from System Used when the OS pages parts out.
        private ProfilerRecorder m_AppResidentRecorder;

        // Reusable buffer for ProfilerRecorder.CopyTo. Sized to the largest recorder
        // we read — GC.Collect at 4096 samples — so steady-state sampling never
        // reallocates after the first call. Memory cost ~64 KB.
        private const int SAMPLE_BUFFER_CAPACITY = 4096;
        private readonly List<ProfilerRecorderSample> m_SampleBuffer = new(SAMPLE_BUFFER_CAPACITY);

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
            // Memory totals + process RSS.
            m_VideoMemoryRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Video Used Memory");
            m_AudioUsedRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Audio Used Memory");
            m_SystemUsedRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "System Used Memory");
            m_AppResidentRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "App Resident Memory");

            // Render-thread timing breakdown.
            m_MainThreadRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "CPU Main Thread Frame Time", FRAME_TIMING_SAMPLE_COUNT);
            m_RenderThreadRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "CPU Render Thread Frame Time", FRAME_TIMING_SAMPLE_COUNT);
            m_GpuFrameTimeRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GPU Frame Time", FRAME_TIMING_SAMPLE_COUNT);
            m_PresentWaitRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Gfx.WaitForPresentOnGfxThread", FRAME_TIMING_SAMPLE_COUNT);

            // Per-frame render counts (LastValue, all confirmed in CS2 release via the
            // MarkerEnumerator dump).
            m_DrawCallsRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            m_SetPassCallsRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            m_TrianglesRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            m_VerticesRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Vertices Count");
            m_ShadowCastersRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Shadow Casters Count");

            // GPU memory breakdown — splits opaque Gfx total into buffers vs RTs.
            m_UsedBuffersBytesRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Used Buffers Bytes");
            m_UsedBuffersCountRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Used Buffers Count");
            m_RenderTexturesBytesRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Render Textures Bytes");

            // GC.Collect lives in a category that Unity 2022.3 doesn't expose as a typed
            // ProfilerCategory constant — only by name in ProfilerRecorderHandle.GetAvailable.
            // Cap 4096 — live CS2 1.5.7 logs hit 512 (the previous cap) every report after
            // the city is loaded, with a worst-observed 637 ms stall in a 5 s window. Real
            // count was higher; the ring was truncating. 4096 covers >800 collections/sec
            // (truly pathological — needs city-scale allocation pressure) at ~64 KB cost.
            m_GcCollectRecorder = StartByHandle("GC", "GC.Collect", 4096);

            // Speculative job-worker recorders were dropped: MarkerEnumerator confirmed
            // CS2 release strips JobsParallelFor.Execute / WaitForJobGroupID. Adding
            // them back only makes sense if Paradox ships a development build later.

            ModLog.Info(
                "ProfilerRecorder validity: " +
                $"Video={m_VideoMemoryRecorder.Valid} " +
                $"Audio={m_AudioUsedRecorder.Valid} " +
                $"System={m_SystemUsedRecorder.Valid} " +
                $"AppResident={m_AppResidentRecorder.Valid} " +
                $"Main={m_MainThreadRecorder.Valid} " +
                $"Render={m_RenderThreadRecorder.Valid} " +
                $"GPU={m_GpuFrameTimeRecorder.Valid} " +
                $"PresentWait={m_PresentWaitRecorder.Valid} " +
                $"DrawCalls={m_DrawCallsRecorder.Valid} " +
                $"SetPass={m_SetPassCallsRecorder.Valid} " +
                $"ShadowCasters={m_ShadowCastersRecorder.Valid} " +
                $"Triangles={m_TrianglesRecorder.Valid} " +
                $"Vertices={m_VerticesRecorder.Valid} " +
                $"BuffersBytes={m_UsedBuffersBytesRecorder.Valid} " +
                $"BuffersCount={m_UsedBuffersCountRecorder.Valid} " +
                $"RTBytes={m_RenderTexturesBytesRecorder.Valid} " +
                $"GC.Collect={m_GcCollectRecorder.Valid}");
        }

        public void Dispose()
        {
            // Dispose unconditionally. ProfilerRecorder.Valid only reports whether the
            // marker was found at StartNew time — speculative recorders that started Valid
            // can still hold a native handle even after .Valid flips false. Guarding on
            // .Valid here would leak native profiler slots per mod reload.
            // ProfilerRecorder.Dispose is safe on already-disposed/invalid handles.
            m_VideoMemoryRecorder.Dispose();
            m_AudioUsedRecorder.Dispose();
            m_SystemUsedRecorder.Dispose();
            m_AppResidentRecorder.Dispose();
            m_MainThreadRecorder.Dispose();
            m_RenderThreadRecorder.Dispose();
            m_GpuFrameTimeRecorder.Dispose();
            m_PresentWaitRecorder.Dispose();
            m_DrawCallsRecorder.Dispose();
            m_SetPassCallsRecorder.Dispose();
            m_TrianglesRecorder.Dispose();
            m_VerticesRecorder.Dispose();
            m_ShadowCastersRecorder.Dispose();
            m_UsedBuffersBytesRecorder.Dispose();
            m_UsedBuffersCountRecorder.Dispose();
            m_RenderTexturesBytesRecorder.Dispose();
            m_GcCollectRecorder.Dispose();
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
            long presentWait = AverageValid(m_PresentWaitRecorder);
            // Render counts: LastValue, not average — these are per-frame totals already.
            long drawCalls = ReadValid(m_DrawCallsRecorder);
            long setPass = ReadValid(m_SetPassCallsRecorder);
            long triangles = ReadValid(m_TrianglesRecorder);
            long vertices = ReadValid(m_VerticesRecorder);
            long shadowCasters = ReadValid(m_ShadowCastersRecorder);
            long buffersBytes = ReadValid(m_UsedBuffersBytesRecorder);
            long buffersCount = ReadValid(m_UsedBuffersCountRecorder);
            long rtBytes = ReadValid(m_RenderTexturesBytesRecorder);
            long appResident = ReadValid(m_AppResidentRecorder);
            // GC: hybrid count + stall computation.
            //   COUNT: System.GC.CollectionCount is cumulative across process lifetime —
            //          subtract from previous sample for an exact per-window delta with
            //          no ring-overflow risk.
            //   STALL: ProfilerRecorder GC.Collect sum delta. Accurate while the ring
            //          (capacity 4096) holds more than one window of GCs — at typical
            //          24 GCs/sec we have ~170 s of headroom before delta starts to drift.
            int curGen0 = GC.CollectionCount(0);
            int curGen1 = GC.CollectionCount(1);
            int curGen2 = GC.CollectionCount(2);
            (long curGcStallSumNs, _) = SumWithCount(m_GcCollectRecorder);
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
                m_BaselineAudioUsed = audio;
                m_BaselineCaptured = true;
                baselineJustCaptured = true;
            }

            // GPU memory can report 0 before the driver warms up, or forever when the
            // marker/API is stripped. Keep the core memory baseline live immediately
            // and baseline GPU separately only once it becomes a real signal.
            if (!m_GfxBaselineCaptured && gpuMemory > 0)
            {
                m_BaselineGfxUsed = gpuMemory;
                m_GfxBaselineCaptured = true;
            }

            return new MemorySample
            {
                ManagedBytes = managed,
                MonoHeapBytes = mono,
                NativeAllocBytes = nativeAlloc,
                NativeReservedBytes = nativeReserved,
                GfxUsedBytes = gpuMemory,
                AudioUsedBytes = audio,
                SystemUsedBytes = system,
                ManagedDelta = managed - m_BaselineManaged,
                MonoHeapDelta = mono - m_BaselineMonoHeap,
                NativeAllocDelta = nativeAlloc - m_BaselineNativeAlloc,
                NativeReservedDelta = nativeReserved - m_BaselineNativeReserved,
                GfxUsedDelta = m_GfxBaselineCaptured ? gpuMemory - m_BaselineGfxUsed : 0,
                AudioUsedDelta = audio - m_BaselineAudioUsed,
                ManagedGrowthMBperSec = managedGrowthRate,
                BaselineJustCaptured = baselineJustCaptured,
                MainThreadCpuNs = mainThread,
                RenderThreadCpuNs = renderThread,
                GpuFrameTimeNs = gpuFrame,
                PresentWaitNs = presentWait,
                DrawCallsCount = drawCalls,
                SetPassCallsCount = setPass,
                TrianglesCount = triangles,
                VerticesCount = vertices,
                ShadowCastersCount = shadowCasters,
                UsedBuffersBytes = buffersBytes,
                UsedBuffersCount = buffersCount,
                RenderTexturesBytes = rtBytes,
                GcCollectTotalNs = gcTotalNs,
                GcCollectCount = gcCount,
                AppResidentBytes = appResident,
            };
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

        // Resolve a marker by category name + statName when the typed
        // ProfilerCategory constant isn't exposed (e.g. "GC" in 2022.3). Falls back
        // to a default recorder when no match is found — reads stay at zero.
        private static ProfilerRecorder StartByHandle(string categoryName, string statName, int capacity)
        {
            var handles = new List<ProfilerRecorderHandle>(256);
            try
            {
                ProfilerRecorderHandle.GetAvailable(handles);
            }
            catch
            {
                return default;
            }
            for (int i = 0; i < handles.Count; i++)
            {
                var desc = ProfilerRecorderHandle.GetDescription(handles[i]);
                if (string.Equals(desc.Category.Name, categoryName, StringComparison.Ordinal)
                    && string.Equals(desc.Name, statName, StringComparison.Ordinal))
                {
                    // ProfilerRecorder constructor takes a handle + capacity; the typed
                    // StartNew(category, name, capacity) factory only accepts ProfilerCategory.
                    var recorder = new ProfilerRecorder(handles[i], capacity, ProfilerRecorderOptions.Default);
                    recorder.Start();
                    return recorder;
                }
            }
            return default;
        }

        // For event-style markers (GC.Collect fires once per collection): sum all
        // captured samples to get total work over the window, plus the count of
        // events. Returns (0, 0) when the recorder is invalid or empty.
        private (long sum, long count) SumWithCount(ProfilerRecorder r)
        {
            if (!r.Valid || r.Capacity == 0) return (0, 0);
            int n = r.Count;
            if (n == 0) return (0, 0);
            m_SampleBuffer.Clear();
            r.CopyTo(m_SampleBuffer);
            long sum = 0;
            int captured = m_SampleBuffer.Count;
            for (int i = 0; i < captured; i++)
                sum += m_SampleBuffer[i].Value;
            return (sum, captured);
        }
    }
}
