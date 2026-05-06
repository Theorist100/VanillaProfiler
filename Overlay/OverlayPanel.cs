using UnityEngine;

namespace VanillaProfiler.Overlay
{
    /// <summary>
    /// Drawing primitives for the Classic Gold panel: background, gold border,
    /// left accent strip, separator line, section headers, body text rows.
    /// Stateless — operates on the theme + GUI context passed in.
    /// </summary>
    public static class OverlayPanel
    {
        public const float LINE_H = 24f;
        public const float PAD = 10f;
        public const float MARGIN = 12f;

        /// <summary>Draws background, gold border on all four sides, and gold accent strip on the left.</summary>
        public static void DrawFrame(OverlayTheme theme, Rect rect)
        {
            GUI.DrawTexture(rect, theme.BgTexture, ScaleMode.StretchToFill, true);

            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 1f), theme.BorderTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), theme.BorderTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.y, 1f, rect.height), theme.BorderTexture);
            GUI.DrawTexture(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), theme.BorderTexture);

            GUI.DrawTexture(new Rect(rect.x, rect.y, 3f, rect.height), theme.AccentTexture);
        }

        public static void DrawSeparator(DrawContext ctx)
        {
            var rect = new Rect(ctx.X, ctx.Y + 4f, ctx.Width - 6f, 1f);
            GUI.DrawTexture(rect, ctx.Theme.BorderTexture);
            ctx.Y += 8f;
        }

        public static void DrawSection(DrawContext ctx, string title)
        {
            ctx.Y += 4f;
            DrawLine(ctx, $"▸ {title}", ctx.Theme.SectionStyle);
        }

        public static void DrawLine(DrawContext ctx, string text, GUIStyle style)
        {
            GUI.Label(new Rect(ctx.X, ctx.Y, ctx.Width, LINE_H), text, style);
            ctx.Y += LINE_H;
        }

        public static void DrawHeader(DrawContext ctx, string title)
        {
            DrawLine(ctx, title, ctx.Theme.HeaderStyle);
            DrawSeparator(ctx);
        }

        /// <summary>
        /// Two-line header for cycle-mode panels: gold title on top, dim breadcrumb
        /// underneath ("View 1/4 — Ctrl+F9 → Diagnosis"). Tells the player the panel
        /// belongs to a cycle and what pressing Ctrl+F9 will switch to.
        /// </summary>
        public static void DrawHeaderWithCycle(DrawContext ctx, string title)
        {
            DrawLine(ctx, title, ctx.Theme.HeaderStyle);
            if (ctx.ModeCount > 0)
            {
                string next = string.IsNullOrEmpty(ctx.NextModeName) ? "next view" : ctx.NextModeName;
                string breadcrumb = $"View {ctx.ModeIndex + 1}/{ctx.ModeCount}  —  Ctrl+F9 → {next}";
                DrawLine(ctx, breadcrumb, ctx.Theme.DimStyle);
            }
            DrawSeparator(ctx);
        }
    }
}
