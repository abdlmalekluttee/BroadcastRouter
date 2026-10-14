# Phase D combined qualification

This main-based integration combines the accepted final changes from #83, #84,
#85, #86 and #87, in that order. It does not authorize deployment or hardware
testing. Only the repository owner merges. R18's two KnownDefect tests remain
open, not passing product behavior. Hardware rows remain open.

## Conflict decisions

### #83 then #84: exact-handle guard and output controllers

- `FfmpegProcessSupervisor.cs`: keep both immutable `BenchOutputGuard` and shared
  `PortReservationManager` dependencies. Every live, fallback, idle-standby and
  recovery-standby call carries its actual port through the output-controller
  worker. Validate the final instrumented argv under the owner gate, before any
  process creation. Retain the configuration snapshot and asynchronous observation
  ownership transfer from #84; do not perform a duplicate live start.
- `RouterCoordinator.cs`: retain #84's single long-lived supervisor and future-start
  configuration generations. The initial constructor receives BOTH the guard and
  shared reservation registry by name. Configuration edits neither replace the
  supervisor nor drop guard authority.
- `docs/PHASE2_PROGRESS.md`: preserve both independent histories. Historical branch
  numbers are not evidence that the combined head passes.
- `Program.cs` merged cleanly; review both startup release preflight and controller
  health reporting as shared boundaries despite no textual conflict.

## Verification

Fresh main baseline: 672 normal tests passed, zero failed/skipped. Combined
qualification is pending; no merge-ready claim is made by this document.
