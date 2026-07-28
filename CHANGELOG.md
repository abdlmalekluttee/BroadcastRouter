# Changelog

## Unreleased

## 1.3.1 — 2026-07-29

- Corrected the standby clock filter expression to use the portable `%T` `HH:MM:SS` format without over-escaped delimiters.
- Require the FFmpeg DeckLink build to include HarfBuzz so FFmpeg 8.1.2 enables `drawtext` alongside FreeType and Fontconfig.
- Added functional SMPTE-bars-plus-clock validation to the builder so presence-only filter checks cannot ship a broken standby runtime.

## 1.3.0 — 2026-07-28

- Added an explicit, fail-closed output-port designation for every DeckLink connector; input-only connectors are excluded from manual, rule-based, and automatic routing.
- Persist configured and discovered incoming streams while they are offline, including clean source-inventory recovery after a host restart.
- Added durable preconfigured and manual routing entries that can be prepared before a publisher is active.
- Enforce deterministic assignment priority: preconfigured, then manual, then automatic; lower-priority saved entries remain visible as routing conflicts and are never silently deleted.
- Reserve saved output ports while their streams are offline unless the operator explicitly enables temporary automatic use.
- Added per-port SMPTE/HD color bars or TV test patterns with optional logo, card/connector name, custom label, synchronized clock, and silent 48 kHz stereo audio.
- Automatically switch each marked output between its standby screen and live playout under a per-port process gate.
- Reset transient FFmpeg/lease state at startup while preserving saved intent, preventing duplicate restored ownership after an interrupted host.
- Added media-tool capability checks and regressions for output-only assignment, saved-route policy, standby command generation, tagged-source restart persistence, and transient route recovery.

## 1.2.11 — 2026-07-28

- Require approximately one second of the advertised video cadence before sparse frames can override verified continuous audio.
- Retain audio-led mode for the lifetime of a publisher observation so isolated still/black bursts cannot destabilize preview or SDI playout.
- Added regressions for isolated-frame classification and sticky audio-led readiness.

## 1.2.10 — 2026-07-28

- Accept audio-led RTSP inputs only after FFprobe counts real audio packets or frames, even when no video frame arrives during the bounded probe window.
- Generate continuous preset-matched black video with 48 kHz stereo PCM live audio for audio-only and sparse-video sources.
- End generated-black routes when their live audio input ends, preventing an indefinite black-output process after source loss.
- Make embedded preview use generated black video plus its existing VU meter for audio-led sources.
- Show audio-led/generated-black operation explicitly on the Sources page instead of reporting supported audio as unsupported media.
- Added regressions for sparse-video admission, true audio-only admission, metadata-only rejection, continuous-black route arguments, and audio-led browser preview.

## 1.2.9 — 2026-07-27

- Added operator-defined physical-card names keyed by the stable DeckLink device-group identity.
- Display DeckLink choices consistently as `Physical card / Connector` in routes, manual sources, and routing rules.
- Group the Outputs page by physical card and move raw persistent/topology values into a collapsed technical-identity section.
- Preserve unavailable saved output references in selectors instead of silently replacing them.

## 1.2.8 — 2026-07-27

- Clean legacy auto-generated DeckLink labels that included FFmpeg's trailing `] (none)` status while preserving custom operator-assigned names.

## 1.2.7 — 2026-07-27

- Added direct, dependency-free Windows COM access to Blackmagic Desktop Video's `IDeckLinkProfileAttributes` interface.
- Use unique `BMDDeckLinkPersistentID` values as stable output IDs while retaining the opaque FFmpeg device handle for playout.
- Preserve physical-card grouping, subdevice index, topology, model, and mapping-confidence metadata for identical DeckLink Quad 2 cards.
- Atomically migrate saved output names, groups, reserved flags, manual/rule fixed-output references, and persisted route assignments from legacy FFmpeg-hash IDs.
- Defer identity migration until a controlled restart if any output lease or owned FFmpeg process is active.
- Correct FFmpeg sink parsing so trailing status text such as `(none)` no longer appears in DeckLink display names.
- Added persistent-identity, duplicate-ID fallback, deferred-migration, and saved-reference migration regressions.

## 1.2.6 — 2026-07-27

- Replaced the disabled, ambiguous Sources-page Route button with explicit View route, Retry route, and busy/result states.
- Detect the patched FFmpeg `win_safe_terminate` DeckLink capability and enable it only when supported.
- Added an opt-in FFmpeg 8.1.2 DeckLink teardown patch for the Desktop Video 16.1 final `IDeckLink::Release()` crash.
- Added a redistributable builder-source release asset; the resulting `--enable-nonfree` DeckLink binary remains local-only under FFmpeg licensing.
- Added regression coverage for Sources-page route actions and safe-termination argument tokenization.

## 1.2.5 — 2026-07-27

- Corrected fallback-audio FFmpeg argument ordering so `anullsrc` is declared as an input before output `-map` options.
- Made route stops idempotent when an output lease is already free while preserving strict rejection of foreign-owned and locked leases.
- Added Windows Job Object containment with kill-on-close semantics so abrupt server termination cannot leave application-owned FFmpeg processes running.
- Added regressions for fallback argument ordering, reservation-release outcomes, and real Windows orphan-process termination.

## 1.2.4 — 2026-07-26

- Replaced the rejected FFmpeg `-rw_timeout` input argument with the RTSP demuxer's supported `-timeout` option.
- Made persisted-route recovery single-shot per host start so an immediately exiting FFmpeg process is classified instead of relaunched forever as `STARTING`.
- Monitor process exits before route reconciliation and retain a redacted retry reason in structured logs.
- Made interlaced output presets generate explicit 50-field/s temporal interlacing with top-field-first metadata.
- Normalize DeckLink audio output to 48 kHz stereo PCM and validate the additional interlacing filters and RTSP timeout capability.
- Added regression coverage for single-shot startup recovery, RTSP timeout tokenization, interlaced output, and audio normalization.

## 1.2.3 — 2026-07-26

- Added saved output-preset selection to manual route creation and existing route reassignment.
- Validate an explicitly selected preset before stopping the current route, rejecting stale browser selections safely.
- Prevent removal of presets still referenced by active or waiting routes.
- Replaced the routing matrix and routing-rule table with responsive labeled editor cards.
- Replaced fragile Boolean scan binding with explicit progressive/interlaced values so 1080i50 never falls to a blank selection.
- Added regression coverage for scan round trips and validated manual preset selection.

## 1.2.2 — 2026-07-26

- Replaced the external FFplay desktop window with an embedded H.264/AAC browser preview.
- Reduced the preview canvas from 1440×900 to a compact 720×450 player.
- Preserved the real FFmpeg `showvolume` peak/dB overlay inside the browser video.
- Added an administrator-only, no-store preview stream endpoint with a random per-session token.
- Reduced preview ownership to one exact FFmpeg process and stop it when the player disconnects or the operator stops it.
- Removed FFplay from the preview and deployment requirements while retaining tokenized process arguments and bounded cleanup.

## 1.2.1 — 2026-07-25

- Replaced the cramped fixed-width output-preset table with responsive labeled editor cards.
- Added cache-busted application styles so upgraded layouts appear without a manual browser cache clear.
- Added an administrator-controlled 1440×900 FFplay desktop preview with a real FFmpeg `showvolume` audio VU overlay.
- Added a large preview status surface with source media, uptime, owned-process IDs, and FFplay playback statistics.
- Added application-owned preview process supervision that stops only the FFmpeg/FFplay pair started by BroadcastRouter.
- Corrected Boolean scan option values so interlaced presets display accurately.
- Added tokenization and audio/no-audio preview command regression coverage.
- Extended the production verifier with an optional bounded live-preview start/stop check.

## 1.2.0 — 2026-07-25

- Released DeckLink reservations when FFmpeg cannot start and added grace-based release for missing unlocked sources.
- Removed the production database and sensitive topology from sanitized diagnostics.
- Added CIDR, embedded-credential, malformed FFprobe-output, and antiforgery hardening.
- Updated the .NET 8 SQLite packages to remove the flagged native SQLite vulnerability.
- Improved form-control accessible names and destructive route-action safeguards.
- Refused anonymous non-loopback hosting and limited forwarded headers to exact trusted proxies.
- Added server-side administrator authorization for route commands, authenticated SignalR, and login rate limiting.
- Serialized route starts per source, enforced route-state transitions, and made waiting-queue sequence govern retries.
- Added opt-in retry exhaustion, first-progress supervision, FFprobe scan-type detection, and trusted Wowza TLS behavior.
- Streamed diagnostics without loading complete archives into memory and added reference-aware deletion safeguards.

## 1.1.1 — 2026-07-24

- Normalized persisted GUI identifiers and paths to prevent invisible leading/trailing whitespace from creating distinct source identities.

## 1.1.0 — 2026-07-24

- Added authoritative stale-source reconciliation after Wowza server-ID changes, disablement, and successful publisher removal.
- Retained last healthy observations during temporary Wowza REST failures.
- Added complete Wowza REST JSON negotiation and a configurable connection timeout.
- Added collision-safe IDs for new Wowza servers and output presets.
- Added comprehensive validation for GUI-backed Wowza, RTSP, preset, routing, and security settings.
- Added visible operator errors for source and route commands.
- Added application branding, icons, web manifest, release automation, and repository documentation.
- Expanded the automated suite to cover source renames and every persisted GUI settings group.

## 1.0.0 — 2026-07-24

- Initial production implementation with Wowza discovery, RTSP probing, DeckLink routing, process supervision, SQLite persistence, DPAPI credentials, diagnostics, and Blazor operator UI.
