using UnityEngine;

namespace VanillaProfiler.Overlay
{
    /// <summary>
    /// Mutable UI navigation state separated from drawing and persistence.
    /// </summary>
    public sealed class OverlayState
    {
        public int ModeIndex { get; private set; }
        public OverlayModeId ModeId { get; private set; } = OverlayModeId.Status;
        public Anchor Anchor { get; private set; } = Anchor.TopLeft;

        public void Initialize(int persistedDefaultMode, int anchor, OverlayModeDescriptor[] modes)
        {
            SetMode(OverlayModeCatalog.FromPersisted(persistedDefaultMode), modes);
            Anchor = (Anchor)Mathf.Clamp(anchor, 0, 3);
        }

        public void SetMode(OverlayModeId id, OverlayModeDescriptor[] modes)
        {
            if (modes == null || modes.Length <= 0)
            {
                ModeId = OverlayModeId.Status;
                ModeIndex = 0;
                return;
            }

            for (int i = 0; i < modes.Length; i++)
            {
                if (modes[i].Id != id) continue;
                ModeId = id;
                ModeIndex = i;
                return;
            }

            ModeId = modes[0].Id;
            ModeIndex = 0;
        }

        public void SetModeByIndex(int index, OverlayModeDescriptor[] modes)
        {
            if (modes == null || modes.Length <= 0)
            {
                ModeId = OverlayModeId.Status;
                ModeIndex = 0;
                return;
            }

            ModeIndex = Mathf.Clamp(index, 0, modes.Length - 1);
            ModeId = modes[ModeIndex].Id;
        }

        public void CycleMode(OverlayModeDescriptor[] modes)
        {
            if (modes == null || modes.Length <= 0) return;
            SetModeByIndex((ModeIndex + 1) % modes.Length, modes);
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
