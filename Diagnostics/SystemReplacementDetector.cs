using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Unity.Entities;

namespace VanillaProfiler.Diagnostics
{
    /// <summary>
    /// Surfaces vanilla systems whose <c>OnUpdate</c> is hooked by a foreign
    /// Harmony patch. The total <c>SystemBase.Update</c> elapsed time is still
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
            public Replacement(Type systemType, string vanillaSystem, IReadOnlyList<PatchOwnerIdentity> owners)
            {
                SystemType = systemType;
                VanillaSystem = vanillaSystem;
                Owners = owners;
            }

            public Type SystemType { get; }
            public string VanillaSystem { get; }
            public IReadOnlyList<PatchOwnerIdentity> Owners { get; }
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
            /// foreign Harmony patch when this snapshot was scanned.
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

                        var owners = ResolveOnUpdatePatchOwners(type);
                        if (owners.Count == 0) continue;

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
        /// Returns the structured identities that patch this vanilla type's
        /// OnUpdate, or an empty list if no foreign patch exists. Our own profiler patch
        /// targets SystemBase.Update, not OnUpdate, so it never appears here —
        /// the HARMONY_ID check guards against future drift.
        /// </summary>
        private static IReadOnlyList<PatchOwnerIdentity> ResolveOnUpdatePatchOwners(Type type)
        {
            var onUpdate = AccessTools.Method(type, "OnUpdate");
            if (onUpdate == null) return Array.Empty<PatchOwnerIdentity>();
            var info = Harmony.GetPatchInfo(onUpdate);
            if (info == null) return Array.Empty<PatchOwnerIdentity>();

            Dictionary<string, PatchOwnerIdentity>? owners = null;
            AddPatchOwners(info.Prefixes, ref owners);
            AddPatchOwners(info.Postfixes, ref owners);
            AddPatchOwners(info.Transpilers, ref owners);
            AddPatchOwners(info.Finalizers, ref owners);

            if (owners == null || owners.Count == 0) return Array.Empty<PatchOwnerIdentity>();
            var ordered = new List<PatchOwnerIdentity>(owners.Values);
            ordered.Sort(static (a, b) => string.CompareOrdinal(
                ModAttribution.FormatPatchOwner(a),
                ModAttribution.FormatPatchOwner(b)));
            return ordered;
        }

        private static void AddPatchOwners(IEnumerable<Patch>? patches, ref Dictionary<string, PatchOwnerIdentity>? owners)
        {
            if (patches == null) return;
            foreach (var patch in patches)
            {
                if (patch == null) continue;
                if (string.Equals(patch.owner, VanillaProfilerMod.HARMONY_ID, StringComparison.Ordinal))
                    continue;
                var owner = ModAttribution.ResolvePatchOwner(patch.PatchMethod, patch.owner);
                if (ShouldSkipOwner(owner)) continue;
                owners ??= new Dictionary<string, PatchOwnerIdentity>(StringComparer.Ordinal);
                owners[OwnerKey(owner)] = owner;
            }
        }

        private static bool ShouldSkipOwner(PatchOwnerIdentity owner)
        {
            if (string.Equals(owner.HarmonyOwnerId, VanillaProfilerMod.HARMONY_ID, StringComparison.Ordinal))
                return true;

            var identity = owner.PatchAssembly;
            if (identity.Origin == AssemblyOrigin.Profiler)
                return true;

            if (identity.IsVanillaSystemOwner || identity.Origin == AssemblyOrigin.TrustedFramework)
                return owner.Confidence >= AttributionConfidence.TrustedRuntimeAssembly;

            return false;
        }

        private static string OwnerKey(PatchOwnerIdentity owner)
        {
            string assembly = owner.PatchAssembly.AssemblyName;
            string harmony = owner.HarmonyOwnerId;
            return $"{assembly}|{harmony}|{owner.PatchAssembly.Confidence}";
        }

        public static string FormatOwners(IReadOnlyList<PatchOwnerIdentity> owners)
        {
            if (owners == null || owners.Count == 0) return ModAttribution.UNKNOWN;
            var formatted = new List<string>(owners.Count);
            for (int i = 0; i < owners.Count; i++)
                formatted.Add(ModAttribution.FormatPatchOwner(owners[i]));
            return string.Join(", ", formatted);
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
                    sb.AppendLine("  section. The elapsed time blends the patching mod's Harmony hooks with the");
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
                    sb.AppendLine($"  {r.VanillaSystem,-60}  ← {FormatOwners(r.Owners)}");
                profiler.LogInfo(sb.ToString());
            }
            catch (Exception ex)
            {
                ModLog.Warn($"System replacement log failed: {ex}");
            }
        }
    }
}
