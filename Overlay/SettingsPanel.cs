using System;
using System.Globalization;
using UnityEngine;
using VanillaProfiler.Diagnostics;
using V = VanillaProfiler.Overlay.SettingsValidation;

namespace VanillaProfiler.Overlay
{
    /// <summary>
    /// Ctrl+F8 settings panel. Uses a draft-then-apply model:
    ///   - Open  → snapshot SettingsStore.Snapshot.Settings into m_Draft
    ///   - Edit  → mutate m_Draft only (the live settings stay untouched)
    ///   - Apply → m_Draft → SettingsStore.Apply, persist once, fire OnApplied
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

        public event EventHandler? OnApplied;

        // Last label matches HiddenMode.DisplayName ("Hide") so the segmented row
        // shows the same word as the bottom-row mode tabs. "Hidden" used to clip
        // at this column width (~48 px per slot on a 6-mode strip).
        private static readonly string[] s_ModeLabels = { "Status", "Diag", "Tips", "Details", "Engine", "Hide" };
        private static readonly string[] s_AnchorLabels = { "Top-L", "Top-R", "Bot-R", "Bot-L" };
        private static readonly string[] s_ScaleLabels = { "Auto", "1x", "1.5x", "2x", "2.5x" };
        private static readonly float[] s_ScaleValues = { 0f, 1f, 1.5f, 2f, 2.5f };

        private SettingsDraft? m_Draft;
        private string m_IntervalText = string.Empty;
        private string m_SparklineWidthText = string.Empty;
        private string m_SpikeThresholdText = string.Empty;
        private string? m_ErrorText;
        private readonly SettingsDirtyState m_Dirty = new();

        // Draggable window state. Rect is in logical (pre-GUI.matrix) coordinates.
        private Rect m_WindowRect;
        private bool m_HasWindowRect;
        private bool m_PosDirty;
        private float m_PosDirtyAt;
        private bool m_WindowDraggedThisFrame;

        // Cached theme reference for the GUI.Window callback (Unity invokes it without
        // arguments other than windowID, so the panel state has to live on the instance).
        private OverlayTheme m_ActiveTheme = null!;

        public bool IsOpen => m_Draft != null;

        public void SyncLiveSettings()
        {
            if (m_Draft == null) return;
            var live = SettingsStore.Snapshot.Settings;

            m_Dirty.SyncLiveSettings(m_Draft, live);

            if (m_PosDirty && Time.realtimeSinceStartup - m_PosDirtyAt >= SAVE_DEBOUNCE_S)
                FlushPendingPosition();
        }

        public void SyncAnchorFromHotkey(int anchor)
        {
            if (m_Draft == null) return;
            m_Draft.Anchor = anchor;
            m_Dirty.Anchor = false;
        }

        public void SyncSpikeScreenshotsFromHotkey(bool enabled)
        {
            if (m_Draft == null) return;
            m_Draft.SpikeScreenshots = enabled;
            m_Dirty.SpikeScreenshots = false;
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
            SettingsStore.Update(settings => settings.With(
                settingsX: m_WindowRect.x,
                settingsY: m_WindowRect.y));
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
            PanelLayout.ClampInsideLogicalScreen(ref m_WindowRect, scale, MIN_VISIBLE_PX);
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
            var s = SettingsStore.Snapshot.Settings;
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

            ly = DrawTextSettings(theme, lx, ly);
            ly = DrawToggleSettings(theme, lx, ly, fw);
            ly = DrawSpikeThreshold(theme, lx, ly);
            ly = DrawModeSettings(theme, lx, ly, fw);
            ly += 4f;

            ly = DrawActionButtons(lx, ly);

            if (!string.IsNullOrEmpty(m_ErrorText))
            {
                GUI.Label(new Rect(lx, ly, fw, OverlayPanel.LINE_H), m_ErrorText, theme.StyleForHealth(HealthLevel.Poor));
                ly += OverlayPanel.LINE_H;
            }

            DrawOutputHints(theme, lx, ly, fw);

            // Drag handle = title band only — the rest of the window has interactive
            // text fields and buttons that must receive clicks.
            var dragHandle = new Rect(0f, 0f, W, OverlayPanel.LINE_H + 12f);
            var e = Event.current;
            if (e != null && e.type == EventType.MouseDrag && dragHandle.Contains(e.mousePosition))
                m_WindowDraggedThisFrame = true;
            GUI.DragWindow(dragHandle);
        }

        private float DrawTextSettings(OverlayTheme theme, float lx, float ly)
        {
            ly = SettingsWidgets.DrawTextField(theme, lx, ly, "Report interval (s)",
                ref m_IntervalText, () => { m_Dirty.ReportInterval = true; m_ErrorText = null; }, "1-60");
            return SettingsWidgets.DrawTextField(theme, lx, ly, "Sparkline width",
                ref m_SparklineWidthText, () => { m_Dirty.SparklineWidth = true; m_ErrorText = null; }, "10-60");
        }

        private float DrawSpikeThreshold(OverlayTheme theme, float lx, float ly)
        {
            return SettingsWidgets.DrawTextField(theme, lx, ly, "Spike threshold (ms)",
                ref m_SpikeThresholdText, () => { m_Dirty.SpikeThreshold = true; m_ErrorText = null; }, "33-1000");
        }

        private float DrawToggleSettings(OverlayTheme theme, float lx, float ly, float fw)
        {
            ly = SettingsWidgets.DrawToggleRow(theme, lx, ly, fw, m_Draft!.SpikeScreenshots,
                " Spike screenshots (also Ctrl+F7 toggle)",
                v => { m_Draft.SpikeScreenshots = v; m_Dirty.SpikeScreenshots = true; });

            ly = SettingsWidgets.DrawToggleRow(theme, lx, ly, fw, m_Draft.ProfileVanillaSystems,
                " Profile vanilla systems",
                v => { m_Draft.ProfileVanillaSystems = v; m_Dirty.ProfileVanilla = true; },
                bottomGap: 2f);

            GUI.Label(new Rect(lx, ly, fw, OverlayPanel.LINE_H),
                "    Slows the game — only enable while investigating a vanilla system.",
                theme.HintStyle);
            ly += OverlayPanel.LINE_H + 4f;

            return SettingsWidgets.DrawToggleRow(theme, lx, ly, fw, m_Draft.HideHintBadge,
                " Show hint pill in Hide mode",
                v => { m_Draft.HideHintBadge = v; m_Dirty.HideHintBadge = true; });
        }

        private float DrawModeSettings(OverlayTheme theme, float lx, float ly, float fw)
        {
            ly = SettingsWidgets.DrawSegmentedRow(theme, lx, ly, fw, "Default mode",
                m_Draft!.DefaultMode, s_ModeLabels,
                v => { m_Draft.DefaultMode = v; m_Dirty.DefaultMode = true; });
            ly = SettingsWidgets.DrawSegmentedRow(theme, lx, ly, fw, "Position",
                m_Draft.Anchor, s_AnchorLabels,
                v => { m_Draft.Anchor = v; m_Dirty.Anchor = true; });
            return DrawScaleRow(theme, lx, ly, fw);
        }

        private static void DrawOutputHints(OverlayTheme theme, float lx, float ly, float fw)
        {
            GUI.Label(new Rect(lx, ly, fw, OverlayPanel.LINE_H),
                "Settings: VanillaProfiler/settings.json", theme.HintStyle);
            ly += OverlayPanel.LINE_H;
            GUI.Label(new Rect(lx, ly, fw, OverlayPanel.LINE_H),
                "Live log: Logs/VanillaProfiler.log", theme.HintStyle);
            ly += OverlayPanel.LINE_H;
            GUI.Label(new Rect(lx, ly, fw, OverlayPanel.LINE_H),
                "Reports (Ctrl+F11): Reports/CSII_Report_*.txt", theme.HintStyle);
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
            float rowY = ly;
            int current = ScaleIndex(m_Draft!.UiScale);
            ly = SettingsWidgets.DrawSegmentedRow(theme, lx, ly, fw, "UI scale",
                current, s_ScaleLabels,
                v => { m_Draft.UiScale = s_ScaleValues[v]; m_Dirty.UiScale = true; });
            if (current < 0)
            {
                GUI.Label(new Rect(lx + 120f, rowY + OverlayPanel.LINE_H, fw - 120f, OverlayPanel.LINE_H),
                    $"Custom scale: {m_Draft.UiScale:0.##}x", theme.HintStyle);
                return rowY + OverlayPanel.LINE_H * 2f + 4f;
            }
            return ly;
        }

        private void OpenWithCurrent()
        {
            m_Draft = new SettingsDraft(SettingsStore.Snapshot.Settings);
            ClearDirty();
            m_ErrorText = null;
            SyncTextFieldsFromDraft();
        }

        private void ResetDraftToDefaults()
        {
            m_Draft = new SettingsDraft(new ProfilerSettings());
            m_Dirty.ReplaceAll();
            m_ErrorText = null;
            SyncTextFieldsFromDraft();
        }

        private void Apply()
        {
            if (!ValidateDraftFromText())
                return;

            var live = SettingsStore.Snapshot.Settings;
            var merged = m_Dirty.Merge(live, m_Draft!);
            if (m_PosDirty)
            {
                merged = merged.With(settingsX: m_WindowRect.x, settingsY: m_WindowRect.y);
                m_PosDirty = false;
            }

            SettingsStore.Apply(merged);
            OnApplied?.Invoke(this, EventArgs.Empty);
            m_Draft = null;
            m_HasWindowRect = false;
            ClearDirty();
            ReleaseGuiFocus();
        }

        private bool ValidateDraftFromText()
        {
            m_ErrorText = null;
            var draft = m_Draft;
            if (draft == null) return false;

            if (m_Dirty.ReportInterval)
            {
                if (!V.TryFloatInRange(m_IntervalText, 1f, 60f, "Report interval", out var v, out m_ErrorText))
                    return false;
                draft.ReportIntervalSec = v;
            }
            if (m_Dirty.SparklineWidth)
            {
                if (!V.TryIntInRange(m_SparklineWidthText, 10, 60, "Sparkline width", out var v, out m_ErrorText))
                    return false;
                draft.SparklineWidth = v;
            }
            if (m_Dirty.SpikeThreshold)
            {
                if (!V.TryFloatInRange(m_SpikeThresholdText, 33f, 1000f, "Spike threshold", out var v, out m_ErrorText))
                    return false;
                draft.SpikeThresholdMs = v;
            }
            return true;
        }

        private void SyncTextFieldsFromDraft()
        {
            m_IntervalText = m_Draft!.ReportIntervalSec.ToString("F1", CultureInfo.InvariantCulture);
            m_SparklineWidthText = m_Draft.SparklineWidth.ToString();
            m_SpikeThresholdText = m_Draft.SpikeThresholdMs.ToString("F0", CultureInfo.InvariantCulture);
        }

        private void ClearDirty()
        {
            m_Dirty.Clear();
        }

    }
}
