using System;
using System.Collections.Generic;
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
        private OverlaySnapshot? m_CachedSnapshot;
        private HealthReport? m_CachedHealth;
        private IReadOnlyList<Recommendation>? m_CachedPicks;

        public string DisplayName => "Tips";
        public bool IsHidden => false;

        public void InvalidateCache()
        {
            m_CachedSnapshot = null;
            m_CachedHealth = null;
            m_CachedPicks = null;
        }

        public float MeasureHeight(OverlaySnapshot snapshot)
        {
            var picks = Picks(snapshot, LastHealth());
            int lines = picks.Count == 0
                ? 1 + 1               // ALL CLEAR + body
                : 1 + (picks.Count * 3) + Math.Max(0, picks.Count - 1);
            return OverlayPanel.PAD * 2 + OverlayPanel.LINE_H * lines + 12f;
        }

        // The mode contract gives Draw both snapshot + health but MeasureHeight only
        // gets the snapshot. Health is recomputed each report cycle and exposed via
        // ProfilerHost; reading it here keeps the height in sync with what Draw uses.
        private static HealthReport LastHealth()
            => ProfilerHost.TryGetReadSurface()?.LastHealth ?? new HealthReport();

        public void Draw(DrawContext ctx, OverlaySnapshot snapshot, HealthReport health)
        {
            var picks = Picks(snapshot, health);
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

        private IReadOnlyList<Recommendation> Picks(OverlaySnapshot snapshot, HealthReport health)
        {
            if (m_CachedPicks != null
                && ReferenceEquals(m_CachedSnapshot, snapshot)
                && ReferenceEquals(m_CachedHealth, health))
                return m_CachedPicks;

            m_CachedSnapshot = snapshot;
            m_CachedHealth = health;
            m_CachedPicks = ProfilerHost.TryGetReadSurface()?.BuildRecommendations(health, snapshot)
                ?? Array.Empty<Recommendation>();
            return m_CachedPicks;
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
