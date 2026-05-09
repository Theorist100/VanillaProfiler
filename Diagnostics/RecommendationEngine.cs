using System;
using System.Collections.Generic;

namespace VanillaProfiler.Diagnostics
{
    /// <summary>
    /// Picks a short ordered list of player-actionable fixes based on the latest
    /// HealthReport and OverlaySnapshot. Pure function: no I/O, no statics, no
    /// no I/O after the lazy graphics probe, and cheap enough for overlay layout/draw.
    ///
    /// Recommendations are sourced from Paradox's official "Optimizing Performance"
    /// guide and from CS2 community testing (PC Gamer / cs2performance.com /
    /// switchbladegaming.com). Order: Critical first, then Suggested, then Info.
    /// </summary>
    public sealed class RecommendationEngine
    {
        private const int MAX_RECOMMENDATIONS = 6;
        private readonly GraphicsSettingsProbe m_GraphicsSettings;

        public RecommendationEngine(GraphicsSettingsProbe graphicsSettings)
        {
            m_GraphicsSettings = graphicsSettings ?? throw new ArgumentNullException(nameof(graphicsSettings));
        }

        public IReadOnlyList<Recommendation> Build(HealthReport health, OverlaySnapshot snap)
        {
            var list = new List<Recommendation>(MAX_RECOMMENDATIONS);
            if (health == null || snap == null) return list;

            // Lazy probe — first time the player opens the Tips screen, read what's
            // currently set so we don't suggest fixes the player has already applied.
            m_GraphicsSettings.EnsureProbed();
            var probed = m_GraphicsSettings.State;

            AddCriticalRecommendations(list, health, probed);

            // Suggested-tier — render-related mild OR FPS poor without strong specifics.
            bool perfTrouble = health.Overall == HealthLevel.Poor || health.Overall == HealthLevel.Ok;
            bool renderHint = health.Bottleneck == BottleneckKind.GpuBound
                || health.Bottleneck == BottleneckKind.CpuRenderBound;
            AddRenderSuggestions(list, probed, perfTrouble || renderHint);

            // Mod isolation hint — only when a mod actually stands out by CPU.
            // Skip VanillaProfiler itself; suggesting "disable the profiler" defeats
            // the point of the screen the player is reading.
            AddModIsolationHint(list, snap);

            // Info-tier — universally helpful safety nets, always last.
            AddInfoRecommendations(list, probed, perfTrouble);

            if (list.Count > MAX_RECOMMENDATIONS)
                list.RemoveRange(MAX_RECOMMENDATIONS, list.Count - MAX_RECOMMENDATIONS);
            return list;
        }

        private static void AddCriticalRecommendations(
            List<Recommendation> list, HealthReport health, GraphicsSettingsState probed)
        {
            if ((health.Bottleneck == BottleneckKind.GpuBound
                    || health.Bottleneck == BottleneckKind.CpuRenderBound)
                && health.RenderSeverity == RenderSeverityLevel.Severe)
            {
                AddLatencyRecommendation(list, health, probed);
                AddVolumetricsRecommendation(list, health, probed);
            }

            if (health.MemoryLevel == HealthLevel.Poor || health.GrowthLevel == HealthLevel.Poor)
            {
                list.Add(new Recommendation
                {
                    Level = RecommendationLevel.Critical,
                    Title = "Restart the game",
                    Action = "Save the city, quit to desktop, relaunch.",
                    Reason = "Managed memory is rising — clears caches.",
                });
            }
        }

        private static void AddLatencyRecommendation(
            List<Recommendation> list, HealthReport health, GraphicsSettingsState probed)
        {
            if (health.RenderCause != RenderCause.GpuUnderutilizedByCpuRender) return;
            if (!probed.MaxFrameLatency.HasValue || probed.MaxFrameLatency.Value >= 3) return;
            list.Add(new Recommendation
            {
                Level = RecommendationLevel.Critical,
                Title = "Raise Max Frame Latency to 3",
                Action = "Settings -> Graphics -> Max Frame Latency = 3",
                Reason = $"GPU sits at {health.GpuBusyPercent:F0}% — CPU render is the gate.",
            });
        }

        private static void AddVolumetricsRecommendation(
            List<Recommendation> list, HealthReport health, GraphicsSettingsState probed)
        {
            if (probed.VolumetricsEnabled != true) return;
            string reason = health.RenderCause == RenderCause.PresentWait
                ? "Heavy GPU effect while the CPU is waiting on present."
                : "Heavy render effect that can increase CPU submission and GPU cost.";
            list.Add(new Recommendation
            {
                Level = RecommendationLevel.Critical,
                Title = "Disable Volumetrics",
                Action = "Settings -> Graphics -> Volumetrics = Off",
                Reason = reason,
            });
        }

        private static void AddRenderSuggestions(
            List<Recommendation> list, GraphicsSettingsState probed, bool shouldSuggest)
        {
            if (!shouldSuggest) return;
            AddLodRecommendation(list, probed);
            AddToggleRecommendation(list, probed.DepthOfFieldEnabled, "Disable Depth of Field",
                "Settings -> Graphics -> Depth of Field = Off",
                "Sharper image and a measurable FPS gain.");
            AddToggleRecommendation(list, probed.MotionBlurEnabled, "Disable Motion Blur",
                "Settings -> Graphics -> Motion Blur = Off",
                "Hides stutter at low FPS, no benefit at high FPS.");
        }

        private static void AddLodRecommendation(List<Recommendation> list, GraphicsSettingsState probed)
        {
            if (!probed.LevelOfDetail.HasValue || probed.LevelOfDetail.Value <= 0.75f) return;
            list.Add(new Recommendation
            {
                Level = RecommendationLevel.Suggested,
                Title = "Lower Level of Detail to 0.75",
                Action = "Settings -> Graphics -> Level of Detail = 0.75",
                Reason = $"Currently {probed.LevelOfDetail.Value:F2} — single most impactful CS2 setting.",
            });
        }

        private static void AddToggleRecommendation(
            List<Recommendation> list, bool? enabled, string title, string action, string reason)
        {
            if (enabled != true) return;
            list.Add(new Recommendation
            {
                Level = RecommendationLevel.Suggested,
                Title = title,
                Action = action,
                Reason = reason,
            });
        }

        private static void AddModIsolationHint(List<Recommendation> list, OverlaySnapshot snap)
        {
            var heaviest = HeaviestNonProfilerMod(snap);
            if (heaviest == null) return;
            list.Add(new Recommendation
            {
                Level = RecommendationLevel.Suggested,
                Title = "Test without your top mod",
                Action = $"Disable '{heaviest.Value.ModName}' and re-check.",
                Reason = $"It consumes {heaviest.Value.Percent:P0} of the report window ({heaviest.Value.TotalMs:F0} ms self-time).",
            });
        }

        private static void AddInfoRecommendations(
            List<Recommendation> list, GraphicsSettingsState probed, bool perfTrouble)
        {
            if (list.Count < MAX_RECOMMENDATIONS && probed.IsFullscreenWindowed == false)
            {
                list.Add(new Recommendation
                {
                    Level = RecommendationLevel.Info,
                    Title = "Use Fullscreen Windowed",
                    Action = "Settings -> Display -> Fullscreen Windowed",
                    Reason = "Performs better than Exclusive in CS2.",
                });
            }

            if (list.Count < MAX_RECOMMENDATIONS && perfTrouble && probed.TerrainShadowsEnabled == true)
            {
                list.Add(new Recommendation
                {
                    Level = RecommendationLevel.Info,
                    Title = "Disable Terrain Casts Shadows",
                    Action = "Settings -> Graphics (Advanced) -> Terrain Shadows = Off",
                    Reason = "Large terrain footprint — shadows are expensive.",
                });
            }
        }

        private const double HEAVY_MOD_PERCENT = 0.10;
        private const string PROFILER_MOD_NAME = "VanillaProfiler";

        private static (string ModName, double TotalMs, double Percent)? HeaviestNonProfilerMod(OverlaySnapshot snap)
        {
            if (snap.TopMods == null) return null;
            double windowMs = snap.WindowSeconds * 1000.0;
            if (windowMs <= 0 || double.IsNaN(windowMs) || double.IsInfinity(windowMs)) return null;

            for (int i = 0; i < snap.TopMods.Count; i++)
            {
                var entry = snap.TopMods[i];
                if (string.IsNullOrEmpty(entry.ModName)) continue;
                if (string.Equals(entry.ModName, PROFILER_MOD_NAME, StringComparison.Ordinal)) continue;
                double percent = entry.TotalMs / windowMs;
                if (percent < HEAVY_MOD_PERCENT) continue;
                return (entry.ModName, entry.TotalMs, percent);
            }
            return null;
        }
    }
}
