using System;
using System.Diagnostics;
using VanillaProfiler.Aggregation;
using VanillaProfiler.Diagnostics;
using VanillaProfiler.Output;

namespace VanillaProfiler
{
    /// <summary>
    /// Profiler instance — coordinates aggregation, memory sampling, report building, and sinks.
    /// Created and disposed by VanillaProfilerMod. Accessed from hot paths via ProfilerHost.TryGet.
    /// </summary>
    /// <remarks>
    /// THREADING CONTRACT
    /// ------------------
    /// Unity Entities invokes managed systems sequentially from ComponentSystemGroup.UpdateAllSystems,
    /// and Harmony Postfix code runs on the same thread as the patched method. Unity MonoBehaviour
    /// callbacks and IMGUI are also main-thread callbacks. Therefore all hot-path entry points here
    /// are main-thread only.
    ///
    /// DISPOSAL GUARD
    /// --------------
    /// <c>m_Disposed</c> rejects stale calls after the mod has been disposed. ProfilerHost still uses
    /// Volatile.Read/Write for explicit static publication across Harmony entry points.
    /// </remarks>
    public sealed class Profiler : IDisposable
    {
        private const double MS_PER_SECOND = 1000.0;
        private const double SPIKE_30FPS_MS = 1000.0 / 30.0;
        private const double SPIKE_20FPS_MS = 1000.0 / 20.0;

        private readonly MetricsAggregator m_Metrics = new();
        private readonly MemorySampler m_Memory = new();
        private readonly ReportBuilder m_Builder = new();
        private readonly IReportSink[] m_Sinks;

        // Frame timing — main thread only
        private long m_LastFrameTicks;
        private long m_LastReportTicks;
        private float m_ReportTimer;
        private int m_ReportCount;
        private bool m_HarmonyScanned;
        private bool m_ReplacementsLogged;
        private ProfilerLifecycleState m_LifecycleState = ProfilerLifecycleState.Initializing;

        // Defensive stale-reference guard for callbacks after OnDispose.
        private volatile bool m_Disposed;

        public OverlaySnapshot LastSnapshot { get; private set; }
        public HealthReport LastHealth { get; private set; }
        public MemoryHistory MemoryHistory { get; } = new();
        public FpsSparkline FpsSparkline { get; } = new();

        public ProfilerLifecycleState LifecycleState => m_LifecycleState;
        public bool IsGameLoaded => m_LifecycleState == ProfilerLifecycleState.Settling
            || m_LifecycleState == ProfilerLifecycleState.Active;
        public bool IsLoading => m_LifecycleState == ProfilerLifecycleState.LoadingCity;
        public bool IsSettling => m_LifecycleState == ProfilerLifecycleState.Settling;

        private float ReportInterval => SettingsStore.Current.ReportIntervalSec;

        public Profiler(IReportSink[] sinks)
        {
            m_Sinks = sinks ?? Array.Empty<IReportSink>();
            m_LastReportTicks = Stopwatch.GetTimestamp();
            foreach (var sink in m_Sinks) sink.Initialize();
        }

        public void Dispose()
        {
            m_Disposed = true;
            foreach (var sink in m_Sinks) sink.Shutdown();
            ResetSessionState();
            m_Memory.Dispose();
        }

        public void SetGameLoaded(bool gameLoaded)
        {
            MainThreadGuard.AssertMainThread(nameof(SetGameLoaded));
            if (m_Disposed) return;

            if (gameLoaded)
            {
                if (m_LifecycleState == ProfilerLifecycleState.Settling) return;
                m_LifecycleState = ProfilerLifecycleState.Settling;
            }
            else
            {
                if (m_LifecycleState == ProfilerLifecycleState.NoCity) return;
                m_LifecycleState = ProfilerLifecycleState.NoCity;
            }

            ResetSessionState();
            CityContext.Reset();
            if (m_LifecycleState == ProfilerLifecycleState.Settling)
                MemoryHistory.SuppressNextReports(5);
        }

        public void BeginLoading(bool loadsCity)
        {
            MainThreadGuard.AssertMainThread(nameof(BeginLoading));
            if (m_Disposed) return;
            if (!loadsCity)
            {
                SetGameLoaded(false);
                return;
            }

            m_LifecycleState = ProfilerLifecycleState.LoadingCity;
        }

        // ---------- Hot-path recording ----------

        /// <summary>Main thread (ECS GameSimulation phase).</summary>
        public void OnSimTick()
        {
            MainThreadGuard.AssertMainThread(nameof(OnSimTick));
            if (m_Disposed) return;
            m_Metrics.RecordSimTick();
        }

        /// <summary>Main thread (SystemBase.Update Harmony Postfix).</summary>
        public void RecordSystem(string name, long ticks, bool isVanilla, string modName = null)
        {
            MainThreadGuard.AssertMainThread(nameof(RecordSystem));
            if (m_Disposed) return;
            m_Metrics.RecordSystem(name, ticks, isVanilla, modName);
        }

        /// <summary>
        /// Main thread (SystemBase.Update Harmony Postfix). Routes a vanilla
        /// system whose OnUpdate is currently patched by a foreign Harmony
        /// prefix to the dedicated bucket — the elapsed time blends mod
        /// prefix and (possibly skipped) vanilla original, which is honest
        /// total cost but not attributable to either side.
        /// </summary>
        public void RecordPatchedVanilla(string name, long ticks)
        {
            MainThreadGuard.AssertMainThread(nameof(RecordPatchedVanilla));
            if (m_Disposed) return;
            m_Metrics.RecordPatchedVanilla(name, ticks);
        }

        /// <summary>Main thread (UpdateSystem phase Postfix).</summary>
        public void RecordPhase(string name, long ticks)
        {
            MainThreadGuard.AssertMainThread(nameof(RecordPhase));
            if (m_Disposed) return;
            m_Metrics.RecordPhase(name, ticks);
        }

        /// <summary>Main thread (Rendering phase Postfix). Drives the report cadence.</summary>
        public void OnFrame()
        {
            MainThreadGuard.AssertMainThread(nameof(OnFrame));
            if (m_Disposed) return;

            long now = Stopwatch.GetTimestamp();
            if (m_LastFrameTicks == 0)
            {
                m_LastFrameTicks = now;
                m_LastReportTicks = now;
                return;
            }

            long delta = now - m_LastFrameTicks;
            m_LastFrameTicks = now;

            double frameMs = delta * MS_PER_SECOND / Stopwatch.Frequency;
            float frameSec = (float)(delta * 1.0 / Stopwatch.Frequency);

            m_Metrics.RecordFrame(delta, frameMs, SPIKE_30FPS_MS, SPIKE_20FPS_MS);
            FpsSparkline.OnFrame(frameMs, frameSec);
            if (IsGameLoaded)
                SpikeScreenshot.OnFrame(frameMs);

            float reportInterval = ReportInterval;
            if (reportInterval <= 0f) reportInterval = 5f;
            m_ReportTimer += frameSec;
            if (m_ReportTimer >= reportInterval)
            {
                m_ReportTimer %= reportInterval;
                Report(now);
            }
        }

        // ---------- Report orchestration ----------

        public void ForceReport()
        {
            MainThreadGuard.AssertMainThread(nameof(ForceReport));
            if (m_Disposed) return;
            Report(Stopwatch.GetTimestamp());
            m_ReportTimer = 0f;
        }

        private void Report(long nowTicks)
        {
            MainThreadGuard.AssertMainThread(nameof(Report));
            if (m_Disposed) return;

#pragma warning disable CIVIC021 // Stopwatch.Frequency is a hardware constant, always >= 1
            float elapsedSec = (float)((nowTicks - m_LastReportTicks) / (double)Stopwatch.Frequency);
#pragma warning restore CIVIC021
            m_LastReportTicks = nowTicks;
            if (elapsedSec <= 0.001f) elapsedSec = ReportInterval;

            if (!IsGameLoaded)
            {
                m_Memory.Sample(elapsedSec);
                using (m_Metrics.Drain()) { }
                return;
            }

            // Deferred Harmony scan once other mods finish patching
            if (!m_HarmonyScanned)
            {
                m_HarmonyScanned = true;
                HarmonyConflictDetector.ScanAndLog(this);
            }

            using (var lease = m_Metrics.Drain())
            {
                var metrics = lease.Sample;
                metrics.ElapsedSec = elapsedSec;
                if (metrics.FrameCount == 0)
                {
                    LogInfo("Skipped empty report (no frames collected).");
                    return;
                }

                m_ReportCount++;
                var memory = m_Memory.Sample(elapsedSec);
                MemoryHistory.Record(memory.ManagedBytes, elapsedSec);

                var (snapshot, health) = m_Builder.Build(metrics, memory, MemoryHistory);

                // Replaced-systems scan walks World.All, so it only makes sense
                // once a city is live (IsGameLoaded gate above already enforces
                // that). Run every cycle — mods may toggle Enabled at runtime
                // (mod-options screens flip vanilla systems on the fly), so a
                // one-shot scan would freeze stale data into the overlay.
                var replacements = SystemReplacementDetector.Scan();
                snapshot.ReplacedVanillaSystems = ToTuples(replacements, metrics.PatchedVanillaSystems);
                if (!m_ReplacementsLogged)
                {
                    m_ReplacementsLogged = true;
                    SystemReplacementDetector.LogTo(this, replacements);
                }

                LastSnapshot = snapshot;
                LastHealth = health;
                m_LifecycleState = ProfilerLifecycleState.Active;

                // Per-sink isolation: a misbehaving sink (e.g. disk full, AV scan locking
                // the file) must not abort delivery to the others. We log the first failure
                // and keep going; sink internals are expected to handle their own retries.
                foreach (var sink in m_Sinks)
                {
                    try { sink.WriteReport(m_ReportCount, snapshot, health, metrics, memory); }
                    catch (Exception ex)
                    {
                        VanillaProfilerMod.Log?.Warn($"Sink {sink?.GetType().Name} WriteReport failed: {ex}");
                    }
                }
            }
        }

        /// <summary>Info-level system message routed to all sinks. Main thread.</summary>
        public void LogInfo(string msg) => WriteSystem(SystemLogLevel.Info, msg);

        /// <summary>Warning-level system message routed to all sinks. Main thread.</summary>
        public void LogWarn(string msg) => WriteSystem(SystemLogLevel.Warn, msg);

        /// <summary>Error-level system message routed to all sinks. Main thread.</summary>
        public void LogError(string msg) => WriteSystem(SystemLogLevel.Error, msg);

        private void WriteSystem(SystemLogLevel level, string msg)
        {
            if (m_Disposed) return;
            foreach (var sink in m_Sinks)
            {
                try
                {
                    sink.WriteSystemMessage(level, msg);
                }
                catch (Exception ex)
                {
                    VanillaProfilerMod.Log?.Warn($"VanillaProfiler sink system-write failed: {ex}");
                }
            }
        }

        private static (string, string, double)[] ToTuples(
            System.Collections.Generic.List<SystemReplacementDetector.Replacement> list,
            System.Collections.Generic.Dictionary<string, Aggregation.PhaseData> patchedMs)
        {
            if (list == null || list.Count == 0) return Array.Empty<(string, string, double)>();
            var arr = new (string, string, double)[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                double ms = 0.0;
                if (patchedMs != null && patchedMs.TryGetValue(r.VanillaSystem, out var phase))
                    ms = phase.TotalTicks * 1000.0 / Stopwatch.Frequency;
                arr[i] = (r.VanillaSystem, r.OwnerMod, ms);
            }
            return arr;
        }

        private void ResetSessionState()
        {
            m_Metrics.Reset();
            m_Memory.ResetBaseline();
            MemoryHistory.Reset();
            FpsSparkline.Reset();
            SpikeScreenshot.Reset();
            m_HarmonyScanned = false;
            m_ReplacementsLogged = false;
            m_LastFrameTicks = 0;
            m_LastReportTicks = Stopwatch.GetTimestamp();
            m_ReportTimer = 0f;
            // Snapshot belongs to the previous session — drop it. Overlay reads
            // IsSettling to decide what to render in this gap (explicit settling
            // banner) instead of either a blank panel or numbers from a save the
            // player isn't looking at anymore.
            LastSnapshot = null;
            LastHealth = null;
        }

    }
}
