namespace VanillaProfiler.Diagnostics
{
    /// <summary>
    /// Snapshot of graphics-related settings the player can change. Populated lazily
    /// on first access (see <see cref="GraphicsSettingsProbe.EnsureProbed"/>) so the
    /// hot path is never touched. Every field is tri-state — null means "we couldn't
    /// read it", so concrete setting recommendations are skipped instead of guessed.
    /// </summary>
    public sealed class GraphicsSettingsState
    {
        public bool? IsFullscreenWindowed { get; internal set; }
        public bool? MotionBlurEnabled { get; internal set; }
        public bool? DepthOfFieldEnabled { get; internal set; }
        public bool? VolumetricsEnabled { get; internal set; }
        public bool? TerrainShadowsEnabled { get; internal set; }
        public float? LevelOfDetail { get; internal set; }     // 0.10 - 1.00; Paradox recommends 0.75
        public int? MaxFrameLatency { get; internal set; }     // 1-3, CS2's "pre-rendered frames" setting
        public bool ProbeAttempted { get; internal set; }
    }

    /// <summary>
    /// Instance-owned cache for current CS2 graphics settings. The reflection reader
    /// is intentionally isolated from this cache so recommendation/lifecycle code
    /// depends on one cheap read surface, not on Harmony reflection details.
    /// </summary>
    public sealed class GraphicsSettingsProbe
    {
        private GraphicsSettingsState? m_State;

        public GraphicsSettingsState State
        {
            get
            {
                EnsureProbed();
                return m_State!;
            }
        }

        public void EnsureProbed()
        {
            if (m_State != null) return;
            var state = new GraphicsSettingsState { ProbeAttempted = true };
            GraphicsSettingsReflectionReader.ReadInto(state);
            m_State = state;
            ModLog.Info(
                "Graphics probe: " +
                $"FullscreenWindowed={Fmt(state.IsFullscreenWindowed)} " +
                $"MotionBlur={Fmt(state.MotionBlurEnabled)} " +
                $"DepthOfField={Fmt(state.DepthOfFieldEnabled)} " +
                $"Volumetrics={Fmt(state.VolumetricsEnabled)} " +
                $"TerrainShadows={Fmt(state.TerrainShadowsEnabled)} " +
                $"LOD={(state.LevelOfDetail.HasValue ? state.LevelOfDetail.Value.ToString("F2") : "?")} " +
                $"MaxFrameLatency={(state.MaxFrameLatency.HasValue ? state.MaxFrameLatency.Value.ToString() : "?")}");
        }

        /// <summary>Force a re-read on next access. Call after the player likely changed settings.</summary>
        public void Invalidate()
        {
            m_State = null;
        }

        private static string Fmt(bool? v) => v.HasValue ? (v.Value ? "on" : "off") : "?";
    }
}
