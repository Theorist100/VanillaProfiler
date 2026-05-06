using System;
using UnityEngine;
using VanillaProfiler.Diagnostics;

namespace VanillaProfiler.Overlay.Modes
{
    /// <summary>
    /// "What can I actually change right now" screen. Shows a short ordered list of
    /// concrete fixes (graphics settings, NVIDIA driver, top-mod isolation) picked
    /// by RecommendationEngine. Critical entries first, then Suggested, then Info.
    /// </summary>
    public sealed class RecommendationsMode : IOverlayMode
    {
        public string DisplayName => "Tips";
        public bool IsHidden => false;

        public float MeasureHeight(OverlaySnapshot snapshot)
        {
            // Header (2) + section (1) + N entries × 3 lines + (N-1) spacers between.
            // RecommendationEngine.Build is a pure function whose result is fully
            // determined by the cached probe + snapshot, so calling it twice (here
            // and in Draw) returns the same list with no extra side effects.
            var picks = RecommendationEngine.Build(LastHealth(snapshot), snapshot);
            int lines = picks.Count == 0
                ? 2 + 1 + 1               // header + ALL CLEAR + body
                : 2 + 1 + (picks.Count * 3) + Math.Max(0, picks.Count - 1);
            return OverlayPanel.PAD * 2 + OverlayPanel.LINE_H * lines + 12f;
        }

        // The mode contract gives Draw both snapshot + health but MeasureHeight only
        // gets the snapshot. Health is recomputed each report cycle and exposed via
        // ProfilerHost; reading it here keeps the height in sync with what Draw uses.
        private static HealthReport LastHealth(OverlaySnapshot snapshot)
            => ProfilerHost.TryGet()?.LastHealth;

        public void Draw(DrawContext ctx, OverlaySnapshot snapshot, HealthReport health)
        {
            OverlayPanel.DrawHeaderWithCycle(ctx, "VANILLA PROFILER  >  RECOMMENDATIONS");

            var picks = RecommendationEngine.Build(health, snapshot);
            if (picks.Count == 0)
            {
                OverlayPanel.DrawSection(ctx, "ALL CLEAR");
                OverlayPanel.DrawLine(ctx, "Nothing to change — performance is healthy.", ctx.Theme.BodyStyle);
                return;
            }

            OverlayPanel.DrawSection(ctx, "TRY THESE — TOP TO BOTTOM");
            for (int i = 0; i < picks.Count; i++)
            {
                if (i > 0) OverlayPanel.DrawLine(ctx, "", ctx.Theme.BodyStyle);
                DrawEntry(ctx, i + 1, picks[i]);
            }
        }

        private static void DrawEntry(DrawContext ctx, int index, Recommendation rec)
        {
            var titleStyle = StyleForLevel(ctx, rec.Level);
            OverlayPanel.DrawLine(ctx, $"{index}. {Truncate(rec.Title, 52)}", titleStyle);
            if (!string.IsNullOrEmpty(rec.Action))
                OverlayPanel.DrawLine(ctx, "   " + Truncate(rec.Action, 52), ctx.Theme.BodyStyle);
            if (!string.IsNullOrEmpty(rec.Reason))
                OverlayPanel.DrawLine(ctx, "   " + Truncate(rec.Reason, 52), ctx.Theme.DimStyle);
        }

        private static GUIStyle StyleForLevel(DrawContext ctx, RecommendationLevel level) => level switch
        {
            RecommendationLevel.Critical => ctx.Theme.StyleForHealth(HealthLevel.Poor),
            RecommendationLevel.Suggested => ctx.Theme.StyleForHealth(HealthLevel.Ok),
            RecommendationLevel.Info => ctx.Theme.StyleForHealth(HealthLevel.Good),
            RecommendationLevel.Unknown => ctx.Theme.BodyStyle,
            _ => ctx.Theme.BodyStyle,
        };

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max - 1) + "…";
        }
    }
}
