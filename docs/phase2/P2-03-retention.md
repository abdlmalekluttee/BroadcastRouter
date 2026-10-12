# P2-03 follow-up — shared telemetry capacity and visible loss

Scope: storage layout and diagnostics only. No routing policy, process deadline,
driver, service, production configuration, or physical output change. Owner
approved shared rotating segments with complete per-session reconstruction.

## Root causes and correction

- One physical stderr stream per short-lived session exhausted the 512-file cap
  far below its byte budget. Session churn also forced a directory scan/sort on
  almost every child. Stderr now shares a rotating journal per service run;
  router and decision records retain their existing separate run journals.
- Retention loss was omitted from `IsHealthy`, so `/health` could remain healthy.
  It now degrades health and renders a non-dismissible alert in the application
  layout. This signal is diagnostic only; nothing uses it to stop an output.
- Eviction counters vanished on restart. `retention-loss.json` is durably
  written before early deletion and reloaded by the storage worker. It is a
  sticky conservative loss record, not a claim that missing evidence can heal.
  A crash between marking and deleting can over-report loss, never hide it.
  Normal 30-day age expiry does not create a loss alarm. An unreadable/corrupt
  loss record degrades storage rather than authorizing further deletion.
- Fresh review reproduced an idle restart reporting healthy until the next
  record/flush. The worker now checks prior loss immediately. Until that check
  completes, health is not green and the UI says it is checking storage. A
  corrupt marker raises a write failure without waiting for any new record.

No new operator setting or dismiss button. Capacity lives in application config:

```json
"Telemetry": {
  "SegmentBytes": 16777216,
  "MaxSegments": 8192
}
```

This allows up to 128 GiB of segments; it does **not** preallocate disk. Reserve
disk headroom for the OS, database, other application data, and filesystem
metadata before deployment. Reducing the configured cap can immediately evict
recent logs and raise the alarm. Config is read at service startup; this does
not change the existing immediate-apply contract for routing/output settings.

## Reproducible measurement

Run locally, not on a shared broadcast host:

```powershell
dotnet run --project tools/TelemetryLoad -c Release -- 1000
```

The tool leaves a uniquely named synthetic evidence directory for inspection.
It never starts a child, opens a media device, or contacts any server. Fixture:
1,000 sessions, 100 synthetic stderr lines/session (roughly 290 characters),
two lifecycle records and one router warning/session; flush after each ten
sessions. All 103,000 records must be accepted and written with no loss.

| Layout | Bytes | Files | Elapsed (local) |
|---|---:|---:|---:|
| Per-session, 1 MiB segments | 81,841,731 | 1,940 | 202.53 s |
| Shared, 1 MiB segments | 81,842,370 | 80 | 73.95 s |
| Shared, 16 MiB segments (before initialization field) | 81,841,613 | 7 | 57.72 s |
| Shared, 16 MiB segments (final reviewed code) | 83,797,462 | 7 | 106.68 s |

No drops, write failures, or early evictions in these measurements. Local runs
are not controlled benchmarks: background load and storage cache differ.

Using the larger observed aggregate churn (624,413 starts in 30 days), the
defined fixture requires **104,648,449,280 bytes** for 30 days × 2; the chosen
137,438,953,472-byte capacity exceeds it. The single storm-owner rate requires
73,626,964,038 bytes. Compute capacity from bytes, not `7 files / 1000 sessions`:
the final partly filled segments do not repeat for every batch in one run.

**Limit:** this is a complete sink measurement of a stated synthetic workload,
not a universal upper bound on FFmpeg stderr or a real 30-day retention soak.
Very verbose long-lived sessions, excessive service restarts, queue overflow,
or insufficient disk can still lose evidence; the health/UI alarm must make
that visible. Storage remains bounded and non-blocking to media callbacks.

## Full session reconstruction

`StderrLog` now names `run-<service-run-guid>.stderr.*.jsonl`. It is a shared
file glob, **not an already filtered transcript**. Select records whose
`Event.ServiceRunId` **and** `Event.SessionId` match the decision/process record,
then sort by envelope `Sequence` (file names are random rotation IDs). Preserve
the envelope UTC, monotonic ticks, timestamp frequency and telemetry counters.
Do not sort by file enumeration order or wall time. Never combine different
service-run epochs, even if a session GUID is reused in a test fixture.

Existing `session-<run>-<session>.stderr.*.jsonl` files remain readable using the
same filter; retention recognizes old and new names. Foreign files and reparse
paths are not accepted as deletion targets. Logs remain internal and redacted;
do not publish raw shared segments as if they contained only one session.

## Evidence and verification

- `fa6317a`: two exact red assertions (recent evidence evicted by session count;
  loss disappears after restart), plus a passing reconstruction characterization.
- `a8eaead`: sharing and durable loss record; focused storage/lifecycle 29/29;
  initial full normal suite 258/258.
- `5f48aea`: exact red health and rendered-layout alert assertions; healthy UI
  negative control passed. After correction, focused alarm/storage 22/22.
- Reconstruction covers 100 records interleaved with another session, multiple
  segments, and another service run reusing the same session GUID.
- Existing secret redaction, queue/disk faults, reparse path, age and foreign-file
  tests retained. The old age test now explicitly expects the earlier capacity
  loss to stay unhealthy while asserting zero I/O failures.
- Local renderer exercised real MainLayout markup and both alarm states.
  Impeccable's scoped detector reported no findings. Visual browser verification
  is **not performed**: local preview launch was denied by execution policy;
  no alternate launch path was attempted.
- One local test invocation hit a DLL lock because another test run was active.
  It was not counted as the red reproduction; sequential rerun produced the
  expected two assertion failures above.

Fresh independent review found one Important startup-alarm defect, reproduced
in `2f16723` by two exact assertion failures, then corrected in one fix pass;
focused storage/alarm tests pass 23/23. No second review is implied. Final full
normal suite: **262 passed, 0 failed, 0 skipped** (2m51s); seven added by this
follow-up. Strict KnownDefect verification accepted all seven exact failures;
all seven remain OPEN. Python provenance12/12 and CI-contract12/12 passed.
Clean-source package and privacy check passed for code commit `5ee5b14`;
local candidate SHA256 `B2ED9920CC72D23192A3BFDC1DB207A81791F5B5CF96646BE80D599470912885`.
Candidate remains private, not deployed/released. PR #72 is stacked after #71;
hosted checks pending at this documentation checkpoint. This is not Gate B acceptance.

## Review exclusions and rulings

- Queue drops, I/O errors, and abandoned shutdown records still have run-local
  counters; this change persists capacity loss only. Their cross-restart history
  can remain incomplete. No claim of universal durable loss accounting is made.
- Capacity is sized for the stated measured fixture, not arbitrary verbosity.
  Actual disk headroom/log volume and a retention soak remain deployment checks;
  cost if underestimated: early eviction with a sticky alarm.
- Already lost legacy evidence cannot be recovered by migration. A clean new
  marker is not proof of completeness before this version.
- The existing trusted, single-host telemetry directory assumption remains.
  Reparse checks are not a hostile local filesystem race defense, nor a
  multi-writer storage protocol; do not share a directory between hosts.
- Real browser layout/live announcement, physical silence/driver/Session 0,
  500-cycle qualification, disk soak and deployment rollback are unverified.
  Renderer and software tests do not substitute for those acceptance checks.
- The seven KnownDefects and unrelated routing/auth/export issues remain with
  their owning tasks. These are open risks, not accepted production fixes.

## Operational caveats

The retention-loss marker is deliberately sticky. Do not delete it simply to
turn health green. After preserving evidence, investigating capacity and
documenting the missing interval, a future operator-approved archival/reset
procedure may start a new evidence epoch. No automatic alarm clear is added.

This follow-up does not close the physical stop/silence/busy-device acceptance,
the 500-cycle requirement, or the unresolved one-step bench rollback.
