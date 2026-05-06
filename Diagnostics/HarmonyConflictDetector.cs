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
        public static int LastConflictCount { get; private set; }

        public static void ScanAndLog(Profiler profiler)
        {
            if (profiler == null) return;
            try
            {
                var conflicts = Detect();
                LastConflictCount = conflicts.Count;
                var sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine($"HARMONY PATCHES SCAN  —  {conflicts.Count} potential conflicts");
                sb.AppendLine(new string('─', 50));
                if (conflicts.Count == 0)
                {
                    sb.AppendLine("  No multi-owner patches detected.");
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
            public string Method;
            public List<string> Owners;
        }

        private static List<ConflictEntry> Detect()
        {
            var result = new List<ConflictEntry>();
            foreach (var method in Harmony.GetAllPatchedMethods().ToArray())
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;

                var owners = new HashSet<string>(StringComparer.Ordinal);
                AddOwners(owners, info.Prefixes);
                AddOwners(owners, info.Postfixes);
                AddOwners(owners, info.Transpilers);
                AddOwners(owners, info.Finalizers);

                if (owners.Count < 2) continue;

                result.Add(new ConflictEntry
                {
                    Method = $"{method.DeclaringType?.FullName}.{method.Name}",
                    Owners = owners.OrderBy(s => s, StringComparer.Ordinal).ToList(),
                });
            }
            result.Sort((a, b) => string.CompareOrdinal(a.Method, b.Method));
            return result;
        }

        private static void AddOwners(HashSet<string> set, IEnumerable<Patch> patches)
        {
            if (patches == null) return;
            foreach (var p in patches)
                if (!string.IsNullOrEmpty(p.owner))
                    set.Add(p.owner);
        }
    }
}
