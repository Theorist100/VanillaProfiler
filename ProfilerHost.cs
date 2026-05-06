using System;
using System.Threading;

namespace VanillaProfiler
{
    /// <summary>
    /// The single static reference into the profiler. Required because Harmony patch methods
    /// must be static, so they can't hold an instance via constructor injection.
    /// Lifecycle: VanillaProfilerMod.OnLoad → Register; VanillaProfilerMod.OnDispose → Unregister.
    /// </summary>
    public static class ProfilerHost
    {
        private static Profiler s_Current;

        /// <summary>
        /// Atomic read of the current Profiler instance. Returns null if not registered or
        /// already unregistered. Callers MUST capture the result into a local before use,
        /// otherwise the Unregister-then-call race re-emerges.
        /// </summary>
        public static Profiler TryGet() => Volatile.Read(ref s_Current);

        /// <summary>Throws if accessed outside Register..Unregister window. Prefer TryGet().</summary>
        public static Profiler Current
            => TryGet() ?? throw new InvalidOperationException(
                "ProfilerHost.Current accessed before Register or after Unregister");

        public static bool IsAvailable => Volatile.Read(ref s_Current) != null;

        public static void Register(Profiler profiler)
        {
            if (profiler == null) throw new ArgumentNullException(nameof(profiler));
            Volatile.Write(ref s_Current, profiler);
        }

        public static void Unregister()
        {
            Volatile.Write(ref s_Current, null);
        }
    }
}
