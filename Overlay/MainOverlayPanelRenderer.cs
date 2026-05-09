using System;
using UnityEngine;
using VanillaProfiler.Diagnostics;
using VanillaProfiler.Overlay.Modes;

namespace VanillaProfiler.Overlay
{
    internal sealed class MainOverlayPanelRenderer
    {
        private const float MAIN_HEADER_HEIGHT = OverlayPanel.PAD + OverlayPanel.LINE_H * 2f + 8f;

        private readonly OverlayTheme m_Theme;
        private readonly OverlayModeDescriptor[] m_Modes;
        private readonly MainPanelButtons m_Buttons;
        private readonly PanelPositionController m_PanelPosition;
        private readonly OverlayState m_State;
        private readonly Action<int> m_SetModeIndex;
        private readonly float m_Width;

        private Vector2 m_MainScroll;

        public MainOverlayPanelRenderer(
            OverlayTheme theme,
            OverlayModeDescriptor[] modes,
            MainPanelButtons buttons,
            PanelPositionController panelPosition,
            OverlayState state,
            float width,
            Action<int> setModeIndex)
        {
            m_Theme = theme;
            m_Modes = modes;
            m_Buttons = buttons;
            m_PanelPosition = panelPosition;
            m_State = state;
            m_Width = width;
            m_SetModeIndex = setModeIndex;
        }

        public void ResetScroll()
        {
            m_MainScroll = Vector2.zero;
        }

        public void DrawMainPanel(IProfilerReadSurface profiler, IOverlayMode mode, OverlaySnapshot snapshot,
            HealthReport health, float scale, ProfilerSettingsSnapshot settings)
        {
            float contentHeight = mode.MeasureHeight(snapshot);
            if (contentHeight <= 0) return;
            float naturalHeight = MAIN_HEADER_HEIGHT + contentHeight + MainPanelButtons.BLOCK_HEIGHT;
            float totalHeight = Mathf.Min(naturalHeight, PanelLayout.MaxWindowHeight(scale));

            m_PanelPosition.ApplyLayout(m_State.Anchor, m_Width, totalHeight, scale);
            var rect = m_PanelPosition.Rect;
            var before = new Vector2(rect.x, rect.y);

            m_PanelPosition.BeginWindow();
            rect = GUI.Window(ProfilerOverlay.MAIN_WINDOW_ID, rect,
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
                try
                {
                    DrawModeContent(profiler, snapshot, health, mode, settings, view.width, OverlayPanel.PAD);
                }
                finally
                {
                    GUI.EndScrollView();
                }
            }
            else
            {
                m_MainScroll = Vector2.zero;
                DrawModeContent(profiler, snapshot, health, mode, settings, rect.width, MAIN_HEADER_HEIGHT + OverlayPanel.PAD);
            }

            m_SetModeIndex(m_Buttons.Draw(rect, m_State.ModeIndex, m_State.Anchor));

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

            mode.Draw(ctx, snapshot, health);
        }
    }
}
