# VanillaProfiler — Architecture

## Goals

- **Zero gameplay impact.** No entities created, no game state mutated. All measurement happens through Harmony patches and read-only ECS queries.
- **Low overhead.** ~300 Stopwatch start/stop pairs per sim tick + four small queries every 5 seconds. Negligible compared to the systems being measured.
- **Two audiences.** Status / Diagnosis modes for normal players (traffic-light health, leak detection, support-file export). Details mode + `VanillaProfiler.log` for mod authors and power users (per-system self main-thread cost, sync-point flagging, ECB.Playback timing, memory deltas).

## Scope of measurement

Top per-system numbers (`SystemAutoProfiler` Stopwatch around `SystemBase.Update`) capture **self/exclusive main-thread cost only** — scheduling overhead, sync points (`Dependency.Complete`, `CompleteDependencyBeforeRO`), structural changes, `EntityCommandBuffer.Playback`, and any synchronous main-thread work, with nested `SystemBase.Update` calls subtracted from the parent system. Inclusive elapsed Update time is retained in `PhaseData.InclusiveTicks` for diagnostics such as patched-vanilla rows. Burst-compiled jobs scheduled to worker threads run as native code outside `SystemBase.Update()` and cannot be instrumented from a mod in a release build. Frame time, GPU/CPU thread time (Unity ProfilerRecorder), all memory metrics and the sync-point threshold flag are accurate. For per-job analysis attach Unity Profiler.

## Module layout

```
VanillaProfiler (assembly)
├── Mod.cs                      Entry point — loads settings, starts profiler, registers patches
├── Profiler.cs                 Main-thread coordinator. Implements IProfilerPatchSurface + IProfilerReadSurface; delegates timing to ReportScheduler and sink fan-out to ReportDispatcher.
├── ProfilerSessionState.cs     Lifecycle/read-state owner; clears public snapshots on menu/loading/city boundaries.
├── SessionBoundary.cs          Typed reset reasons used by Profiler.ResetForBoundary.
├── IProfilerSurfaces.cs        Two interfaces over the same Profiler instance:
│                               • IProfilerPatchSurface — narrow surface for Harmony patches and ECS counter systems
│                                 (OnFrame / OnSimTick / RecordSystem / RecordPatchedVanilla / RecordPhase)
│                               • IProfilerReadSurface — overlay/export/lifecycle/log surface (LastSnapshot,
│                                 LastHealth, MemoryHistory, FpsSparkline, SpikeScreenshots, GraphicsSettings,
│                                 Recommendations, LifecycleState, ForceReport, SetGameLoaded, BeginLoading, LogInfo/Warn/Error)
├── ProfilerHost.cs             Static handle — Volatile.Read returning IProfilerPatchSurface or IProfilerReadSurface; the concrete Profiler type is not exposed.
├── ProfilerOverlay.cs          IMGUI presentation layer. No measurement logic. Holds OverlayState for navigation.
├── ReportScheduler.cs          Owns frame-to-frame timing and report cadence; answers "is a report due now?".
├── SystemAutoProfiler.cs       Harmony patch — measures every SystemBase.Update() call (self + inclusive main-thread cost).
├── UpdateSystemPatch.cs        Harmony patch — measures phase boundaries (Pre/Post/Render).
├── EntityCommandBufferPatch.cs Harmony patch — times EntityCommandBuffer.Playback, including thrown playbacks via finalizer.
├── SimTickCounterSystem.cs     Counts simulation ticks (1 call per game tick).
├── CityContextSystem.cs        ECS — refreshes citizen/vehicle/building counts every 5 s.
├── Aggregation/                Sample collection and snapshot construction. Single-threaded buffers.
├── Diagnostics/                Pure-logic helpers, no Harmony, no Unity API except where noted.
├── Output/                     Report sinks and dispatcher (per-sink failure isolation).
└── Overlay/                    IMGUI panels, modes, theme, settings UI, navigation state.
```

## Data flow

```
                    ┌─────────────────────────────────────────────┐
                    │ Harmony patches  +  ECS counter systems     │
                    │  • SystemBase.Update         (per system)   │
                    │  • UpdateSystem.Update       (per phase)    │
                    │  • EntityCommandBuffer.Playback (ECB time)  │
                    │  • SimTickCounterSystem      (per sim tick) │
                    └──────────┬──────────────────────────────────┘
                               │ Stopwatch deltas via IProfilerPatchSurface
                               ▼
                    ┌──────────────────────────────────────────┐
                    │  Profiler  (instance, held by ProfilerHost)│
                    │  Patch entries (IProfilerPatchSurface):   │
                    │   • OnFrame, OnSimTick                    │
                    │   • RecordSystem, RecordPatchedVanilla    │
                    │   • RecordPhase                           │
                    │  Delegates to:                            │
                    │   • ReportScheduler  (frame timing,       │
                    │                       report cadence)     │
                    │   • MetricsAggregator (single-threaded)   │
                    │   • MemorySampler                         │
                    │   • MemoryHistory + FpsSparkline          │
                    └──────────┬───────────────────────────────┘
                               │ ReportScheduler signals "report due"
                               ▼
                    ┌──────────────────────────────────────────┐
                    │            Profiler.Report()             │
                    │  1. MetricsAggregator.Drain()            │
                    │  2. MemorySampler.Sample()               │
                    │  3. MemoryHistory.Record                 │
                    │  4. ReportBuilder → snapshot + health    │
                    │  5. AttachReplacementSnapshot            │
                    │  6. PublishSnapshot (LastSnapshot/Health)│
                    │  7. ReportDispatcher.WriteReport         │
                    └──────────┬─────────────────┬─────────────┘
                               │                 │
                               ▼                 ▼
                    ┌──────────────────┐  ┌──────────────────┐
                    │ ReportDispatcher │  │  ProfilerOverlay │
                    │  per-sink try/   │  │  reads LastSnap  │
                    │  catch isolation │  │  and LastHealth  │
                    │  → IReportSink[] │  │                  │
                    │   • LogFileSink  │  │                  │
                    └──────────────────┘  └──────────────────┘
```

`Profiler` is an **instance** owned by `VanillaProfilerMod`. `ProfilerHost` exposes it only through two interfaces: `ProfilerHost.TryGetPatchSurface()` returns `IProfilerPatchSurface` for Harmony patches and ECS counter systems; `ProfilerHost.TryGetReadSurface()` returns `IProfilerReadSurface` for overlay, export, lifecycle and logging. The concrete `Profiler` type is not reachable through the host. `MainThreadGuard` asserts the main-thread contract on the patch surface; `MetricsAggregator` is therefore a single-threaded accumulator with borrowed dictionaries, not a thread-safe container.

`ProfilerSessionState` is the single lifecycle/read-state source. `Mod.OnLoad` calls `Profiler.InitializeFromCurrentMode(GameManager.instance.gameMode)` immediately after registering the profiler, so hot-reloading the mod while a city is already active enters the same settling path as a normal load-complete callback. City load/unload, loading start and dispose all route through `Profiler.ResetForBoundary(SessionBoundary)`; that reset clears stale public snapshots, city context, graphics/replacement caches, spike screenshot state, memory history and the scheduler. `MemorySampler.ResetSession()` recreates Unity `ProfilerRecorder` instances at these boundaries so CPU/GPU timing windows do not bleed across menu/loading/previous-city samples.

## Key contracts

### `Profiler.OnFrame()`

Called from the **Rendering** phase Harmony patch (`UpdateSystemPatch`). One call per render frame. Responsibilities:

1. Ask `ReportScheduler.TryAdvanceFrame` for the frame delta and the "report due" flag
2. Detect spikes (`> SPIKE_30FPS_MS` / `> SPIKE_20FPS_MS`)
3. Push the frame into `FpsSparkline` and `SpikeScreenshot`
4. Call `Report()` when `ReportScheduler` signals the cadence has elapsed

`ReportScheduler` owns frame-to-frame timing and the report timer — `Profiler` does not hold those fields directly. The split between "what happens every frame" and "what happens every report" matters: the per-frame work is critical-path; everything else (table sorting, file writes, classification) is amortised every 5 s.

### `SystemAutoProfiler.Postfix`

Runs after every `SystemBase.Update`. Does:

- Pop the thread-local measurement stack and compute `selfTicks = inclusiveTicks - childSystemUpdateTicks`
- Resolve `Type` → `(name, isVanilla, modName)` once via `ModAttribution.Resolve` (cached)
- For vanilla types, query `SystemReplacementDetector.IsPatched(Type)` (O(1) hash lookup, not cached on `SystemInfo` because mod-options screens can flip Harmony patches at runtime)
- Route the elapsed delta:
  - **patched vanilla** → `Profiler.RecordPatchedVanilla` (independent of `ProfileVanillaSystems`; reports total/inclusive Update ms because the elapsed time blends mod prefix + vanilla original)
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

`MemoryHistory` receives a `Func<ProfilerSettingsSnapshot>` accessor at construction; `WindowSeconds` is computed against the snapshot's `ReportIntervalSec` so changing the report cadence at runtime keeps the displayed text honest. A report elapsed time greater than `2 × ReportIntervalSec` is treated as a long pause/loading/alt-tab boundary and resets the leak window instead of inserting a clamped sample; explicit session boundaries call `OnSessionBoundary()` for the same reason.

### `HealthClassifier`

Pure function: `(snapshot, memoryHistory, simPhaseMs, renderPhaseMs) → HealthReport`. No mutation. Thresholds are constants in the file — change them there if you tune.

`Classify()` returns:
- Per-metric levels (FPS, stutter, memory, managed growth) — fed to the overlay traffic light
- Overall level — worst of the four
- Bottleneck verdict (RENDER / SIM / MEMORY / BALANCED) with a one-line hint

The bottleneck logic uses **average phase ms per call** — derived from exact phase-key lookups for `UpdateSystem.GameSimulation` and `UpdateSystem.Rendering`.

### `ReportDispatcher`

Cold-path fan-out to `IReportSink[]`. Wraps each sink in its own `try/catch` so a misbehaving sink (disk full, AV scan locking the file) cannot abort delivery to the others. Failures are logged at `Warn` level via `VanillaProfilerMod.Log`. `Profiler.Report` calls `ReportDispatcher.WriteReport`; system messages route through `ReportDispatcher.WriteSystem`.

### `ProfilerSettings` and `ProfilerSettingsSnapshot`

`ProfilerSettings` is **immutable** — every field is `{ get; }`, the constructor takes one argument per field with defaults, and edits go through `.With(...)` returning a new instance. `Normalize()` returns a clamped copy without mutating; `Normalize(out bool changed)` is used at load time to migrate legacy values.

`ProfilerSettingsSnapshot` is the point-in-time view passed through hot/cold boundaries. Because `ProfilerSettings` is now immutable the snapshot just holds a reference (no clone needed). Read it via `SettingsStore.Snapshot`; runtime services receive a `Func<ProfilerSettingsSnapshot>` accessor at construction so settings reads stay explicit and consistent across a frame.

`SettingsStore` no longer exposes a mutable `Current` field. The only public read is `Snapshot`; the only writes are `SettingsStore.Apply(ProfilerSettings)` (replace wholesale) and `SettingsStore.Update(Func<ProfilerSettings, ProfilerSettings>)` (functional update). JSON persistence uses a private `SerializedSettings` DTO so the immutable shape doesn't constrain the serializer.

### `OverlayState`

Mutable UI navigation state (`ModeIndex`, `Anchor`) lifted out of `ProfilerOverlay` so drawing, persistence, and navigation each have a single home. `ProfilerOverlay` owns the instance, holds it for the session, and routes hotkey/button events to `SetMode` / `SetAnchor` / `CycleMode` / `CycleAnchor`.

### `ProfilerRecorderFactory` & `ProfilerRecorderSamples`

`MemorySampler` exposes a wide set of Unity `ProfilerRecorder` markers. Two pieces keep that path honest and allocation-free:

- `ProfilerRecorderFactory.StartByHandle(category, stat, capacity)` resolves a marker by walking `ProfilerRecorderHandle.GetAvailable()` so the lookup tolerates Unity's handle-based markers that have no typed `ProfilerCategory` constant. A missing marker returns `default`, surfaced in the snapshot as a counter-availability flag rather than a misleading zero.
- `ProfilerRecorderSamples` owns a single reusable `List<ProfilerRecorderSample>` buffer and exposes `Average(recorder)` / `SumWithCount(recorder)`. `MemorySampler` holds one instance, so per-window recorder reads do not allocate.

### `ReportTextSections`

Shared text-section builders used by both the periodic `LogFileSink` writes and the one-shot `ReportExporter`. Centralises counter-availability formatting, top-table layout, and patched-vanilla-system summaries so the two output paths stay consistent.

## Harmony patches

| Patch | Target | Purpose |
|---|---|---|
| `SystemAutoProfiler` | `SystemBase.Update` | Per-system self and inclusive main-thread cost for every system in every world |
| `UpdateSystemPatch.UpdatePhase` | `UpdateSystem.Update(SystemUpdatePhase)` | Phase timing + render frame trigger |
| `UpdateSystemPatch.UpdatePhaseWithIndex` | `UpdateSystem.Update(SystemUpdatePhase, uint, int)` | GameSimulation tick phase timing |
| `EntityCommandBufferPatch.PlaybackEntityManager` | `EntityCommandBuffer.Playback(EntityManager)` | ECB playback time, surfaced as `ECB.Playback` phase |
| `EntityCommandBufferPatch.PlaybackExclusiveTransaction` | `EntityCommandBuffer.Playback(ExclusiveEntityTransaction)` | Same, alternate overload (gated when missing on the build) |

`HarmonyConflictDetector` runs once after the first report (deferred to give other mods time to apply their patches). It enumerates `Harmony.GetAllPatchedMethods()` and lists methods patched by more than one owner only when VanillaProfiler is one of those owners. The result goes to `VanillaProfiler.log` as a one-off section.

## Threading model

The mod follows a **main-thread measurement** rule rather than scattering locks everywhere:

- **Patch surface is `IProfilerPatchSurface`, read/control surface is `IProfilerReadSurface`.** Both implemented by `Profiler`; `ProfilerHost` only exposes the interfaces, not the concrete type, so a Harmony patch cannot reach `LastSnapshot` and an overlay cannot reach `RecordSystem`. New patch-callable methods belong on `IProfilerPatchSurface`; new overlay/export/lifecycle access goes on `IProfilerReadSurface`.
- **`Profiler.RecordSystem`** is invoked from `SystemBase.Update` Postfix on the Unity main thread. It records into `MetricsAggregator` without locking.
- **Measurement state is main-thread only:**
  - `OnFrame` is called from `UpdateSystem.Update(Rendering)` Postfix — main thread by Unity contract.
  - `OnSimTick` is called from `SimTickCounterSystem.OnUpdate` — ECS GameSimulation phase, main thread.
  - `RecordPhase` is called from the per-phase Postfix — main thread.
  - `RecordSystem` is called from `SystemBase.Update` Postfix — main thread.
  - `RecordPatchedVanilla` is called from `SystemBase.Update` Postfix when `SystemReplacementDetector.IsPatched(type)` returns true — main thread.
  - `ReportScheduler`, `MemorySampler`, `MemoryHistory`, `FpsSparkline` are touched only from `OnFrame`/`Report` and lifecycle boundary resets.
  - `LastSnapshot` and `LastHealth` are written by `Report` and read by `ProfilerOverlay.OnGUI` — both main thread.
  - `OverlayState` (mode index, anchor) is written and read by `ProfilerOverlay` only — main thread.
- **Disposal** is guarded by a `volatile bool m_Disposed`. Every public entry point checks it first so a stale Harmony callback landing after `Mod.OnDispose` no-ops cleanly.
- **Log output has its own lock.** `LogFileSink` serializes file IO because system messages can be routed from defensive off-thread diagnostics.
- **`ReportDispatcher`** is invoked only from `Profiler.Report` and `Profiler.WriteSystem`, both main-thread; per-sink `try/catch` makes one bad sink isolated rather than fatal.
- **`ProfilerHost.TryGetReadSurface()` / `TryGetPatchSurface()`** each do a single `Volatile.Read`. Callers must capture the result into a local variable before use. `ProfilerHost.IsAvailable` is a single-shot existence gate (used by `CityContextSystem` to skip work when no profiler is alive); never pair it with a subsequent `TryGet*Surface()` in the same control flow — that's a TOCTOU race.

## What this mod intentionally does NOT do

- **No persistence beyond settings.** Nothing serialised in saves. Sessions are independent.
- **No structural ECS changes.** Only read-only `EntityQuery.CalculateEntityCount()`. Patches use prefix/postfix, plus a narrow finalizer only where needed to close `ProfilerMarker` scopes on thrown `SystemBase.Update()`.
- **No UI Toolkit / Coherent UI.** IMGUI is sufficient and avoids the layering complexity of CS2's binding system.
- **No quality recommendations beyond bottleneck hint.** "Render-bound, lower graphics" is generic enough; finer advice would be guesswork.
- **No per-job worker-thread profiling.** Burst-compiled jobs run as native code outside `SystemBase.Update()`; their per-system cost cannot be observed from a mod in a release build. Unity Profiler with engine-level instrumentation is the only tool that can. We surface aggregate worker time when the `ProfilerCategory.Internal` markers are exposed (usually stripped in release — silently zero when missing).

## See also

- [../USER_GUIDE.md](../USER_GUIDE.md) — Player-facing reference for the overlay
- [DEVELOPER_NOTES.md](DEVELOPER_NOTES.md) — How to add a metric or extend the overlay
- [../CHANGELOG.md](../CHANGELOG.md) — User-facing changes per release
