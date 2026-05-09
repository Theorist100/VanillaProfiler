using System;
using System.Diagnostics;
using VanillaProfiler.Aggregation;
using VanillaProfiler.Diagnostics;
using VanillaProfiler.Output;

namespace VanillaProfiler
{
    internal sealed class ProfilerReportPipeline
    {
        private readonly ReportBuilder m_Builder = new();
        private readonly ReportDispatcher m_Dispatcher;
        private readonly IProfilerReadSurface m_ProfilerLogTarget;

        public ProfilerReportPipeline(ReportDispatcher dispatcher, IProfilerReadSurface profilerLogTarget)
        {
            m_Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            m_ProfilerLogTarget = profilerLogTarget ?? throw new ArgumentNullException(nameof(profilerLogTarget));
        }

        public ProfilerReportFrame BuildAndWrite(
            int reportNumber,
            MetricsSample metrics,
            MemorySample memory,
            MemoryHistory memoryHistory,
            bool logReplacements)
        {
            var (snapshot, health) = m_Builder.Build(metrics, memory, memoryHistory);
            AttachReplacementSnapshot(snapshot, metrics, logReplacements);
            m_Dispatcher.WriteReport(reportNumber, snapshot, health, metrics, memory);
            return new ProfilerReportFrame(snapshot, health, logReplacements);
        }

        private void AttachReplacementSnapshot(
            OverlaySnapshot snapshot,
            MetricsSample metrics,
            bool logReplacements)
        {
            var replacements = metrics.WindowContext.Replacements;
            snapshot.ReplacedVanillaSystems = ToReplacementRows(replacements, metrics.Buckets.PatchedVanillaSystems);
            if (logReplacements)
                SystemReplacementDetector.LogTo(m_ProfilerLogTarget, replacements);
        }

        private static ReplacedVanillaSystemRow[] ToReplacementRows(
            SystemReplacementDetector.ReplacementSnapshot snapshot,
            System.Collections.Generic.IReadOnlyDictionary<string, PhaseData>? patchedMs)
        {
            var list = snapshot.Replacements;
            if (list == null || list.Count == 0) return Array.Empty<ReplacedVanillaSystemRow>();
            var arr = new ReplacedVanillaSystemRow[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                double ms = 0.0;
                if (patchedMs != null && patchedMs.TryGetValue(r.VanillaSystem, out var phase))
                    ms = phase.InclusiveTicks * 1000.0 / Stopwatch.Frequency;
                arr[i] = new ReplacedVanillaSystemRow(r.VanillaSystem, r.Owners, ms);
            }
            return arr;
        }
    }

    internal readonly struct ProfilerReportFrame
    {
        public ProfilerReportFrame(OverlaySnapshot snapshot, HealthReport health, bool replacementsLogged)
        {
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            Health = health ?? throw new ArgumentNullException(nameof(health));
            ReplacementsLogged = replacementsLogged;
        }

        public OverlaySnapshot Snapshot { get; }
        public HealthReport Health { get; }
        public bool ReplacementsLogged { get; }
    }
}
