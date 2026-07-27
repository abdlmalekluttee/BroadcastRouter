[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string] $PackageDirectory
)

$ErrorActionPreference = 'Stop'
$resolvedPackage = (Resolve-Path -LiteralPath $PackageDirectory).Path
$forbiddenNames = [ordered]@{
    'Production database' = '(?i)(^|[\\/])(?:data[\\/].*|[^\\/]*\.(?:db|sqlite|sqlite3)(?:-(?:shm|wal))?)$'
    'Diagnostic or log artifact' = '(?i)(^|[\\/])(?:diagnostics?|credentials?|rollback|backup|crash)(?:[-_.][^\\/]*)?\.(?:zip|json|xml|txt|log|dmp|etl)$|\.(?:log|dmp|etl)$'
    'Private key or certificate' = '(?i)\.(?:pfx|p12|pem|key)$'
    'Environment settings override' = '(?i)(^|[\\/])appsettings\.(?:Development|Production|Staging|Local)\.json$'
}
$forbiddenContent = [ordered]@{
    'Windows user-profile path' = '(?i)[A-Z]:\\Users\\'
    'Embedded URL credential' = '(?i)(?:rtsp|rtsps|https?)://[^\s/:@]+:[^\s/@]+@'
    'Private IPv4 address' = '(?<!\d)(?:192\.168\.(?:\d{1,3}\.)\d{1,3}|172\.(?:1[6-9]|2\d|3[01])\.(?:\d{1,3}\.)\d{1,3})(?!\d)'
    'Private key material' = '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'
    'GitHub credential' = '(?i)(?:github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9_]{20,})'
    'Common cloud credential' = '(?:AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{30,}|xox[baprs]-[0-9A-Za-z-]{10,})'
}

$findings = [Collections.Generic.List[object]]::new()
$textExtensions = @('.config', '.css', '.html', '.htm', '.ini', '.js', '.json', '.md', '.ps1', '.txt', '.xml', '.yaml', '.yml')

foreach ($file in Get-ChildItem -LiteralPath $resolvedPackage -Recurse -File) {
    $relative = [IO.Path]::GetRelativePath($resolvedPackage, $file.FullName)
    foreach ($rule in $forbiddenNames.GetEnumerator()) {
        if ($relative -match $rule.Value) {
            $findings.Add([pscustomobject]@{ File = $relative; Rule = $rule.Key })
        }
    }

    if ($file.Extension.ToLowerInvariant() -in $textExtensions) {
        $text = [IO.File]::ReadAllText($file.FullName)
        foreach ($rule in $forbiddenContent.GetEnumerator()) {
            if ($text -match $rule.Value) {
                $findings.Add([pscustomobject]@{ File = $relative; Rule = $rule.Key })
            }
        }
    }
}

if ($findings.Count -gt 0) {
    $summary = $findings |
        Sort-Object File, Rule -Unique |
        ForEach-Object { "- $($_.File): $($_.Rule)" }
    throw "Release privacy check failed:`n$($summary -join "`n")"
}

Write-Host "Release privacy check passed: $resolvedPackage" -ForegroundColor Green
