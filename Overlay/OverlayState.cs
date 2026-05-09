using UnityEngine;

namespace VanillaProfiler.Overlay
{
    /// <summary>
    /// Mutable UI navigation state separated from drawing and persistence.
    /// </summary>
    public sealed class OverlayState
    {
        public int ModeIndex { get; private set; }
        public Anchor Anchor { get; private set; } = Anchor.TopLeft;

        public void ApplyStartup(int defaultMode, int anchor, int modeCount)
        {
            SetMode(defaultMode, modeCount);
            Anchor = (Anchor)Mathf.Clamp(anchor, 0, 3);
        }

        public void SetMode(int index, int modeCount)
        {
            if (modeCount <= 0)
            {
                ModeIndex = 0;
                return;
            }
            ModeIndex = Mathf.Clamp(index, 0, modeCount - 1);
        }

        public void CycleMode(int modeCount)
        {
            if (modeCount <= 0) return;
            SetMode(ModeIndex + 1, modeCount);
        }

        public void SetAnchor(Anchor anchor)
        {
            Anchor = anchor;
        }

        public void CycleAnchor()
        {
            Anchor = (Anchor)(((int)Anchor + 1) % 4);
        }
    }
}
