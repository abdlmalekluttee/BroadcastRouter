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
- Full suite, strict KnownDefect and independent-review results follow in the PR.

Existing saved-assignment startup reset policy is deliberately unchanged. This
fix does not claim to eliminate writes caused by genuine reset-policy changes.
