namespace VanillaProfiler.Overlay
{
    /// <summary>
    /// Pure formatting helpers shared by overlay modes. No state, no allocations beyond strings.
    /// </summary>
    public static class OverlayFormat
    {
        public static string Delta(double mb)
            => mb >= 0 ? $"+{mb:F1} MB" : $"{mb:F1} MB";

        public static string Count(int n)
        {
            if (n >= 1_000_000) return $"{n / 1_000_000.0:F1}M";
            if (n >= 1_000) return $"{n / 1_000.0:F0}k";
            return n.ToString();
        }

        public static string Truncate(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || maxChars <= 0) return string.Empty;
            if (value.Length <= maxChars) return value;
            if (maxChars <= 3) return value.Substring(0, maxChars);
            return value.Substring(0, maxChars - 3) + "...";
        }
    }
}
