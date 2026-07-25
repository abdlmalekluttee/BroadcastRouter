# Production validation checklist

The current development machine has no production Wowza server, DeckLink cards, or selected DeckLink-enabled FFmpeg package. Simulation and automated checks cannot certify those external systems.

Before broadcast use, verify:

- FFmpeg/FFprobe versions, DeckLink compilation, `scale`/`fps`/`yadif`, `uyvy422`, rawvideo, and every enumerated output pass in Media Tools.
- FFmpeg aliases map to the labeled physical connectors; power-cycle and reboot twice, then confirm mappings do not change.
- 1080p25, 1080p50, 1080i50, and 720p50 presets play for at least 30 minutes on every compatible port with embedded audio checked.
- Two concurrent route-start requests never obtain the same port; locked routes retain ownership through publisher and Wowza-API outages.
- Wowza connection tests report authentication, applications, active streams, and successful RTSP frame receipt for each monitored application/instance.
- Pull network, stop the publisher, kill FFmpeg, create an RTSP stall, mark a port busy, disconnect a card if safe, and verify classification, queueing, standby, backoff and recovery.
- Restart the application and Windows while routes are desired; confirm leases rebuild before FFmpeg and all primary routes recover.
- Run an 8–24 hour multi-port soak while watching frames, FPS, speed, drops, CPU, memory and orphan processes.
- Test emergency stop and confirm every owned FFmpeg process exits; confirm unrelated FFmpeg processes are untouched.
- If trialing Windows Service mode, repeat the complete matrix in Session 0. Do not use service mode if results differ from interactive logon.
- Verify LAN firewall/CIDR restrictions, HTTPS certificate trust, administrator controls, read-only operator behavior, session timeout, backup and rollback.
