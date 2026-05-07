using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Unity.Entities;

namespace VanillaProfiler.Diagnostics
{
    /// <summary>
    /// Surfaces vanilla systems whose <c>OnUpdate</c> is hooked by a foreign
    /// Harmony prefix. The original method may be wrapped or skipped outright,
    /// so the per-system cost table cannot show its true vanilla cost — but
    /// the replacement signal itself ("Mod X patches vanilla Y") is the
    /// diagnostic players otherwise have no way to see.
    ///
    /// We do NOT report <c>Enabled = false</c>: vanilla itself disables
    /// dozens of systems based on game mode (debug systems in release,
    /// editor-only UI, tools when no tool is active, save/load when not
    /// loading, mode-specific simulation systems). There is no Harmony
    /// trail to attribute the disable to a specific mod, so listing it
    /// produces 80+ rows of false positives that bury the real signal.
    ///
    /// Scan walks <c>World.All</c> so any sub-world (gameplay, editor) is
    /// covered. Attribution piggybacks on <see cref="ModAttribution"/>, so
    /// owner names match the Top mods table.
    /// </summary>
    public static class SystemReplacementDetector
    {
        public sealed class Replacement
        {
            public string VanillaSystem;
            public string OwnerMod;
        }

        public static List<Replacement> Scan()
        {
            var result = new List<Replacement>();
            try
            {
                foreach (var world in World.All)
                {
                    if (world == null || !world.IsCreated) continue;
                    foreach (var system in world.Systems)
                    {
                        var sys = system;
                        if (sys == null) continue;
                        var type = sys.GetType();
                        if (!ModAttribution.IsVanilla(type)) continue;

                        string owners = ResolveOnUpdatePrefixOwners(type);
                        if (owners == null) continue;

                        result.Add(new Replacement
                        {
                            VanillaSystem = type.FullName,
                            OwnerMod = owners,
                        });
                    }
                }
                result.Sort((a, b) => string.CompareOrdinal(a.VanillaSystem, b.VanillaSystem));
            }
            catch (Exception ex)
            {
                ModLog.Warn($"System replacement scan failed: {ex}");
            }
            return result;
        }

        /// <summary>
        /// Returns the comma-joined list of mods that prefix this vanilla type's
        /// OnUpdate, or null if no foreign prefix exists. Our own profiler patch
        /// targets SystemBase.Update, not OnUpdate, so it never appears here —
        /// the HARMONY_ID check guards against future drift.
        /// </summary>
        private static string ResolveOnUpdatePrefixOwners(Type type)
        {
            var onUpdate = AccessTools.Method(type, "OnUpdate");
            if (onUpdate == null) return null;
            var info = Harmony.GetPatchInfo(onUpdate);
            if (info == null || info.Prefixes == null) return null;

            HashSet<string> set = null;
            foreach (var prefix in info.Prefixes)
            {
                if (prefix == null) continue;
                if (string.Equals(prefix.owner, VanillaProfilerMod.HARMONY_ID, StringComparison.Ordinal))
                    continue;
                string mod = ModAttribution.Resolve(prefix.PatchMethod?.DeclaringType);
                if (string.IsNullOrEmpty(mod)) continue;
                if (mod == ModAttribution.VANILLA || mod == ModAttribution.PROFILER) continue;
                set ??= new HashSet<string>(StringComparer.Ordinal);
                set.Add(mod);
            }
            if (set == null || set.Count == 0) return null;
            var ordered = new List<string>(set);
            ordered.Sort(StringComparer.Ordinal);
            return string.Join(", ", ordered);
        }

        /// <summary>
        /// Writes a one-off section to PERF.log on the first scan after city
        /// load, mirroring <see cref="HarmonyConflictDetector"/>'s pattern so
        /// support files capture the replacement signal alongside conflicts.
        /// </summary>
        public static void LogTo(Profiler profiler, List<Replacement> replacements)
        {
            if (profiler == null || replacements == null) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine($"PATCHED VANILLA SYSTEMS  —  {replacements.Count} found");
                sb.AppendLine(new string('─', 50));
                if (replacements.Count == 0)
                {
                    sb.AppendLine("  No mod-patched vanilla OnUpdate methods detected.");
                    profiler.LogInfo(sb.ToString());
                    return;
                }
                foreach (var r in replacements)
                    sb.AppendLine($"  {r.VanillaSystem,-60}  ← {r.OwnerMod}");
                profiler.LogInfo(sb.ToString());
            }
            catch (Exception ex)
            {
                ModLog.Warn($"System replacement log failed: {ex}");
            }
        }
    }
}
