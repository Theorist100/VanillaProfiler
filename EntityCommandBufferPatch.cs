using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using HarmonyLib;
using Unity.Entities;
using VanillaProfiler.Diagnostics;

namespace VanillaProfiler
{
    /// <summary>
    /// Times EntityCommandBuffer.Playback. Always main-thread: ECB playback is the
    /// single moment when queued structural commands hit the EntityManager. This is a
    /// real, measurable cost — unlike Burst-compiled work on worker threads — and
    /// surfaces under "ECB.Playback" alongside the per-phase rows.
    /// </summary>
    public static class EntityCommandBufferPatch
    {
        private const string ECB_KEY = "ECB.Playback";

#pragma warning disable CA1815
        [StructLayout(LayoutKind.Auto)]
        public struct PatchTimingMeasurement
        {
            public long StartTicks;
            public bool Started;
            public bool Completed;
        }
#pragma warning restore CA1815

        [HarmonyPatch(typeof(EntityCommandBuffer), nameof(EntityCommandBuffer.Playback), typeof(EntityManager))]
        public static class PlaybackEntityManager
        {
            [HarmonyPrepare]
            public static bool Prepare()
            {
                var method = AccessTools.Method(typeof(EntityCommandBuffer),
                    nameof(EntityCommandBuffer.Playback), new[] { typeof(EntityManager) });
                if (method == null)
                {
                    ModLog.Warn("EntityCommandBuffer.Playback(EntityManager) not found — ECB timing disabled");
                    return false;
                }
                return true;
            }

            [HarmonyPrefix]
            public static void Prefix(out PatchTimingMeasurement __state)
            {
                __state = default;
                try { __state = Begin(); }
                catch { /* profiler — never crash game */ }
            }

            [HarmonyPostfix]
            public static void Postfix(ref PatchTimingMeasurement __state)
            {
                Complete(ref __state);
            }

            [HarmonyFinalizer]
            public static Exception? Finalizer(ref PatchTimingMeasurement __state, Exception? __exception)
            {
                if (!__state.Started) return __exception;
                Complete(ref __state);
                return __exception;
            }
        }

        [HarmonyPatch(typeof(EntityCommandBuffer), nameof(EntityCommandBuffer.Playback), typeof(ExclusiveEntityTransaction))]
        public static class PlaybackExclusiveTransaction
        {
            [HarmonyPrepare]
            public static bool Prepare()
            {
                // This overload exists in newer DOTS but may be absent on the CS2 build —
                // gate quietly so a missing target doesn't hard-fail Harmony.
                bool exists = AccessTools.Method(typeof(EntityCommandBuffer),
                    nameof(EntityCommandBuffer.Playback),
                    new[] { typeof(ExclusiveEntityTransaction) }) != null;
                if (!exists)
                    ModLog.Info("EntityCommandBuffer.Playback(ExclusiveEntityTransaction) not present — second ECB path skipped");
                return exists;
            }

            [HarmonyPrefix]
            public static void Prefix(out PatchTimingMeasurement __state)
            {
                __state = default;
                try { __state = Begin(); }
                catch { /* profiler — never crash game */ }
            }

            [HarmonyPostfix]
            public static void Postfix(ref PatchTimingMeasurement __state)
            {
                Complete(ref __state);
            }

            [HarmonyFinalizer]
            public static Exception? Finalizer(ref PatchTimingMeasurement __state, Exception? __exception)
            {
                if (!__state.Started) return __exception;
                Complete(ref __state);
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

        private static void Complete(ref PatchTimingMeasurement measurement)
        {
            try
            {
                if (!measurement.Started || measurement.Completed) return;
                measurement.Completed = true;
                ProfilerHost.TryGetPatchSurface()?.RecordPhase(new PhaseMeasurement(
                    ECB_KEY,
                    Stopwatch.GetTimestamp() - measurement.StartTicks));
            }
            catch { /* profiler — never crash game */ }
        }

        public static void VerifyAndReport()
        {
            VerifyRequiredPatch();
            VerifyOptionalExclusiveTransactionPatch();
        }

        private static void VerifyRequiredPatch()
        {
            var method = AccessTools.Method(typeof(EntityCommandBuffer),
                nameof(EntityCommandBuffer.Playback), new[] { typeof(EntityManager) });
            if (method == null)
            {
                PatchStatusTracker.ReportFailure("EntityCommandBufferPatch.PlaybackEntityManager", "target method not found");
                return;
            }

            VerifyPatch("EntityCommandBufferPatch.PlaybackEntityManager", method, typeof(PlaybackEntityManager));
        }

        private static void VerifyOptionalExclusiveTransactionPatch()
        {
            var method = AccessTools.Method(typeof(EntityCommandBuffer),
                nameof(EntityCommandBuffer.Playback), new[] { typeof(ExclusiveEntityTransaction) });
            if (method == null)
            {
                PatchStatusTracker.ReportSuccess("EntityCommandBufferPatch.PlaybackExclusiveTransaction");
                return;
            }

            VerifyPatch("EntityCommandBufferPatch.PlaybackExclusiveTransaction", method, typeof(PlaybackExclusiveTransaction));
        }

        private static void VerifyPatch(string patchName, System.Reflection.MethodInfo method, Type patchType)
        {
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
