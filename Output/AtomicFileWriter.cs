using System;
using System.IO;
using System.Text;

namespace VanillaProfiler.Output
{
    /// <summary>
    /// Atomic file write helper using temp-file + rename pattern.
    /// Prevents data corruption on crash/power loss during write — NTFS guarantees
    /// atomic rename within same volume. Mirrors the CivicSurvival pattern; required
    /// by CIVIC009 analyzer expectation that text writes go through this entry point.
    /// </summary>
    public static class AtomicFileWriter
    {
        /// <summary>
        /// Atomically write text to file. Writes to .tmp first, then renames.
        /// </summary>
        public static void WriteAllText(string path, string content, Encoding encoding = null)
        {
            WriteAllText(path, content, encoding, backupPath: null);
        }

        /// <summary>
        /// Atomically write text to file with optional backup of the previous version.
        /// When backupPath is non-null and the target already exists, File.Replace stores
        /// the previous content there so callers can recover from a corrupted save.
        /// </summary>
        public static void WriteAllText(string path, string content, Encoding encoding, string backupPath)
        {
            string tmp = CreateTempPath(path);
            try
            {
                File.WriteAllText(tmp, content, encoding ?? Encoding.UTF8);
                ReplaceWithTemp(tmp, path, backupPath);
            }
            finally
            {
                DeleteTempIfPresent(tmp);
            }
        }

        private static string CreateTempPath(string path) => $"{path}.{Guid.NewGuid():N}.tmp";

        private static void ReplaceWithTemp(string tmpPath, string targetPath, string backupPath = null)
        {
            // File.Replace is atomic swap when target exists; Move otherwise.
            try
            {
                if (File.Exists(targetPath))
                    File.Replace(tmpPath, targetPath, backupPath);
                else
                    File.Move(tmpPath, targetPath);
            }
            catch (IOException) when (File.Exists(targetPath) && File.Exists(tmpPath))
            {
                // Race: another writer created target between Exists and Move.
                File.Replace(tmpPath, targetPath, backupPath);
            }
        }

        private static void DeleteTempIfPresent(string tmpPath)
        {
            try
            {
                File.Delete(tmpPath);
            }
            catch (DirectoryNotFoundException) { }
            catch (FileNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
