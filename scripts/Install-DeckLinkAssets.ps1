[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string] $ArchivePath,
    [string] $ApplicationRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $DataDirectory = 'data',
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-FileSha256([string] $Path) {
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '') }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

$archive = (Resolve-Path -LiteralPath $ArchivePath).Path
$appRoot = (Resolve-Path -LiteralPath $ApplicationRoot).Path
$dataRoot = if ([IO.Path]::IsPathRooted($DataDirectory)) {
    [IO.Path]::GetFullPath($DataDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $appRoot $DataDirectory))
}
$destination = Join-Path $dataRoot 'decklink-assets'

if ([IO.Path]::GetExtension($archive) -ne '.zip') { throw 'The DeckLink asset pack must be a ZIP archive.' }
if ((Test-Path -LiteralPath $destination) -and -not $Force) {
    throw "DeckLink assets already exist at '$destination'. Re-run with -Force to replace them after a validated backup is created."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $files = @($zip.Entries | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Name) })
    if ($files.Count -eq 0) { throw 'The archive contains no files.' }
    foreach ($entry in $files) {
        $name = $entry.FullName.Replace('\', '/')
        if ($name.StartsWith('/') -or $name -match '^[A-Za-z]:' -or $name.Split('/') -contains '..') {
            throw "Unsafe archive entry rejected: $($entry.FullName)"
        }
    }
    $roots = @($files | ForEach-Object { $_.FullName.Replace('\', '/').Split('/')[0] } | Sort-Object -Unique)
    if ($roots.Count -ne 1) { throw 'The archive must contain one top-level asset-pack folder.' }
    $archiveRootName = $roots[0]
    if (-not ($files | Where-Object { $_.FullName.Replace('\', '/') -eq "$archiveRootName/manifest.min.json" })) {
        throw 'The archive does not contain manifest.min.json in its top-level asset-pack folder.'
    }
}
finally {
    $zip.Dispose()
}

$staging = Join-Path ([IO.Path]::GetTempPath()) ('BroadcastRouter-decklink-assets-staging-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($staging) | Out-Null
try {
    Expand-Archive -LiteralPath $archive -DestinationPath $staging -WhatIf:$false
    $extractedRoot = Join-Path $staging $archiveRootName
    $manifestPath = Join-Path $extractedRoot 'manifest.min.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $models = @($manifest.models)
    if ($models.Count -eq 0) { throw 'The manifest contains no DeckLink models.' }

    $verifiedAssets = 0
    foreach ($model in $models) {
        foreach ($kind in @('product', 'connections', 'physical', 'accessories')) {
            $assetProperty = $model.assets.PSObject.Properties[$kind]
            $asset = if ($null -eq $assetProperty) { $null } else { $assetProperty.Value }
            if ($null -eq $asset) { continue }
            $relative = [string]$asset.path
            if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or $relative.Replace('\', '/').Split('/') -contains '..') {
                throw "Unsafe $kind path for model '$($model.name)'."
            }
            $assetPath = [IO.Path]::GetFullPath((Join-Path $extractedRoot $relative))
            $rootPrefix = $extractedRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
            if (-not $assetPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
                throw "Missing or unsafe $kind asset for model '$($model.name)'."
            }
            if ([IO.Path]::GetExtension($assetPath).ToLowerInvariant() -notin @('.jpg', '.jpeg', '.png', '.webp')) {
                throw "Unsupported image type for '$relative'."
            }
            if (-not [string]::IsNullOrWhiteSpace([string]$asset.sha256)) {
                $actualHash = Get-FileSha256 $assetPath
                if (-not $actualHash.Equals(([string]$asset.sha256), [StringComparison]::OrdinalIgnoreCase)) {
                    throw "SHA-256 mismatch for '$relative'."
                }
            }
            $verifiedAssets++
        }
    }
    if ($verifiedAssets -eq 0) { throw 'The manifest did not reference any valid image assets.' }

    if ($PSCmdlet.ShouldProcess($destination, "Install $($models.Count) DeckLink model entries and $verifiedAssets verified images")) {
        New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
        $backup = $null
        if (Test-Path -LiteralPath $destination) {
            $backup = "$destination.backup-$(Get-Date -Format 'yyyyMMdd-HHmmssfff')"
            Move-Item -LiteralPath $destination -Destination $backup
        }
        try {
            Move-Item -LiteralPath $extractedRoot -Destination $destination
        }
        catch {
            if ($null -ne $backup -and -not (Test-Path -LiteralPath $destination) -and (Test-Path -LiteralPath $backup)) {
                Move-Item -LiteralPath $backup -Destination $destination
            }
            throw
        }
        [pscustomobject]@{
            Destination = $destination
            Models = $models.Count
            VerifiedAssets = $verifiedAssets
            PreviousAssetsBackup = $backup
            RestartRequired = $false
            RightsNotice = 'Images remain subject to Blackmagic Design terms. Do not redistribute without permission.'
        } | Format-List
    }
}
finally {
    if ([IO.Directory]::Exists($staging)) { [IO.Directory]::Delete($staging, $true) }
}
