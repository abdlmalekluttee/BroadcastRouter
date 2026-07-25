param(
    [Parameter(Mandatory = $true)] [string] $ExecutablePath,
    [string] $ServiceName = "BroadcastRouter"
)
$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath -ErrorAction Stop).Path
if ([IO.Path]::GetFileName($resolvedExecutable) -ne "BroadcastRouter.Server.exe") { throw "ExecutablePath must select BroadcastRouter.Server.exe." }
New-Service -Name $ServiceName -BinaryPathName ('"' + $resolvedExecutable + '"') -DisplayName "BroadcastRouter" -Description "ASP.NET Core Wowza-to-DeckLink routing host (Session 0 DeckLink access is unverified)" -StartupType AutomaticDelayedStart
Write-Warning "Service installed but DeckLink access from Session 0 is NOT certified. Complete docs\PRODUCTION-VALIDATION.md before broadcast use."
