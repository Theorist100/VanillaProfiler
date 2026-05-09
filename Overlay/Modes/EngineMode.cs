using VanillaProfiler.Diagnostics;

namespace VanillaProfiler.Overlay.Modes
{
    /// <summary>
    /// Engine-counter view: per-frame thread breakdown, render counts, GPU memory
    /// breakdown, GC stalls. Sourced from Unity's ProfilerRecorder API (Memory/Render/GC
    /// categories) which stays live in CS2 release — what the in-game dev panel "Display
    /// Stats" tab shows, just consolidated and historical via the report cadence.
    ///
    /// Reading guide:
    ///   - PresentWait high + Main low → GPU bottleneck (lower graphics, not ECS)
    ///   - SetPass spike → shader-state churn (material/keyword variance)
    ///   - GPU breakdown lets you tell a buffer leak from a render-target leak
    ///   - GC.Collect total stall isolates GC stutter from sync-point stutter
    /// </summary>
    public sealed class EngineMode : IOverlayMode
    {
        public string DisplayName => "Engine";
        public bool IsHidden => false;

        public float MeasureHeight(OverlaySnapshot snapshot)
        {
            // Header + breadcrumb (2 lines via DrawHeaderWithCycle pattern). Then sections:
            //   Frame timing (4 rows: Main/Render/GPU/PresentWait)         — always shown
            //   Render counts (3 rows: DrawCalls+SetPass / Tris+Verts / Shadow casters)
            //   GPU memory (1-2 rows: Buffers, RT)                          — conditional
            //   GC (1 row)                                                  — conditional
            //   Process RSS (1 row)                                         — conditional
            int lines = 2; // header + breadcrumb (one line each via OverlayPanel)
            // Always render the frame-timing block so the screen has stable height during
            // settling — empty rows are clearer than a panel that resizes every report.
            lines += 1 + 4; // section title + 4 rows
            lines += 1 + 3; // Render counts: title + 3 rows
            if (HasGpuMemory(snapshot))
                lines += 1 + 2; // title + Buffers + RT
            if (snapshot.GcCollectCount > 0)
                lines += 1 + 1; // title + 1 row
            if (snapshot.AppResidentMB > 0)
                lines += 1; // RSS row in-line
            return OverlayPanel.PAD * 2 + OverlayPanel.LINE_H * lines + 12f;
        }

        public void Draw(DrawContext ctx, OverlaySnapshot snapshot, HealthReport health)
        {
            OverlayPanel.DrawHeaderWithCycle(ctx, "VANILLA PROFILER  >  ENGINE");

            // Frame timing — direct readouts of Unity's CPU/GPU thread markers so the
            // player sees which stage of the pipeline is gating the frame. PresentWait is
            // the headline GPU-bound signal: high here means CPU is idle waiting on GPU.
            OverlayPanel.DrawSection(ctx, "Frame timing");
            OverlayPanel.DrawCounterRow(ctx, "  CPU main:", snapshot.MainThreadCpuMs,
                snapshot.MainThreadCpuAvailable, "ms", ctx.Theme.BodyStyle);
            OverlayPanel.DrawCounterRow(ctx, "  CPU render:", snapshot.RenderThreadCpuMs,
                snapshot.RenderThreadCpuAvailable, "ms", ctx.Theme.BodyStyle);
            OverlayPanel.DrawCounterRow(ctx, "  GPU:", snapshot.GpuFrameTimeMs,
                snapshot.GpuFrameTimeAvailable, "ms", ctx.Theme.BodyStyle);
            OverlayPanel.DrawLine(ctx,
                $"  Present wait:  {OverlayFormat.Counter(snapshot.PresentWaitMs, snapshot.PresentWaitAvailable, "ms"),9}{PresentWaitHint(snapshot)}",
                snapshot.PresentWaitMs > snapshot.AvgFrameMs * 0.5
                    ? ctx.Theme.StyleForHealth(HealthLevel.Poor)
                    : ctx.Theme.DimStyle);

            OverlayPanel.DrawSection(ctx, "Render counts");
            OverlayPanel.DrawLine(ctx,
                $"  DrawCalls {OverlayFormat.Counter(snapshot.DrawCalls, snapshot.DrawCallsAvailable),6}   SetPass {OverlayFormat.Counter(snapshot.SetPassCalls, snapshot.SetPassCallsAvailable),4}",
                ctx.Theme.BodyStyle);
            OverlayPanel.DrawLine(ctx,
                $"  Tris {OverlayFormat.CounterK(snapshot.Triangles, snapshot.TrianglesAvailable),8}   Verts {OverlayFormat.CounterK(snapshot.Vertices, snapshot.VerticesAvailable),8}",
                ctx.Theme.BodyStyle);
            OverlayPanel.DrawLine(ctx,
                $"  Shadow casters: {OverlayFormat.Counter(snapshot.ShadowCasters, snapshot.ShadowCastersAvailable)}",
                ctx.Theme.BodyStyle);

            if (HasGpuMemory(snapshot))
            {
                OverlayPanel.DrawSection(ctx, "GPU memory");
                OverlayPanel.DrawLine(ctx,
                    $"  Used buffers:  {OverlayFormat.Counter(snapshot.UsedBuffersMB, snapshot.UsedBuffersBytesAvailable, "MB"),10}  ({OverlayFormat.Counter(snapshot.UsedBuffersCount, snapshot.UsedBuffersCountAvailable)} bufs)",
                    ctx.Theme.BodyStyle);
                OverlayPanel.DrawLine(ctx,
                    $"  Render targets:{OverlayFormat.Counter(snapshot.RenderTexturesMB, snapshot.RenderTexturesBytesAvailable, "MB"),10}",
                    ctx.Theme.BodyStyle);
            }

            if (snapshot.GcCollectCount > 0)
            {
                OverlayPanel.DrawSection(ctx, "GC");
                OverlayPanel.DrawLine(ctx,
                    $"  {snapshot.GcCollectCount} collections, total stall {snapshot.GcCollectStallMs:F2} ms",
                    snapshot.GcCollectStallMs > snapshot.AvgFrameMs
                        ? ctx.Theme.StyleForHealth(HealthLevel.Poor)
                        : ctx.Theme.BodyStyle);
            }

            if (snapshot.AppResidentMB > 0)
            {
                OverlayPanel.DrawLine(ctx,
                    $"Process RSS:    {snapshot.AppResidentMB,7:F0} MB",
                    ctx.Theme.DimStyle);
            }
        }

        private static bool HasGpuMemory(OverlaySnapshot snap)
            => snap.UsedBuffersBytesAvailable || snap.RenderTexturesBytesAvailable;

        private static string PresentWaitHint(OverlaySnapshot snap)
        {
            if (snap.AvgFrameMs <= 0 || snap.PresentWaitMs <= 0) return "";
            double share = snap.PresentWaitMs / snap.AvgFrameMs * 100.0;
            return $"  ({share:F0}% of frame — GPU-bound when high)";
        }
    }
}
