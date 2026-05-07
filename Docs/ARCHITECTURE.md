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
├── Profiler.cs                 Aggregator. Owns the lock, the report writer, the snapshot.
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
                    │   • MetricsAggregator (locked dicts)       │
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

`Profiler` is an **instance** owned by `VanillaProfilerMod` and reachable via the static `ProfilerHost.TryGet()`. The only multi-threaded entry point is `Profiler.RecordSystem`, which delegates to `MetricsAggregator`'s internal lock — that is the single point of synchronisation in this mod. Everything else (`OnFrame`, `OnSimTick`, `RecordPhase`, `Report`, `MemorySampler`, `MemoryHistory`, `FpsSparkline`, `LastSnapshot`, `LastHealth`) is main-thread only.

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
- Skip own systems (`namespace == "VanillaProfiler"`)
- Push delta into `Profiler.RecordSystem`

`ModAttribution` decides ownership using:

1. Namespace prefix (Game./Unity./Colossal. → Vanilla)
2. Assembly scan for `IMod` implementer (the assembly name becomes the mod name)
3. Fallback to assembly simple name

The result is cached per type. Cold-path scan happens once per type discovered; subsequent calls are dictionary lookups.

### `MemoryHistory`

Sliding window of 12 samples (≈ 60 s at 5 s reporting). Leak detection requires a full 5-sample window so a single one-off allocation doesn't trip the alarm. The metric used is the **median** delta per report — robust against single outliers.

`WindowSeconds` is computed against `ProfilerSettings.Current.ReportIntervalSec` so changing the report cadence at runtime keeps the displayed text honest.

### `HealthClassifier`

Pure function: `(snapshot, memoryHistory, simPhaseMs, renderPhaseMs) → HealthReport`. No mutation. Thresholds are constants in the file — change them there if you tune.

`Classify()` returns:
- Per-metric levels (FPS, stutter, memory, managed growth) — fed to the overlay traffic light
- Overall level — worst of the four
- Bottleneck verdict (RENDER / SIM / MEMORY / BALANCED) with a one-line hint

The bottleneck logic uses **average phase ms per call** — derived from `Profiler.SumPhaseMs` which sums total ticks across phases matching a substring (`"GameSimulation"`, `"Rendering"`).

## Harmony patches

| Patch | Target | Purpose |
|---|---|---|
| `SystemAutoProfiler` | `SystemBase.Update` | Per-system main-thread cost for every system in every world |
| `UpdateSystemPatch.UpdatePhase` | `UpdateSystem.Update(SystemUpdatePhase)` | Phase timing + render frame trigger |
| `UpdateSystemPatch.UpdatePhaseWithIndex` | `UpdateSystem.Update(SystemUpdatePhase, uint, int)` | GameSimulation tick phase timing |
| `EntityCommandBufferPatch.PlaybackEntityManager` | `EntityCommandBuffer.Playback(EntityManager)` | ECB playback time, surfaced as `ECB.Playback` phase |
| `EntityCommandBufferPatch.PlaybackExclusiveTransaction` | `EntityCommandBuffer.Playback(ExclusiveEntityTransaction)` | Same, alternate overload (gated when missing on the build) |

`HarmonyConflictDetector` runs once after the first report (deferred to give other mods time to apply their patches). It enumerates `Harmony.GetAllPatchedMethods()` and lists any method patched by more than one owner. The result goes to `VanillaProfiler.log` as a one-off section.

## Threading model

The mod follows a **single multi-threaded entry point** rule rather than scattering locks everywhere:

- **`Profiler.RecordSystem`** is the only API that can be called from worker threads. It is invoked from `SystemBase.Update` Postfix, which Unity Entities can schedule on any worker. It delegates to `MetricsAggregator`, whose internal lock is the only synchronisation primitive in the mod.
- **Everything else is main-thread only:**
  - `OnFrame` is called from `UpdateSystem.Update(Rendering)` Postfix — main thread by Unity contract.
  - `OnSimTick` is called from `SimTickCounterSystem.OnUpdate` — ECS GameSimulation phase, main thread.
  - `RecordPhase` is called from the per-phase Postfix — main thread.
  - `MemorySampler`, `MemoryHistory`, `FpsSparkline` are touched only from `OnFrame`/`Report`.
  - `LastSnapshot` and `LastHealth` are written by `Report` and read by `ProfilerOverlay.OnGUI` — both main thread.
- **Disposal** is guarded by a `volatile bool m_Disposed`. Every public entry point checks it first so an in-flight worker-thread Postfix landing after `Mod.OnDispose` no-ops cleanly.
- **`ProfilerHost.TryGet()`** does a single `Volatile.Read` of the instance reference. Callers must capture the result into a local variable before use; never write `ProfilerHost.IsAvailable` followed by `ProfilerHost.Current` — that's a TOCTOU bug.

## What this mod intentionally does NOT do

- **No persistence beyond settings.** Nothing serialised in saves. Sessions are independent.
- **No structural ECS changes.** Only read-only `EntityQuery.CalculateEntityCount()`. Patches are pre/postfix only — never transpilers, never finalizers.
- **No UI Toolkit / Coherent UI.** IMGUI is sufficient and avoids the layering complexity of CS2's binding system.
- **No quality recommendations beyond bottleneck hint.** "Render-bound, lower graphics" is generic enough; finer advice would be guesswork.
- **No per-job worker-thread profiling.** Burst-compiled jobs run as native code outside `SystemBase.Update()`; their per-system cost cannot be observed from a mod in a release build. Unity Profiler with engine-level instrumentation is the only tool that can. We surface aggregate worker time when the `ProfilerCategory.Internal` markers are exposed (usually stripped in release — silently zero when missing).

## See also

- [../USER_GUIDE.md](../USER_GUIDE.md) — Player-facing reference for the overlay
- [DEVELOPER_NOTES.md](DEVELOPER_NOTES.md) — How to add a metric or extend the overlay
- [PLAN.md](../../VanillaProfiler/PLAN.md) — Original phase-by-phase plan
