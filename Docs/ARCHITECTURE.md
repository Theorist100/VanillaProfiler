# VanillaProfiler — Architecture

## Goals

- **Zero gameplay impact.** No entities created, no game state mutated. All measurement happens through Harmony patches and read-only ECS queries.
- **Low overhead.** ~300 Stopwatch start/stop pairs per sim tick + four small queries every 5 seconds. Negligible compared to the systems being measured.
- **Two audiences.** Status / Diagnosis / Tips modes for normal players (traffic-light health, recommendations, leak detection, support-file export). Details / Engine modes + `VanillaProfiler.log` for mod authors and power users (per-system self main-thread cost, sync-point flagging, ECB.Playback timing, memory deltas, engine counters).

## Scope of measurement

Top per-system numbers (`SystemAutoProfiler` Stopwatch around `SystemBase.Update`) capture **self/exclusive main-thread cost only** — scheduling overhead, sync points (`Dependency.Complete`, `CompleteDependencyBeforeRO`), structural changes, `EntityCommandBuffer.Playback`, and any synchronous main-thread work, with nested `SystemBase.Update` calls subtracted from the parent system. Inclusive elapsed Update time is retained in `PhaseData.InclusiveTicks` for diagnostics such as patched-vanilla rows. Burst-compiled jobs scheduled to worker threads run as native code outside `SystemBase.Update()` and cannot be instrumented from a mod in a release build. Frame time, GPU/CPU thread time (Unity ProfilerRecorder), all memory metrics and the sync-point threshold flag are accurate. For per-job analysis attach Unity Profiler.

## Module layout

```
VanillaProfiler (assembly)
├── Mod.cs                      Entry point — loads settings, starts profiler, registers patches
├── Profiler.cs                 Main-thread coordinator. Implements IProfilerPatchSurface + IProfilerReadSurface; delegates timing to ReportScheduler and sink fan-out to ReportDispatcher.
├── ProfilerSessionState.cs     Token-based lifecycle/read-state owner; returns explicit transitions for Profiler to apply.
├── SessionBoundary.cs          Typed active-session reset reasons used by Profiler.ApplySessionBoundary.
├── IProfilerSurfaces.cs        Two interfaces over the same Profiler instance:
│                               • IProfilerPatchSurface — narrow surface for Harmony patches and ECS counter systems
│                                 (OnFrame / OnSimTick / RecordSystem / RecordPatchedVanilla / RecordPhase)
│                               • IProfilerReadSurface — overlay/export/lifecycle/log surface (LastSnapshot,
│                                 LastHealth, LatestMemoryHistory, GraphicsSettings state, LifecycleState,
│                                 SpikeScreenshotsCaptured, FpsSparklineText, BuildRecommendations,
│                                 SetSpikeScreenshotsEnabled, InvalidateRecommendationsCache, ForceReport,
│                                 SetGameLoaded, BeginLoading, LogInfo/Warn/Error)
├── ProfilerHost.cs             Static handle — Volatile.Read returning IProfilerPatchSurface or IProfilerReadSurface; the concrete Profiler type is not exposed.
├── ProfilerOverlay.cs          IMGUI presentation layer. No measurement logic. Holds OverlayState for navigation.
├── ReportScheduler.cs          Owns frame-to-frame timing and report cadence; answers "is a report due now?".
├── SystemAutoProfiler.cs       Harmony patch — measures every SystemBase.Update() call (self + inclusive main-thread cost) and asks IProfilerPatchSurface whether the current type is patched.
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
                    │  Patch entries (IProfilerPatchSurface):    │
                    │   • IsVanillaSystemPatched                 │
                    │   • OnFrame, OnSimTick                     │
                    │   • RecordSystem, RecordPatchedVanilla     │
                    │   • RecordPhase                            │
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

`IProfilerReadSurface` is a DTO/command boundary. It does not return live mutable services such as `MemoryHistory`, `FpsSparkline`, `SpikeScreenshot`, `GraphicsSettingsProbe`, or `RecommendationEngine`; consumers receive snapshots (`LatestMemoryHistory`, `GraphicsSettings`) or invoke narrow commands (`FpsSparklineText`, `BuildRecommendations`, `SetSpikeScreenshotsEnabled`, `InvalidateRecommendationsCache`).

`ProfilerSessionState` is the single lifecycle/read-state source. It owns immutable `ProfilerSessionToken` values (`SessionId`, `LoadId`, `LifecycleState`) and returns `ProfilerLifecycleTransition` objects for initialization, load start, load completion and publish. `Mod.OnLoad` calls `Profiler.InitializeFromCurrentMode(GameManager.instance.gameMode)` immediately after registering the profiler, so hot-reloading the mod while a city is already active enters the same settling path as a normal load-complete callback. `Profiler` owns the effects for real session boundaries through `ApplySessionBoundary(SessionBoundary)`: metrics, public snapshots, city context, graphics/replacement caches, spike screenshot state, memory history, recorder windows and scheduler reset together. `Dispose()` is a separate shutdown path; it shuts down sinks and disposes `MemorySampler`, but it does not call `ApplySessionBoundary`, `MemorySampler.ResetSession()` or create a new report window.

`ReportWindowContext` is the measurement/report boundary. Each window captures a `WindowId`, `ProfilerSettingsSnapshot`, `SyncPointThresholdTicks`, and `SystemReplacementDetector.ReplacementSnapshot`. Hot-path routing reads the active context through `Profiler.CurrentWindowContext`; `MetricsAggregator.StartWindow(context)` captures the same context and copies it onto the drained `MetricsSample`. `Profiler.Report()` drains and reports the old/current window, attaches replacements from `metrics.WindowContext`, then rotates to a freshly scanned context in `BeginNextWindow()` from a `finally` block. This prevents old metrics from being labelled with settings or Harmony state scanned after the window ended.

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
- Resolve `Type` → `(name, isVanilla, modName)` once via `ModAttribution.ResolveIdentity` plus output-edge formatting (cached)
- For vanilla types, query `IProfilerPatchSurface.IsVanillaSystemPatched(Type)` (O(1) lookup on the active `ReportWindowContext.Replacements`, not cached on `SystemInfo` because mod-options screens can flip Harmony patches at runtime and routing must stay tied to the current report window)
- Route the elapsed delta:
  - **patched vanilla** → `Profiler.RecordPatchedVanilla` (independent of `ProfileVanillaSystems`; reports total/inclusive Update ms because the elapsed time blends mod prefix + vanilla original)
  - **plain vanilla** → `Profiler.RecordSystem` if `ProfileVanillaSystems = true`, else dropped
  - **mod system** → `Profiler.RecordSystem`

`ModAttribution` decides ownership as structured identity, not a display string:

1. Trusted game/Unity assembly origin plus a trusted namespace segment marks a vanilla ECS owner.
2. Reflection over `IMod` implementers is allowed only on startup/export paths and can upgrade low-confidence cache entries.
3. Hot-path fallback keeps assembly-name or unknown identity with confidence, so a helper assembly is not mistaken for a confirmed mod owner.

The result is cached per type and assembly. Low-confidence cache entries can be upgraded, but trusted/profiler identities are not downgraded.

### `SystemReplacementDetector`

Walks `World.All`, looking for vanilla systems whose `OnUpdate` has a foreign Harmony patch. Prefixes, postfixes, transpilers and finalizers all count as replacement evidence. It deduplicates by system `Type` because the same system can appear in multiple worlds, then returns a `ReplacementSnapshot`.

- **`ReplacementSnapshot.IsPatched(Type)`** — O(1) dictionary probe used by `Profiler.IsVanillaSystemPatched`, which is exposed on the patch surface for `SystemAutoProfiler`.
- **`ReplacementSnapshot.Replacements`** — sorted, deduplicated rows consumed by `Profiler.Report` to build `OverlaySnapshot.ReplacedVanillaSystems`. The rows are matched with `MetricsSample.PatchedVanillaSystems` by full type name, so each row carries the vanilla system name, structured patch-owner evidence, and total Update ms.

`Profiler` owns the active replacement snapshot as part of the active `ReportWindowContext`. Lifecycle boundaries discard the current partial window and immediately publish a fresh context. Normal report cadence drains the current metrics with their original context, writes the report, then scans and publishes the next context. `Profiler.IsVanillaSystemPatched(Type)` therefore routes a system using the same replacement snapshot that will later be attached to that window's report.

Why a separate bucket: Harmony prefixes can wrap or skip the original `OnUpdate`, and postfixes, transpilers, or finalizers can still add work or change behaviour. The elapsed time inside the patched `SystemBase.Update` is a blend of patching-mod work and the vanilla original. There is no Harmony hook boundary that gives VanillaProfiler a reliable split. Surfacing the total `Update` ms is honest — the split is what's not measurable, not the cost itself.

### `MemoryHistory`

Sliding window of 12 samples (≈ 60 s at 5 s reporting). Leak detection requires a full 5-sample window so a single one-off allocation doesn't trip the alarm. The metric used is the **median** delta per report — robust against single outliers.

`MemoryHistory` receives a `Func<ProfilerSettingsSnapshot>` accessor at construction; `WindowSeconds` is computed against the snapshot's `ReportIntervalSec` so changing the report cadence at runtime keeps the displayed text honest. A report elapsed time greater than `2 × ReportIntervalSec` is treated as a long pause/loading/alt-tab boundary and resets the leak window instead of inserting a clamped sample; explicit session boundaries call `OnSessionBoundary()` for the same reason.

### `HealthClassifier`

Pure function: `(snapshot, memoryHistory, simPhaseMs, renderPhaseMs) → HealthReport`. No mutation. Thresholds are constants in the file — change them there if you tune.

`Classify()` returns:
- Per-metric levels (FPS, stutter, memory, managed growth) — fed to the overlay traffic light
- Overall level — worst of the four
- Bottleneck verdict (`GpuBound`, `CpuRenderBound`, `SimBound`, `MemoryBound`, `Balanced`, `Unknown`) with a one-line hint

The bottleneck logic uses **average phase ms per call** — derived from exact phase-key lookups for `UpdateSystem.GameSimulation` and `UpdateSystem.Rendering`.

### `ReportDispatcher`

Cold-path fan-out to `IReportSink[]`. Wraps each sink in its own `try/catch` so a misbehaving sink (disk full, AV scan locking the file) cannot abort delivery to the others. Failures are logged at `Warn` level via `VanillaProfilerMod.Log`. `Profiler.Report` calls `ReportDispatcher.WriteReport`; system messages route through `ReportDispatcher.WriteSystem`.

### `ProfilerSettings` and `ProfilerSettingsSnapshot`

`ProfilerSettings` is **immutable** — every field is `{ get; }`, the constructor takes one argument per field with defaults, and edits go through `.With(...)` returning a new instance. `Normalize()` returns a clamped copy without mutating; `Normalize(out bool changed)` is used at load time to migrate legacy values.

`ProfilerSettingsSnapshot` is the point-in-time view passed through hot/cold boundaries. Because `ProfilerSettings` is now immutable the snapshot just holds a reference (no clone needed). Read it via `SettingsStore.Snapshot`; runtime services receive a `Func<ProfilerSettingsSnapshot>` accessor at construction so settings reads stay explicit and consistent across a frame.

`SettingsStore` no longer exposes a mutable `Current` field. The only public read is `Snapshot`; the only writes are `SettingsStore.Apply(ProfilerSettings)` (replace wholesale) and `SettingsStore.Update(Func<ProfilerSettings, ProfilerSettings>)` (functional update). JSON persistence uses a private `SerializedSettings` DTO so the immutable shape doesn't constrain the serializer.

### `OverlayState`

Mutable UI navigation state (`ModeId`, `ModeIndex`, `Anchor`) lifted out of `ProfilerOverlay` so drawing, persistence, and navigation each have a single home. Persisted default mode values are interpreted through `OverlayModeCatalog` as stable `OverlayModeId` values, while the current array index is only a draw-order lookup. `ProfilerOverlay` owns the instance, holds it for the session, and routes semantic `OverlayCommand` values plus button clicks to `SetMode` / `SetAnchor` / `CycleMode` / `CycleAnchor`.

The main overlay header is drawn outside the scroll view, so long Details/Engine/Recommendations bodies scroll under a stable title/drag band. Reset Defaults writes `PanelX = -1` / `PanelY = -1`; `ProfilerOverlay.ApplyLiveSettings()` reloads panel positioning so the next layout returns to the selected anchor preset instead of preserving a stale manual drag.

### `ProfilerRecorderFactory` & `ProfilerRecorderSamples`

`MemorySampler` exposes a wide set of Unity `ProfilerRecorder` markers. Two pieces keep that path honest and allocation-free:

- `ProfilerRecorderFactory.StartByHandle(category, stat, capacity)` resolves a marker by walking `ProfilerRecorderHandle.GetAvailable()` so the lookup tolerates Unity's handle-based markers that have no typed `ProfilerCategory` constant. A missing marker returns `default`, surfaced in the snapshot as a counter-availability flag rather than a misleading zero.
- `ProfilerRecorderSamples` owns a single reusable `List<ProfilerRecorderSample>` buffer and exposes `Average(recorder)` / `SumWithCount(recorder)`. `MemorySampler` holds one instance, so per-window recorder reads do not allocate.

### `ReportTextSections`

Shared text-section builders used by both the periodic `Output/LogFileSink` writes and the one-shot `Output/ReportExporter`. Centralises counter-availability formatting, top-table layout, and patched-vanilla-system summaries so the two output paths stay consistent. Report and support-bundle writers live in `Output`; diagnostics code supplies analysis data but does not own file publication or logging policy.

## Harmony patches

| Patch | Target | Purpose |
|---|---|---|
| `SystemAutoProfiler` | `SystemBase.Update` | Per-system self and inclusive main-thread cost for every system in every world |
| `UpdateSystemPatch.UpdatePhase` | `UpdateSystem.Update(SystemUpdatePhase)` | Phase timing + render frame trigger |
| `UpdateSystemPatch.UpdatePhaseWithIndex` | `UpdateSystem.Update(SystemUpdatePhase, uint, int)` | GameSimulation tick phase timing |
| `EntityCommandBufferPatch.PlaybackEntityManager` | `EntityCommandBuffer.Playback(EntityManager)` | ECB playback time, surfaced as `ECB.Playback` phase |
| `EntityCommandBufferPatch.PlaybackExclusiveTransaction` | `EntityCommandBuffer.Playback(ExclusiveEntityTransaction)` | Same, alternate overload (gated when missing on the build) |

`HarmonyConflictDetector` computes a stable signature from `Harmony.GetAllPatchedMethods()` and owner ids each report cycle. It logs only when that signature changes, and the visible conflict list is limited to multi-owner patches where VanillaProfiler is one of the owners. The result goes to `VanillaProfiler.log`.

## Threading model

The mod follows a **main-thread measurement** rule rather than scattering locks everywhere:

- **Patch surface is `IProfilerPatchSurface`, read/control surface is `IProfilerReadSurface`.** Both implemented by `Profiler`; `ProfilerHost` only exposes the interfaces, not the concrete type, so a Harmony patch cannot reach `LastSnapshot` and an overlay cannot reach `RecordSystem`. New patch-callable methods belong on `IProfilerPatchSurface`; new overlay/export/lifecycle access goes on `IProfilerReadSurface` as DTOs or narrow commands, not live mutable helper instances.
- **`Profiler.RecordSystem`** is invoked from `SystemBase.Update` Postfix on the Unity main thread. It records into `MetricsAggregator` without locking.
- **Measurement state is main-thread only:**
  - `OnFrame` is called from `UpdateSystem.Update(Rendering)` Postfix — main thread by Unity contract.
  - `OnSimTick` is called from `SimTickCounterSystem.OnUpdate` — ECS GameSimulation phase, main thread.
  - `RecordPhase` is called from the per-phase Postfix — main thread.
  - `RecordSystem` is called from `SystemBase.Update` Postfix — main thread.
  - `RecordPatchedVanilla` is called from `SystemBase.Update` Postfix when `IProfilerPatchSurface.IsVanillaSystemPatched(type)` returns true — main thread.
  - `ReportScheduler`, `MemorySampler`, `MemoryHistory`, `FpsSparkline` are touched only from `OnFrame`/`Report` and lifecycle boundary resets.
  - `LastSnapshot` and `LastHealth` are written by `Report` and read by `ProfilerOverlay.OnGUI` — both main thread.
  - `OverlayState` (stable mode id, draw index, anchor) is written and read by `ProfilerOverlay` only — main thread.
- **Disposal** is guarded by a `volatile bool m_Disposed`. Every public entry point checks it first so a stale Harmony callback landing after `Mod.OnDispose` no-ops cleanly.
- **Log output has its own lock.** `LogFileSink` serializes file IO because system messages can be routed from defensive off-thread diagnostics.
- **`ReportDispatcher`** is invoked only from `Profiler.Report` and `Profiler.WriteSystem`, both main-thread; per-sink `try/catch` makes one bad sink isolated rather than fatal.
- **`ProfilerHost.TryGetReadSurface()` / `TryGetPatchSurface()`** each do a single `Volatile.Read`. Callers must capture the result into a local variable before use, then make lifecycle/read-state decisions on that captured surface. `CityContextSystem` follows this pattern before running entity-count queries.

## What this mod intentionally does NOT do

- **No persistence beyond settings.** Nothing serialised in saves. Sessions are independent.
- **No structural ECS changes.** Only read-only `EntityQuery.CalculateEntityCount()`. Patches use prefix/postfix, plus a narrow finalizer only where needed to close `ProfilerMarker` scopes on thrown `SystemBase.Update()`.
- **No UI Toolkit / Coherent UI.** IMGUI is sufficient and avoids the layering complexity of CS2's binding system.
- **Recommendations are bounded by measured evidence.** Tips may suggest graphics or isolation steps when counters/settings support them, but the mod does not guess at unmeasured engine internals or per-job worker costs.
- **No per-job worker-thread profiling.** Burst-compiled jobs run as native code outside `SystemBase.Update()`; their per-system cost cannot be observed from a mod in a release build. Unity Profiler with engine-level instrumentation is the only tool that can. We surface aggregate worker time when the `ProfilerCategory.Internal` markers are exposed (usually stripped in release — silently zero when missing).

## See also

- [../USER_GUIDE.md](../USER_GUIDE.md) — Player-facing reference for the overlay
- [DEVELOPER_NOTES.md](DEVELOPER_NOTES.md) — How to add a metric or extend the overlay
- [../CHANGELOG.md](../CHANGELOG.md) — User-facing changes per release
