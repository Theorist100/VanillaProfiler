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
        private static Profiler? s_Current;

        /// <summary>
        /// Atomic read of the read/control surface. Returns null if not registered or
        /// already unregistered. Callers MUST capture the result into a local before use,
        /// otherwise the Unregister-then-call race re-emerges.
        /// </summary>
        public static IProfilerReadSurface? TryGetReadSurface() => Volatile.Read(ref s_Current);

        /// <summary>Atomic read of the patch-only surface used by Harmony/ECS hot paths.</summary>
        public static IProfilerPatchSurface? TryGetPatchSurface() => Volatile.Read(ref s_Current);

        /// <summary>
        /// Single-shot existence check. Safe to combine with a subsequent read because
        /// no caller currently does both — readers either skip work entirely (this gate)
        /// or capture a surface via TryGet*Surface(). Do not pair this with TryGet*Surface()
        /// in the same control flow; that re-introduces the Unregister race.
        /// </summary>
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
