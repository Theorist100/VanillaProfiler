using System;
using VanillaProfiler.Overlay.Modes;

namespace VanillaProfiler.Overlay
{
    public static class OverlayModeCatalog
    {
        private static readonly OverlayModeId[] s_Order =
        {
            OverlayModeId.Status,
            OverlayModeId.Diagnosis,
            OverlayModeId.Tips,
            OverlayModeId.Details,
            OverlayModeId.Engine,
            OverlayModeId.Hide,
        };

        public static readonly string[] SettingsLabels =
        {
            "Status",
            "Diag",
            "Tips",
            "Details",
            "Engine",
            "Hide",
        };

        public static OverlayModeDescriptor[] CreateDefaultModes()
            => new[]
            {
                new OverlayModeDescriptor(OverlayModeId.Status, new StatusMode()),
                new OverlayModeDescriptor(OverlayModeId.Diagnosis, new DiagnosisMode()),
                new OverlayModeDescriptor(OverlayModeId.Tips, new RecommendationsMode()),
                new OverlayModeDescriptor(OverlayModeId.Details, new DetailsMode()),
                new OverlayModeDescriptor(OverlayModeId.Engine, new EngineMode()),
                new OverlayModeDescriptor(OverlayModeId.Hide, new HiddenMode()),
            };

        public static OverlayModeId FromPersisted(int value)
        {
            var id = (OverlayModeId)value;
            return IsKnown(id) ? id : OverlayModeId.Status;
        }

        public static int ToPersisted(OverlayModeId id)
            => IsKnown(id) ? (int)id : (int)OverlayModeId.Status;

        public static int IndexFromPersisted(int value)
            => IndexOf(FromPersisted(value));

        public static int PersistedFromIndex(int index)
            => ToPersisted(IdAt(index));

        public static OverlayModeId IdAt(int index)
        {
            if (index < 0 || index >= s_Order.Length)
                return OverlayModeId.Status;
            return s_Order[index];
        }

        public static int IndexOf(OverlayModeId id)
        {
            for (int i = 0; i < s_Order.Length; i++)
                if (s_Order[i] == id) return i;
            return 0;
        }

        public static bool IsKnown(OverlayModeId id)
            => Array.IndexOf(s_Order, id) >= 0;
    }
}
