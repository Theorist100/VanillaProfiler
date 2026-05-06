using VanillaProfiler.Diagnostics;

namespace VanillaProfiler.Overlay
{
    /// <summary>
    /// Contract for an overlay rendering mode. Adding a new mode is a matter of
    /// implementing this interface — no other file needs to change (OCP).
    /// </summary>
    public interface IOverlayMode
    {
        /// <summary>Display name shown in the header line.</summary>
        string DisplayName { get; }

        /// <summary>True if the mode draws nothing (Hidden) — caller skips DrawFrame.</summary>
        bool IsHidden { get; }

        /// <summary>Required panel height for the given snapshot. Caller positions the rect.</summary>
        float MeasureHeight(OverlaySnapshot snapshot);

        /// <summary>Draws the mode contents inside the panel. Frame and background are already drawn.</summary>
        void Draw(DrawContext ctx, OverlaySnapshot snapshot, HealthReport health);
    }
}
