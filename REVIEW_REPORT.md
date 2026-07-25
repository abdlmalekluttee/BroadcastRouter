# BroadcastRouter production-safety review

Review date: 2026-07-25  
Review branch: `codex/functional-gui-hardening`  
Reviewed baseline: `0a03b25` (`v1.1.1`, `origin/main`)
Resulting release version: `1.2.0`

## Executive summary

BroadcastRouter has a strong compact architecture and several correct production-safety foundations: fail-closed hardware validation, opt-in simulation, no network scanning, atomic in-memory output ownership, `ProcessStartInfo.ArgumentList`, DPAPI protection for Wowza passwords, transactional SQLite writes, and explicit warnings around FFmpeg/DeckLink identity and Session 0.

This review confirmed and fixed high-severity issues in failed-start lease rollback, emergency-stop enforcement, locked-route stopping, diagnostics confidentiality, anonymous LAN exposure, reverse-proxy trust, and a vulnerable native SQLite dependency. Additional fixes cover missing-source lease grace, stable restore timing, per-source route serialization, runtime state-transition enforcement, waiting-queue fairness, retry exhaustion, first-progress supervision, FFprobe termination/scan detection, strict CIDR and proxy validation, persisted embedded credentials, antiforgery, server-side command authorization, login throttling, diagnostics streaming/redaction, duplicate UI commands, destructive-action confirmation, and accessible form names.

The repository is materially safer after these changes, but this review does **not** certify production broadcast readiness. Real Wowza, DeckLink hardware, Blackmagic drivers, a DeckLink-enabled FFmpeg build, power-cycle identity stability, Session 0, and an 8–24 hour soak were not available for this pass. The principal remaining software risk is the coordinator's breadth and the limited end-to-end process-supervision integration coverage.

## Architecture assessment

- `BroadcastRouter.Domain` contains normalized source identities, configuration models, media/output models, and runtime snapshots.
- `BroadcastRouter.Application` contains assignment, atomic reservation, waiting priority, rules, retry policy, source reconciliation, and route-control safety policies.
- `BroadcastRouter.Infrastructure` owns Wowza REST, RTSP probing, FFmpeg argument construction and supervision, DeckLink enumeration, SQLite, DPAPI, logging redaction, network policy, and diagnostics sanitization.
- `BroadcastRouter.Web` owns the long-running host, coordinator, Blazor UI, SignalR status publication, authentication, health, and diagnostics endpoints. Browser refresh/closure does not own or stop FFmpeg.
- `BroadcastRouter.Tests` is a dependency-light executable regression suite.

Route lifecycle: Wowza/manual discovery → normalized source identity → bounded FFprobe frame validation → ordered rule evaluation → compatible port selection → atomic lease → fail-closed media-tool gate → FFmpeg start/progress supervision → stall/exit classification → retry/fallback → explicit stop or grace-based release → SQLite persistence and restart lease reconstruction.

The main coupling risk remains `RouterCoordinator`: it combines discovery scheduling, probing, state mutation, retry/fallback, persistence, process supervision, and operator commands in one large service. A large rewrite is not justified. This pass added per-source serialization and small testable policy boundaries while retaining the existing deployment shape.

## Baseline results

| Check | Result |
|---|---|
| `dotnet restore .\BroadcastRouter.sln` | Passed; already up to date. |
| Release build | Passed with 0 warnings and 0 errors. |
| Test executable | Passed 37/37 before changes. |
| Documented web-start command | Failed because `127.0.0.1:5080` was already owned by `BroadcastRouter.Server.exe` from the v1.1.1 release. That proven-owned process was not interrupted. |
| Existing release UI | All documented pages loaded; no browser console warnings/errors; no document-level horizontal overflow at 1366×768, 1920×1080, 2560×1440, or 768×1024. |
| Dependency audit | Initially reported high-severity GHSA-2m69-gcr7-jv3q through `SQLitePCLRaw.lib.e_sqlite3 2.1.6`. |

The failed documented web start is an environmental port conflict, not an application startup regression. The reviewed build was later launched separately with a temporary simulation-only database on `127.0.0.1:5180`.

## Findings

| ID | Severity | Category | Affected location | Description / reproduction / impact / root cause | Status and evidence |
|---|---|---|---|---|---|
| BR-001 | High | Reservation safety | `src/BroadcastRouter.Web/Services/RouterCoordinator.cs:439`; `src/BroadcastRouter.Application/RouteStartFailureRecovery.cs:7` | Reserve a port, then make `Process.Start()` fail. The prior code persisted `Starting` and propagated the exception without releasing the lease, permanently blocking the output. Root cause: reservation acquisition and process-start rollback were not one failure boundary. | **Fixed.** Failed starts force-release only that source's lease, clear port fields, persist `Failed/ProcessStart`, and return a sanitized operator error. Regression: `Startup failure releases reservation`. |
| BR-002 | High | Emergency control | `src/BroadcastRouter.Web/Services/RouterCoordinator.cs:95`; `src/BroadcastRouter.Application/RouteControlSafety.cs:5` | After emergency stop, `start`/`restore` set `_emergencyStopped=false`; restart/reassign/standby could also start without an explicit clear. A single stop exception could abort the emergency-stop loop. | **Fixed.** Starts are rejected until explicit `clear-emergency`; emergency stop remains active, attempts every owned route independently, and reports partial failure. Regression: `Emergency stop blocks route starts`. |
| BR-003 | High | Diagnostics confidentiality | `src/BroadcastRouter.Web/Program.cs:117`; `src/BroadcastRouter.Infrastructure/DiagnosticSanitizer.cs:7` | Download `/diagnostics/package` on the baseline. The archive labeled sanitized contained `broadcast-router.db`, runtime RTSP URIs, internal topology, paths, and logs. Impact: disclosure of DPAPI ciphertext, addresses, identities, and operational data. | **Fixed.** Database removed; IDs are opaque hashes; paths, credentials, addresses, and topology names are omitted/redacted; endpoint always requires Administrator. Archive verified to contain only four sanitized files. Regression: `Diagnostics omit sensitive data`. |
| BR-004 | High | Dependency security | `src/BroadcastRouter.Infrastructure/BroadcastRouter.Infrastructure.csproj:6` | `dotnet list package --vulnerable --include-transitive` reported the embedded SQLite native package under GHSA-2m69-gcr7-jv3q. | **Fixed.** Updated `Microsoft.Data.Sqlite` to 8.0.29 and pinned patched `SQLitePCLRaw.lib.e_sqlite3` 2.1.12. Final audit reports no vulnerable packages. |
| BR-005 | Medium | Recovery policy | `src/BroadcastRouter.Web/Services/RouterCoordinator.cs:373`; `:614` | `ReservationGraceSeconds` and `StableRestoreSeconds` were persisted/displayed but unused. Missing unlocked sources could retain a port indefinitely, while recovered primaries restarted immediately. | **Fixed.** Successful authoritative removal starts an unlocked grace timer; locked routes remain held; recovered sources must remain ready for the configured stable interval. Regression: `Missing-source lease retention`. |
| BR-006 | Medium | Process cleanup | `src/BroadcastRouter.Infrastructure/FfprobeStreamProbe.cs:46`; `:133` | Timeout/cancellation killed FFprobe but did not await exit or drain both redirected streams; empty/malformed JSON threw through the reconciliation cycle. | **Fixed.** Termination now kills the owned process tree, awaits exit, drains tasks, preserves caller cancellation, and returns `InvalidOutput` for empty/malformed JSON. Regression: `FFprobe rejects malformed output`. |
| BR-007 | Medium | Input/security validation | `src/BroadcastRouter.Infrastructure/SqliteDataStore.cs:332`; `:386`; `src/BroadcastRouter.Infrastructure/NetworkAccessPolicy.cs:5` | Invalid CIDR tokens were saved and silently ignored. Management/manual/standby URLs could persist embedded plaintext credentials. | **Fixed.** Strict IPv4/IPv6 CIDR validation, IPv4-mapped IPv6 matching, and fail-closed rejection of persisted URI userinfo. Regressions: invalid CIDR and embedded credentials. |
| BR-008 | Medium | Authentication/authorization | `src/BroadcastRouter.Web/Program.cs:94`; `:139`; `src/BroadcastRouter.Web/Components/Pages/Login.razor:8` | Login/logout explicitly disabled antiforgery; diagnostics authorization was conditional on authentication being enabled; anonymous health returned operational flags. | **Fixed.** Token validation returns 400 on missing/invalid token, logout uses a protected form, diagnostics always require Administrator, and anonymous health returns only `healthy/degraded`. HTTP verification covered tokenless/tokened login and unauthenticated diagnostics. |
| BR-009 | Medium | Logging confidentiality | `src/BroadcastRouter.Infrastructure/LogRedactor.cs:7`; `src/BroadcastRouter.Web/Program.cs:41`; `src/BroadcastRouter.Web/Components/Pages/Sources.razor:31` | Baseline framework logs printed the full Wowza endpoint; persisted errors and source tooltips could expose authenticated URIs and internal IPs. | **Fixed.** HTTP-client informational logging suppressed; HTTP/RTSP credentials and IP literals redacted before persistence/browser display; diagnostics remove complete URIs. Existing redaction tests expanded. |
| BR-010 | Low | GUI/accessibility/operator safety | `src/BroadcastRouter.Web/Components/Pages/RoutesPage.razor:8`; `src/BroadcastRouter.Web/Components/Layout/NavMenu.razor:2` | Automated DOM review found 15 unlabeled Wowza controls, 44 rule/preset controls, 14+ settings controls, and unlabeled route selects. Emergency/reassign actions lacked confirmation and duplicate-submit protection. | **Fixed.** All tested controls now have accessible names; collapsed navigation has names/tooltips; route actions use busy guards; emergency/reassign require confirmation; locked normal stop is refused with explicit guidance. Final DOM count: zero unlabeled controls on every reviewed page. |
| BR-011 | Medium | State integrity | `src/BroadcastRouter.Application/RouteStateMachine.cs`; `src/BroadcastRouter.Web/Services/RouterCoordinator.cs` | Coordinator transitions previously replaced records without consulting the tested state machine. | **Fixed.** The persistence boundary now rejects invalid transitions and skips stale concurrent updates; the graph covers real restart/recovery paths. |
| BR-012 | Medium | Queue fairness | `src/BroadcastRouter.Application/PriorityWaitingQueue.cs`; `src/BroadcastRouter.Web/Services/RouterCoordinator.cs` | Queue sequence was displayed but did not govern assignment retries. | **Fixed.** Ready queued sources are reconciled first in priority/FIFO snapshot order, followed by new eligible sources. |
| BR-013 | Low | Brute-force defense | `src/BroadcastRouter.Web/Program.cs` | Login had no throttling. | **Fixed.** ASP.NET Core fixed-window limiting permits five attempts per effective client IP per minute and returns 429 after exhaustion. |
| BR-014 | High | Host exposure / proxy trust | `src/BroadcastRouter.Web/Program.cs`; `src/BroadcastRouter.Infrastructure/NetworkAccessPolicy.cs` | A non-loopback bind with authentication disabled granted anonymous Administrator, while unrestricted forwarded headers could alter the address used by the allowlist. | **Fixed.** Anonymous non-loopback startup is refused; forwarded headers are processed only from exact configured proxies; forwarded loopback can never use the direct-loopback shortcut. Regression: `Network exposure and proxy trust are fail-closed`. |
| BR-015 | High | Locked route safety | `src/BroadcastRouter.Web/Services/RouterCoordinator.cs`; `src/BroadcastRouter.Application/RouteControlSafety.cs` | Normal stop could terminate FFmpeg before discovering that a locked lease could not be released. | **Fixed.** Lock authorization is checked before queue, process, lease, or route state mutation. Regression: `Locked route stop is refused before release`. |
| BR-016 | Medium | Concurrency / supervision | `src/BroadcastRouter.Web/Services/RouterCoordinator.cs`; `src/BroadcastRouter.Infrastructure/FfmpegProgressParser.cs` | Duplicate starts could interleave and a live child that never emitted its first progress record could remain `Starting` indefinitely. | **Fixed.** Per-source async gates deduplicate active starts, emergency stop is rechecked during launch, and a configurable first-progress deadline triggers owned-process cleanup/retry. |

## GUI review

| Page/workflow | Result |
|---|---|
| Login | Token rendered; tokenless POST returns 400; invalid credential with valid token returns to `?failed=1`; controls labeled. |
| Dashboard | Production/simulation and hardware-block status are explicit text; emergency stop confirmed and busy-guarded. |
| Wowza Servers | Controls have accessible names; password remains blank-to-preserve; no network scanning action exists; removal now requires confirmation and explains grace-period impact. |
| Sources | Credential/internal-address-safe RTSP display; route/reprobe actions visible only to Administrator. |
| DeckLink Outputs | Identity-confidence warning is prominent; rescan remains non-destructive. |
| Routes / Matrix | Accessible selects; emergency and reassign confirmation; locked stop refusal; busy guard. A richer modal could explain affected routes/ports before reassign. |
| Rules & Presets | All generated controls named; bounded regex validation retained; rule removal is confirmed and referenced presets cannot be removed until dependencies are changed. |
| Logs & Diagnostics | Package contents now accurately state there is no database copy; logged IPs/credentials are redacted. |
| Settings | All current and generated port/manual controls named; strict validation preserves prior active settings; bind/authentication/HTTPS/allowlist/proxy changes require a disruption warning confirmation. |
| Error | Sanitized generic error content; no detailed exception exposed. |

Responsive checks passed without document-level horizontal overflow at 1366×768, 1920×1080, 2560×1440, and 768×1024. Wide tables intentionally scroll inside `.table-wrap`. Browser console inspection reported no warnings or errors. No before/after screenshots were captured because the fixes were behavioral/accessibility changes rather than a visual redesign; the live v1.1.1 process was intentionally not interrupted.

## Test coverage

Baseline: 37/37 tests. Final: 52/52 tests.

Added coverage:

1. 500-way concurrent reservation stress proves one winner.
2. FFmpeg startup-failure reservation rollback.
3. Locked and unlocked missing-source grace behavior plus stable restore.
4. Emergency-stop start blocking.
5. Empty/malformed FFprobe JSON.
6. Diagnostics secret/address/topology omission.
7. Invalid CIDR and IPv4-mapped IPv6 behavior.
8. Embedded management/manual RTSP credential rejection.
9. Failed settings validation preserves the active configuration.
10. Locked-route stop refusal before process or lease mutation.
11. Administrator-only route-command authorization.
12. Opt-in retry-attempt exhaustion.
13. No-first-progress timeout classification.
14. Progressive/interlaced FFprobe field-order parsing.
15. Anonymous exposure, trusted-proxy, and forwarded-loopback fail-closed behavior.

Expanded existing credential/log redaction assertions. HTTP integration checks separately verified antiforgery and diagnostics authorization.

Still requiring integration or hardware coverage:

- real FFprobe timeout/cancellation with a deliberately hanging child process;
- actual FFmpeg start failure, process exit, graceful `q`, forced process-tree kill, PID reuse, and unrelated-process protection;
- full operator-role browser session and direct SignalR/circuit authorization attempts;
- corrupted/migrated production database recovery and rollback;
- real Wowza response variants/partial instance failures;
- authenticated operator/admin end-to-end command denial and login-limit exhaustion through a real reverse proxy.

## Production validation matrix

| Validation | Status |
|---|---|
| Restore, Release build, tests | **Verified automatically**: clean; 52/52 at the second-pass checkpoint. |
| Package vulnerability audit | **Verified automatically**: no vulnerable packages reported. |
| Atomic single-port ownership | **Verified automatically** with 500 concurrent contenders. |
| Local browser pages/viewports | **Verified locally** in isolated simulation on port 5180; no console errors or document overflow; zero unlabeled controls. |
| Authentication/antiforgery/diagnostics gate | **Verified locally** in isolated authenticated modes on ports 5181/5183, including operator denial and login throttling. |
| Real Wowza REST and RTSP | **Requires a real Wowza server**; not certified in this pass. |
| DeckLink enumeration and output | **Requires DeckLink hardware**, matching Desktop Video drivers, and DeckLink-enabled FFmpeg. |
| Physical connector identity stability | **Requires two reboots and power cycles** plus labeled-output verification. |
| Broadcast modes/audio | **Requires physical tests** for 1080p25, 1080p50, 1080i50, and 720p50 on every compatible port. |
| Failure/recovery matrix | **Requires lab fault injection**: network pull, publisher stop, FFmpeg kill, RTSP stall, busy port, and safe card disconnect. |
| Restart recovery | **Requires application and Windows restart testing** with desired routes. |
| Soak | **Requires 8–24 hours** with multi-port frame/FPS/drop/CPU/memory/orphan monitoring. |
| Windows Service / Session 0 | **Not verified**; do not claim production readiness until the full matrix passes there. |

## Files changed

- Safety policies: `RouteControlSafety.cs`, `RouteLeaseRetentionPolicy.cs`, `RouteStartFailureRecovery.cs`.
- Infrastructure: `DiagnosticSanitizer.cs`, `NetworkAccessPolicy.cs`, FFprobe cleanup, redaction, SQLite validation/dependencies.
- Web: coordinator recovery/emergency handling, authentication/diagnostics/health endpoints, and targeted Razor accessibility/operator safeguards.
- Tests: nine new regression cases and expanded redaction coverage.
- Documentation: README, security, architecture, deployment, changelog, and this report.

## Commands and results

```powershell
dotnet restore .\BroadcastRouter.sln
dotnet build .\BroadcastRouter.sln --configuration Release
dotnet run --project .\tests\BroadcastRouter.Tests\BroadcastRouter.Tests.csproj --configuration Release
dotnet list .\BroadcastRouter.sln package --vulnerable --include-transitive
dotnet run --no-build --project .\src\BroadcastRouter.Web\BroadcastRouter.Web.csproj --configuration Release
git diff --check
```

Final results: restore passed; Release build passed with 0 warnings/errors; 52/52 tests passed; vulnerability audit and .NET 8 servicing audit were clean; isolated simulation/authenticated hosts passed page, authorization, antiforgery, diagnostic-streaming, and login-throttling checks; `git diff --check` reported no whitespace errors. The self-contained `win-x64` 1.2.0 package built successfully with file version `1.2.0.0`, a generated SHA-256 checksum, and no database/diagnostics archive.

## Recommended follow-up

1. Add injectable process/time abstractions and end-to-end supervisor tests, including actual hanging/rapid-exit child processes.
2. Add a full authenticated operator/admin integration matrix through the deployment reverse proxy.
3. Consider decomposing `RouterCoordinator` only after hardware validation establishes stable operational boundaries.
4. Run the exact hardware, reboot, failure-injection, soak, and Session 0 matrix above before broadcast deployment.
