# Migration from batch files

1. Inventory every existing batch route: RTSP URL, DeckLink name/connector, raster/rate/scan, pixel format, audio, transport, buffer and fallback expectation.
2. Install BroadcastRouter in simulation and model those settings as output presets. The sample batch maps to a 1080p25 preset with TCP as desired, 256 MB input buffer, `uyvy422`, and the labeled DeckLink port.
3. Configure Wowza discovery and an RTSP URL template instead of copying credentials/URLs into commands. Use manual RTSP sources only for inputs not discoverable through Wowza.
4. Rescan real hardware and confirm each supported connector reports a Blackmagic persistent hardware ID. Give each physical card an operator name such as `Studio input card`, label its connectors once, and mark only the real playout connectors as **Output ports**. Selectors will show `Studio input card / Output 1` while input-only connectors remain unavailable to routing. Configure each output's standby bars/logo/label/clock, then save permanent batch wiring as preconfigured routes; use manual entries for operator choices and automatic rules/groups only for remaining streams. Legacy FFmpeg-hash mappings are migrated during a controlled host start; do not delete the pre-migration backup until connector verification passes.
5. Stop one batch process, start the equivalent BroadcastRouter route, verify video/audio at the destination, then migrate the next port. Never run the batch file and BroadcastRouter against the same output.
   For an audio-led batch input with absent or sparse pictures, select an audio-enabled preset. BroadcastRouter generates continuous black video and maps the live audio; verify that loss of the audio publisher causes normal route recovery rather than indefinite black output.
6. After all ports pass soak testing, disable batch-file startup tasks. Retain them offline for rollback until the new system completes the agreed acceptance period.

Version 1.3.3 advances operator settings to schema 6 and adds a monotonic configuration revision plus last-applied metadata. The SQLite audit table is created automatically. Existing settings, output-port selections, identities, aliases, routes, and source inventory remain compatible; no manual migration or restart is required beyond the normal version deployment itself.

Version 1.3.4 does not change the settings or database schema. Existing output-port selections, stable DeckLink identities, saved routes, source inventory, and standby configuration are reused unchanged. The normal version deployment restart is sufficient.

Version 1.3.5 is a cache-busting maintenance release and does not change the settings or database schema. Use a clean versioned directory as described in the deployment guide; browsers that previously loaded 1.3.4 will request the corrected stylesheet automatically.

Version 1.3.6 changes presentation only and does not change the settings or database schema. Saved assignments and route state are unchanged; the management UI now presents availability, waiting, and assignment identity as independent color-coded badges.

Version 1.4.0 does not change the settings or database schema. Existing output designations, saved routes, aliases, presets, and standby configuration are preserved. Standby rendering adopts the new four-corner layout automatically, and low-latency presets use faster RTSP input analysis. Verify the host clock is NTP-synchronized and set source encoder keyframe intervals to two seconds or less before timing physical SDI cuts.

Version 1.5.0 does not change the settings or database schema. The optional DeckLink visual catalog reads an operator-installed pack from `data\decklink-assets`; public archives do not redistribute Blackmagic-owned images. Existing card identities, connector roles, saved routes, presets, and standby processes are independent of the catalog and remain unchanged when the pack is installed, replaced, missing, or unmatched.

Version 1.5.1 does not change the settings or database schema. It corrects DeckLink image rendering and physical-card responsiveness, and adds read-only Desktop Video installation/update metadata. Driver lookup and the bounded official update check are presentation-only; a failed or blocked check displays `Not Available` and cannot affect card discovery, output ownership, or playout.

Version 1.5.2 does not change the settings or database schema. It serializes FFmpeg stop/restart ownership through full process exit, makes standby audio explicitly silent, adds exact PID lifecycle telemetry, and introduces the automatic credential-aware Windows Service installer. Create a cold backup, disable the old interactive-logon task, and install the service under the same DPAPI account. Reboot and repeat the Session 0/DeckLink validation matrix before broadcast acceptance.

Version 1.5.3 does not change the database schema. Existing settings receive an eight-second frozen-input watchdog default during JSON deserialization. Source refresh is now independent from hardware validation, and stale live-video decoders are recycled without changing saved assignments, leases, or output-port designations. Verify the operator-selected timeout, exercise rapid publisher stop/start, and confirm audio-led sources remain stable before broadcast acceptance.

Version 1.5.4 does not change the settings or database schema. Native process shutdown and diagnostic cleanup now have finite reaping/draining limits, and an independent two-minute coordinator watchdog relies on the existing Windows Service recovery configuration. Confirm the service recovery actions remain configured, monitor `/health`, and perform one controlled watchdog/restart drill before broadcast acceptance.

Version 1.5.5 does not change the settings or database schema. DeckLink identity/reference polling now runs in a bounded helper process. No operator action is required; verify that reference status remains visible and that a forced helper timeout produces one warning without changing port identity or stopping coordinator cycles.

Version 1.5.6 does not change the settings or database schema. Ambiguous audio/video sources receive a bounded extended keyframe-acquisition probe, and a fatal RTSP `CSeq` desynchronization recycles only the affected owned route process while retaining saved intent and output ownership. Validate one long-GOP source plus one genuinely audio-led source after deployment.

Version 1.5.7 does not change the settings or database schema. It completes RTSP recovery by distinguishing fallback and live process purpose, reaping fallback ownership before live restart, and isolating per-route supervision failures. Existing source inventory, output designations, aliases, standby settings, and saved routes are reused unchanged. Validate one controlled RTSP interruption and confirm the route progresses `Fallback` → `Reserved` → `Starting` → `Running` without a service restart.

Version 1.5.8 does not change the settings or database schema. It routes an active saved entry through retry/fallback when its live process disappears before reconciliation, preventing a direct `Running` → `Reserved` transition. Repeat the controlled owned-PID termination drill and require zero invalid-transition or duplicate-owner errors.

Version 1.5.9 does not change the settings or database schema. It adds fast, route-scoped recovery when a live FFmpeg process reports paired DeckLink video/audio starvation after a rapid upstream reset. Repeat the rapid `resetStream` drill against one controlled source, require the configured silent per-port standby during recovery, and verify the exact old PID exits before replacement live playout begins.

Version 1.5.10 does not change the settings or database schema. It closes the production gap where DeckLink video/audio starvation warnings arrived outside the 1.5.9 pairing window and the live retry waited behind long FFprobe work. Repeat the drill and require either post-startup starvation warning to trigger exact-PID recovery, with the replacement live process launched from the 250 ms supervision path after its retry delay.

Version 1.5.11 adds `Routing.InputReadTimeoutMilliseconds`; existing settings receive the 1000 ms default during JSON deserialization and no database migration is required. Values from 500 through 30000 ms are accepted. Re-run the rapid reset drill and confirm saved routes retry their known RTSP URI without waiting for a later discovery cycle, while unsaved automatic routes still require a confirmed ready source.

Version 1.5.14 requires no settings or database migration. It retains the supported RTSP timeout and saved-route retry behavior from 1.5.13, then adds a 100 ms Wowza publisher-presence monitor that requires two consecutive authoritative disconnected/missing observations. Management REST failures are ignored for route health. Repeat the controlled reset and reject the release if the old live PID survives beyond one second, the route becomes `WaitingForStream`, or recovery waits for FFprobe/discovery.

Version 1.5.15 requires no settings or database migration. It keeps the fast publisher monitor active for saved routes while standby is on air and consumes a confirmed publisher-return transition to wake the reserved route immediately. Repeat at least three controlled rapid resets and require exact-owner cleanup, zero orphan processes, stable service PID, immediate standby, and automatic live restoration without waiting for the normal discovery probe.

Version 1.5.16 requires no settings or database migration. A returned publisher retains `Ready` only when the source already has validated media metadata; an unknown source remains probe-gated. This prevents normal reconciliation from recycling the first accelerated replacement while preserving validation for new inputs. Repeat the controlled rapid-reset matrix and require the first replacement PID to remain stable after it begins producing media.

Example equivalent FFmpeg arguments are generated internally as tokens. The operator never edits a shell command, credentials are not placed in command text, and uncompressed output does not use `-b:v`.
