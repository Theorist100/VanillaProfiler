using System;
using System.Collections.Generic;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;

namespace VanillaProfiler.Aggregation
{
    internal static class ProfilerRecorderFactory
    {
        /// <summary>
        /// Resolve a marker by category name + stat name when Unity does not expose
        /// the category as a typed ProfilerCategory constant.
        /// </summary>
        public static ProfilerRecorder StartByHandle(string categoryName, string statName, int capacity)
        {
            var handles = new List<ProfilerRecorderHandle>(256);
            try
            {
                ProfilerRecorderHandle.GetAvailable(handles);
            }
            catch
            {
                return default;
            }

            for (int i = 0; i < handles.Count; i++)
            {
                var desc = ProfilerRecorderHandle.GetDescription(handles[i]);
                if (string.Equals(desc.Category.Name, categoryName, StringComparison.Ordinal)
                    && string.Equals(desc.Name, statName, StringComparison.Ordinal))
                {
                    var recorder = new ProfilerRecorder(handles[i], capacity, ProfilerRecorderOptions.Default);
                    recorder.Start();
                    return recorder;
                }
            }
            return default;
        }
    }
}
