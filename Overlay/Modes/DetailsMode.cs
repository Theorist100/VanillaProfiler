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
                    $"Bottleneck:    {health.Bottleneck} — {health.BottleneckHint}",
                    ctx.Theme.StyleForBottleneck(health.Bottleneck));

            if (CityContext.HasData)
                OverlayPanel.DrawLine(ctx,
                    $"City:          {OverlayFormat.Count(CityContext.Citizens)} pop, {OverlayFormat.Count(CityContext.Vehicles)} vehicles, {OverlayFormat.Count(CityContext.Buildings)} buildings",
                    ctx.Theme.DimStyle);

            OverlayPanel.DrawTopTable(ctx, "Top mods (self main-thread ms)", snapshot.TopMods);
            OverlayPanel.DrawTopTable(ctx, "Top vanilla systems (self main-thread cost)", snapshot.TopVanillaSystems);
            OverlayPanel.DrawTopTable(ctx, "Top mod systems (self main-thread cost)", snapshot.TopModSystems);
            DrawReplacements(ctx, snapshot.ReplacedVanillaSystems);
        }

        private static void DrawReplacements(
            DrawContext ctx, IReadOnlyList<(string VanillaSystem, string OwnerMod, double TotalMs)> items)
        {
            if (items == null || items.Count == 0) return;
            // The ms is honest total Update elapsed time. We can't split it
            // between the patching mod's prefix and the (possibly skipped)
            // vanilla original — Harmony does not expose a hook between
            // them. Header reflects that: cost is shown, attribution split
            // is what's not measurable.
            OverlayPanel.DrawSection(ctx, "Patched vanilla systems (total Update ms, mod+vanilla split unknown)");
            int shown = items.Count > REPLACEMENTS_LIMIT ? REPLACEMENTS_LIMIT : items.Count;
            for (int i = 0; i < shown; i++)
            {
                var item = items[i];
                string sys = OverlayFormat.Truncate(item.VanillaSystem, 38);
                OverlayPanel.DrawLine(ctx,
                    $"  {sys,-38}  {item.TotalMs,6:F1} ms  ← {item.OwnerMod}",
                    ctx.Theme.BodyStyle);
            }
            if (items.Count > shown)
                OverlayPanel.DrawLine(ctx,
                    $"  +{items.Count - shown} more — see VanillaProfiler.log",
                    ctx.Theme.DimStyle);
        }

        private static bool HasItems(IReadOnlyCollection<(string, double)> rows) => rows != null && rows.Count > 0;
    }
}
