using System;
using Unity.Profiling;
using UnityProfiler = UnityEngine.Profiling.Profiler;

namespace VanillaProfiler.Aggregation
{
#pragma warning disable CA1815
    internal readonly struct RecorderValue
    {
        public RecorderValue(long value, bool available)
        {
            Value = available ? value : 0;
            Available = available;
        }

        public long Value { get; }
        public bool Available { get; }
    }
#pragma warning restore CA1815

    internal struct RawMemoryCounters
    {
        public long Managed;
        public long Mono;
        public long NativeAlloc;
        public long NativeReserved;
        public RecorderValue GpuMemory;
        public RecorderValue Audio;
        public RecorderValue System;
        public RecorderValue AppResident;
    }

    internal struct FrameTimingCounters
    {
        public RecorderValue MainThread;
        public RecorderValue RenderThread;
        public RecorderValue GpuFrame;
        public RecorderValue PresentWait;
    }

    internal struct RenderCounters
    {
        public RecorderValue DrawCalls;
        public RecorderValue SetPass;
        public RecorderValue Triangles;
        public RecorderValue Vertices;
        public RecorderValue ShadowCasters;
        public RecorderValue BuffersBytes;
        public RecorderValue BuffersCount;
        public RecorderValue RenderTexturesBytes;
    }

    internal readonly struct GcRecorderSnapshot
    {
        public GcRecorderSnapshot(long totalNs, bool available)
        {
            TotalNs = totalNs;
            Available = available;
        }

        public readonly long TotalNs;
        public readonly bool Available;
    }

    internal sealed class MemoryRecorderSet : IDisposable
    {
        // 60fps x 5s default report interval = 300 samples.
        private const int FRAME_TIMING_SAMPLE_COUNT = 300;
        private const int SAMPLE_BUFFER_CAPACITY = 4096;

        private readonly ProfilerRecorderSamples m_Samples = new(SAMPLE_BUFFER_CAPACITY);

        private ProfilerRecorder m_VideoMemoryRecorder;
        private ProfilerRecorder m_AudioUsedRecorder;
        private ProfilerRecorder m_SystemUsedRecorder;
        private ProfilerRecorder m_MainThreadRecorder;
        private ProfilerRecorder m_RenderThreadRecorder;
        private ProfilerRecorder m_GpuFrameTimeRecorder;
        private ProfilerRecorder m_PresentWaitRecorder;
        private ProfilerRecorder m_DrawCallsRecorder;
        private ProfilerRecorder m_SetPassCallsRecorder;
        private ProfilerRecorder m_TrianglesRecorder;
        private ProfilerRecorder m_VerticesRecorder;
        private ProfilerRecorder m_ShadowCastersRecorder;
        private ProfilerRecorder m_UsedBuffersBytesRecorder;
        private ProfilerRecorder m_UsedBuffersCountRecorder;
        private ProfilerRecorder m_RenderTexturesBytesRecorder;
        private ProfilerRecorder m_GcCollectRecorder;
        private ProfilerRecorder m_AppResidentRecorder;

        public void Start()
        {
            StartMemoryRecorders();
            StartTimingRecorders();
            StartRenderCountRecorders();
            StartGpuBreakdownRecorders();
            m_GcCollectRecorder = ProfilerRecorderFactory.StartByHandle("GC", "GC.Collect", 4096);
        }

        public void Reset()
        {
            DisposeRecorders();
            m_Samples.Clear();
            Start();
        }

        public void Dispose()
        {
            DisposeRecorders();
        }

        public RawMemoryCounters ReadRawMemory()
        {
            long gpuMemory = UnityProfiler.GetAllocatedMemoryForGraphicsDriver();
            bool gpuMemoryAvailable = gpuMemory > 0 || m_VideoMemoryRecorder.Valid;
            if (gpuMemory == 0) gpuMemory = ReadValue(m_VideoMemoryRecorder).Value;

            return new RawMemoryCounters
            {
                Managed = GC.GetTotalMemory(forceFullCollection: false),
                Mono = UnityProfiler.GetMonoHeapSizeLong(),
                NativeAlloc = UnityProfiler.GetTotalAllocatedMemoryLong(),
                NativeReserved = UnityProfiler.GetTotalReservedMemoryLong(),
                GpuMemory = new RecorderValue(gpuMemory, gpuMemoryAvailable),
                Audio = ReadValue(m_AudioUsedRecorder),
                System = ReadValue(m_SystemUsedRecorder),
                AppResident = ReadValue(m_AppResidentRecorder),
            };
        }

        public FrameTimingCounters ReadTimingCounters()
        {
            return new FrameTimingCounters
            {
                MainThread = ReadAverage(m_MainThreadRecorder),
                RenderThread = ReadAverage(m_RenderThreadRecorder),
                GpuFrame = ReadAverage(m_GpuFrameTimeRecorder),
                PresentWait = ReadAverage(m_PresentWaitRecorder),
            };
        }

        public RenderCounters ReadRenderCounters()
        {
            return new RenderCounters
            {
                DrawCalls = ReadValue(m_DrawCallsRecorder),
                SetPass = ReadValue(m_SetPassCallsRecorder),
                Triangles = ReadValue(m_TrianglesRecorder),
                Vertices = ReadValue(m_VerticesRecorder),
                ShadowCasters = ReadValue(m_ShadowCastersRecorder),
                BuffersBytes = ReadValue(m_UsedBuffersBytesRecorder),
                BuffersCount = ReadValue(m_UsedBuffersCountRecorder),
                RenderTexturesBytes = ReadValue(m_RenderTexturesBytesRecorder),
            };
        }

        public GcRecorderSnapshot ReadGcCollect()
        {
            (long totalNs, _) = m_Samples.SumWithCount(m_GcCollectRecorder);
            return new GcRecorderSnapshot(totalNs, m_GcCollectRecorder.Valid);
        }

        public string BuildValiditySummary()
        {
            return "ProfilerRecorder validity: " +
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
                $"GC.Collect={m_GcCollectRecorder.Valid}";
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

        private void DisposeRecorders()
        {
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

        private RecorderValue ReadAverage(ProfilerRecorder recorder)
            => new RecorderValue(m_Samples.Average(recorder), recorder.Valid);

        private static RecorderValue ReadValue(ProfilerRecorder recorder)
            => new RecorderValue(recorder.Valid ? recorder.LastValue : 0, recorder.Valid);
    }
}
