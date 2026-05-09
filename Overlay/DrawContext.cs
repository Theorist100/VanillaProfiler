namespace VanillaProfiler.Overlay
{
    /// <summary>
    /// Mutable cursor + theme bundle passed to mode renderers.
    /// Renderers advance Y as they draw; this keeps the call signatures small.
    /// </summary>
    public sealed class DrawContext
    {
        public OverlayTheme Theme { get; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; }

        // Mode position in the cycle, used by modes to render a "1/4 next: Diagnosis"
        // breadcrumb so the player knows Ctrl+F9 leads somewhere meaningful.
        public int ModeIndex { get; set; }
        public int ModeCount { get; set; }
        public string NextModeName { get; set; } = string.Empty;
        public int SparklineWidth { get; set; }
        public string FpsSparkline { get; set; } = string.Empty;

        public DrawContext(OverlayTheme theme, float x, float y, float width)
        {
            Theme = theme;
            X = x;
            Y = y;
            Width = width;
        }
    }
}
