using System;
using VanillaProfiler.Aggregation;
using VanillaProfiler.Diagnostics;

namespace VanillaProfiler.Output
{
    /// <summary>
    /// Cold-path fan-out to report sinks. Keeps Profiler focused on collection
    /// cadence while all sink exception handling stays in one place.
    /// </summary>
    public sealed class ReportDispatcher
    {
        private readonly IReportSink[] m_Sinks;
        private bool m_Shutdown;

        public ReportDispatcher(IReportSink[] sinks)
        {
            m_Sinks = sinks ?? Array.Empty<IReportSink>();
        }

        public void Initialize()
        {
            m_Shutdown = false;
            foreach (var sink in m_Sinks) sink.Initialize();
        }

        public void Shutdown()
        {
            m_Shutdown = true;
            foreach (var sink in m_Sinks) sink.Shutdown();
        }

        public void WriteReport(int reportNumber, OverlaySnapshot snapshot, HealthReport health,
            MetricsSample metrics, MemorySample memory)
        {
            if (m_Shutdown) return;
            foreach (var sink in m_Sinks)
            {
                try { sink.WriteReport(reportNumber, snapshot, health, metrics, memory); }
                catch (Exception ex)
                {
                    VanillaProfilerMod.Log?.Warn($"Sink {sink?.GetType().Name} WriteReport failed: {ex}");
                }
            }
        }

        public void WriteSystem(SystemLogLevel level, string message)
        {
            if (m_Shutdown) return;
            foreach (var sink in m_Sinks)
            {
                try { sink.WriteSystemMessage(level, message); }
                catch (Exception ex)
                {
                    VanillaProfilerMod.Log?.Warn($"VanillaProfiler sink system-write failed: {ex}");
                }
            }
        }
    }
}
