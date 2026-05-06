using UnityEngine;
using VanillaProfiler.Diagnostics;

namespace VanillaProfiler.Overlay
{
    /// <summary>
    /// Shared positioning helpers for IMGUI panels rendered under GUI.matrix scale.
    /// Stateless — operates on the screen dimensions and supplied parameters.
    /// </summary>
    public static class PanelLayout
    {
        /// <summary>UI scale derived from settings (manual override) or screen height (auto).</summary>
        public static float ResolveScale()
        {
            var s = SettingsStore.Current;
            if (s.UiScale > 0f) return Mathf.Clamp(s.UiScale, 0.75f, 3f);
            // Auto: 1080p = 1x, 1440p ≈ 1.33x, 4K = 2x. Let smaller displays
            // shrink to the same lower bound as manual scale so panels still fit.
            return Mathf.Clamp(Screen.height / 1080f, 0.75f, 3f);
        }

        /// <summary>Pre-scale ("logical") screen size — what GUI.matrix sees.</summary>
        public static (float w, float h) LogicalSize(float scale)
        {
            float safeScale = Mathf.Max(scale, 0.01f);
            return (Screen.width / safeScale, Screen.height / safeScale);
        }

        /// <summary>Compute panel rect snapped to a screen corner with margin.</summary>
        public static Rect ComputeAnchorRect(Anchor anchor, float width, float height, float scale)
        {
            var (logicalW, logicalH) = LogicalSize(scale);
            float left = OverlayPanel.MARGIN;
            float right = logicalW - width - OverlayPanel.MARGIN;
            float top = OverlayPanel.MARGIN;
            float bottom = logicalH - height - OverlayPanel.MARGIN;

            float x = anchor switch
            {
                Anchor.TopLeft or Anchor.BottomLeft => left,
                Anchor.TopRight or Anchor.BottomRight => right,
                _ => left,
            };
            float y = anchor switch
            {
                Anchor.TopLeft or Anchor.TopRight => top,
                Anchor.BottomLeft or Anchor.BottomRight => bottom,
                _ => top,
            };
            return new Rect(x, y, width, height);
        }

        /// <summary>
        /// Keep at least <paramref name="minVisible"/> pixels of the panel on-screen so
        /// a user can't drag the window into oblivion and lose access to it.
        /// </summary>
        public static void ClampInsideLogicalScreen(ref Rect rect, float scale, float minVisible)
        {
            var (logicalW, logicalH) = LogicalSize(scale);
            rect.x = Mathf.Clamp(rect.x, -rect.width + minVisible, logicalW - minVisible);
            rect.y = Mathf.Clamp(rect.y, 0f, logicalH - minVisible);
        }
    }
}
