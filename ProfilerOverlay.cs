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
        private const int MAIN_WINDOW_ID = 0xC1F1C0;
        private const float MIN_VISIBLE_PX = 80f;
        private const float MAIN_HEADER_HEIGHT = OverlayPanel.PAD + OverlayPanel.LINE_H * 2f + 8f;

        // Throttle persistence of drag positions so a held-mouse-move doesn't write
        // the JSON file on every frame.
        private const float SAVE_DEBOUNCE_S = 0.5f;

        private readonly OverlayTheme m_Theme = new();
        private readonly Toast m_Toast = new();
        private readonly SettingsPanel m_Settings = new();
        private readonly OverlayInputHandler m_Input = new();
        private readonly OverlayState m_State = new();

        private IOverlayMode[] m_Modes = Array.Empty<IOverlayMode>();
        private MainPanelButtons m_Buttons = null!;

        private PanelPositionController m_PanelPosition = null!;
        private Vector2 m_MainScroll;
        private bool m_SettingsSaveDirty;
        private float m_SettingsSaveDirtyAt;

        private void Start()
        {
            m_Modes = new IOverlayMode[]
            {
                new StatusMode(),
                new DiagnosisMode(),
                new RecommendationsMode(),
                new DetailsMode(),
                new EngineMode(),
                new HiddenMode(),
            };
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

            LoadInitialSettings();

            // Hotkeys and on-panel buttons share the same action methods so the two
            // entry points cannot drift in behaviour.
            m_Input.OnCycleMode += (s, e) => DoCycleMode();
            m_Input.OnForceDump += (s, e) => DoForceDump();
            m_Input.OnExportReport += (s, e) => DoExportReport();
            m_Input.OnCyclePosition += (s, e) => DoCycleAnchor();
            m_Input.OnToggleScreenshots += (s, e) => DoToggleScreenshots();
            m_Input.OnToggleSettings += (s, e) => DoOpenSettings();

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
            m_State.Initialize(s.DefaultMode, s.Anchor, m_Modes.Length);
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
            m_Input.Poll(m_Settings.IsOpen);
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
            var mode = m_Modes[m_State.ModeIndex];
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
            // NoCity (main menu) intentionally renders nothing — DrawStandby was wired
            // here briefly to address a "dead method" audit finding, but a badge in the
            // main menu is intrusive for the common-case player who only wants to play.
            // The standby badge stays defined for a future opt-in setting.
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
                    DrawMainPanel(profiler, mode, snapshot, health, scale, settings);
            }
            // else: Initializing / LoadingCity / NoCity → nothing drawn

            m_Settings.Draw(m_Theme, scale);
            m_Toast.Draw(m_Theme, scale);
        }

        private void DrawMainPanel(IProfilerReadSurface profiler, IOverlayMode mode, OverlaySnapshot snapshot,
            HealthReport health, float scale, ProfilerSettingsSnapshot settings)
        {
            float contentHeight = mode.MeasureHeight(snapshot);
            if (contentHeight <= 0) return;
            float naturalHeight = MAIN_HEADER_HEIGHT + contentHeight + MainPanelButtons.BLOCK_HEIGHT;
            float totalHeight = Mathf.Min(naturalHeight, PanelLayout.MaxWindowHeight(scale));

            // Layout: position from drag (manual) or from anchor preset; height
            // always recomputed from the active mode.
            m_PanelPosition.ApplyLayout(m_State.Anchor, WIDTH, totalHeight, scale);
            var rect = m_PanelPosition.Rect;
            var before = new Vector2(rect.x, rect.y);

            m_PanelPosition.BeginWindow();
            rect = GUI.Window(MAIN_WINDOW_ID, rect,
                id => DrawMainWindowContents(profiler, snapshot, health, mode, settings),
                GUIContent.none, GUIStyle.none);
            m_PanelPosition.CompleteWindow(rect, before, scale);
        }

        private void DrawMainWindowContents(IProfilerReadSurface profiler, OverlaySnapshot snapshot, HealthReport health,
            IOverlayMode mode, ProfilerSettingsSnapshot settings)
        {
            var panelRect = m_PanelPosition.Rect;
            var rect = new Rect(0f, 0f, panelRect.width, panelRect.height);
            OverlayPanel.DrawFrame(m_Theme, rect);
            DrawMainHeader(mode, rect.width);

            float contentHeight = mode.MeasureHeight(snapshot);
            float viewportHeight = Mathf.Max(OverlayPanel.LINE_H,
                rect.height - MAIN_HEADER_HEIGHT - MainPanelButtons.BLOCK_HEIGHT);
            bool scrolling = contentHeight > viewportHeight;
            if (scrolling)
            {
                var viewport = new Rect(0f, MAIN_HEADER_HEIGHT, rect.width, viewportHeight);
                var view = new Rect(0f, 0f, rect.width - 18f, contentHeight);
                m_MainScroll = GUI.BeginScrollView(viewport, m_MainScroll, view, false, true);
                DrawModeContent(profiler, snapshot, health, mode, settings, view.width, OverlayPanel.PAD);
                GUI.EndScrollView();
            }
            else
            {
                m_MainScroll = Vector2.zero;
                DrawModeContent(profiler, snapshot, health, mode, settings, rect.width, MAIN_HEADER_HEIGHT + OverlayPanel.PAD);
            }

            SetModeIndex(m_Buttons.Draw(rect, m_State.ModeIndex, m_State.Anchor));

            // Drag handle = the top header band only. Buttons and text fields below
            // must still receive their own click events.
            var dragHandle = new Rect(0f, 0f, rect.width, OverlayPanel.LINE_H + 12f);
            m_PanelPosition.MarkDragIfActive(dragHandle);
            GUI.DragWindow(dragHandle);
        }

        private void DrawMainHeader(IOverlayMode mode, float width)
        {
            var ctx = new DrawContext(
                m_Theme,
                OverlayPanel.PAD,
                OverlayPanel.PAD,
                width - OverlayPanel.PAD * 2)
            {
                ModeIndex = m_State.ModeIndex,
                ModeCount = m_Modes.Length,
                NextModeName = m_Modes[(m_State.ModeIndex + 1) % m_Modes.Length].DisplayName,
            };
            string title = mode is RecommendationsMode
                ? "RECOMMENDATIONS"
                : mode.DisplayName.ToUpperInvariant();
            OverlayPanel.DrawHeaderWithCycle(ctx, $"VANILLA PROFILER  >  {title}");
        }

        private void DrawModeContent(IProfilerReadSurface profiler, OverlaySnapshot snapshot, HealthReport health,
            IOverlayMode mode, ProfilerSettingsSnapshot settings, float width, float y)
        {
            var ctx = new DrawContext(
                m_Theme,
                OverlayPanel.PAD,
                y,
                width - OverlayPanel.PAD * 2)
            {
                ModeIndex = m_State.ModeIndex,
                ModeCount = m_Modes.Length,
                NextModeName = m_Modes[(m_State.ModeIndex + 1) % m_Modes.Length].DisplayName,
                SparklineWidth = settings.Settings.SparklineWidth,
                FpsSparkline = profiler.FpsSparklineText(settings.Settings.SparklineWidth),
            };

            // Contract: this method is only reached when LifecycleState == Active,
            // so snapshot/health are current data. The Settling badge handles the
            // pre-snapshot phase.
            mode.Draw(ctx, snapshot, health);
        }

        // Action methods — invoked by hotkeys (OverlayInputHandler events) and
        // on-panel buttons. Single source of truth for each command.

        private void DoCycleMode() => SetModeIndex((m_State.ModeIndex + 1) % m_Modes.Length);

        private void SetModeIndex(int index)
        {
            int previousMode = m_State.ModeIndex;
            m_State.SetMode(index, m_Modes.Length);
            if (m_State.ModeIndex == previousMode) return;
            m_MainScroll = Vector2.zero;
            if (m_Modes[m_State.ModeIndex] is RecommendationsMode recommendationsMode)
            {
                ProfilerHost.TryGetReadSurface()?.InvalidateRecommendationsCache();
                recommendationsMode.InvalidateCache();
            }
            // Hide-mode safety net: when the hint pill is disabled the screen
            // goes fully blank, leaving no visible cue how to come back. The
            // toast lasts ~3s, then the screen is truly clean as Emilithe asked.
            // We skip it when the pill is on — the pill already advertises the
            // hotkey, and a flashing toast on top of it is just noise.
            if (m_Modes[m_State.ModeIndex] is HiddenMode && !SettingsStore.Snapshot.Settings.HideHintBadge)
                m_Toast.Show("Profiler hidden — Ctrl+F9 to cycle, Ctrl+F8 settings");
        }

        private void DoOpenSettings() => m_Settings.Toggle();

        private void DoForceDump() => ProfilerHost.TryGetReadSurface()?.ForceReport();

        private void DoExportReport()
        {
            ProfilerHost.TryGetReadSurface()?.ForceReport();
            string? saved = ReportExporter.Export();
            m_Toast.Show(saved != null
                ? $"Support file created: {Path.GetFileName(saved)}"
                : "Support file export failed");
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
