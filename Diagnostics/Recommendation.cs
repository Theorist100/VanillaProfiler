namespace VanillaProfiler.Diagnostics
{
    /// <summary>Severity flag drives how the overlay renders the row (color + ordering).</summary>
    public enum RecommendationLevel
    {
        Unknown = 0,
        Info,        // safe default that always helps a bit
        Suggested,   // signals fired but milder; try this if you want more headroom
        Critical,    // multiple signals fired; this is the likely culprit
    }

    /// <summary>One actionable suggestion produced by RecommendationEngine.</summary>
    public sealed class Recommendation
    {
        public RecommendationLevel Level;
        public string Title;       // short label, ≤ 40 chars
        public string Action;      // user instruction, ≤ 60 chars per line
        public string Reason;      // why we suggest it, optional, ≤ 60 chars
    }
}
