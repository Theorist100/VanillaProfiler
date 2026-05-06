using System;
using System.Diagnostics;

namespace VanillaProfiler
{
    /// <summary>
    /// Debug-only tripwire for the verified CS2 contract: ECS, Harmony postfixes,
    /// MonoBehaviour callbacks, and IMGUI all enter VanillaProfiler on the main thread.
    /// </summary>
    internal static class MainThreadGuard
    {
        private static int s_MainThreadId;
        private static bool s_Warned;

        public static void Capture()
        {
            s_MainThreadId = Environment.CurrentManagedThreadId;
            s_Warned = false;
        }

        [Conditional("DEBUG")]
        public static void AssertMainThread(string caller)
        {
            int expected = s_MainThreadId;
            int actual = Environment.CurrentManagedThreadId;
            if (expected == 0 || actual == expected)
                return;

            if (s_Warned) return;
            s_Warned = true;
            ModLog.Warn(
                $"Main-thread contract violated in {caller}: expected thread {expected}, " +
                $"actual thread {actual}");
        }
    }
}
