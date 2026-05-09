using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using VanillaProfiler.Aggregation;
using VanillaProfiler.Diagnostics;

namespace VanillaProfiler.Output
{
    /// <summary>
    /// Single human-readable log file (Logs/VanillaProfiler.log) carrying both
    /// system-level messages (init / warnings / errors / dispose) and periodic
    /// performance reports, interleaved chronologically. One file = one
    /// attachment in player bug reports, with cause-and-effect visible inline.
    /// </summary>
    public sealed class LogFileSink : IReportSink
    {
        public const string LOG_FILENAME = "VanillaProfiler.log";
        public const string LOG_DIR_NAME = "Logs";

        private const double MS_PER_SEC = 1000.0;
        private const double BYTES_PER_MB = 1024.0 * 1024.0;

        private readonly string m_LogDir;
        private StreamWriter? m_Writer;
        private DateTime m_LastIoFailureLogUtc = DateTime.MinValue;
        private DateTime m_NextOpenRetryUtc = DateTime.MinValue;
        private bool m_Shutdown;

        // Serializes file IO. The Profiler's hot path is main-thread-only, but ModLog
        // dispatches every Info/Warn/Error through here, and MainThreadGuard.AssertMainThread
        // itself logs from off-thread when a contract violation is detected. Without a lock
        // those off-thread writes can interleave bytes with an in-progress report and tear
        // the log file. Holding the lock around CloseWriter/TryOpenWriter is also necessary
        // because the writer field is published across calls; a parallel writer could
        // observe a half-disposed StreamWriter and crash with ObjectDisposedException.
        private readonly object m_WriteLock = new();

        // Truncate the log on the first successful open of the session, then append
        // for the rest of the run. Without this, every session adds tens of MB to a
        // file that nobody trims and the log balloons over time. Subsequent reopens
        // after an IO failure must NOT truncate — that would erase the current run.
        private bool m_TruncateOnNextOpen = true;

        public LogFileSink(string logDir)
        {
            m_LogDir = logDir;
        }

        public void Initialize()
        {
            lock (m_WriteLock)
            {
                m_Shutdown = false;
                _ = TryOpenWriter();
            }
        }

        public void WriteSystemMessage(SystemLogLevel level, string message)
        {
            string tag = level switch
            {
                SystemLogLevel.Info => "[INFO]",
                SystemLogLevel.Warn => "[WARN]",
                SystemLogLevel.Error => "[ERROR]",
                SystemLogLevel.Unknown => "[INFO]",
                _ => "[INFO]",
            };
            WriteSystemLine(Inv($"{DateTime.Now:HH:mm:ss} {tag} {message}"));
        }

        public void Shutdown()
        {
            lock (m_WriteLock)
            {
                m_Shutdown = true;
                try { CloseWriter(); }
                catch { }
            }
        }

        public void WriteReport(int reportNumber, OverlaySnapshot snapshot, HealthReport health,
            MetricsSample metrics, MemorySample memory)
        {
            string text = BuildReportText(reportNumber, snapshot, health, metrics, memory);
            WriteReportText(text);
        }

        private string BuildReportText(int reportNumber, OverlaySnapshot snapshot, HealthReport health,
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

            AppendPhaseTable(sb, metrics.Phases);
            AppendSystemTable(sb, "TOP MODS (self main-thread cost)", metrics.ModAggregate, 10);
            AppendSystemTable(sb, "VANILLA SYSTEMS — self main-thread cost (top 15)", metrics.VanillaSystems, 15);
            AppendSystemTable(sb, "MOD SYSTEMS — self main-thread cost (top 15)", metrics.ModSystems, 15);
            // Patched vanilla systems are tracked in their own bucket because their
            // elapsed time blends the patching mod's prefix with the vanilla original.
            // Surfacing the table per cycle means a bug-report log carries the same
            // signal the overlay shows.
            AppendSystemTable(sb, "PATCHED VANILLA SYSTEMS — total Update ms (mod+vanilla split unknown)",
                metrics.PatchedVanillaSystems, 15, useInclusiveAsPrimary: true);
            AppendMemorySection(sb, memory);
            AppendHealthSummary(sb, health);
            return sb.ToString();
        }

        private void WriteReportText(string text)
        {
            lock (m_WriteLock)
            {
                WriteText(text, flush: true);
            }
        }

        private void WriteSystemLine(string msg)
        {
            lock (m_WriteLock)
            {
                WriteText(msg + Environment.NewLine, flush: false);
            }
        }

        private void WriteText(string text, bool flush)
        {
            if (m_Shutdown) return;
            try
            {
                if (m_Writer == null && DateTime.UtcNow < m_NextOpenRetryUtc)
                    return;
                if (m_Writer == null && !TryOpenWriter())
                    return;
                if (m_Writer == null) return;
                m_Writer.Write(text);
                if (flush)
                    m_Writer.Flush();
            }
            catch (Exception ex)
            {
                // CloseWriter calls StreamWriter.Dispose which itself flushes buffered
                // bytes and can throw IOException on disk full. The original write
                // failure is more interesting than a chained close failure, so guard
                // the close and force the writer field clear regardless. Mirrors
                // Shutdown's pattern.
                try { CloseWriter(); }
                catch { /* secondary IO failure — primary one is reported below */ }
                m_Writer = null;
                m_NextOpenRetryUtc = DateTime.UtcNow.AddSeconds(5);
                WarnIoFailure("write", ex);
            }
        }

        // ---------- Section appenders ----------

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
            // SYNC? column = number of individual Update() calls > SyncPointThresholdMs.
            // A non-zero value means the system did real main-thread work (sync point,
            // structural change, ECB playback, synchronous foreach), not just scheduling.
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

        private bool TryOpenWriter()
        {
            try
            {
                CloseWriter();
                m_Writer = OpenWriter();
                m_NextOpenRetryUtc = DateTime.MinValue;
                return true;
            }
            catch (Exception ex)
            {
                m_NextOpenRetryUtc = DateTime.UtcNow.AddSeconds(5);
                WarnIoFailure("open", ex);
                return false;
            }
        }

        private void CloseWriter()
        {
            try
            {
                m_Writer?.Dispose();
            }
            finally
            {
                m_Writer = null;
            }
        }

        private void WarnIoFailure(string operation, Exception ex)
        {
            var now = DateTime.UtcNow;
            if ((now - m_LastIoFailureLogUtc).TotalSeconds < 30) return;
            m_LastIoFailureLogUtc = now;
            VanillaProfilerMod.Log?.Warn($"LogFileSink {operation} failed: {ex}");
        }

        private StreamWriter OpenWriter()
        {
            Directory.CreateDirectory(m_LogDir);
            string path = Path.Combine(m_LogDir, LOG_FILENAME);
            var mode = m_TruncateOnNextOpen ? FileMode.Create : FileMode.Append;
            var stream = new FileStream(path, mode, FileAccess.Write, FileShare.ReadWrite);
            try
            {
                var writer = CreateInitializedWriter(stream);
                m_TruncateOnNextOpen = false;
                return writer;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private static StreamWriter CreateInitializedWriter(Stream stream)
        {
            var writer = new StreamWriter(stream) { AutoFlush = false };
            try
            {
                WriteSessionHeader(writer);
                writer.Flush();
                return writer;
            }
            catch
            {
                writer.Dispose();
                throw;
            }
        }

        private static void WriteSessionHeader(TextWriter writer)
        {
            writer.WriteLine();
            writer.WriteLine(Inv($"=== Vanilla Profiler session === {DateTime.Now:yyyy-MM-dd HH:mm:ss}"));
            writer.WriteLine(Inv($"Report interval: {SettingsStore.Snapshot.Settings.ReportIntervalSec}s"));
            writer.WriteLine();
            writer.WriteLine("NOTE on per-system numbers below:");
            writer.WriteLine("  SELF columns reflect exclusive main-thread cost — scheduling overhead, sync points,");
            writer.WriteLine("  structural changes, ECB playback, and any work done synchronously on the");
            writer.WriteLine("  main thread, with nested SystemBase.Update calls subtracted. INCL keeps");
            writer.WriteLine("  total elapsed Update time when that context is useful.");
            writer.WriteLine("  Job execution on worker threads is NOT captured here, because");
            writer.WriteLine("  Burst-compiled jobs run outside of SystemBase.Update(). For accurate");
            writer.WriteLine("  per-job profiling attach Unity Profiler to the running game.");
            writer.WriteLine();
        }

        private static string Inv(FormattableString value) => FormattableString.Invariant(value);

        private static string BottleneckText(HealthReport health)
        {
            return health.RenderCause == RenderCause.None
                ? health.Bottleneck.ToString()
                : $"{health.Bottleneck}/{health.RenderCause}";
        }

        public static string GetLogDirectory(string persistentDataPath)
            => Path.Combine(persistentDataPath, LOG_DIR_NAME);

        public static string GetLogPath(string persistentDataPath)
            => Path.Combine(GetLogDirectory(persistentDataPath), LOG_FILENAME);
    }
}
