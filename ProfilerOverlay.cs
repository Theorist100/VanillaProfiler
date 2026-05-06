using System;
using System.IO;
using UnityEngine;
using VanillaProfiler.Diagnostics;
using VanillaProfiler.Overlay;
using VanillaProfiler.Overlay.Modes;

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

        // Throttle persistence of drag positions so a held-mouse-move doesn't write
        // the JSON file on every frame.
        private const float SAVE_DEBOUNCE_S = 0.5f;

        private readonly OverlayTheme m_Theme = new();
        private readonly Toast m_Toast = new();
        private readonly SettingsPanel m_Settings = new();
        private readonly OverlayInputHandler m_Input = new();

        private IOverlayMode[] m_Modes;
        private MainPanelButtons m_Buttons;
        private int m_ModeIndex;
        private Anchor m_Anchor = Anchor.TopLeft;

        private PanelPositionController m_PanelPosition;
        private bool m_SettingsSaveDirty;
        private float m_SettingsSaveDirtyAt;

        private void Start()
        {
            m_Modes = new IOverlayMode[]
            {
                new StatusMode(),
                new DiagnosisMode(),
                new DetailsMode(),
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
                () => (SettingsStore.Current.PanelX, SettingsStore.Current.PanelY),
                (x, y) =>
                {
                    SettingsStore.Current.PanelX = x;
                    SettingsStore.Current.PanelY = y;
                },
                ScheduleSettingsSave);

            ApplyStartupSettings();

            // Hotkeys and on-panel buttons share the same action methods so the two
            // entry points cannot drift in behaviour.
            m_Input.OnCycleMode += (s, e) => DoCycleMode();
            m_Input.OnForceDump += (s, e) => DoForceDump();
            m_Input.OnExportReport += (s, e) => DoExportReport();
            m_Input.OnCyclePosition += (s, e) => DoCycleAnchor();
            m_Input.OnToggleScreenshots += (s, e) => DoToggleScreenshots();
            m_Input.OnToggleSettings += (s, e) => DoOpenSettings();

            // Apply has already committed the new settings to Current. DefaultMode is a
            // startup preference, so applying unrelated settings must not jump screens.
            m_Settings.OnApplied += (s, e) =>
            {
                ApplyLiveSettings();
                m_Toast.Show("Settings saved");
            };
        }

        private void ApplyStartupSettings()
        {
            var s = SettingsStore.Current;
            m_ModeIndex = Mathf.Clamp(s.DefaultMode, 0, m_Modes.Length - 1);
            m_Anchor = (Anchor)Mathf.Clamp(s.Anchor, 0, 3);
            m_PanelPosition.Load();
        }

        private void ApplyLiveSettings()
        {
            var s = SettingsStore.Current;
            var nextAnchor = (Anchor)Mathf.Clamp(s.Anchor, 0, 3);
            if (nextAnchor != m_Anchor)
            {
                // Choosing an anchor is an explicit snap request. If the panel had a
                // manual drag position, discard it so the anchor can take effect.
                m_PanelPosition.SnapToAnchor();
            }
            m_Anchor = nextAnchor;
        }

        private void Update()
        {
            m_Input.Poll(m_Settings.IsOpen);
            m_Settings.SyncLiveSettings();

            // Debounced save: panel drags and corner snaps update Current
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

            float scale = PanelLayout.ResolveScale();
            var savedMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            try
            {
                DrawScaled(scale);
            }
            finally
            {
                GUI.matrix = savedMatrix;
            }
        }

        private void DrawScaled(float scale)
        {
            var profiler = ProfilerHost.TryGet();
            var snapshot = profiler?.LastSnapshot;
            var health = profiler?.LastHealth;
            var mode = m_Modes[m_ModeIndex];
            var lifecycle = profiler?.LifecycleState ?? ProfilerLifecycleState.Initializing;

            // Render order = Z-order (last drawn = topmost):
            //   1. Mode panel / badge (background layer)
            //   2. Toast (overlay notification)
            //   3. Settings panel (modal — must always be on top)
            //
            // The overlay only appears once a city is actually being measured. Splash,
            // main menu, editor menu and city-loading screens all stay clean. Settings
            // and toast still render because the player may want to configure the mod
            // pre-game; they are not lifecycle-gated.
            if (lifecycle == ProfilerLifecycleState.Settling)
                OverlayBadges.DrawSettling(m_Theme, scale);
            else if (lifecycle == ProfilerLifecycleState.Active && mode.IsHidden)
                OverlayBadges.DrawHidden(m_Theme, scale);
            else if (lifecycle == ProfilerLifecycleState.Active)
                DrawMainPanel(profiler, mode, snapshot, health, scale);
            // else: Initializing / LoadingCity / NoCity → nothing drawn

            m_Toast.Draw(m_Theme, scale);
            m_Settings.Draw(m_Theme, scale);
        }

        private void DrawMainPanel(Profiler profiler, IOverlayMode mode, OverlaySnapshot snapshot, HealthReport health, float scale)
        {
            float contentHeight = mode.MeasureHeight(snapshot);
            if (contentHeight <= 0) return;
            float totalHeight = contentHeight + MainPanelButtons.BLOCK_HEIGHT;

            // Layout: position from drag (manual) or from anchor preset; height
            // always recomputed from the active mode.
            m_PanelPosition.ApplyLayout(m_Anchor, WIDTH, totalHeight, scale);
            var rect = m_PanelPosition.Rect;
            var before = new Vector2(rect.x, rect.y);

            m_PanelPosition.BeginWindow();
            rect = GUI.Window(MAIN_WINDOW_ID, rect,
                id => DrawMainWindowContents(profiler, snapshot, health, mode),
                GUIContent.none, GUIStyle.none);
            m_PanelPosition.CompleteWindow(rect, before, scale);
        }

        private void DrawMainWindowContents(Profiler profiler, OverlaySnapshot snapshot, HealthReport health, IOverlayMode mode)
        {
            var panelRect = m_PanelPosition.Rect;
            var rect = new Rect(0f, 0f, panelRect.width, panelRect.height);
            OverlayPanel.DrawFrame(m_Theme, rect);

            var ctx = new DrawContext(
                m_Theme,
                OverlayPanel.PAD,
                OverlayPanel.PAD,
                rect.width - OverlayPanel.PAD * 2)
            {
                ModeIndex = m_ModeIndex,
                ModeCount = m_Modes.Length,
                NextModeName = m_Modes[(m_ModeIndex + 1) % m_Modes.Length].DisplayName,
                SparklineWidth = SettingsStore.Current.SparklineWidth,
                FpsSparkline = profiler?.FpsSparkline.Render(SettingsStore.Current.SparklineWidth) ?? string.Empty,
            };

            // Contract: this method is only reached when LifecycleState == Active,
            // so snapshot/health are current data. The Settling badge handles the
            // pre-snapshot phase.
            mode.Draw(ctx, snapshot, health);

            m_ModeIndex = m_Buttons.Draw(rect, m_ModeIndex, m_Anchor);

            // Drag handle = the top header band only. Buttons and text fields below
            // must still receive their own click events.
            var dragHandle = new Rect(0f, 0f, rect.width, OverlayPanel.LINE_H + 12f);
            m_PanelPosition.MarkDragIfActive(dragHandle);
            GUI.DragWindow(dragHandle);
        }

        // Action methods — invoked by hotkeys (OverlayInputHandler events) and
        // on-panel buttons. Single source of truth for each command.

        private void DoCycleMode() => m_ModeIndex = (m_ModeIndex + 1) % m_Modes.Length;

        private void DoOpenSettings() => m_Settings.Toggle();

        private void DoForceDump() => ProfilerHost.TryGet()?.ForceReport();

        private void DoExportReport()
        {
            ProfilerHost.TryGet()?.ForceReport();
            string saved = ReportExporter.Export();
            m_Toast.Show(saved != null
                ? $"Support file created: {Path.GetFileName(saved)}"
                : "Support file export failed");
        }

        private void DoCycleAnchor()
        {
            m_Anchor = m_Anchor.Cycle();
            SettingsStore.Current.Anchor = (int)m_Anchor;
            // Cycling anchor explicitly snaps the panel to a corner — discard
            // any prior manual drag so the next layout uses the preset.
            m_PanelPosition.SnapToAnchor();
        }

        private void DoToggleScreenshots()
        {
            bool next = !SpikeScreenshot.Enabled;
            SpikeScreenshot.Enabled = next;
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
