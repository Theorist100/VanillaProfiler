# VanillaProfiler — Player Guide

Lightweight in-game profiler. Tells you when the game is healthy, when it isn't, and which mod is responsible. Designed to be turned on permanently or pulled out when you need to diagnose lag.

## Hotkeys

All shortcuts use **Ctrl + F-key** so they don't collide with vanilla Cities: Skylines II bindings (F5 quicksave, F9 quickload, etc.).

| Key | Action |
|---|---|
| **Ctrl+F7** | Toggle spike screenshots (auto-capture on frames over threshold) |
| **Ctrl+F8** | Open / close settings panel |
| **Ctrl+F9** | Cycle overlay mode: Status → Diagnosis → Details → Hidden |
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

### Details
Advanced screen for mod authors and support. It shows top mods, top vanilla systems, top mod systems, FPS sparkline, and city context.

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

### Hidden
Overlay disappears completely. Hotkeys still work. Toasts (e.g. "Support file created") still appear briefly at the bottom of the screen.

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
| Spike threshold | 100 ms | Lower = more captures (with 30 s cooldown) |
| Default mode | Status | Mode at game start |
| Position | Top-Left | Anchor of the overlay |

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
