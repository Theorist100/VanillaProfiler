namespace VanillaProfiler.Overlay
{
    /// <summary>Screen corner the main panel snaps to when no manual drag is set.</summary>
    public enum Anchor
    {
        TopLeft = 0,
        TopRight = 1,
        BottomRight = 2,
        BottomLeft = 3,
    }

    public static class AnchorExtensions
    {
        /// <summary>Compact label for the in-panel "Snap: ..." button.</summary>
        public static string ShortName(this Anchor a) => a switch
        {
            Anchor.TopLeft => "Top-L",
            Anchor.TopRight => "Top-R",
            Anchor.BottomRight => "Bot-R",
            Anchor.BottomLeft => "Bot-L",
            _ => "?",
        };

        /// <summary>Cycle order matches Ctrl+F12: TL → TR → BR → BL → TL.</summary>
        public static Anchor Cycle(this Anchor a) => (Anchor)(((int)a + 1) % 4);
    }
}
