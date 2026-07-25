# Windows deployment

## Recommended: dedicated broadcast user at logon

1. Install the matching Blackmagic Desktop Video release and verify each output in Desktop Video Setup/Media Express.
2. Obtain a trusted prebuilt 64-bit FFmpeg package that includes DeckLink. Do not place it inside the BroadcastRouter release folder; upgrades should be explicit.
3. Extract `BroadcastRouter-production-win-x64-<version>.zip` to a versioned directory writable only by administrators and the dedicated broadcast account.
4. Sign in as that account and run `BroadcastRouter.Server.exe` once. Open `http://127.0.0.1:5080`.
5. In **Settings > Media Tools**, select `ffmpeg.exe` and `ffprobe.exe`, save, and require every validation gate to pass.
6. Configure and test Wowza, name the physical outputs, assign groups/reserved ports, create presets/rules, and test one output at a time.
7. Run `scripts\Install-InteractiveLogon.ps1` as the broadcast account. It creates a normal per-user Task Scheduler entry that starts the server when that user logs in; elevation is not required.
8. Perform the soak plan in `PRODUCTION-VALIDATION.md` before enabling automatic routing.

Data and DPAPI credentials belong to the Windows account that runs the executable. Back up the `data` directory only while the app is stopped and protect the backup as a production secret. Sanitized diagnostics deliberately exclude the database. Copying DPAPI ciphertext to a different account does not make it decryptable.

## Windows Service trial

The executable includes normal Windows Service lifetime integration. `scripts\Install-WindowsService.ps1` creates an opt-in Automatic (Delayed Start) service. This proves hosting only; it does not prove DeckLink works in Session 0. Use it only in a lab, run the hardware validation plan, and revert to interactive-logon hosting if output enumeration or playout differs.

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

Stop the scheduled task/service, copy the complete new release to a new versioned folder, copy the `data` directory, and start the new version. Keep the prior folder and a separately protected database backup made while the app is stopped. SQLite settings retain a last-valid configuration row; sanitized diagnostics intentionally contain no database backup. Roll back by stopping the new executable and starting the prior folder with the matching protected database backup.
