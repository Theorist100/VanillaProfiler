using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Unity.Entities;

namespace VanillaProfiler.Diagnostics
{
    /// <summary>
    /// Surfaces vanilla systems whose <c>OnUpdate</c> is hooked by a foreign
    /// Harmony prefix. The total <c>SystemBase.Update</c> elapsed time is still
    /// measured by <see cref="SystemAutoProfiler"/>, but Harmony does not let us
    /// split that elapsed time between the patching mod's prefix and the
    /// (possibly skipped) vanilla original — there is no hook between prefix
    /// and original for an intermediate timestamp. We surface both the patch
    /// signal ("Mod X patches vanilla Y") and the total ms via
    /// <see cref="IsPatched"/> + the dedicated bucket in <c>MetricsAggregator</c>.
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
            public Replacement(Type systemType, string vanillaSystem, string ownerMod)
            {
                SystemType = systemType;
                VanillaSystem = vanillaSystem;
                OwnerMod = ownerMod;
            }

            public Type SystemType { get; }
            public string VanillaSystem { get; }
            public string OwnerMod { get; }
        }

        public sealed class ReplacementSnapshot
        {
            public static readonly ReplacementSnapshot Empty =
                new ReplacementSnapshot(new Dictionary<Type, Replacement>());

            private readonly IReadOnlyDictionary<Type, Replacement> m_ByType;
            private readonly IReadOnlyList<Replacement> m_Replacements;

            internal ReplacementSnapshot(Dictionary<Type, Replacement> byType)
            {
                var copy = new Dictionary<Type, Replacement>(byType);
                m_ByType = copy;

                var ordered = new List<Replacement>(copy.Values);
                ordered.Sort(static (a, b) => string.CompareOrdinal(a.VanillaSystem, b.VanillaSystem));
                m_Replacements = ordered.ToArray();
            }

            public IReadOnlyDictionary<Type, Replacement> ByType => m_ByType;
            public IReadOnlyList<Replacement> Replacements => m_Replacements;

            /// <summary>
            /// True if the given vanilla System type's <c>OnUpdate</c> had a
            /// foreign Harmony prefix when this snapshot was scanned.
            /// </summary>
            public bool IsPatched(Type? type)
                => type != null && m_ByType.ContainsKey(type);
        }

        public static void Reset()
        {
        }

        public static ReplacementSnapshot Scan()
        {
            var result = new Dictionary<Type, Replacement>();
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

                        string? owners = ResolveOnUpdatePrefixOwners(type);
                        if (owners == null) continue;

                        if (result.ContainsKey(type)) continue;
                        result[type] = new Replacement(
                            type,
                            type.FullName ?? type.Name,
                            owners);
                    }
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn($"System replacement scan failed: {ex}");
            }
            return result.Count == 0 ? ReplacementSnapshot.Empty : new ReplacementSnapshot(result);
        }

        /// <summary>
        /// Returns the comma-joined list of mods that prefix this vanilla type's
        /// OnUpdate, or null if no foreign prefix exists. Our own profiler patch
        /// targets SystemBase.Update, not OnUpdate, so it never appears here —
        /// the HARMONY_ID check guards against future drift.
        /// </summary>
        private static string? ResolveOnUpdatePrefixOwners(Type type)
        {
            var onUpdate = AccessTools.Method(type, "OnUpdate");
            if (onUpdate == null) return null;
            var info = Harmony.GetPatchInfo(onUpdate);
            if (info == null || info.Prefixes == null) return null;

            HashSet<string>? set = null;
            foreach (var prefix in info.Prefixes)
            {
                if (prefix == null) continue;
                if (string.Equals(prefix.owner, VanillaProfilerMod.HARMONY_ID, StringComparison.Ordinal))
                    continue;
                string mod = ResolvePrefixOwner(prefix);
                if (string.IsNullOrEmpty(mod)) continue;
                if (string.Equals(mod, ModAttribution.VANILLA, StringComparison.Ordinal)
                    || string.Equals(mod, ModAttribution.PROFILER, StringComparison.Ordinal)) continue;
                set ??= new HashSet<string>(StringComparer.Ordinal);
                set.Add(mod);
            }
            if (set == null || set.Count == 0) return null;
            var ordered = new List<string>(set);
            ordered.Sort(StringComparer.Ordinal);
            return string.Join(", ", ordered);
        }

        private static string ResolvePrefixOwner(Patch prefix)
        {
            string mod = ModAttribution.Resolve(prefix.PatchMethod?.DeclaringType);
            if (!string.IsNullOrEmpty(mod) && !string.Equals(mod, ModAttribution.UNKNOWN, StringComparison.Ordinal))
                return mod;

            return string.IsNullOrEmpty(prefix.owner) ? ModAttribution.UNKNOWN : prefix.owner;
        }

        /// <summary>
        /// Writes a one-off section to PERF.log on the first scan after city
        /// load, mirroring <see cref="HarmonyConflictDetector"/>'s pattern so
        /// support files capture the replacement signal alongside conflicts.
        /// Per-cycle ms numbers are not in this listing — they live in the
        /// overlay's Patched vanilla systems section and the per-report
        /// snapshot, since this method runs once before any window has
        /// accumulated timing data.
        /// </summary>
        public static void LogTo(IProfilerReadSurface profiler, ReplacementSnapshot snapshot)
        {
            if (profiler == null || snapshot == null) return;
            try
            {
                var replacements = snapshot.Replacements;
                var sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine($"PATCHED VANILLA SYSTEMS  —  {replacements.Count} found");
                if (replacements.Count > 0)
                {
                    sb.AppendLine("  Total Update ms is reported in the overlay's Patched vanilla systems");
                    sb.AppendLine("  section. The elapsed time blends the patching mod's prefix with the");
                    sb.AppendLine("  vanilla original — Harmony does not let us split them.");
                }
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
