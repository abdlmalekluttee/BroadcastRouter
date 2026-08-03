# Windows deployment

## Recommended: dedicated broadcast Windows Service

1. Install the matching Blackmagic Desktop Video release and verify each output in Desktop Video Setup/Media Express.
2. Obtain a trusted prebuilt 64-bit FFmpeg package that includes DeckLink, FFprobe, H.264/AAC encoding, fragmented MP4 output, and the `showvolume`, `overlay`, `drawtext`, `scale`, `pad`, `color`, `smptebars`, `smptehdbars`, and `testsrc2` filters. Do not place it inside the BroadcastRouter release folder; upgrades should be explicit.
   The GitHub release also provides a source-only FFmpeg 8.1.2 builder with the validated Windows DeckLink safe-termination patch. DeckLink builds require `--enable-nonfree`, so compiled binaries cannot be redistributed; supply the Blackmagic SDK and build locally.
3. Extract `BroadcastRouter-production-win-x64-<version>.zip` to a versioned directory writable only by administrators and the dedicated broadcast account.
4. Sign in as that account and run `BroadcastRouter.Server.exe` once. Open `http://127.0.0.1:5080`.
5. In **Settings > Media Tools**, select `ffmpeg.exe` and `ffprobe.exe`, save, and require every routing validation gate to pass. Embedded preview additionally requires the H.264/AAC, fragmented MP4, and preview-filter capabilities listed above.
6. Configure and test Wowza. Name each persistent physical card and connector, then mark only real playout connectors as **Output ports**. Input-only connectors are intentionally absent from every route selector. Configure each output's standby preset, bars, logo, label, and clock before creating routes.
   The standby clock is FFmpeg `drawtext` local time from the Windows system clock. Configure Windows Time against the station NTP source, verify synchronization before air, and monitor clock drift operationally.
   Prepare preconfigured/manual routes for both active and offline streams. Preconfigured entries outrank manual entries; both outrank automatic assignment. Leave output reservation enabled unless temporary automatic use during source downtime is operationally acceptable.
   On supported hardware, the Outputs page must report **Blackmagic persistent hardware ID**. Version 1.2.7 migrates legacy FFmpeg-hash references during the first controlled restart and preserves operator names, fixed rules, and desired routes. If ownership is active during discovery, migration is intentionally deferred until the next restart.
   Audio-only and sparse-video sources require an audio-enabled preset. BroadcastRouter verifies live audio packets, generates continuous black video at the preset raster/rate, and preserves the live audio; it does not treat codec metadata by itself as route-ready.
   Optionally install a DeckLink visual asset pack that your organization is permitted to use with `scripts\Install-DeckLinkAssets.ps1 -ArchivePath <zip> -ApplicationRoot <release-folder>`. The importer writes only to `data\decklink-assets`, validates referenced checksums, and does not require a restart. These visuals are presentation-only. Public release archives intentionally omit Blackmagic-owned images.
7. Disable any previous interactive-logon BroadcastRouter task. From an elevated PowerShell window, obtain a credential for the same dedicated Windows account and run:

   ```powershell
   $serviceAccount = Get-Credential "$env:COMPUTERNAME\BroadcastRouterUser"
   .\scripts\Install-WindowsService.ps1 `
     -ExecutablePath .\BroadcastRouter.Server.exe `
     -Credential $serviceAccount `
     -StartService
   ```

   The password is passed as a `SecureString`; the installer does not write it to a file or command line. The account receives only the required **Log on as a service** right. The service display name is `Broadcast Router – Version <version>`, startup is Automatic, and SCM recovery restarts it after 5, 15, then 60 seconds. Service/application lifecycle events are written to Windows Application Event Log and the structured application log.
   Keep those recovery actions enabled. Version 1.5.4 treats a coordinator that makes no progress for two minutes as a fatal service fault, records the blocked stage, and exits so SCM can restore discovery/routing automatically. Monitor `/health`; `degraded` now includes a stale routing coordinator as well as a failed database integrity check.
   Version 1.5.5 additionally isolates DeckLink SDK identity/reference polling in a five-second helper process so a blocked Desktop Video COM call no longer stalls the coordinator in the first place. Version 1.5.6 adds bounded long-GOP confirmation and route-scoped RTSP protocol recovery. Version 1.5.7 serializes fallback-to-live ownership and isolates supervision faults per source. Version 1.5.8 also handles a live PID disappearing between supervision and saved-route reconciliation. Version 1.5.9 adds a 250 ms fatal-session supervision path and DeckLink media-starvation recovery for rapid Wowza resets. Version 1.5.10 reacts to either post-startup video or audio starvation and executes due live retries on that same fast path; no settings or database migration is required.
8. Reboot without signing in, confirm the server and owned FFmpeg children are in Session 0 with no windows, then perform the complete soak plan in `PRODUCTION-VALIDATION.md` before enabling automatic routing.

Data and DPAPI credentials belong to the Windows account that runs the executable. Back up the `data` directory only while the app is stopped and protect the backup as a production secret. Sanitized diagnostics deliberately exclude the database. Copying DPAPI ciphertext to a different account does not make it decryptable.

## Service-mode safety and rollback

The service must use the same Windows account that encrypted the Wowza credentials. LocalSystem cannot decrypt another account's DPAPI data. Never leave the old scheduled task enabled: a second host could contend for SQLite and physical DeckLink outputs.

The installer configures hosting and recovery, but that alone does not prove a particular driver/card combination works in Session 0. Run the full hardware validation plan. If output enumeration, audio, picture, or teardown differs, stop and disable the service, restore the protected cold backup, and temporarily return to the interactive-logon task under the same account while the difference is investigated.

The embedded browser preview has no desktop-window dependency and can be served by the same host in service mode. This does not change the requirement to physically validate DeckLink behavior in Session 0.

## LAN and HTTPS

Keep loopback binding unless remote control is required. Before enabling LAN access:

- set `BROADCASTROUTER_ADMIN_PASSWORD` as a machine/user environment variable for the host account;
- optionally set `BROADCASTROUTER_OPERATOR_PASSWORD` for read-only access;
- enable authentication, configure an exact bind address and CIDR allowlist, then restart;
- configure a trusted Kestrel certificate with standard ASP.NET Core `Kestrel:Certificates:Default` settings, or use a trusted reverse proxy restricted to the host;
- when using a reverse proxy, configure its exact IP address under **Trusted reverse proxies**; forwarded headers from every other peer are ignored;
- firewall the port only to the management networks.

The app refuses startup when authentication is enabled without an administrator password or when a non-loopback bind has authentication disabled.

## Upgrade and rollback

Stop the scheduled task/service, copy the complete new release to a new versioned folder, copy the `data` directory, and start the new version. Keep the prior folder and a separately protected database backup made while the app is stopped. For a LAN deployment, also merge the prior `AllowedHosts` value into the new release's `appsettings.json`; public packages intentionally contain only localhost host-filter entries. Do not replace the remaining new-version defaults blindly. Verify health through the actual configured bind address because an exact NIC bind does not necessarily listen on loopback. SQLite settings retain a last-valid configuration row; sanitized diagnostics intentionally contain no database backup. Roll back by stopping the new executable and starting the prior folder with the matching protected database backup.

When upgrading to 1.2.7, keep the pre-upgrade database backup with the prior executable. Persistent-ID migration rewrites stored output references in the new database. A rollback must use the matching pre-migration database rather than the migrated copy.
