using VanillaProfiler.Diagnostics;

namespace VanillaProfiler.Overlay.Modes
{
    /// <summary>No-op mode. Frame is not drawn. Hotkeys and toasts still work.</summary>
    public sealed class HiddenMode : IOverlayMode
    {
        // Single word so it fits the bottom tab row alongside Status / Diagnosis /
        // Details. Breadcrumb on the previous mode reads "Ctrl+F9 → Hide".
        public string DisplayName => "Hide";
        public bool IsHidden => true;
        public float MeasureHeight(OverlaySnapshot snapshot) => 0;
        public void Draw(DrawContext ctx, OverlaySnapshot snapshot, HealthReport health) { }
    }
}
