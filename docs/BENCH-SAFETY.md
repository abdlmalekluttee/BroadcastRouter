# Shared-machine bench safety

Bench qualification is not a deployment. Never use a production database as the
candidate's writable data directory, overwrite the production application or media
tools, or infer permission to use a connector from an empty UI row.

## Explicit candidate mode

Start a separately published candidate with both absolute arguments:

```powershell
& 'C:\IsolatedBench\candidate\BroadcastRouter.Server.exe' `
  --Bench:PolicyFile 'C:\IsolatedBench\candidate\bench-policy.json' `
  --DataDirectory 'C:\IsolatedBench\candidate\data'
```

The private policy contains `productionApplicationDirectory`, `productionDatabase`
and `outputs`. Each of the **five** `outputs` entries pins `connector` (1–5),
`stableId`, `deviceHandle`, `deviceGroupId`, and zero-based `subdeviceIndex`.
Populate these from fresh, verified hardware identity evidence, never a model name
or a computed handle based on display order. Do not publish the operational policy.

Candidate application and data must be separate from production. Data and policy
must be inside the candidate folder; reparse points are rejected. The candidate
listener must use a literal loopback address and a separate unused HTTP port. Start
with a clean candidate database containing only the dedicated approved test source.
No production source inventory, credentials or database copy belongs in that runtime.

At startup, before opening the candidate database or starting routing:

1. Read production's valid active settings, port inventory and routes through a
   read-only SQLite connection. No initialization, migrations or fallback defaults.
2. Require explicit disabled output selections for all five matching identities and
   no current or saved assignment on those connectors.
3. Collect current Windows process command lines using a hidden, contained, bounded
   read-only helper. Refuse matching handles, unreadable FFmpeg owners, or ambiguous
   DeckLink name-based output owners. Protected outputs must use their known handles.
4. Re-read the production state; any conflicting selection/route or settings change
   rejects startup. Missing, corrupt, locked or incomplete evidence rejects startup.

Every candidate supervisor launch (live, fallback, recovery standby and idle standby)
checks the immutable allowlist and actual tokenized command. A settings edit or
supervisor configuration change cannot widen it. There must be exactly one DeckLink
output and its final target must be the same exact approved opaque handle. Unknown
identity, display aliases, other connectors and shell/raw-argument launches are refused.

## Limits of the proof

This guard is an accidental-misrouting safety interlock, **not** an OS security
boundary against an administrator, an atomic lease shared with production, or proof
that an unrelated native application has not opened a device. It does not replace
the authorized session window, consistent backup, UI release/restore rehearsal,
continuous protected-output/CPU/event monitoring, independent hardware-busy check,
or physical picture/audio measurement. Do not use older direct-launch bench runners
as a substitute for the guarded candidate.

If production reclaims a connector after startup, or the monitor sees a protected
output change, a driver error or a CPU-limit breach, abort the session and restore
the authorized connectors through the production UI. Never kill processes by name.
Readiness results must be shown before the authorized window; a missed cutoff means
cancel/reschedule, not running late. The owner alone approves merges and deployment.
