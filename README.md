# VanillaProfiler

Standalone diagnostic mod for Cities: Skylines II. Drop-in install: shows a player-friendly status / diagnosis / details overlay, attributes CPU time to individual mods, detects managed memory leaks, and exports a one-click bug report.

## What it does

- Shows a simple player status and diagnosis screen, with advanced details one Ctrl+F9 press away
- Measures FPS, frame time, sim ticks/s, managed memory growth, per-system timing
- Splits vanilla vs mod systems and attributes each system to its owning mod
- Detects sustained memory growth (likely leak) and renders a "traffic light" health summary
- Classifies bottleneck (RENDER / SIM / MEMORY / BALANCED) with one-line player advice
- Captures auto-screenshots on frame spikes, exports a one-click diagnostic report for bug reports

## Documentation

| Document | Audience | Purpose |
|---|---|---|
| [Docs/USER_GUIDE.md](Docs/USER_GUIDE.md) | Players | Hotkeys, overlay modes, how to read the report, how to share it |
| [Docs/ARCHITECTURE.md](Docs/ARCHITECTURE.md) | Developers | Code layout, data flow, key classes |
| [Docs/DEVELOPER_NOTES.md](Docs/DEVELOPER_NOTES.md) | Contributors | How to add new metrics, gotchas, build tips |

## Key files

```
.
├── Mod.cs                              IMod entrypoint, Harmony patches, system registration
├── Profiler.cs                         Instance facade — owns aggregator, sampler, builder, sinks
├── ProfilerHost.cs                     Static handle to the live Profiler instance (Volatile.Read)
├── ProfilerLifecycleState.cs           Lifecycle enum: NoCity / LoadingCity / Settling / Active
├── ProfilerOverlay.cs                  MonoBehaviour shell — picks an IOverlayMode, draws panel
├── SystemAutoProfiler.cs               Harmony patch on SystemBase.Update — covers ~300 systems
├── UpdateSystemPatch.cs                Phase timing (Pre/PostSimulation, Rendering, etc.)
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
│   ├── MemorySampler.cs                Managed/native memory totals + managed growth
│   ├── MemorySample.cs                 Memory snapshot model
│   ├── PhaseData.cs                    Per-key accumulator (total/max/calls)
│   ├── ReportBuilder.cs                Pure (sample + memory + history) → snapshot + health
│   └── OverlaySnapshot.cs              Immutable view exposed to overlay/exporter
├── Output/
│   ├── IReportSink.cs                  Interface for reporting destinations
│   └── LogFileSink.cs                  Writes VanillaProfiler.log
├── Overlay/
│   ├── IOverlayMode.cs                 Mode contract — adding a mode is purely additive (OCP)
│   ├── OverlayTheme.cs                 Classic Gold palette + cached GUIStyles
│   ├── OverlayPanel.cs                 DrawFrame / DrawSeparator / DrawSection / DrawLine
│   ├── OverlayFormat.cs                Pure formatting helpers
│   ├── OverlayInputHandler.cs          Ctrl+F-key polling → semantic events
│   ├── OverlayBadges.cs                Hidden / Standby / Settling badges (small fixed pills)
│   ├── DrawContext.cs                  Cursor + theme bundle passed to mode renderers
│   ├── Toast.cs                        Bottom-of-screen status messages
│   ├── Anchor.cs                       Screen-corner enum + Cycle/ShortName extensions
│   ├── PanelLayout.cs                  Stateless positioning helpers (scale, anchor rect, clamp)
│   ├── PanelPositionController.cs      Anchor / manual drag / persistence for main panel
│   ├── MainPanelButtons.cs             Bottom button block — mode tabs + action row + hint
│   ├── SettingsPanel.cs                Ctrl+F8 panel using draft-then-apply pattern
│   ├── SettingsWidgets.cs              Stateless DrawTextField / DrawSegmented helpers
│   ├── SettingsDraft.cs                Clone(ProfilerSettings) helper for the draft pattern
│   └── Modes/
│       ├── HiddenMode.cs               No-op mode
│       ├── StatusMode.cs               Player-facing traffic light + likely cause
│       ├── DiagnosisMode.cs            Plain-language explanation when Status is not Good
│       └── DetailsMode.cs              Mod-author view: top mods, sparkline, system tables
└── Diagnostics/
    ├── ModAttribution.cs               Type → ModName via IMod scan + assembly fallback
    ├── HealthClassifier.cs             GOOD/OK/POOR per metric + bottleneck verdict
    ├── MemoryHistory.cs                Rolling 60 s window, median delta leak detector
    ├── FpsSparkline.cs                 Unicode block chart of last 60 s of FPS
    ├── SpikeScreenshot.cs              Frame > threshold → ScreenCapture, throttled
    ├── HarmonyConflictDetector.cs      Lists methods patched by >1 mod
    ├── CityContext.cs                  Static snapshot of in-game entity counts
    ├── ReportExporter.cs               Ctrl+F11 → CSII_Report_*.txt with system info & log tail
    ├── ProfilerSettings.cs             User-preferences DTO with Clamp() — no I/O of its own
    └── SettingsStore.cs                Load / Save / Current — atomic .tmp+.bak write protocol
```

## Output locations

| File | Path |
|---|---|
| Continuous log | `<persistentDataPath>/Logs/VanillaProfiler.log` |
| Diagnostic reports | `<persistentDataPath>/Reports/CSII_Report_*.txt` |
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
