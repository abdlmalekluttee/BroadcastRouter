[CmdletBinding()]
param([Parameter(Mandatory = $true)][string] $SourceRoot)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Get-Item -LiteralPath $SourceRoot).FullName
foreach ($entry in @(
    @('Directory.Build.props', 'DirectoryBuildPropsPath', 'ImportDirectoryBuildProps'),
    @('Directory.Build.targets', 'DirectoryBuildTargetsPath', 'ImportDirectoryBuildTargets'),
    @('Directory.Packages.props', 'DirectoryPackagesPropsPath', 'ImportDirectoryPackagesProps')
)) {
    $path = Join-Path $root $entry[0]
    if (Test-Path -LiteralPath $path -PathType Leaf) { "-property:$($entry[1])=$path" }
    else { "-property:$($entry[2])=false" }
}
$nuget = Join-Path $root 'NuGet.Config'
if (Test-Path -LiteralPath $nuget -PathType Leaf) { "-property:RestoreConfigFile=$nuget" }
