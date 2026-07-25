# Contributing

1. Create a focused branch from `main`.
2. Keep production behavior fail-closed: no simulated sources, network scanning, or hardware starts by default.
3. Never commit databases, credentials, diagnostics archives, proprietary SDK files, FFmpeg binaries, or Blackmagic redistributables.
4. Run the complete validation before opening a pull request:

```powershell
dotnet restore .\BroadcastRouter.sln
dotnet build .\BroadcastRouter.sln --configuration Release
dotnet run --project .\tests\BroadcastRouter.Tests\BroadcastRouter.Tests.csproj --configuration Release
```

Changes to routing, process ownership, persistence, security, or identity reconciliation require regression coverage and an update to the relevant document in `docs`.
