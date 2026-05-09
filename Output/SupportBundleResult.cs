using System;
using System.Collections.Generic;

namespace VanillaProfiler.Output
{
    internal sealed class SupportBundleResult
    {
        public SupportBundleResult(string? zipPath, IReadOnlyList<string> warnings, string? error)
        {
            ZipPath = zipPath;
            Warnings = warnings ?? Array.Empty<string>();
            Error = error;
        }

        public string? ZipPath { get; }
        public IReadOnlyList<string> Warnings { get; }
        public string? Error { get; }
        public bool ZipWritten => ZipPath != null && Error == null;
    }
}
