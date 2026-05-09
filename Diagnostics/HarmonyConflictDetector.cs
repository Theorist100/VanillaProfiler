using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;

namespace VanillaProfiler.Diagnostics
{
    /// <summary>
    /// Scans every method patched by any Harmony instance and lists ones touched by
    /// more than one mod. Multi-owner patches are a frequent source of crashes and
    /// silent gameplay regressions, so surfacing them in PERF.log helps players
    /// pinpoint mod conflicts.
    /// </summary>
    public static class HarmonyConflictDetector
    {
        private static string? s_LastSignature;

        public static void Reset()
        {
            s_LastSignature = null;
        }

        public static void LogIfChanged(IProfilerReadSurface profiler)
        {
            if (profiler == null) return;
            try
            {
                var (conflicts, signature) = Detect();
                if (string.Equals(signature, s_LastSignature, StringComparison.Ordinal))
                    return;
                s_LastSignature = signature;

                var sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine($"HARMONY PATCHES SCAN  —  {conflicts.Count} VanillaProfiler patch conflicts");
                sb.AppendLine(new string('─', 50));
                if (conflicts.Count == 0)
                {
                    sb.AppendLine("  No VanillaProfiler-involved multi-owner patches detected.");
                    profiler.LogInfo(sb.ToString());
                    return;
                }

                foreach (var entry in conflicts)
                    sb.AppendLine($"  {entry.Method,-60}  ← {string.Join(", ", entry.Owners)}");
                profiler.LogInfo(sb.ToString());
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Harmony conflict scan failed: {ex}");
            }
        }

        public sealed class ConflictEntry
        {
            public string Method = string.Empty;
            public IReadOnlyList<string> Owners = Array.Empty<string>();
        }

        private static (IReadOnlyList<ConflictEntry> Conflicts, string Signature) Detect()
        {
            var result = new List<ConflictEntry>();
            var signatureParts = new List<string>();
            foreach (var method in Harmony.GetAllPatchedMethods().ToArray())
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;

                var owners = new HashSet<string>(StringComparer.Ordinal);
                AddOwners(owners, info.Prefixes);
                AddOwners(owners, info.Postfixes);
                AddOwners(owners, info.Transpilers);
                AddOwners(owners, info.Finalizers);

                var typeName = method.DeclaringType?.FullName ?? "<global>";
                var methodName = $"{typeName}.{method.Name}";
                var orderedOwners = owners.OrderBy(s => s, StringComparer.Ordinal).ToList();
                signatureParts.Add(methodName + "|" + string.Join(",", orderedOwners));

                if (owners.Count < 2) continue;

                // Only report conflicts where VanillaProfiler is one of the patchers.
                // Otherwise PERF.log would shame third-party patch fights this mod can
                // neither cause nor resolve, and players would mistake the noise as
                // VanillaProfiler's fault. The comparison is case-sensitive against the
                // canonical HARMONY_ID we register patches under.
                if (!owners.Contains(VanillaProfilerMod.HARMONY_ID)) continue;

                // Module-level methods (rare in C# but legal in IL) have null
                // DeclaringType; render as "<global>" instead of a leading dot.
                result.Add(new ConflictEntry
                {
                    Method = methodName,
                    Owners = orderedOwners,
                });
            }
            result.Sort((a, b) => string.CompareOrdinal(a.Method, b.Method));
            signatureParts.Sort(StringComparer.Ordinal);
            return (result, string.Join("\n", signatureParts));
        }

        private static void AddOwners(HashSet<string> set, IEnumerable<Patch>? patches)
        {
            if (patches == null) return;
            foreach (var p in patches)
                if (!string.IsNullOrEmpty(p.owner))
                    set.Add(p.owner);
        }
    }
}
