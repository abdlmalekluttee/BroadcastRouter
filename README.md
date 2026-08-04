<p align="center">
  <img src="src/BroadcastRouter.Web/wwwroot/images/broadcastrouter-logo-192.png" width="128" alt="BroadcastRouter logo" />
</p>

<h1 align="center">BroadcastRouter</h1>

<p align="center"><strong>Production Wowza-to-Blackmagic DeckLink routing control for Windows.</strong></p>

BroadcastRouter is a self-contained .NET 8 Blazor Server application that inventories active and offline Wowza publishers, validates active RTSP media with FFprobe, atomically reserves operator-designated DeckLink output ports, and supervises FFmpeg playout and standby screens. The browser is only a control surface: closing or refreshing it does not stop the routing host.

## What it provides

- authenticated Wowza REST discovery across configured applications and instances;
- deterministic source identities with stale-source reconciliation after server-ID changes;
- RTSP video-frame/audio-packet validation and media-property detection before routing;
- audio-led and sparse-video routing with continuous preset-matched black output and live audio;
- atomic DeckLink port reservations, priorities, locks, queues, retries, standby, and recovery;
- persistent preconfigured/manual routing for offline streams, with deterministic preconfigured → manual → automatic priority;
- explicit output-port designation so input connectors can never be selected by routing;
- per-port SMPTE/HD color bars with card/SDI identity, centered time and full date, bottom operator label, a configurable four-corner logo, and the NTP-synchronized Windows clock;
- Blackmagic SDK persistent hardware identities that keep operator-defined physical-card names, connector names, and assignments attached when identical supported cards move between PCIe slots;
- optional manifest-driven DeckLink product and connector visuals, loaded from an operator-supplied local asset pack without making images part of routing identity;
- production-safe defaults: loopback binding, simulation disabled, and hardware starts blocked until validation passes;
- SQLite persistence, DPAPI-protected Wowza credentials, structured redacted logs, minimal health checks, and sanitized diagnostics that never embed the production database;
- a responsive dark operator UI for servers, sources, outputs, routes, rules, presets, logs, and settings;
- saved output-preset selection for manual route creation and confirmed route reassignment;
- an administrator-controlled 720×450 embedded browser preview with confidence audio, a real VU overlay, and live process statistics;
- self-contained Windows releases and a credential-aware automatic Windows Service installer with crash recovery;
- coordinator liveness monitoring that reports degraded health and invokes Windows Service recovery if discovery, process supervision, or reconciliation stops making progress.

## Validated production baseline

| Component | Baseline |
|---|---|
| Host | Windows 10/11 or Windows Server, x64 |
| Application | Self-contained .NET 8 release |
| Wowza | Streaming Engine 4.8 REST API enabled and reachable |
| Media tools | 64-bit FFmpeg/FFprobe build with `decklink` output support |
| Hardware | Blackmagic DeckLink card plus matching Desktop Video drivers |
| Network | Wowza REST management port and configured RTSP port reachable from the host |

FFmpeg, Blackmagic Desktop Video, and Wowza are not bundled. Their licenses and installation remain the operator's responsibility.

## Quick start

1. Download the latest `BroadcastRouter-production-win-x64-*.zip` from [Releases](https://github.com/abdlmalekluttee/BroadcastRouter/releases).
2. Extract it to a versioned directory such as `C:\BroadcastRouter\1.5.16`.
3. Run `BroadcastRouter.Server.exe` once as the dedicated Windows broadcast account to complete configuration and hardware validation.
4. Open `http://127.0.0.1:5080`.
5. Under **Settings**, select the DeckLink-enabled `ffmpeg.exe` and matching `ffprobe.exe`, then run **Validate / rescan**.
6. Under **Wowza Servers**, configure the REST URL, credentials, RTSP host/port, applications, and instances. Test the connection and save.
7. Mark only the intended SDI connectors as **Output ports**, configure each standby screen, and verify the Windows clock is synchronized to NTP.
8. Prepare preconfigured/manual routes for active or offline streams, then enable automatic routing for unassigned active streams.
9. From an elevated PowerShell window, install the automatic background service under that same DPAPI account:

```powershell
$serviceAccount = Get-Credential "$env:COMPUTERNAME\BroadcastRouterUser"
.\scripts\Install-WindowsService.ps1 `
  -ExecutablePath .\BroadcastRouter.Server.exe `
  -Credential $serviceAccount `
  -StartService
```

Disable any older interactive-logon task before starting the service; two hosts must never share the database or DeckLink outputs. Use the same Windows account for configuration and service runtime because DPAPI credentials are account-bound. The service starts at boot without a user logon, runs in Session 0, restarts after unexpected failure, and records lifecycle events. Physically repeat the DeckLink validation matrix after every driver, account, or service-hosting change.

### Human-friendly DeckLink identity

Under **Settings > Physical DeckLink cards**, name each card for its real operational role, such as `Studio input card` or `Transmission card`. Under **DeckLink connector mappings**, name its connectors `Input 1`, `Input 2`, and so on. Output selectors then show `Studio input card / Input 1`; operators never need to memorize a persistent ID. Card names are stored against the Blackmagic physical-card group identity, while connector names are stored against each persistent connector ID. Raw IDs remain available under **Outputs > Technical identity** for troubleshooting.

### Optional DeckLink visual asset pack

BroadcastRouter can match the Blackmagic SDK model name to `manifest.min.json` and show a product view, model/category/connection facts, live connector roles, current stream ownership, connection diagrams, optional Micro-model accessory diagrams, and physical dimensions. The main card view uses `product.jpg`; `physical.jpg` appears only inside the expanded technical guide. Images retain their native colors and aspect ratio on a neutral canvas and never affect hardware discovery, stable IDs, port selection, or route ownership.

The Outputs page reads the installed Desktop Video version and installation date from Windows, then performs a bounded cached comparison with Blackmagic Design's official Desktop Video page. Network or parsing failures never affect hardware discovery or routing and are shown as `Not Available`. The public DeckLink SDK does not expose a reliable per-card firmware version, so firmware fields also remain `Not Available` rather than displaying a guessed value.

The provided images remain subject to Blackmagic Design's applicable copyright and trademark terms, so they are not committed or bundled in public releases. Install a pack you are permitted to use into the application's persistent data directory:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install-DeckLinkAssets.ps1 `
  -ArchivePath C:\Path\To\blackmagic-decklink-assets.zip `
  -ApplicationRoot C:\BroadcastRouter
```

The importer validates archive paths, the manifest, every referenced file, and available SHA-256 values. The UI detects the installed manifest without an application restart. Replacing an existing pack requires `-Force` and creates a timestamped backup. If no pack or no exact model match is available, the interface shows the existing technical identity and port state without guessing a card image.

### Saved routing priority and standby

Incoming streams remain in the inventory when their publisher is offline. A preconfigured or manual routing entry can therefore be saved in advance and activates when FFprobe marks the stream ready. Preconfigured entries outrank manual entries, and both outrank automatic assignment. A saved output remains reserved while offline unless **Allow automatic streams to use it temporarily** is enabled; the saved entry itself is never deleted or reassigned.

Every connector marked as an output port can run an independent standby screen whenever it is not carrying live playout. Configure SMPTE/HD bars, a four-corner logo path, and a custom port/stream label under **Settings > DeckLink connector mappings and standby**. The top line identifies the physical card and SDI connector, the center shows `HH:mm:ss` and the full date, and the label remains at the bottom. FFmpeg reads the Windows system clock, so configure and monitor Windows Time/NTP on the host.

Standby audio is always application-generated zero-valued 48 kHz stereo PCM; live source audio is never mapped into the per-port standby process. A standby replacement cannot start until the prior owned FFmpeg PID has exited and been reaped. Process start, stop, forced termination, and exit-code events are available under **Logs & Diagnostics** in the `ProcessLifecycle` category.

For fast standby-to-live cuts, enable **Low latency** on the output preset and configure the source encoder for a keyframe interval no longer than two seconds. BroadcastRouter bounds RTSP analysis and standby shutdown, but a receiver cannot display predictive video until a decodable keyframe arrives. Each completed cutover records its measured first-DeckLink-frame latency in structured logs under the `Cutover` category.

Live-video supervision also detects a stale decoder that keeps FFmpeg output progress alive by repeating almost every frame. After the configurable frozen-input timeout, only that owned route process is stopped and retried; the saved assignment and output-port configuration remain intact. Audio-led sources are excluded because their continuous black video is intentional. Ambiguous H.264/AAC inputs receive a bounded, rate-limited extended keyframe-acquisition probe before audio-led mode is committed, so a long GOP is not mistaken for an absent picture. Fatal RTSP control-session desynchronization recreates only the affected owned route while preserving its saved output reservation. During recovery, the generated fallback is identified separately and fully reaped before replacement live playout starts, so its frames cannot be reported as recovered source video or block the new source owner. **Refresh discovery** on Sources is a source-only operation and displays the backend-confirmed completion time and inventory count without rescanning DeckLink hardware.

The routing worker publishes an internal heartbeat before and after process supervision, discovery/probing, hardware validation, route reconciliation, and standby reconciliation. `/health` becomes `degraded` if that worker makes no progress for two minutes. The independent watchdog then writes the stalled stage and terminates the host so the configured Windows Service recovery action restarts it; the process Job Object removes only BroadcastRouter-owned media children. This protects against a responsive web interface masking a stalled routing engine.

Native DeckLink identity and reference-lock polling runs in a hidden, short-lived helper process with its own five-second deadline and kill-on-close containment. If Desktop Video blocks inside a COM call, the helper is discarded and retried later while discovery, routing, and the service host continue using the last confirmed hardware state.

## Build and test

```powershell
dotnet restore .\BroadcastRouter.sln
dotnet build .\BroadcastRouter.sln --configuration Release
dotnet run --project .\tests\BroadcastRouter.Tests\BroadcastRouter.Tests.csproj --configuration Release
dotnet run --project .\src\BroadcastRouter.Web\BroadcastRouter.Web.csproj --configuration Release
```

Create a clean self-contained release:

```powershell
.\scripts\Publish-Release.ps1 -Version 1.5.1
```

The publisher removes build-path PDBs and runs `scripts\Test-ReleasePrivacy.ps1` before creating the archive. Packaging fails if it finds a database, diagnostics/log artifact, credential-bearing URL, user-profile path, private network address, private key, or common service token.

## Configuration and security

The database is `data\broadcast-router.db` beside the deployed executable. SQLite uses WAL mode, transactions, integrity checks, and route history. Wowza passwords are encrypted with Windows DPAPI and are never placed in release or diagnostics archives. Take database backups only while the application is stopped, and protect them as production secrets.

The default bind is loopback only. Before LAN access, enable authentication, configure an exact bind address and CIDR allowlist, provide `BROADCASTROUTER_ADMIN_PASSWORD` (and optionally `BROADCASTROUTER_OPERATOR_PASSWORD`), deploy trusted HTTPS, and restrict the Windows firewall to management networks. Startup is refused when a non-loopback bind has authentication disabled or when authentication lacks an administrator password. Forwarded headers are ignored unless exact trusted proxy IP addresses are configured.

## Repository layout

| Path | Responsibility |
|---|---|
| `src/BroadcastRouter.Domain` | Identities, settings, media/output/route models |
| `src/BroadcastRouter.Application` | Assignment, reconciliation, routing rules, retry policy |
| `src/BroadcastRouter.Infrastructure` | Wowza, FFmpeg/FFprobe, DeckLink, SQLite, DPAPI, redaction |
| `src/BroadcastRouter.Web` | Long-running Blazor host, UI, background reconciliation, health and diagnostics |
| `tests/BroadcastRouter.Tests` | Dependency-light regression and persistence suite |
| `tools` | Secure local configuration and verification utilities |
| `docs` | Architecture, deployment, migration, validation, and state-machine guides |

Read [deployment](docs/DEPLOYMENT.md), [production validation](docs/PRODUCTION-VALIDATION.md), [architecture](docs/ARCHITECTURE.md), [route state machine](docs/ROUTE-STATE-MACHINE.md), and [security policy](SECURITY.md) before broadcast use.

## License

MIT. See [LICENSE](LICENSE). BroadcastRouter is an independent project and is not affiliated with Wowza Media Systems, Blackmagic Design, or FFmpeg.
