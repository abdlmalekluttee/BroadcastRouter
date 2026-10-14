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

### #85: bounded diagnostic persistence

- `RouterCoordinator.cs`: retain the existing six-argument activation overload
  and the guarded constructor, and expose diagnostic persistence health. Lifecycle
  callbacks enqueue into the store-owned queue; essential route persistence remains
  awaited. The long-lived supervisor configuration method from #84 is retained.
- `Program.cs`: health requires BOTH a healthy output-controller state and healthy
  diagnostic persistence, in addition to the existing liveness/telemetry conditions.
  The diagnostic worker is registered before coordinator/lifecycle services so its
  shutdown drain occurs after their final events.
- `MainLayout.razor` merged cleanly: both independent visible alarms remain.
- `docs/PHASE2_PROGRESS.md`: append #85 evidence without dropping earlier histories.

### #86: atomic native child containment

- `FfmpegProcessSupervisor.cs`: keep #84's admission close, pending-launch drain,
  per-output controllers, bounded shutdown and failed-start reap. Replace the
  managed `Process` with #86's `OwnedChildProcess` and remove the old supervisor-
  wide job assignment. Native creation is contained before execution; no second
  post-start job assignment remains.
- Final shutdown requests termination only through each retained helper's own
  job. Keep handles/pipes until the bounded observer drain, then dispose wrappers.
  Do not restore #86's older unbounded waits or dispose controller semaphores that
  still have in-flight users. Delayed cleanup cannot create replacement ownership.
- `CoordinatorIndependentMediaTests.cs` merged cleanly: retain #85's sample
  high-water synchronization and #86's exact owned-wrapper reflection type.
- `docs/PHASE2_PROGRESS.md`: preserve containment evidence and its open hardware,
  Session 0 and combined-build qualifications.

### #87: actual monotonic-clock consumers

- `docs/PHASE2_PROGRESS.md`: preserve all independent branch results and clearly
  distinguish them from combined qualification.
- The test changes merged without text conflicts. Explicitly retain the #85
  store-owned diagnostic drain and sample high-water synchronization alongside
  #87's actual UTC audit, SQLite retry round-trip and media clock-step assertions.
  Keep #86's native process wrapper in shared media fixtures. No runtime thresholds
  or policy were changed by the clock-test integration.

## Verification

Fresh main baseline: 672 normal tests passed, zero failed/skipped. Combined
qualification is pending; no merge-ready claim is made by this document.

Incremental conflict checks (not substitutes for final-head qualification):

| Integrated step | Focused normal cases | Result |
| --- | ---: | --- |
| #83 | 88 | Passed |
| #84 | 119 | Passed |
| #85 | 73 | Passed |
| #86 | 87 | Passed |
| #87 | 67 | Passed |

The first #85 diagnostic filter also selected the two R18 KnownDefect cases;
they failed at their expected assertions. The corrected normal-only filter above
passed without changing tests or implementation. Strict KnownDefect verification
is still a separate final-head requirement.
