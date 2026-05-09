# VanillaProfiler

Diagnostic mod for Cities: Skylines II. Two audiences in one drop-in install — a traffic-light health overlay for normal players, and a main-thread / sync-point / memory analyzer for mod authors and power users.

## For players

- Status screen — Good / Warning / Problem with the likely cause and one-line advice
- Diagnosis screen — plain-language explanation when something is wrong
- Tips screen — prioritized settings and troubleshooting recommendations
- Engine screen — raw Unity counters: per-thread frame timing (Main / Render / GPU / PresentWait), DrawCalls / SetPass / Tris / Verts / shadow casters, GPU memory split (buffers vs render targets), GC stalls, process RSS
- Memory leak detector — flags sustained managed-heap growth across the recent window
- Bottleneck verdict (RenderBound / SimBound / MemoryBound / Balanced) with concrete next step
- Auto-screenshots on frame spikes
- Ctrl+F11 export — one-click support report plus bounded support bundle (system info, mods, stats, settings, log tail) to send to mod authors

## For mod authors and power users

- Details overlay — per-system self main-thread cost (vanilla vs each mod), FPS sparkline, top mods/systems
- Engine overlay — raw Unity engine counters with PresentWait for honest GPU-bound detection (so the "98 % GPU" trap doesn't fool the bottleneck verdict). Counters that the build does not expose render as `n/a` instead of a misleading zero.
- Sync-point flagging — Update() calls above SyncPointThresholdMs (default 0.5 ms) tagged `[likely sync point]` in the log; per-system suspect-call counter
- ECB.Playback timing — `EntityCommandBuffer.Playback` Harmony hook surfaces structural-change cost separately from system Update time
- Full PERF report every 5 s in `VanillaProfiler.log` — phase tables, top vanilla and mod systems, ECB cost, memory deltas, render counts, GC stall sums
- HarmonyConflictDetector — lists multi-owner Harmony patches that involve VanillaProfiler
- SystemReplacementDetector — surfaces vanilla `OnUpdate` methods hooked by a foreign Harmony prefix and reports total `Update` ms per cycle. The elapsed time blends the patching mod's prefix with the vanilla original (Harmony does not let us split them), but the total is honest and shown unconditionally — independent of `Profile vanilla systems`.

## Scope of measurement

Top per-system numbers reflect **self main-thread cost only** — scheduling overhead, sync points (`Dependency.Complete`, `CompleteDependencyBeforeRO`), structural changes (`EntityManager.*`), `EntityCommandBuffer.Playback`, and any synchronous main-thread work, with nested `SystemBase.Update` calls subtracted from the parent. Inclusive/total Update time is still kept for patched-vanilla diagnostics. Job execution on worker threads is **not** captured: Burst-compiled jobs run as native code outside `SystemBase.Update()` and cannot be instrumented from a mod. A well-architected DOTS system that schedules cleanly to workers will show ~0.01 ms here while its jobs may cost 5–20 ms on workers — those appear on Unity Profiler's worker timeline.

Frame time, GPU frame time, CPU main/render thread time (via Unity ProfilerRecorder), all memory metrics, frame spike detection and sync-point flagging are accurate. For per-job analysis attach **Unity Profiler** to the running game.

## Documentation

| Document | Audience | Purpose |
|---|---|---|
| [USER_GUIDE.md](USER_GUIDE.md) | Players | Hotkeys, overlay modes, how to read the report, how to share it |
| [Docs/ARCHITECTURE.md](Docs/ARCHITECTURE.md) | Developers | Code layout, data flow, key classes |
| [Docs/DEVELOPER_NOTES.md](Docs/DEVELOPER_NOTES.md) | Contributors | How to add new metrics, gotchas, build tips |

## Key files

```
.
├── Mod.cs                              IMod entrypoint, Harmony patches, system registration
├── Profiler.cs                         Instance facade — implements IProfilerPatchSurface + IProfilerReadSurface, owns aggregator, sampler, builder, sinks
├── IProfilerSurfaces.cs                Two interfaces: IProfilerPatchSurface (Harmony/ECS hot path) and IProfilerReadSurface (overlay/export/lifecycle/log)
├── ProfilerHost.cs                     Static handle — exposes the live profiler only as IProfilerPatchSurface or IProfilerReadSurface (Volatile.Read)
├── ProfilerLifecycleState.cs           Lifecycle enum: NoCity / LoadingCity / Settling / Active
├── ProfilerOverlay.cs                  MonoBehaviour shell — picks an IOverlayMode, draws panel
├── ReportScheduler.cs                  Owns frame-to-frame timing and report cadence
├── SystemAutoProfiler.cs               Harmony patch on SystemBase.Update — covers ~300 systems
├── UpdateSystemPatch.cs                Phase timing (Pre/PostSimulation, Rendering, etc.)
├── EntityCommandBufferPatch.cs         Harmony patch on EntityCommandBuffer.Playback (with finalizer for thrown playbacks)
├── SimTickCounterSystem.cs             Sim tick counter (1 call per game tick)
├── CityContextSystem.cs                Citizen/vehicle/building counts, refreshed every 5 s
├── MainThreadGuard.cs                  Asserts main-thread invariants on hot-path entries
├── ModLog.cs                           Buffered logger; flushes into VanillaProfiler.log
├── Properties/
│   ├── PublishConfiguration.xml        Paradox Mods metadata (description, hotkeys, paths)
│   └── thumbnail.png                   512×512 mod page artwork
├── Aggregation/
│   ├── MetricsAggregator.cs            Single-threaded accumulator with borrowed dictionaries
│   ├── MetricsLease.cs                 Disposable owner for drained buffers (Recycle on dispose)
│   ├── MetricsSample.cs                Drained data passed to ReportBuilder
│   ├── MemorySampler.cs                Managed/native memory + GPU breakdown + frame timing + GC stalls
│   ├── MemorySample.cs                 Memory snapshot model
│   ├── PhaseData.cs                    Per-key accumulator (total/max/calls)
│   ├── ReportBuilder.cs                Pure (sample + memory + history) → snapshot + health
│   ├── OverlaySnapshot.cs              Immutable view exposed to overlay/exporter (carries counter-availability flags)
│   ├── ProfilerRecorderFactory.cs      Resolves Unity profiler markers by category+stat name via ProfilerRecorderHandle
│   ├── ProfilerRecorderSamples.cs      Reusable sample buffer so recorder reads do not allocate per report
│   └── MarkerEnumerator.cs             One-shot dump of available ProfilerRecorder markers at startup
├── Output/
│   ├── IReportSink.cs                  Interface for reporting destinations
│   ├── LogFileSink.cs                  Writes VanillaProfiler.log
│   ├── ReportDispatcher.cs             Cold-path fan-out to sinks with per-sink failure isolation
│   └── AtomicFileWriter.cs             Temp-file + rename helper for crash-safe writes
├── Overlay/
│   ├── IOverlayMode.cs                 Mode contract — adding a mode is purely additive (OCP)
│   ├── OverlayTheme.cs                 Classic Gold palette + cached GUIStyles
│   ├── OverlayPanel.cs                 DrawFrame / DrawSeparator / DrawSection / DrawLine
│   ├── OverlayFormat.cs                Pure formatting helpers
│   ├── OverlayInputHandler.cs          Ctrl+F-key polling → semantic events
│   ├── OverlayBadges.cs                Hidden / Standby / Settling badges (small fixed pills)
│   ├── OverlayState.cs                 UI navigation state (mode index, anchor) separated from drawing
│   ├── DrawContext.cs                  Cursor + theme bundle passed to mode renderers
│   ├── Toast.cs                        Bottom-of-screen status messages
│   ├── Anchor.cs                       Screen-corner enum + Cycle/ShortName extensions
│   ├── PanelLayout.cs                  Stateless positioning helpers (scale, anchor rect, clamp)
│   ├── PanelPositionController.cs      Anchor / manual drag / persistence for main panel
│   ├── MainPanelButtons.cs             Bottom button block — mode tabs + action row + hint
│   ├── SettingsPanel.cs                Ctrl+F8 panel using draft-then-apply pattern
│   ├── SettingsWidgets.cs              Stateless DrawTextField / DrawSegmented helpers
│   ├── SettingsValidation.cs           Bounds and sanity checks for the draft before apply
│   ├── SettingsDraft.cs                Mutable form state — absorbs IMGUI edits before Apply rebuilds an immutable ProfilerSettings
│   ├── SettingsDirtyState.cs           Per-field dirty flags; merges draft into the live ProfilerSettings via .With(...)
│   └── Modes/
│       ├── HiddenMode.cs               No-op mode
│       ├── StatusMode.cs               Player-facing traffic light + likely cause
│       ├── DiagnosisMode.cs            Plain-language explanation when Status is not Good
│       ├── RecommendationsMode.cs      Actionable Tips screen
│       ├── DetailsMode.cs              Mod-author view: top mods, sparkline, system tables
│       └── EngineMode.cs               Raw Unity engine counters (frame / render / GPU mem / GC)
└── Diagnostics/
    ├── ModAttribution.cs               Type → ModName via IMod scan + assembly fallback
    ├── HealthClassifier.cs             GOOD/OK/POOR per metric + bottleneck verdict
    ├── MemoryHistory.cs                Rolling 60 s window, median delta leak detector
    ├── FpsSparkline.cs                 Unicode block chart of last 60 s of FPS
    ├── SpikeScreenshot.cs              Frame > threshold → ScreenCapture, throttled (instance, owned by Profiler)
    ├── HarmonyConflictDetector.cs      Lists multi-owner patches involving VanillaProfiler
    ├── SystemReplacementDetector.cs    Lists vanilla OnUpdate methods prefixed by other mods
    ├── GraphicsSettingsProbe.cs        Reads CS2 graphics settings (LOD, volumetrics, etc.) — instance, owned by Profiler
    ├── Recommendation.cs               DTO for a single recommendation (level + title + reason + action)
    ├── RecommendationEngine.cs         Builds recommendations from health + snapshot + probed settings — instance, takes GraphicsSettingsProbe via DI
    ├── CityContext.cs                  Static snapshot of in-game entity counts
    ├── ReportExporter.cs               Ctrl+F11 → CSII_Report_*.txt + bounded CSII_Report_*.zip support bundle
    ├── ReportTextSections.cs           Shared text sections (counter availability, top tables) for export and log
    ├── ProfilerSettings.cs             Immutable user-preferences DTO with .With(...) and .Normalize() — no I/O of its own
    ├── ProfilerSettingsSnapshot.cs     Point-in-time settings view passed through hot/cold boundaries (holds an immutable ProfilerSettings reference)
    └── SettingsStore.cs                Load / Apply / Update / Snapshot — atomic .tmp+.bak write protocol; settings are not mutable from runtime code
```

## Output locations

| File | Path |
|---|---|
| Continuous log | `<persistentDataPath>/Logs/VanillaProfiler.log` |
| Diagnostic reports | `<persistentDataPath>/Reports/CSII_Report_*.txt` |
| Support bundles | `<persistentDataPath>/Reports/CSII_Report_*.zip` |
| Spike screenshots | `<persistentDataPath>/Logs/spikes/spike_*.png` |
| Settings | `<persistentDataPath>/VanillaProfiler/settings.json` |

`<persistentDataPath>` on Windows resolves to `%LOCALAPPDATA%Low\Colossal Order\Cities Skylines II\`.

## Build

Requires the **CS2 Modding Toolchain** (`CSII_TOOLPATH` environment variable
must point to its installation, set up by the official Cities Skylines II
Editor). The project references `Game.dll` from the Steam install of CS2.

```powershell
dotnet build VanillaProfiler.csproj --no-incremental
```

Output is auto-deployed to `%LOCALAPPDATA%Low\Colossal Order\Cities Skylines II\Mods\VanillaProfiler\`.

## License

Dual-licensed — see [LICENSE](LICENSE).
