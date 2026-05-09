namespace VanillaProfiler
{
    /// <summary>
    /// Minimal API exposed to Harmony patches and ECS counter systems. Keeping this
    /// separate from export/UI/lifecycle methods makes hot-path coupling explicit.
    /// </summary>
    public interface IProfilerHotPath
    {
        bool ShouldProfileVanillaSystems { get; }

        void OnSimTick();
        void OnFrame();
        void RecordSystem(string name, long ticks, bool isVanilla, string? modName = null);
        void RecordPatchedVanilla(string name, long ticks);
        void RecordPhase(string name, long ticks);
    }
}
