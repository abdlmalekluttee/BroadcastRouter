# Changelog

## Unreleased

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
