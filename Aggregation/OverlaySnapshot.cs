using System;
using System.Collections.Generic;

namespace VanillaProfiler
{
    /// <summary>
    /// Read-only view of the last reporting window. Built by ReportBuilder and
    /// then published to overlay/export consumers.
    /// </summary>
    public sealed class OverlaySnapshot
    {
        public double AvgFps { get; internal set; }
        public float WindowSeconds { get; internal set; }
        public double MinFps { get; internal set; }
        public double AvgFrameMs { get; internal set; }
        public double MaxFrameMs { get; internal set; }
        public double SimTicksPerSec { get; internal set; }
        public double ManagedGrowthMBperSec { get; internal set; }
        public int Spikes30fps { get; internal set; }
        public int Spikes20fps { get; internal set; }
        // Top rows use self/exclusive main-thread cost. Tuple member stays TotalMs
        // for API stability inside the overlay/export code.
        public IReadOnlyList<(string Name, double TotalMs)> TopVanillaSystems { get; internal set; }
            = Array.Empty<(string, double)>();
        public IReadOnlyList<(string Name, double TotalMs)> TopModSystems { get; internal set; }
            = Array.Empty<(string, double)>();
        public IReadOnlyList<(string ModName, double TotalMs)> TopMods { get; internal set; }
            = Array.Empty<(string, double)>();
        public double ManagedMB { get; internal set; }
        public double ManagedDeltaMB { get; internal set; }

        public double ProfilerSelfMs { get; internal set; }
        public double ProfilerSelfPercent { get; internal set; }

        public double GfxUsedMB { get; internal set; }
        public double AudioUsedMB { get; internal set; }
        public double MainThreadCpuMs { get; internal set; }
        public double RenderThreadCpuMs { get; internal set; }
        public double GpuFrameTimeMs { get; internal set; }
        public double PresentWaitMs { get; internal set; }
        public bool GfxUsedAvailable { get; internal set; }
        public bool AudioUsedAvailable { get; internal set; }
        public bool MainThreadCpuAvailable { get; internal set; }
        public bool RenderThreadCpuAvailable { get; internal set; }
        public bool GpuFrameTimeAvailable { get; internal set; }
        public bool PresentWaitAvailable { get; internal set; }

        public long DrawCalls { get; internal set; }
        public long SetPassCalls { get; internal set; }
        public long Triangles { get; internal set; }
        public long Vertices { get; internal set; }
        public long ShadowCasters { get; internal set; }
        public bool DrawCallsAvailable { get; internal set; }
        public bool SetPassCallsAvailable { get; internal set; }
        public bool TrianglesAvailable { get; internal set; }
        public bool VerticesAvailable { get; internal set; }
        public bool ShadowCastersAvailable { get; internal set; }
        public double UsedBuffersMB { get; internal set; }
        public long UsedBuffersCount { get; internal set; }
        public double RenderTexturesMB { get; internal set; }
        public bool UsedBuffersBytesAvailable { get; internal set; }
        public bool UsedBuffersCountAvailable { get; internal set; }
        public bool RenderTexturesBytesAvailable { get; internal set; }
        public double GcCollectStallMs { get; internal set; }
        public long GcCollectCount { get; internal set; }
        public bool GcCollectAvailable { get; internal set; }
        public double AppResidentMB { get; internal set; }
        public bool SystemUsedAvailable { get; internal set; }
        public bool AppResidentAvailable { get; internal set; }

        public IReadOnlyList<(string VanillaSystem, string OwnerMod, double TotalMs)> ReplacedVanillaSystems { get; internal set; }
            = Array.Empty<(string, string, double)>();
    }
}
