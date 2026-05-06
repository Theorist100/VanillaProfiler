namespace VanillaProfiler.Diagnostics
{
    /// <summary>
    /// Snapshot of in-game entity counts (citizens, vehicles, buildings).
    /// Populated by <see cref="CityContextSystem"/> and read by overlay/exporter.
    /// All counts are 0 in main menu / before save loads — overlay should hide the line in that case.
    /// </summary>
    public static class CityContext
    {
        public static int Citizens { get; private set; }
        public static int Vehicles { get; private set; }
        public static int Buildings { get; private set; }
        public static bool HasData => Citizens + Vehicles + Buildings > 0;

        public static void Update(int citizens, int vehicles, int buildings)
        {
            MainThreadGuard.AssertMainThread(nameof(Update));
            Citizens = citizens;
            Vehicles = vehicles;
            Buildings = buildings;
        }

        public static void Reset()
        {
            MainThreadGuard.AssertMainThread(nameof(Reset));
            Citizens = 0;
            Vehicles = 0;
            Buildings = 0;
        }
    }
}
