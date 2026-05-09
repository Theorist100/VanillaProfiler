using UnityEngine;

namespace VanillaProfiler.Overlay
{
    /// <summary>
    /// Brief bottom-of-screen status message. Disappears after DURATION_S.
    /// Encapsulates its own state so multiple call sites can fire toasts independently.
    /// </summary>
    public sealed class Toast
    {
        private const float DURATION_S = 3.0f;
        private const float WIDTH = 440f;
        private const int WINDOW_ID = 0xC1F1C2;

        private float m_HideAt;
        private string m_Text = string.Empty;
        private OverlayTheme m_Theme = null!;
        private Rect m_WindowRect;

        public void Show(string message)
        {
            m_Text = message;
            m_HideAt = Time.realtimeSinceStartup + DURATION_S;
        }

        public void Draw(OverlayTheme theme, float scale)
        {
            if (Time.realtimeSinceStartup > m_HideAt || string.IsNullOrEmpty(m_Text)) return;
            theme.EnsureInitialized();

            // Position in logical (pre-scale) coordinates so the toast keeps a fixed
            // physical size regardless of GUI.matrix scale set by ProfilerOverlay.
            float safeScale = Mathf.Max(scale, 0.01f);
            float logicalW = Screen.width / safeScale;
            float logicalH = Screen.height / safeScale;
            float h = OverlayPanel.LINE_H + OverlayPanel.PAD * 2;
            float margin = OverlayPanel.MARGIN;
            float width = Mathf.Min(WIDTH, Mathf.Max(0f, logicalW - margin * 2f));
            if (width <= 0f) return;
            var rect = new Rect(Mathf.Max(margin, (logicalW - width) * 0.5f), logicalH - h - 40f, width, h);
            m_Theme = theme;
            m_WindowRect = rect;
            GUI.Window(WINDOW_ID, rect, DrawWindow, GUIContent.none, GUIStyle.none);
            GUI.BringWindowToFront(WINDOW_ID);
        }

        private void DrawWindow(int windowId)
        {
            var rect = new Rect(0f, 0f, m_WindowRect.width, m_WindowRect.height);
            OverlayPanel.DrawFrame(m_Theme, rect);
            GUI.Label(
                new Rect(OverlayPanel.PAD, OverlayPanel.PAD,
                    rect.width - OverlayPanel.PAD * 2, OverlayPanel.LINE_H),
                m_Text, m_Theme.BadgeStyle);
        }
    }
}
