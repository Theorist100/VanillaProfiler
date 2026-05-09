using System;
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
            string text = LogReportFormatter.BuildReportText(reportNumber, snapshot, health, metrics, memory);
            WriteReportText(text);
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
