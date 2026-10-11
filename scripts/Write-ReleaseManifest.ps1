[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PackageDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $Commit,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string] $SdkVersion,
    [Parameter(Mandatory)][string] $FFmpegQualificationFile
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path -LiteralPath $PackageDirectory).Path
$rootItem = Get-Item -LiteralPath $root
if (-not $rootItem.PSIsContainer -or ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Package root must be a real directory, not a link.'
}
$root = $rootItem.FullName
$destination = Join-Path $root 'release-manifest.json'
if (Test-Path -LiteralPath $destination) { throw 'A release manifest already exists; replacement is forbidden.' }
$qualification = Get-Content -LiteralPath $FFmpegQualificationFile -Raw | ConvertFrom-Json
if ($qualification.status -notin @('qualified', 'not-qualified')) { throw 'Unknown FFmpeg qualification status.' }
if ($qualification.status -eq 'qualified' -and (
    [string]$qualification.buildId -notmatch '^[A-Za-z0-9][A-Za-z0-9._+-]{0,127}$' -or
    [string]$qualification.sha256 -notmatch '^[0-9a-fA-F]{64}$')) {
    throw 'Qualified FFmpeg requires a safe build ID and SHA-256 digest.'
}
if ($qualification.status -eq 'not-qualified' -and ($null -ne $qualification.buildId -or $null -ne $qualification.sha256)) {
    throw 'Unqualified metadata must not imply an accepted FFmpeg build.'
}

$prefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$paths = [Collections.Generic.List[string]]::new()
foreach ($item in Get-ChildItem -LiteralPath $root -Recurse -Force) {
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package links are forbidden.' }
    if (-not $item.PSIsContainer) {
        if (-not $item.FullName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Package file escapes its root.'
        }
        $paths.Add($item.FullName.Substring($prefix.Length).Replace('\', '/'))
    }
}
$paths.Sort([StringComparer]::Ordinal)
$files = @(foreach ($relative in $paths) {
    $path = Join-Path $root $relative
    [ordered]@{
        path = $relative
        length = (Get-Item -LiteralPath $path).Length
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
$manifest = [ordered]@{
    schemaVersion = 1
    commit = $Commit
    sdkVersion = $SdkVersion
    ffmpeg = [ordered]@{
        status = $qualification.status
        buildId = $qualification.buildId
        sha256 = $qualification.sha256
    }
    files = $files
}
# CreateNew prevents a race from replacing a previously published manifest.
$bytes = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 8) + "`n")
$stream = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
finally { $stream.Dispose() }
