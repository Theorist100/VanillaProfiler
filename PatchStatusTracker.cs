using System.Collections.Generic;
using System.Linq;

namespace VanillaProfiler
{
    /// <summary>
    /// Runtime-visible status for Harmony patch application. A profiler that loses
    /// one hook must say so in the overlay, not only in the startup log.
    /// </summary>
    public static class PatchStatusTracker
    {
        private static readonly object s_Lock = new();
        private static readonly Dictionary<string, string> s_Failures = new();

        public static bool HasFailures
        {
            get
            {
                lock (s_Lock)
                    return s_Failures.Count > 0;
            }
        }

        public static string Summary
        {
            get
            {
                lock (s_Lock)
                    return s_Failures.Count == 0
                        ? string.Empty
                        : "Profiler patches incomplete — check VanillaProfiler.log";
            }
        }

        public static string DetailedSummary
        {
            get
            {
                lock (s_Lock)
                {
                    if (s_Failures.Count == 0)
                        return string.Empty;

                    return "Profiler patches incomplete: "
                        + string.Join("; ", s_Failures.Select(kvp => $"{kvp.Key}: {kvp.Value}"));
                }
            }
        }

        public static void Clear()
        {
            lock (s_Lock)
                s_Failures.Clear();
        }

        public static void ReportSuccess(string patchName)
        {
            lock (s_Lock)
                s_Failures.Remove(patchName);

            ModLog.Info($"{patchName}: SUCCESS");
        }

        public static void ReportFailure(string patchName, string reason)
        {
            lock (s_Lock)
                s_Failures[patchName] = reason;

            ModLog.Warn($"{patchName}: FAILED - {reason}");
        }
    }
}
