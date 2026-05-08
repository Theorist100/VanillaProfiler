# VanillaProfiler — Architecture

## Goals

- **Zero gameplay impact.** No entities created, no game state mutated. All measurement happens through Harmony patches and read-only ECS queries.
- **Low overhead.** ~300 Stopwatch start/stop pairs per sim tick + four small queries every 5 seconds. Negligible compared to the systems being measured.
- **Two audiences.** Status / Diagnosis modes for normal players (traffic-light health, leak detection, support-file export). Details mode + `VanillaProfiler.log` for mod authors and power users (per-system main-thread cost, sync-point flagging, ECB.Playback timing, memory deltas).

## Scope of measurement

Per-system numbers (`SystemAutoProfiler` Stopwatch around `SystemBase.Update`) capture **main-thread cost only** — scheduling overhead, sync points (`Dependency.Complete`, `CompleteDependencyBeforeRO`), structural changes, `EntityCommandBuffer.Playback`, and any synchronous main-thread work. Burst-compiled jobs scheduled to worker threads run as native code outside `SystemBase.Update()` and cannot be instrumented from a mod in a release build. Frame time, GPU/CPU thread time (Unity ProfilerRecorder), all memory metrics and the sync-point threshold flag are accurate. For per-job analysis attach Unity Profiler.

## Module layout

```
VanillaProfiler (assembly)
├── Mod.cs                      Entry point — loads settings, starts profiler, registers patches
├── Profiler.cs                 Main-thread coordinator. Owns sinks, sampler, builder, snapshots.
├── ProfilerOverlay.cs          IMGUI presentation layer. No measurement logic.
├── SystemAutoProfiler.cs       Harmony patch — measures every SystemBase.Update() call (main-thread cost).
├── UpdateSystemPatch.cs        Harmony patch — measures phase boundaries (Pre/Post/Render).
├── EntityCommandBufferPatch.cs Harmony patch — times EntityCommandBuffer.Playback (always main-thread).
├── SimTickCounterSystem.cs     Counts simulation ticks (1 call per game tick).
├── CityContextSystem.cs        ECS — refreshes citizen/vehicle/building counts every 5 s.
└── Diagnostics/                Pure-logic helpers, no Harmony, no Unity API except where noted.
```

## Data flow

```
                    ┌─────────────────────────────────────────────┐
                    │ Harmony patches                              │
                    │  • SystemBase.Update         (per system)    │
                    │  • UpdateSystem.Update       (per phase)     │
                    └──────────┬─────────────────────┬─────────────┘
                               │ Stopwatch deltas    │
                               ▼                     ▼
                    ┌──────────────────────────────────────────┐
                    │  Profiler  (instance, held by ProfilerHost)│
                    │  Delegates to:                             │
                    │   • MetricsAggregator (single-threaded)    │
                    │   • MemorySampler                          │
                    │   • MemoryHistory + FpsSparkline           │
                    └──────────┬───────────────────────────────┘
                               │ every REPORT_INTERVAL
                               ▼
                    ┌──────────────────────────────────────────┐
                    │            Profiler.Report()             │
                    │  1. MetricsAggregator.Drain()            │
                    │  2. MemorySampler.Sample()               │
                    │  3. MemoryHistory.Record                 │
                    │  4. ReportBuilder → snapshot + health    │
                    │  5. Foreach IReportSink: WriteReport     │
                    │  6. Update LastSnapshot, LastHealth      │
                    └──────────┬─────────────────┬─────────────┘
                               │                 │
                               ▼                 ▼
                    ┌──────────────────┐  ┌──────────────────┐
                    │ LogFileSink      │  │  ProfilerOverlay │
                    │ (VanillaProfiler.log│  │  reads LastSnap  │
                    │  via StreamWriter)│  │  and LastHealth  │
                    └──────────────────┘  └──────────────────┘
```

`Profiler` is an **instance** owned by `VanillaProfilerMod` and reachable via the static `ProfilerHost.TryGet()`. Measurement entry points (`OnFrame`, `OnSimTick`, `RecordPhase`, `RecordSystem`, `Report`) are main-thread only; `MainThreadGuard` asserts that contract. `MetricsAggregator` is therefore a single-threaded accumulator with borrowed dictionaries, not a thread-safe container.

## Key contracts

### `Profiler.OnFrame()`

Called from the **Rendering** phase Harmony patch (`UpdateSystemPatch`). One call per render frame. Responsibilities:

1. Compute frame delta from previous timestamp
2. Detect spikes (`> SPIKE_30FPS_MS` / `> SPIKE_20FPS_MS`)
3. Push the frame into `FpsSparkline` and `SpikeScreenshot`
4. Trigger `Report()` when `s_ReportTimer` exceeds `ReportIntervalSec`

The split between "what happens every frame" and "what happens every report" matters: the per-frame work is critical-path; everything else (table sorting, file writes, classification) is amortised every 5 s.

### `SystemAutoProfiler.Postfix`

Runs after every `SystemBase.Update`. Does:

- Resolve `Type` → `(name, isVanilla, modName)` once via `ModAttribution.Resolve` (cached)
- For vanilla types, query `SystemReplacementDetector.IsPatched(Type)` (O(1) hash lookup, not cached on `SystemInfo` because mod-options screens can flip Harmony patches at runtime)
- Route the elapsed delta:
  - **patched vanilla** → `Profiler.RecordPatchedVanilla` (independent of `ProfileVanillaSystems`; the elapsed time blends mod prefix + vanilla original)
  - **plain vanilla** → `Profiler.RecordSystem` if `ProfileVanillaSystems = true`, else dropped
  - **mod system** → `Profiler.RecordSystem`

`ModAttribution` decides ownership using:

1. Namespace prefix (Game./Unity./Colossal. → Vanilla)
2. Assembly scan for `IMod` implementer (the assembly name becomes the mod name)
3. Fallback to assembly simple name

The result is cached per type. Cold-path scan happens once per type discovered; subsequent calls are dictionary lookups.

### `SystemReplacementDetector`

Walks `World.All` once per report cycle, looking for vanilla systems whose `OnUpdate` has a foreign Harmony prefix. Maintains two outputs:

- **`IsPatched(Type)`** — O(1) hash-set probe used by `SystemAutoProfiler.Postfix` on the hot path. Atomically swapped on each scan; main-thread only, no volatile needed.
- **`Scan()` result** — `List<Replacement>` consumed by `Profiler.Report` to build the `OverlaySnapshot.ReplacedVanillaSystems` array. Merged with `MetricsSample.PatchedVanillaSystems` (the per-cycle ms bucket) by full type name, so each row carries `(VanillaSystem, OwnerMod, TotalMs)`.

Why a separate bucket: a Harmony prefix can wrap or skip the original `OnUpdate`, so the elapsed time inside the patched `SystemBase.Update` is a blend of mod-prefix work and (possibly skipped) vanilla original. There is no Harmony hook between prefix and original to capture an intermediate timestamp. Surfacing the total `Update` ms is honest — the split is what's not measurable, not the cost itself.

### `MemoryHistory`

Sliding window of 12 samples (≈ 60 s at 5 s reporting). Leak detection requires a full 5-sample window so a single one-off allocation doesn't trip the alarm. The metric used is the **median** delta per report — robust against single outliers.

`WindowSeconds` is computed against `ProfilerSettings.Current.ReportIntervalSec` so changing the report cadence at runtime keeps the displayed text honest.

### `HealthClassifier`

Pure function: `(snapshot, memoryHistory, simPhaseMs, renderPhaseMs) → HealthReport`. No mutation. Thresholds are constants in the file — change them there if you tune.

`Classify()` returns:
- Per-metric levels (FPS, stutter, memory, managed growth) — fed to the overlay traffic light
- Overall level — worst of the four
- Bottleneck verdict (RENDER / SIM / MEMORY / BALANCED) with a one-line hint

The bottleneck logic uses **average phase ms per call** — derived from exact phase-key lookups for `UpdateSystem.GameSimulation` and `UpdateSystem.Rendering`.

## Harmony patches

| Patch | Target | Purpose |
|---|---|---|
| `SystemAutoProfiler` | `SystemBase.Update` | Per-system main-thread cost for every system in every world |
| `UpdateSystemPatch.UpdatePhase` | `UpdateSystem.Update(SystemUpdatePhase)` | Phase timing + render frame trigger |
| `UpdateSystemPatch.UpdatePhaseWithIndex` | `UpdateSystem.Update(SystemUpdatePhase, uint, int)` | GameSimulation tick phase timing |
| `EntityCommandBufferPatch.PlaybackEntityManager` | `EntityCommandBuffer.Playback(EntityManager)` | ECB playback time, surfaced as `ECB.Playback` phase |
| `EntityCommandBufferPatch.PlaybackExclusiveTransaction` | `EntityCommandBuffer.Playback(ExclusiveEntityTransaction)` | Same, alternate overload (gated when missing on the build) |

`HarmonyConflictDetector` runs once after the first report (deferred to give other mods time to apply their patches). It enumerates `Harmony.GetAllPatchedMethods()` and lists methods patched by more than one owner only when VanillaProfiler is one of those owners. The result goes to `VanillaProfiler.log` as a one-off section.

## Threading model

The mod follows a **main-thread measurement** rule rather than scattering locks everywhere:

- **`Profiler.RecordSystem`** is invoked from `SystemBase.Update` Postfix on the Unity main thread. It records into `MetricsAggregator` without locking.
- **Measurement state is main-thread only:**
  - `OnFrame` is called from `UpdateSystem.Update(Rendering)` Postfix — main thread by Unity contract.
  - `OnSimTick` is called from `SimTickCounterSystem.OnUpdate` — ECS GameSimulation phase, main thread.
  - `RecordPhase` is called from the per-phase Postfix — main thread.
  - `RecordSystem` is called from `SystemBase.Update` Postfix — main thread.
  - `MemorySampler`, `MemoryHistory`, `FpsSparkline` are touched only from `OnFrame`/`Report`.
  - `LastSnapshot` and `LastHealth` are written by `Report` and read by `ProfilerOverlay.OnGUI` — both main thread.
- **Disposal** is guarded by a `volatile bool m_Disposed`. Every public entry point checks it first so a stale Harmony callback landing after `Mod.OnDispose` no-ops cleanly.
- **Log output has its own lock.** `LogFileSink` serializes file IO because system messages can be routed from defensive off-thread diagnostics.
- **`ProfilerHost.TryGet()`** does a single `Volatile.Read` of the instance reference. Callers must capture the result into a local variable before use; never write `ProfilerHost.IsAvailable` followed by `ProfilerHost.Current` — that's a TOCTOU bug.

## What this mod intentionally does NOT do

- **No persistence beyond settings.** Nothing serialised in saves. Sessions are independent.
- **No structural ECS changes.** Only read-only `EntityQuery.CalculateEntityCount()`. Patches use prefix/postfix, plus a narrow finalizer only where needed to close `ProfilerMarker` scopes on thrown `SystemBase.Update()`.
- **No UI Toolkit / Coherent UI.** IMGUI is sufficient and avoids the layering complexity of CS2's binding system.
- **No quality recommendations beyond bottleneck hint.** "Render-bound, lower graphics" is generic enough; finer advice would be guesswork.
- **No per-job worker-thread profiling.** Burst-compiled jobs run as native code outside `SystemBase.Update()`; their per-system cost cannot be observed from a mod in a release build. Unity Profiler with engine-level instrumentation is the only tool that can. We surface aggregate worker time when the `ProfilerCategory.Internal` markers are exposed (usually stripped in release — silently zero when missing).

## See also

- [../USER_GUIDE.md](../USER_GUIDE.md) — Player-facing reference for the overlay
- [DEVELOPER_NOTES.md](DEVELOPER_NOTES.md) — How to add a metric or extend the overlay
- [PLAN.md](../../VanillaProfiler/PLAN.md) — Original phase-by-phase plan
