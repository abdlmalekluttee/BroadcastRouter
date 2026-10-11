# Phase 2 progress

Production is read-only. No hardware tests in Phase A. Only the repository owner
merges PRs; the agent never merges, force-pushes, or pushes directly to main.
Deployment requires separate explicit approval and a maintenance window.

| Task | Findings | Branch | PR | Status | Evidence link |
|---|---|---|---|---|---|
| P2-01 | R16, R23 | fix/P2-01-repo-provenance | [#63](https://github.com/abdlmalekluttee/BroadcastRouter/pull/63) | Awaiting owner; partial acceptance | [P2-01 evidence](phase2/P2-01.md) |
| P2-02 | R04, R05, R06, R07, R18; harness | fix/P2-02-test-harness | [#64](https://github.com/abdlmalekluttee/BroadcastRouter/pull/64) | Draft; CI green; full matrix gap open | [P2-02 evidence](phase2/P2-02.md) |
| P2-03 | R10, R11 | fix/P2-03-session-telemetry | [#65](https://github.com/abdlmalekluttee/BroadcastRouter/pull/65) | Draft review approved; retention/full-transcript gaps open | [P2-03 evidence](phase2/P2-03.md) |

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
| FreshProgressDoesNotConcealStalledMedia | R06 | Open; exact assertion failure required by CI |
| MissingIncomingStreamsIsUnknown | R07 | Open; exact assertion failure required by CI |
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
- Gate A is NOT passed. No physical stability or media qualification is claimed.

## Gate A acceptance gaps

- P2-01 pins the hosted runner OS family, not an immutable VM image. Direct-push
  rejection was not tested by sending a real main update: doing so risks violating
  the no-direct-push rule if protection is wrong. Protection was read back instead.
- P2-01's required KnownDefect check fails closed until P2-02 supplies the harness.
  The owner must decide the stacked-PR bootstrap order; do not waive protection.
- P2-02's full end-to-end acceptance matrix is not implemented; see its coverage table.
- P2-03 demonstrates specific injected-fault transcripts, not every policy path;
  bounded file retention can evict evidence before 30 days. No retention soak is claimed.
- These are draft task PRs, not merge/release readiness. Production stays unchanged.
