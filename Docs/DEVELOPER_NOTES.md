# VanillaProfiler — Developer Notes

Practical notes for extending the profiler. Read [ARCHITECTURE.md](ARCHITECTURE.md) first for the data flow.

## Build & deploy

```powershell
dotnet build VanillaProfiler.csproj --no-incremental
```

Output deploys to `%LOCALAPPDATA%Low\Colossal Order\Cities Skylines II\Mods\VanillaProfiler\` automatically (csproj `ModsFolder` property). Restart the game to pick up changes — there is no hot-reload.

## Adding a new metric

The flow is always: collect → classify → render. Pick where you plug in:

### Per-frame metric (single-threaded)
Hook into `Profiler.OnFrame` (called from `UpdateSystemPatch` Rendering phase, main thread). Add an instance accumulator field on `Profiler` directly — no lock needed because `OnFrame` is main-thread only. Drain and reset it in `Profiler.Report()`. Add a field to `OverlaySnapshot`, fill it in `ReportBuilder.Build`, render it in an `IOverlayMode` implementation.

### Per-system metric (multi-threaded — only worker-thread path)
Add a method on `MetricsAggregator` and guard the new dictionary with the existing instance lock. Expose it via a delegating `Profiler.RecordX(...)` method that worker-thread Harmony patches can call. **Do NOT** add new locked fields directly on `Profiler` — keep all multi-threaded state inside `MetricsAggregator`.

### Per-report metric
If you only need a value once per report (e.g. a rare query), add it directly inside `Profiler.Report()` after `MetricsAggregator.Drain()`. Main-thread context — touch instance fields freely.

### Health / bottleneck rule
Add a constant threshold in `HealthClassifier`. Add a field to `HealthReport`. Update `Classify()`. Render the new field in the appropriate `IOverlayMode` implementation (`StatusMode` for player-facing, `DetailsMode` for power users).

## Adding an ECS-driven metric

Pattern (see `CityContextSystem`):

1. Create a `partial class FooSystem : GameSystemBase` in the root namespace
2. Build queries in `OnCreate`, never in `OnUpdate`
3. Throttle with `UnityEngine.Time.realtimeSinceStartup` — every tick is too noisy
4. Push into a static class in `Diagnostics/` (e.g. `FooContext`)
5. Register in `Mod.OnLoad`: `updateSystem.UpdateAt<FooSystem>(SystemUpdatePhase.GameSimulation)`
6. Read from overlay or exporter

Use `UnityEngine.Time.realtimeSinceStartup` explicitly — `Time` alone collides with `ComponentSystemBase.Time` (which is deprecated in this Unity Entities version).

## Visual style

The overlay uses the **Classic Gold** palette from `CivicSurvival/UI/src/themes/classicGold.ts`. If you change colours, mirror the source theme; consistency between in-game UI and the profiler matters.

| Element | Colour | Hex |
|---|---|---|
| Background | dark blue-purple, 85 % opacity | `rgba(26, 26, 46, 0.85)` |
| Border / accent | gold | `#ffd700` |
| Text primary | warm white | `#f0f0e8` |
| Text secondary | dim warm grey | `#a0a090` |
| Hint | muted brown-grey | `#908070` |
| Good | green | `#4ade80` |
| Ok / warning | orange | `#ffaa00` |
| Poor / error | red | `#ff4444` |

Signature touches: 1 px gold border on all four sides, 3 px gold accent strip on the left edge, ▸ in section headers, ─ separators after the title row.

## IMGUI gotchas

- **No `using UnityEngine.JsonUtility` available by default.** Add `<Reference Include="UnityEngine.JSONSerializeModule">` to csproj.
- **`ScreenCapture` lives in `UnityEngine.ScreenCaptureModule`.** Same — add the reference explicitly.
- **`Queue<T>` resolves ambiguously** between `System` and `mscorlib` in the CS2 modding toolchain. Use `List<T>` with manual `RemoveAt(0)` instead. (See `MemoryHistory.cs`.)
- **`Time` in a `SystemBase`** triggers a deprecation warning AND collides with `UnityEngine.Time`. Use `UnityEngine.Time.realtimeSinceStartup` fully qualified.
- **`Resolution.refreshRate` is obsolete** — use `Resolution.refreshRateRatio.value`.
- **GUI styles must be initialised inside `OnGUI`** — `Awake/Start` is too early; `GUI.skin` isn't ready.

## Harmony patches — what's safe

- **Pre/Postfix on `SystemBase.Update`** — covers the entire system update loop. `SystemBase.OnUpdate` is abstract and can't be patched directly. `ComponentSystemBase.Update` is also abstract — don't try.
- **Always wrap patch bodies in `try { } catch { }`.** A single throw inside a postfix can crash the whole game.
- **Don't do structural ECS changes from a patch.** No `EntityManager.AddComponent`, no `SetSharedComponent`, no `RemoveComponent` from inside a patch on a rendering or simulation system. Reads only. (See `Mods/CivicSurvival` history for the crashes that followed when this rule was broken.)
- **Don't allocate in the hot path.** No `string.Format`, no LINQ, no `new()` in a per-frame patch. Cache everything in `OnCreate` or via static dictionaries.

## Thread safety

The mod has **one and only one multi-threaded surface**: `Profiler.RecordSystem`, called from the `SystemBase.Update` Postfix patch which Unity Entities can schedule on any worker. It delegates to `MetricsAggregator`, whose instance lock is the single sync primitive.

Everything else is **main-thread only**:
- `OnFrame` is called from `UpdateSystem.Update(Rendering)` Postfix — main thread (rendering always is)
- `OnSimTick` from `SimTickCounterSystem.OnUpdate` — ECS sim phase, main thread
- `RecordPhase` from per-phase Postfix — main thread
- `MemorySampler`, `MemoryHistory`, `FpsSparkline`, `CityContext` — touched only from `OnFrame`/`Report`/ECS update on the main thread
- `LastSnapshot` / `LastHealth` written by `Report` and read by overlay — both main thread

Lifecycle guard: `Profiler` carries a `volatile bool m_Disposed` checked at the top of every public entry point. After `Mod.OnDispose` runs `UnpatchAll → Unregister → Dispose`, any in-flight worker-thread Postfix that captured a stale reference via `ProfilerHost.TryGet()` will no-op.

`ProfilerHost.TryGet()` returns the current instance via `Volatile.Read`. **Never** combine `IsAvailable` with a separate `Current` access — that's TOCTOU. Capture into a local: `var p = ProfilerHost.TryGet(); p?.OnSimTick();`.

## Testing locally

There's no automated test harness. Validation flow:

1. **Compile clean.** `dotnet build --no-incremental` must show 0 errors, 0 warnings.
2. **Load a small save.** Overlay should appear in Status mode within 5 seconds. All four indicators should be green after the first report.
3. **Cycle modes (Ctrl+F9).** Status, Diagnosis, Details, and Hidden should render without overlap. Sparkline fills in within 60 s on Details.
4. **Force a report (Ctrl+F10).** A new section should appear in `VanillaProfiler.log` immediately.
5. **Export (Ctrl+F11).** A toast should appear; the file should be in `Reports/`. Open it and verify all sections.
6. **Open settings (Ctrl+F8).** Edit the report interval to 1, click Apply & Save. Subsequent reports in `VanillaProfiler.log` should be every second.
7. **Trigger a spike.** Run a heavy save while Spike screenshots are enabled (Ctrl+F7). At least one screenshot should land in `Logs/spikes/` after a frame >100 ms.
8. **Multi-mod scenario.** Load with 3+ mods active. The `Top mods (main-thread ms in sample)` block in Details mode should show real assembly names, not "Unknown".
9. **Return to main menu.** `CityContext` counters should reset (no stale "80k pop" in Details after going back to menu).

## Known limitations

- **First report is delayed.** `MemoryHistory` needs 5 samples (≈ 25 s) before it can declare a leak. Health shows OK during this window even if memory is rising.
- **Bottleneck classifier averages over 5 s.** A single bad frame won't change the verdict. This is by design — flapping verdicts confuse players.
- **Mod attribution falls back to assembly name** if the mod's `IMod` is in a separate assembly from its systems. Most mods have everything in one DLL, so this is rare.
- **Settings panel uses fixed-size segmented controls.** Long mode names get cut off. If you add a fifth mode, widen the segmented buttons or rotate to a dropdown.

## Where to look first when debugging

| Symptom | First check |
|---|---|
| No log file | `VanillaProfilerMod.Log` warning at mod load. Check Modding.log. |
| Empty system tables | `SystemAutoProfiler.Prepare` returned false — `SystemBase.Update` not found. |
| Overlay invisible | Mode is `Hidden` (cycle with Ctrl+F9), or `OnGUI` threw before `EnsureInitialized`. |
| All systems labeled "Unknown" | `ModAttribution.GetTypes` threw — IMod scan failed. Check `ReflectionTypeLoadException`. |
| Sparkline stays empty | `FpsSparkline.OnFrame` not getting called — verify `Rendering` phase patch active. |
| Spike screenshots missing | Threshold too high, or `Logs/spikes/` not writable, or 30 s cooldown still active. |

## See also

- [../USER_GUIDE.md](../USER_GUIDE.md) — for player-facing context
- [ARCHITECTURE.md](ARCHITECTURE.md) — for data flow and contracts
