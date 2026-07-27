# Production validation checklist

The July 27, 2026 host check authenticated to the configured Wowza server, discovered an active publisher, received RTSP frames, enumerated two DeckLink Quad 2 cards and sixteen output subdevices, and completed bounded route/preview checks. Physical SDI picture and embedded audio must still be observed at the connected destination.

The live preview check can be repeated with `BroadcastRouter.Verify --database <database> --ffprobe <ffprobe.exe> --preview-seconds 8`. It requires an interactive Windows logon and a successfully probed active publisher; the duration is bounded to 30 seconds.

Before broadcast use, verify:

- FFmpeg/FFprobe versions, DeckLink compilation, RTSP demuxer timeout support, `scale`/`fps`/`yadif`/`tinterlace`/`setfield`, `uyvy422`, rawvideo, and every enumerated output pass in Media Tools.
- The Sources page plays a 720×450 embedded preview; video, muted-autoplay/unmute, stereo confidence audio, peak/dB VU overlay, browser-disconnect cleanup, and repeated source switching work without orphan processes.
- Every supported output reports a unique `BMDDeckLinkPersistentID`; verify all sixteen IDs are unique, each group contains the expected eight subdevices, and operator names remain attached after swapping the two identical cards between PCIe slots.
- Power-cycle and reboot twice, then confirm persistent IDs, output labels, fixed rules, desired routes, and connector pictures do not change. The topological IDs may change and must never be treated as saved identity.
- 1080p25, 1080p50, 1080i50, and 720p50 presets play for at least 30 minutes on every compatible port with embedded audio checked. Confirm 1080i50 is top-field-first at 25 frames/50 fields per second and audio is 48 kHz stereo PCM at the DeckLink output.
- Two concurrent route-start requests never obtain the same port; locked routes retain ownership through publisher and Wowza-API outages.
- Wowza connection tests report authentication, applications, active streams, and successful RTSP frame receipt for each monitored application/instance.
- Pull network, stop the publisher, kill FFmpeg, create an RTSP stall, mark a port busy, disconnect a card if safe, and verify classification, queueing, standby, backoff and recovery.
- Restart the application and Windows while routes are desired; confirm leases rebuild before FFmpeg and all primary routes recover.
- Run an 8–24 hour multi-port soak while watching frames, FPS, speed, drops, CPU, memory and orphan processes.
- Repeat the soak with preview running and stopped to quantify its separate CPU/GPU cost and confirm it does not disturb DeckLink route progress.
- Test emergency stop and confirm every owned FFmpeg process exits; confirm unrelated FFmpeg processes are untouched.
- If trialing Windows Service mode, repeat the complete matrix in Session 0. Do not use service mode if results differ from interactive logon.
- Verify LAN firewall/CIDR restrictions, exact reverse-proxy trust, spoofed forwarded-header rejection, HTTPS certificate trust, administrator controls, read-only operator behavior, login throttling, session timeout, backup and rollback.
