[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string] $Version,
    [string] $Runtime = 'win-x64',
    [string] $OutputRoot = ''
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repositoryRoot 'release' }
$resolvedOutput = [IO.Path]::GetFullPath($OutputRoot)
$packageName = "BroadcastRouter-production-$Runtime-$Version"
$packageDirectory = Join-Path $resolvedOutput "$packageName-package"
$zipPath = Join-Path $resolvedOutput "$packageName.zip"
$hashPath = "$zipPath.sha256"

New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
if (Test-Path -LiteralPath $packageDirectory) { throw "Package directory already exists: $packageDirectory" }
if (Test-Path -LiteralPath $zipPath) { throw "Release archive already exists: $zipPath" }

dotnet publish (Join-Path $repositoryRoot 'src\BroadcastRouter.Web\BroadcastRouter.Web.csproj') `
    --configuration Release --runtime $Runtime --self-contained true `
    --property:Version=$Version --output $packageDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

# Portable PDBs can disclose local build paths and are not required at runtime.
Get-ChildItem -LiteralPath $packageDirectory -Filter '*.pdb' -File |
    Remove-Item -Force

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination $packageDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $packageDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'CHANGELOG.md') -Destination $packageDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'FFMPEG-SETUP.txt') -Destination $packageDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs') -Destination $packageDirectory -Recurse
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'scripts') -Destination $packageDirectory -Recurse

& (Join-Path $PSScriptRoot 'Test-ReleasePrivacy.ps1') -PackageDirectory $packageDirectory

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($packageDirectory, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
Set-Content -LiteralPath $hashPath -Value "$hash  $packageName.zip" -Encoding ascii

[pscustomobject]@{
    Version = $Version
    Runtime = $Runtime
    PackageDirectory = $packageDirectory
    Archive = $zipPath
    Sha256 = $hash
} | Format-List
