# Isolated hardware bench: exact output launch guard implementation plan

> For agentic workers: use the executing-plans and test-driven-development skills
> to implement this approved bench-safety task inline, with one fresh final review.

Goal: make the approved bench candidate refuse any output outside an immutable,
explicit five-connector allowlist, and refuse startup while production still owns
any of those connectors. This is a safety prerequisite, not hardware qualification.

Architecture: an optional, startup-only bench policy is independent of editable
operator settings. The FFmpeg supervisor checks identity and the actual output
argument at the final launch boundary for live, fallback and standby. Startup
preflight reads the production SQLite database with a read-only connection and
reads Windows process command lines without opening DeckLink hardware. Ambiguous,
incomplete or failed reads refuse startup; no defaults or cached release proof.

Tech stack: .NET 8, xUnit, Microsoft.Data.Sqlite, contained read-only Windows CIM helper.
Spec: owner's Phase B/C shared-bench constraints and 13 October guard ruling.

## Global constraints

- No production binary/configuration/service/driver changes in this task.
- Hardware calls are prohibited during fake tests. All fixtures use synthetic IDs.
- Bench startup requires a separate application/data directory and loopback HTTP.
- The allowlist pins stable ID, opaque FFmpeg device handle, card group and connector.
  Display names, order in discovery and approximate/substring matches grant no access.
- Preflight requires all five output selections off, no assigned/saved routes, and
  no process targeting any of their handles. Unreadable process evidence fails closed.
- Startup proof is not an atomic cross-application hardware lease. The independent
  session monitor remains mandatory; production must not reclaim a test connector.
- Credentials, addresses, real handles, source names and window details stay private.
- Existing media behavior and production defaults remain unchanged. Owner merges.

## Review focus

1. Refuse a same-model display-name alias or a sixth connector in every launch path.
2. Refuse a valid stable ID paired with a changed handle, group or connector.
3. Refuse missing/ambiguous production identity, output enabled, route or old process.
4. Refuse absent/corrupt/locked preflight data without initializing or repairing it.
5. Prevent supervisor recreation and settings edits from dropping the immutable guard.

## Execution

Files/interfaces: `BenchOutputGuard.ValidateLaunch(DeckLinkPort?, ProcessStartInfo)`
in Infrastructure is consumed by `FfmpegProcessSupervisor.StartProcessAsync`;
`BenchReleasePreflight.EnsureReleasedAsync` consumes that immutable identity list
and a process-reader delegate. `BenchModeStartup.LoadAndVerifyAsync` produces the
guard for Program DI and every `RouterCoordinator` supervisor generation. Tests
are the matching three `Bench*Tests.cs` classes. No DB schema migration.

1. Record green baseline. Add negative supervisor and read-only preflight tests;
   observe assertion failures (minimal compile scaffolding only).
2. Implement exact guard, all four supervisor start paths and final argv validation.
3. Implement bounded read-only release preflight and isolated startup wiring.
4. Prove allowed launches with fake FFmpeg, rejected launches without any child,
   unchanged production database, immutable policy and settings-generation wiring.
5. Run all normal shards, strict KnownDefect checks and Release build; one fresh-eyes
   review. Publish a single PR against main with red/green evidence and limitations.
6. Separately prepare private session instructions/evidence. Show guard results before
   hardware; enforce the approved readiness cutoff and restoration margin. No soak.

Phase D follows in separate test-first PRs (P2-30, P2-31, P2-32, P2-33); the telemetry
flush timeout must be reproduced and explained in P2-31. R18 remains open for Phase G.
