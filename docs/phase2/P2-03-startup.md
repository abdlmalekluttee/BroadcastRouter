# P2-03 startup persistence correction

Base: owner-merged main `ac0dbae`. Scope: one startup comparison, no recovery
threshold changes and no production or hardware access.

## Root cause

Startup compared a clock-rebased route after migration/reset against the original
UTC record. Even a one-tick boundary offset made an otherwise unchanged automatic
route unequal and caused a SQLite upsert on every startup. Compare against the
rebased input instead, so both operands have the same clock basis. Real legacy
assignment migration and transient-state resets still persist.

The reported unnecessary writes are reproduced. **Extra route-history rows from
timestamp rebasing alone are not reproduced:** SaveRouteAsync inserts history only
when the previous and new route states differ. The regression checks both writes
and history, rather than asserting the suspected history amplification as fact.

## Red/green evidence

`StartupRoutePersistenceTests` executes the real coordinator startup with a
temporary SQLite store and injected clock. A test logger stops execution at the
Host-started boundary, before all network/hardware/reconciliation loops. A database
trigger counts actual UPDATEs rather than relying only on timestamp round-trips.

- First commit: three unchanged-route cases fail with expected writes 0, actual 2
  across two loads (offsets +1 tick, -1 tick, +10 seconds).
- Genuine manual legacy assignment migration: correct desired output retained,
  transient owner/frame/deadline removed, one write and one state-history row.
- Implementation: retain the rebased record as the comparison baseline.
- Full main suite: 187 passed, 0 failed, 0 skipped (15 seconds).
- Strict KnownDefect: all seven exact expected failures; all seven remain OPEN.
- CI validator and provenance tests: 12 + 12 passed.
- Fresh independent reviewer: no Critical/Important/Minor findings; approved the
  narrow correction subject to required hosted CI and owner-only merge. Reviewer
  independently ran startup 4/4, main 187/187, and strict KnownDefect checks.

Review rulings: future Phase B implementations, deferred Gate A work, physical
qualification and deployment are outside this correction, and remain required
under their own gates. Reviewer used existing binaries; implementer ran fresh
build/tests. No deferred minor findings.

Existing saved-assignment startup reset policy is deliberately unchanged. This
fix does not claim to eliminate writes caused by genuine reset-policy changes.
