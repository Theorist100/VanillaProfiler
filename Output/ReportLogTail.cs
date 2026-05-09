using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VanillaProfiler.Output
{
    internal static class ReportLogTail
    {
        public static IEnumerable<string> Read(string persistentDataPath, int count)
        {
            try
            {
                if (count < 1) count = 1;
                string path = LogFileSink.GetLogPath(persistentDataPath);
                if (!File.Exists(path))
                    return new[] { $"  ({LogFileSink.LOG_FILENAME} not found)" };

                const int CHUNK = 8192;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long length = stream.Length;
                if (length == 0)
                    return new[] { $"  ({LogFileSink.LOG_FILENAME} is empty)" };

                long startOffset = FindTailStartOffset(stream, length, count, CHUNK);
                string text = ReadUtf8From(stream, startOffset, length);
                return LastLines(text, count);
            }
            catch (Exception ex)
            {
                return new[] { $"  (failed to read log: {ex.Message})" };
            }
        }

        private static long FindTailStartOffset(FileStream stream, long length, int count, int chunkSize)
        {
            var buffer = new byte[chunkSize];
            long scanEnd = length;
            stream.Position = length - 1;
            if (stream.ReadByte() == '\n')
                scanEnd--;

            long pos = scanEnd;
            long startOffset = 0;
            int newlines = 0;
            while (pos > 0)
            {
                int readSize = (int)Math.Min(chunkSize, pos);
                pos -= readSize;
                stream.Position = pos;
                int read = stream.Read(buffer, 0, readSize);
                if (read <= 0) break;
                if (TryFindTailOffset(buffer, read, pos, count, ref newlines, out startOffset))
                    break;
            }
            return startOffset;
        }

        private static bool TryFindTailOffset(
            byte[] buffer, int read, long chunkStart, int count, ref int newlines, out long offset)
        {
            offset = 0;
            for (int i = read - 1; i >= 0; i--)
            {
                if (buffer[i] != (byte)'\n') continue;
                newlines++;
                if (newlines != count) continue;
                offset = chunkStart + i + 1;
                return true;
            }
            return false;
        }

        private static string ReadUtf8From(FileStream stream, long startOffset, long length)
        {
            int byteCount = checked((int)(length - startOffset));
            var bytes = new byte[byteCount];
            stream.Position = startOffset;
            int total = 0;
            while (total < byteCount)
            {
                int read = stream.Read(bytes, total, byteCount - total);
                if (read <= 0) break;
                total += read;
            }
            return Encoding.UTF8.GetString(bytes, 0, total);
        }

        private static IReadOnlyList<string> LastLines(string text, int count)
        {
            var raw = text.Split('\n');
            int lineCount = raw.Length;
            if (lineCount > 0 && raw[lineCount - 1].Length == 0)
                lineCount--;
            int first = Math.Max(0, lineCount - count);
            var lines = new List<string>(lineCount - first);
            for (int i = first; i < lineCount; i++)
                lines.Add(raw[i].TrimEnd('\r'));
            return lines;
        }
    }
}
