using System;
using System.Diagnostics;
using System.Threading;

namespace VanillaProfiler
{
    /// <summary>
    /// Debug-only tripwire for the verified CS2 contract: ECS, Harmony postfixes,
    /// MonoBehaviour callbacks, and IMGUI all enter VanillaProfiler on the main thread.
    /// </summary>
    internal static class MainThreadGuard
    {
        private static int s_MainThreadId;
        private static int s_Warned;

        public static void Capture()
        {
            s_MainThreadId = Environment.CurrentManagedThreadId;
            s_Warned = 0;
        }

        [Conditional("DEBUG")]
        public static void AssertMainThread(string caller)
        {
            int expected = s_MainThreadId;
            int actual = Environment.CurrentManagedThreadId;
            if (expected == 0 || actual == expected)
                return;

            if (Interlocked.CompareExchange(ref s_Warned, 1, 0) != 0) return;
            ModLog.Warn(
                $"Main-thread contract violated in {caller}: expected thread {expected}, " +
                $"actual thread {actual}");
        }
    }
}
