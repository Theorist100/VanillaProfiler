using System;
using VanillaProfiler.Diagnostics;

namespace VanillaProfiler.Overlay.Modes
{
    /// <summary>
    /// Player-facing explanation screen. It uses the existing health verdict and
    /// top-mod attribution, but renders advice instead of raw profiler tables.
    /// </summary>
    public sealed class DiagnosisMode : IOverlayMode
    {
        public string DisplayName => "Diagnosis";
        public bool IsHidden => false;

        public float MeasureHeight(OverlaySnapshot snapshot)
        {
            // Problem (1) + spacer (1) + LIKELY CAUSE section (1)
            // + cause lines (worst case 2) + WHAT TO DO section (1) + advice (worst 4).
            // SUSPECTED MOD section (header + body = 2 lines) only when a mod stands out.
            // The fixed header is drawn by ProfilerOverlay.
            int lines = 10;
            if (!string.IsNullOrEmpty(TopMod(snapshot))) lines += 2;
            return OverlayPanel.PAD * 2 + OverlayPanel.LINE_H * lines + 12f;
        }

        public void Draw(DrawContext ctx, OverlaySnapshot snapshot, HealthReport health)
        {
            // Contract: snapshot and health are non-null here (overlay's settling
            // badge handles the warmup phase). No defensive null branch.
            OverlayPanel.DrawLine(ctx, $"Problem: {Problem(health)}", ctx.Theme.StyleForHealth(health.Overall));
            OverlayPanel.DrawLine(ctx, "", ctx.Theme.BodyStyle);

            OverlayPanel.DrawSection(ctx, "LIKELY CAUSE");
            foreach (var line in CauseLines(health))
                OverlayPanel.DrawLine(ctx, line, ctx.Theme.BodyStyle);

            // Only render SUSPECTED MOD when a mod actually stands out. Otherwise the
            // header reads as an accusation that the body then retracts ("Suspected
            // mod: nothing suspected" is contradictory) — clearer to omit the section.
            string? mod = TopMod(snapshot);
            if (!string.IsNullOrEmpty(mod))
            {
                OverlayPanel.DrawSection(ctx, "SUSPECTED MOD");
                OverlayPanel.DrawLine(ctx, OverlayFormat.Truncate(mod, 54), ctx.Theme.BodyStyle);
            }

            OverlayPanel.DrawSection(ctx, "WHAT TO DO");
            foreach (var line in AdviceLines(health, snapshot))
                OverlayPanel.DrawLine(ctx, line, ctx.Theme.BodyStyle);

        }

        private static string Problem(HealthReport health)
        {
            if (health.Overall == HealthLevel.Good) return "no clear performance problem";
            if (health.MemoryLevel == HealthLevel.Poor) return "memory is growing";
            if (health.GrowthLevel == HealthLevel.Poor) return "managed memory is growing fast";
            if (health.StutterLevel == HealthLevel.Poor) return "repeated frame spikes";

            return health.Bottleneck switch
            {
                BottleneckKind.GpuBound => "GPU/present wait is overloaded",
                BottleneckKind.CpuRenderBound => "CPU rendering is overloaded",
                BottleneckKind.SimBound => "simulation is overloaded",
                BottleneckKind.MemoryBound => "managed memory is growing fast",
                BottleneckKind.Balanced => "performance is unstable",
                BottleneckKind.Unknown => "performance is unstable",
                _ => throw new ArgumentOutOfRangeException(nameof(health), health.Bottleneck, "Unhandled BottleneckKind"),
            };
        }

        private static string[] CauseLines(HealthReport health)
        {
            if (health.MemoryLevel == HealthLevel.Poor)
                return new[] { "Memory is rising over time.", "A restart may help temporarily." };

            if (health.GrowthLevel == HealthLevel.Poor || health.Bottleneck == BottleneckKind.MemoryBound)
                return new[] { "Managed memory is growing quickly.", "This often comes from a leaking or caching mod." };

            if (health.Bottleneck == BottleneckKind.SimBound)
                return new[] { "The simulation is taking most of the frame.", "This can be a large city or a heavy gameplay mod." };

            if (health.Bottleneck == BottleneckKind.GpuBound)
                return new[]
                {
                    "The CPU is waiting on the GPU present path.",
                    "Lower GPU-heavy graphics settings first.",
                };

            if (health.Bottleneck == BottleneckKind.CpuRenderBound)
            {
                if (health.RenderCause == RenderCause.GpuUnderutilizedByCpuRender)
                    return new[]
                    {
                        $"CPU rendering is very heavy ({health.RenderPhaseMs:F0} ms/frame).",
                        "The GPU is being starved by CPU render/driver latency.",
                    };
                return new[] { "CPU render submission is taking most of the frame.", "Draw calls, LOD and shadows are likely contributors." };
            }

            if (health.StutterLevel == HealthLevel.Poor)
                return new[] { "Frame time has large spikes.", "Check if this happens after a specific action." };

            return new[] { "The last sample looks stable.", "Keep this screen open if the problem comes back." };
        }

        private string[] AdviceLines(HealthReport health, OverlaySnapshot snapshot)
        {
            if (health.Overall == HealthLevel.Good)
                return new[] { "1. Keep playing normally.", "2. Press Ctrl+F11 only if someone asks for a report." };

            if (health.Bottleneck == BottleneckKind.GpuBound)
                return new[]
                {
                    "1. Lower GPU-heavy settings first.",
                    "2. Re-check FPS and present wait after each change.",
                    "3. Do not change Max Frame Latency for this case.",
                };

            if (health.Bottleneck == BottleneckKind.CpuRenderBound)
            {
                if (health.RenderSeverity == RenderSeverityLevel.Severe)
                    return new[]
                    {
                        "1. Open the Recommendations screen (next mode).",
                        "2. Apply the listed render fixes one at a time.",
                        "3. Re-check this report after each change.",
                    };
                return new[] { "1. Lower graphics settings.", "2. If it continues, press Ctrl+F11 and send the report." };
            }

            // No mod stands out and the slow systems are vanilla — this is the "your
            // city / hardware / settings" case, not a "mod broke the game" case. Don't
            // tell the player to send a report; tell them what they can change.
            if (string.IsNullOrEmpty(TopMod(snapshot)) && IsVanillaHeavy(snapshot))
                return new[]
                {
                    "1. Lower graphics settings (LOD, shadows, post-processing).",
                    "2. A 300k+ city is at the edge of vanilla performance.",
                    "3. The slow systems are vanilla; this is not a mod problem.",
                };

            if (health.StutterLevel == HealthLevel.Poor)
                return new[]
                {
                    "1. Note what you were doing when the spike hit.",
                    "2. If it repeats, press Ctrl+F11 and send the report.",
                };

            return new[]
            {
                "1. Save the city.",
                "2. Restart the game.",
                "3. If it repeats, press Ctrl+F11 and send the report.",
            };
        }

        private static bool IsVanillaHeavy(OverlaySnapshot snapshot)
        {
            // Top vanilla system spent a meaningful chunk of the report window — the
            // performance hit is on the engine, not on a mod system.
            if (snapshot.TopVanillaSystems.Count == 0)
                return false;
            return snapshot.TopVanillaSystems[0].TotalMs > 50.0;
        }

        private static string? TopMod(OverlaySnapshot snapshot)
        {
            if (snapshot.TopMods.Count == 0)
                return null;

            var top = snapshot.TopMods[0];
            if (string.IsNullOrEmpty(top.ModName) || top.TotalMs < 1.0)
                return null;

            return top.ModName;
        }
    }
}
