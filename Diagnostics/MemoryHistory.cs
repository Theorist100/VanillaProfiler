using System.Collections.Generic;
using System.Runtime.InteropServices;
using System;

namespace VanillaProfiler.Diagnostics
{
    /// <summary>
    /// Tracks managed memory across reports and detects sustained growth (a likely leak).
    /// Uses a rolling window so a one-off allocation spike does not trip the detector.
    /// </summary>
    public sealed class MemoryHistory
    {
        private const int CAPACITY = 12;          // 12 configured report windows
        private const int LEAK_WINDOW = 5;        // last 5 reports
        private const double LEAK_THRESHOLD_MB_PER_SEC = 1.0;
        private const double BYTES_PER_MB = 1024.0 * 1024.0;
        private readonly List<Sample> m_Samples = new(CAPACITY);
        private readonly Func<ProfilerSettingsSnapshot> m_Settings;
        private int m_SuppressedReports;
        private double m_TotalSeconds;

        public bool LeakSuspected { get; private set; }
        public double GrowthMBperReport { get; private set; }
        public double GrowthMBperSec { get; private set; }
        public double TotalGrownMB { get; private set; }
        public int WindowSeconds { get; private set; }

        public MemoryHistory(Func<ProfilerSettingsSnapshot> settings)
        {
            m_Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public void Record(long managedBytes, float elapsedSec)
        {
            if (IsLongPause(elapsedSec))
            {
                Reset();
                return;
            }

            double sampleSeconds = NormalizeElapsed(elapsedSec);
            if (m_SuppressedReports > 0)
            {
                m_SuppressedReports--;
                m_TotalSeconds += sampleSeconds;
                ClearComputed();
                return;
            }

            if (m_Samples.Count >= CAPACITY)
                m_Samples.RemoveAt(0);
            m_TotalSeconds += sampleSeconds;
            m_Samples.Add(new Sample(managedBytes, m_TotalSeconds));

            Recompute();
        }

        public void Reset()
        {
            m_Samples.Clear();
            m_SuppressedReports = 0;
            m_TotalSeconds = 0;
            ClearComputed();
        }

        public void OnSessionBoundary()
        {
            Reset();
        }

        public void SuppressNextReports(int count)
        {
            m_Samples.Clear();
            m_SuppressedReports = count < 0 ? 0 : count;
            m_TotalSeconds = 0;
            ClearComputed();
        }

        public MemoryHistorySnapshot ToSnapshot()
        {
            if (m_Samples.Count == 0)
                return MemoryHistorySnapshot.Empty;

            var points = new MemorySamplePoint[m_Samples.Count];
            for (int i = 0; i < m_Samples.Count; i++)
            {
                var sample = m_Samples[i];
                points[i] = new MemorySamplePoint(sample.Bytes, sample.Seconds);
            }

            return new MemoryHistorySnapshot(WindowSeconds, GrowthMBperSec, LeakSuspected, points);
        }

        private void Recompute()
        {
            int n = m_Samples.Count;
            if (n < 2)
            {
                LeakSuspected = false;
                GrowthMBperReport = 0;
                GrowthMBperSec = 0;
                TotalGrownMB = 0;
                WindowSeconds = 0;
                return;
            }

            int windowSize = n < LEAK_WINDOW ? n : LEAK_WINDOW;
            int skip = n - windowSize;

            var deltaRatesMBps = new List<double>(windowSize - 1);
            var firstInWindow = m_Samples[skip];
            var lastInWindow = m_Samples[n - 1];
            for (int i = skip + 1; i < n; i++)
            {
                var prev = m_Samples[i - 1];
                var current = m_Samples[i];
                double dt = current.Seconds - prev.Seconds;
                if (dt <= 0) continue;
                deltaRatesMBps.Add((current.Bytes - prev.Bytes) / BYTES_PER_MB / dt);
            }

            double medianRate = Median(deltaRatesMBps);
            double totalMB = (lastInWindow.Bytes - firstInWindow.Bytes) / BYTES_PER_MB;
            double windowSeconds = lastInWindow.Seconds - firstInWindow.Seconds;

            GrowthMBperReport = medianRate * ReportIntervalS;
            GrowthMBperSec = medianRate;
            TotalGrownMB = totalMB;
            WindowSeconds = (int)windowSeconds;
            // Need full window before raising the alarm — short windows give false positives
            double minNetGrowthMB = LEAK_THRESHOLD_MB_PER_SEC * windowSeconds;
            LeakSuspected = windowSize >= LEAK_WINDOW
                && medianRate >= LEAK_THRESHOLD_MB_PER_SEC
                && totalMB >= minNetGrowthMB;
        }

        private void ClearComputed()
        {
            LeakSuspected = false;
            GrowthMBperReport = 0;
            GrowthMBperSec = 0;
            TotalGrownMB = 0;
            WindowSeconds = 0;
        }

        private double NormalizeElapsed(float elapsedSec)
        {
            double fallback = ReportIntervalS;
            if (elapsedSec <= 0 || float.IsNaN(elapsedSec) || float.IsInfinity(elapsedSec))
                return fallback;
            return elapsedSec;
        }

        private bool IsLongPause(float elapsedSec)
        {
            if (elapsedSec <= 0 || float.IsNaN(elapsedSec) || float.IsInfinity(elapsedSec))
                return false;
            return elapsedSec > ReportIntervalS * 2.0f;
        }

        private float ReportIntervalS => m_Settings().Settings.ReportIntervalSec;

        private static double Median(List<double> values)
        {
            if (values.Count == 0) return 0;
            values.Sort();
            int mid = values.Count / 2;
            return (values.Count % 2 == 0)
                ? (values[mid - 1] + values[mid]) * 0.5
                : values[mid];
        }

        [StructLayout(LayoutKind.Auto)]
        private readonly struct Sample
        {
            public readonly long Bytes;
            public readonly double Seconds;

            public Sample(long bytes, double seconds)
            {
                Bytes = bytes;
                Seconds = seconds;
            }
        }
    }
}
