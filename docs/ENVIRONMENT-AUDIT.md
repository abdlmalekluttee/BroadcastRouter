# Environment validation notes

This file records the validated development baseline without publishing workstation paths, addresses, credentials, or customer stream names.

## .NET

- The solution targets .NET 8 and pins SDK feature band 8.0.4xx through `global.json`.
- NuGet sources are scoped by the repository's `NuGet.Config`.
- The production release is self-contained for `win-x64`.

## FFmpeg and Blackmagic

- RTSP discovery and frame probing were verified with FFmpeg/FFprobe 8.1.x.
- The selected production FFmpeg build reports compiled DeckLink output support.
- DeckLink output enumeration still requires a compatible Blackmagic Desktop Video driver and visible hardware on the deployment host.
- FFmpeg binaries, Blackmagic drivers/SDKs, and proprietary redistributables are never committed or bundled.

## Wowza

- Wowza Streaming Engine 4.8 REST authentication, application listing, active-publisher discovery, and RTSP playback were verified end-to-end.
- An unauthenticated REST request returns HTTP 401, and authenticated API responses are requested as JSON.
- Credentials remain DPAPI-encrypted in the local production database and are excluded from source control and release archives.

Re-run `docs/PRODUCTION-VALIDATION.md` after driver, SDK, FFmpeg, Wowza, network, or hardware changes.
