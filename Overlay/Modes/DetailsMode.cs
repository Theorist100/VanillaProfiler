using VanillaProfiler.Diagnostics;

namespace VanillaProfiler.Overlay.Modes
{
    /// <summary>
    /// Advanced view for mod authors / support — adds top mods, top vanilla systems,
    /// top mod systems and city context to Status's data.
    /// </summary>
    public sealed class DetailsMode : IOverlayMode
    {
        public string DisplayName => "Details";
        public bool IsHidden => false;

        public float MeasureHeight(OverlaySnapshot snapshot)
        {
            // Contract: ProfilerOverlay only calls MeasureHeight when a snapshot
            // exists (settling badge covers the pre-snapshot phase). The collapsed
            // null-snapshot fallback is therefore unreachable; modelling sizes only
            // for the data path keeps the contract explicit.
            // Header title + breadcrumb, fps, sparkline, sim, gc, mem, bottleneck.
            // City is conditional and should not reserve a blank row before data arrives.
            int lines = 8;
            if (CityContext.HasData) lines++;
            if (snapshot.GfxUsedMB > 0 || snapshot.AudioUsedMB > 0) lines++;
            if (snapshot.MainThreadCpuMs > 0 || snapshot.RenderThreadCpuMs > 0) lines++;
            if (HasItems(snapshot.TopMods)) lines += 1 + snapshot.TopMods.Length;
            if (HasItems(snapshot.TopVanillaSystems)) lines += 1 + snapshot.TopVanillaSystems.Length;
            if (HasItems(snapshot.TopModSystems)) lines += 1 + snapshot.TopModSystems.Length;
            return OverlayPanel.PAD * 2 + OverlayPanel.LINE_H * lines + 12f;
        }

        public void Draw(DrawContext ctx, OverlaySnapshot snapshot, HealthReport health)
        {
            OverlayPanel.DrawHeaderWithCycle(ctx, "VANILLA PROFILER  >  DETAILS");

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

            if (snapshot.GfxUsedMB > 0 || snapshot.AudioUsedMB > 0)
                OverlayPanel.DrawLine(ctx,
                    $"GPU memory:    {snapshot.GfxUsedMB,5:F0} MB Gfx,  {snapshot.AudioUsedMB,4:F0} MB audio",
                    ctx.Theme.DimStyle);

            if (snapshot.MainThreadCpuMs > 0 || snapshot.RenderThreadCpuMs > 0 || snapshot.GpuFrameTimeMs > 0)
                OverlayPanel.DrawLine(ctx,
                    $"Threads:       CPU main {snapshot.MainThreadCpuMs,5:F1} ms  /  CPU render {snapshot.RenderThreadCpuMs,5:F1} ms  /  GPU {snapshot.GpuFrameTimeMs,5:F1} ms",
                    ctx.Theme.DimStyle);

            if (health != null)
                OverlayPanel.DrawLine(ctx,
                    $"Bottleneck:    {health.Bottleneck} — {health.BottleneckHint}",
                    ctx.Theme.StyleForBottleneck(health.Bottleneck));

            if (CityContext.HasData)
                OverlayPanel.DrawLine(ctx,
                    $"City:          {OverlayFormat.Count(CityContext.Citizens)} pop, {OverlayFormat.Count(CityContext.Vehicles)} vehicles, {OverlayFormat.Count(CityContext.Buildings)} buildings",
                    ctx.Theme.DimStyle);

            DrawTopList(ctx, "Top mods (by total ms in sample)", snapshot.TopMods);
            DrawTopList(ctx, "Top vanilla systems", snapshot.TopVanillaSystems);
            DrawTopList(ctx, "Top mod systems", snapshot.TopModSystems);
        }

        private static void DrawTopList(DrawContext ctx, string title, (string Name, double TotalMs)[] items)
        {
            if (!HasItems(items)) return;
            OverlayPanel.DrawSection(ctx, title);
            foreach (var (name, ms) in items)
                OverlayPanel.DrawLine(ctx, $"  {OverlayFormat.Truncate(name, 36),-36}  {ms,7:F1} ms", ctx.Theme.BodyStyle);
        }

        private static bool HasItems((string, double)[] arr) => arr != null && arr.Length > 0;
    }
}
