using System;
using System.IO;
using UnityEngine;
using VanillaProfiler.Output;

namespace VanillaProfiler.Diagnostics
{
    /// <summary>
    /// Owns persisted settings state. Runtime code can read Snapshot and can only
    /// replace settings atomically through Apply/Update; there is no mutable global
    /// ProfilerSettings instance exposed to draw paths or Harmony patches.
    /// </summary>
    public static class SettingsStore
    {
        private const string SETTINGS_DIR = "VanillaProfiler";
        private const string SETTINGS_FILE = "settings.json";

        // Defensive cap for ReadAllText — settings.json is normally a few KB; anything
        // close to this means the file was corrupted or replaced by a malicious payload.
        private const long MAX_SETTINGS_BYTES = 256 * 1024;

        private static ProfilerSettings s_Current = new();
        private static ProfilerSettingsSnapshot s_Snapshot = ProfilerSettingsSnapshot.From(s_Current);

        public static ProfilerSettingsSnapshot Snapshot => s_Snapshot;

        public static string FilePath =>
            Path.Combine(Application.persistentDataPath, SETTINGS_DIR, SETTINGS_FILE);

        public static void Load()
        {
            string path = FilePath;
            string backup = path + ".bak";

            if (!File.Exists(path))
            {
                if (File.Exists(backup) && TryLoadFrom(backup, "backup"))
                {
                    SaveCore(preserveBackup: true);
                    return;
                }
                ApplyCore(new ProfilerSettings(), save: true, preserveBackup: false);
                return;
            }

            if (TryLoadFrom(path, "primary")) return;

            if (File.Exists(backup) && TryLoadFrom(backup, "backup"))
            {
                ModLog.Warn("Recovered settings from backup after primary file was corrupted");
                SaveCore(preserveBackup: true);
                return;
            }

            ModLog.Warn("Settings load failed and no usable backup found — resetting to defaults");
            ApplyCore(new ProfilerSettings(), save: true, preserveBackup: false);
        }

        public static void Apply(ProfilerSettings settings, bool save = true)
        {
            ApplyCore(settings, save, preserveBackup: false);
        }

        public static void Update(Func<ProfilerSettings, ProfilerSettings>? update, bool save = true)
        {
            if (update == null) return;
            ApplyCore(update(s_Current), save, preserveBackup: false);
        }

        public static void Save()
        {
            SaveCore();
        }

        private static bool TryLoadFrom(string path, string label)
        {
            try
            {
                long size = new FileInfo(path).Length;
                if (size > MAX_SETTINGS_BYTES)
                    throw new InvalidDataException(
                        $"Settings JSON exceeds {MAX_SETTINGS_BYTES} bytes ({size}); refusing to load");

                string json;
                using (var reader = new StreamReader(path))
                {
                    json = reader.ReadToEnd();
                }
                if (string.IsNullOrWhiteSpace(json))
                    throw new InvalidDataException("Empty settings JSON");

                var loaded = new SerializedSettings();
                JsonUtility.FromJsonOverwrite(json, loaded);
                var settings = loaded.ToSettings().Normalize(out bool migrated);
                ApplyCore(settings, save: false, preserveBackup: false);
                ModLog.Info($"Settings loaded from {label}: {path}");
                if (migrated && string.Equals(label, "primary", StringComparison.Ordinal))
                    Save();
                return true;
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Settings load from {label} failed: {ex}");
                return false;
            }
        }

        private static void ApplyCore(ProfilerSettings settings, bool save, bool preserveBackup)
        {
            s_Current = (settings ?? new ProfilerSettings()).Normalize();
            RefreshSnapshot();
            if (save) SaveCore(preserveBackup);
        }

        private static void SaveCore(bool preserveBackup = false)
        {
            string path = FilePath;
            string backup = path + ".bak";
            try
            {
                s_Current = s_Current.Normalize();
                RefreshSnapshot();
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                AtomicFileWriter.WriteAllText(
                    path,
                    JsonUtility.ToJson(SerializedSettings.From(s_Current), prettyPrint: true),
                    encoding: null,
                    backupPath: preserveBackup ? null : backup);
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Settings save failed: {ex}");
            }
        }

        private static void RefreshSnapshot()
        {
            s_Snapshot = ProfilerSettingsSnapshot.From(s_Current);
        }

        [Serializable]
        private sealed class SerializedSettings
        {
            public float ReportIntervalSec = 5.0f;
            public int DefaultMode = 0;
            public int Anchor = 0;
            public int SparklineWidth = 60;
            public bool SpikeScreenshots = true;
            public float SpikeThresholdMs = 100.0f;
            public float SyncPointThresholdMs = 0.5f;
            public bool SettingsPanelHotkey = true;
            public bool ProfileVanillaSystems;
            public bool HideHintBadge = true;
            public float UiScale;
            public float PanelX = -1f;
            public float PanelY = -1f;
            public float SettingsX = -1f;
            public float SettingsY = -1f;

            public static SerializedSettings From(ProfilerSettings settings)
                => new()
                {
                    ReportIntervalSec = settings.ReportIntervalSec,
                    DefaultMode = settings.DefaultMode,
                    Anchor = settings.Anchor,
                    SparklineWidth = settings.SparklineWidth,
                    SpikeScreenshots = settings.SpikeScreenshots,
                    SpikeThresholdMs = settings.SpikeThresholdMs,
                    SyncPointThresholdMs = settings.SyncPointThresholdMs,
                    SettingsPanelHotkey = settings.SettingsPanelHotkey,
                    ProfileVanillaSystems = settings.ProfileVanillaSystems,
                    HideHintBadge = settings.HideHintBadge,
                    UiScale = settings.UiScale,
                    PanelX = settings.PanelX,
                    PanelY = settings.PanelY,
                    SettingsX = settings.SettingsX,
                    SettingsY = settings.SettingsY,
                };

            public ProfilerSettings ToSettings()
                => new(
                    ReportIntervalSec,
                    DefaultMode,
                    Anchor,
                    SparklineWidth,
                    SpikeScreenshots,
                    SpikeThresholdMs,
                    SyncPointThresholdMs,
                    SettingsPanelHotkey,
                    ProfileVanillaSystems,
                    HideHintBadge,
                    UiScale,
                    PanelX,
                    PanelY,
                    SettingsX,
                    SettingsY);
        }
    }
}
