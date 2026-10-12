# P2-01 follow-up — isolated branch-protection rejection proof

Executed 2026-10-12, beginning 08:30:33 UTC, authenticated as repository owner
`abdlmalekluttee`. No token/credential is included in this record. Main was never
the target of either negative test. No agent merge was performed.

## Test construction

- Main baseline: `ac0dbae89cf64e8b73ce7c9e6b1ce9f76e437d9d`.
- Created temporary `protection-test/phase-a-20261012` at that baseline.
- Created active ruleset 24916178 for `refs/heads/protection-test/*`, copying
  main's rules with no bypass actors (`current_user_can_bypass: never`).
- Rules: PR required, 0 approvals, conversation resolution, linear history,
  deletion prohibited, force push prohibited, up-to-date required checks
  `Build`, `Main suite`, `KnownDefect` bound to GitHub Actions (integration 15368).
- Zero approvals is deliberate while the agent uses the owner's account. The
  owner alone merges. Bot/CODEOWNERS/one-approval follow-up remains pending.

## Observed rejection, not inferred protection

| Attempt | Candidate | Exit | Server result |
|---|---|---|---|
| Direct fast-forward push | `d12be2e637de3a227f2117065158ebf0ca1c1df7` | 1 | GH013; changes must go through a PR; 3 required status checks expected |
| Force push backward | `ad8e2dcb7e0fc7b6cf4819394998b84c27d5cb79` | 1 | GH013; cannot force-push; PR required; 3 required status checks expected |

The temporary ref was verified unchanged after both rejections. The temporary
ruleset and test branch deletion calls succeeded. A separate read-only follow-up
on 2026-10-12 verified both API reads returned HTTP 404 and `git ls-remote` listed
no matching branch. It also compared the test rules' configured requirements
against current main protection and found parity.

The original script compared main protection before/after in memory and recorded
`mainProtectionUnchanged: true`; it did **not** retain those two raw snapshots.
That historical comparison is recorded evidence, not independently reconstructable
raw-snapshot proof. The follow-up retains current main protection separately and
does not retroactively establish the missing historical snapshots.

The full command outputs and ruleset snapshot are retained privately as
`protection-proof.json` and `protection-cleanup-verification.json`; the public
summary contains no operational server data.
These were actual negative GitHub tests, not a claim that a local static test
proves server enforcement. This documentation-only PR records the owner's
requested Gate A follow-up; no runtime, CI policy or recovery behavior changes.

## Remaining limits

Administrative owners can edit repository rules. Human-only merging is still a
procedural constraint, not something these rejection tests can enforce against
an owner deliberately changing protection. The separate bot account is not yet
available. Runner immutability remains the explicitly accepted risk: hosted
windows-2022, recorded ImageVersion, and identical two-clean-build payload hashes,
not pinned immutable images or self-hosted infrastructure.

Independent review found no Critical issue and one Important evidence overclaim
(absence read-back), corrected above with actual read-only checks. The historical
snapshot limitation remains explicit. No runtime tests are implied by this
documentation review; hosted PR checks still apply. Exclusions were GitHub
mutations, production/hardware access, runtime tests and merge; none was needed
to review the retained proof. No operational permission is expanded by this PR.
