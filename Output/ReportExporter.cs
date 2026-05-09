using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using VanillaProfiler.Diagnostics;

namespace VanillaProfiler.Output
{
    /// <summary>
    /// One-shot diagnostic report writer for players to attach to bug reports.
    /// Triggered by Ctrl+F11 in the overlay. Output: persistentDataPath/Reports/CSII_Report_yyyyMMdd_HHmmss_fff.txt
    /// plus a best-effort .zip support bundle.
    /// </summary>
    public static class ReportExporter
    {
        private const int LOG_TAIL_LINES = 50;
        private const double SUMMARY_HEAVY_SYSTEM_MS = 10.0;
        private const double SUMMARY_HEAVY_MOD_MS = 10.0;
        private const string PROFILER_MOD_NAME = "VanillaProfiler";

        public static ExportResult Export()
        {
            string? path = null;
            string? report = null;
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "Reports");
                Directory.CreateDirectory(dir);

                string fileName = $"CSII_Report_{DateTime.Now:yyyyMMdd_HHmmss_fff}.txt";
                path = Path.Combine(dir, fileName);

                report = BuildReport();
                AtomicFileWriter.WriteAllText(path, report, Encoding.UTF8);
                ModLog.Info($"Performance report saved: {path}");
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Report export failed: {ex}");
                return new ExportResult(
                    reportPath: path,
                    zipPath: null,
                    zipWarnings: Array.Empty<string>(),
                    error: ex.Message,
                    reportWritten: false,
                    zipWritten: false);
            }

            var bundle = WriteSupportBundle(path, report);
            return new ExportResult(
                reportPath: path,
                zipPath: bundle.ZipPath,
                zipWarnings: bundle.Warnings,
                error: bundle.Error,
                reportWritten: true,
                zipWritten: bundle.ZipWritten);
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
            AppendSummary(sb, snap, health);

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

        private static void AppendSummary(StringBuilder sb, OverlaySnapshot? snap, HealthReport? health)
        {
            sb.AppendLine("--- Summary ---");
            if (snap == null || health == null)
            {
                sb.AppendLine("Overall:          No report window yet");
                sb.AppendLine("Action:           Load a city and wait for the first report window.");
                sb.AppendLine();
                return;
            }

            sb.AppendLine($"Overall:          {health.Overall}");
            sb.AppendLine($"Bottleneck:       {BottleneckText(health)} - {health.BottleneckHint}");
            sb.AppendLine(Inv($"Frame:            {snap.AvgFps:F1} FPS avg, {snap.AvgFrameMs:F1} ms avg, {snap.MaxFrameMs:F1} ms max"));
            sb.AppendLine($"Memory:           {health.MemoryLevel}, growth {health.GrowthLevel} ({health.MemoryHint})");
            AppendSummaryTopMod(sb, snap);
            AppendSummaryTopSystem(sb, snap);
            sb.AppendLine(Inv($"Profiler cost:    {snap.ProfilerSelfMs:F2} ms/frame ({snap.ProfilerSelfPercent:F2}% of frame)"));
            sb.AppendLine($"Action:           {SummaryAction(snap, health)}");
            sb.AppendLine();
        }

        private static void AppendSummaryTopMod(StringBuilder sb, OverlaySnapshot snap)
        {
            if (TryFirstNonProfilerMod(snap.TopMods, out var top))
                sb.AppendLine(Inv($"Top mod:          {top.ModName} - {top.TotalMs:F1} ms self in {WindowLabel(snap)}"));
            else
                sb.AppendLine("Top mod:          (none)");
        }

        private static void AppendSummaryTopSystem(StringBuilder sb, OverlaySnapshot snap)
        {
            if (TryFirstNonProfilerSystem(snap.TopModSystems, out var top))
                sb.AppendLine(Inv($"Top mod system:   {top.Name} - {top.TotalMs:F1} ms self"));
            else
                sb.AppendLine("Top mod system:   (none)");
        }

        private static string SummaryAction(OverlaySnapshot snap, HealthReport health)
        {
            if (health.MemoryLevel == HealthLevel.Poor || health.GrowthLevel == HealthLevel.Poor)
                return "Check managed memory growth first; compare another export after 60 seconds.";

            if (TryFirstNonProfilerSystem(snap.TopModSystems, out var system) && system.TotalMs >= SUMMARY_HEAVY_SYSTEM_MS)
                return $"Inspect {system.Name} first; it is the heaviest mod system in this window.";

            if (TryFirstNonProfilerMod(snap.TopMods, out var mod) && mod.TotalMs >= SUMMARY_HEAVY_MOD_MS)
                return $"Inspect {mod.ModName} first; it leads mod self-time in this window.";

            if (health.Overall == HealthLevel.Good)
                return "No obvious profiler-level problem in the last report window.";

            return "Review Health and Top Mods/Systems below; no single mod system dominates this window.";
        }

        private static bool TryFirstNonProfilerMod(
            IReadOnlyList<(string ModName, double TotalMs)>? mods,
            out (string ModName, double TotalMs) value)
        {
            value = default;
            if (mods == null || mods.Count == 0) return false;

            for (int i = 0; i < mods.Count; i++)
            {
                var entry = mods[i];
                if (string.IsNullOrEmpty(entry.ModName)) continue;
                if (string.Equals(entry.ModName, PROFILER_MOD_NAME, StringComparison.Ordinal)) continue;
                value = entry;
                return true;
            }

            value = mods[0];
            return !string.IsNullOrEmpty(value.ModName);
        }

        private static bool TryFirstNonProfilerSystem(
            IReadOnlyList<(string Name, double TotalMs)>? systems,
            out (string Name, double TotalMs) value)
        {
            value = default;
            if (systems == null || systems.Count == 0) return false;

            for (int i = 0; i < systems.Count; i++)
            {
                var entry = systems[i];
                if (string.IsNullOrEmpty(entry.Name)) continue;
                if (entry.Name.StartsWith(PROFILER_MOD_NAME, StringComparison.Ordinal)) continue;
                value = entry;
                return true;
            }

            value = systems[0];
            return !string.IsNullOrEmpty(value.Name);
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
            sb.AppendLine($"Bottleneck:       {BottleneckText(health)} — {health.BottleneckHint}");
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

        private static string BottleneckText(HealthReport health)
        {
            return health.RenderCause == RenderCause.None
                ? health.Bottleneck.ToString()
                : $"{health.Bottleneck}/{health.RenderCause}";
        }

        private static string Counter(double value, bool available, string unit)
            => available ? Inv($"{value:F2} {unit}") : "unavailable";

        private static SupportBundleResult WriteSupportBundle(string reportPath, string report)
        {
            var result = SupportBundleWriter.Write(reportPath, report);
            if (result.ZipPath != null)
            {
                ModLog.Info($"Support bundle saved: {result.ZipPath}");
            }
            if (result.Warnings.Count > 0)
            {
                ModLog.Warn($"Support bundle created with warnings: {string.Join("; ", result.Warnings)}");
            }
            if (result.Error != null)
                ModLog.Warn($"Support bundle export failed: {result.Error}");
            return result;
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
