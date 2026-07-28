# Production validation checklist

Lab validation authenticated to a Wowza server, discovered an active publisher, received RTSP frames, enumerated DeckLink output subdevices, and completed bounded route/preview checks. Environment-specific addresses, credentials, device identifiers, labels, and inventory counts are intentionally excluded from this public document. Physical SDI picture and embedded audio must still be observed at the connected destination.

The live preview check can be repeated with `BroadcastRouter.Verify --database <database> --ffprobe <ffprobe.exe> --preview-seconds 8`. It requires an interactive Windows logon and a successfully probed active publisher; the duration is bounded to 30 seconds.

Before broadcast use, verify:

- FFmpeg/FFprobe versions, DeckLink compilation, RTSP demuxer timeout support, route filters, `drawtext`/`overlay`/`smptebars`/`smptehdbars`/`testsrc2` standby filters, `uyvy422`, rawvideo, and every enumerated output pass in Media Tools.
- The Sources page plays a 720×450 embedded preview; video, muted-autoplay/unmute, stereo confidence audio, peak/dB VU overlay, browser-disconnect cleanup, and repeated source switching work without orphan processes.
- Every supported output reports a unique `BMDDeckLinkPersistentID`; verify all detected IDs are unique, each device group contains the expected subdevices for that model, and both physical-card and connector operator names remain attached after swapping identical cards between PCIe slots.
- Power-cycle and reboot twice, then confirm persistent IDs, output labels, fixed rules, desired routes, and connector pictures do not change. The topological IDs may change and must never be treated as saved identity.
- 1080p25, 1080p50, 1080i50, and 720p50 presets play for at least 30 minutes on every compatible port with embedded audio checked. Confirm 1080i50 is top-field-first at 25 frames/50 fields per second and audio is 48 kHz stereo PCM at the DeckLink output.
- Two concurrent route-start requests never obtain the same port; locked routes retain ownership through publisher and Wowza-API outages.
- Mark one connector as input-only and confirm it is absent from manual, rule, and automatic selectors. Attempt a stale fixed-port save and confirm validation rejects it.
- Save conflicting preconfigured and manual entries for one output. Confirm only the preconfigured entry owns the connector, the manual intent remains visible as a routing conflict, and an automatic stream never overrides either saved entry.
- Stop an assigned publisher and confirm it remains visible as Offline/Waiting for stream after repeated discovery polls and a host restart. Verify the saved output remains reserved unless temporary use is explicitly enabled.
- Toggle one publisher from OBS at least 100 times with short and irregular intervals. During the run, poll route supervision and output inventory: every marked output must keep the same persistent ID and output-port designation, saved assignments must remain present, standby must replace unavailable live output, and recovery must occur without a service restart.
- Open Settings in two browser sessions, save from the first, and confirm the stale second session is rejected with a backend-revision warning. Confirm the successful session displays the exact backend application timestamp and the audit table records actor, reason, previous/new state, card/port, stream context, and service status.
- Change an output label, standby pattern, logo, preset, and output-port designation while the service remains running. Confirm each accepted change is persisted before acknowledgement and applied immediately; no application, task, service, or Windows restart is allowed.
- On every marked output, physically verify its selected bars, logo, card/connector name, custom label, silent audio, and `HH:mm:ss` clock. Compare the clock with the station NTP reference, then verify live → standby → live transitions without overlap or an orphaned FFmpeg process.
- Wowza connection tests report authentication, applications, active streams, and successful RTSP frame receipt for each monitored application/instance.
- Validate one audio-only input and one audio-led input whose video frames are sparse. Confirm metadata alone is rejected, counted live audio becomes Ready, generated black remains continuous at the selected preset, embedded audio is 48 kHz stereo, and stopping the audio publisher terminates/retries the owned route rather than leaving black output running indefinitely.
- Pull network, stop the publisher, kill FFmpeg, create an RTSP stall, mark a port busy, disconnect a card if safe, and verify classification, queueing, standby, backoff and recovery.
- For each reference-capable output, test reference locked, reference removed, and reference restored. Confirm the UI reports genlock/reference separately from source readiness; logs distinguish no decoded frames, unsupported mode, DeckLink initialization/header failure, process failure, and reference loss; a temporary reference condition must not clear saved assignment or output-port configuration.
- Restart the application and Windows while routes are desired; confirm stale process/lease fields are cleared, leases rebuild preconfigured → manual → automatic, no connector has duplicate ownership, and all primary routes recover.
- Run an 8–24 hour multi-port soak while watching frames, FPS, speed, drops, CPU, memory and orphan processes.
- Repeat the soak with preview running and stopped to quantify its separate CPU/GPU cost and confirm it does not disturb DeckLink route progress.
- Test emergency stop and confirm every owned FFmpeg process exits; confirm unrelated FFmpeg processes are untouched.
- If trialing Windows Service mode, repeat the complete matrix in Session 0. Do not use service mode if results differ from interactive logon.
- Verify LAN firewall/CIDR restrictions, exact reverse-proxy trust, spoofed forwarded-header rejection, HTTPS certificate trust, administrator controls, read-only operator behavior, login throttling, session timeout, backup and rollback.
