# VanillaProfiler — Player Guide

Lightweight in-game frame-health and main-thread monitor. Tells you when the game is healthy, when it isn't, and which mod (or vanilla system) is doing the most work on the main thread. Designed to be turned on permanently or pulled out when you need to diagnose lag.

## What this mod can and cannot measure

**Accurate:** frame time, FPS, GPU frame time, CPU main/render thread time, memory (managed/Mono/native/GPU), memory leak detection, frame spike count, frame spike screenshot, mod conflict detection.

**Main-thread cost only** (per-system / per-mod tables): scheduling overhead, sync points, structural changes (`EntityManager`), ECB playback, and synchronous main-thread work. Useful for finding badly-architected mods and sync-point hotspots — flagged with `[likely sync point]` in the log when an Update() exceeds the threshold.

**Not measured:** Burst-compiled job execution on worker threads. A clean DOTS system can cost 5–20 ms on workers and show ~0.01 ms here. For per-job analysis attach Unity Profiler to the running game — that's the only tool with engine-level instrumentation inside jobs.

## Hotkeys

All shortcuts use **Ctrl + F-key** so they don't collide with vanilla Cities: Skylines II bindings (F5 quicksave, F9 quickload, etc.).

| Key | Action |
|---|---|
| **Ctrl+F7** | Toggle spike screenshots (auto-capture on frames over threshold) |
| **Ctrl+F8** | Open / close settings panel |
| **Ctrl+F9** | Cycle overlay mode: Status → Diagnosis → Tips → Details → Engine → Hide |
| **Ctrl+F10** | Force an immediate report dump to `VanillaProfiler.log` |
| **Ctrl+F11** | Export full diagnostic report (support file) to `Reports/CSII_Report_*.txt` |
| **Ctrl+F12** | Cycle overlay position: Top-Left → Top-Right → Bottom-Right → Bottom-Left |

## Overlay modes

### Status (default)
Short player-facing status. No system tables, no technical breakdown — just the current state, likely cause, and the useful keys.

```
VANILLA PROFILER  >  STATUS
Status: Good
Cause:  no clear problem
FPS:    58 avg / 41 min
Memory: stable
Likely mod: no mod stands out yet
Ctrl+F9 next  •  Ctrl+F8 settings  •  Ctrl+F11 support file
```

### Diagnosis
Plain-language explanation for normal players. Open it when the status says `Warning` or `Problem`.

```
VANILLA PROFILER  >  DIAGNOSIS
Problem: simulation is overloaded

▸ LIKELY CAUSE
The simulation is taking most of the frame.
This can be a large city or a heavy gameplay mod.

▸ SUSPECTED MOD
TrafficLightsEnhancer

▸ WHAT TO DO
1. Save the city.
2. Restart the game.
3. If it repeats, press Ctrl+F11 and send the report.
```

### Tips
Actionable recommendations picked from the current health report and graphics settings.

### Details
Advanced screen for mod authors and support. Shows top mods (by main-thread cost), top vanilla systems (main-thread cost), top mod systems (main-thread cost), FPS sparkline, and city context. Per-thread CPU/GPU/PresentWait breakdown lives on the Engine screen instead. Per-system numbers reflect main-thread time only — see the "What this mod can and cannot measure" section at the top.

A **Patched vanilla systems** section appears when another mod has applied a Harmony prefix to a vanilla `OnUpdate`. Those vanilla systems run through a foreign gate (or are skipped outright), so their cost cannot be measured directly — but the line `Game.Foo.BarSystem ← ModName` tells you which mod owns the replacement. Up to 6 entries on screen; the full list is always written to `VanillaProfiler.log` as the `PATCHED VANILLA SYSTEMS` section once per save.

```
VANILLA PROFILER  >  DETAILS
FPS    58 avg  41 min  |  17.2ms / 24.3ms
60s    ▅▆▆▇▆▇▇▆▅▆▆▇█▇▆▅▆▇▇▆▅▆▆▆▇▆▇▇▇▆
SIM     32 ticks/s
GROW    +0.20 MB/s   |   spikes 0<30  0<20
MEM     1840 MB managed   |   +12.4 MB from baseline
BOTTLENECK  Balanced — Frame budget balanced
CITY    80k pop, 24k vehicles, 12k buildings
▸ TOP MODS
  CivicSurvival              42.3 ms
  ModX                        3.1 ms
▸ VANILLA SYSTEMS
  CitizenAISystem            18.0 ms
  ...
▸ MOD SYSTEMS
  ThreatMovementSystem       12.4 ms
  ...
Ctrl+F8 settings  •  Ctrl+F9 mode  •  Ctrl+F10 dump  •  Ctrl+F11 export  •  Ctrl+F12 move
```

### Engine
Raw Unity engine counters — frame timing, render counts, GPU memory breakdown, GC stalls. For diagnosing what *type* of bottleneck you have rather than which system caused it.

```
VANILLA PROFILER  >  ENGINE
▸ Frame timing
  CPU main:      35.32 ms
  CPU render:     8.37 ms
  GPU:           40.16 ms
  Present wait:   0.05 ms  (0% of frame — GPU-bound when high)
▸ Render counts
  DrawCalls   5379   SetPass  440
  Tris   55509K   Verts   78195K
  Shadow casters: 3510
▸ GPU memory
  Used buffers:    625.9 MB  (8652 bufs)
  Render targets: 2536.0 MB
▸ GC
  64 collections, total stall 29.63 ms
Process RSS:    7906 MB
```

Reading guide:
- **Present wait** is the real GPU-bound signal. >30 % of the frame ⇒ CPU sits idle waiting on the GPU; lowering graphics quality helps. Near-zero with high CPU main ⇒ CPU bottleneck, ECS is the place to optimise.
- **SetPass** spike ⇒ shader-state churn (lots of material/keyword variance).
- **GPU memory split** lets you tell a buffer leak from a render-target leak.
- **GC stall** ⇒ how much frame time was lost to managed garbage collection in the window.

### Hide
Overlay disappears. Hotkeys still work. Toasts (e.g. "Support file created") still appear briefly at the bottom of the screen.

By default a small `[Ctrl+F9] Profiler` pill stays in the top-right so you don't lose the way back. If it overlaps in-game HUD buttons you can disable it: **Ctrl+F8 → uncheck "Show hint pill in Hide mode" → Apply & Save**. With the pill off the screen is fully clean in Hide mode; entering Hide flashes a 3-second toast at the bottom (`Profiler hidden — Ctrl+F9 to cycle, Ctrl+F8 settings`) as a one-shot reminder.

## Reading the metrics

| Metric | Good | Ok | Poor |
|---|---|---|---|
| FPS avg | ≥ 50 | 30–50 | < 30 |
| Frame max | < 33 ms | < 50 ms | ≥ 50 ms |
| Memory delta | < 50 MB | < 200 MB | ≥ 200 MB or growing |
| Managed growth | < 1 MB/s | < 5 MB/s | ≥ 5 MB/s |
| Spikes / 5 s | 0 | 1–5 | > 5 |

A red **MEMORY** with a `LEAK SUSPECTED: +120 MB over 30s` hint means managed memory has been growing steadily across the recent report window — most likely a mod with an allocation leak. Restart the game.

## Bottleneck verdict

| Verdict | Meaning | What to try |
|---|---|---|
| **Balanced** | No single subsystem dominates the frame | Nothing |
| **RenderBound** | GPU/render phase > 60 % of frame | Lower graphics settings (volumetrics, shadow distance, anti-aliasing) |
| **SimBound** | Sim phase > 60 % of frame | City is large or a mod is heavy — check **Top Mods** |
| **MemoryBound** | Managed memory growth > 10 MB/s | Restart the game; report which mod is heaviest |

## Sharing a bug report

1. Press **Ctrl+F11** while the lag is happening (or right after).
2. Find the file: `%LOCALAPPDATA%Low\Colossal Order\Cities Skylines II\Reports\CSII_Report_*.txt`
3. Attach it to your bug report (Discord, GitHub, forum). It contains:
   - System info (CPU / GPU / RAM / OS)
   - Loaded mods
   - Latest report window (FPS, memory, bottleneck, leak status)
   - City context (citizens / vehicles / buildings)
   - Top mods, top systems
   - Last 50 lines of `VanillaProfiler.log`

If the issue is repeated stutter (not just "feels slow"), enable **Ctrl+F7 spike screenshots** — frames over 100 ms (configurable in settings) will be saved to `Logs/spikes/`. Attach those too.

## Settings (Ctrl+F8)

| Setting | Default | Notes |
|---|---|---|
| Report interval | 5 s | How often `VanillaProfiler.log` gets a new entry |
| Sparkline width | 60 | Number of seconds shown in the FPS sparkline |
| Spike screenshots | on | Auto-capture on frame > threshold |
| Profile vanilla systems | off | Patches every vanilla `Update`; adds measurable overhead |
| Show hint pill in Hide mode | on | Off = fully clean screen in Hide; entering Hide flashes a 3 s toast |
| Spike threshold | 100 ms | Lower = more captures (with 30 s cooldown) |
| Default mode | Status | Mode at game start |
| Position | Top-Left | Anchor of the overlay |
| UI scale | Auto | `Auto` follows screen height; pick `1×` … `2.5×` to override |

Settings persist in `<persistentDataPath>/VanillaProfiler/settings.json` — survives game restarts. Reset Defaults restores everything in one click.

## Frequently asked

**Why is "MEMORY" red right after a save loads?**
Loading a city pulls in 200+ MB of vanilla allocations. Leak detection waits for a 5-report window before raising the alarm, so a one-off load spike doesn't trip it.

**Top Mods shows my mod even when I'm doing nothing in-game.**
Most ECS systems run every tick. Background work — pathfinding, citizen AI, building demand — happens regardless of player action.

**Spike screenshots aren't saving.**
Check `Logs/spikes/` — the directory is created on first capture. There's a 30-second cooldown between captures so a long stutter sequence doesn't flood the disk.

**Can I run VanillaProfiler alongside CivicSurvival?**
Yes. The two are independent assemblies. CivicSurvival has its own performance profiler (`PERF.log`) that overlaps in some metrics but stays in its own log. VanillaProfiler is for diagnosing the **whole game** including all loaded mods.
