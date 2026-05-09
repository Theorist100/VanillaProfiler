using System;
using System.Collections.Generic;

namespace VanillaProfiler.Output
{
    public sealed class ExportResult
    {
        public ExportResult(
            string? reportPath,
            string? zipPath,
            IReadOnlyList<string> zipWarnings,
            string? error,
            bool reportWritten,
            bool zipWritten)
        {
            ReportPath = reportPath;
            ZipPath = zipPath;
            ZipWarnings = zipWarnings ?? Array.Empty<string>();
            Error = error;
            ReportWritten = reportWritten;
            ZipWritten = zipWritten;
        }

        public string? ReportPath { get; }
        public string? ZipPath { get; }
        public IReadOnlyList<string> ZipWarnings { get; }
        public string? Error { get; }
        public bool ReportWritten { get; }
        public bool ZipWritten { get; }

        public bool Succeeded => ReportWritten && ZipWritten && Error == null;
        public bool IsPartialSuccess => ReportWritten && (!ZipWritten || ZipWarnings.Count > 0 || Error != null);
    }
}
