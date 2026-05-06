using System;
using UnityEngine;

namespace VanillaProfiler.Overlay
{
    /// <summary>
    /// Stateless rendering helpers for the SettingsPanel form. Kept separate so the
    /// panel itself stays focused on draft / validation / persistence logic instead
    /// of layout math.
    /// </summary>
    public static class SettingsWidgets
    {
        /// <summary>
        /// Label + text field + optional range hint. Returns the next Y cursor.
        /// Calls <paramref name="onChanged"/> only when the buffer actually changes.
        /// </summary>
        public static float DrawTextField(
            OverlayTheme theme, float lx, float ly,
            string label, ref string buffer, Action onChanged,
            string rangeHint = null)
        {
            GUI.Label(new Rect(lx, ly, 160f, OverlayPanel.LINE_H), label, theme.BodyStyle);
            string updated = GUI.TextField(
                new Rect(lx + 170f, ly, 80f, OverlayPanel.LINE_H),
                buffer ?? "",
                theme.TextFieldStyle);
            if (updated != buffer)
            {
                buffer = updated;
                onChanged?.Invoke();
            }
            if (!string.IsNullOrEmpty(rangeHint))
                GUI.Label(new Rect(lx + 258f, ly, 80f, OverlayPanel.LINE_H), rangeHint, theme.HintStyle);
            return ly + OverlayPanel.LINE_H + 4f;
        }

        /// <summary>
        /// Segmented selector — N buttons drawn side by side, the selected one tinted
        /// gold. Returns the new selected index (or the same one if no click landed).
        /// </summary>
        public static int DrawSegmented(OverlayTheme theme, Rect rect, int selected, string[] labels)
        {
            float segW = rect.width / labels.Length;
            int result = selected;
            for (int i = 0; i < labels.Length; i++)
            {
                bool isSel = i == selected;
                var btn = new Rect(rect.x + segW * i, rect.y, segW - 2f, rect.height);
                var saved = GUI.color;
                try
                {
                    if (isSel) GUI.color = new Color(1f, 215f / 255f, 0f, 0.4f);
                    if (GUI.Button(btn, labels[i], theme.ButtonStyle)) result = i;
                }
                finally
                {
                    GUI.color = saved;
                }
            }
            return result;
        }
    }
}
