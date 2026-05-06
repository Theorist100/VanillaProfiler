namespace VanillaProfiler
{
    /// <summary>
    /// Explicit profiler lifecycle. This replaces scattered boolean combinations so
    /// menu, loading, warmup and active-city states cannot contradict each other.
    /// </summary>
    public enum ProfilerLifecycleState
    {
        Unknown = 0,
        // Initial state — mod has loaded (IMod.OnLoad finished) but no game loading
        // event has fired yet. Splash screens before main menu sit in this state;
        // overlay renders nothing because there is no meaningful state to advertise.
        Initializing,
        NoCity,
        LoadingCity,
        Settling,
        Active,
    }
}
