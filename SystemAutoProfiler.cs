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
    /// </summary>
    [HarmonyPatch(typeof(SystemBase), nameof(SystemBase.Update))]
    public static class SystemAutoProfiler
    {
        private sealed class SystemInfo
        {
            public string Name;
            public bool IsVanilla;
            public string ModName;
            public bool Skip;
        }

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
        public static void Prefix(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        [HarmonyPostfix]
        public static void Postfix(SystemBase __instance, long __state)
        {
            try
            {
                MainThreadGuard.AssertMainThread(nameof(Postfix));
                long elapsed = Stopwatch.GetTimestamp() - __state;
                var type = __instance.GetType();

                if (!s_Cache.TryGetValue(type, out var info))
                {
                    info = BuildInfo(type);
                    s_Cache[type] = info;
                }
                if (info.Skip) return;
                if (info.IsVanilla && !SettingsStore.Current.ProfileVanillaSystems) return;

                ProfilerHost.TryGet()?.RecordSystem(info.Name, elapsed, info.IsVanilla, info.ModName);
            }
            catch { /* profiler — never crash game */ }
        }

        private static SystemInfo BuildInfo(Type type)
        {
            string modName = ModAttribution.Resolve(type);
            if (modName == ModAttribution.PROFILER)
                return new SystemInfo { Skip = true };

            return new SystemInfo
            {
                Name = string.IsNullOrEmpty(type.Name) ? "<anonymous>" : type.Name,
                IsVanilla = modName == ModAttribution.VANILLA,
                ModName = modName,
            };
        }
    }
}
