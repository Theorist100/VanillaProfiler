using System;
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
            ReportSummarySection.Append(sb, snap, health);

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
            foreach (var line in ReportLogTail.Read(Application.persistentDataPath, LOG_TAIL_LINES))
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
