# Phase 2 progress

Gate A follow-up: [isolated direct/force-push rejection proof](phase2/P2-01-protection-proof.md)
records both actual GH013 rejections, verified cleanup and unchanged main protection.
No runtime changes or production access were needed for that proof.

Production is read-only. No hardware tests in Phase A. Only the repository owner
merges PRs; the agent never merges, force-pushes, or pushes directly to main.
Deployment requires separate explicit approval and a maintenance window.

| Task | Findings | Branch | PR | Status | Evidence link |
|---|---|---|---|---|---|
| P2-01 | R16, R23 | fix/P2-01-repo-provenance | [#63](https://github.com/abdlmalekluttee/BroadcastRouter/pull/63) | Owner merged; Gate A conditionally accepted | [P2-01 evidence](phase2/P2-01.md) |
| P2-02 | R04, R05, R06, R07, R18; harness | fix/P2-02-test-harness | [#64](https://github.com/abdlmalekluttee/BroadcastRouter/pull/64), integrated with #63 | Owner integrated; matrix rows assigned to owning tasks | [P2-02 evidence](phase2/P2-02.md) |
| P2-03 | R10, R11 | fix/P2-03-session-telemetry | [#65](https://github.com/abdlmalekluttee/BroadcastRouter/pull/65) | Owner merged; retention/full-transcript follow-ups remain | [P2-03 evidence](phase2/P2-03.md) |
| P2-03 follow-up | Startup timestamp-only writes | fix/P2-03-startup-migration | [#66](https://github.com/abdlmalekluttee/BroadcastRouter/pull/66) | Independent review and GitHub checks passed; main 187/187; awaiting owner | [Startup evidence](phase2/P2-03-startup.md) |
| P2-11 | R02 | fix/P2-11-offline-fallback | [#67](https://github.com/abdlmalekluttee/BroadcastRouter/pull/67) | Independent review approved software scope; main 211/211; required hosted CI green; bench pending | [Recovery evidence](phase2/P2-11.md) |
| P2-12 | R08 | fix/P2-12-publisher-epochs | [#68](https://github.com/abdlmalekluttee/BroadcastRouter/pull/68) | Independent software review approved; main 225/225; required hosted CI green; physical qualification pending | [Epoch evidence](phase2/P2-12.md) |
| P2-10 | R01 | fix/P2-10-standby-lifecycle | [#69](https://github.com/abdlmalekluttee/BroadcastRouter/pull/69) | Review findings fixed test-first; main 246/246; physical qualification and total log-volume measurement pending | [Standby evidence](phase2/P2-10.md) |
| P2-13 | ForcedTermination | fix/P2-13-graceful-stop | [#71](https://github.com/abdlmalekluttee/BroadcastRouter/pull/71) | Review finding fixed test-first; normal suite 255/255; strict seven OPEN; physical 500-cycle acceptance OPEN | [Stop evidence](phase2/P2-13.md) |
| P2-03 retention follow-up | R10, R11; Gate A capacity gap | fix/P2-03-shared-retention | [#72](https://github.com/abdlmalekluttee/BroadcastRouter/pull/72) | Fresh review startup finding fixed test-first; normal262/262, strict seven OPEN; measured capacity and visible alarm; hosted checks pending | [Retention evidence](phase2/P2-03-retention.md) |
| P2-03 disk budget | R10,R11; owner Gate B ruling | fix/P2-03-disk-budget | [#73](https://github.com/abdlmalekluttee/BroadcastRouter/pull/73) | Review findings fixed test-first; normal273/273, strict seven OPEN, build and all hosted checks green; awaiting owner | [Disk budget](phase2/P2-03-disk-budget.md) |
| P2-23 | R07 | fix/P2-23-strict-wowza | [#74](https://github.com/abdlmalekluttee/BroadcastRouter/pull/74) | Fixture corrections reviewed/test-first; Release315/315, strict six OPEN; Build/Main suite/KnownDefect hosted rerun38314054985 all green | [Strict inventory](phase2/P2-23.md) |
| P2-22 | R06 | fix/P2-22-media-health | Not opened | WIP supervisor/session13/13, Release370/370, strict five OPEN; capability/policy/audio-sample integration incomplete; not merge/deploy ready | [Media evidence checkpoint](phase2/P2-22.md) |

## Open KnownDefect register

These are uncorrected findings, never passing fixes. P2-02 adds correctness
assertions tagged KnownDefect and finding ID. Strict CI requires each exact
assertion failure; a different failure, crash, compile error, missing test, or
unexpected pass fails CI. A fixing PR removes its entry and promotes the test
to the normal suite in the same PR.

| Planned test | Finding | Status |
|---|---|---|
| SamePacketAudioBurstDoesNotAuthorizeRecovery | R04 | Open; exact assertion failure required by CI |
| MillisecondQueueWarningsDoNotAuthorizeRecovery | R04 | Open; exact assertion failure required by CI |
| DuplicateBurstRespectsConfirmationWindow | R05 | Open; exact assertion failure required by CI |
| FreshProgressDoesNotConcealStalledMedia | R06 | Identical probe promoted on P2-22 WIP branch; broader finding still OPEN pending coordinator/hold/UI integration |
| MissingIncomingStreamsIsUnknown | R07 | Fixed on P2-23 branch; identical assertion promoted to normal suite; not merged/deployed |
| DiagnosticActorIsRedacted | R18 | Open; exact assertion failure required by CI |
| DiagnosticReasonIsRedacted | R18 | Open; exact assertion failure required by CI |

## Approved execution constraints

- One branch and PR per task; commit 1 captures failing tests before implementation.
- Preserve the 136-test baseline, then prove unchanged KnownDefect results across
  the clock refactor. P2-33 owns NTP-step qualification.
- GitHub authentication uses the repository owner's account. Main requires PRs,
  Build/Main suite/KnownDefect checks from GitHub Actions, up-to-date branches,
  conversation resolution and linear history. Zero approvals, admin enforcement,
  no force-push or deletion. Human-only merging is a procedural restriction.
- Evidence bundles stay internal; public tests use synthetic data only.
- Gate A was conditionally accepted by the owner on 2026-10-12; Phase B authorized.
  No physical stability or media qualification is claimed. The historical Gate A
  report records the original report, not this subsequent owner decision.

## Gate A conditions and follow-ups (owner decision, 2026-10-12)

- Accepted residual risk: windows-2022 plus recorded ImageVersion and two-clean-build
  hash comparison, rather than immutable or self-hosted runner infrastructure.
- Integration dependency resolved by the owner. Fetched main ac0dbae matches the
  prior Phase A tree byte-for-byte. Old branch tips have a verified private recovery
  bundle; local main and the three stale task branches now match origin/main.
  The three remote task branches were already deleted when fetched.
- Required follow-up: test direct/force push rejection on a throwaway protection-test/*
  ruleset matching main, never against main. Remove temporary branch/ruleset afterward.
- Full fault matrix deferred by design: every row in P2-02's coverage table is an
  acceptance requirement of P2-10–13, P2-20–24, or P2-30–33. Every gate lists closed/open rows.
- Required telemetry follow-up: configurable segment capacity; harness measurement
  at worst-case churn; capacity >= 30 days x measured daily bytes x 2. Early eviction
  must produce a visible health/UI alarm, not only a counter. No retention soak claimed.
- Triage accepted: drop 6a859fc. Reimplement only required portions of e96c565,
  0e7d471 and 6e03693 under their owning tasks, test-first; no cherry-picks.
- Bot account pending creation by owner. Until supplied, current owner-account
  protection and human-only merge remain. Then switch account, add owner CODEOWNERS,
  and require one approval with code-owner review; no credential collection in git.
- Phase B order: startup follow-up, P2-11, P2-12, P2-10, P2-13. Stop at Gate B.

## Shared-bench safety

Only the specifically assigned test connectors may be released through the
application and tested in an owner-approved window. The owner subsequently
authorized removing routes/owners on those assigned connectors; that exception
does not cover any other connector. No production binary, media-tool installation,
service, driver, profile or Wowza configuration may be changed. Bench artifacts stay in a separate
folder. Exact operational before/after configuration and a one-step rollback must
be recorded in a PRIVATE copy of this progress document and shown to the owner
before applying the release; public git never contains operational identifiers.

Owner ruling after Gate B: SOFTWARE PART ACCEPTED, hardware BLOCKED-ON-BENCH.
The one-step restore requirement is superseded: consistent DB backup + exact
before values, UI-only release/restore of the fifth assigned test connector,
then release the five assigned connectors and restore all five at session end.
Exact private UI procedure recorded; next window explicitly approved privately.
Bench release/restoration and isolated hardware qualification have now started
under the owner's separately expanded window. Exact settings and evidence remain
private; no hardware gate completion is implied. Abort/monitoring/protected-output
limits remain unchanged. Phase C fakes authorized in order P2-23,22,20,21,24; hardware
Gate B must close before Gate C can pass. Existing PRs #66–72 remain untouched.
The137GB storm budget above is historical/rejected; replacement10GB budget and
free-space reserve are tracked separately, not amended into the frozen branches.

Before each session, propose start time/duration and obtain window approval. Only
dedicated synthetic test sources; no on-air pulls. Minimum two seconds per connector
between start/stop cycles; 500 cycles in 100-cycle batches with checks between them.
Monitor production route states, Windows events and host CPU throughout. Abort on
any production state change, DeckLink/driver error, or CPU above 70%. Verify release
state and untouched production outputs at the end. An initial short bench window
and dedicated test-stream reuse were approved. A consistent backup and UI-only
release/restore rehearsal are recorded privately. That rehearsal verifies saved
settings and process ownership, not physical picture/audio on an unobserved port.
The first repeated-cycle session has ended and all five saved assignments and
port overrides were compared equal to their original private snapshot. No
production application/service replacement or restart occurred. The faithful
configuration cohort completed 500 stops (100 live, 400 standby): zero forced
terminations, all exit codes zero, nearest-rank p99 1199.4283 ms, maximum
1265.5513 ms. The tested stop budget remains 5 seconds; this is margin over one
measured workload, not proof for all workloads.

One verified output/input loopback produced 100 tone captures and 100 bit-exact
silent standby captures. Six representative captures were independently checked
for stereo tone/silence, image and interlaced mode. This does not qualify the
other physical connectors. The 2,272 monitor samples recorded maximum host CPU
37%, no reported driver errors, unchanged production service PID and no protected
output owners. No bench media owners remained at session end. The actual
production data-volume free space is recorded only in the private gate report.

Earlier failed experiments remain disclosed: a monitor file-sharing abort and a
publisher reader sharing failure were corrected in private bench tooling. One
earlier receiver stop was forced after its harness omitted the production input
timeout. It is not included in the faithful-configuration cohort and is not
discarded evidence. The dedicated publisher subsequently exited zero; its
wrapper reported the intentional bench-stop exception. Private evidence contains
the individual measurements and all failed runs.

Hardware Gate B remains OPEN: standby busy/unavailable matrix and historical
failure-code root cause remain unproven. No 72-hour soak, all-port qualification,
full-service Session 0 qualification or reboot test is claimed. Later-phase
native-runtime and healthy-fixture requirements remain open.
