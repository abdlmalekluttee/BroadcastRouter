# Migration from batch files

1. Inventory every existing batch route: RTSP URL, DeckLink name/connector, raster/rate/scan, pixel format, audio, transport, buffer and fallback expectation.
2. Install BroadcastRouter in simulation and model those settings as output presets. The sample batch maps to a 1080p25 preset with TCP as desired, 256 MB input buffer, `uyvy422`, and the labeled DeckLink port.
3. Configure Wowza discovery and an RTSP URL template instead of copying credentials/URLs into commands. Use manual RTSP sources only for inputs not discoverable through Wowza.
4. Rescan real hardware and confirm each supported connector reports a Blackmagic persistent hardware ID. Give each physical card an operator name such as `Studio input card`, label its connectors once, and mark only the real playout connectors as **Output ports**. Selectors will show `Studio input card / Output 1` while input-only connectors remain unavailable to routing. Configure each output's standby bars/logo/label/clock, then save permanent batch wiring as preconfigured routes; use manual entries for operator choices and automatic rules/groups only for remaining streams. Legacy FFmpeg-hash mappings are migrated during a controlled host start; do not delete the pre-migration backup until connector verification passes.
5. Stop one batch process, start the equivalent BroadcastRouter route, verify video/audio at the destination, then migrate the next port. Never run the batch file and BroadcastRouter against the same output.
   For an audio-led batch input with absent or sparse pictures, select an audio-enabled preset. BroadcastRouter generates continuous black video and maps the live audio; verify that loss of the audio publisher causes normal route recovery rather than indefinite black output.
6. After all ports pass soak testing, disable batch-file startup tasks. Retain them offline for rollback until the new system completes the agreed acceptance period.

Version 1.3.3 advances operator settings to schema 6 and adds a monotonic configuration revision plus last-applied metadata. The SQLite audit table is created automatically. Existing settings, output-port selections, identities, aliases, routes, and source inventory remain compatible; no manual migration or restart is required beyond the normal version deployment itself.

Example equivalent FFmpeg arguments are generated internally as tokens. The operator never edits a shell command, credentials are not placed in command text, and uncompressed output does not use `-b:v`.
