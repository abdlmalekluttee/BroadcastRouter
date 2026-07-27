# Migration from batch files

1. Inventory every existing batch route: RTSP URL, DeckLink name/connector, raster/rate/scan, pixel format, audio, transport, buffer and fallback expectation.
2. Install BroadcastRouter in simulation and model those settings as output presets. The sample batch maps to a 1080p25 preset with TCP as desired, 256 MB input buffer, `uyvy422`, and the labeled DeckLink port.
3. Configure Wowza discovery and an RTSP URL template instead of copying credentials/URLs into commands. Use manual RTSP sources only for inputs not discoverable through Wowza.
4. Rescan real hardware and confirm each supported output reports a Blackmagic persistent hardware ID. Label the connector once, then create fixed/locked routes for batch files that represented permanent wiring. Use rules/groups for dynamic routes. Legacy FFmpeg-hash mappings are migrated during a controlled host start; do not delete the pre-migration backup until connector verification passes.
5. Stop one batch process, start the equivalent BroadcastRouter route, verify video/audio at the destination, then migrate the next port. Never run the batch file and BroadcastRouter against the same output.
6. After all ports pass soak testing, disable batch-file startup tasks. Retain them offline for rollback until the new system completes the agreed acceptance period.

Example equivalent FFmpeg arguments are generated internally as tokens. The operator never edits a shell command, credentials are not placed in command text, and uncompressed output does not use `-b:v`.
