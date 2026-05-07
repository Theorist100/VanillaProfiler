using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Unity.Profiling.LowLevel.Unsafe;

namespace VanillaProfiler.Aggregation
{
    /// <summary>
    /// One-shot enumeration of every ProfilerRecorder marker registered by the running
    /// Unity / CS2 build. Logged at startup so MemorySampler's hardcoded counter names
    /// can be verified after Unity version updates change marker visibility (e.g.
    /// "Gfx Used Memory" was renamed to "Video Used Memory" between Unity versions).
    ///
    /// Output goes through ModLog → Profiler.LogInfo → LogFileSink, producing one
    /// timestamped line per marker. Verbose, but only runs once per session and keeps
    /// the full list grep-able alongside reports.
    /// </summary>
    internal static class MarkerEnumerator
    {
        public static void LogAvailable()
        {
            // Plain managed List, not NativeList — Unity API takes a managed list directly
            // and resolves disposal via GC like any other managed allocation.
            var handles = new List<ProfilerRecorderHandle>(256);
            try
            {
                ProfilerRecorderHandle.GetAvailable(handles);
            }
            catch (Exception ex)
            {
                if (ex is OutOfMemoryException || ex is ThreadAbortException) throw;
                ModLog.Warn($"ProfilerRecorderHandle.GetAvailable failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            int total = handles.Count;
            if (total == 0)
            {
                ModLog.Info("Available ProfilerRecorder markers: none — CS2 build exposes no recorders");
                return;
            }

            // Bucket by category for compact output. ProfilerCategory.Name is a managed
            // string; null-coalesce protects us from the rare unnamed categories some
            // Unity native subsystems register.
            var byCategory = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            for (int i = 0; i < total; i++)
            {
                var desc = ProfilerRecorderHandle.GetDescription(handles[i]);
                string cat = desc.Category.Name ?? "(no-category)";
                if (!byCategory.TryGetValue(cat, out var list))
                {
                    list = new List<string>();
                    byCategory[cat] = list;
                }
                string name = desc.Name ?? "(unnamed)";
                list.Add($"{name} [{desc.UnitType}]");
            }

            var sb = new StringBuilder(8192);
            sb.AppendLine($"Available ProfilerRecorder markers: {total} total across {byCategory.Count} categories");

            var orderedCats = new List<string>(byCategory.Keys);
            orderedCats.Sort(StringComparer.Ordinal);
            foreach (string cat in orderedCats)
            {
                var list = byCategory[cat];
                list.Sort(StringComparer.Ordinal);
                sb.AppendLine($"  [{cat}] ({list.Count})");
                foreach (string entry in list)
                    sb.AppendLine($"    {entry}");
            }
            ModLog.Info(sb.ToString().TrimEnd());
        }
    }
}
