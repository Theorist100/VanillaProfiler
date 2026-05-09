namespace VanillaProfiler.Diagnostics
{
    public static class HealthClassifier
    {
        public static HealthReport Classify(
            OverlaySnapshot snap,
            MemoryHistory mem,
            double simPhaseMs,
            double renderPhaseMs)
        {
            var fpsLevel = HealthLevelClassifier.ClassifyFps(snap.AvgFps);
            var stutterLevel = HealthLevelClassifier.ClassifyStutter(
                snap.MaxFrameMs,
                snap.Spikes30fps,
                snap.WindowSeconds);
            var memoryLevel = HealthLevelClassifier.ClassifyMemory(snap.ManagedDeltaMB, mem);
            var growthLevel = HealthLevelClassifier.ClassifyGrowthRate(snap.ManagedGrowthMBperSec);

            var overall = HealthLevelClassifier.Worst(
                fpsLevel,
                stutterLevel,
                memoryLevel,
                growthLevel);

            return new HealthReport(
                fpsLevel,
                stutterLevel,
                memoryLevel,
                growthLevel,
                overall,
                HealthLevelClassifier.BuildMemoryHint(mem),
                RenderBottleneckClassifier.Classify(snap, simPhaseMs, renderPhaseMs));
        }
    }
}
