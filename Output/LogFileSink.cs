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

        private const double MS_PER_SEC = 1000.0;
        private const double BYTES_PER_MB = 1024.0 * 1024.0;

        private readonly string m_LogDir;
        private StreamWriter m_Writer;
        private DateTime m_LastIoFailureLogUtc = DateTime.MinValue;
        private DateTime m_NextOpenRetryUtc = DateTime.MinValue;

        public LogFileSink(string logDir)
        {
            m_LogDir = logDir;
        }

        public void Initialize()
        {
            _ = TryOpenWriter();
        }

        public void WriteSystemMessage(SystemLogLevel level, string message)
        {
            string tag = level switch
            {
                SystemLogLevel.Info => "[INFO]",
                SystemLogLevel.Warn => "[WARN]",
                SystemLogLevel.Error => "[ERROR]",
                _ => "[INFO]",
            };
            WriteLine(Inv($"{DateTime.Now:HH:mm:ss} {tag} {message}"));
        }

        public void Shutdown()
        {
            try { CloseWriter(); }
            catch { }
        }

        public void WriteReport(int reportNumber, OverlaySnapshot snapshot, HealthReport health,
            MetricsSample metrics, MemorySample memory)
        {
            WriteLine("");
            WriteLine(Inv($"══════ Report #{reportNumber} ({DateTime.Now:HH:mm:ss}, {snapshot?.WindowSeconds:F1}s) ══════"));

            if (snapshot != null && metrics.FrameCount > 0)
            {
                WriteLine(Inv($"Render: {snapshot.AvgFps:F1} FPS ({snapshot.MinFps:F1} min) | Frame: {snapshot.AvgFrameMs:F1}ms avg, {snapshot.MaxFrameMs:F1}ms max | Sim: {snapshot.SimTicksPerSec:F0} ticks/s"));
                WriteLine(Inv($"Spikes: {metrics.Spikes30} below 30fps, {metrics.Spikes20} below 20fps"));
            }

            WritePhaseTable(metrics.Phases);
            WriteSystemTable("TOP MODS (by total CPU time)", metrics.ModAggregate, 10);
            WriteSystemTable("VANILLA SYSTEMS (top 15)", metrics.VanillaSystems, 15);
            WriteSystemTable("MOD SYSTEMS (top 15)", metrics.ModSystems, 15);
            WriteMemorySection(memory);
            WriteHealthSummary(health);
        }

        public void WriteLine(string msg)
        {
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
                CloseWriter();
                m_NextOpenRetryUtc = DateTime.UtcNow.AddSeconds(5);
                WarnIoFailure("write", ex);
            }
        }

        // ---------- Section writers ----------

        private void WritePhaseTable(Dictionary<string, PhaseData> phases)
        {
            if (phases.Count == 0) return;
            var sorted = new List<KeyValuePair<string, PhaseData>>(phases);
            sorted.Sort((a, b) => b.Value.TotalTicks.CompareTo(a.Value.TotalTicks));
            WriteLine("");
            WriteLine($"{"PHASE",-35} {"CALLS",6} {"TOTAL",10} {"AVG",8} {"MAX",8}");
            WriteLine(new string('─', 72));
            foreach (var kvp in sorted)
            {
                var d = kvp.Value;
                double totalMs = d.TotalTicks * MS_PER_SEC / Stopwatch.Frequency;
                double avgMs = d.CallCount > 0 ? totalMs / d.CallCount : 0;
                double maxMs = d.MaxTicks * MS_PER_SEC / Stopwatch.Frequency;
                WriteLine(Inv($"{kvp.Key,-35} {d.CallCount,6} {totalMs,9:F1}ms {avgMs,7:F2}ms {maxMs,7:F1}ms"));
            }
        }

        private void WriteSystemTable(string header, Dictionary<string, PhaseData> systems, int maxRows)
        {
            if (systems.Count == 0) return;
            var sorted = new List<KeyValuePair<string, PhaseData>>(systems);
            sorted.Sort((a, b) => b.Value.TotalTicks.CompareTo(a.Value.TotalTicks));
            WriteLine("");
            WriteLine(header);
            WriteLine($"{"SYSTEM",-45} {"CALLS",6} {"TOTAL",10} {"AVG",8} {"MAX",8}");
            WriteLine(new string('─', 82));
            int shown = 0;
            foreach (var kvp in sorted)
            {
                if (shown >= maxRows) break;
                var d = kvp.Value;
                double totalMs = d.TotalTicks * MS_PER_SEC / Stopwatch.Frequency;
                if (totalMs < 0.5) continue;
                double avgMs = d.CallCount > 0 ? totalMs / d.CallCount : 0;
                double maxMs = d.MaxTicks * MS_PER_SEC / Stopwatch.Frequency;
                WriteLine(Inv($"{kvp.Key,-45} {d.CallCount,6} {totalMs,9:F1}ms {avgMs,7:F2}ms {maxMs,7:F1}ms"));
                shown++;
            }
        }

        private void WriteMemorySection(MemorySample mem)
        {
            if (mem.BaselineJustCaptured)
            {
                WriteLine("");
                WriteLine("MEMORY BASELINE");
                WriteLine(new string('─', 50));
                WriteLine(Inv($"  Managed (GC):     {mem.ManagedBytes / BYTES_PER_MB,8:F1} MB"));
                WriteLine(Inv($"  Mono Heap:        {mem.MonoHeapBytes / BYTES_PER_MB,8:F1} MB"));
                WriteLine(Inv($"  Native Alloc:     {mem.NativeAllocBytes / BYTES_PER_MB,8:F1} MB"));
                WriteLine(Inv($"  Native Reserved:  {mem.NativeReservedBytes / BYTES_PER_MB,8:F1} MB"));
                return;
            }

            WriteLine("");
            WriteLine(Inv($"MEMORY (delta from baseline)  |  Managed growth: {mem.ManagedGrowthMBperSec:+0.00;-0.00} MB/s"));
            WriteLine(new string('─', 50));
            WriteLine(Inv($"  Managed (GC):     {mem.ManagedBytes / BYTES_PER_MB,8:F1} MB  ({DeltaMB(mem.ManagedDelta)})"));
            WriteLine(Inv($"  Mono Heap:        {mem.MonoHeapBytes / BYTES_PER_MB,8:F1} MB  ({DeltaMB(mem.MonoHeapDelta)})"));
            WriteLine(Inv($"  Native Alloc:     {mem.NativeAllocBytes / BYTES_PER_MB,8:F1} MB  ({DeltaMB(mem.NativeAllocDelta)})"));
            WriteLine(Inv($"  Native Reserved:  {mem.NativeReservedBytes / BYTES_PER_MB,8:F1} MB  ({DeltaMB(mem.NativeReservedDelta)})"));
        }

        private void WriteHealthSummary(HealthReport h)
        {
            if (h == null) return;
            WriteLine("");
            WriteLine($"HEALTH  FPS:{h.FpsLevel}  STUTTER:{h.StutterLevel}  MEM:{h.MemoryLevel}  GROWTH:{h.GrowthLevel}  →  OVERALL:{h.Overall}");
            WriteLine($"BOTTLENECK  {h.Bottleneck}  —  {h.BottleneckHint}");
            if (!string.IsNullOrEmpty(h.MemoryHint) && h.MemoryHint != "Stable")
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
            m_Writer?.Dispose();
            m_Writer = null;
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
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var writer = new StreamWriter(stream) { AutoFlush = true };
            writer.WriteLine();
            writer.WriteLine(Inv($"=== Vanilla Profiler session === {DateTime.Now:yyyy-MM-dd HH:mm:ss}"));
            writer.WriteLine(Inv($"Report interval: {SettingsStore.Current.ReportIntervalSec}s"));
            writer.WriteLine();
            return writer;
        }

        private static string Inv(FormattableString value) => FormattableString.Invariant(value);
    }
}
