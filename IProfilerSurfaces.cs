using System;
using VanillaProfiler.Diagnostics;

namespace VanillaProfiler
{
    /// <summary>
    /// Minimal API exposed to Harmony patches and ECS counter systems. Keeping this
    /// separate from export/UI/lifecycle methods makes hot-path coupling explicit.
    /// </summary>
    public interface IProfilerPatchSurface
    {
        bool ShouldProfileVanillaSystems { get; }

        bool IsVanillaSystemPatched(Type type);
        void OnSimTick();
        void OnFrame();
        void RecordSystem(string name, long selfTicks, long inclusiveTicks, bool isVanilla, string? modName = null);
        void RecordPatchedVanilla(string name, long selfTicks, long inclusiveTicks);
        void RecordPhase(string name, long ticks);
    }

    /// <summary>
    /// Non-patch surface used by overlay, exports, lifecycle callbacks, and logging.
    /// It intentionally does not expose hot-path recording methods.
    /// </summary>
    public interface IProfilerReadSurface
    {
        OverlaySnapshot? LastSnapshot { get; }
        HealthReport? LastHealth { get; }
        MemoryHistory MemoryHistory { get; }
        FpsSparkline FpsSparkline { get; }
        SpikeScreenshot SpikeScreenshots { get; }
        GraphicsSettingsProbe GraphicsSettings { get; }
        RecommendationEngine Recommendations { get; }
        ProfilerLifecycleState LifecycleState { get; }

        void ForceReport();
        void SetGameLoaded(bool gameLoaded);
        void BeginLoading(bool loadsCity);
        void LogInfo(string msg);
        void LogWarn(string msg);
        void LogError(string msg);
    }
}
