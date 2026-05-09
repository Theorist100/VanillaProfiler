# Changelog

## Unreleased

### Added

- Counter availability is now surfaced in diagnostic exports so unavailable Unity profiler markers are visible instead of being confused with real zero values.
- Ctrl+F11 export now attempts to create a bounded `.zip` support bundle alongside the text report.
- Support reports include recommendation reasons.

### Changed

- Engine counter UI can show `n/a` for unavailable markers.
- ECB playback timing records elapsed time even when playback throws and Harmony runs the finalizer path.

### Internal

- Local analyzer profile remains ignored by git and can be run with `.\scripts\check-local.ps1`.
