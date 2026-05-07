using UnityEngine;

namespace VanillaProfiler.Overlay
{
    /// <summary>
    /// Small fixed-position pills used when the full overlay is not appropriate:
    /// HiddenMode (player toggled overlay off) and Standby (no save loaded yet).
    /// Both anchor to the top-right so the player learns one place to look.
    /// </summary>
    public static class OverlayBadges
    {
        /// <summary>"[Ctrl+F9] Profiler" — shown when the active mode is HiddenMode.</summary>
        public static void DrawHidden(OverlayTheme theme, float scale)
        {
            DrawBadge(theme, scale, "[Ctrl+F9] Profiler", theme.BodyStyle);
        }

        /// <summary>
        /// Neutral no-city state badge — main menu, editor, or pre-game screens.
        ///
        /// INTENTIONALLY UNCALLED. Paradox Mods loads every mod assembly during
        /// GameManager.Initialize, well before the player picks a save, so OnGUI runs
        /// throughout splash + main menu. Rendering anything during NoCity puts a profiler
        /// pill in front of every player on every launch — intrusive for the common case
        /// where the user only wants to play, not measure. The method stays defined so a
        /// future opt-in setting ("show standby badge in main menu") can wire it without a
        /// rewrite. Do NOT wire this from ProfilerOverlay.DrawScaled unconditionally —
        /// audit waves will keep flagging it as dead, but that's the correct state.
        /// </summary>
        public static void DrawStandby(OverlayTheme theme, float scale)
        {
            DrawBadge(theme, scale,
                "Profiler waiting for game  |  Ctrl+F8 settings",
                theme.DimStyle);
        }

        /// <summary>
        /// Bridge state between save-loaded and first measurement report. Tells the
        /// player the mod is alive and counting down to fresh data, rather than
        /// showing either a blank panel or stale numbers from a previous session.
        /// </summary>
        public static void DrawSettling(OverlayTheme theme, float scale)
        {
            DrawBadge(theme, scale,
                "Profiler settling — first report in a few seconds…",
                theme.BodyStyle);
        }

        private static void DrawBadge(OverlayTheme theme, float scale, string text, GUIStyle style)
        {
            var size = style.CalcSize(new GUIContent(text));
            float w = size.x + OverlayPanel.PAD * 2;
            float h = OverlayPanel.LINE_H + OverlayPanel.PAD;
            var (logicalW, _) = PanelLayout.LogicalSize(scale);
            float maxW = Mathf.Max(0f, logicalW - OverlayPanel.MARGIN * 2);
            if (w > maxW) w = maxW;
            if (w <= 0f) return;

            var rect = new Rect(Mathf.Max(OverlayPanel.MARGIN, logicalW - w - OverlayPanel.MARGIN), OverlayPanel.MARGIN, w, h);
            OverlayPanel.DrawFrame(theme, rect);
            GUI.Label(
                new Rect(rect.x + OverlayPanel.PAD, rect.y + OverlayPanel.PAD * 0.5f,
                    rect.width - OverlayPanel.PAD * 2, OverlayPanel.LINE_H),
                text, style);
        }
    }
}
