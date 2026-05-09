using System;
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
        private readonly ProfilerRecorderSamples m_Samples = new(SAMPLE_BUFFER_CAPACITY);

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
            StartMemoryRecorders();
            StartTimingRecorders();
            StartRenderCountRecorders();
            StartGpuBreakdownRecorders();
            StartGcRecorder();

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

        private void StartMemoryRecorders()
        {
            m_VideoMemoryRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Video Used Memory");
            m_AudioUsedRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Audio Used Memory");
            m_SystemUsedRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "System Used Memory");
            m_AppResidentRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "App Resident Memory");
        }

        private void StartTimingRecorders()
        {
            m_MainThreadRecorder = ProfilerRecorder.StartNew(
                ProfilerCategory.Render, "CPU Main Thread Frame Time", FRAME_TIMING_SAMPLE_COUNT);
            m_RenderThreadRecorder = ProfilerRecorder.StartNew(
                ProfilerCategory.Render, "CPU Render Thread Frame Time", FRAME_TIMING_SAMPLE_COUNT);
            m_GpuFrameTimeRecorder = ProfilerRecorder.StartNew(
                ProfilerCategory.Render, "GPU Frame Time", FRAME_TIMING_SAMPLE_COUNT);
            m_PresentWaitRecorder = ProfilerRecorder.StartNew(
                ProfilerCategory.Render, "Gfx.WaitForPresentOnGfxThread", FRAME_TIMING_SAMPLE_COUNT);
        }

        private void StartRenderCountRecorders()
        {
            m_DrawCallsRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            m_SetPassCallsRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            m_TrianglesRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            m_VerticesRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Vertices Count");
            m_ShadowCastersRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Shadow Casters Count");
        }

        private void StartGpuBreakdownRecorders()
        {
            m_UsedBuffersBytesRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Used Buffers Bytes");
            m_UsedBuffersCountRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Used Buffers Count");
            m_RenderTexturesBytesRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Render Textures Bytes");
        }

        private void StartGcRecorder()
        {
            m_GcCollectRecorder = ProfilerRecorderFactory.StartByHandle("GC", "GC.Collect", 4096);
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
            var raw = ReadRawMemory();
            var timing = ReadTimingCounters();
            var render = ReadRenderCounters();
            var gc = CaptureGcDelta();
            double managedGrowthRate = UpdateManagedGrowth(raw.Managed, reportIntervalSeconds);
            bool baselineJustCaptured = CaptureBaselines(raw);
            return new MemorySample
            {
                ManagedBytes = raw.Managed,
                MonoHeapBytes = raw.Mono,
                NativeAllocBytes = raw.NativeAlloc,
                NativeReservedBytes = raw.NativeReserved,
                GfxUsedBytes = raw.GpuMemory,
                AudioUsedBytes = raw.Audio,
                SystemUsedBytes = raw.System,
                GfxUsedAvailable = raw.GpuMemoryAvailable,
                AudioUsedAvailable = raw.AudioAvailable,
                SystemUsedAvailable = raw.SystemAvailable,
                ManagedDelta = raw.Managed - m_BaselineManaged,
                MonoHeapDelta = raw.Mono - m_BaselineMonoHeap,
                NativeAllocDelta = raw.NativeAlloc - m_BaselineNativeAlloc,
                NativeReservedDelta = raw.NativeReserved - m_BaselineNativeReserved,
                GfxUsedDelta = m_GfxBaselineCaptured ? raw.GpuMemory - m_BaselineGfxUsed : 0,
                AudioUsedDelta = raw.Audio - m_BaselineAudioUsed,
                ManagedGrowthMBperSec = managedGrowthRate,
                BaselineJustCaptured = baselineJustCaptured,
                MainThreadCpuNs = timing.MainThread,
                RenderThreadCpuNs = timing.RenderThread,
                GpuFrameTimeNs = timing.GpuFrame,
                PresentWaitNs = timing.PresentWait,
                MainThreadCpuAvailable = timing.MainThreadAvailable,
                RenderThreadCpuAvailable = timing.RenderThreadAvailable,
                GpuFrameTimeAvailable = timing.GpuFrameAvailable,
                PresentWaitAvailable = timing.PresentWaitAvailable,
                DrawCallsCount = render.DrawCalls,
                SetPassCallsCount = render.SetPass,
                TrianglesCount = render.Triangles,
                VerticesCount = render.Vertices,
                ShadowCastersCount = render.ShadowCasters,
                UsedBuffersBytes = render.BuffersBytes,
                UsedBuffersCount = render.BuffersCount,
                RenderTexturesBytes = render.RenderTexturesBytes,
                DrawCallsAvailable = render.DrawCallsAvailable,
                SetPassCallsAvailable = render.SetPassAvailable,
                TrianglesAvailable = render.TrianglesAvailable,
                VerticesAvailable = render.VerticesAvailable,
                ShadowCastersAvailable = render.ShadowCastersAvailable,
                UsedBuffersBytesAvailable = render.BuffersBytesAvailable,
                UsedBuffersCountAvailable = render.BuffersCountAvailable,
                RenderTexturesBytesAvailable = render.RenderTexturesBytesAvailable,
                GcCollectTotalNs = gc.TotalNs,
                GcCollectCount = gc.Count,
                GcCollectAvailable = gc.Available,
                AppResidentBytes = raw.AppResident,
                AppResidentAvailable = raw.AppResidentAvailable,
            };
        }

        private (long Managed, long Mono, long NativeAlloc, long NativeReserved, long GpuMemory,
            long Audio, long System, long AppResident, bool GpuMemoryAvailable, bool AudioAvailable,
            bool SystemAvailable, bool AppResidentAvailable) ReadRawMemory()
        {
            long gpuMemory = UnityProfiler.GetAllocatedMemoryForGraphicsDriver();
            bool gpuMemoryAvailable = gpuMemory > 0 || m_VideoMemoryRecorder.Valid;
            if (gpuMemory == 0) gpuMemory = ReadValid(m_VideoMemoryRecorder);
            return (
                GC.GetTotalMemory(forceFullCollection: false),
                UnityProfiler.GetMonoHeapSizeLong(),
                UnityProfiler.GetTotalAllocatedMemoryLong(),
                UnityProfiler.GetTotalReservedMemoryLong(),
                gpuMemory,
                ReadValid(m_AudioUsedRecorder),
                ReadValid(m_SystemUsedRecorder),
                ReadValid(m_AppResidentRecorder),
                gpuMemoryAvailable,
                m_AudioUsedRecorder.Valid,
                m_SystemUsedRecorder.Valid,
                m_AppResidentRecorder.Valid);
        }

        private (long MainThread, long RenderThread, long GpuFrame, long PresentWait,
            bool MainThreadAvailable, bool RenderThreadAvailable, bool GpuFrameAvailable,
            bool PresentWaitAvailable) ReadTimingCounters()
            => (
                m_Samples.Average(m_MainThreadRecorder),
                m_Samples.Average(m_RenderThreadRecorder),
                m_Samples.Average(m_GpuFrameTimeRecorder),
                m_Samples.Average(m_PresentWaitRecorder),
                m_MainThreadRecorder.Valid,
                m_RenderThreadRecorder.Valid,
                m_GpuFrameTimeRecorder.Valid,
                m_PresentWaitRecorder.Valid);

        private (long DrawCalls, long SetPass, long Triangles, long Vertices, long ShadowCasters,
            long BuffersBytes, long BuffersCount, long RenderTexturesBytes, bool DrawCallsAvailable,
            bool SetPassAvailable, bool TrianglesAvailable, bool VerticesAvailable,
            bool ShadowCastersAvailable, bool BuffersBytesAvailable, bool BuffersCountAvailable,
            bool RenderTexturesBytesAvailable) ReadRenderCounters()
            => (
                ReadValid(m_DrawCallsRecorder),
                ReadValid(m_SetPassCallsRecorder),
                ReadValid(m_TrianglesRecorder),
                ReadValid(m_VerticesRecorder),
                ReadValid(m_ShadowCastersRecorder),
                ReadValid(m_UsedBuffersBytesRecorder),
                ReadValid(m_UsedBuffersCountRecorder),
                ReadValid(m_RenderTexturesBytesRecorder),
                m_DrawCallsRecorder.Valid,
                m_SetPassCallsRecorder.Valid,
                m_TrianglesRecorder.Valid,
                m_VerticesRecorder.Valid,
                m_ShadowCastersRecorder.Valid,
                m_UsedBuffersBytesRecorder.Valid,
                m_UsedBuffersCountRecorder.Valid,
                m_RenderTexturesBytesRecorder.Valid);

        private (long TotalNs, long Count, bool Available) CaptureGcDelta()
        {
            int curGen0 = GC.CollectionCount(0);
            int curGen1 = GC.CollectionCount(1);
            int curGen2 = GC.CollectionCount(2);
            (long curGcStallSumNs, _) = m_Samples.SumWithCount(m_GcCollectRecorder);
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
            return (gcTotalNs, gcCount, m_GcCollectRecorder.Valid);
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

        private bool CaptureBaselines(
            (long Managed, long Mono, long NativeAlloc, long NativeReserved, long GpuMemory,
                long Audio, long System, long AppResident, bool GpuMemoryAvailable, bool AudioAvailable,
                bool SystemAvailable, bool AppResidentAvailable) raw)
        {
            bool baselineJustCaptured = CaptureMemoryBaseline(raw);
            if (!m_GfxBaselineCaptured && raw.GpuMemory > 0)
            {
                m_BaselineGfxUsed = raw.GpuMemory;
                m_GfxBaselineCaptured = true;
            }
            return baselineJustCaptured;
        }

        private bool CaptureMemoryBaseline(
            (long Managed, long Mono, long NativeAlloc, long NativeReserved, long GpuMemory,
                long Audio, long System, long AppResident, bool GpuMemoryAvailable, bool AudioAvailable,
                bool SystemAvailable, bool AppResidentAvailable) raw)
        {
            if (m_BaselineCaptured) return false;
            m_BaselineManaged = raw.Managed;
            m_BaselineMonoHeap = raw.Mono;
            m_BaselineNativeAlloc = raw.NativeAlloc;
            m_BaselineNativeReserved = raw.NativeReserved;
            m_BaselineAudioUsed = raw.Audio;
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

        private static long ReadValid(ProfilerRecorder r) => r.Valid ? r.LastValue : 0;

    }
}
