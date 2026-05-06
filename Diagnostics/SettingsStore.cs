using System;
using System.IO;
using UnityEngine;
using VanillaProfiler.Output;

namespace VanillaProfiler.Diagnostics
{
    /// <summary>
    /// Owns persisted settings state. ProfilerSettings is only the serialized DTO;
    /// this store is the single place that loads, replaces and writes it.
    /// </summary>
    public static class SettingsStore
    {
        private const string SETTINGS_DIR = "VanillaProfiler";
        private const string SETTINGS_FILE = "settings.json";

        // Defensive cap for ReadAllText — settings.json is normally a few KB; anything
        // close to this means the file was corrupted or replaced by a malicious payload.
        private const long MAX_SETTINGS_BYTES = 256 * 1024;

        public static ProfilerSettings Current { get; private set; } = new();

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
                    SaveCore();
                    return;
                }
                Current = new ProfilerSettings();
                SaveCore();
                return;
            }

            if (TryLoadFrom(path, "primary")) return;

            if (File.Exists(backup) && TryLoadFrom(backup, "backup"))
            {
                ModLog.Warn("Recovered settings from backup after primary file was corrupted");
                SaveCore();
                return;
            }

            ModLog.Warn("Settings load failed and no usable backup found — resetting to defaults");
            Current = new ProfilerSettings();
            SaveCore();
        }

        public static void Replace(ProfilerSettings settings, bool save)
        {
            Current = settings ?? new ProfilerSettings();
            Current.Clamp();
            if (save) SaveCore();
        }

        public static void Mutate(Action<ProfilerSettings> mutate, bool save)
        {
            mutate?.Invoke(Current);
            Current.Clamp();
            if (save) SaveCore();
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

                var loaded = new ProfilerSettings();
                JsonUtility.FromJsonOverwrite(json, loaded);
                bool migrated = loaded.Clamp();
                Current = loaded;
                ModLog.Info($"Settings loaded from {label}: {path}");
                if (migrated && label == "primary")
                    Save();
                return true;
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Settings load from {label} failed: {ex}");
                return false;
            }
        }

        private static void SaveCore()
        {
            string path = FilePath;
            string backup = path + ".bak";
            try
            {
                Current.Clamp();
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                AtomicFileWriter.WriteAllText(
                    path,
                    JsonUtility.ToJson(Current, prettyPrint: true),
                    encoding: null,
                    backupPath: backup);
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Settings save failed: {ex}");
            }
        }

    }
}
