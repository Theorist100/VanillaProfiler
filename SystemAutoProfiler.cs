using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Unity.Entities;
using VanillaProfiler.Diagnostics;

namespace VanillaProfiler
{
    /// <summary>
    /// Patches SystemBase.Update() to profile ALL systems individually.
    /// Separates vanilla (Game.*, Unity.*, Colossal.*) from mod systems and
    /// attributes each system to its owning mod via ModAttribution.
    ///
    /// SystemBase.Update is invoked sequentially from ComponentSystemGroup.UpdateAllSystems on
    /// the main thread. Harmony Postfix runs on the same thread, so plain dictionaries are enough.
    /// Stopwatch is the only timing source — ProfilerMarker.Begin/End would only be visible to
    /// subscribers, and CS2 ships with Profiler.enabled=false (no subscriber). See
    /// Docs/Reference/API/CS2_Profiling_API.md "What you can NOT do from a mod".
    /// </summary>
    [HarmonyPatch(typeof(SystemBase), nameof(SystemBase.Update))]
    public static class SystemAutoProfiler
    {
        private sealed class SystemInfo
        {
            public string Name;
            public bool IsVanilla;
            public string ModName;
        }

        // Per-call state carried via Harmony __state. Must survive nested SystemBase.Update
        // invocations — vanilla CS2 nests these calls (ReplacePrefabSystem.FinalizeReplaces,
        // HeatmapPreviewSystem.OnUpdate, PhotoModeRenderSystem all run other systems' Update
        // synchronously inside their own OnUpdate). Each (Prefix, Postfix) pair gets its
        // own __state instance, so nesting is intrinsically safe.
#pragma warning disable CA1815
        public struct UpdateState
        {
            public long StartTicks;
        }
#pragma warning restore CA1815

        private static readonly Dictionary<Type, SystemInfo> s_Cache = new();

        /// <summary>Clears caches. Call from Mod.OnDispose so reloads get fresh attribution.</summary>
        public static void Reset()
        {
            s_Cache.Clear();
        }

        [HarmonyPrepare]
        public static bool Prepare()
        {
            var method = AccessTools.Method(typeof(SystemBase), nameof(SystemBase.Update));
            if (method == null)
            {
                ModLog.Warn("SystemBase.Update not found — auto-profiler disabled");
                return false;
            }
            ModLog.Info("Per-system auto-profiler enabled");
            return true;
        }

        [HarmonyPrefix]
        public static void Prefix(out UpdateState __state)
        {
            __state.StartTicks = Stopwatch.GetTimestamp();
        }

        [HarmonyPostfix]
        public static void Postfix(SystemBase __instance, UpdateState __state)
        {
            Complete(__instance, __state);
        }

        [HarmonyFinalizer]
        public static Exception Finalizer(SystemBase __instance, UpdateState __state, Exception __exception)
        {
            if (__exception != null)
                Complete(__instance, __state);
            return __exception;
        }

        private static void Complete(SystemBase __instance, UpdateState __state)
        {
            try
            {
                MainThreadGuard.AssertMainThread(nameof(Postfix));
                long elapsed = Stopwatch.GetTimestamp() - __state.StartTicks;
                var type = __instance.GetType();

                if (!s_Cache.TryGetValue(type, out var info))
                {
                    info = BuildInfo(type);
                    s_Cache[type] = info;
                }
                if (info.IsVanilla && !SettingsStore.Current.ProfileVanillaSystems) return;

                ProfilerHost.TryGet()?.RecordSystem(info.Name, elapsed, info.IsVanilla, info.ModName);
            }
            catch { /* profiler — never crash game */ }
        }

        private static SystemInfo BuildInfo(Type type)
        {
            string modName = ModAttribution.Resolve(type);
            string name = string.IsNullOrEmpty(type.FullName)
                ? (string.IsNullOrEmpty(type.Name) ? "<anonymous>" : type.Name)
                : type.FullName;

            // Profiler systems used to be skipped here; include them so the player
            // can see how much VanillaProfiler itself costs and confirm it's not
            // making things worse than the mod they're trying to diagnose.
            return new SystemInfo
            {
                Name = name,
                IsVanilla = modName == ModAttribution.VANILLA,
                ModName = modName,
            };
        }
    }
}
