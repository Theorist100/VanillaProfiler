using System;
using System.IO;
using UnityEngine;
using VanillaProfiler.Diagnostics;
using VanillaProfiler.Overlay;
using VanillaProfiler.Overlay.Modes;
using VanillaProfiler.Output;

namespace VanillaProfiler
{
    /// <summary>
    /// Thin MonoBehaviour shell. Owns lifecycle (Start/Update/OnGUI), the active mode,
    /// the panel rect, and routes hotkeys + on-panel buttons to a single set of action
    /// methods. Drawing helpers live in PanelLayout / OverlayBadges / MainPanelButtons
    /// so this file stays focused on wiring.
    /// </summary>
    public sealed class ProfilerOverlay : MonoBehaviour
    {
        private const float WIDTH = 440f;
        internal const int MAIN_WINDOW_ID = 0xC1F1C0;
        private const float MIN_VISIBLE_PX = 80f;

        // Throttle persistence of drag positions so a held-mouse-move doesn't write
        // the JSON file on every frame.
        private const float SAVE_DEBOUNCE_S = 0.5f;

        private readonly OverlayTheme m_Theme = new();
        private readonly Toast m_Toast = new();
        private readonly SettingsPanel m_Settings = new();
        private readonly OverlayInputHandler m_Input = new();
        private readonly OverlayState m_State = new();

        private OverlayModeDescriptor[] m_Modes = Array.Empty<OverlayModeDescriptor>();
        private MainPanelButtons m_Buttons = null!;
        private MainOverlayPanelRenderer m_MainPanel = null!;

        private PanelPositionController m_PanelPosition = null!;
        private bool m_SettingsSaveDirty;
        private float m_SettingsSaveDirtyAt;

        private void Start()
        {
            m_Modes = OverlayModeCatalog.CreateDefaultModes();
            m_Buttons = new MainPanelButtons(m_Theme, m_Modes, new MainPanelButtons.Actions
            {
                OpenSettings = DoOpenSettings,
                ExportReport = DoExportReport,
                CycleAnchor = DoCycleAnchor,
            });
            m_PanelPosition = new PanelPositionController(
                new Rect(OverlayPanel.MARGIN, OverlayPanel.MARGIN, WIDTH, 100f),
                MIN_VISIBLE_PX,
                () => (SettingsStore.Snapshot.Settings.PanelX, SettingsStore.Snapshot.Settings.PanelY),
                (x, y) =>
                {
                    SettingsStore.Update(settings => settings.With(panelX: x, panelY: y), save: false);
                },
                ScheduleSettingsSave);
            m_MainPanel = new MainOverlayPanelRenderer(
                m_Theme,
                m_Modes,
                m_Buttons,
                m_PanelPosition,
                m_State,
                WIDTH,
                SetModeIndex);

            LoadInitialSettings();

            // Apply has already committed the new settings to SettingsStore. DefaultMode is a
            // startup preference, so applying unrelated settings must not jump screens.
            m_Settings.OnApplied += (s, e) =>
            {
                ApplyLiveSettings();
                m_Toast.Show("Settings saved");
            };
        }

        private void LoadInitialSettings()
        {
            var s = SettingsStore.Snapshot.Settings;
            m_State.Initialize(s.DefaultMode, s.Anchor, m_Modes);
            m_PanelPosition.Load();
        }

        private void ApplyLiveSettings()
        {
            var s = SettingsStore.Snapshot.Settings;
            var nextAnchor = (Anchor)Mathf.Clamp(s.Anchor, 0, 3);
            if (s.PanelX < 0f || s.PanelY < 0f)
                m_PanelPosition.Load();
            if (nextAnchor != m_State.Anchor)
            {
                // Choosing an anchor is an explicit snap request. If the panel had a
                // manual drag position, discard it so the anchor can take effect.
                m_PanelPosition.SnapToAnchor();
            }
            m_State.SetAnchor(nextAnchor);
        }

        private void Update()
        {
            DispatchCommand(m_Input.Poll(m_Settings.IsOpen));
            m_Settings.SyncLiveSettings();

            // Debounced save: panel drags and corner snaps update SettingsStore
            // immediately, then persist the final state once after input settles.
            if (m_SettingsSaveDirty && Time.realtimeSinceStartup - m_SettingsSaveDirtyAt >= SAVE_DEBOUNCE_S)
                FlushPendingSettingsSave();
        }

        private void OnDestroy()
        {
            FlushPendingSettingsSave();
            m_Settings.FlushPendingPosition();
            m_Theme.Release();
        }

        private void OnGUI()
        {
            m_Theme.EnsureInitialized();

            var settings = SettingsStore.Snapshot;
            float scale = PanelLayout.ResolveScale(settings);
            var savedMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            try
            {
                DrawScaled(scale, settings);
            }
            finally
            {
                GUI.matrix = savedMatrix;
            }
        }

        private void DrawScaled(float scale, ProfilerSettingsSnapshot settings)
        {
            var profiler = ProfilerHost.TryGetReadSurface();
            var snapshot = profiler?.LastSnapshot;
            var health = profiler?.LastHealth;
            var mode = m_Modes[m_State.ModeIndex].Mode;
            var lifecycle = profiler?.LifecycleState ?? ProfilerLifecycleState.Initializing;

            // Render order = Z-order (last drawn = topmost):
            //   1. Mode panel / badge (background layer)
            //   2. Settings panel
            //   3. Toast (notification, no controls)
            //
            // The overlay only appears once a city is actually being measured. Splash,
            // main menu, editor menu and city-loading screens all stay clean. Settings
            // and toast still render because the player may want to configure the mod
            // pre-game; they are not lifecycle-gated.
            //
            if (lifecycle == ProfilerLifecycleState.Settling)
                OverlayBadges.DrawSettling(m_Theme, scale);
            else if (lifecycle == ProfilerLifecycleState.Active && mode.IsHidden)
            {
                // True-hide opt-out: a player who knows the Ctrl+F9 hotkey can disable
                // the hint pill so it stops overlapping top-right HUD buttons. Default
                // is on so a first-time hider doesn't lose the way back.
                if (settings.Settings.HideHintBadge)
                    OverlayBadges.DrawHidden(m_Theme, scale);
            }
            else if (lifecycle == ProfilerLifecycleState.Active)
            {
                if (profiler != null && snapshot != null && health != null)
                    m_MainPanel.DrawMainPanel(profiler, mode, snapshot, health, scale, settings);
            }
            // else: Initializing / LoadingCity / NoCity → nothing drawn

            m_Settings.Draw(m_Theme, scale);
            m_Toast.Draw(m_Theme, scale);
        }

        // Action methods — invoked by semantic hotkey commands and on-panel buttons.
        // Single source of truth for each command.

        private void DispatchCommand(OverlayCommand command)
        {
            switch (command)
            {
                case OverlayCommand.None:
                    return;
                case OverlayCommand.ToggleSettings:
                    DoOpenSettings();
                    return;
                case OverlayCommand.ToggleScreenshots:
                    DoToggleScreenshots();
                    return;
                case OverlayCommand.CycleMode:
                    DoCycleMode();
                    return;
                case OverlayCommand.ForceDump:
                    DoForceDump();
                    return;
                case OverlayCommand.ExportReport:
                    DoExportReport();
                    return;
                case OverlayCommand.CycleAnchor:
                    DoCycleAnchor();
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command), command, "Unhandled overlay command");
            }
        }

        private void DoCycleMode()
        {
            m_State.CycleMode(m_Modes);
            OnModeChanged();
        }

        private void SetModeIndex(int index)
        {
            int previousMode = m_State.ModeIndex;
            m_State.SetModeByIndex(index, m_Modes);
            if (m_State.ModeIndex == previousMode) return;
            OnModeChanged();
        }

        private void OnModeChanged()
        {
            m_MainPanel.ResetScroll();
            if (m_Modes[m_State.ModeIndex].Mode is RecommendationsMode recommendationsMode)
            {
                ProfilerHost.TryGetReadSurface()?.InvalidateRecommendationsCache();
                recommendationsMode.InvalidateCache();
            }
            // Hide-mode safety net: when the hint pill is disabled the screen
            // goes fully blank, leaving no visible cue how to come back. The
            // toast lasts ~3s, then the screen is truly clean as Emilithe asked.
            // We skip it when the pill is on — the pill already advertises the
            // hotkey, and a flashing toast on top of it is just noise.
            if (m_State.ModeId == OverlayModeId.Hide && !SettingsStore.Snapshot.Settings.HideHintBadge)
                m_Toast.Show("Profiler hidden — Ctrl+F9 to cycle, Ctrl+F8 settings");
        }

        private void DoOpenSettings() => m_Settings.Toggle();

        private void DoForceDump() => ProfilerHost.TryGetReadSurface()?.ForceReport();

        private void DoExportReport()
        {
            ProfilerHost.TryGetReadSurface()?.ForceReport();
            var result = ReportExporter.Export();
            m_Toast.Show(ExportToastText(result));
        }

        private static string ExportToastText(ExportResult result)
        {
            if (!result.ReportWritten)
                return "Support file export failed";
            if (result.ZipWritten && result.ZipWarnings.Count == 0)
                return $"Support bundle created: {Path.GetFileName(result.ZipPath)}";
            if (result.ZipWritten)
                return $"Report + partial bundle created: {Path.GetFileName(result.ReportPath)}";
            return $"Report created, bundle failed: {Path.GetFileName(result.ReportPath)}";
        }

        private void DoCycleAnchor()
        {
            m_State.SetAnchor(m_State.Anchor.Cycle());
            SettingsStore.Update(settings => settings.With(anchor: (int)m_State.Anchor), save: false);
            m_Settings.SyncAnchorFromHotkey((int)m_State.Anchor);
            // Cycling anchor explicitly snaps the panel to a corner — discard
            // any prior manual drag so the next layout uses the preset.
            m_PanelPosition.SnapToAnchor();
        }

        private void DoToggleScreenshots()
        {
            var profiler = ProfilerHost.TryGetReadSurface();
            bool next = !SettingsStore.Snapshot.Settings.SpikeScreenshots;
            if (profiler != null)
                profiler.SetSpikeScreenshotsEnabled(next);
            else
                SettingsStore.Update(settings => settings.With(spikeScreenshots: next));
            m_Settings.SyncSpikeScreenshotsFromHotkey(next);
            m_Toast.Show(next ? "Spike screenshots: ON" : "Spike screenshots: OFF");
        }

        private void ScheduleSettingsSave()
        {
            m_SettingsSaveDirty = true;
            m_SettingsSaveDirtyAt = Time.realtimeSinceStartup;
        }

        private void FlushPendingSettingsSave()
        {
            if (!m_SettingsSaveDirty) return;
            SettingsStore.Save();
            m_SettingsSaveDirty = false;
        }
    }
}
