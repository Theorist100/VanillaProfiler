using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using VanillaProfiler.Diagnostics;
using Application = UnityEngine.Application;

namespace VanillaProfiler.Output
{
    internal static class SupportBundleWriter
    {
        public static void Write(string reportPath, string report)
        {
            string zipPath = Path.ChangeExtension(reportPath, ".zip");
            string tempPath = zipPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                using (var zip = ZipFile.Open(tempPath, ZipArchiveMode.Create))
                {
                    AddTextEntry(zip, Path.GetFileName(reportPath), report);
                    AddFileIfExists(zip, SettingsStore.FilePath, "settings.json", maxBytes: 256 * 1024);
                    AddFileIfExists(zip, LogFileSink.GetLogPath(Application.persistentDataPath),
                        LogFileSink.LOG_FILENAME, maxBytes: 1024 * 1024);
                }

                Publish(tempPath, zipPath);
                ModLog.Info($"Support bundle saved: {zipPath}");
            }
            catch
            {
                TryDelete(tempPath);
                throw;
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

        private static void AddFileIfExists(ZipArchive zip, string path, string entryName, int maxBytes)
        {
            if (!File.Exists(path)) return;
            var entry = zip.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Fastest);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var output = entry.Open();
            CopyTail(input, output, maxBytes);
        }

        private static void CopyTail(FileStream input, Stream output, int maxBytes)
        {
            long length = input.Length;
            long start = length > maxBytes ? length - maxBytes : 0;
            long remaining = length - start;
            input.Position = start;

            var buffer = new byte[8192];
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = input.Read(buffer, 0, toRead);
                if (read <= 0) break;
                output.Write(buffer, 0, read);
                remaining -= read;
            }
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
