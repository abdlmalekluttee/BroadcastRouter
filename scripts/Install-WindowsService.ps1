[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)] [string] $ExecutablePath,
    [string] $ServiceName = "BroadcastRouter",
    [PSCredential] $Credential,
    [switch] $UseLocalSystem,
    [switch] $ReplaceExisting,
    [switch] $StartService
)

$ErrorActionPreference = "Stop"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Install-WindowsService.ps1 must be run from an elevated PowerShell session."
    }
}

function Grant-LogOnAsService([Security.Principal.SecurityIdentifier] $Sid) {
    if (-not ("BroadcastRouter.ServiceAccountRights" -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BroadcastRouter {
    public static class ServiceAccountRights {
        [StructLayout(LayoutKind.Sequential)]
        private struct LSA_OBJECT_ATTRIBUTES {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct LSA_UNICODE_STRING {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint LsaOpenPolicy(IntPtr systemName, ref LSA_OBJECT_ATTRIBUTES attributes,
            uint desiredAccess, out IntPtr policyHandle);

        [DllImport("advapi32.dll")]
        private static extern uint LsaAddAccountRights(IntPtr policyHandle, IntPtr accountSid,
            LSA_UNICODE_STRING[] userRights, uint countOfRights);

        [DllImport("advapi32.dll")]
        private static extern uint LsaNtStatusToWinError(uint status);

        [DllImport("advapi32.dll")]
        private static extern uint LsaClose(IntPtr policyHandle);

        public static void GrantLogOnAsService(byte[] sid) {
            const uint POLICY_LOOKUP_NAMES = 0x00000800;
            const uint POLICY_CREATE_ACCOUNT = 0x00000010;
            var attributes = new LSA_OBJECT_ATTRIBUTES { Length = Marshal.SizeOf<LSA_OBJECT_ATTRIBUTES>() };
            IntPtr policy;
            var status = LsaOpenPolicy(IntPtr.Zero, ref attributes,
                POLICY_LOOKUP_NAMES | POLICY_CREATE_ACCOUNT, out policy);
            if (status != 0) throw new Win32Exception((int)LsaNtStatusToWinError(status));

            var sidPointer = Marshal.AllocHGlobal(sid.Length);
            var rightBuffer = Marshal.StringToHGlobalUni("SeServiceLogonRight");
            try {
                Marshal.Copy(sid, 0, sidPointer, sid.Length);
                var right = new LSA_UNICODE_STRING {
                    Buffer = rightBuffer,
                    Length = (ushort)("SeServiceLogonRight".Length * 2),
                    MaximumLength = (ushort)(("SeServiceLogonRight".Length + 1) * 2)
                };
                status = LsaAddAccountRights(policy, sidPointer, new[] { right }, 1);
                if (status != 0) throw new Win32Exception((int)LsaNtStatusToWinError(status));
            }
            finally {
                Marshal.FreeHGlobal(rightBuffer);
                Marshal.FreeHGlobal(sidPointer);
                LsaClose(policy);
            }
        }
    }
}
'@
    }

    $bytes = New-Object byte[] $Sid.BinaryLength
    $Sid.GetBinaryForm($bytes, 0)
    [BroadcastRouter.ServiceAccountRights]::GrantLogOnAsService($bytes)
}

Assert-Administrator
if ([string]::IsNullOrWhiteSpace($ServiceName) -or $ServiceName -notmatch '^[A-Za-z0-9_.-]+$') {
    throw "ServiceName may contain only letters, numbers, dot, underscore, and hyphen."
}
if (($null -eq $Credential) -eq (-not $UseLocalSystem)) {
    throw "Specify exactly one of -Credential or -UseLocalSystem. Use the same Windows account as the existing application when DPAPI credentials are present."
}

$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath -ErrorAction Stop).Path
if ([IO.Path]::GetFileName($resolvedExecutable) -ne "BroadcastRouter.Server.exe") {
    throw "ExecutablePath must select BroadcastRouter.Server.exe."
}

$productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($resolvedExecutable).ProductVersion
$versionMatch = [regex]::Match([string]$productVersion, '^\d+\.\d+\.\d+')
$version = if ($versionMatch.Success) { $versionMatch.Value } else { "Unknown" }
$displayName = "Broadcast Router $([char]0x2013) Version $version"
$description = "BroadcastRouter Wowza-to-DeckLink routing host. Owned media processes run hidden and are contained by the host job object."

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and -not $ReplaceExisting) {
    throw "Service '$ServiceName' already exists. Use -ReplaceExisting for an intentional replacement."
}

if ($existing -and $PSCmdlet.ShouldProcess($ServiceName, "replace existing Windows service")) {
    if ($existing.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $ServiceName -Force
        (Get-Service -Name $ServiceName).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(60))
    }
    & sc.exe delete $ServiceName | Out-Null
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        throw "Existing service '$ServiceName' is still pending deletion."
    }
}

$newService = @{
    Name = $ServiceName
    BinaryPathName = ('"' + $resolvedExecutable + '"')
    DisplayName = $displayName
    Description = $description
    StartupType = 'Automatic'
}

if ($null -ne $Credential) {
    $account = $Credential.UserName
    if ($account -notmatch '[\\@]') { $account = "$env:COMPUTERNAME\$account" }
    $sid = ([Security.Principal.NTAccount]$account).Translate([Security.Principal.SecurityIdentifier])
    Grant-LogOnAsService $sid
    $Credential = [PSCredential]::new($account, $Credential.Password)
    $newService.Credential = $Credential
}

if ($PSCmdlet.ShouldProcess($ServiceName, "install automatic Windows service '$displayName'")) {
    New-Service @newService | Out-Null
    & sc.exe description $ServiceName $description | Out-Null
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
    & sc.exe failureflag $ServiceName 1 | Out-Null

    if (-not [Diagnostics.EventLog]::SourceExists("BroadcastRouter")) {
        New-EventLog -LogName Application -Source "BroadcastRouter"
    }

    if ($StartService) {
        Start-Service -Name $ServiceName
        (Get-Service -Name $ServiceName).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(60))
    }
}

$installed = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
[pscustomobject]@{
    ServiceName = $installed.Name
    DisplayName = $installed.DisplayName
    State = $installed.State
    StartMode = $installed.StartMode
    StartAccount = $installed.StartName
    Executable = $resolvedExecutable
    Recovery = (& sc.exe qfailure $ServiceName | Out-String).Trim()
    RecoveryOnNonCrashFailure = (& sc.exe qfailureflag $ServiceName | Out-String).Trim()
    SessionZeroDeckLinkValidationRequired = $true
}
