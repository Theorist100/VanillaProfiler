namespace VanillaProfiler
{
#pragma warning disable CA1815
    public readonly struct ProfiledSystemIdentity
    {
        public ProfiledSystemIdentity(string name, bool isVanilla, string modName)
        {
            Name = name ?? string.Empty;
            IsVanilla = isVanilla;
            ModName = modName ?? string.Empty;
        }

        public string Name { get; }
        public bool IsVanilla { get; }
        public string ModName { get; }
    }

    public readonly struct ProfiledSystemMeasurement
    {
        public ProfiledSystemMeasurement(ProfiledSystemIdentity identity, long selfTicks, long inclusiveTicks)
        {
            Identity = identity;
            SelfTicks = selfTicks;
            InclusiveTicks = inclusiveTicks;
        }

        public ProfiledSystemIdentity Identity { get; }
        public long SelfTicks { get; }
        public long InclusiveTicks { get; }
    }

    public readonly struct PhaseMeasurement
    {
        public PhaseMeasurement(string name, long ticks)
        {
            Name = name ?? string.Empty;
            Ticks = ticks;
        }

        public string Name { get; }
        public long Ticks { get; }
    }
#pragma warning restore CA1815
}
