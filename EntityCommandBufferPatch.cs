using System.Diagnostics;
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
            public static void Prefix(out long __state)
            {
                __state = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            public static void Postfix(long __state)
            {
                try
                {
                    ProfilerHost.TryGet()?.RecordPhase(ECB_KEY, Stopwatch.GetTimestamp() - __state);
                }
                catch { /* profiler — never crash game */ }
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
            public static void Prefix(out long __state)
            {
                __state = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            public static void Postfix(long __state)
            {
                try
                {
                    ProfilerHost.TryGet()?.RecordPhase(ECB_KEY, Stopwatch.GetTimestamp() - __state);
                }
                catch { /* profiler — never crash game */ }
            }
        }
    }
}
