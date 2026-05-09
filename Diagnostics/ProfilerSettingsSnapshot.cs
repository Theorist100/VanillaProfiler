namespace VanillaProfiler.Diagnostics
{
    /// <summary>
    /// Point-in-time settings view for runtime services. ProfilerSettings is
    /// immutable, so the snapshot can hold the settings reference directly.
    /// </summary>
    public sealed class ProfilerSettingsSnapshot
    {
        private readonly ProfilerSettings m_Settings;

        public ProfilerSettings Settings => m_Settings;

        private ProfilerSettingsSnapshot(ProfilerSettings settings)
        {
            m_Settings = settings ?? new ProfilerSettings();
        }

        public static ProfilerSettingsSnapshot From(ProfilerSettings settings)
            => new(settings);
    }
}
