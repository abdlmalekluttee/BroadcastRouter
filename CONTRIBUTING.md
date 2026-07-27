# Contributing

1. Create a focused branch from `main`.
2. Keep production behavior fail-closed: no simulated sources, network scanning, or hardware starts by default.
3. Never commit databases, credentials, internal addresses, machine/user paths, production device identifiers or labels, diagnostics archives, proprietary SDK files, FFmpeg binaries, or Blackmagic redistributables. Tests and documentation must use clearly synthetic fixtures.
4. Run the complete validation before opening a pull request:

```powershell
dotnet restore .\BroadcastRouter.sln
dotnet build .\BroadcastRouter.sln --configuration Release
dotnet run --project .\tests\BroadcastRouter.Tests\BroadcastRouter.Tests.csproj --configuration Release
.\scripts\Publish-Release.ps1 -Version 0.0.0-ci -OutputRoot .\artifacts-ci
```

Changes to routing, process ownership, persistence, security, or identity reconciliation require regression coverage and an update to the relevant document in `docs`.
