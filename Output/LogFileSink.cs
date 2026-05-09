using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

        // Reused across reports to avoid one List allocation per phase/system table.
        private readonly List<KeyValuePair<string, PhaseData>> m_SortBuffer = new();

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
            WriteLine(Inv($"{DateTime.Now:HH:mm:ss} {tag} {message}"));
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
            // Hold the write lock for the whole report so an off-thread WriteSystemMessage
            // (e.g. MainThreadGuard violation log) cannot interleave between report lines.
            // C# lock is re-entrant on the same thread — nested WriteLine calls below
            // re-acquire the lock cheaply without deadlocking.
            lock (m_WriteLock)
            {
                WriteLine("");
                WriteLine(Inv($"══════ Report #{reportNumber} ({DateTime.Now:HH:mm:ss}, {snapshot.WindowSeconds:F1}s) ══════"));

                if (metrics.FrameCount > 0)
                {
                    WriteLine(Inv($"Render: {snapshot.AvgFps:F1} FPS ({snapshot.MinFps:F1} min) | Frame: {snapshot.AvgFrameMs:F1}ms avg, {snapshot.MaxFrameMs:F1}ms max | Sim: {snapshot.SimTicksPerSec:F0} ticks/s"));
                    WriteLine(Inv($"Spikes: {metrics.Spikes30} below 30fps, {metrics.Spikes20} below 20fps"));
                }

                WritePhaseTable(metrics.Phases);
                WriteSystemTable("TOP MODS (by main-thread time)", metrics.ModAggregate, 10);
                WriteSystemTable("VANILLA SYSTEMS — main-thread cost (top 15)", metrics.VanillaSystems, 15);
                WriteSystemTable("MOD SYSTEMS — main-thread cost (top 15)", metrics.ModSystems, 15);
                // Patched vanilla systems are tracked in their own bucket
                // because their elapsed time blends the patching mod's prefix
                // with the vanilla original. Surfacing the table per cycle
                // means a bug-report log carries the same signal the overlay
                // shows — without it the only patched-vanilla ms data lives
                // off-disk in the snapshot.
                WriteSystemTable("PATCHED VANILLA SYSTEMS — total Update ms (mod+vanilla split unknown)",
                    metrics.PatchedVanillaSystems, 15);
                WriteMemorySection(memory);
                WriteHealthSummary(health);
            }
        }

        public void WriteLine(string msg)
        {
            lock (m_WriteLock)
            {
                if (m_Shutdown) return;
                try
                {
                    if (m_Writer == null && DateTime.UtcNow < m_NextOpenRetryUtc)
                        return;
                    if (m_Writer == null && !TryOpenWriter())
                        return;
                    m_Writer?.WriteLine(msg);
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
        }

        // ---------- Section writers ----------

        private void WritePhaseTable(IReadOnlyDictionary<string, PhaseData> phases)
        {
            if (phases.Count == 0) return;
            m_SortBuffer.Clear();
            foreach (var kvp in phases) m_SortBuffer.Add(kvp);
            m_SortBuffer.Sort(static (a, b) => b.Value.TotalTicks.CompareTo(a.Value.TotalTicks));
            WriteLine("");
            WriteLine($"{"PHASE",-35} {"CALLS",6} {"TOTAL",10} {"AVG",8} {"MAX",8} {"SYNC?",6}");
            WriteLine(new string('─', 79));
            for (int i = 0; i < m_SortBuffer.Count; i++)
            {
                var kvp = m_SortBuffer[i];
                var d = kvp.Value;
                double totalMs = d.TotalTicks * MS_PER_SEC / Stopwatch.Frequency;
                double avgMs = d.CallCount > 0 ? totalMs / d.CallCount : 0;
                double maxMs = d.MaxTicks * MS_PER_SEC / Stopwatch.Frequency;
                WriteLine(Inv($"{kvp.Key,-35} {d.CallCount,6} {totalMs,9:F1}ms {avgMs,7:F2}ms {maxMs,7:F1}ms {d.SyncPointSuspectCount,6}"));
            }
        }

        private void WriteSystemTable(string header, IReadOnlyDictionary<string, PhaseData> systems, int maxRows)
        {
            if (systems.Count == 0) return;
            m_SortBuffer.Clear();
            foreach (var kvp in systems) m_SortBuffer.Add(kvp);
            m_SortBuffer.Sort(static (a, b) => b.Value.TotalTicks.CompareTo(a.Value.TotalTicks));
            WriteLine("");
            WriteLine(header);
            // SYNC? column = number of individual Update() calls > SyncPointThresholdMs.
            // A non-zero value means the system did real main-thread work (sync point,
            // structural change, ECB playback, synchronous foreach), not just scheduling.
            WriteLine($"{"SYSTEM",-45} {"CALLS",6} {"TOTAL",10} {"AVG",8} {"MAX",8} {"SYNC?",6}");
            WriteLine(new string('─', 89));
            int shown = 0;
            for (int i = 0; i < m_SortBuffer.Count && shown < maxRows; i++)
            {
                var kvp = m_SortBuffer[i];
                var d = kvp.Value;
                double totalMs = d.TotalTicks * MS_PER_SEC / Stopwatch.Frequency;
                if (totalMs < 0.5) continue;
                double avgMs = d.CallCount > 0 ? totalMs / d.CallCount : 0;
                double maxMs = d.MaxTicks * MS_PER_SEC / Stopwatch.Frequency;
                string flag = d.SyncPointSuspectCount > 0 ? "  [likely sync point]" : "";
                WriteLine(Inv($"{kvp.Key,-45} {d.CallCount,6} {totalMs,9:F1}ms {avgMs,7:F2}ms {maxMs,7:F1}ms {d.SyncPointSuspectCount,6}{flag}"));
                shown++;
            }
        }

        private void WriteMemorySection(MemorySample mem)
        {
            if (mem.BaselineJustCaptured)
            {
                WriteMemoryBaseline(mem);
                WriteCounterAvailability(mem);
                return;
            }

            WriteMemoryDeltas(mem);
            WriteHardwareCounters(mem);
            WriteRenderCounters(mem);
            WriteCounterAvailability(mem);
        }

        private void WriteMemoryBaseline(MemorySample mem)
        {
            WriteLine("");
            WriteLine("MEMORY BASELINE");
            WriteLine(new string('─', 50));
            WriteLine(Inv($"  Managed (GC):     {mem.ManagedBytes / BYTES_PER_MB,8:F1} MB"));
            WriteLine(Inv($"  Mono Heap:        {mem.MonoHeapBytes / BYTES_PER_MB,8:F1} MB"));
            WriteLine(Inv($"  Native Alloc:     {mem.NativeAllocBytes / BYTES_PER_MB,8:F1} MB"));
            WriteLine(Inv($"  Native Reserved:  {mem.NativeReservedBytes / BYTES_PER_MB,8:F1} MB"));
            if (mem.GfxUsedBytes > 0)
                WriteLine(Inv($"  Gfx (GPU):        {mem.GfxUsedBytes / BYTES_PER_MB,8:F1} MB"));
            if (mem.AudioUsedBytes > 0)
                WriteLine(Inv($"  Audio:            {mem.AudioUsedBytes / BYTES_PER_MB,8:F1} MB"));
            if (mem.SystemUsedBytes > 0)
                WriteLine(Inv($"  System total:     {mem.SystemUsedBytes / BYTES_PER_MB,8:F1} MB"));
            if (mem.AppResidentBytes > 0)
                WriteLine(Inv($"  Process RSS:      {mem.AppResidentBytes / BYTES_PER_MB,8:F1} MB  (real physical RAM)"));
        }

        private void WriteMemoryDeltas(MemorySample mem)
        {
            WriteLine("");
            WriteLine(Inv($"MEMORY (delta from baseline)  |  Managed growth: {mem.ManagedGrowthMBperSec:+0.00;-0.00} MB/s"));
            WriteLine(new string('─', 50));
            WriteLine(Inv($"  Managed (GC):     {mem.ManagedBytes / BYTES_PER_MB,8:F1} MB  ({DeltaMB(mem.ManagedDelta)})"));
            WriteLine(Inv($"  Mono Heap:        {mem.MonoHeapBytes / BYTES_PER_MB,8:F1} MB  ({DeltaMB(mem.MonoHeapDelta)})"));
            WriteLine(Inv($"  Native Alloc:     {mem.NativeAllocBytes / BYTES_PER_MB,8:F1} MB  ({DeltaMB(mem.NativeAllocDelta)})"));
            WriteLine(Inv($"  Native Reserved:  {mem.NativeReservedBytes / BYTES_PER_MB,8:F1} MB  ({DeltaMB(mem.NativeReservedDelta)})"));
            if (mem.GfxUsedBytes > 0)
                WriteLine(Inv($"  Gfx (GPU):        {mem.GfxUsedBytes / BYTES_PER_MB,8:F1} MB  ({DeltaMB(mem.GfxUsedDelta)})"));
            if (mem.AudioUsedBytes > 0)
                WriteLine(Inv($"  Audio:            {mem.AudioUsedBytes / BYTES_PER_MB,8:F1} MB  ({DeltaMB(mem.AudioUsedDelta)})"));
            if (mem.SystemUsedBytes > 0)
                WriteLine(Inv($"  System total:     {mem.SystemUsedBytes / BYTES_PER_MB,8:F1} MB"));
            if (mem.AppResidentBytes > 0)
                WriteLine(Inv($"  Process RSS:      {mem.AppResidentBytes / BYTES_PER_MB,8:F1} MB"));
        }

        private void WriteHardwareCounters(MemorySample mem)
        {
            if (mem.UsedBuffersBytes > 0 || mem.RenderTexturesBytes > 0)
                WriteLine(Inv($"  GPU breakdown:    Buffers {mem.UsedBuffersBytes / BYTES_PER_MB,7:F1} MB ({mem.UsedBuffersCount} bufs),  RT {mem.RenderTexturesBytes / BYTES_PER_MB,7:F1} MB"));
            if (mem.MainThreadCpuNs > 0 || mem.RenderThreadCpuNs > 0)
                WriteLine(Inv($"  CPU threads:      Main {mem.MainThreadCpuNs / 1_000_000.0,5:F2} ms,  Render {mem.RenderThreadCpuNs / 1_000_000.0,5:F2} ms  (Unity ProfilerRecorder avg)"));
            if (mem.PresentWaitNs > 0)
                WriteLine(Inv($"  Present wait:     {mem.PresentWaitNs / 1_000_000.0,5:F2} ms  (CPU stalled on GPU swapchain)"));
        }

        private void WriteRenderCounters(MemorySample mem)
        {
            if (mem.DrawCallsCount > 0 || mem.SetPassCallsCount > 0 || mem.TrianglesCount > 0)
            {
                WriteLine(Inv($"  Render counts:    DrawCalls {mem.DrawCallsCount,5},  SetPass {mem.SetPassCallsCount,4},  Shadow casters {mem.ShadowCastersCount,5}"));
                WriteLine(Inv($"                    Tris {mem.TrianglesCount / 1000,6:N0}K,  Verts {mem.VerticesCount / 1000,6:N0}K"));
            }
            if (mem.GcCollectCount > 0)
                WriteLine(Inv($"  GC.Collect:       {mem.GcCollectCount} collections,  total stall {mem.GcCollectTotalNs / 1_000_000.0,6:F2} ms"));
        }

        private void WriteCounterAvailability(MemorySample mem)
        {
            WriteLine(ReportTextSections.CompactCounterStatus(
                mem.MainThreadCpuAvailable,
                mem.RenderThreadCpuAvailable,
                mem.GpuFrameTimeAvailable,
                mem.PresentWaitAvailable,
                mem.DrawCallsAvailable,
                mem.SetPassCallsAvailable,
                mem.GcCollectAvailable));
        }

        private void WriteHealthSummary(HealthReport h)
        {
            WriteLine("");
            WriteLine($"HEALTH  FPS:{h.FpsLevel}  STUTTER:{h.StutterLevel}  MEM:{h.MemoryLevel}  GROWTH:{h.GrowthLevel}  →  OVERALL:{h.Overall}");
            WriteLine($"BOTTLENECK  {h.Bottleneck}  —  {h.BottleneckHint}");
            if (!string.IsNullOrEmpty(h.MemoryHint)
                && !string.Equals(h.MemoryHint, "Stable", StringComparison.Ordinal))
                WriteLine($"MEMORY  {h.MemoryHint}");
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
            var writer = new StreamWriter(stream) { AutoFlush = true };
            try
            {
                WriteSessionHeader(writer);
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
            writer.WriteLine("  These reflect main-thread cost only — scheduling overhead, sync points,");
            writer.WriteLine("  structural changes, ECB playback, and any work done synchronously on the");
            writer.WriteLine("  main thread. Job execution on worker threads is NOT captured here, because");
            writer.WriteLine("  Burst-compiled jobs run outside of SystemBase.Update(). For accurate");
            writer.WriteLine("  per-job profiling attach Unity Profiler to the running game.");
            writer.WriteLine();
        }

        private static string Inv(FormattableString value) => FormattableString.Invariant(value);

        public static string GetLogDirectory(string persistentDataPath)
            => Path.Combine(persistentDataPath, LOG_DIR_NAME);

        public static string GetLogPath(string persistentDataPath)
            => Path.Combine(GetLogDirectory(persistentDataPath), LOG_FILENAME);
    }
}
