using System;
using System.IO;
using UnityEngine;

namespace VanillaProfiler.Diagnostics
{
    /// <summary>
    /// Captures a screenshot when a frame exceeds the spike threshold.
    /// Throttled so a long stutter sequence does not flood the disk.
    /// Output: persistentDataPath/Logs/spikes/spike_yyyyMMdd_HHmmss.png
    /// </summary>
    public sealed class SpikeScreenshot
    {
        private const float COOLDOWN_S = 30.0f;       // at most one capture per 30 seconds

        public bool Enabled
        {
            get => SettingsStore.Snapshot.Settings.SpikeScreenshots;
            set => SettingsStore.Update(settings => settings.With(spikeScreenshots: value));
        }

        private float m_LastCaptureRealtime = float.NegativeInfinity;
        private int m_TotalCaptured;
        private int m_SessionSerial;
        private string? m_OutputDir;
        private bool m_DirChecked;

        public int TotalCaptured => m_TotalCaptured;

        public void OnFrame(double frameMs)
        {
            OnFrame(frameMs, SettingsStore.Snapshot);
        }

        public void OnFrame(double frameMs, ProfilerSettingsSnapshot settings)
        {
            if (!settings.Settings.SpikeScreenshots || frameMs < settings.Settings.SpikeThresholdMs) return;

            float now = Time.realtimeSinceStartup;
            if (now - m_LastCaptureRealtime < COOLDOWN_S) return;

            try
            {
                EnsureDir();
                if (m_OutputDir == null)
                {
                    m_LastCaptureRealtime = now;
                    return;
                }

                int captureNumber = m_TotalCaptured + 1;
                string fileName = $"spike_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{m_SessionSerial:00}_{captureNumber:000}_{frameMs:F0}ms.png";
                string fullPath = Path.Combine(m_OutputDir, fileName);
                ScreenCapture.CaptureScreenshot(fullPath);
                m_LastCaptureRealtime = now;
                m_TotalCaptured = captureNumber;
                ModLog.Info($"Spike screenshot: {fileName} (frame {frameMs:F0}ms)");
            }
            catch (Exception ex)
            {
                m_DirChecked = false;
                m_OutputDir = null;
                ModLog.Warn($"Spike screenshot failed: {ex}");
            }
        }

        public void Reset()
        {
            m_LastCaptureRealtime = float.NegativeInfinity;
            m_TotalCaptured = 0;
            m_SessionSerial = (m_SessionSerial + 1) % 100;
            m_OutputDir = null;
            m_DirChecked = false;
        }

        private void EnsureDir()
        {
            if (m_DirChecked && !string.IsNullOrEmpty(m_OutputDir) && Directory.Exists(m_OutputDir))
                return;

            try
            {
                m_OutputDir = Path.Combine(Application.persistentDataPath, "Logs", "spikes");
                Directory.CreateDirectory(m_OutputDir);
                m_DirChecked = true;
            }
            catch
            {
                m_OutputDir = null;
                m_DirChecked = false;
            }
        }
    }
}
