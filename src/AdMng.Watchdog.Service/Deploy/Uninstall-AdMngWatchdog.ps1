#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes the AdMng Watchdog Windows service (docs/dev/watchdog-spec.md, section 19.6).

.DESCRIPTION
    Stops and deletes the service and the firewall rule. The data folder (database, key ring, backups) and the
    bootstrap configuration are kept unless -RemoveData is given; the program files are kept unless -RemoveFiles.
    Supports -WhatIf.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $InstallPath = (Join-Path $env:ProgramFiles 'AdMng\Watchdog'),

    [string] $DataDirectory = (Join-Path $env:ProgramData 'AdMng\Watchdog\data'),

    # Also delete the data folder (events, snapshots, secrets, backups) and the configuration – irreversible.
    [switch] $RemoveData,

    # Also delete the program files.
    [switch] $RemoveFiles
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$ServiceName = 'AdMngWatchdog'
$Root = Join-Path $env:ProgramData 'AdMng\Watchdog'

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped' -and $PSCmdlet.ShouldProcess($ServiceName, 'Stop')) {
        Stop-Service -Name $ServiceName -Force
    }

    if ($PSCmdlet.ShouldProcess($ServiceName, 'Delete service')) {
        & sc.exe delete $ServiceName | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed with exit code $LASTEXITCODE." }
    }
}
else {
    Write-Host "The service $ServiceName is not installed."
}

$rule = Get-NetFirewallRule -Name 'AdMngWatchdog-HTTPS' -ErrorAction SilentlyContinue
if ($rule -and $PSCmdlet.ShouldProcess('AdMngWatchdog-HTTPS', 'Remove firewall rule')) {
    $rule | Remove-NetFirewallRule
}

if ($RemoveData) {
    foreach ($path in @($DataDirectory, $Root)) {
        if ((Test-Path -LiteralPath $path) -and $PSCmdlet.ShouldProcess($path, 'Delete (data and configuration)')) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
}
else {
    Write-Host "Kept $DataDirectory and the configuration (use -RemoveData to delete them)."
}

if ($RemoveFiles -and (Test-Path -LiteralPath $InstallPath) -and $PSCmdlet.ShouldProcess($InstallPath, 'Delete program files')) {
    Remove-Item -LiteralPath $InstallPath -Recurse -Force
}
