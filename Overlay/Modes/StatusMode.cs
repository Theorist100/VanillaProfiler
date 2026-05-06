using System;
using VanillaProfiler.Diagnostics;

namespace VanillaProfiler.Overlay.Modes
{
    /// <summary>
    /// Player-facing short status. No tables, no system names, just the current
    /// state, likely cause, and the two useful keys.
    /// </summary>
    public sealed class StatusMode : IOverlayMode
    {
        public string DisplayName => "Status";
        public bool IsHidden => false;

        public float MeasureHeight(OverlaySnapshot snapshot)
        {
            // header (title + breadcrumb), 4 data rows (Status/Cause/FPS/Memory),
            // a permanent Profiler-self row, and an optional Likely-mod row.
            int lines = 7;
            if (!string.IsNullOrEmpty(TopMod(snapshot))) lines++;
            return OverlayPanel.PAD * 2 + OverlayPanel.LINE_H * lines + 12f;
        }

        public void Draw(DrawContext ctx, OverlaySnapshot snapshot, HealthReport health)
        {
            // Contract: ProfilerOverlay only renders modes when IsSettling=false →
            // snapshot and health are non-null here. Pre-game / settling states are
            // shown as overlay badges instead of empty mode panels.
            OverlayPanel.DrawHeaderWithCycle(ctx, "VANILLA PROFILER  >  STATUS");

            OverlayPanel.DrawLine(ctx,
                $"Status: {Label(health.Overall)}",
                ctx.Theme.StyleForHealth(health.Overall));
            OverlayPanel.DrawLine(ctx, $"Cause:  {Cause(health)}", ctx.Theme.BodyStyle);
            OverlayPanel.DrawLine(ctx, $"FPS:    {snapshot.AvgFps:F0} avg / {snapshot.MinFps:F0} min", ctx.Theme.BodyStyle);
            OverlayPanel.DrawLine(ctx, $"Memory: {MemoryText(health)}",
                ctx.Theme.StyleForHealth(MemoryWorst(health)));

            // Always-on profiler self-cost so players can see the mod isn't the
            // bottleneck before assuming it is. Labeled "/frame" so it's not
            // confused with the total-over-window numbers in the Details screen.
            OverlayPanel.DrawLine(ctx,
                $"Profiler self: {snapshot.ProfilerSelfMs:F2} ms/frame ({snapshot.ProfilerSelfPercent:F2}% of frame)",
                ctx.Theme.DimStyle);

            // Only show the "Likely mod:" line when a mod actually stands out. The
            // previous "Likely mod: no mod stands out yet" reads as a contradiction
            // (the label promises a suspect, the text retracts it) — better to omit.
            string mod = TopMod(snapshot);
            if (!string.IsNullOrEmpty(mod))
                OverlayPanel.DrawLine(ctx, $"Likely mod: {OverlayFormat.Truncate(mod, 46)}", ctx.Theme.BodyStyle);
        }

        private static string Label(HealthLevel level)
        {
            return level switch
            {
                HealthLevel.Good => "Good",
                HealthLevel.Ok => "Warning",
                HealthLevel.Poor => "Problem",
                HealthLevel.Unknown => "Unknown",
                _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unhandled HealthLevel"),
            };
        }

        private static string Cause(HealthReport health)
        {
            // Severe signals first (overall=Poor) — describe the dominant Poor-level
            // condition before falling back to bottleneck.
            if (health.MemoryLevel == HealthLevel.Poor) return "memory is growing";
            if (health.GrowthLevel == HealthLevel.Poor) return "managed memory growth";
            if (health.StutterLevel == HealthLevel.Poor) return "frame spikes";

            string fromBottleneck = health.Bottleneck switch
            {
                BottleneckKind.RenderBound => "graphics/rendering",
                BottleneckKind.SimBound => "simulation",
                BottleneckKind.MemoryBound => "managed memory growth",
                BottleneckKind.Balanced => null,
                BottleneckKind.Unknown => null,
                _ => throw new ArgumentOutOfRangeException(nameof(health), health.Bottleneck, "Unhandled BottleneckKind"),
            };
            if (fromBottleneck != null) return fromBottleneck;

            // Overall=Warning with Balanced bottleneck and no Poor signal — describe
            // whichever Ok-level signal lifted the verdict above Good. Without this
            // the player sees "Status: Warning" + "Cause: no clear problem", which
            // reads as a contradiction.
            if (health.MemoryLevel == HealthLevel.Ok) return "minor memory growth";
            if (health.GrowthLevel == HealthLevel.Ok) return "minor managed growth";
            if (health.StutterLevel == HealthLevel.Ok) return "occasional frame spikes";

            return health.Overall == HealthLevel.Good ? "no clear problem" : "minor instability";
        }

        private static string MemoryText(HealthReport health)
        {
            // GrowthLevel fires earlier than MemoryLevel — managed memory can be
            // growing quickly while the absolute total is still under threshold. Show
            // the worse of the two so Status doesn't disagree with Diagnosis (which
            // already triggers on either signal).
            if (!string.IsNullOrEmpty(health.MemoryHint) && !IsStable(health.MemoryHint))
                return health.MemoryHint;
            return MemoryWorst(health) switch
            {
                HealthLevel.Good => "stable",
                HealthLevel.Ok => "growing",
                HealthLevel.Poor => "rising fast",
                HealthLevel.Unknown => "unknown",
                _ => throw new ArgumentOutOfRangeException(nameof(health), "Unhandled HealthLevel"),
            };
        }

        private static HealthLevel MemoryWorst(HealthReport health)
        {
            // Higher enum value = worse. Take the max of MemoryLevel and GrowthLevel.
            return (HealthLevel)System.Math.Max((int)health.MemoryLevel, (int)health.GrowthLevel);
        }

        private static bool IsStable(string hint)
            => string.Equals(hint, "Stable", System.StringComparison.OrdinalIgnoreCase);

        private static string TopMod(OverlaySnapshot snapshot)
        {
            if (snapshot?.TopMods == null || snapshot.TopMods.Length == 0)
                return null;

            var top = snapshot.TopMods[0];
            if (string.IsNullOrEmpty(top.ModName) || top.TotalMs < 1.0)
                return null;

            return top.ModName;
        }
    }
}
