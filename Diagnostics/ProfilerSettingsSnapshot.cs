namespace VanillaProfiler.Diagnostics
{
    /// <summary>
    /// Point-in-time settings view for runtime services. The snapshot owns a clone
    /// of ProfilerSettings, so adding a persisted setting does not require adding
    /// another field here.
    /// </summary>
    public sealed class ProfilerSettingsSnapshot
    {
        private readonly ProfilerSettings m_Settings;

        public ProfilerSettings Settings => m_Settings;

        private ProfilerSettingsSnapshot(ProfilerSettings settings)
        {
            m_Settings = (settings ?? new ProfilerSettings()).Clone();
        }

        public static ProfilerSettingsSnapshot From(ProfilerSettings settings)
            => new(settings);
    }
}
