# P2-03 follow-up: 10 GB limit and disk reserve

Owner ruling: Gate B software accepted, hardware BLOCKED-ON-BENCH. The previous
storm-rate capacity estimate is not a sizing requirement. PRs #66–72 stay frozen.
This follow-up does not deploy code or change production settings.

## Storage behavior

`Telemetry:MaxTotalBytes` defaults to **10,000,000,000 bytes** (decimal 10 GB).
Values above 10 GB or too small for one minimum segment plus headroom are rejected.
Existing SegmentBytes/MaxSegments remain additional limits, not permission to
exceed the total byte cap. The budget reserves 64 KiB for control-file/allocation
headroom. Files are not preallocated to 10 GB.

Before each record append the single storage worker checks actual available
space. It preserves **max(20% of volume capacity, 20,000,000,000 bytes)** plus the
headroom, and evicts oldest owned segments until the append fits. It never
deletes foreign files. If nothing can be reclaimed, the new record is dropped;
media callbacks do not wait for disk and no media recovery is triggered.
Unknown capacity or unreadable/corrupt loss state fails closed.

The 4096-byte loss marker is provisioned while adequate space exists, then
overwritten in place and flushed before early eviction. Disk pressure is sticky
even when there was nothing to evict. A malformed marker degrades health after
restart. Legacy positive-eviction markers remain readable. An initial launch
already below reserve with no provisioned marker refuses logging rather than
allocating a marker unsafely. An existing directory with no marker is treated as
unknown history on restart, not a healthy zero-loss epoch. The existing health/UI
alarm remains nondismissible. An uncertain/partially successful append invalidates
the byte inventory before another write, so unaccounted bytes cannot bypass the cap.

All filesystem work stays on the consumer; per-append free-space queries do not
rescan the directory normally. Inventory refresh/rotation and pressure eviction
do scan. Queue overflow, I/O failures and abandoned drain remain visible.

This is an application admission guard, **not an OS quota**. An independent
writer can consume space between query and append; no application can promise
that other software never fills the disk. The single-writer trusted-directory
assumption remains. Reparse paths are rejected; hostile local filesystem races
and other applications' logs are outside this guard. Disk soak remains open.

## RED → GREEN evidence

First commit 9f4c5ea: six intended failures, no compiler/crash substitutions:
total byte cap; 20 GB floor; 20% floor; no reclaimable space; unknown disk capacity;
configuration above 10 GB. The constructor dependency seams were inert in RED.

During implementation, a missing-field marker (`{}`) reproduced a false healthy
state. The new schema discriminator test failed first, then became green. Old
storage tests now inject only volume capacity to avoid depending on developer/
hosted-runner free space; actual storage and rotations remain real. New guard
tests use capacity reduced by the real fixture-file lengths.

Fresh independent review found initial-pressure loss could disappear after restart,
and uncertain writes could undercount disk consumption. Three additional cases
failed at their intended assertions before the fixes. The focused storage/guard/
alarm suite then passed 33/33. Full-suite results are recorded in progress.

## Post-Phase-B measurement

Reproduce locally (no network or hardware):

```powershell
dotnet test tests/BroadcastRouter.Tests -c Release --filter FullyQualifiedName~PhaseBTelemetryMeasurementTests --logger 'trx;LogFileName=measurement.trx'
```

Workload: 86,400 simulated seconds at a 30-second supervision cadence; one real
fake-child process emitting advancing 25 fps progress through redirected pipes,
with 100 synthetic 256-character startup stderr lines; one offline reserved source
running its exact retained fake fallback child. Its real probe subprocess returns
RTSP 404, and every completed decision is asserted to be RtspNotFound. Actual
coordinator monitoring/reconciliation, readiness/backoff, supervisor, SQLite and
file-logger code run. This is **not a real decoder or SDI test**.
No repeating startup storm, on-air input, actual Wowza request or device access.

Verified full-suite measurement follows. The earlier provisional 10.21 MB/day
and 979-day projection are withdrawn: that fixture used a missing probe executable
and an isolated progress parser rather than two supervised owners. The corrected
fixture asserts two owners and the expected RTSP-404 decisions, so those setup
errors cannot silently pass.

| Metric | Result |
|---|---:|
| Segment bytes per simulated day |10,381,699 bytes (~10.38 MB)|
| Preallocated control file |4096 bytes|
| Accepted/written records |5863/5863|
| Queue loss/write failures/evictions |0/0/0|
| Offline RTSP-404 probe completions |2880|
| Owned starts (live + fallback) |2, one each|
| Offline fallback stops/replacements |0|
| Normal parsed frames |2,160,000|
| 10 GB capacity at measured rate, after headroom |~963 days|
| Actual age-retention limit |30 days|

Small serialized sizes vary with process IDs/timing. This narrow reproducible
baseline is an estimate, **not a universal verbosity maximum or production
bytes/day observation**. More sources, warning-heavy feeds, restarts and framework
requests add records. Admission controls still cap storage under those loads;
the alarm reports incomplete evidence instead of pretending 30 days always fit.

## Production volume read (private evidence retained)

The production data volume was inspected read-only and had space above the new
reserve. Exact capacity/free-byte figures are in the private progress report and
owner handoff, not public git. No server setting/file/service/route was changed.
Paths, host identifiers and raw captures stay private. The developer workstation
was below the reserve; tests inject volume capacity, and no files were removed.

## Verification and review

Release solution build: 0 warnings, 0 errors. Normal suite: 273 passed, 0 failed,
0 skipped (7m06s locally). Strict KnownDefect verifier: seven exact expected
assertions observed; all seven remain OPEN. Python provenance 12/12 and strict-CI
validator 12/12 passed. Raw TRX paths/host metadata stay in private evidence.

Fresh independent review preceded the single fix pass. Initial-pressure marker
durability, uncertain append accounting, and the misleading sizing fixture were
reproduced by assertions and corrected; the full green run includes those fixes.
No second reviewer pass is represented. Hardware and field retention were outside
the software review, not silently accepted. Queue/drop/shutdown-loss durability
outside this byte-budget change is not claimed solved.

## Open acceptance

Physical silence/driver/Session 0, busy-device tests, 500 cycles and field retention
soak remain BLOCKED-ON-BENCH/open. Gate C cannot pass before hardware Gate B closes.
KnownDefects R04 (two), R05, R06, R07, R18 (two) remain OPEN until their owning fixes.

Deferred minor: the displayed oldest-retained timestamp can lag byte-pressure
eviction until the next prune. The sticky incomplete-retention alarm and storage
admission do not depend on that timestamp. No physical readiness is inferred.
