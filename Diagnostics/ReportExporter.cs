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
    /// Triggered by Ctrl+F11 in the overlay. Output: persistentDataPath/Reports/CSII_Report_yyyyMMdd_HHmmss_fff.txt.
    /// </summary>
    public static class ReportExporter
    {
        private const int LOG_TAIL_LINES = 50;

        /// <returns>Full path of the saved report, or null on failure.</returns>
        public static string Export()
        {
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "Reports");
                Directory.CreateDirectory(dir);

                string fileName = $"CSII_Report_{DateTime.Now:yyyyMMdd_HHmmss_fff}.txt";
                string path = Path.Combine(dir, fileName);

                AtomicFileWriter.WriteAllText(path, BuildReport(), Encoding.UTF8);
                ModLog.Info($"Performance report saved: {path}");
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
            var profiler = ProfilerHost.TryGet();
            var snap = profiler?.LastSnapshot;
            var health = profiler?.LastHealth;

            sb.AppendLine("=== Cities: Skylines II Performance Report ===");
            sb.AppendLine(Inv($"Generated:        {DateTime.Now:yyyy-MM-dd HH:mm:ss}"));
            sb.AppendLine($"Game version:     {Application.version}");
            sb.AppendLine($"Unity:            {Application.unityVersion}");
            sb.AppendLine();
            sb.AppendLine("--- Scope of measurement ---");
            sb.AppendLine("  Per-system numbers below reflect main-thread cost only — scheduling");
            sb.AppendLine("  overhead, sync points (Dependency.Complete / CompleteDependencyBeforeRO),");
            sb.AppendLine("  structural changes, ECB playback, and any synchronous main-thread work.");
            sb.AppendLine("  Job execution on worker threads is NOT captured: Burst-compiled jobs run");
            sb.AppendLine("  outside SystemBase.Update() and cannot be instrumented from a mod.");
            sb.AppendLine("  For accurate per-job profiling attach Unity Profiler to the running game.");
            sb.AppendLine("  Frame time, GPU/CPU thread time and memory metrics are accurate.");
            sb.AppendLine();

            sb.AppendLine("--- System Info ---");
            sb.AppendLine($"OS:               {SystemInfo.operatingSystem}");
            sb.AppendLine(Inv($"CPU:              {SystemInfo.processorType} ({SystemInfo.processorCount} cores, {SystemInfo.processorFrequency} MHz)"));
            sb.AppendLine(Inv($"GPU:              {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsMemorySize} MB)"));
            sb.AppendLine($"GPU API:          {SystemInfo.graphicsDeviceType} (vendor {SystemInfo.graphicsDeviceVendor})");
            sb.AppendLine(Inv($"RAM:              {SystemInfo.systemMemorySize} MB"));
            sb.AppendLine(Inv($"Screen:           {Screen.width}x{Screen.height} @ {Screen.currentResolution.refreshRateRatio.value:F1} Hz"));
            sb.AppendLine();

            sb.AppendLine("--- Loaded Mods ---");
            var mods = ModAttribution.GetLoadedMods();
            if (mods.Count == 0)
                sb.AppendLine("  (none detected)");
            else
                foreach (var name in mods)
                    sb.AppendLine($"  {name}");
            sb.AppendLine();

            sb.AppendLine($"--- Current Snapshot ({WindowLabel(snap)}) ---");
            if (snap == null)
            {
                sb.AppendLine("  No data collected yet — start a save first.");
            }
            else
            {
                sb.AppendLine(Inv($"FPS:              {snap.AvgFps:F1} avg / {snap.MinFps:F1} min"));
                sb.AppendLine(Inv($"Frame:            {snap.AvgFrameMs:F2} ms avg / {snap.MaxFrameMs:F2} ms max"));
                sb.AppendLine(Inv($"Sim:              {snap.SimTicksPerSec:F0} ticks/s"));
                sb.AppendLine(Inv($"Spikes:           {snap.Spikes30fps} below 30 fps, {snap.Spikes20fps} below 20 fps"));
                sb.AppendLine(Inv($"Managed growth:   {snap.ManagedGrowthMBperSec:+0.00;-0.00} MB/s"));
                sb.AppendLine(Inv($"Managed memory:   {snap.ManagedMB:F1} MB ({DeltaStr(snap.ManagedDeltaMB)} from baseline)"));
            }
            sb.AppendLine();

            if (CityContext.HasData)
            {
                sb.AppendLine("--- City Context ---");
                sb.AppendLine(Inv($"Citizens:         {CityContext.Citizens:N0}"));
                sb.AppendLine(Inv($"Vehicles:         {CityContext.Vehicles:N0}"));
                sb.AppendLine(Inv($"Buildings:        {CityContext.Buildings:N0}"));
                sb.AppendLine();
            }

            if (health != null)
            {
                sb.AppendLine("--- Health ---");
                sb.AppendLine($"FPS:              {health.FpsLevel}");
                sb.AppendLine($"Stutter:          {health.StutterLevel}");
                sb.AppendLine($"Memory:           {health.MemoryLevel}  ({health.MemoryHint})");
                sb.AppendLine($"Growth:           {health.GrowthLevel}");
                sb.AppendLine($"Overall:          {health.Overall}");
                sb.AppendLine($"Bottleneck:       {health.Bottleneck} — {health.BottleneckHint}");
                sb.AppendLine();
            }

            if (snap?.TopMods != null && snap.TopMods.Length > 0)
            {
                sb.AppendLine($"--- Top Mods (by main-thread time, {WindowLabel(snap)}) ---");
                foreach (var (modName, ms) in snap.TopMods)
                    sb.AppendLine(Inv($"  {modName,-40} {ms,8:F1} ms"));
                sb.AppendLine();
            }

            if (snap?.TopModSystems != null && snap.TopModSystems.Length > 0)
            {
                sb.AppendLine("--- Top Mod Systems (main-thread cost) ---");
                foreach (var (name, ms) in snap.TopModSystems)
                    sb.AppendLine(Inv($"  {name,-40} {ms,8:F1} ms"));
                sb.AppendLine();
            }

            if (snap?.TopVanillaSystems != null && snap.TopVanillaSystems.Length > 0)
            {
                sb.AppendLine("--- Top Vanilla Systems (main-thread cost) ---");
                foreach (var (name, ms) in snap.TopVanillaSystems)
                    sb.AppendLine(Inv($"  {name,-40} {ms,8:F1} ms"));
                sb.AppendLine();
            }

            sb.AppendLine($"--- Last {LOG_TAIL_LINES} lines of VanillaProfiler.log ---");
            foreach (var line in TailPerfLog(LOG_TAIL_LINES))
                sb.AppendLine(line);

            return sb.ToString();
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

                var buffer = new byte[CHUNK];
                long scanEnd = length;
                stream.Position = length - 1;
                if (stream.ReadByte() == '\n')
                    scanEnd--;

                long pos = scanEnd;
                long startOffset = 0;
                int newlines = 0;
                while (pos > 0)
                {
                    int readSize = (int)Math.Min(CHUNK, pos);
                    pos -= readSize;
                    stream.Position = pos;
                    int read = stream.Read(buffer, 0, readSize);
                    if (read <= 0) break;

                    for (int i = read - 1; i >= 0; i--)
                    {
                        if (buffer[i] != (byte)'\n') continue;
                        newlines++;
                        if (newlines == count)
                        {
                            startOffset = pos + i + 1;
                            pos = 0;
                            break;
                        }
                    }
                }

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

                string text = Encoding.UTF8.GetString(bytes, 0, total);
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
            catch (Exception ex)
            {
                return new[] { $"  (failed to read log: {ex.Message})" };
            }
        }

        private static string DeltaStr(double mb) => mb >= 0 ? Inv($"+{mb:F1} MB") : Inv($"{mb:F1} MB");

        private static string WindowLabel(OverlaySnapshot snap)
        {
            if (snap?.WindowSeconds > 0)
                return Inv($"last {snap.WindowSeconds:F1}s report window");
            return "last report window";
        }

        private static string Inv(FormattableString value) => FormattableString.Invariant(value);
    }
}
