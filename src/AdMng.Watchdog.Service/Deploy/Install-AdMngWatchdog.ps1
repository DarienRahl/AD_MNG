#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs or updates the AdMng Watchdog Windows service (docs/dev/watchdog-spec.md, section 19.6).

.DESCRIPTION
    Idempotent; supports -WhatIf. Steps:
      1. checks the gMSA (Test-ADServiceAccount, when the ActiveDirectory module exists),
      2. copies the published files to -InstallPath,
      3. creates %ProgramData%\AdMng\Watchdog with config\ and the data folder, ACLs by SID (no localized names),
      4. writes config\appsettings.json (URL, certificate, role groups, host group, subnets),
      5. creates the Application log source "AdMng Watchdog",
      6. grants "Log on as a service" (SeServiceLogonRight) to the gMSA,
      7. grants the gMSA Read on the TLS certificate's private key,
      8. excludes AdMng.Watchdog.exe from Windows Error Reporting dumps (they could hold secrets / directory data),
      9. creates or reconfigures the service (delayed-auto, gMSA without password, recovery actions, sidtype),
     10. creates the inbound firewall rule for the admin and monitoring subnets (Domain profile),
     11. checks that SPN HTTP/<host> is registered on the gMSA only,
     12. starts the service and runs "AdMng.Watchdog.exe --health".

.EXAMPLE
    .\Install-AdMngWatchdog.ps1 -ServiceAccount 'CONTOSO\svc-admngwd$' -Url 'https://admng-wd.contoso.com:8443' `
        -CertificateThumbprint 'A1B2…' -ViewerGroupSid 'S-1-5-21-…-1301' -AdminGroupSid 'S-1-5-21-…-1302' `
        -HostGroupSid 'S-1-5-21-…-1303' -AllowedSubnets '10.10.0.0/16' -MonitoringSubnets '10.20.30.0/24'
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[^\\]+\\[^\\]+\$$')]
    [string] $ServiceAccount,

    [string] $InstallPath = (Join-Path $env:ProgramFiles 'AdMng\Watchdog'),

    [string] $DataDirectory = (Join-Path $env:ProgramData 'AdMng\Watchdog\data'),

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^https://')]
    [string] $Url,

    [string] $CertificateThumbprint,

    [ValidatePattern('^S-1-\d+(-\d+)+$')]
    [string] $ViewerGroupSid,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^S-1-\d+(-\d+)+$')]
    [string] $AdminGroupSid,

    [ValidatePattern('^S-1-\d+(-\d+)+$')]
    [string] $HostGroupSid,

    [string[]] $AllowedSubnets = @(),

    [string[]] $MonitoringSubnets = @(),

    # The published service (the folder that contains AdMng.Watchdog.exe); default: the parent of this script.
    [string] $SourcePath = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$ServiceName = 'AdMngWatchdog'
$DisplayName = 'AdMng Watchdog'
$EventSource = 'AdMng Watchdog'
$ExeName = 'AdMng.Watchdog.exe'
$Root = Join-Path $env:ProgramData 'AdMng\Watchdog'
$ConfigDir = Join-Path $Root 'config'
$SystemSid = 'S-1-5-18'
$AdminsSid = 'S-1-5-32-544'

function Write-Step([string] $Text) { Write-Host "==> $Text" -ForegroundColor Cyan }

function Get-AccountSid([string] $Account) {
    ([System.Security.Principal.NTAccount] $Account).Translate([System.Security.Principal.SecurityIdentifier]).Value
}

function Invoke-Native([string] $File, [string[]] $Arguments) {
    & $File @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$File $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}

function Set-FolderAcl([string] $Path, [string[]] $Grants) {
    # Protected DACL (inheritance removed), owner Administrators, grants by SID ("*S-1-5-18:(OI)(CI)F").
    if (-not (Test-Path -LiteralPath $Path)) { New-Item -ItemType Directory -Path $Path -Force | Out-Null }
    Invoke-Native 'icacls.exe' (@($Path, '/inheritance:r', '/grant:r') + $Grants + @('/Q'))
    Invoke-Native 'icacls.exe' @($Path, '/setowner', "*$AdminsSid", '/Q')
}

Add-Type -ErrorAction Stop -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Security.Principal;

public static class AdMngWatchdogInstall
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LSA_UNICODE_STRING { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LSA_OBJECT_ATTRIBUTES { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint LsaOpenPolicy(IntPtr systemName, ref LSA_OBJECT_ATTRIBUTES attributes, int access, out IntPtr policy);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint LsaAddAccountRights(IntPtr policy, byte[] sid, LSA_UNICODE_STRING[] rights, int count);

    [DllImport("advapi32.dll")]
    private static extern uint LsaClose(IntPtr policy);

    [DllImport("advapi32.dll")]
    private static extern int LsaNtStatusToWinError(uint status);

    [DllImport("wer.dll", CharSet = CharSet.Unicode)]
    private static extern int WerAddExcludedApplication(string exeName, bool allUsers);

    public static void GrantRight(string sid, string right)
    {
        var attributes = new LSA_OBJECT_ATTRIBUTES();
        IntPtr policy;
        var status = LsaOpenPolicy(IntPtr.Zero, ref attributes, 0x00000800 | 0x00000010, out policy); // POLICY_CREATE_ACCOUNT | POLICY_LOOKUP_NAMES
        if (status != 0) { throw new InvalidOperationException("LsaOpenPolicy failed: " + LsaNtStatusToWinError(status)); }
        try
        {
            var identifier = new SecurityIdentifier(sid);
            var bytes = new byte[identifier.BinaryLength];
            identifier.GetBinaryForm(bytes, 0);
            var name = new LSA_UNICODE_STRING { Buffer = Marshal.StringToHGlobalUni(right), Length = (ushort)(right.Length * 2), MaximumLength = (ushort)((right.Length + 1) * 2) };
            try
            {
                status = LsaAddAccountRights(policy, bytes, new[] { name }, 1);
                if (status != 0) { throw new InvalidOperationException("LsaAddAccountRights failed: " + LsaNtStatusToWinError(status)); }
            }
            finally { Marshal.FreeHGlobal(name.Buffer); }
        }
        finally { LsaClose(policy); }
    }

    public static void ExcludeFromWer(string exeName)
    {
        var hr = WerAddExcludedApplication(exeName, true);
        if (hr != 0) { throw new InvalidOperationException("WerAddExcludedApplication failed: 0x" + hr.ToString("X8")); }
    }
}
'@

$uri = [Uri] $Url
$hostName = $uri.Host

# 1. The gMSA.
Write-Step "Checking the service account $ServiceAccount"
if (Get-Module -ListAvailable -Name ActiveDirectory) {
    Import-Module ActiveDirectory
    $samName = $ServiceAccount.Split('\')[1].TrimEnd('$')
    if (-not (Test-ADServiceAccount -Identity $samName)) {
        throw "Test-ADServiceAccount failed for $ServiceAccount – add this computer to PrincipalsAllowedToRetrieveManagedPassword."
    }
}
else {
    Write-Warning 'The ActiveDirectory module is not installed – the gMSA could not be tested.'
}
$serviceSid = Get-AccountSid $ServiceAccount

# 2. Files.
Write-Step "Copying the service to $InstallPath"
if (-not (Test-Path -LiteralPath (Join-Path $SourcePath $ExeName))) { throw "$ExeName was not found in $SourcePath (use -SourcePath)." }
if ($PSCmdlet.ShouldProcess($InstallPath, 'Copy service files')) {
    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existing -and $existing.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
    New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
    Copy-Item -Path (Join-Path $SourcePath '*') -Destination $InstallPath -Recurse -Force
}

# 3. Folders and ACLs (SIDs avoid localized names such as "Administratorzy").
Write-Step "Creating $Root with protected ACLs"
if ($PSCmdlet.ShouldProcess($Root, 'Create folders and set ACLs')) {
    Set-FolderAcl $Root @("*${SystemSid}:(OI)(CI)F", "*${AdminsSid}:(OI)(CI)F", "*${serviceSid}:RX")
    Set-FolderAcl $ConfigDir @("*${SystemSid}:(OI)(CI)F", "*${AdminsSid}:(OI)(CI)F", "*${serviceSid}:(OI)(CI)R")
    Set-FolderAcl $DataDirectory @("*${SystemSid}:(OI)(CI)F", "*${AdminsSid}:(OI)(CI)F", "*${serviceSid}:(OI)(CI)M")
}

# 4. Bootstrap configuration (read-only for the service).
Write-Step 'Writing config\appsettings.json'
$hosting = [ordered]@{
    Server               = 'Kestrel'
    Urls                 = $Url
    Certificate          = if ($CertificateThumbprint) { [ordered]@{ Thumbprint = $CertificateThumbprint } } else { [ordered]@{ Subject = $hostName } }
    RequireKerberos      = $true
    AllowedRemoteSubnets = @($AllowedSubnets)
    MonitoringSubnets    = @($MonitoringSubnets)
}
$authorization = [ordered]@{
    ViewerSids            = @(@($ViewerGroupSid) | Where-Object { $_ })
    AdminSids             = @($AdminGroupSid)
    WatchdogHostGroupSids = @(@($HostGroupSid) | Where-Object { $_ })
}
$bootstrap = [ordered]@{
    Watchdog = [ordered]@{
        DataDirectory  = $DataDirectory
        Authentication = 'Negotiate'
        Hosting        = $hosting
        Authorization  = $authorization
    }
}
$configFile = Join-Path $ConfigDir 'appsettings.json'
if ($PSCmdlet.ShouldProcess($configFile, 'Write bootstrap configuration')) {
    $bootstrap | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $configFile -Encoding UTF8
}

# 5. Event source (New-EventLog does not exist in PowerShell 7).
Write-Step "Registering the Application log source '$EventSource'"
if (-not [System.Diagnostics.EventLog]::SourceExists($EventSource)) {
    if ($PSCmdlet.ShouldProcess($EventSource, 'Create event source')) {
        [System.Diagnostics.EventLog]::CreateEventSource($EventSource, 'Application')
    }
}

# 6. Log on as a service (a domain GPO that defines this right replaces local assignments – add the gMSA there too).
Write-Step 'Granting SeServiceLogonRight'
if ($PSCmdlet.ShouldProcess($ServiceAccount, 'Grant SeServiceLogonRight')) {
    [AdMngWatchdogInstall]::GrantRight($serviceSid, 'SeServiceLogonRight')
}

# 7. Private key of the TLS certificate.
if ($CertificateThumbprint) {
    Write-Step 'Granting the service account Read on the certificate private key'
    $certificate = Get-Item -LiteralPath ("Cert:\LocalMachine\My\" + ($CertificateThumbprint -replace '\s', ''))
    $keyName = $null
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
    if ($rsa -is [System.Security.Cryptography.RSACng]) { $keyName = $rsa.Key.UniqueName }
    elseif ($rsa) { $keyName = $rsa.CspKeyContainerInfo.UniqueKeyContainerName }
    else {
        $ecdsa = [System.Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPrivateKey($certificate)
        if ($ecdsa -is [System.Security.Cryptography.ECDsaCng]) { $keyName = $ecdsa.Key.UniqueName }
    }
    if (-not $keyName) { throw 'The certificate has no accessible private key.' }
    $keyFile = @(
        (Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$keyName"),
        (Join-Path $env:ProgramData "Microsoft\Crypto\RSA\MachineKeys\$keyName")
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $keyFile) { throw "The private key file $keyName was not found." }
    if ($PSCmdlet.ShouldProcess($keyFile, 'Grant Read')) {
        Invoke-Native 'icacls.exe' @($keyFile, '/grant', "*${serviceSid}:R", '/Q')
    }
}

# 8. No WER dumps of the service process.
Write-Step 'Excluding the service from Windows Error Reporting dumps'
if ($PSCmdlet.ShouldProcess($ExeName, 'WerAddExcludedApplication')) {
    [AdMngWatchdogInstall]::ExcludeFromWer($ExeName)
}

# 9. The service.
Write-Step "Registering the service $ServiceName"
$binPath = '"' + (Join-Path $InstallPath $ExeName) + '"'
if ($PSCmdlet.ShouldProcess($ServiceName, 'Create or configure the service')) {
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Invoke-Native 'sc.exe' @('config', $ServiceName, 'binPath=', $binPath, 'start=', 'delayed-auto', 'obj=', $ServiceAccount, 'DisplayName=', $DisplayName)
    }
    else {
        Invoke-Native 'sc.exe' @('create', $ServiceName, 'binPath=', $binPath, 'start=', 'delayed-auto', 'obj=', $ServiceAccount, 'DisplayName=', $DisplayName)
    }
    Invoke-Native 'sc.exe' @('description', $ServiceName, 'AdMng Watchdog – monitoring of Active Directory changes with notifications.')
    Invoke-Native 'sc.exe' @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/60000/restart/60000/restart/300000')
    Invoke-Native 'sc.exe' @('failureflag', $ServiceName, '1')
    Invoke-Native 'sc.exe' @('sidtype', $ServiceName, 'unrestricted')
}

# 10. Firewall (Domain profile, admin and monitoring subnets only).
Write-Step 'Creating the firewall rule AdMngWatchdog-HTTPS'
$remote = @($AllowedSubnets) + @($MonitoringSubnets) | Where-Object { $_ }
if (-not $remote) { $remote = @('LocalSubnet') }
if ($PSCmdlet.ShouldProcess('AdMngWatchdog-HTTPS', 'Create firewall rule')) {
    Get-NetFirewallRule -Name 'AdMngWatchdog-HTTPS' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -Name 'AdMngWatchdog-HTTPS' -DisplayName 'AdMng Watchdog (HTTPS)' -Direction Inbound -Protocol TCP `
        -LocalPort $uri.Port -Profile Domain -RemoteAddress $remote -Action Allow | Out-Null
}

# 11. SPN HTTP/<host> must be registered on the gMSA only (Kerberos for the API).
Write-Step "Checking SPN HTTP/$hostName"
$spnOutput = & setspn.exe -Q "HTTP/$hostName" 2>&1 | Out-String
$samAccount = $ServiceAccount.Split('\')[1].TrimEnd('$')
if ($spnOutput -notmatch [Regex]::Escape("CN=$samAccount,")) {
    Write-Warning "SPN HTTP/$hostName is not registered on $ServiceAccount (setspn -S HTTP/$hostName $ServiceAccount)."
}
elseif (([Regex]::Matches($spnOutput, '(?m)^CN=')).Count -gt 1) {
    Write-Warning "SPN HTTP/$hostName is registered on more than one account – Kerberos will fail (setspn -Q HTTP/$hostName)."
}

# 12. Start and check.
Write-Step 'Starting the service'
if ($PSCmdlet.ShouldProcess($ServiceName, 'Start')) {
    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 5
    & (Join-Path $InstallPath $ExeName) --health
    if ($LASTEXITCODE -ne 0) {
        Write-Warning 'The health check failed – see the Application log (source "AdMng Watchdog", event ids 1000–1010).'
    }
    else {
        Write-Host 'AdMng Watchdog is running.' -ForegroundColor Green
    }
}
