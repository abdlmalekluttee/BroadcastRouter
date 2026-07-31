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

Example equivalent FFmpeg arguments are generated internally as tokens. The operator never edits a shell command, credentials are not placed in command text, and uncompressed output does not use `-b:v`.
