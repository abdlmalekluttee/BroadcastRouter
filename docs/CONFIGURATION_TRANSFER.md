# Fresh-database deployment

This is an offline preparation tool, not an upgrade applied to the running service.
Keep the original installation and verified backups for rollback. Do not copy its
database into the new installation. No diagnostics, route history, audit history,
discovered-device cache or active process state is migrated.

Build `tools/BroadcastRouter.ConfigurationTransfer` with .NET 8, then:

```powershell
dotnet run --project tools/BroadcastRouter.ConfigurationTransfer -- export "SOURCE.db" "PRIVATE.json"
# Owner reviews PRIVATE.json before deployment.
dotnet run --project tools/BroadcastRouter.ConfigurationTransfer -- import "PRIVATE.json" "NEW.db"
```

Export opens the source SQLite database read-only and reads settings and saved
routing intent in a single snapshot. It does not initialize or alter that source.
The export file is **private operational configuration**, not a public attachment:
it contains server addresses, user names, device identities and source labels.
Keep it under the operator's restricted local directory; never commit or upload it.

Review all exported Wowza profiles, card names, connector/output settings, standby
labels and asset paths, presets (especially audio-disabled presets), saved routes,
rules, manual sources and source-media overrides. Routing policy values are also
retained. An automatic runtime assignment is not a saved route. Legacy saved
assignments are converted to explicit desired-port intent.

No DPAPI password blob is exported. Embedded URL user information is removed.
Connection URLs containing query/fragment data are refused: those may contain
credentials and require manual re-entry. The source remains unchanged. Unknown
transfer fields/versions are refused, not silently ignored.

Import requires a new path with no existing database or SQLite sidecars. It builds
a separate staging database, validates the configuration, checkpoints and closes
it, then publishes it by a same-volume, no-overwrite rename. On failure the new
destination remains absent; a process crash may leave an unreferenced `.import-*`
file for investigation. Never point the service at a staging file.

The fresh install intentionally starts with:

- All Wowza profiles and manual-source connections disabled; re-enter passwords
  using the new installation's DPAPI account before enabling intended profiles.
- Automatic routing disabled, media executable paths unset, authentication
  required and the default loopback-only bind. Configure these explicitly.
- Saved routing intent in **Waiting for stream**, no active port lease or counters.
- Empty diagnostic, route-history, configuration-audit and discovered-cache tables.

The transfer does not copy logo/media files. Validate every referenced asset and
DeckLink persistent identity on the new installation before enabling outputs.
Port/output selections and saved intent are preserved, not silently remapped.
Import does not start services, open media, change the running service path or
deploy anything. Only an explicitly approved deployment may activate this DB.

## Release gate

Run synthetic 102,001-row and 2,000,000-row backlog recovery, crash/resume, essential
data preservation, configuration round-trip and import-failure tests. Retain the
private owner-reviewed configuration inventory. A production DB copy, sanitizer,
legacy catch-up migration and private/public release attestation are **not** required
or authorized by this fresh-database deployment plan.

Runtime diagnostic maintenance commits at most 1,000 removed rows per transaction,
releasing the shared writer gate between catch-up batches. A later insertion error
cannot roll back earlier batches. Disk-reserve guards remain unchanged. Catch-up
has a 300-second test ceiling; subsequent flush has a five-second ceiling; essential
writes must complete within two seconds. Historical loss/early eviction remains
visible rather than being cleared by successful recovery.
