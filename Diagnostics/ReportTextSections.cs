using System;
using System.Collections.Generic;
using System.Text;

namespace VanillaProfiler.Diagnostics
{
    /// <summary>
    /// Shared text sections used by one-shot exports and periodic logs.
    /// </summary>
    public static class ReportTextSections
    {
        public static void AppendCounterAvailability(StringBuilder sb, OverlaySnapshot? snap)
        {
            if (snap == null) return;
            sb.AppendLine("--- Counter Availability ---");
            sb.AppendLine($"CPU main:         {StatusWord(snap.MainThreadCpuAvailable)}");
            sb.AppendLine($"CPU render:       {StatusWord(snap.RenderThreadCpuAvailable)}");
            sb.AppendLine($"GPU frame time:   {StatusWord(snap.GpuFrameTimeAvailable)}");
            sb.AppendLine($"Present wait:     {StatusWord(snap.PresentWaitAvailable)}");
            sb.AppendLine($"Draw calls:       {StatusWord(snap.DrawCallsAvailable)}");
            sb.AppendLine($"SetPass calls:    {StatusWord(snap.SetPassCallsAvailable)}");
            sb.AppendLine($"Triangles:        {StatusWord(snap.TrianglesAvailable)}");
            sb.AppendLine($"Vertices:         {StatusWord(snap.VerticesAvailable)}");
            sb.AppendLine($"GPU buffers:      {StatusWord(snap.UsedBuffersBytesAvailable)}");
            sb.AppendLine($"Render targets:   {StatusWord(snap.RenderTexturesBytesAvailable)}");
            sb.AppendLine($"GC.Collect:       {StatusWord(snap.GcCollectAvailable)}");
            sb.AppendLine($"Process RSS:      {StatusWord(snap.AppResidentAvailable)}");
            sb.AppendLine();
        }

        public static void AppendTopTables(StringBuilder sb, OverlaySnapshot? snap, string windowLabel)
        {
            if (snap == null) return;
            AppendTopTable(sb, $"--- Top Mods (self main-thread cost, {windowLabel}) ---", snap.TopMods);
            AppendTopTable(sb, "--- Top Mod Systems (self main-thread cost) ---", snap.TopModSystems);
            AppendTopTable(sb, "--- Top Vanilla Systems (self main-thread cost) ---", snap.TopVanillaSystems);
            AppendReplacements(sb, snap.ReplacedVanillaSystems);
        }

        public static string CompactCounterStatus(
            bool mainThread, bool renderThread, bool gpu, bool presentWait,
            bool drawCalls, bool setPass, bool gc)
            => "  Counter status:   " +
               $"Main={StatusShort(mainThread)} " +
               $"Render={StatusShort(renderThread)} " +
               $"GPU={StatusShort(gpu)} " +
               $"PresentWait={StatusShort(presentWait)} " +
               $"DrawCalls={StatusShort(drawCalls)} " +
               $"SetPass={StatusShort(setPass)} " +
               $"GC={StatusShort(gc)}";

        private static void AppendTopTable(StringBuilder sb, string title, IReadOnlyList<SystemCostRow> rows)
        {
            if (rows == null || rows.Count == 0) return;
            sb.AppendLine(title);
            for (int i = 0; i < rows.Count; i++)
                sb.AppendLine(Inv($"  {rows[i].Name,-40} {rows[i].TotalMs,8:F1} ms"));
            sb.AppendLine();
        }

        private static void AppendTopTable(StringBuilder sb, string title, IReadOnlyList<ModCostRow> rows)
        {
            if (rows == null || rows.Count == 0) return;
            sb.AppendLine(title);
            for (int i = 0; i < rows.Count; i++)
                sb.AppendLine(Inv($"  {rows[i].ModName,-40} {rows[i].TotalMs,8:F1} ms"));
            sb.AppendLine();
        }

        private static void AppendReplacements(
            StringBuilder sb, IReadOnlyList<ReplacedVanillaSystemRow> rows)
        {
            if (rows == null || rows.Count == 0) return;
            sb.AppendLine("--- Patched Vanilla Systems (total Update ms, mod+vanilla split unknown) ---");
            AppendReplacementOwnerSummary(sb, rows);
            foreach (var row in rows)
                sb.AppendLine(Inv($"  {row.VanillaSystem,-40} {row.TotalMs,8:F1} ms  <- {row.OwnerText}"));
            sb.AppendLine();
        }

        private static void AppendReplacementOwnerSummary(
            StringBuilder sb, IReadOnlyList<ReplacedVanillaSystemRow> rows)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < rows.Count; i++)
            {
                string owner = rows[i].OwnerText;
                if (string.IsNullOrEmpty(owner)) continue;
                counts.TryGetValue(owner, out int count);
                counts[owner] = count + 1;
            }
            if (counts.Count == 0) return;

            sb.AppendLine("Owners:");
            foreach (var kvp in counts)
                sb.AppendLine($"  {kvp.Key}: {kvp.Value} patched vanilla system(s)");
            sb.AppendLine("Systems:");
        }

        public static string StatusWord(bool available) => available ? "available" : "unavailable";

        private static string StatusShort(bool available) => available ? "ok" : "unavailable";

        private static string Inv(FormattableString value) => FormattableString.Invariant(value);
    }
}
