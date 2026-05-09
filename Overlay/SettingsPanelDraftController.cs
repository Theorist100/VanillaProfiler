using System.Globalization;
using UnityEngine;
using VanillaProfiler.Diagnostics;
using V = VanillaProfiler.Overlay.SettingsValidation;

namespace VanillaProfiler.Overlay
{
    internal sealed class SettingsPanelDraftController
    {
        public SettingsDraft? Draft { get; private set; }
        public string IntervalText { get; set; } = string.Empty;
        public string SparklineWidthText { get; set; } = string.Empty;
        public string SpikeThresholdText { get; set; } = string.Empty;
        public string? ErrorText { get; set; }
        public SettingsDirtyState Dirty { get; } = new();

        public bool IsOpen => Draft != null;

        public void OpenWithCurrent()
        {
            Draft = new SettingsDraft(SettingsStore.Snapshot.Settings);
            ClearDirty();
            ErrorText = null;
            SyncTextFieldsFromDraft();
        }

        public void CloseDraft()
        {
            Draft = null;
            ClearDirty();
        }

        public void ResetDraftToDefaults()
        {
            Draft = new SettingsDraft(new ProfilerSettings());
            Dirty.ReplaceAll();
            ErrorText = null;
            SyncTextFieldsFromDraft();
        }

        public void SyncLiveSettings()
        {
            if (Draft == null) return;
            Dirty.SyncLiveSettings(Draft, SettingsStore.Snapshot.Settings);
        }

        public void SyncAnchorFromHotkey(int anchor)
        {
            if (Draft == null) return;
            Draft.Anchor = anchor;
            Dirty.Anchor = false;
        }

        public void SyncSpikeScreenshotsFromHotkey(bool enabled)
        {
            if (Draft == null) return;
            Draft.SpikeScreenshots = enabled;
            Dirty.SpikeScreenshots = false;
        }

        public bool Apply(Rect windowRect, bool includePosition)
        {
            if (!ValidateDraftFromText())
                return false;

            var live = SettingsStore.Snapshot.Settings;
            var merged = Dirty.Merge(live, Draft!);
            if (includePosition)
                merged = merged.With(settingsX: windowRect.x, settingsY: windowRect.y);

            SettingsStore.Apply(merged);
            CloseDraft();
            return true;
        }

        public void ClearDirty()
        {
            Dirty.Clear();
        }

        private bool ValidateDraftFromText()
        {
            ErrorText = null;
            var draft = Draft;
            if (draft == null) return false;

            if (Dirty.ReportInterval)
            {
                if (!V.TryFloatInRange(IntervalText, 1f, 60f, "Report interval", out var v, out var error))
                {
                    ErrorText = error;
                    return false;
                }
                draft.ReportIntervalSec = v;
            }
            if (Dirty.SparklineWidth)
            {
                if (!V.TryIntInRange(SparklineWidthText, 10, 60, "Sparkline width", out var v, out var error))
                {
                    ErrorText = error;
                    return false;
                }
                draft.SparklineWidth = v;
            }
            if (Dirty.SpikeThreshold)
            {
                if (!V.TryFloatInRange(SpikeThresholdText, 33f, 1000f, "Spike threshold", out var v, out var error))
                {
                    ErrorText = error;
                    return false;
                }
                draft.SpikeThresholdMs = v;
            }
            return true;
        }

        private void SyncTextFieldsFromDraft()
        {
            IntervalText = Draft!.ReportIntervalSec.ToString("F1", CultureInfo.InvariantCulture);
            SparklineWidthText = Draft.SparklineWidth.ToString();
            SpikeThresholdText = Draft.SpikeThresholdMs.ToString("F0", CultureInfo.InvariantCulture);
        }
    }
}
