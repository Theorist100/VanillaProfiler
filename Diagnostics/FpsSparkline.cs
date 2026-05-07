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
        // Cap how many samples a single OnFrame call can push. A long pause (alt-tab,
        // save load, multi-second GC) accumulates 30+ seconds before the next OnFrame,
        // and without this cap the backfill loop would dump CAPACITY identical samples
        // and wipe every prior FPS reading from the sparkline. Three is enough to
        // visually mark "something paused here" without losing history.
        private const int MAX_BACKFILL_SAMPLES = 3;
        // ▁▂▃▄▅▆▇█ — eight levels of fill
        private static readonly char[] BLOCKS = { '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█' };

        private readonly List<double> m_Samples = new(CAPACITY);
        private float m_AccumSeconds;
        private int m_AccumFrames;

        // Render cache: sparkline string changes only on Push (≤ 1 Hz), so OnGUI
        // can call Render every frame without allocating.
        private string m_CachedRender;
        private int m_CachedWidth;
        private int m_CachedSampleCount;

        /// <summary>Feed a per-frame delta. Sampler buckets up to 1 second internally.</summary>
        public void OnFrame(double frameMs, float deltaSeconds)
        {
            if (frameMs <= 0 || deltaSeconds <= 0) return;
            m_AccumFrames++;
            m_AccumSeconds += deltaSeconds;

            if (m_AccumSeconds < SAMPLE_INTERVAL_S) return;
            if (m_AccumFrames <= 0) return;

            double fps = m_AccumSeconds > 0 ? m_AccumFrames / m_AccumSeconds : 0;
            int wholeSamples = (int)(m_AccumSeconds / SAMPLE_INTERVAL_S);
            int samples = wholeSamples;
            if (samples < 1) samples = 1;
            if (samples > MAX_BACKFILL_SAMPLES) samples = MAX_BACKFILL_SAMPLES;
            for (int i = 0; i < samples; i++)
                Push(fps);

            if (wholeSamples > MAX_BACKFILL_SAMPLES)
            {
                m_AccumSeconds = 0;
                m_AccumFrames = 0;
                return;
            }

            float consumedSeconds = samples * SAMPLE_INTERVAL_S;
            float totalSeconds = m_AccumSeconds;
            m_AccumSeconds -= consumedSeconds;
            if (m_AccumSeconds < 0) m_AccumSeconds = 0;
            int consumedFrames = totalSeconds > 0
                ? (int)System.Math.Round(m_AccumFrames * (consumedSeconds / totalSeconds))
                : m_AccumFrames;
            m_AccumFrames -= consumedFrames;
            if (m_AccumFrames < 0) m_AccumFrames = 0;
        }

        public void Reset()
        {
            m_Samples.Clear();
            m_AccumSeconds = 0;
            m_AccumFrames = 0;
            InvalidateCache();
        }

        private void InvalidateCache()
        {
            m_CachedRender = null;
            m_CachedWidth = 0;
            m_CachedSampleCount = 0;
        }

        public int Count => m_Samples.Count;

        /// <summary>Render the last `width` samples as a sparkline string. Auto-scales to local min/max.</summary>
        public string Render(int width = CAPACITY)
        {
            if (m_Samples.Count == 0) return string.Empty;
            if (m_CachedRender != null
                && m_CachedWidth == width
                && m_CachedSampleCount == m_Samples.Count)
                return m_CachedRender;

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
                double norm = (m_Samples[i] - min) / range;
                int idx = rawRange < 0.001
                    ? BLOCKS.Length / 2
                    : (int)System.Math.Round(norm * (BLOCKS.Length - 1));
                if (idx < 0) idx = 0;
                if (idx >= BLOCKS.Length) idx = BLOCKS.Length - 1;
                sb.Append(BLOCKS[idx]);
            }
            m_CachedRender = sb.ToString();
            m_CachedWidth = width;
            m_CachedSampleCount = m_Samples.Count;
            return m_CachedRender;
        }

        private void Push(double fps)
        {
            if (m_Samples.Count >= CAPACITY)
                m_Samples.RemoveAt(0);
            m_Samples.Add(fps);
            InvalidateCache();
        }
    }
}
