using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HarmonyLib;
using Unity.Entities;
using VanillaProfiler.Diagnostics;

namespace VanillaProfiler
{
    /// <summary>
    /// Patches SystemBase.Update() to profile ALL systems individually.
    /// Separates trusted runtime systems from mod systems and attributes each
    /// system to its owning mod via ModAttribution.
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
            public ProfiledSystemIdentity Identity;
        }

        // Per-call state carried via Harmony __state. CS2 can synchronously call
        // another SystemBase.Update from inside a system's OnUpdate, so we keep a
        // thread-local stack and subtract child Update time from the parent. Reports
        // use self/exclusive cost for top-system attribution while preserving the
        // inclusive elapsed time for patched-vanilla diagnostics.
#pragma warning disable CA1815
        [StructLayout(LayoutKind.Auto)]
        public struct SystemUpdateMeasurement
        {
            public long StartTicks;
            public int Depth;
            public bool Started;
            public bool Completed;
        }
#pragma warning restore CA1815

        private struct CallFrame
        {
            public long ChildTicks;
        }

        private static readonly Dictionary<Type, SystemInfo> s_Cache = new();

        [ThreadStatic]
        private static List<CallFrame>? s_CallStack;

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
        public static void Prefix(out SystemUpdateMeasurement __state)
        {
            var stack = s_CallStack ??= new List<CallFrame>(32);
            __state = Begin(stack.Count);
            stack.Add(default);
        }

        [HarmonyPostfix]
        public static void Postfix(SystemBase __instance, ref SystemUpdateMeasurement __state)
        {
            Complete(__instance, ref __state);
        }

        [HarmonyFinalizer]
        public static Exception? Finalizer(SystemBase __instance, ref SystemUpdateMeasurement __state, Exception? __exception)
        {
            if (!__state.Started) return __exception;
            Complete(__instance, ref __state);
            return __exception;
        }

        private static void Complete(SystemBase __instance, ref SystemUpdateMeasurement __state)
        {
            try
            {
                if (!__state.Started || __state.Completed) return;
                __state.Completed = true;
                long elapsed = Stopwatch.GetTimestamp() - __state.StartTicks;
                if (!TryPopFrameAndGetChildTicks(__state.Depth, elapsed, out long childTicks))
                    return;
                MainThreadGuard.AssertMainThread(nameof(Postfix));
                long selfTicks = elapsed > childTicks ? elapsed - childTicks : 0;
                var type = __instance.GetType();

                if (!s_Cache.TryGetValue(type, out var info))
                {
                    info = BuildInfo(type);
                    s_Cache[type] = info;
                }

                // Patched vanilla systems are routed to a dedicated bucket
                // independent of ProfileVanillaSystems. Their elapsed time
                // blends the patching mod's hooks with the (possibly skipped)
                // vanilla original — Harmony does not expose a hook between
                // them — but the total ms is honest, and surfacing it is the
                // whole point of the Patched vanilla systems section. We
                // Re-check the membership every call (instead of caching on
                // SystemInfo) against the active report-window context.
                // Mod-options screens can flip Harmony patches at runtime;
                // Profiler rotates immutable report-window contexts at
                // lifecycle boundaries and after each report cycle.
                var profiler = ProfilerHost.TryGetPatchSurface();
                if (profiler == null) return;

                if (info.Identity.IsVanilla)
                {
                    if (profiler.IsVanillaSystemPatched(type))
                    {
                        profiler.RecordPatchedVanilla(new ProfiledSystemMeasurement(info.Identity, selfTicks, elapsed));
                        return;
                    }
                    if (!profiler.ShouldProfileVanillaSystems) return;
                }

                profiler.RecordSystem(new ProfiledSystemMeasurement(info.Identity, selfTicks, elapsed));
            }
            catch { /* profiler — never crash game */ }
        }

        private static SystemUpdateMeasurement Begin(int depth)
        {
            return new SystemUpdateMeasurement
            {
                StartTicks = Stopwatch.GetTimestamp(),
                Depth = depth,
                Started = true,
                Completed = false,
            };
        }

        private static bool TryPopFrameAndGetChildTicks(int depth, long elapsedTicks, out long childTicks)
        {
            childTicks = 0;
            var stack = s_CallStack;
            if (stack == null || depth < 0 || depth >= stack.Count)
            {
                stack?.Clear();
                return false;
            }

            childTicks = stack[depth].ChildTicks;
            stack.RemoveRange(depth, stack.Count - depth);

            if (depth > 0)
            {
                int parentIndex = depth - 1;
                var parent = stack[parentIndex];
                parent.ChildTicks += elapsedTicks;
                stack[parentIndex] = parent;
            }

            return true;
        }

        private static SystemInfo BuildInfo(Type type)
        {
            var identity = ModAttribution.ResolveIdentity(type);
            string name = string.IsNullOrEmpty(type.FullName)
                ? (string.IsNullOrEmpty(type.Name) ? "<anonymous>" : type.Name)
                : type.FullName;

            // Profiler systems used to be skipped here; include them so the player
            // can see how much VanillaProfiler itself costs and confirm it's not
            // making things worse than the mod they're trying to diagnose.
            return new SystemInfo
            {
                Identity = new ProfiledSystemIdentity(
                    name,
                    identity.IsVanillaSystemOwner,
                    ModAttribution.FormatIdentity(identity)),
            };
        }
    }
}
