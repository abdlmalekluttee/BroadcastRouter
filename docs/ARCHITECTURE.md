# Architecture

## Deployment shape

`BroadcastRouter.Server.exe` is the only application process. ASP.NET Core owns Blazor Server, SignalR, hosted discovery/reconciliation workers, SQLite, and all child FFmpeg processes. Routes therefore survive browser refreshes and browser closure.

The executable supports automatic Windows Service hosting under the same dedicated broadcast account used to create DPAPI credentials. The host and every media child run silently in Session 0 without a user logon. Service recovery is configured by the installer, while physical DeckLink behavior must still pass the production card/driver matrix on each target machine before that machine is certified.

## Routing flow

1. Poll each enabled Wowza v2 management endpoint; manual RTSP sources are explicit and no network scanning occurs.
2. Canonicalize the source as `server/application/instance/stream`, persist the source inventory, and retain active and offline sources plus healthy routes during management-API outages.
3. Render a validated RTSP template and run bounded, concurrency-limited FFprobe validation. Normal video requires decoded/read video frames. An audio-led input is eligible only when FFprobe counts real audio packets or frames; codec metadata alone remains insufficient. Two consecutive sustained-video observations are required before an audio-led publisher returns to decoded-video mode.
4. Evaluate ordered wildcard or `regex:` rules; regex execution has a 100 ms timeout. An administrator may explicitly choose any saved preset for manual creation or confirmed reassignment; stale preset IDs are rejected before the current route is stopped.
5. Reconcile saved entries first in preconfigured → manual priority order. Saved entries remain valid while offline and reserve their desired connector unless temporary use is explicitly enabled.
6. Select a preset and compatible port group for remaining automatic sources, considering only connectors explicitly marked as output ports and excluding reserved/protected ports.
7. Acquire the only atomic port lease. A second source cannot own the same stable port ID.
8. Refuse real start unless Media Tools validation passes; launch FFmpeg using `ProcessStartInfo.ArgumentList`. RTSP socket I/O uses the demuxer-specific bounded `-timeout` option. Low-latency presets also use no-buffer input, a one-second analysis/probe budget, and zero frame-rate probing before input open. DeckLink audio is normalized to 48 kHz stereo PCM; interlaced presets explicitly produce 50 fields/s with top-field-first metadata. Audio-led inputs receive continuous preset-matched black video, and `-shortest` ends playout if the verified live audio ends.
9. Parse `-progress pipe:1`, drain stderr, enforce a first-progress deadline, detect later progress stalls, and detect live-video decoder sessions whose advancing output is almost entirely duplicated frames. Recreate only the affected owned process, classify exits, and retry with capped backoff plus jitter and an optional attempt limit. Audio-led generated-black routes are excluded from duplicate-frame detection. Process exits are observed before reconciliation so a failed start cannot be hidden by immediate recovery.
10. Under a per-port process gate, stop that connector's standby owner immediately before live FFmpeg starts. This on-air handoff has a dedicated 750 ms stop deadline; ordinary route shutdown retains the configurable maintenance deadline. Record elapsed time until the first DeckLink frame. When no live route owns a marked output, run its configured bars with card/SDI identity at the top, centered system time/date, the port label at the bottom, the configured logo in all four corners, and 48 kHz silent PCM.
11. Persist operator configuration before applying it, reject stale editor revisions, then return the confirmed backend revision and application time to the browser. Runtime-safe output, routing, preset, alias, and standby changes require no restart. Security listener changes remain the explicit exception.
12. Validate and persist every route transition. At restart, discard transient process/lease fields, rebuild leases from saved intent in priority order, and only then start playout. Failed process starts release their live lease; saved desired assignments remain visible and continue retrying.

## Main models

- `SourceIdentity`: immutable escaped four-part identity.
- `DiscoveredSource` / `MediaProperties`: discovery and independent media truth.
- `DeckLinkPort`: Blackmagic persistent ID when supported, opaque FFmpeg device handle for playout, explicit output-port designation, separately observed reference/genlock state, physical-card group/subdevice/topology hints, modes, group, reserved state, operator card/connector names, confidence, and legacy aliases used only for one-time migration. The physical-card alias is keyed by `DeviceGroupId`; connector aliases remain keyed by `StableId`.
- `DeckLinkAssetCatalog`: optional presentation-only metadata loaded from `data/decklink-assets/manifest.min.json`. Exact normalized SDK model-name matches expose validated, root-confined image paths through an authenticated endpoint. Missing, malformed, unmatched, or incomplete packs fail visually closed and cannot change discovery, reservations, settings, or FFmpeg arguments.
- `OutputPresetProfile`: raster, rational rate, scan, pixel format, audio, latency, buffer, standby behavior.
- `RoutingRuleProfile`: ordered source/media match and preset/group/fixed-port/priority/lock action.
- `RuntimeRoute`: persistent desired port/preset/mode and offline-reservation policy plus transient lease, state, process metrics, failure, and retry state.
- `OperatorSettings`: schema-versioned complete configuration with an optimistic revision and last-confirmed apply metadata; credential values remain DPAPI ciphertext.

## Reservation and restart design

`PortReservationManager` is the sole mutable owner map and serializes reserve/release operations. Preconfigured, manual, rule, and automatic routes use the same API. The coordinator reconciles saved assignments first by mode and stream priority; conflicts never mutate the lower-priority saved intent. On startup, saved intent is separated from stale runtime state, leases are rebuilt, conflicts are exposed, and only then are FFmpeg processes restored. A process-start exception atomically releases its live lease instead of leaving ghost ownership.

Per-port standby uses a synthetic, non-reversible owner identity and the same process supervisor/job containment as live routes. A connector-specific semaphore serializes standby stop, configuration replacement, live shutdown, and live start. The process supervisor also serializes by logical owner and retains its registry entry until the exact child has exited, stdout/stderr have drained, and the process has been reaped. It first asks the exact standby child to quit and force-terminates only that owned tree if it has not released the device within 750 ms. A signature of the effective standby configuration causes an immediate guarded replacement when its preset, bars, logo, label, or clock settings change. Emergency stop and host shutdown stop both route and standby owners without enumerating or terminating unrelated FFmpeg processes.

DeckLink rediscovery never edits the persisted output-port designation. Connectors with live ownership are retained through a transient enumeration gap, and legacy-identity deferral occurs only while unresolved legacy references exist. Configuration changes, identity migrations, rediscovery, and service starts are written to a durable audit table with actor, reason, before/after state, backend status, stream, card, and connector context.

## Supervision

FFmpeg receives individually tokenized arguments and never a shell command. The command deliberately omits `-b:v` for uncompressed DeckLink output. Standby/fallback audio is generated as explicit zero-valued 48 kHz stereo PCM; live audio timestamps are normalized before DeckLink output. The supervisor captures stdout/stderr asynchronously, tracks PID, frame, FPS, speed, output time, drop/duplicate counters, last progress, uptime, exit code, and recent redacted errors. Graceful `q` shutdown is followed by process-tree termination after the configured deadline. Start, stop request, forced termination, and exit events are persisted with the exact PID. Media validation detects the optional patched `win_safe_terminate` DeckLink capability and enables it only for builds that advertise support. DeckLink initialization, header/buffer, and reference/genlock failures are classified separately from network failures. Reference status is observed independently and never changes source readiness or persistent port configuration. Saved assignments retry temporary output failures without losing intent.

Source polling runs before periodic media-tool validation. Automatic polling and operator-triggered refreshes share one discovery gate, preventing overlapping observations and stale probe commits. The Sources action runs discovery immediately without forcing a DeckLink rescan, waits for the backend result, and records request/completion telemetry. Failed Wowza polls retain the last healthy observations and routes; the next scheduled or manual poll can recover without a host restart.

The coordinator records stage-level progress around supervision, discovery/probing, media-tool validation, routing, and standby work. Native child shutdown, forced reaping, and redirected-stream draining have independent finite deadlines. A separate hosted watchdog is not dependent on the coordinator task: after two minutes without progress it records the stalled stage and fails the host so Windows Service recovery restarts the application. The health endpoint reports `degraded` during the stale interval even if SQLite remains healthy. Process Job containment limits crash cleanup to application-owned children.

DeckLink SDK identity and reference-lock queries are never executed in the service host. A hidden copy of the server executable performs the COM enumeration, returns bounded JSON over redirected output, and exits. The caller enforces a five-second deadline in a kill-on-close Job Object; timeout retains the last confirmed hardware state and delays the next attempt for 30 seconds without blocking coordinator progress.

## Embedded browser preview

Preview is an administrator-only, application-owned operation and is independent of DeckLink route ownership. One preview may run at a time. A tokenized FFmpeg producer opens the selected RTSP source, scales it into a compact 720×450 canvas, renders a real `showvolume` peak/dB audio meter when audio is present, and emits fragmented H.264/AAC MP4 directly to an authenticated browser endpoint. A random per-session token prevents stale players from attaching to a replacement preview, and the endpoint is explicitly non-cacheable.

The server owns the exact FFmpeg process object, drains progress/errors, and stops that process when the operator stops preview, the player disconnects, the process exits, or the application shuts down. It never searches for or terminates unrelated FFmpeg processes. Audio-led preview uses generated black video under the live VU overlay so confidence audio remains observable without waiting for a sparse video frame. Browser preview does not require FFplay or an interactive desktop, but it is not a substitute for physical DeckLink confidence monitoring.

## Known production risks

- Older DeckLink devices may not expose `BMDDeckLinkPersistentID`. Those ports deliberately retain their legacy FFmpeg-handle ID with a visible lower-confidence warning. Migration is deferred only when unresolved legacy references and active ownership coexist.
- FFmpeg does not consistently expose output-mode tables for every driver/build. Known mode tables are checked before assignment; otherwise FFmpeg start is the final compatibility check and format errors become permanent.
- Wowza REST response shapes and permissions vary by version; validate each server with the connection test.
- Windows Service Session 0 behavior depends on Blackmagic driver/device profile and must be tested before service deployment.
- Freeze-frame standby requires a configured image. Automatic capture of the last decoded frame is not in this release.
- Browser preview adds a separate decode/encode workload. Validate CPU/GPU headroom during the multi-port soak and stop preview when it is not required.
- A live decoder still requires an encoder keyframe. Bound the source GOP/keyframe interval to two seconds or less; an arbitrarily long GOP can still delay the first picture even after the DeckLink handoff is complete.
- DeckLink product images and diagrams are third-party copyrighted material. The application supports an operator-supplied local pack, but the public project and release archives do not redistribute those files without separate permission.
