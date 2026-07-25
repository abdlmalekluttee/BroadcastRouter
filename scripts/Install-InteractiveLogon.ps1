param(
    [Parameter(Mandatory = $true)] [string] $ExecutablePath,
    [string] $TaskName = "BroadcastRouter",
    [string] $UserId = $env:USERNAME
)
$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath -ErrorAction Stop).Path
if ([IO.Path]::GetFileName($resolvedExecutable) -ne "BroadcastRouter.Server.exe") { throw "ExecutablePath must select BroadcastRouter.Server.exe." }
$action = New-ScheduledTaskAction -Execute $resolvedExecutable -WorkingDirectory (Split-Path -Parent $resolvedExecutable)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $UserId
$principal = New-ScheduledTaskPrincipal -UserId $UserId -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description "BroadcastRouter interactive DeckLink routing host" -Force -ErrorAction Stop
Write-Host "Installed scheduled task '$TaskName' for '$UserId'. DeckLink behavior must still pass production validation."
