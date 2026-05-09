# Changelog

## Unreleased

### Added

- Counter availability is now surfaced in diagnostic exports so unavailable Unity profiler markers are visible instead of being confused with real zero values.
- Ctrl+F11 export now attempts to create a bounded `.zip` support bundle alongside the text report.
- Support reports include recommendation reasons.

### Changed

- Engine counter UI can show `n/a` for unavailable markers.
- ECB playback timing records elapsed time even when playback throws and Harmony runs the finalizer path.
- Session lifecycle now initializes from the current game mode on mod load, so reloading the mod inside a city starts measuring without waiting for a future load callback.
- Long pauses now reset the managed-memory leak window instead of clamping elapsed time into a false growth rate.
- Top Mods/Systems now report self-cost (exclusive main-thread time) instead of inclusive nested `SystemBase.Update` time. Numbers may be lower than previous builds; that is expected because nested systems are no longer double-counted.
- System tables in `VanillaProfiler.log` now include both `SELF` and `INCL` columns. Patched-vanilla diagnostics keep using total Update ms because the mod-prefix/vanilla-original split is not measurable.
- Patched-vanilla detection now uses a deduplicated snapshot refreshed at lifecycle boundaries and at the start of each report window, so hot-path routing and report output read the same state.
- Overlay read access now uses DTOs and commands instead of exposing live profiler internals. Long overlay bodies scroll under a fixed header, and Reset Defaults returns manual panel positions to the anchor preset.

### Internal

- Local analyzer profile remains ignored by git and can be run with `.\scripts\check-local.ps1`.
- Session boundary resets now go through a typed `SessionBoundary` path and recreate Unity `ProfilerRecorder` timing counters.
