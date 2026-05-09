using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using VanillaProfiler.Output;

namespace VanillaProfiler.Diagnostics
{
    /// <summary>
    /// One-shot diagnostic report writer for players to attach to bug reports.
    /// Triggered by Ctrl+F11 in the overlay. Output: persistentDataPath/Reports/CSII_Report_yyyyMMdd_HHmmss_fff.txt
    /// plus a best-effort .zip support bundle.
    /// </summary>
    public static class ReportExporter
    {
        private const int LOG_TAIL_LINES = 50;

        /// <returns>Full path of the saved report, or null on failure.</returns>
        public static string? Export()
        {
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "Reports");
                Directory.CreateDirectory(dir);

                string fileName = $"CSII_Report_{DateTime.Now:yyyyMMdd_HHmmss_fff}.txt";
                string path = Path.Combine(dir, fileName);

                string report = BuildReport();
                AtomicFileWriter.WriteAllText(path, report, Encoding.UTF8);
                ModLog.Info($"Performance report saved: {path}");
                WriteSupportBundle(path, report);
                return path;
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Report export failed: {ex}");
                return null;
            }
        }

        private static string BuildReport()
        {
            var sb = new StringBuilder(8192);
            var profiler = ProfilerHost.TryGetReadSurface();
            var snap = profiler?.LastSnapshot;
            var health = profiler?.LastHealth;

            sb.AppendLine("=== Cities: Skylines II Performance Report ===");
            sb.AppendLine(Inv($"Generated:        {DateTime.Now:yyyy-MM-dd HH:mm:ss}"));
            sb.AppendLine($"Game version:     {Application.version}");
            sb.AppendLine($"Unity:            {Application.unityVersion}");
            sb.AppendLine();
            sb.AppendLine("--- Scope of measurement ---");
            sb.AppendLine("  Top per-system numbers below use self/exclusive main-thread cost — scheduling");
            sb.AppendLine("  overhead, sync points (Dependency.Complete / CompleteDependencyBeforeRO),");
            sb.AppendLine("  structural changes, ECB playback, and any synchronous main-thread work.");
            sb.AppendLine("  Nested SystemBase.Update calls are subtracted from the parent system.");
            sb.AppendLine("  Inclusive/total Update time is still kept for patched-vanilla diagnostics.");
            sb.AppendLine("  Job execution on worker threads is NOT captured: Burst-compiled jobs run");
            sb.AppendLine("  outside SystemBase.Update() and cannot be instrumented from a mod.");
            sb.AppendLine("  For accurate per-job profiling attach Unity Profiler to the running game.");
            sb.AppendLine("  Frame time, GPU/CPU thread time and memory metrics are accurate.");
            sb.AppendLine();

            AppendSystemInfo(sb);
            AppendLoadedMods(sb);
            AppendSnapshot(sb, snap);
            AppendCounterAvailability(sb, snap);
            AppendCityContext(sb);
            AppendHealth(sb, health);
            AppendRecommendations(sb, snap, health);
            AppendTopTables(sb, snap);

            sb.AppendLine($"--- Last {LOG_TAIL_LINES} lines of VanillaProfiler.log ---");
            foreach (var line in TailPerfLog(LOG_TAIL_LINES))
                sb.AppendLine(line);

            return sb.ToString();
        }

        private static void AppendSystemInfo(StringBuilder sb)
        {
            sb.AppendLine("--- System Info ---");
            sb.AppendLine($"OS:               {SystemInfo.operatingSystem}");
            sb.AppendLine(Inv($"CPU:              {SystemInfo.processorType} ({SystemInfo.processorCount} cores, {SystemInfo.processorFrequency} MHz)"));
            sb.AppendLine(Inv($"GPU:              {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsMemorySize} MB)"));
            sb.AppendLine($"GPU API:          {SystemInfo.graphicsDeviceType} (vendor {SystemInfo.graphicsDeviceVendor})");
            sb.AppendLine(Inv($"RAM:              {SystemInfo.systemMemorySize} MB"));
            sb.AppendLine(Inv($"Screen:           {Screen.width}x{Screen.height} @ {Screen.currentResolution.refreshRateRatio.value:F1} Hz"));
            sb.AppendLine();
        }

        private static void AppendLoadedMods(StringBuilder sb)
        {
            sb.AppendLine("--- Loaded Mods ---");
            var mods = ModAttribution.GetLoadedMods();
            if (mods.Count == 0)
                sb.AppendLine("  (none detected)");
            else
                foreach (var name in mods)
                    sb.AppendLine($"  {name}");
            sb.AppendLine();
        }

        private static void AppendSnapshot(StringBuilder sb, OverlaySnapshot? snap)
        {
            sb.AppendLine($"--- Current Snapshot ({WindowLabel(snap)}) ---");
            if (snap == null)
            {
                sb.AppendLine("  No data collected yet — start a save first.");
                sb.AppendLine();
                return;
            }

            sb.AppendLine(Inv($"FPS:              {snap.AvgFps:F1} avg / {snap.MinFps:F1} min"));
            sb.AppendLine(Inv($"Frame:            {snap.AvgFrameMs:F2} ms avg / {snap.MaxFrameMs:F2} ms max"));
            sb.AppendLine(Inv($"Sim:              {snap.SimTicksPerSec:F0} ticks/s"));
            sb.AppendLine(Inv($"Spikes:           {snap.Spikes30fps} below 30 fps, {snap.Spikes20fps} below 20 fps"));
            sb.AppendLine(Inv($"Managed growth:   {snap.ManagedGrowthMBperSec:+0.00;-0.00} MB/s"));
            sb.AppendLine(Inv($"Managed memory:   {snap.ManagedMB:F1} MB ({DeltaStr(snap.ManagedDeltaMB)} from baseline)"));
            sb.AppendLine(Inv($"CPU/GPU frame:    Main {Counter(snap.MainThreadCpuMs, snap.MainThreadCpuAvailable, "ms")}, Render {Counter(snap.RenderThreadCpuMs, snap.RenderThreadCpuAvailable, "ms")}, GPU {Counter(snap.GpuFrameTimeMs, snap.GpuFrameTimeAvailable, "ms")}"));
            sb.AppendLine(Inv($"Present wait:     {Counter(snap.PresentWaitMs, snap.PresentWaitAvailable, "ms")}"));
            sb.AppendLine();
        }

        private static void AppendCounterAvailability(StringBuilder sb, OverlaySnapshot? snap)
        {
            ReportTextSections.AppendCounterAvailability(sb, snap);
        }

        private static void AppendCityContext(StringBuilder sb)
        {
            if (!CityContext.HasData) return;
            sb.AppendLine("--- City Context ---");
            sb.AppendLine(Inv($"Citizens:         {CityContext.Citizens:N0}"));
            sb.AppendLine(Inv($"Vehicles:         {CityContext.Vehicles:N0}"));
            sb.AppendLine(Inv($"Buildings:        {CityContext.Buildings:N0}"));
            sb.AppendLine();
        }

        private static void AppendHealth(StringBuilder sb, HealthReport? health)
        {
            if (health == null) return;
            sb.AppendLine("--- Health ---");
            sb.AppendLine($"FPS:              {health.FpsLevel}");
            sb.AppendLine($"Stutter:          {health.StutterLevel}");
            sb.AppendLine($"Memory:           {health.MemoryLevel}  ({health.MemoryHint})");
            sb.AppendLine($"Growth:           {health.GrowthLevel}");
            sb.AppendLine($"Overall:          {health.Overall}");
            sb.AppendLine($"Bottleneck:       {health.Bottleneck} — {health.BottleneckHint}");
            sb.AppendLine();
        }

        private static void AppendRecommendations(StringBuilder sb, OverlaySnapshot? snap, HealthReport? health)
        {
            if (snap == null || health == null) return;
            var recommendations = ProfilerHost.TryGetReadSurface()?.BuildRecommendations(health, snap)
                ?? Array.Empty<Recommendation>();
            if (recommendations.Count == 0) return;

            sb.AppendLine("--- Recommendations (why they appeared) ---");
            foreach (var rec in recommendations)
            {
                sb.AppendLine($"[{rec.Level}] {rec.Title}");
                if (!string.IsNullOrEmpty(rec.Action))
                    sb.AppendLine($"  Action: {rec.Action}");
                if (!string.IsNullOrEmpty(rec.Reason))
                    sb.AppendLine($"  Signal: {rec.Reason}");
            }
            sb.AppendLine();
        }

        private static void AppendTopTables(StringBuilder sb, OverlaySnapshot? snap)
        {
            ReportTextSections.AppendTopTables(sb, snap, WindowLabel(snap));
        }

        private static IEnumerable<string> TailPerfLog(int count)
        {
            try
            {
                if (count < 1) count = 1;
                string path = LogFileSink.GetLogPath(Application.persistentDataPath);
                if (!File.Exists(path))
                    return new[] { $"  ({LogFileSink.LOG_FILENAME} not found)" };

                const int CHUNK = 8192;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long length = stream.Length;
                if (length == 0)
                    return new[] { $"  ({LogFileSink.LOG_FILENAME} is empty)" };

                long startOffset = FindTailStartOffset(stream, length, count, CHUNK);
                string text = ReadUtf8From(stream, startOffset, length);
                return LastLines(text, count);
            }
            catch (Exception ex)
            {
                return new[] { $"  (failed to read log: {ex.Message})" };
            }
        }

        private static long FindTailStartOffset(FileStream stream, long length, int count, int chunkSize)
        {
            var buffer = new byte[chunkSize];
            long scanEnd = length;
            stream.Position = length - 1;
            if (stream.ReadByte() == '\n')
                scanEnd--;

            long pos = scanEnd;
            long startOffset = 0;
            int newlines = 0;
            while (pos > 0)
            {
                int readSize = (int)Math.Min(chunkSize, pos);
                pos -= readSize;
                stream.Position = pos;
                int read = stream.Read(buffer, 0, readSize);
                if (read <= 0) break;
                if (TryFindTailOffset(buffer, read, pos, count, ref newlines, out startOffset))
                    break;
            }
            return startOffset;
        }

        private static bool TryFindTailOffset(
            byte[] buffer, int read, long chunkStart, int count, ref int newlines, out long offset)
        {
            offset = 0;
            for (int i = read - 1; i >= 0; i--)
            {
                if (buffer[i] != (byte)'\n') continue;
                newlines++;
                if (newlines != count) continue;
                offset = chunkStart + i + 1;
                return true;
            }
            return false;
        }

        private static string ReadUtf8From(FileStream stream, long startOffset, long length)
        {
            int byteCount = checked((int)(length - startOffset));
            var bytes = new byte[byteCount];
            stream.Position = startOffset;
            int total = 0;
            while (total < byteCount)
            {
                int read = stream.Read(bytes, total, byteCount - total);
                if (read <= 0) break;
                total += read;
            }
            return Encoding.UTF8.GetString(bytes, 0, total);
        }

        private static IReadOnlyList<string> LastLines(string text, int count)
        {
            var raw = text.Split('\n');
            int lineCount = raw.Length;
            if (lineCount > 0 && raw[lineCount - 1].Length == 0)
                lineCount--;
            int first = Math.Max(0, lineCount - count);
            var lines = new List<string>(lineCount - first);
            for (int i = first; i < lineCount; i++)
                lines.Add(raw[i].TrimEnd('\r'));
            return lines;
        }

        private static string DeltaStr(double mb) => mb >= 0 ? Inv($"+{mb:F1} MB") : Inv($"{mb:F1} MB");

        private static string Counter(double value, bool available, string unit)
            => available ? Inv($"{value:F2} {unit}") : "unavailable";

        private static void WriteSupportBundle(string reportPath, string report)
        {
            try
            {
                SupportBundleWriter.Write(reportPath, report);
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Support bundle export failed: {ex.Message}");
            }
        }

        private static string WindowLabel(OverlaySnapshot? snap)
        {
            if (snap?.WindowSeconds > 0)
                return Inv($"last {snap.WindowSeconds:F1}s report window");
            return "last report window";
        }

        private static string Inv(FormattableString value) => FormattableString.Invariant(value);
    }
}
