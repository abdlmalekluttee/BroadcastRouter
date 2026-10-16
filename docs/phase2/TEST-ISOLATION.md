# Coordinator media-test isolation

## Reproduced causes, not package attribution

The frozen 40-job comparison included the same qualification timeout with the
old packages as with TimeProvider-only. It does not establish a package cause.
The assembly already disables xUnit test parallelization. Two fixture paths
already copied the fake executable into GUID-private directories; a new test
opens two copies exclusively at once and confirms they are independent.

`MediaChildFixture` previously emitted 150 batches on a three-second wall-clock
schedule. `Lab.Qualify` repeatedly stepped a separate fake clock and waited for
each reader's timestamp to equal it. Under scheduling delay the finite producer
could finish before the next virtual-time step. Waiting longer cannot create
missing records. A deterministic regression first waits for that producer to
finish, advances virtual time, and reproduces the original qualification timeout.

The stderr completion marker was also not a cross-pipe consumption barrier.
Buffered PCM/video could be timestamped *after* the test advanced time to assert
failure. Monitor correctly re-reads media before stopping; such late evidence
could invalidate the test's premise. The actual current stop implementation
awaits its controller and reaping: the explanation that it merely queues a stop
is incorrect.

## Boundaries now observed

- The entire coordinator test class and its shared monotonic-clock Lab use an
  opt-in command-stepped real child. Each batch still crosses the actual named
  pipes and production parsers. An internal observation notification fires after
  ingestion, and the test waits for every expected counter before changing time.
- Child startup, progress and stderr expectations use observed-event tasks. The
  fast-loop test waits for its *current completed iteration*, not existence of a
  semaphore left by an earlier iteration. Confirmed recovery also observes the
  owned exit event; it never invokes Stop itself to make a recovery assertion pass.
- All health, retry-count, same-session, stop-decision and failed-signal assertions
  remain. No sleep or larger observation deadline was introduced. Notifications
  are internal, have no production subscribers, and do not alter policy thresholds.
- The helper fault-injection test now retains the original stderr-reader task
  before replacing its property with the injected pending/faulted task. Fixture
  deletion waits for the actual reader. A failing-first test proves deletion is
  blocked until that barrier releases. Cleanup remains bounded to five seconds,
  uses a private executable and reports persistent sharing/access failures.

The historical UnauthorizedAccess record does not identify the external
file-handle holder. Reader ownership is now explicit and transient Windows
deletion denial is handled, but attributing the historical denial specifically
to an antivirus scanner or a particular kernel handle would be speculation.

## Verification

Focused coordinator/clock/cleanup group: 54/54, zero skipped. The qualification
and cleanup-barrier tests failed at their intended assertions before the fixes.
Independent review also found an unobserved command-task failure and cleanup
skipped after a completed-faulted reader. Both were reproduced test-first; the
child now reports the original endpoint error and exits nonzero, and cleanup
deletes owned files before rethrowing a completed reader's error. A pending
reader timeout still fails and does not pretend ownership has been released.

The private stress runner's missing deadline was corrected with a failing-first
hang exercise. Its ten-minute per-invocation cap now preserves partial separate
stdout/stderr, a timeout receipt and exact owned-tree reaping. Four preliminary
20/20 runs were retained but do not count towards final-payload qualification;
the fifth was interrupted for review corrections and is incomplete, not a pass.
Software checkpoint on implementation `06bfb65`: local normal 934/934, no
failures/skips (11m35s unsharded); build zero warnings/errors; provenance 12,
CI policy 17, recipe 15. Strict KnownDefect contract passed with the two expected
R18 assertion failures still OPEN.

Hosted CI run 38905818859 passed first attempt on the same implementation:
811 core + 100 media + 22 standby + 1 telemetry = 934 unique normal cases,
no failures/skips. Shard durations 4m45s / 4m53s / 3m19s / 9m04s, all below
ten minutes. Build/privacy, two identical clean-publish manifests and CodeQL
passed. Recorded runner: windows-2022 image 20261012.333.1.

At this software checkpoint the final twenty sequential CPU-contention
repetitions are still running against frozen compiled files. PR #97 records the
terminal qualification receipt and readiness decision; do not infer completion
from this checkpoint. No production or hardware qualification.

R18 remains OPEN; native shutdown, physical output/audio, reboot recovery and
production-storage latency remain separate gates.
