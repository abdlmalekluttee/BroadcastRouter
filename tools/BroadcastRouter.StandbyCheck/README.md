# Local standby qualification

This source-built .NET 8 Windows tool is a **local software measurement tool**,
not a DeckLink bench runner. It accepts only five or eight synthetic outputs and
a null sink. It never exposes a hardware destination, network publisher, shell
command, or arbitrary FFmpeg argument option. `capture` is reserved by the contract
but deliberately refused by this executable; bounded media capture is separate.

Build with `dotnet build tools/BroadcastRouter.StandbyCheck -c Release`.
Run the generated executable with:

```text
--ffmpeg <local-ffmpeg.exe> --concurrency 5 --sink null
--variant production --ownership supervisor --iterations 10
--logo <local-logo.png> --evidence-root <new-private-directory>
```

Variants: `production` (original composed graph), `static` (baked slate plus a
blinking liveness indicator), `static-clock` (baked slate plus live clock/date).
Ownership: `direct`, `contained`, or `supervisor` (actual controller/supervisor).
An optional private `--configuration` JSON supplies `Port`, `Preset`, and
`Configuration`, plus `ExpectedInputGraph` copied from the original command.
The original variant refuses an input mismatch even when `filter_complex`
matches. Only the local logo path and output sink are substituted.
Keep operational configuration, logos and raw arguments private.

Each group waits for 50 output frames, measures a ten-second steady interval,
checks continuing advancement, then stops every owner from one barrier.
CPU samples refresh Windows process data. Core-equivalent CPU is process CPU
seconds divided by wall seconds; divide by logical processor count for host %.
Preparation time is separate. Direct/contained receipts include q delivery,
exit, reader drain and disposed-handle completion. Supervisor receipts include
the complete `StopAsync` boundary, lifecycle events and snapshots, **not** internal
q/reader timing or full stderr; no telemetry disk guard is overridden.

Every predeclared group is retained. A clean but over-one-second group records a
target failure and makes the executable return failure after the cohort. An
incomplete/operational failure aborts the cohort; it is never silently retried.
Existing evidence directories are refused. No result proves physical picture,
SDI silence, driver shutdown, Session 0 behavior or a production-storage soak.
