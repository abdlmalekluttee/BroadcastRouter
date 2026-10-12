# Phase 2 progress

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
| P2-12 | R08 | fix/P2-12-publisher-epochs | Pending | Independent software review approved; main 225/225; physical qualification pending | [Epoch evidence](phase2/P2-12.md) |

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

Only the specifically assigned idle test connectors may be released through the
application and tested in an owner-approved window. No port carrying a route,
other connector, production binary, media-tool installation, service, driver,
profile or Wowza configuration may be changed. Bench artifacts stay in a separate
folder. Exact operational before/after configuration and a one-step rollback must
be recorded in a PRIVATE copy of this progress document and shown to the owner
before applying the release; public git never contains operational identifiers.

Before each session, propose start time/duration and obtain window approval. Only
dedicated synthetic test sources; no on-air pulls. Minimum two seconds per connector
between start/stop cycles; 500 cycles in 100-cycle batches with checks between them.
Monitor production route states, Windows events and host CPU throughout. Abort on
any production state change, DeckLink/driver error, or CPU above 70%. Verify release
state and untouched production outputs at the end. No bench session authorized yet.
