using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using VanillaProfiler.Diagnostics;
using Application = UnityEngine.Application;

namespace VanillaProfiler.Output
{
    internal static class SupportBundleWriter
    {
        public static SupportBundleResult Write(string reportPath, string report)
        {
            string zipPath = Path.ChangeExtension(reportPath, ".zip");
            string tempPath = zipPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var warnings = new List<string>();

            try
            {
                using (var zip = ZipFile.Open(tempPath, ZipArchiveMode.Create))
                {
                    AddTextEntry(zip, Path.GetFileName(reportPath), report);
                    AddOptionalFile(zip, SettingsStore.FilePath, "settings.json", maxBytes: 256 * 1024, warnings);
                    AddOptionalFile(zip, LogFileSink.GetLogPath(Application.persistentDataPath),
                        LogFileSink.LOG_FILENAME, maxBytes: 1024 * 1024, warnings);
                    if (warnings.Count > 0)
                        AddTextEntry(zip, "bundle_warnings.txt", BuildWarningsText(warnings));
                }

                Publish(tempPath, zipPath);
                return new SupportBundleResult(zipPath, warnings, error: null);
            }
            catch (Exception ex)
            {
                TryDelete(tempPath);
                return new SupportBundleResult(zipPath: null, warnings, error: ex.Message);
            }
        }

        private static void Publish(string tempPath, string zipPath)
        {
            if (File.Exists(zipPath))
            {
                File.Replace(tempPath, zipPath, destinationBackupFileName: null);
                return;
            }

            File.Move(tempPath, zipPath);
        }

        private static void AddTextEntry(ZipArchive zip, string entryName, string text)
        {
            var entry = zip.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Fastest);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(text);
        }

        private static void AddOptionalFile(ZipArchive zip, string path, string entryName, int maxBytes, List<string> warnings)
        {
            try
            {
                if (!File.Exists(path)) return;
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                byte[] bytes = ReadTail(input, maxBytes);
                AddBytesEntry(zip, entryName, bytes);
            }
            catch (Exception ex)
            {
                warnings.Add($"{entryName}: {ex.Message}");
            }
        }

        private static string BuildWarningsText(IReadOnlyList<string> warnings)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Support bundle was created, but some optional attachments could not be added.");
            sb.AppendLine();
            for (int i = 0; i < warnings.Count; i++)
                sb.AppendLine("- " + warnings[i]);
            return sb.ToString();
        }

        private static void AddBytesEntry(ZipArchive zip, string entryName, byte[] bytes)
        {
            var entry = zip.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Fastest);
            using var output = entry.Open();
            output.Write(bytes, 0, bytes.Length);
        }

        private static byte[] ReadTail(FileStream input, int maxBytes)
        {
            long length = input.Length;
            long start = length > maxBytes ? length - maxBytes : 0;
            long remaining = length - start;
            input.Position = start;

            using var output = new MemoryStream((int)Math.Min(remaining, maxBytes));
            var buffer = new byte[8192];
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = input.Read(buffer, 0, toRead);
                if (read <= 0) break;
                output.Write(buffer, 0, read);
                remaining -= read;
            }
            return output.ToArray();
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Best effort cleanup only; the original export failure is more useful.
            }
        }
    }
}
