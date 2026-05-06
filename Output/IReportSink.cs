using VanillaProfiler.Aggregation;
using VanillaProfiler.Diagnostics;

namespace VanillaProfiler.Output
{
    public enum SystemLogLevel
    {
        Info,
        Warn,
        Error,
    }

    /// <summary>
    /// Output destination for profiler data. Adding a new format (CSV, JSON, network)
    /// means implementing this interface — the rest of the pipeline doesn't change (OCP).
    /// Sinks are free to be no-ops if their target isn't ready (e.g. file open failed).
    /// </summary>
    public interface IReportSink
    {
        void Initialize();

        /// <summary>
        /// Free-form system message (mod lifecycle, errors, warnings) — interleaved with reports
        /// in the same chronological log so support files show the full picture.
        /// </summary>
        void WriteSystemMessage(SystemLogLevel level, string message);

        /// <summary>Called once per report cycle. Sink decides what to write.</summary>
        void WriteReport(int reportNumber, OverlaySnapshot snapshot, HealthReport health,
            MetricsSample metrics, MemorySample memory);

        void Shutdown();
    }
}
