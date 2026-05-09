using System.Collections.Generic;
using Unity.Profiling;

namespace VanillaProfiler.Aggregation
{
    /// <summary>
    /// Reusable ProfilerRecorder sample buffer. MemorySampler owns one instance so
    /// recorder reads do not allocate each report window.
    /// </summary>
    internal sealed class ProfilerRecorderSamples
    {
        private readonly List<ProfilerRecorderSample> m_Buffer;

        public ProfilerRecorderSamples(int capacity)
        {
            m_Buffer = new List<ProfilerRecorderSample>(capacity);
        }

        public long Average(ProfilerRecorder recorder)
        {
            if (!recorder.Valid || recorder.Capacity == 0) return 0;
            int count = recorder.Count;
            if (count == 0) return 0;

            m_Buffer.Clear();
            recorder.CopyTo(m_Buffer);
            long sum = 0;
            int n = m_Buffer.Count;
            for (int i = 0; i < n; i++)
                sum += m_Buffer[i].Value;
            return n > 0 ? sum / n : 0;
        }

        public void Clear()
        {
            m_Buffer.Clear();
        }

        public (long sum, long count) SumWithCount(ProfilerRecorder recorder)
        {
            if (!recorder.Valid || recorder.Capacity == 0) return (0, 0);
            int n = recorder.Count;
            if (n == 0) return (0, 0);

            m_Buffer.Clear();
            recorder.CopyTo(m_Buffer);
            long sum = 0;
            int captured = m_Buffer.Count;
            for (int i = 0; i < captured; i++)
                sum += m_Buffer[i].Value;
            return (sum, captured);
        }
    }
}
