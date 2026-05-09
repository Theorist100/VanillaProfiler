using System;
using UnityEngine;

namespace VanillaProfiler.Overlay
{
    /// <summary>
    /// Bottom button block of the main overlay panel: mode tabs (one per IOverlayMode)
    /// and an action row (Settings / Export / Snap corner). Click handlers come in via
    /// <see cref="Actions"/> so this class doesn't need to know about ProfilerOverlay.
    /// </summary>
    public sealed class MainPanelButtons
    {
        public const float ROW_HEIGHT = 38f;
        // Hint wraps to two lines so all six hotkeys fit at the default panel width
        // (440 logical px). One row was clipping at Ctrl+F12 once F7+F10 were added.
        public const float HINT_HEIGHT = 38f;
        public const float BLOCK_HEIGHT = ROW_HEIGHT * 2 + 8f + HINT_HEIGHT;

        public sealed class Actions
        {
            public Action? OpenSettings;
            public Action? ExportReport;
            public Action? CycleAnchor;
        }

        private readonly OverlayTheme m_Theme;
        private readonly OverlayModeDescriptor[] m_Modes;
        private readonly Actions m_Actions;

        public MainPanelButtons(OverlayTheme theme, OverlayModeDescriptor[] modes, Actions actions)
        {
            m_Theme = theme;
            m_Modes = modes;
            m_Actions = actions;
        }

        /// <summary>
        /// Draws the button block at the bottom of <paramref name="panelRect"/>. Returns
        /// the index of the mode the player clicked, or <paramref name="currentMode"/>
        /// if nothing changed this frame.
        /// </summary>
        public int Draw(Rect panelRect, int currentMode, Anchor currentAnchor)
        {
            float fw = panelRect.width - OverlayPanel.PAD * 2;
            float gap = 4f;
            float btnH = ROW_HEIGHT - 4f;
            float lx = OverlayPanel.PAD;

            float row1Y = panelRect.height - BLOCK_HEIGHT + 2f;
            int newMode = DrawModeTabs(row1Y, lx, fw, gap, btnH, currentMode);

            float row2Y = row1Y + ROW_HEIGHT + 3f;
            DrawActionRow(row2Y, lx, fw, gap, btnH, currentAnchor);

            DrawHotkeyHint(panelRect.height, lx, fw);
            return newMode;
        }

        private int DrawModeTabs(float y, float lx, float fw, float gap, float btnH, int currentMode)
        {
            // Mode tabs: current view tinted gold (matches SettingsPanel segmented
            // selector so the visual language is shared across the overlay).
            float tabW = (fw - gap * (m_Modes.Length - 1)) / m_Modes.Length;
            int next = currentMode;
            for (int i = 0; i < m_Modes.Length; i++)
            {
                bool isCurrent = i == currentMode;
                var rect = new Rect(lx + (tabW + gap) * i, y, tabW, btnH);
                var saved = GUI.color;
                try
                {
                    if (isCurrent) GUI.color = new Color(1f, 215f / 255f, 0f, 0.45f);
                    if (GUI.Button(rect, m_Modes[i].DisplayName, m_Theme.ButtonStyle) && !isCurrent)
                        next = i;
                }
                finally
                {
                    GUI.color = saved;
                }
            }
            return next;
        }

        private void DrawActionRow(float y, float lx, float fw, float gap, float btnH, Anchor currentAnchor)
        {
            float btnW = (fw - gap * 2f) / 3f;
            string anchorLabel = $"Snap: {currentAnchor.ShortName()}";

            if (GUI.Button(new Rect(lx, y, btnW, btnH), "Settings", m_Theme.ButtonStyle))
                m_Actions.OpenSettings?.Invoke();
            if (GUI.Button(new Rect(lx + (btnW + gap), y, btnW, btnH), "Export report", m_Theme.ButtonStyle))
                m_Actions.ExportReport?.Invoke();
            if (GUI.Button(new Rect(lx + (btnW + gap) * 2f, y, btnW, btnH), anchorLabel, m_Theme.ButtonStyle))
                m_Actions.CycleAnchor?.Invoke();
        }

        private void DrawHotkeyHint(float panelHeight, float lx, float fw)
        {
            // Quiet hint under the buttons — two lines so all six hotkeys fit at
            // default panel width without clipping. Common ones first.
            float hintY = panelHeight - HINT_HEIGHT + 2f;
            float lineH = HINT_HEIGHT * 0.5f;
            GUI.Label(new Rect(lx, hintY, fw, lineH),
                "Ctrl+F9 mode  •  Ctrl+F8 settings  •  Ctrl+F11 export  •  Ctrl+F12 snap",
                m_Theme.HintStyle);
            GUI.Label(new Rect(lx, hintY + lineH, fw, lineH),
                "Ctrl+F7 spike-shots  •  Ctrl+F10 force dump",
                m_Theme.HintStyle);
        }
    }
}
