namespace VanillaProfiler.Overlay
{
    public sealed class OverlayModeDescriptor
    {
        public OverlayModeDescriptor(OverlayModeId id, IOverlayMode mode)
        {
            Id = id;
            Mode = mode;
        }

        public OverlayModeId Id { get; }
        public IOverlayMode Mode { get; }
        public string DisplayName => Mode.DisplayName;
    }
}
