[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Repository,
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-f0-9]{40}$')][string] $Commit,
    [Parameter(Mandatory = $true)][string] $Destination
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$target = [IO.Path]::GetFullPath($Destination)
$archive = "$target.source.zip"
if ((Test-Path -LiteralPath $target) -or (Test-Path -LiteralPath $archive)) {
    throw 'Release source destination already exists.'
}
$resolvedCommit = (git -C $Repository rev-parse "$Commit^{commit}").Trim()
if ($LASTEXITCODE -ne 0 -or $resolvedCommit -ne $Commit) { throw 'Source commit cannot be verified.' }
$entries = @(git -C $Repository ls-tree -r $Commit)
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate tracked source.' }
if (@($entries | Where-Object { $_ -notmatch '^100(644|755) blob ' }).Count -gt 0) {
    throw 'Release source cannot contain symbolic links, submodules or special entries.'
}

# git archive reads the commit objects: working-tree, ignored and untracked files
# cannot affect compilation or recursively copied release documentation.
git -C $Repository archive --format=zip "--output=$archive" $Commit
if ($LASTEXITCODE -ne 0) { throw 'Cannot export tracked release source.' }
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $target)
    (Get-Item -LiteralPath $target).FullName
}
finally {
    Remove-Item -LiteralPath $archive -Force
}
