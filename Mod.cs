using System;
using System.IO;
using System.Linq;
using Colossal.Logging;
using Colossal.Serialization.Entities;
using Game;
using Game.Modding;
using Game.SceneFlow;
using HarmonyLib;
using VanillaProfiler.Aggregation;
using VanillaProfiler.Diagnostics;
using VanillaProfiler.Output;

namespace VanillaProfiler
{
    /// <summary>
    /// Minimal profiler mod — zero gameplay, zero entities.
    /// Measures FPS, frame time, simulation phases, memory.
    /// Output: Logs/VanillaProfiler.log in game logs directory.
    /// </summary>
    public sealed class VanillaProfilerMod : IMod
    {
        internal const string HARMONY_ID = "com.vanillaprofiler";

        public static ILog? Log { get; private set; }

        private Harmony? m_Harmony;
        private Profiler? m_Profiler;
        private UnityEngine.GameObject? m_OverlayObject;

        public void OnLoad(UpdateSystem updateSystem)
        {
            MainThreadGuard.Capture();
            Log = LogManager.GetLogger(nameof(VanillaProfiler));
            ModLog.Info("VanillaProfiler loading...");

            try
            {
                SettingsStore.Load();

                var logDir = LogFileSink.GetLogDirectory(UnityEngine.Application.persistentDataPath);

                RegisterProfiler(logDir);
                InitializeProfilerLifecycle();
                RegisterGameLifecycleCallbacks();
                ModLog.Info($"Log directory: {logDir}");
                ModAttribution.PrewarmLoadedAssemblies();

                RegisterSimulationSystems(updateSystem);
                ApplyHarmonyPatches();
                CreateOverlay();

                ModLog.Info("VanillaProfiler ready.");
            }
            catch (Exception ex)
            {
                ModLog.Error($"VanillaProfiler load failed: {ex}");
                Cleanup(logDispose: false);
                throw;
            }
        }

        public void OnDispose()
        {
            Cleanup(logDispose: true);
        }

        private void RegisterProfiler(string logDir)
        {
            m_Profiler = new Profiler(new IReportSink[] { new LogFileSink(logDir) });
            ProfilerHost.Register(m_Profiler);
            ModLog.Flush();  // replay buffered early-init messages before later live diagnostics

            // Diagnostic only; confirms MemorySampler's hardcoded counter names are still valid.
            MarkerEnumerator.LogAvailable();
        }

        private void InitializeProfilerLifecycle()
        {
            m_Profiler?.InitializeFromCurrentMode(GameManager.instance.gameMode);
        }

        private static void RegisterGameLifecycleCallbacks()
        {
            // Wipe stale city counts when the player returns to the main menu.
            GameManager.instance.onGameLoadingComplete += OnGameLoadingComplete;
            // Hide overlay/badges during city loading only.
            GameManager.instance.onGamePreload += OnGamePreload;
        }

        private static void RegisterSimulationSystems(UpdateSystem updateSystem)
        {
            updateSystem.UpdateAt<SimTickCounterSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<CityContextSystem>(SystemUpdatePhase.GameSimulation);
        }

        private void ApplyHarmonyPatches()
        {
            m_Harmony = new Harmony(HARMONY_ID);
            bool phasePatchAvailable = AccessTools.Method(typeof(UpdateSystem), "Update",
                new[] { typeof(SystemUpdatePhase) }) != null;
            bool indexedPatchAvailable = AccessTools.Method(typeof(UpdateSystem), "Update",
                new[] { typeof(SystemUpdatePhase), typeof(uint), typeof(int) }) != null;

            ModLog.Info($"Hook check: Update(SystemUpdatePhase) = {(phasePatchAvailable ? "OK" : "MISSING")}");
            ModLog.Info($"Hook check: Update(SystemUpdatePhase,uint,int) = {(indexedPatchAvailable ? "OK" : "MISSING")}");

            m_Harmony.PatchAll(typeof(VanillaProfilerMod).Assembly);

            int patchCount = m_Harmony.GetPatchedMethods()
                .Count(method => Harmony.GetPatchInfo(method)?.Owners?.Contains(HARMONY_ID) == true);
            ModLog.Info($"Harmony patches applied: {patchCount}");
        }

        private void CreateOverlay()
        {
            m_OverlayObject = new UnityEngine.GameObject("VanillaProfilerOverlay");
            UnityEngine.Object.DontDestroyOnLoad(m_OverlayObject);
            m_OverlayObject.AddComponent<ProfilerOverlay>();
        }

        private static void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            // Profile only the actual gameplay session. Editor mode loads a similar
            // ECS world but the player wouldn't be looking at gameplay performance —
            // it would just clutter the editor with our overlay.
            ProfilerHost.TryGetReadSurface()?.SetGameLoaded(mode == GameMode.Game);
        }

        private static void OnGamePreload(Purpose purpose, GameMode mode)
        {
            ProfilerHost.TryGetReadSurface()?.BeginLoading(mode == GameMode.Game);
        }

        private void Cleanup(bool logDispose)
        {
            if (logDispose)
                TryCleanup("log dispose", () => ModLog.Info("VanillaProfiler disposing"));

            TryCleanup("unsubscribe loading callback",
                () => GameManager.instance.onGameLoadingComplete -= OnGameLoadingComplete);
            TryCleanup("unsubscribe preload callback",
                () => GameManager.instance.onGamePreload -= OnGamePreload);
            TryCleanup("unregister profiler host", ProfilerHost.Unregister);
            TryCleanup("unpatch harmony", () => m_Harmony?.UnpatchAll(HARMONY_ID));
            TryCleanup("destroy overlay", () =>
            {
                if (m_OverlayObject == null) return;
                UnityEngine.Object.Destroy(m_OverlayObject);
                m_OverlayObject = null;
            });
            TryCleanup("dispose profiler", () =>
            {
                m_Profiler?.Dispose();
                m_Profiler = null;
            });
            TryCleanup("reset attribution", ModAttribution.Reset);
            TryCleanup("reset auto-profiler", SystemAutoProfiler.Reset);
            TryCleanup("reset replacement detector", SystemReplacementDetector.Reset);
            TryCleanup("reset harmony conflict detector", HarmonyConflictDetector.Reset);
            TryCleanup("reset city context", CityContext.Reset);
            TryCleanup("clear buffered log messages", ModLog.ClearBuffer);
            m_Harmony = null;
        }

        private static void TryCleanup(string step, Action? action)
        {
            try { action?.Invoke(); }
            catch (Exception ex) { Log?.Warn($"VanillaProfiler cleanup failed ({step}): {ex}"); }
        }
    }
}
