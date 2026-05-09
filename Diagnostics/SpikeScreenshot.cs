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
    public static class SpikeScreenshot
    {
        private const float COOLDOWN_S = 30.0f;       // at most one capture per 30 seconds

        public static bool Enabled
        {
            get => SettingsStore.Current.SpikeScreenshots;
            set { SettingsStore.Current.SpikeScreenshots = value; SettingsStore.Save(); }
        }

        private static float s_LastCaptureRealtime = float.NegativeInfinity;
        private static int s_TotalCaptured;
        private static int s_SessionSerial;
        private static string? s_OutputDir;
        private static bool s_DirChecked;

        public static int TotalCaptured => s_TotalCaptured;

        public static void OnFrame(double frameMs)
        {
            OnFrame(frameMs, SettingsStore.Snapshot);
        }

        public static void OnFrame(double frameMs, ProfilerSettingsSnapshot settings)
        {
            if (!settings.Settings.SpikeScreenshots || frameMs < settings.Settings.SpikeThresholdMs) return;

            float now = Time.realtimeSinceStartup;
            if (now - s_LastCaptureRealtime < COOLDOWN_S) return;

            try
            {
                EnsureDir();
                if (s_OutputDir == null)
                {
                    s_LastCaptureRealtime = now;
                    return;
                }

                int captureNumber = s_TotalCaptured + 1;
                string fileName = $"spike_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{s_SessionSerial:00}_{captureNumber:000}_{frameMs:F0}ms.png";
                string fullPath = Path.Combine(s_OutputDir, fileName);
                ScreenCapture.CaptureScreenshot(fullPath);
                s_LastCaptureRealtime = now;
                s_TotalCaptured = captureNumber;
                ModLog.Info($"Spike screenshot: {fileName} (frame {frameMs:F0}ms)");
            }
            catch (Exception ex)
            {
                s_DirChecked = false;
                s_OutputDir = null;
                ModLog.Warn($"Spike screenshot failed: {ex}");
            }
        }

        public static void Reset()
        {
            s_LastCaptureRealtime = float.NegativeInfinity;
            s_TotalCaptured = 0;
            s_SessionSerial = (s_SessionSerial + 1) % 100;
            s_OutputDir = null;
            s_DirChecked = false;
        }

        private static void EnsureDir()
        {
            if (s_DirChecked && !string.IsNullOrEmpty(s_OutputDir) && Directory.Exists(s_OutputDir))
                return;

            try
            {
                s_OutputDir = Path.Combine(Application.persistentDataPath, "Logs", "spikes");
                Directory.CreateDirectory(s_OutputDir);
                s_DirChecked = true;
            }
            catch
            {
                s_OutputDir = null;
                s_DirChecked = false;
            }
        }
    }
}
