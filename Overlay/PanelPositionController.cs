using System;
using UnityEngine;

namespace VanillaProfiler.Overlay
{
    /// <summary>
    /// Owns main overlay panel position. Anchor snaps and manual drags update the
    /// same persisted PanelX/Y fields through one controller instead of scattering
    /// rect-diff and save scheduling logic through ProfilerOverlay.
    /// </summary>
    public sealed class PanelPositionController
    {
        private readonly Func<(float X, float Y)> m_LoadPosition;
        private readonly Action<float, float> m_StorePosition;
        private readonly Action m_ScheduleSave;
        private readonly float m_MinVisible;

        private bool m_Manual;

        public PanelPositionController(
            Rect initialRect,
            float minVisible,
            Func<(float X, float Y)> loadPosition,
            Action<float, float> storePosition,
            Action scheduleSave)
        {
            Rect = initialRect;
            m_MinVisible = minVisible;
            m_LoadPosition = loadPosition;
            m_StorePosition = storePosition;
            m_ScheduleSave = scheduleSave;
        }

        public Rect Rect { get; private set; }

        public void Load()
        {
            var (x, y) = m_LoadPosition();
            m_Manual = x >= 0f && y >= 0f;
            if (!m_Manual) return;

            var rect = Rect;
            rect.x = x;
            rect.y = y;
            Rect = rect;
        }

        public void ApplyLayout(Anchor anchor, float width, float height, float scale)
        {
            Rect rect = m_Manual
                ? Rect
                : PanelLayout.ComputeAnchorRect(anchor, width, height, scale);
            rect.width = width;
            rect.height = height;
            PanelLayout.ClampInsideLogicalScreen(ref rect, scale, m_MinVisible);
            Rect = rect;
        }

        public void BeginWindow() { /* kept for caller symmetry; no per-frame state */ }

        public void MarkDragIfActive(Rect dragHandle)
        {
            // Kept for source-compat with the caller; drag detection now lives in
            // CompleteWindow and reads the rect-delta GUI.DragWindow produced.
            _ = dragHandle;
        }

        public void CompleteWindow(Rect windowRect, Vector2 before, float scale)
        {
            // Detect drag from the GUI.Window return rect: GUI.DragWindow only
            // changes the rect when the player actually moves the window. Compare
            // BEFORE applying ClampInsideLogicalScreen so a programmatic clamp
            // (resolution change, scale switch) cannot impersonate a drag.
            bool dragged = !Mathf.Approximately(before.x, windowRect.x)
                        || !Mathf.Approximately(before.y, windowRect.y);

            Rect = windowRect;
            var rect = Rect;
            PanelLayout.ClampInsideLogicalScreen(ref rect, scale, m_MinVisible);
            Rect = rect;

            if (!dragged) return;

            m_Manual = true;
            m_StorePosition(rect.x, rect.y);
            m_ScheduleSave();
        }

        public void SnapToAnchor()
        {
            m_Manual = false;
            m_StorePosition(-1f, -1f);
            m_ScheduleSave();
        }
    }
}
