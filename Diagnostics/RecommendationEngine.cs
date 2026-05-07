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
    public static class RecommendationEngine
    {
        private const int MAX_RECOMMENDATIONS = 6;

        public static List<Recommendation> Build(HealthReport health, OverlaySnapshot snap)
        {
            var list = new List<Recommendation>(MAX_RECOMMENDATIONS);
            if (health == null || snap == null) return list;

            // Lazy probe — first time the player opens the Tips screen, read what's
            // currently set so we don't suggest fixes the player has already applied.
            GraphicsSettingsProbe.EnsureProbed();
            var probed = GraphicsSettingsProbe.State;

            // Critical-tier — fired only when multiple signals confirm the symptom.
            if (health.Bottleneck == BottleneckKind.RenderBound && health.RenderSeverity == RenderBoundSeverity.Severe)
            {
                if (probed.MaxFrameLatency.HasValue && probed.MaxFrameLatency.Value < 3)
                {
                    list.Add(new Recommendation
                    {
                        Level = RecommendationLevel.Critical,
                        Title = "Raise Max Frame Latency to 3",
                        Action = "Settings -> Graphics -> Max Frame Latency = 3",
                        Reason = health.GpuUnderutilized
                            ? $"GPU sits at {health.GpuBoundPercent:F0}% — CPU render is the gate."
                            : $"Currently {probed.MaxFrameLatency.Value} — locks GPU to CPU each frame.",
                    });
                }
                if (probed.VolumetricsEnabled != false)
                {
                    list.Add(new Recommendation
                    {
                        Level = RecommendationLevel.Critical,
                        Title = "Disable Volumetrics",
                        Action = "Settings -> Graphics -> Volumetrics = Off",
                        Reason = "Heaviest single GPU/CPU render cost.",
                    });
                }
            }

            // Memory pressure trumps render advice — restart helps cleanly.
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

            // Suggested-tier — render bound mild OR FPS poor without strong specifics.
            bool perfTrouble = health.Overall == HealthLevel.Poor || health.Overall == HealthLevel.Ok;
            bool renderHint = health.Bottleneck == BottleneckKind.RenderBound;
            if (perfTrouble || renderHint)
            {
                // LOD: skip the suggestion if the player is already at or below 0.75.
                if (!probed.LevelOfDetail.HasValue || probed.LevelOfDetail.Value > 0.75f)
                {
                    list.Add(new Recommendation
                    {
                        Level = RecommendationLevel.Suggested,
                        Title = "Lower Level of Detail to 0.75",
                        Action = "Settings -> Graphics -> Level of Detail = 0.75",
                        Reason = probed.LevelOfDetail.HasValue
                            ? $"Currently {probed.LevelOfDetail.Value:F2} — single most impactful CS2 setting."
                            : "Single most impactful CS2 setting per Paradox.",
                    });
                }
                if (probed.DepthOfFieldEnabled != false)
                {
                    list.Add(new Recommendation
                    {
                        Level = RecommendationLevel.Suggested,
                        Title = "Disable Depth of Field",
                        Action = "Settings -> Graphics -> Depth of Field = Off",
                        Reason = "Sharper image and a measurable FPS gain.",
                    });
                }
                if (probed.MotionBlurEnabled != false)
                {
                    list.Add(new Recommendation
                    {
                        Level = RecommendationLevel.Suggested,
                        Title = "Disable Motion Blur",
                        Action = "Settings -> Graphics -> Motion Blur = Off",
                        Reason = "Hides stutter at low FPS, no benefit at high FPS.",
                    });
                }
            }

            // Mod isolation hint — only when a mod actually stands out by CPU.
            // Skip VanillaProfiler itself; suggesting "disable the profiler" defeats
            // the point of the screen the player is reading.
            var heaviest = HeaviestNonProfilerMod(snap);
            if (heaviest != null)
            {
                list.Add(new Recommendation
                {
                    Level = RecommendationLevel.Suggested,
                    Title = "Test without your top mod",
                    Action = $"Disable '{heaviest.Value.ModName}' and re-check.",
                    Reason = $"It alone consumes {heaviest.Value.TotalMs:F0} ms over the window.",
                });
            }

            // Info-tier — universally helpful safety nets, always last.
            if (list.Count < MAX_RECOMMENDATIONS && probed.IsFullscreenWindowed != true)
            {
                list.Add(new Recommendation
                {
                    Level = RecommendationLevel.Info,
                    Title = "Use Fullscreen Windowed",
                    Action = "Settings -> Display -> Fullscreen Windowed",
                    Reason = "Performs better than Exclusive in CS2.",
                });
            }

            if (list.Count < MAX_RECOMMENDATIONS && perfTrouble)
            {
                list.Add(new Recommendation
                {
                    Level = RecommendationLevel.Info,
                    Title = "Disable Terrain Casts Shadows",
                    Action = "Settings -> Graphics (Advanced) -> Terrain Shadows = Off",
                    Reason = "Large terrain footprint — shadows are expensive.",
                });
            }

            if (list.Count > MAX_RECOMMENDATIONS)
                list.RemoveRange(MAX_RECOMMENDATIONS, list.Count - MAX_RECOMMENDATIONS);
            return list;
        }

        private const double HEAVY_MOD_MS = 30.0;
        private const string PROFILER_MOD_NAME = "VanillaProfiler";

        private static (string ModName, double TotalMs)? HeaviestNonProfilerMod(OverlaySnapshot snap)
        {
            if (snap.TopMods == null) return null;
            for (int i = 0; i < snap.TopMods.Length; i++)
            {
                var entry = snap.TopMods[i];
                if (string.IsNullOrEmpty(entry.ModName)) continue;
                if (entry.ModName == PROFILER_MOD_NAME) continue;
                if (entry.TotalMs < HEAVY_MOD_MS) continue;
                return entry;
            }
            return null;
        }
    }
}
