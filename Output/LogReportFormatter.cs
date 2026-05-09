using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using VanillaProfiler.Aggregation;
using VanillaProfiler.Diagnostics;

namespace VanillaProfiler.Output
{
    internal static class LogReportFormatter
    {
        private const double MS_PER_SEC = 1000.0;
        private const double BYTES_PER_MB = 1024.0 * 1024.0;

        public static string BuildReportText(int reportNumber, OverlaySnapshot snapshot, HealthReport health,
            MetricsSample metrics, MemorySample memory)
        {
            var sb = new StringBuilder(8192);
            sb.AppendLine();
            sb.AppendLine(Inv($"══════ Report #{reportNumber} ({DateTime.Now:HH:mm:ss}, {snapshot.WindowSeconds:F1}s) ══════"));

            if (metrics.FrameCount > 0)
            {
                sb.AppendLine(Inv($"Render: {snapshot.AvgFps:F1} FPS ({snapshot.MinFps:F1} min) | Frame: {snapshot.AvgFrameMs:F1}ms avg, {snapshot.MaxFrameMs:F1}ms max | Sim: {snapshot.SimTicksPerSec:F0} ticks/s"));
                sb.AppendLine(Inv($"Spikes: {metrics.Spikes30} below 30fps, {metrics.Spikes20} below 20fps"));
            }

            AppendPhaseTable(sb, metrics.Buckets.Phases);
            AppendSystemTable(sb, "TOP MODS (self main-thread cost)", metrics.Buckets.ModAggregate, 10);
            AppendSystemTable(sb, "VANILLA SYSTEMS — self main-thread cost (top 15)", metrics.Buckets.VanillaSystems, 15);
            AppendSystemTable(sb, "MOD SYSTEMS — self main-thread cost (top 15)", metrics.Buckets.ModSystems, 15);
            AppendSystemTable(sb, "PATCHED VANILLA SYSTEMS — total Update ms (mod+vanilla split unknown)",
                metrics.Buckets.PatchedVanillaSystems, 15, useInclusiveAsPrimary: true);
            AppendMemorySection(sb, memory);
            AppendHealthSummary(sb, health);
            return sb.ToString();
        }

        private static void AppendPhaseTable(StringBuilder sb, IReadOnlyDictionary<string, PhaseData> phases)
        {
            if (phases.Count == 0) return;
            var rows = new List<KeyValuePair<string, PhaseData>>(phases.Count);
            foreach (var kvp in phases) rows.Add(kvp);
            rows.Sort(static (a, b) => b.Value.TotalTicks.CompareTo(a.Value.TotalTicks));
            sb.AppendLine();
            sb.AppendLine($"{"PHASE",-35} {"CALLS",6} {"TOTAL",10} {"AVG",8} {"MAX",8} {"SYNC?",6}");
            sb.AppendLine(new string('─', 79));
            for (int i = 0; i < rows.Count; i++)
            {
                var kvp = rows[i];
                var d = kvp.Value;
                double totalMs = d.TotalTicks * MS_PER_SEC / Stopwatch.Frequency;
                double avgMs = d.CallCount > 0 ? totalMs / d.CallCount : 0;
                double maxMs = d.MaxTicks * MS_PER_SEC / Stopwatch.Frequency;
                sb.AppendLine(Inv($"{kvp.Key,-35} {d.CallCount,6} {totalMs,9:F1}ms {avgMs,7:F2}ms {maxMs,7:F1}ms {d.SyncPointSuspectCount,6}"));
            }
        }

        private static void AppendSystemTable(
            StringBuilder sb,
            string header,
            IReadOnlyDictionary<string, PhaseData> systems,
            int maxRows,
            bool useInclusiveAsPrimary = false)
        {
            if (systems.Count == 0) return;
            var rows = new List<KeyValuePair<string, PhaseData>>(systems.Count);
            foreach (var kvp in systems) rows.Add(kvp);
            if (useInclusiveAsPrimary)
                rows.Sort(static (a, b) => b.Value.InclusiveTicks.CompareTo(a.Value.InclusiveTicks));
            else
                rows.Sort(static (a, b) => b.Value.SelfTicks.CompareTo(a.Value.SelfTicks));
            sb.AppendLine();
            sb.AppendLine(header);
            string primaryHeader = useInclusiveAsPrimary ? "TOTAL" : "SELF";
            sb.AppendLine($"{"SYSTEM",-45} {"CALLS",6} {primaryHeader,10} {"INCL",10} {"AVG",8} {"MAX",8} {"SYNC?",6}");
            sb.AppendLine(new string('─', 101));
            int shown = 0;
            for (int i = 0; i < rows.Count && shown < maxRows; i++)
            {
                var kvp = rows[i];
                var d = kvp.Value;
                long primaryTicks = useInclusiveAsPrimary ? d.InclusiveTicks : d.SelfTicks;
                double primaryMs = primaryTicks * MS_PER_SEC / Stopwatch.Frequency;
                if (primaryMs < 0.5) continue;
                double inclusiveMs = d.InclusiveTicks * MS_PER_SEC / Stopwatch.Frequency;
                double avgMs = d.CallCount > 0 ? primaryMs / d.CallCount : 0;
                double maxMs = d.MaxTicks * MS_PER_SEC / Stopwatch.Frequency;
                string flag = d.SyncPointSuspectCount > 0 ? "  [likely sync point]" : "";
                sb.AppendLine(Inv($"{kvp.Key,-45} {d.CallCount,6} {primaryMs,9:F1}ms {inclusiveMs,9:F1}ms {avgMs,7:F2}ms {maxMs,7:F1}ms {d.SyncPointSuspectCount,6}{flag}"));
                shown++;
            }
        }

        private static void AppendMemorySection(StringBuilder sb, MemorySample mem)
        {
            if (mem.BaselineJustCaptured)
            {
                AppendMemoryBaseline(sb, mem);
                AppendCounterAvailability(sb, mem);
                return;
            }

            AppendMemoryDeltas(sb, mem);
            AppendHardwareCounters(sb, mem);
            AppendRenderCounters(sb, mem);
            AppendCounterAvailability(sb, mem);
        }

        private static void AppendMemoryBaseline(StringBuilder sb, MemorySample mem)
        {
            sb.AppendLine();
            sb.AppendLine("MEMORY BASELINE");
            sb.AppendLine(new string('─', 50));
            sb.AppendLine(Inv($"  Managed (GC):     {mem.ManagedBytes / BYTES_PER_MB,8:F1} MB"));
            sb.AppendLine(Inv($"  Mono Heap:        {mem.MonoHeapBytes / BYTES_PER_MB,8:F1} MB"));
            sb.AppendLine(Inv($"  Native Alloc:     {mem.NativeAllocBytes / BYTES_PER_MB,8:F1} MB"));
            sb.AppendLine(Inv($"  Native Reserved:  {mem.NativeReservedBytes / BYTES_PER_MB,8:F1} MB"));
            if (mem.GfxUsedBytes > 0)
                sb.AppendLine(Inv($"  Gfx (GPU):        {mem.GfxUsedBytes / BYTES_PER_MB,8:F1} MB"));
            if (mem.AudioUsedBytes > 0)
                sb.AppendLine(Inv($"  Audio:            {mem.AudioUsedBytes / BYTES_PER_MB,8:F1} MB"));
            if (mem.SystemUsedBytes > 0)
                sb.AppendLine(Inv($"  System total:     {mem.SystemUsedBytes / BYTES_PER_MB,8:F1} MB"));
            if (mem.AppResidentBytes > 0)
                sb.AppendLine(Inv($"  Process RSS:      {mem.AppResidentBytes / BYTES_PER_MB,8:F1} MB  (real physical RAM)"));
        }

        private static void AppendMemoryDeltas(StringBuilder sb, MemorySample mem)
        {
            sb.AppendLine();
            sb.AppendLine(Inv($"MEMORY (delta from baseline)  |  Managed growth: {mem.ManagedGrowthMBperSec:+0.00;-0.00} MB/s"));
            sb.AppendLine(new string('─', 50));
            sb.AppendLine(Inv($"  Managed (GC):     {mem.ManagedBytes / BYTES_PER_MB,8:F1} MB  ({DeltaMB(mem.ManagedDelta)})"));
            sb.AppendLine(Inv($"  Mono Heap:        {mem.MonoHeapBytes / BYTES_PER_MB,8:F1} MB  ({DeltaMB(mem.MonoHeapDelta)})"));
            sb.AppendLine(Inv($"  Native Alloc:     {mem.NativeAllocBytes / BYTES_PER_MB,8:F1} MB  ({DeltaMB(mem.NativeAllocDelta)})"));
            sb.AppendLine(Inv($"  Native Reserved:  {mem.NativeReservedBytes / BYTES_PER_MB,8:F1} MB  ({DeltaMB(mem.NativeReservedDelta)})"));
            if (mem.GfxUsedBytes > 0)
                sb.AppendLine(Inv($"  Gfx (GPU):        {mem.GfxUsedBytes / BYTES_PER_MB,8:F1} MB  ({DeltaMB(mem.GfxUsedDelta)})"));
            if (mem.AudioUsedBytes > 0)
                sb.AppendLine(Inv($"  Audio:            {mem.AudioUsedBytes / BYTES_PER_MB,8:F1} MB  ({DeltaMB(mem.AudioUsedDelta)})"));
            if (mem.SystemUsedBytes > 0)
                sb.AppendLine(Inv($"  System total:     {mem.SystemUsedBytes / BYTES_PER_MB,8:F1} MB"));
            if (mem.AppResidentBytes > 0)
                sb.AppendLine(Inv($"  Process RSS:      {mem.AppResidentBytes / BYTES_PER_MB,8:F1} MB"));
        }

        private static void AppendHardwareCounters(StringBuilder sb, MemorySample mem)
        {
            if (mem.UsedBuffersBytes > 0 || mem.RenderTexturesBytes > 0)
                sb.AppendLine(Inv($"  GPU breakdown:    Buffers {mem.UsedBuffersBytes / BYTES_PER_MB,7:F1} MB ({mem.UsedBuffersCount} bufs),  RT {mem.RenderTexturesBytes / BYTES_PER_MB,7:F1} MB"));
            if (mem.MainThreadCpuNs > 0 || mem.RenderThreadCpuNs > 0)
                sb.AppendLine(Inv($"  CPU threads:      Main {mem.MainThreadCpuNs / 1_000_000.0,5:F2} ms,  Render {mem.RenderThreadCpuNs / 1_000_000.0,5:F2} ms  (Unity ProfilerRecorder avg)"));
            if (mem.PresentWaitNs > 0)
                sb.AppendLine(Inv($"  Present wait:     {mem.PresentWaitNs / 1_000_000.0,5:F2} ms  (CPU stalled on GPU swapchain)"));
        }

        private static void AppendRenderCounters(StringBuilder sb, MemorySample mem)
        {
            if (mem.DrawCallsCount > 0 || mem.SetPassCallsCount > 0 || mem.TrianglesCount > 0)
            {
                sb.AppendLine(Inv($"  Render counts:    DrawCalls {mem.DrawCallsCount,5},  SetPass {mem.SetPassCallsCount,4},  Shadow casters {mem.ShadowCastersCount,5}"));
                sb.AppendLine(Inv($"                    Tris {mem.TrianglesCount / 1000,6:N0}K,  Verts {mem.VerticesCount / 1000,6:N0}K"));
            }
            if (mem.GcCollectCount > 0)
                sb.AppendLine(Inv($"  GC.Collect:       {mem.GcCollectCount} collections,  total stall {mem.GcCollectTotalNs / 1_000_000.0,6:F2} ms"));
        }

        private static void AppendCounterAvailability(StringBuilder sb, MemorySample mem)
        {
            sb.AppendLine(ReportTextSections.CompactCounterStatus(
                mem.MainThreadCpuAvailable,
                mem.RenderThreadCpuAvailable,
                mem.GpuFrameTimeAvailable,
                mem.PresentWaitAvailable,
                mem.DrawCallsAvailable,
                mem.SetPassCallsAvailable,
                mem.GcCollectAvailable));
        }

        private static void AppendHealthSummary(StringBuilder sb, HealthReport h)
        {
            sb.AppendLine();
            sb.AppendLine($"HEALTH  FPS:{h.FpsLevel}  STUTTER:{h.StutterLevel}  MEM:{h.MemoryLevel}  GROWTH:{h.GrowthLevel}  →  OVERALL:{h.Overall}");
            sb.AppendLine($"BOTTLENECK  {BottleneckText(h)}  —  {h.BottleneckHint}");
            if (!string.IsNullOrEmpty(h.MemoryHint)
                && !string.Equals(h.MemoryHint, "Stable", StringComparison.Ordinal))
                sb.AppendLine($"MEMORY  {h.MemoryHint}");
        }

        private static string DeltaMB(long bytes)
        {
            double mb = bytes / BYTES_PER_MB;
            return mb >= 0 ? Inv($"+{mb:F1} MB") : Inv($"{mb:F1} MB");
        }

        private static string BottleneckText(HealthReport health)
        {
            return health.RenderCause == RenderCause.None
                ? health.Bottleneck.ToString()
                : $"{health.Bottleneck}/{health.RenderCause}";
        }

        private static string Inv(FormattableString value) => FormattableString.Invariant(value);
    }
}
