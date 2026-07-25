# Architecture

## Deployment shape

`BroadcastRouter.Server.exe` is the only application process. ASP.NET Core owns Blazor Server, SignalR, hosted discovery/reconciliation workers, SQLite, and all child FFmpeg processes. Routes therefore survive browser refreshes and browser closure.

The executable supports Windows Service hosting, but service/Session 0 DeckLink access is **not claimed to work** until it passes the production card/driver soak plan. The recommended first deployment starts the same server executable at logon under a dedicated broadcast Windows account, because DPAPI credentials and DeckLink device access then share that interactive user context.

## Routing flow

1. Poll each enabled Wowza v2 management endpoint; manual RTSP sources are explicit and no network scanning occurs.
2. Canonicalize the source as `server/application/instance/stream` and retain healthy routes during management-API outages.
3. Render a validated RTSP template and run bounded FFprobe frame validation.
4. Evaluate ordered wildcard or `regex:` rules; regex execution has a 100 ms timeout.
5. Select a preset and compatible port group, excluding reserved ports from automatic assignment.
6. Acquire the only atomic port lease. A second source cannot own the same stable port ID.
7. Refuse real start unless Media Tools validation passes; launch FFmpeg using `ProcessStartInfo.ArgumentList`.
8. Parse `-progress pipe:1`, drain stderr, enforce a first-progress deadline, detect later stalls, classify exits, and retry with capped backoff plus jitter and an optional attempt limit.
9. Retain the reservation through recovery. Preset standby can output black, SMPTE bars, an image, a looping media file, or a standby RTSP source.
10. Validate and persist every route transition. At restart, rebuild leases before starting any process and reconcile persisted routes. Failed process starts release their lease, while missing unlocked sources retain leases only for the configured grace period. Waiting routes retry in priority/FIFO sequence.

## Main models

- `SourceIdentity`: immutable escaped four-part identity.
- `DiscoveredSource` / `MediaProperties`: discovery and independent media truth.
- `DeckLinkPort`: stable ID, FFmpeg alias, topology hints, modes, group, reserved state, operator name, confidence.
- `OutputPresetProfile`: raster, rational rate, scan, pixel format, audio, latency, buffer, standby behavior.
- `RoutingRuleProfile`: ordered source/media match and preset/group/fixed-port/priority/lock action.
- `RuntimeRoute`: desired assignment, state, process metrics, failure, retry and lock state.
- `OperatorSettings`: versioned complete configuration; credential values remain DPAPI ciphertext.

## Reservation and restart design

`PortReservationManager` is the sole mutable owner map and serializes reserve/release operations. Fixed and manual routes use the same API as automatic rules. Locked leases require explicit unlock or forced emergency release. A process-start exception atomically releases its lease and records a failed route instead of leaving a ghost reservation. On startup, persisted active routes are sorted locked-first and priority-first, leases are rebuilt, duplicates are moved to the waiting queue, and only then are FFmpeg processes restored.

## Supervision

FFmpeg receives individually tokenized arguments and never a shell command. The command deliberately omits `-b:v` for uncompressed DeckLink output. The supervisor captures stdout/stderr asynchronously, tracks PID, frame, FPS, speed, output time, drop/duplicate counters, last progress, uptime, exit code, and recent redacted errors. Graceful `q` shutdown is followed by process-tree termination after the configured deadline. Permanent authentication/codec/format/configuration failures require operator action; network/process failures retry.

## Known production risks

- Identical DeckLink cards may expose only FFmpeg aliases; PCI/SDK-quality stable identity needs hardware verification and a future DeckLink SDK adapter.
- FFmpeg does not consistently expose output-mode tables for every driver/build. Known mode tables are checked before assignment; otherwise FFmpeg start is the final compatibility check and format errors become permanent.
- Wowza REST response shapes and permissions vary by version; validate each server with the connection test.
- Windows Service Session 0 behavior depends on Blackmagic driver/device profile and must be tested before service deployment.
- Freeze-frame standby requires a configured image. Automatic capture of the last decoded frame is not in this release.
- Low-resolution browser preview is intentionally omitted to protect decode/output capacity.
