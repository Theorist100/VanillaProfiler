using System;
using System.Diagnostics;
using Game;
using VanillaProfiler.Aggregation;
using VanillaProfiler.Diagnostics;
using VanillaProfiler.Output;

namespace VanillaProfiler
{
    /// <summary>
    /// Profiler instance — coordinates aggregation, memory sampling, report building, and sinks.
    /// Created and disposed by VanillaProfilerMod. Accessed from hot paths via ProfilerHost.TryGetPatchSurface.
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
    public sealed class Profiler : IProfilerPatchSurface, IProfilerReadSurface, IDisposable
    {
        private const double SPIKE_30FPS_MS = 1000.0 / 30.0;
        private const double SPIKE_20FPS_MS = 1000.0 / 20.0;

        private readonly Func<ProfilerSettingsSnapshot> m_Settings = () => SettingsStore.Snapshot;
        private readonly MetricsAggregator m_Metrics;
        private readonly MemorySampler m_Memory = new();
        private readonly ReportBuilder m_Builder = new();
        private readonly ReportDispatcher m_Dispatcher;
        private readonly ReportScheduler m_Scheduler = new();
        private readonly SpikeScreenshot m_SpikeScreenshots = new();
        private readonly GraphicsSettingsProbe m_GraphicsSettings = new();
        private readonly ProfilerSessionState m_Session = new();

        // Frame timing — main thread only
        private int m_ReportCount;
        private bool m_HarmonyScanned;
        private bool m_ReplacementsLogged;

        // Defensive stale-reference guard for callbacks after OnDispose.
        private volatile bool m_Disposed;

        public OverlaySnapshot? LastSnapshot => m_Session.LastSnapshot;
        public HealthReport? LastHealth => m_Session.LastHealth;
        public MemoryHistory MemoryHistory { get; }
        public FpsSparkline FpsSparkline { get; } = new();
        public SpikeScreenshot SpikeScreenshots => m_SpikeScreenshots;
        public GraphicsSettingsProbe GraphicsSettings => m_GraphicsSettings;
        public RecommendationEngine Recommendations { get; }

        public ProfilerLifecycleState LifecycleState => m_Session.LifecycleState;
        public bool IsGameLoaded => m_Session.IsGameLoaded;
        public bool IsLoading => m_Session.IsLoading;
        public bool IsSettling => m_Session.IsSettling;
        public bool ShouldProfileVanillaSystems => m_Settings().Settings.ProfileVanillaSystems;

        private float ReportInterval => m_Settings().Settings.ReportIntervalSec;

        public Profiler(IReportSink[] sinks)
        {
            m_Metrics = new MetricsAggregator(m_Settings);
            MemoryHistory = new MemoryHistory(m_Settings);
            Recommendations = new RecommendationEngine(m_GraphicsSettings);
            m_Dispatcher = new ReportDispatcher(sinks);
            m_Scheduler.Reset();
            m_Dispatcher.Initialize();
        }

        public void Dispose()
        {
            m_Disposed = true;
            m_Dispatcher.Shutdown();
            ResetForBoundary(SessionBoundary.Dispose);
            m_Memory.Dispose();
        }

        public void InitializeFromCurrentMode(GameMode current)
        {
            MainThreadGuard.AssertMainThread(nameof(InitializeFromCurrentMode));
            if (m_Disposed) return;
            if (!m_Session.Initialize(current)) return;

            ResetForBoundary(m_Session.IsSettling
                ? SessionBoundary.GameLoaded
                : SessionBoundary.GameUnloaded);
            if (m_Session.IsSettling)
                PrepareLoadedGameSession();
        }

        public void SetGameLoaded(bool gameLoaded)
        {
            MainThreadGuard.AssertMainThread(nameof(SetGameLoaded));
            if (m_Disposed) return;

            if (!m_Session.SetGameLoaded(gameLoaded)) return;

            ResetForBoundary(gameLoaded ? SessionBoundary.GameLoaded : SessionBoundary.GameUnloaded);
            if (m_Session.IsSettling)
                PrepareLoadedGameSession();
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

            if (!m_Session.BeginLoading()) return;
            ResetForBoundary(SessionBoundary.BeginLoading);
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
        public void RecordSystem(string name, long selfTicks, long inclusiveTicks, bool isVanilla, string? modName = null)
        {
            MainThreadGuard.AssertMainThread(nameof(RecordSystem));
            if (m_Disposed) return;
            m_Metrics.RecordSystem(name, selfTicks, inclusiveTicks, isVanilla, modName);
        }

        /// <summary>
        /// Main thread (SystemBase.Update Harmony Postfix). Routes a vanilla
        /// system whose OnUpdate is currently patched by a foreign Harmony
        /// prefix to the dedicated bucket — the elapsed time blends mod
        /// prefix and (possibly skipped) vanilla original, which is honest
        /// total cost but not attributable to either side.
        /// </summary>
        public void RecordPatchedVanilla(string name, long selfTicks, long inclusiveTicks)
        {
            MainThreadGuard.AssertMainThread(nameof(RecordPatchedVanilla));
            if (m_Disposed) return;
            m_Metrics.RecordPatchedVanilla(name, selfTicks, inclusiveTicks);
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
            if (!m_Scheduler.TryAdvanceFrame(now, ReportInterval, out var timing, out bool reportDue))
                return;

            m_Metrics.RecordFrame(timing.DeltaTicks, timing.FrameMs, SPIKE_30FPS_MS, SPIKE_20FPS_MS);
            FpsSparkline.OnFrame(timing.FrameMs, timing.FrameSec);
            if (IsGameLoaded)
                m_SpikeScreenshots.OnFrame(timing.FrameMs, m_Settings());

            if (reportDue)
                Report(now);
        }

        // ---------- Report orchestration ----------

        public void ForceReport()
        {
            MainThreadGuard.AssertMainThread(nameof(ForceReport));
            if (m_Disposed) return;
            Report(Stopwatch.GetTimestamp());
            m_Scheduler.ResetReportTimer();
        }

        private void Report(long nowTicks)
        {
            MainThreadGuard.AssertMainThread(nameof(Report));
            if (m_Disposed) return;

            float elapsedSec = m_Scheduler.ConsumeElapsedSeconds(nowTicks, ReportInterval);

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
                AttachReplacementSnapshot(snapshot, metrics);
                PublishSnapshot(snapshot, health);
                WriteReports(metrics, memory, snapshot, health);
            }
        }

        private void AttachReplacementSnapshot(OverlaySnapshot snapshot, MetricsSample metrics)
        {
            // Run every cycle; mods may toggle Harmony patches at runtime.
            var replacements = SystemReplacementDetector.Scan();
            snapshot.ReplacedVanillaSystems = ToTuples(replacements, metrics.PatchedVanillaSystems);
            if (m_ReplacementsLogged) return;
            m_ReplacementsLogged = true;
            SystemReplacementDetector.LogTo(this, replacements);
        }

        private void PublishSnapshot(OverlaySnapshot snapshot, HealthReport health)
        {
            m_Session.Publish(snapshot, health);
        }

        private void WriteReports(
            MetricsSample metrics, MemorySample memory, OverlaySnapshot snapshot, HealthReport health)
        {
            m_Dispatcher.WriteReport(m_ReportCount, snapshot, health, metrics, memory);
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
            m_Dispatcher.WriteSystem(level, msg);
        }

        private static (string, string, double)[] ToTuples(
            System.Collections.Generic.IReadOnlyList<SystemReplacementDetector.Replacement>? list,
            System.Collections.Generic.IReadOnlyDictionary<string, Aggregation.PhaseData>? patchedMs)
        {
            if (list == null || list.Count == 0) return Array.Empty<(string, string, double)>();
            var arr = new (string, string, double)[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                double ms = 0.0;
                if (patchedMs != null && patchedMs.TryGetValue(r.VanillaSystem, out var phase))
                    ms = phase.InclusiveTicks * 1000.0 / Stopwatch.Frequency;
                arr[i] = (r.VanillaSystem, r.OwnerMod, ms);
            }
            return arr;
        }

        private void ResetForBoundary(SessionBoundary kind)
        {
            m_Metrics.Reset();
            m_Memory.ResetSession();
            MemoryHistory.OnSessionBoundary();
            FpsSparkline.Reset();
            m_SpikeScreenshots.Reset();
            m_GraphicsSettings.Invalidate();
            SystemReplacementDetector.Reset();
            switch (kind)
            {
                case SessionBoundary.Dispose:
                case SessionBoundary.BeginLoading:
                case SessionBoundary.GameLoaded:
                case SessionBoundary.GameUnloaded:
                    CityContext.Reset();
                    break;
            }
            m_HarmonyScanned = false;
            m_ReplacementsLogged = false;
            m_Scheduler.Reset();
            m_Session.ClearReadState();
        }

        private void PrepareLoadedGameSession()
        {
            SystemReplacementDetector.Scan();
            MemoryHistory.SuppressNextReports(5);
        }

    }
}
