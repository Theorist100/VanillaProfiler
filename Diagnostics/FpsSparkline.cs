using System.Collections.Generic;
using System.Text;

namespace VanillaProfiler.Diagnostics
{
    /// <summary>
    /// Rolling 60-second FPS history rendered as unicode block sparkline.
    /// One sample = one elapsed second of average FPS. Older samples drop off the left edge.
    /// </summary>
    public sealed class FpsSparkline
    {
        private const int CAPACITY = 60;
        private const float SAMPLE_INTERVAL_S = 1.0f;
        // ▁▂▃▄▅▆▇█ — eight levels of fill
        private static readonly char[] BLOCKS = { '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█' };

        private readonly List<double> m_Samples = new(CAPACITY);
        private float m_AccumSeconds;
        private int m_AccumFrames;

        /// <summary>Feed a per-frame delta. Sampler buckets up to 1 second internally.</summary>
        public void OnFrame(double frameMs, float deltaSeconds)
        {
            if (frameMs <= 0 || deltaSeconds <= 0) return;
            m_AccumFrames++;
            m_AccumSeconds += deltaSeconds;

            if (m_AccumSeconds < SAMPLE_INTERVAL_S) return;
            if (m_AccumFrames <= 0) return;

            double fps = m_AccumSeconds > 0 ? m_AccumFrames / m_AccumSeconds : 0;
            int samples = (int)m_AccumSeconds;
            if (samples < 1) samples = 1;
            if (samples > CAPACITY) samples = CAPACITY;
            for (int i = 0; i < samples; i++)
                Push(fps);

            m_AccumSeconds = 0;
            m_AccumFrames = 0;
        }

        public void Reset()
        {
            m_Samples.Clear();
            m_AccumSeconds = 0;
            m_AccumFrames = 0;
        }

        public int Count => m_Samples.Count;

        /// <summary>Render the last `width` samples as a sparkline string. Auto-scales to local min/max.</summary>
        public string Render(int width = CAPACITY)
        {
            if (m_Samples.Count == 0) return string.Empty;
            int n = m_Samples.Count;
            int take = n < width ? n : width;
            int skip = n - take;

            double min = double.MaxValue, max = double.MinValue;
            for (int i = skip; i < n; i++)
            {
                var v = m_Samples[i];
                if (v < min) min = v;
                if (v > max) max = v;
            }
            double rawRange = max - min;
            double range = rawRange < 0.001 ? 1.0 : rawRange;

            var sb = new StringBuilder(take);
            for (int i = skip; i < n; i++)
            {
                double norm = range > 0 ? (m_Samples[i] - min) / range : 0;
                int idx = rawRange < 0.001
                    ? BLOCKS.Length / 2
                    : (int)(norm * (BLOCKS.Length - 1));
                if (idx < 0) idx = 0;
                if (idx >= BLOCKS.Length) idx = BLOCKS.Length - 1;
                sb.Append(BLOCKS[idx]);
            }
            return sb.ToString();
        }

        private void Push(double fps)
        {
            if (m_Samples.Count >= CAPACITY)
                m_Samples.RemoveAt(0);
            m_Samples.Add(fps);
        }
    }
}
