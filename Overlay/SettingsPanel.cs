using System;
using System.Globalization;
using UnityEngine;
using VanillaProfiler.Diagnostics;
using V = VanillaProfiler.Overlay.SettingsValidation;

namespace VanillaProfiler.Overlay
{
    /// <summary>
    /// Ctrl+F8 settings panel. Uses a draft-then-apply model:
    ///   - Open  → snapshot SettingsStore.Current into m_Draft
    ///   - Edit  → mutate m_Draft only (the live settings stay untouched)
    ///   - Apply → m_Draft → SettingsStore.Current, persist once, fire OnApplied
    ///   - Reset → replace m_Draft with defaults (live settings unaffected until Apply)
    ///   - Close → discard m_Draft
    /// Layout helpers live in PanelLayout / SettingsWidgets / SettingsDraft so this
    /// file stays focused on form state, validation and persistence.
    /// </summary>
    public sealed class SettingsPanel
    {
        // 560 px gives ~70 px per slot in the 6-mode Default segmented row,
        // which matches the main panel's bottom tab width — so "Details" and
        // "Engine" fit without clipping. Text-field rows have plenty of empty
        // right margin at this width; that's fine, they're not the constraint.
        private const float W = 560f;
        // Base form height — sized to the actual content (last hint row ends at
        // y=436) plus a one-PAD bottom margin matching the top PAD. Dynamic rows
        // (custom scale / validation error) add LINE_H each via MeasureHeight,
        // so adding a fixed row here means bumping BASE_H by LINE_H + 4.
        private const float BASE_H = 446f;
        private const int WINDOW_ID = 0xC1F1C1;
        private const float SAVE_DEBOUNCE_S = 0.5f;
        private const float MIN_VISIBLE_PX = 100f;

        public event EventHandler OnApplied;

        // Last label matches HiddenMode.DisplayName ("Hide") so the segmented row
        // shows the same word as the bottom-row mode tabs. "Hidden" used to clip
        // at this column width (~48 px per slot on a 6-mode strip).
        private static readonly string[] s_ModeLabels = { "Status", "Diag", "Tips", "Details", "Engine", "Hide" };
        private static readonly string[] s_AnchorLabels = { "Top-L", "Top-R", "Bot-R", "Bot-L" };
        private static readonly string[] s_ScaleLabels = { "Auto", "1x", "1.5x", "2x", "2.5x" };
        private static readonly float[] s_ScaleValues = { 0f, 1f, 1.5f, 2f, 2.5f };

        private ProfilerSettings m_Draft;
        private string m_IntervalText;
        private string m_SparklineWidthText;
        private string m_SpikeThresholdText;
        private string m_ErrorText;
        private bool m_DirtyReportInterval;
        private bool m_DirtyDefaultMode;
        private bool m_DirtyAnchor;
        private bool m_DirtySparklineWidth;
        private bool m_DirtySpikeScreenshots;
        private bool m_DirtySpikeThreshold;
        private bool m_DirtyUiScale;
        private bool m_DirtyProfileVanilla;
        private bool m_DirtyHideHintBadge;

        // Draggable window state. Rect is in logical (pre-GUI.matrix) coordinates.
        private Rect m_WindowRect;
        private bool m_HasWindowRect;
        private bool m_PosDirty;
        private float m_PosDirtyAt;
        private bool m_WindowDraggedThisFrame;

        // Cached theme reference for the GUI.Window callback (Unity invokes it without
        // arguments other than windowID, so the panel state has to live on the instance).
        private OverlayTheme m_ActiveTheme;

        public bool IsOpen => m_Draft != null;

        public void SyncLiveSettings()
        {
            if (m_Draft == null) return;
            var live = SettingsStore.Current;

            if (!m_DirtyAnchor)
                m_Draft.Anchor = live.Anchor;
            if (!m_DirtySpikeScreenshots)
                m_Draft.SpikeScreenshots = live.SpikeScreenshots;
            if (!m_DirtyProfileVanilla)
                m_Draft.ProfileVanillaSystems = live.ProfileVanillaSystems;

            if (m_PosDirty && Time.realtimeSinceStartup - m_PosDirtyAt >= SAVE_DEBOUNCE_S)
                FlushPendingPosition();
        }

        public void SyncAnchorFromHotkey(int anchor)
        {
            if (m_Draft == null) return;
            m_Draft.Anchor = anchor;
            m_DirtyAnchor = false;
        }

        public void SyncSpikeScreenshotsFromHotkey(bool enabled)
        {
            if (m_Draft == null) return;
            m_Draft.SpikeScreenshots = enabled;
            m_DirtySpikeScreenshots = false;
        }

        public void Toggle()
        {
            if (IsOpen) Close();
            else OpenWithCurrent();
        }

        public void Close()
        {
            FlushPendingPosition();
            m_Draft = null;
            m_HasWindowRect = false;
            ClearDirty();
            ReleaseGuiFocus();
        }

        // After Apply/Close the destroyed Settings buttons can leave hotControl /
        // keyboardControl pointing at controls that no longer exist next frame.
        // The dangling capture eats MouseDrag events on the main panel until the
        // player clicks somewhere else. Explicit clear restores drag immediately.
        private static void ReleaseGuiFocus()
        {
            GUI.FocusControl(null);
            GUIUtility.keyboardControl = 0;
            GUIUtility.hotControl = 0;
        }

        public void FlushPendingPosition()
        {
            if (!m_PosDirty) return;
            SettingsStore.Current.SettingsX = m_WindowRect.x;
            SettingsStore.Current.SettingsY = m_WindowRect.y;
            SettingsStore.Save();
            m_PosDirty = false;
        }

        public void Draw(OverlayTheme theme, float scale)
        {
            if (m_Draft == null) return;
            theme.EnsureInitialized();
            m_ActiveTheme = theme;

            EnsureWindowRect(scale);
            m_WindowRect.width = W;
            m_WindowRect.height = MeasureHeight();
            var before = new Vector2(m_WindowRect.x, m_WindowRect.y);
            m_WindowDraggedThisFrame = false;
            m_WindowRect = GUI.Window(WINDOW_ID, m_WindowRect, DrawWindowContents, GUIContent.none, GUIStyle.none);
            PanelLayout.ClampInsideLogicalScreen(ref m_WindowRect, scale, MIN_VISIBLE_PX);

            if (m_WindowDraggedThisFrame &&
                (!Mathf.Approximately(before.x, m_WindowRect.x) || !Mathf.Approximately(before.y, m_WindowRect.y)))
            {
                m_PosDirty = true;
                m_PosDirtyAt = Time.realtimeSinceStartup;
            }
        }

        private void EnsureWindowRect(float scale)
        {
            if (m_HasWindowRect) return;
            var s = SettingsStore.Current;
            var (logicalW, logicalH) = PanelLayout.LogicalSize(scale);
            float h = MeasureHeight();

            float x = s.SettingsX >= 0f ? s.SettingsX : (logicalW - W) * 0.5f;
            float y = s.SettingsY >= 0f ? s.SettingsY : (logicalH - h) * 0.5f;
            m_WindowRect = new Rect(x, y, W, h);
            m_HasWindowRect = true;
        }

        private void DrawWindowContents(int windowId)
        {
            var theme = m_ActiveTheme;
            var rect = new Rect(0f, 0f, W, MeasureHeight());
            OverlayPanel.DrawFrame(theme, rect);

            float lx = OverlayPanel.PAD;
            float ly = OverlayPanel.PAD;
            float fw = W - OverlayPanel.PAD * 2;

            GUI.Label(new Rect(lx, ly, fw, OverlayPanel.LINE_H),
                "VANILLA PROFILER  >  SETTINGS  (drag title to move)", theme.HeaderStyle);
            ly += OverlayPanel.LINE_H;
            GUI.DrawTexture(new Rect(lx, ly + 2f, fw - 6f, 1f), theme.BorderTexture);
            ly += 8f;

            ly = SettingsWidgets.DrawTextField(theme, lx, ly, "Report interval (s)",
                ref m_IntervalText, () => { m_DirtyReportInterval = true; m_ErrorText = null; }, "1-60");

            ly = SettingsWidgets.DrawTextField(theme, lx, ly, "Sparkline width",
                ref m_SparklineWidthText, () => { m_DirtySparklineWidth = true; m_ErrorText = null; }, "10-60");

            // Default GUI.skin.toggle renders the checkbox glyph; passing BodyStyle
            // here previously stripped the checkbox and left only the label.
            bool newSpike = GUI.Toggle(new Rect(lx, ly, fw, OverlayPanel.LINE_H),
                m_Draft.SpikeScreenshots, " Spike screenshots (also Ctrl+F7 toggle)", theme.ToggleStyle);
            if (newSpike != m_Draft.SpikeScreenshots)
            {
                m_Draft.SpikeScreenshots = newSpike;
                m_DirtySpikeScreenshots = true;
            }
            ly += OverlayPanel.LINE_H + 4f;

            bool newProfileVanilla = GUI.Toggle(new Rect(lx, ly, fw, OverlayPanel.LINE_H),
                m_Draft.ProfileVanillaSystems, " Profile vanilla systems", theme.ToggleStyle);
            if (newProfileVanilla != m_Draft.ProfileVanillaSystems)
            {
                m_Draft.ProfileVanillaSystems = newProfileVanilla;
                m_DirtyProfileVanilla = true;
            }
            ly += OverlayPanel.LINE_H + 2f;

            GUI.Label(
                new Rect(lx, ly, fw, OverlayPanel.LINE_H),
                "    Slows the game — only enable while investigating a vanilla system.",
                theme.HintStyle);
            ly += OverlayPanel.LINE_H + 4f;

            bool newHideHint = GUI.Toggle(new Rect(lx, ly, fw, OverlayPanel.LINE_H),
                m_Draft.HideHintBadge, " Show hint pill in Hide mode", theme.ToggleStyle);
            if (newHideHint != m_Draft.HideHintBadge)
            {
                m_Draft.HideHintBadge = newHideHint;
                m_DirtyHideHintBadge = true;
            }
            ly += OverlayPanel.LINE_H + 4f;

            ly = SettingsWidgets.DrawTextField(theme, lx, ly, "Spike threshold (ms)",
                ref m_SpikeThresholdText, () => { m_DirtySpikeThreshold = true; m_ErrorText = null; }, "33-1000");

            ly = DrawSegmentedRow(theme, lx, ly, fw, "Default mode",
                m_Draft.DefaultMode, s_ModeLabels,
                v => { m_Draft.DefaultMode = v; m_DirtyDefaultMode = true; });

            ly = DrawSegmentedRow(theme, lx, ly, fw, "Position",
                m_Draft.Anchor, s_AnchorLabels,
                v => { m_Draft.Anchor = v; m_DirtyAnchor = true; });

            ly = DrawScaleRow(theme, lx, ly, fw);
            ly += 4f;

            ly = DrawActionButtons(lx, ly);

            if (!string.IsNullOrEmpty(m_ErrorText))
            {
                GUI.Label(new Rect(lx, ly, fw, OverlayPanel.LINE_H), m_ErrorText, theme.StyleForHealth(HealthLevel.Poor));
                ly += OverlayPanel.LINE_H;
            }

            // Tell the player where the *real* output lives — overlay numbers are a
            // glance view; the log and the Ctrl+F11 export are what should be sent in
            // bug reports. Paths are written relative to persistentDataPath so the
            // hint stays readable across users without leaking absolute home dirs.
            GUI.Label(new Rect(lx, ly, fw, OverlayPanel.LINE_H),
                "Settings: VanillaProfiler/settings.json", theme.HintStyle);
            ly += OverlayPanel.LINE_H;
            GUI.Label(new Rect(lx, ly, fw, OverlayPanel.LINE_H),
                "Live log: Logs/VanillaProfiler.log", theme.HintStyle);
            ly += OverlayPanel.LINE_H;
            GUI.Label(new Rect(lx, ly, fw, OverlayPanel.LINE_H),
                "Reports (Ctrl+F11): Reports/CSII_Report_*.txt", theme.HintStyle);

            // Drag handle = title band only — the rest of the window has interactive
            // text fields and buttons that must receive clicks.
            var dragHandle = new Rect(0f, 0f, W, OverlayPanel.LINE_H + 12f);
            var e = Event.current;
            if (e != null && e.type == EventType.MouseDrag && dragHandle.Contains(e.mousePosition))
                m_WindowDraggedThisFrame = true;
            GUI.DragWindow(dragHandle);
        }

        private float MeasureHeight()
        {
            float height = BASE_H;
            if (m_Draft != null && ScaleIndex(m_Draft.UiScale) < 0)
                height += OverlayPanel.LINE_H;
            if (!string.IsNullOrEmpty(m_ErrorText))
                height += OverlayPanel.LINE_H;
            return height;
        }

        private static float DrawSegmentedRow(OverlayTheme theme, float lx, float ly, float fw,
            string label, int current, string[] labels, Action<int> onChanged)
        {
            // Tighter label gutter than DrawTextField (110 vs 160). Segment row
            // labels ("Default mode", "Position", "UI scale") are shorter than
            // text-field labels ("Report interval (s)") so they fit in 110 px,
            // which gives the segmented buttons more room to render long names.
            GUI.Label(new Rect(lx, ly, 110f, OverlayPanel.LINE_H), label, theme.BodyStyle);
            int next = SettingsWidgets.DrawSegmented(
                theme,
                new Rect(lx + 120f, ly, fw - 120f, OverlayPanel.LINE_H),
                current,
                labels);
            if (next != current) onChanged(next);
            return ly + OverlayPanel.LINE_H + 4f;
        }

        private float DrawActionButtons(float lx, float ly)
        {
            float btnH = OverlayPanel.LINE_H + 8f;
            float gap = 8f;
            // Stretch across the full content width so the action row reaches
            // the same right edge as the segmented rows above. Equal-width
            // thirds means the row reads as a single grid with the form,
            // instead of looking shorter / centered as a separate block.
            float fw = W - OverlayPanel.PAD * 2f;
            float btnW = (fw - gap * 2f) / 3f;
            var theme = m_ActiveTheme;
            if (GUI.Button(new Rect(lx, ly, btnW, btnH), "Apply & Save", theme.ButtonStyle))
                Apply();
            if (GUI.Button(new Rect(lx + btnW + gap, ly, btnW, btnH), "Reset Defaults", theme.ButtonStyle))
                ResetDraftToDefaults();
            if (GUI.Button(new Rect(lx + (btnW + gap) * 2f, ly, btnW, btnH), "Close", theme.ButtonStyle))
                Close();
            return ly + btnH + 8f;
        }

        private static int ScaleIndex(float value)
        {
            for (int i = 0; i < s_ScaleValues.Length; i++)
                if (Mathf.Approximately(s_ScaleValues[i], value)) return i;
            return -1;
        }

        private float DrawScaleRow(OverlayTheme theme, float lx, float ly, float fw)
        {
            // Match DrawSegmentedRow's 110/120 gutter so all three segmented
            // rows (Default mode / Position / UI scale) start at the same x.
            GUI.Label(new Rect(lx, ly, 110f, OverlayPanel.LINE_H), "UI scale", theme.BodyStyle);
            int current = ScaleIndex(m_Draft.UiScale);
            int next = SettingsWidgets.DrawSegmented(
                theme,
                new Rect(lx + 120f, ly, fw - 120f, OverlayPanel.LINE_H),
                current,
                s_ScaleLabels);
            if (next >= 0 && next != current)
            {
                m_Draft.UiScale = s_ScaleValues[next];
                m_DirtyUiScale = true;
            }
            if (current < 0)
            {
                GUI.Label(new Rect(lx + 120f, ly + OverlayPanel.LINE_H, fw - 120f, OverlayPanel.LINE_H),
                    $"Custom scale: {m_Draft.UiScale:0.##}x", theme.HintStyle);
                return ly + OverlayPanel.LINE_H * 2f + 4f;
            }
            return ly + OverlayPanel.LINE_H + 4f;
        }

        private void OpenWithCurrent()
        {
            m_Draft = SettingsDraft.Clone(SettingsStore.Current);
            ClearDirty();
            m_ErrorText = null;
            SyncTextFieldsFromDraft();
        }

        private void ResetDraftToDefaults()
        {
            m_Draft = new ProfilerSettings();
            MarkAllDirty();
            m_ErrorText = null;
            SyncTextFieldsFromDraft();
        }

        private void Apply()
        {
            if (!ValidateDraftFromText())
                return;

            m_Draft.Clamp();
            var merged = SettingsDraft.Clone(SettingsStore.Current);
            if (m_DirtyReportInterval) merged.ReportIntervalSec = m_Draft.ReportIntervalSec;
            if (m_DirtyDefaultMode) merged.DefaultMode = m_Draft.DefaultMode;
            if (m_DirtyAnchor) merged.Anchor = m_Draft.Anchor;
            if (m_DirtySparklineWidth) merged.SparklineWidth = m_Draft.SparklineWidth;
            if (m_DirtySpikeScreenshots) merged.SpikeScreenshots = m_Draft.SpikeScreenshots;
            if (m_DirtySpikeThreshold) merged.SpikeThresholdMs = m_Draft.SpikeThresholdMs;
            if (m_DirtyUiScale) merged.UiScale = m_Draft.UiScale;
            if (m_DirtyProfileVanilla) merged.ProfileVanillaSystems = m_Draft.ProfileVanillaSystems;
            if (m_DirtyHideHintBadge) merged.HideHintBadge = m_Draft.HideHintBadge;
            if (m_PosDirty)
            {
                merged.SettingsX = m_WindowRect.x;
                merged.SettingsY = m_WindowRect.y;
                m_PosDirty = false;
            }

            SettingsStore.Replace(merged, save: true);
            OnApplied?.Invoke(this, EventArgs.Empty);
            m_Draft = null;
            m_HasWindowRect = false;
            ClearDirty();
            ReleaseGuiFocus();
        }

        private bool ValidateDraftFromText()
        {
            m_ErrorText = null;

            if (m_DirtyReportInterval)
            {
                if (!V.TryFloatInRange(m_IntervalText, 1f, 60f, "Report interval", out var v, out m_ErrorText))
                    return false;
                m_Draft.ReportIntervalSec = v;
            }
            if (m_DirtySparklineWidth)
            {
                if (!V.TryIntInRange(m_SparklineWidthText, 10, 60, "Sparkline width", out var v, out m_ErrorText))
                    return false;
                m_Draft.SparklineWidth = v;
            }
            if (m_DirtySpikeThreshold)
            {
                if (!V.TryFloatInRange(m_SpikeThresholdText, 33f, 1000f, "Spike threshold", out var v, out m_ErrorText))
                    return false;
                m_Draft.SpikeThresholdMs = v;
            }
            return true;
        }

        private void SyncTextFieldsFromDraft()
        {
            m_IntervalText = m_Draft.ReportIntervalSec.ToString("F1", CultureInfo.InvariantCulture);
            m_SparklineWidthText = m_Draft.SparklineWidth.ToString();
            m_SpikeThresholdText = m_Draft.SpikeThresholdMs.ToString("F0", CultureInfo.InvariantCulture);
        }

        private void ClearDirty()
        {
            m_DirtyReportInterval = false;
            m_DirtyDefaultMode = false;
            m_DirtyAnchor = false;
            m_DirtySparklineWidth = false;
            m_DirtySpikeScreenshots = false;
            m_DirtySpikeThreshold = false;
            m_DirtyUiScale = false;
            m_DirtyProfileVanilla = false;
            m_DirtyHideHintBadge = false;
        }

        private void MarkAllDirty()
        {
            m_DirtyReportInterval = true;
            m_DirtyDefaultMode = true;
            m_DirtyAnchor = true;
            m_DirtySparklineWidth = true;
            m_DirtySpikeScreenshots = true;
            m_DirtySpikeThreshold = true;
            m_DirtyUiScale = true;
            m_DirtyProfileVanilla = true;
            m_DirtyHideHintBadge = true;
        }
    }
}
