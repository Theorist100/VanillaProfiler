using System;
using System.Collections.Generic;
using System.Text;
using VanillaProfiler.Diagnostics;

namespace VanillaProfiler.Output
{
    internal static class ReportSummarySection
    {
        private const double SUMMARY_HEAVY_SYSTEM_MS = 10.0;
        private const double SUMMARY_HEAVY_MOD_MS = 10.0;
        private const string PROFILER_MOD_NAME = "VanillaProfiler";

        public static void Append(StringBuilder sb, OverlaySnapshot? snap, HealthReport? health)
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
            AppendTopMod(sb, snap);
            AppendTopSystem(sb, snap);
            sb.AppendLine(Inv($"Profiler cost:    {snap.ProfilerSelfMs:F2} ms/frame ({snap.ProfilerSelfPercent:F2}% of frame)"));
            sb.AppendLine($"Action:           {SummaryAction(snap, health)}");
            sb.AppendLine();
        }

        private static void AppendTopMod(StringBuilder sb, OverlaySnapshot snap)
        {
            if (TryFirstNonProfilerMod(snap.TopMods, out var top))
                sb.AppendLine(Inv($"Top mod:          {top.ModName} - {top.TotalMs:F1} ms self in {WindowLabel(snap)}"));
            else
                sb.AppendLine("Top mod:          (none)");
        }

        private static void AppendTopSystem(StringBuilder sb, OverlaySnapshot snap)
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

        private static string BottleneckText(HealthReport health)
        {
            return health.RenderCause == RenderCause.None
                ? health.Bottleneck.ToString()
                : $"{health.Bottleneck}/{health.RenderCause}";
        }

        private static string WindowLabel(OverlaySnapshot snap)
        {
            if (snap.WindowSeconds > 0)
                return Inv($"last {snap.WindowSeconds:F1}s report window");
            return "last report window";
        }

        private static string Inv(FormattableString value) => FormattableString.Invariant(value);
    }
}
