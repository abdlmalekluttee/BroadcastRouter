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
ruleset and test branch were then deleted, each verified absent. Main protection
was compared before/after and was unchanged. Thus cleanup did not weaken main.

The full command outputs and ruleset snapshot are retained privately as
`protection-proof.json`; the public summary contains no operational server data.
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
