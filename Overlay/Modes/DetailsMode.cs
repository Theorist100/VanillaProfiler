using System;
using System.Collections.Generic;
using VanillaProfiler.Diagnostics;

namespace VanillaProfiler.Overlay.Modes
{
    /// <summary>
    /// Advanced view for mod authors / support — adds top mods, top vanilla systems,
    /// top mod systems and city context to Status's data.
    ///
    /// Per-thread CPU/GPU/PresentWait breakdown lives in EngineMode (focused engine
    /// counters screen). Repeating it here just made two screens show the same row
    /// with no added context, so Details now stays mod-attribution focused.
    /// </summary>
    public sealed class DetailsMode : IOverlayMode
    {
        // Cap the visible replacement rows so a config with dozens of disabled
        // systems doesn't push the rest of the panel off-screen. Full list is
        // always in PERF.log; this is a glance summary.
        private const int REPLACEMENTS_LIMIT = 6;

        private OverlaySnapshot? m_CachedSnapshot;
        private IReadOnlyList<string> m_CachedTopModRows = Array.Empty<string>();
        private IReadOnlyList<string> m_CachedTopVanillaRows = Array.Empty<string>();
        private IReadOnlyList<string> m_CachedTopSystemRows = Array.Empty<string>();
        private IReadOnlyList<string> m_CachedReplacementRows = Array.Empty<string>();
        private int m_CachedReplacementOverflow;

        public string DisplayName => "Details";
        public bool IsHidden => false;

        public float MeasureHeight(OverlaySnapshot snapshot)
        {
            // Contract: ProfilerOverlay only calls MeasureHeight when a snapshot
            // exists (settling badge covers the pre-snapshot phase). The collapsed
            // null-snapshot fallback is therefore unreachable; modelling sizes only
            // for the data path keeps the contract explicit.
            // Fps, sparkline, sim, mem growth, memory used, bottleneck.
            // The fixed header is drawn by ProfilerOverlay.
            // City is conditional and should not reserve a blank row before data arrives.
            int lines = 6;
            if (CityContext.HasData) lines++;
            if (snapshot.GfxUsedAvailable || snapshot.AudioUsedAvailable) lines++;
            if (HasItems(snapshot.TopMods)) lines += 1 + snapshot.TopMods.Count;
            if (HasItems(snapshot.TopVanillaSystems)) lines += 1 + snapshot.TopVanillaSystems.Count;
            if (HasItems(snapshot.TopModSystems)) lines += 1 + snapshot.TopModSystems.Count;
            int replaced = snapshot.ReplacedVanillaSystems?.Count ?? 0;
            if (replaced > 0)
            {
                int shown = replaced > REPLACEMENTS_LIMIT ? REPLACEMENTS_LIMIT : replaced;
                // section header + N rows + optional "+M more" row
                lines += 1 + shown + (replaced > REPLACEMENTS_LIMIT ? 1 : 0);
            }
            return OverlayPanel.PAD * 2 + OverlayPanel.LINE_H * lines + 12f;
        }

        public void Draw(DrawContext ctx, OverlaySnapshot snapshot, HealthReport health)
        {
            OverlayPanel.DrawLine(ctx,
                $"FPS:           {snapshot.AvgFps,5:F0} avg / {snapshot.MinFps,4:F0} min   (frame {snapshot.AvgFrameMs:F1} / {snapshot.MaxFrameMs:F1} ms)",
                ctx.Theme.BodyStyle);

            int sparkWidth = ctx.SparklineWidth;
            string spark = ctx.FpsSparkline ?? string.Empty;
            OverlayPanel.DrawLine(ctx,
                string.IsNullOrEmpty(spark) ? $"FPS history:   (collecting, last {sparkWidth}s)" : $"FPS history:   {spark}  (last {sparkWidth}s)",
                ctx.Theme.DimStyle);

            OverlayPanel.DrawLine(ctx,
                $"Simulation:    {snapshot.SimTicksPerSec,5:F0} ticks/s",
                ctx.Theme.BodyStyle);
            OverlayPanel.DrawLine(ctx,
                $"Mem growth:    {snapshot.ManagedGrowthMBperSec,5:+0.00;-0.00} MB/s   (spikes: {snapshot.Spikes30fps} <30fps, {snapshot.Spikes20fps} <20fps)",
                ctx.Theme.BodyStyle);
            OverlayPanel.DrawLine(ctx,
                $"Memory used:   {snapshot.ManagedMB,5:F0} MB managed   ({OverlayFormat.Delta(snapshot.ManagedDeltaMB)} since start)",
                ctx.Theme.BodyStyle);

            if (snapshot.GfxUsedAvailable || snapshot.AudioUsedAvailable)
                OverlayPanel.DrawLine(ctx,
                    $"GPU memory:    {OverlayFormat.MemoryMB(snapshot.GfxUsedMB, snapshot.GfxUsedAvailable),8} Gfx,  {OverlayFormat.MemoryMB(snapshot.AudioUsedMB, snapshot.AudioUsedAvailable),8} audio",
                    ctx.Theme.DimStyle);

            // Per-thread Main/Render/GPU/PresentWait breakdown lives in EngineMode —
            // showing it here too just doubled the row without adding context. Bottleneck
            // line below summarises the conclusion; Engine has the raw numbers.

            if (health != null)
                OverlayPanel.DrawLine(ctx,
                    $"Bottleneck:    {BottleneckText(health)} — {health.BottleneckHint}",
                    ctx.Theme.StyleForBottleneck(health.Bottleneck));

            if (CityContext.HasData)
                OverlayPanel.DrawLine(ctx,
                    $"City:          {OverlayFormat.Count(CityContext.Citizens)} pop, {OverlayFormat.Count(CityContext.Vehicles)} vehicles, {OverlayFormat.Count(CityContext.Buildings)} buildings",
                    ctx.Theme.DimStyle);

            EnsureCachedRows(snapshot);
            DrawCachedTable(ctx, "Top mods (self main-thread ms)", m_CachedTopModRows);
            DrawCachedTable(ctx, "Top vanilla systems (self main-thread cost)", m_CachedTopVanillaRows);
            DrawCachedTable(ctx, "Top mod systems (self main-thread cost)", m_CachedTopSystemRows);
            DrawReplacements(ctx, m_CachedReplacementRows, m_CachedReplacementOverflow);
        }

        private void EnsureCachedRows(OverlaySnapshot snapshot)
        {
            if (ReferenceEquals(m_CachedSnapshot, snapshot)) return;
            m_CachedSnapshot = snapshot;
            m_CachedTopModRows = BuildTopRows(snapshot.TopMods);
            m_CachedTopVanillaRows = BuildTopRows(snapshot.TopVanillaSystems);
            m_CachedTopSystemRows = BuildTopRows(snapshot.TopModSystems);
            m_CachedReplacementRows = BuildReplacementRows(snapshot.ReplacedVanillaSystems, out m_CachedReplacementOverflow);
        }

        private static IReadOnlyList<string> BuildTopRows(IReadOnlyList<SystemCostRow> rows)
        {
            if (rows == null || rows.Count == 0) return Array.Empty<string>();
            var result = new string[rows.Count];
            for (int i = 0; i < rows.Count; i++)
                result[i] = $"  {OverlayFormat.Truncate(rows[i].Name, 36),-36}  {rows[i].TotalMs,7:F1} ms";
            return result;
        }

        private static IReadOnlyList<string> BuildTopRows(IReadOnlyList<ModCostRow> rows)
        {
            if (rows == null || rows.Count == 0) return Array.Empty<string>();
            var result = new string[rows.Count];
            for (int i = 0; i < rows.Count; i++)
                result[i] = $"  {OverlayFormat.Truncate(rows[i].ModName, 36),-36}  {rows[i].TotalMs,7:F1} ms";
            return result;
        }

        private static IReadOnlyList<string> BuildReplacementRows(
            IReadOnlyList<ReplacedVanillaSystemRow> items,
            out int overflow)
        {
            overflow = 0;
            if (items == null || items.Count == 0) return Array.Empty<string>();

            int shown = items.Count > REPLACEMENTS_LIMIT ? REPLACEMENTS_LIMIT : items.Count;
            overflow = items.Count - shown;
            var result = new string[shown];
            for (int i = 0; i < shown; i++)
            {
                var item = items[i];
                string sys = OverlayFormat.Truncate(item.VanillaSystem, 38);
                result[i] = $"  {sys,-38}  {item.TotalMs,6:F1} ms  ← {item.OwnerText}";
            }
            return result;
        }

        private static void DrawCachedTable(DrawContext ctx, string title, IReadOnlyList<string> rows)
        {
            if (rows == null || rows.Count == 0) return;
            OverlayPanel.DrawSection(ctx, title);
            for (int i = 0; i < rows.Count; i++)
                OverlayPanel.DrawLine(ctx, rows[i], ctx.Theme.BodyStyle);
        }

        private static void DrawReplacements(
            DrawContext ctx,
            IReadOnlyList<string> rows,
            int overflow)
        {
            if (rows == null || rows.Count == 0) return;
            // The ms is honest total Update elapsed time. We can't split it
            // between the patching mod's prefix and the (possibly skipped)
            // vanilla original — Harmony does not expose a hook between
            // them. Header reflects that: cost is shown, attribution split
            // is what's not measurable.
            OverlayPanel.DrawSection(ctx, "Patched vanilla systems (total Update ms, mod+vanilla split unknown)");
            for (int i = 0; i < rows.Count; i++)
                OverlayPanel.DrawLine(ctx, rows[i], ctx.Theme.BodyStyle);
            if (overflow > 0)
                OverlayPanel.DrawLine(ctx,
                    $"  +{overflow} more — see VanillaProfiler.log",
                    ctx.Theme.DimStyle);
        }

        private static bool HasItems<T>(IReadOnlyCollection<T> rows) => rows != null && rows.Count > 0;

        private static string BottleneckText(HealthReport health)
        {
            return health.RenderCause == RenderCause.None
                ? health.Bottleneck.ToString()
                : $"{health.Bottleneck}/{health.RenderCause}";
        }
    }
}
