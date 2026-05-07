# VanillaProfiler — Rework Plan (Positioning Fix)

## Context

On 2026-05-02, krzychu124 (TM:PE author, CS:Skylines beta moderator at Colossal Order) posted a technically correct critique on the Performance Monitor mod page (Paradox Mods 84661):

> Not to mention that it doesn't really measure what it should. What's the point of measuring system update time if that update is in 99.9% cases scheduling job(s) that will do work on one or more worker threads? The system update will take 0.001-0.01ms, while spawned jobs can do work on multiple worker threads — roughly 1ms per job worker, but 21ms in total, spread over 26 workers running in parallel.

He also noted GC garbage allocation in the sampler/report path, and recommended Unity Profiler as the real tool for per-job analysis.

He is right. We mispositioned the mod.

## What's actually true

`Stopwatch.GetTimestamp()` around `SystemBase.Update()` captures **main-thread cost only**: scheduling overhead, sync points (`Dependency.Complete`, `CompleteDependencyBeforeRO`), structural changes (`EntityManager.*`), `EntityCommandBuffer.Playback()`, and any system that does foreach work on the main thread. For well-architected DOTS systems that schedule jobs cleanly to workers, our measurement returns ~0.001-0.01ms while the actual work happens invisibly on up to 26 worker threads.

We cannot fix this from a mod in a release build. Burst replaces C# with native code, so Harmony can't add `ProfilerMarker` instrumentation inside vanilla jobs. Per-job markers via `ProfilerRecorder` are stripped from the release build. Only Unity Profiler (with the editor attached) can see job execution accurately.

## What our profiler actually delivers (accurately)

- Frame time + spike detection (Stopwatch on frame deltas — main thread only, but main thread is what frame time *is*)
- GPU Frame Time via `ProfilerRecorder` (Render category, hardware-reported)
- CPU Main Thread / CPU Render Thread Frame Time via `ProfilerRecorder`
- Process memory: managed (`GC.GetTotalMemory`), Mono heap, native alloc/reserved, GPU (`GetAllocatedMemoryForGraphicsDriver`)
- Managed heap growth rate (leak detection)
- Per-system **main-thread** cost: scheduling + sync points + structural changes + ECB playback. Useful for finding badly-architected systems and main-thread bottlenecks. Misleading for ranking well-written DOTS systems.
- Frame spike screenshot
- HarmonyConflictDetector

## Reply already sent to krzychu124

Acknowledged the critique, confirmed the rework, framed VanillaProfiler honestly as a main-thread / sync-point monitor, pointed users to Unity Profiler for per-job analysis.

---

## Plan

### Phase 0 — Public-facing fix (today)

- [ ] Post reply to krzychu124 on Paradox Mods 142844
- [ ] Update mod 142844 description on Paradox Mods: drop "profiler" framing, position as "frame health, sync point and memory monitor for CS2", explicitly recommend Unity Profiler for job-level work

### Phase 1 — Reframe (1-2 days)

`D:\VanillaProfiler-public\` minimal rework, no measurement changes:

- [ ] Rename overlay/log fields: `System Time` → `Main Thread Time per System`, `Top Systems` → `Top Main-Thread Systems`
- [ ] Add explicit caveat in every report header: *"These numbers reflect main-thread cost only (scheduling overhead + sync points + structural changes + ECB playback). Job execution on worker threads is not captured. Use Unity Profiler for job-level analysis."*
- [ ] Sync-point flag: if `SystemBase.Update()` time exceeds threshold (e.g. 0.5 ms), tag the record as `[likely sync point]`
- [ ] Fix GC garbage in `MemorySampler.AverageValid` (pool the `List<ProfilerRecorderSample>`, or use `Span` API if available)
- [ ] Audit `ReportBuilder` and `LogFileSink` for hot-path string formatting and dict iteration allocations
- [ ] Bump version, publish to Paradox Mods with changelog explicitly crediting krzychu124's feedback

### Phase 2 — Add what we *can* measure correctly (1 week)

- [ ] `EntityCommandBuffer.Playback()` Harmony hook — main thread, real cost, accurately measurable
- [x] Sync-point detection per system — implemented as time-threshold suspect count in Phase 1 (`PhaseData.SyncPointSuspectCount`). Direct `JobHandle.Complete` Harmony patching was investigated and **rejected**: it dispatches hundreds to thousands of times per frame in vanilla CS2 and any Postfix overhead would distort the very measurement it produces. The threshold approach captures the same signal (Update() exceeded scheduling overhead → real main-thread work happened) at zero runtime cost. ECB.Playback timing covers the most common explicit sync surface separately.
- [ ] Aggregate parallel-job time per frame via `ProfilerRecorder` Internal category (`JobsParallelFor.Execute` if available in release build) — sums all job time per frame, no per-system attribution but honest
- [ ] Top-N mods by **main-thread** cost (with same caveat as per-system)
- [ ] Update README and USER_GUIDE.md with the new framing

### Phase 3 — Optional experiments (2 weeks out)

- [ ] JobHandle latency tracking: hook dependency chain, measure wall-clock "time job was outstanding". Honest caveat: this is wall-clock latency, not CPU time. A job outstanding 100ms may use 5ms of CPU because workers were busy elsewhere. Only ship if numbers are useful in practice.

### Phase 4 — Apply same rework to CivicSurvival's PERF.log (parallel)

`D:\CivicSurvival\` `VanillaSystemAutoProfiler` and `Tools/analyze_perf.py`:

- [ ] Rename report fields to match new framing
- [ ] Add sync-point flag in `analyze_perf.py` output
- [ ] Internally for our optimization workflow nothing changes — we already use it for sync-point and main-thread bottleneck hunting (IPSO sync point, BuildCoverage in MentalHealth, AirDefState spikes, TAS sync points were all main-thread issues that the existing measurement caught accurately)

---

## Why our internal CivicSurvival workflow stays valid

The bugs we fixed via PERF.log were all main-thread bottlenecks: sync points, structural changes, EntityManager calls, ECB playback on critical path, foreach queries without jobs. `Stopwatch` around `Update()` measures these accurately because they all happen *on* the main thread. The "scheduling time" critique applies to well-architected DOTS systems, which are not what we needed to optimize — they were already cheap. Our profiler is effectively a sync-point and main-thread bottleneck finder, and that matches the problems we have. We just never named it that explicitly.

For deeper investigations (motion blur pulsation, GPU-level analysis), we use Unity Profiler. That separation is correct and stays.

## Key references

- Critique: Performance Monitor mod 84661 comments on Paradox Mods, krzychu124, 2026-05-02
- VanillaProfiler mod ID: 142844 (Paradox Mods)
- Source: `D:\VanillaProfiler-public\` → https://github.com/Theorist100/VanillaProfiler
- Performance Monitor source: https://github.com/rcav8tr/CS2Mod-PerformanceMonitor
