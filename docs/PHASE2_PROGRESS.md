# Phase 2 progress

Production is read-only. No hardware tests in Phase A. Only the repository owner
merges PRs; the agent never merges, force-pushes, or pushes directly to main.
Deployment requires separate explicit approval and a maintenance window.

| Task | Findings | Branch | PR | Status | Evidence link |
|---|---|---|---|---|---|
| P2-01 | R16, R23 | fix/P2-01-repo-provenance | Pending | In progress | Tests and provenance evidence to follow |
| P2-02 | R04, R05, R06, R07, R18; harness | fix/P2-02-test-harness | Pending | Not started | Baseline: 136/136 tests |
| P2-03 | R10, R11 | fix/P2-03-session-telemetry | Pending | Not started | Pending |

## Open KnownDefect register

These are uncorrected findings, never passing fixes. P2-02 adds correctness
assertions tagged KnownDefect and finding ID. Strict CI requires each exact
assertion failure; a different failure, crash, compile error, missing test, or
unexpected pass fails CI. A fixing PR removes its entry and promotes the test
to the normal suite in the same PR.

| Planned test | Finding | Status |
|---|---|---|
| SamePacketAudioBurstDoesNotAuthorizeRecovery | R04 | Open; test migration pending |
| MillisecondQueueWarningsDoNotAuthorizeRecovery | R04 | Open; test migration pending |
| DuplicateBurstRespectsConfirmationWindow | R05 | Open; test migration pending |
| FreshProgressDoesNotConcealStalledMedia | R06 | Open; test migration pending |
| MissingIncomingStreamsIsUnknown | R07 | Open; test migration pending |
| DiagnosticActorIsRedacted | R18 | Open; test migration pending |
| DiagnosticReasonIsRedacted | R18 | Open; test migration pending |

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
