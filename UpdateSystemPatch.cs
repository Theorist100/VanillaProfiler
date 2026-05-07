using HarmonyLib;
using Game;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace VanillaProfiler
{
    /// <summary>
    /// Harmony patches for UpdateSystem.Update — measures phase timing and render FPS.
    /// </summary>
    public static class UpdateSystemPatch
    {
        /// <summary>
        /// Pre-built phase name strings indexed by SystemUpdatePhase enum value.
        /// Avoids per-call string interpolation in hot path (Postfix runs on every phase tick).
        /// </summary>
        private static readonly Dictionary<SystemUpdatePhase, string> s_PhaseNames = BuildPhaseNames();

        private static Dictionary<SystemUpdatePhase, string> BuildPhaseNames()
        {
            var values = Enum.GetValues(typeof(SystemUpdatePhase)).Cast<SystemUpdatePhase>().ToArray();
            var names = new Dictionary<SystemUpdatePhase, string>(values.Length);
            foreach (var phase in values)
            {
                if ((int)phase < 0) continue;
                names[phase] = $"UpdateSystem.{phase}";
            }
            return names;
        }

        private static string PhaseName(SystemUpdatePhase phase)
        {
            return s_PhaseNames.TryGetValue(phase, out var name)
                ? name
                : "UpdateSystem.Unknown";
        }

        /// <summary>
        /// Patch for Update(SystemUpdatePhase phase) — Pre/Post simulation, Rendering, etc.
        /// </summary>
        [HarmonyPatch(typeof(UpdateSystem), nameof(UpdateSystem.Update), typeof(SystemUpdatePhase))]
        public static class UpdatePhase
        {
            [HarmonyPrepare]
            public static bool Prepare()
            {
                return AccessTools.Method(typeof(UpdateSystem), "Update",
                    new[] { typeof(SystemUpdatePhase) }) != null;
            }

            [HarmonyPrefix]
            public static void Prefix(out long __state)
            {
                __state = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            public static void Postfix(SystemUpdatePhase phase, long __state)
            {
                try
                {
                    var profiler = ProfilerHost.TryGet();
                    if (profiler == null) return;
                    profiler.RecordPhase(PhaseName(phase), Stopwatch.GetTimestamp() - __state);

                    // Track render FPS from Rendering phase (fires once per render frame)
                    if (phase == SystemUpdatePhase.Rendering)
                        profiler.OnFrame();
                }
                catch { /* profiler — never crash game */ }
            }
        }

        /// <summary>
        /// Patch for Update(SystemUpdatePhase, uint, int) — GameSimulation ticks.
        /// </summary>
        [HarmonyPatch(typeof(UpdateSystem), nameof(UpdateSystem.Update),
            typeof(SystemUpdatePhase), typeof(uint), typeof(int))]
        public static class UpdatePhaseWithIndex
        {
            [HarmonyPrepare]
            public static bool Prepare()
            {
                return AccessTools.Method(typeof(UpdateSystem), "Update",
                    new[] { typeof(SystemUpdatePhase), typeof(uint), typeof(int) }) != null;
            }

            [HarmonyPrefix]
            public static void Prefix(out long __state)
            {
                __state = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            public static void Postfix(SystemUpdatePhase phase, long __state)
            {
                try
                {
                    ProfilerHost.TryGet()?.RecordPhase(PhaseName(phase), Stopwatch.GetTimestamp() - __state);
                }
                catch { /* profiler — never crash game */ }
            }
        }
    }
}
