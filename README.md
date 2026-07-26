<p align="center">
  <img src="src/BroadcastRouter.Web/wwwroot/images/broadcastrouter-logo-192.png" width="128" alt="BroadcastRouter logo" />
</p>

<h1 align="center">BroadcastRouter</h1>

<p align="center"><strong>Production Wowza-to-Blackmagic DeckLink routing control for Windows.</strong></p>

BroadcastRouter is a self-contained .NET 8 Blazor Server application that discovers active Wowza publishers, validates RTSP media with FFprobe, atomically reserves DeckLink outputs, and supervises FFmpeg playout. The browser is only a control surface: closing or refreshing it does not stop the routing host.

## What it provides

- authenticated Wowza REST discovery across configured applications and instances;
- deterministic source identities with stale-source reconciliation after server-ID changes;
- RTSP frame validation and media-property detection before routing;
- atomic DeckLink port reservations, priorities, locks, queues, retries, standby, and recovery;
- production-safe defaults: loopback binding, simulation disabled, and hardware starts blocked until validation passes;
- SQLite persistence, DPAPI-protected Wowza credentials, structured redacted logs, minimal health checks, and sanitized diagnostics that never embed the production database;
- a responsive dark operator UI for servers, sources, outputs, routes, rules, presets, logs, and settings;
- saved output-preset selection for manual route creation and confirmed route reassignment;
- an administrator-controlled 720×450 embedded browser preview with confidence audio, a real VU overlay, and live process statistics;
- self-contained Windows releases and Task Scheduler/service installation helpers.

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
2. Extract it to a versioned directory such as `C:\BroadcastRouter\1.2.4`.
3. Run `BroadcastRouter.Server.exe` as the dedicated Windows broadcast account.
4. Open `http://127.0.0.1:5080`.
5. Under **Settings**, select the DeckLink-enabled `ffmpeg.exe` and matching `ffprobe.exe`, then run **Validate / rescan**.
6. Under **Wowza Servers**, configure the REST URL, credentials, RTSP host/port, applications, and instances. Test the connection and save.
7. Verify sources and output mappings, then create routes manually before enabling automatic routing.
8. Install startup with:

```powershell
.\scripts\Install-InteractiveLogon.ps1 -ExecutablePath .\BroadcastRouter.Server.exe
```

Use the same Windows account for configuration and runtime because DPAPI credentials are account-bound.

## Build and test

```powershell
dotnet restore .\BroadcastRouter.sln
dotnet build .\BroadcastRouter.sln --configuration Release
dotnet run --project .\tests\BroadcastRouter.Tests\BroadcastRouter.Tests.csproj --configuration Release
dotnet run --project .\src\BroadcastRouter.Web\BroadcastRouter.Web.csproj --configuration Release
```

Create a clean self-contained release:

```powershell
.\scripts\Publish-Release.ps1 -Version 1.2.4
```

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
