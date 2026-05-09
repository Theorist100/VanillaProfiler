# Changelog

## 1.6.1

### Internal

- Refactored profiler architecture into smaller focused components. Report building, health classification, graphics settings reflection, and metrics bucket ownership are now explicit components with typed measurements, typed metrics buckets, typed overlay rows, immutable health reports, and typed recorder values. No behavior changes for end users.
- Split overlay main panel renderer and settings draft into separate components.
- Split report formatting and log tail reading into independent modules.
- Extracted memory recorders into a dedicated `MemoryRecorderSet`.
- Split mod attribution responsibilities across smaller focused components.

## 1.6.0

### Added

- Counter availability is now surfaced in diagnostic exports so unavailable Unity profiler markers are visible instead of being confused with real zero values.
- Ctrl+F11 export now attempts to create a bounded `.zip` support bundle alongside the text report.
- Support reports include recommendation reasons.

### Changed

- Engine counter UI can show `n/a` for unavailable markers.
- Spike screenshots are now off by default. Enable them with Ctrl+F7 or in Settings when you need capture evidence for repeated stutters.
- ECB playback timing records elapsed time even when playback throws and Harmony runs the finalizer path.
- System, ECB, and UpdateSystem patch timings now use explicit started/completed measurement tokens so default Harmony state cannot produce fake samples.
- Reports now drain metrics with the replacement/settings context that was active for that window, then prepare the next context after reporting.
- Tips no longer suggest Max Frame Latency for true GPU-bound/present-wait cases; that advice is now reserved for CPU-render-bound GPU underutilization.
- Lifecycle callbacks now flow through token-based session transitions; duplicate settling callbacks no-op, missing preload completions create a clean synthetic session, and dispose no longer restarts recorders through the boundary reset path.
- Session lifecycle now initializes from the current game mode on mod load, so reloading the mod inside a city starts measuring without waiting for a future load callback.
- Long pauses now reset the managed-memory leak window instead of clamping elapsed time into a false growth rate.
- Top Mods/Systems now report self-cost (exclusive main-thread time) instead of inclusive nested `SystemBase.Update` time. Numbers may be lower than previous builds; that is expected because nested systems are no longer double-counted.
- System tables in `VanillaProfiler.log` now include both `SELF` and `INCL` columns. Patched-vanilla diagnostics keep using total Update ms because the mod-prefix/vanilla-original split is not measurable.
- Patched-vanilla detection now uses a deduplicated snapshot refreshed at lifecycle boundaries and at the start of each report window, so hot-path routing and report output read the same state.
- Overlay read access now uses DTOs and commands instead of exposing live profiler internals. Long overlay bodies scroll under a fixed header, and Reset Defaults returns manual panel positions to the anchor preset.
- Mod attribution now carries origin/confidence data, keeps Harmony owner ids separate from patch assembly identity, and refreshes conflict logs when the Harmony patch signature changes.
- Overlay commands now flow through a semantic hotkey contract, persisted modes use stable mode ids, settings loading validates and reads through one stream, and toast/settings/main panel layering is explicit.

### Internal

- `scripts/check-local.ps1` now runs the reproducible public clean-build path by default; private local analyzers remain opt-in with `-LocalAnalyzers`.
- Added `scripts/release-preflight.ps1` to stage release artifacts from clean Release output and verify source, publish metadata, and DLL versions agree.
- Session boundary resets now go through a typed `SessionBoundary` path and recreate Unity `ProfilerRecorder` timing counters.
