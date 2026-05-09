using System.Collections.Generic;
using VanillaProfiler.Output;

namespace VanillaProfiler
{
    /// <summary>
    /// Routes log messages into the mod's own log file (Logs/VanillaProfiler.log) when the
    /// Profiler instance is ready. Before the Profiler is registered, messages are buffered
    /// in memory and flushed on first <see cref="Flush"/> call so the entire mod lifecycle
    /// — including the early init phase before Profiler construction — lands in one file.
    ///
    /// LogFileSink itself bypasses ModLog (would cause infinite recursion).
    /// </summary>
    internal static class ModLog
    {
        private static readonly object s_Lock = new();
        private static readonly List<(SystemLogLevel Level, string Msg)> s_Buffer = new();
        private const int BUFFER_CAP = 200;

        public static void Info(string msg) => Dispatch(SystemLogLevel.Info, msg);
        public static void Warn(string msg) => Dispatch(SystemLogLevel.Warn, msg);
        public static void Error(string msg) => Dispatch(SystemLogLevel.Error, msg);

        private static void Dispatch(SystemLogLevel level, string msg)
        {
            IProfilerReadSurface? target;
            bool mirrorToColossal;
            lock (s_Lock)
            {
                target = ProfilerHost.TryGetReadSurface();
                mirrorToColossal = target == null;
                if (mirrorToColossal && s_Buffer.Count < BUFFER_CAP)
                    s_Buffer.Add((level, msg));
            }

            if (target != null)
            {
                Route(target, level, msg);
                return;
            }

            // Buffer until Profiler is ready; also mirror to Colossal Log so the mod
            // is still visible in Modding.log if anything goes wrong before Flush().
            if (mirrorToColossal) MirrorToColossal(level, msg);
        }

        /// <summary>Replay every buffered message into the live Profiler. Call once after Register.</summary>
        public static void Flush()
        {
            IProfilerReadSurface? target;
            (SystemLogLevel, string)[] drained;
            lock (s_Lock)
            {
                target = ProfilerHost.TryGetReadSurface();
                if (target == null) return;
                drained = s_Buffer.ToArray();
                s_Buffer.Clear();
            }
            foreach (var (level, msg) in drained)
                Route(target, level, msg);
        }

        public static void ClearBuffer()
        {
            lock (s_Lock)
            {
                s_Buffer.Clear();
            }
        }

        private static void Route(IProfilerReadSurface p, SystemLogLevel level, string msg)
        {
            switch (level)
            {
                case SystemLogLevel.Info: p.LogInfo(msg); break;
                case SystemLogLevel.Warn: p.LogWarn(msg); break;
                case SystemLogLevel.Error: p.LogError(msg); break;
                default: p.LogInfo(msg); break;
            }
        }

        private static void MirrorToColossal(SystemLogLevel level, string msg)
        {
            var log = VanillaProfilerMod.Log;
            if (log == null) return;
            switch (level)
            {
                case SystemLogLevel.Info: log.Info(msg); break;
                case SystemLogLevel.Warn: log.Warn(msg); break;
                case SystemLogLevel.Error: log.Error(msg); break;
                default: log.Info(msg); break;
            }
        }
    }
}
