using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VanillaProfiler
{
    [StructLayout(LayoutKind.Auto)]
    internal readonly struct FrameTiming
    {
        public readonly long DeltaTicks;
        public readonly double FrameMs;
        public readonly float FrameSec;

        public FrameTiming(long deltaTicks)
        {
            DeltaTicks = deltaTicks;
            FrameMs = deltaTicks * 1000.0 / Stopwatch.Frequency;
            FrameSec = (float)(deltaTicks * 1.0 / Stopwatch.Frequency);
        }
    }

    /// <summary>
    /// Owns frame-to-frame timing and report cadence. Profiler decides what to do
    /// with the timing; this class only answers "is a report due now?".
    /// </summary>
    internal sealed class ReportScheduler
    {
        private long m_LastFrameTicks;
        private long m_LastReportTicks;
        private float m_ReportTimer;

        public bool TryAdvanceFrame(long nowTicks, float reportIntervalSec,
            out FrameTiming timing, out bool reportDue)
        {
            reportDue = false;
            timing = default;

            if (m_LastFrameTicks == 0)
            {
                m_LastFrameTicks = nowTicks;
                m_LastReportTicks = nowTicks;
                return false;
            }

            long deltaTicks = nowTicks - m_LastFrameTicks;
            m_LastFrameTicks = nowTicks;
            timing = new FrameTiming(deltaTicks);

            float interval = NormalizeInterval(reportIntervalSec);
            m_ReportTimer += timing.FrameSec;
            if (m_ReportTimer >= interval)
            {
                m_ReportTimer %= interval;
                reportDue = true;
            }
            return true;
        }

        public float ConsumeElapsedSeconds(long nowTicks, float fallbackIntervalSec)
        {
#pragma warning disable CIVIC021 // Stopwatch.Frequency is a hardware constant, always >= 1
            float elapsedSec = (float)((nowTicks - m_LastReportTicks) / (double)Stopwatch.Frequency);
#pragma warning restore CIVIC021
            m_LastReportTicks = nowTicks;
            return elapsedSec <= 0.001f ? NormalizeInterval(fallbackIntervalSec) : elapsedSec;
        }

        public void Reset()
        {
            m_LastFrameTicks = 0;
            m_LastReportTicks = Stopwatch.GetTimestamp();
            m_ReportTimer = 0f;
        }

        public void ResetReportTimer()
        {
            m_ReportTimer = 0f;
        }

        private static float NormalizeInterval(float interval)
            => interval <= 0f ? 5f : interval;
    }
}
