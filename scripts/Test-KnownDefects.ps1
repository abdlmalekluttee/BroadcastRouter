[CmdletBinding()]
param([string] $ResultsDirectory = '')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path ([IO.Path]::GetTempPath()) ("router-known-defects-" + [guid]::NewGuid().ToString('N'))
}
if (Test-Path -LiteralPath $ResultsDirectory) { throw 'Results directory must be new; stale results are not accepted.' }
New-Item -ItemType Directory -Path $ResultsDirectory | Out-Null
Push-Location -LiteralPath $repositoryRoot
try {
    dotnet build tests/BroadcastRouter.Tests/BroadcastRouter.Tests.csproj --configuration Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'KnownDefect test compilation failed.' }
    dotnet test tests/BroadcastRouter.Tests/BroadcastRouter.Tests.csproj --configuration Release --no-build `
        --filter Category=KnownDefect --logger 'trx;LogFileName=known.trx' --results-directory $ResultsDirectory
    $testExitCode = $LASTEXITCODE
    python (Join-Path $PSScriptRoot 'verify_known_defects.py') --registry tests/KnownDefects.json `
        --trx (Join-Path $ResultsDirectory 'known.trx') --exit-code $testExitCode
    if ($LASTEXITCODE -ne 0) { throw 'Strict KnownDefect contract failed. Never weaken assertions to silence this.' }
}
finally { Pop-Location }
