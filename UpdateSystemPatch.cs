using HarmonyLib;
using Game;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace VanillaProfiler
{
    /// <summary>
    /// Harmony patches for UpdateSystem.Update — measures phase timing and render FPS.
    /// </summary>
    public static class UpdateSystemPatch
    {
#pragma warning disable CA1815
        [StructLayout(LayoutKind.Auto)]
        public struct PatchTimingMeasurement
        {
            public long StartTicks;
            public bool Started;
            public bool Completed;
        }
#pragma warning restore CA1815

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
            public static void Prefix(out PatchTimingMeasurement __state)
            {
                __state = default;
                try { __state = Begin(); }
                catch { /* profiler — never crash game */ }
            }

            [HarmonyPostfix]
            public static void Postfix(SystemUpdatePhase phase, ref PatchTimingMeasurement __state)
            {
                Complete(phase, ref __state, emitFrame: true);
            }

            [HarmonyFinalizer]
            public static Exception? Finalizer(SystemUpdatePhase phase, ref PatchTimingMeasurement __state, Exception? __exception)
            {
                if (!__state.Started) return __exception;
                Complete(phase, ref __state, emitFrame: true);
                return __exception;
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
            public static void Prefix(out PatchTimingMeasurement __state)
            {
                __state = default;
                try { __state = Begin(); }
                catch { /* profiler — never crash game */ }
            }

            [HarmonyPostfix]
            public static void Postfix(SystemUpdatePhase phase, ref PatchTimingMeasurement __state)
            {
                Complete(phase, ref __state, emitFrame: false);
            }

            [HarmonyFinalizer]
            public static Exception? Finalizer(SystemUpdatePhase phase, ref PatchTimingMeasurement __state, Exception? __exception)
            {
                if (!__state.Started) return __exception;
                Complete(phase, ref __state, emitFrame: false);
                return __exception;
            }
        }

        private static PatchTimingMeasurement Begin()
        {
            return new PatchTimingMeasurement
            {
                StartTicks = Stopwatch.GetTimestamp(),
                Started = true,
                Completed = false,
            };
        }

        private static void Complete(SystemUpdatePhase phase, ref PatchTimingMeasurement measurement, bool emitFrame)
        {
            try
            {
                if (!measurement.Started || measurement.Completed) return;
                measurement.Completed = true;

                var profiler = ProfilerHost.TryGetPatchSurface();
                if (profiler == null) return;

                profiler.RecordPhase(new PhaseMeasurement(
                    PhaseName(phase),
                    Stopwatch.GetTimestamp() - measurement.StartTicks));

                // Track render FPS from Rendering phase (fires once per render frame).
                if (emitFrame && phase == SystemUpdatePhase.Rendering)
                    profiler.OnFrame();
            }
            catch { /* profiler — never crash game */ }
        }

        public static void VerifyAndReport()
        {
            VerifyPatch(
                "UpdateSystemPatch.UpdatePhase",
                AccessTools.Method(typeof(UpdateSystem), nameof(UpdateSystem.Update), new[] { typeof(SystemUpdatePhase) }),
                typeof(UpdatePhase));
            VerifyPatch(
                "UpdateSystemPatch.UpdatePhaseWithIndex",
                AccessTools.Method(typeof(UpdateSystem), nameof(UpdateSystem.Update), new[] { typeof(SystemUpdatePhase), typeof(uint), typeof(int) }),
                typeof(UpdatePhaseWithIndex));
        }

        private static void VerifyPatch(string patchName, System.Reflection.MethodInfo? method, Type patchType)
        {
            if (method == null)
            {
                PatchStatusTracker.ReportFailure(patchName, "target method not found");
                return;
            }

            var patchInfo = Harmony.GetPatchInfo(method);
            bool hasPostfix = patchInfo?.Postfixes.Any(patch =>
                string.Equals(patch.owner, VanillaProfilerMod.HARMONY_ID, StringComparison.Ordinal)
                && patch.PatchMethod.DeclaringType == patchType) == true;

            if (hasPostfix)
                PatchStatusTracker.ReportSuccess(patchName);
            else
                PatchStatusTracker.ReportFailure(patchName, "postfix not present after PatchAll");
        }
    }
}
