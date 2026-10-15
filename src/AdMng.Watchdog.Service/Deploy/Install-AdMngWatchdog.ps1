#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs or updates the AdMng Watchdog Windows service (docs/dev/watchdog-spec.md, section 19.6).

.DESCRIPTION
    Idempotent; supports -WhatIf. Keep this file pure ASCII: Windows PowerShell 5.1 reads scripts without a BOM in the
    ANSI code page. Steps:
      1. checks the gMSA (Test-ADServiceAccount, when the ActiveDirectory module exists),
      2. copies the published files to -InstallPath,
      3. creates %ProgramData%\AdMng\Watchdog with config\ and the data folder: owner Administrators (also of anything
         created there before), protected DACLs by SID (no localized names) that replace every other ACE,
      4. writes config\appsettings.json: an existing file is MERGED - only the settings given as parameters change,
         everything else (DeniedSids, ViewerDetailLevel, Notifications, Directory, Server=HttpSys, ...) is kept; the
         file is written as a new file, so it carries only the ACEs of the protected config\ folder,
      5. creates the Application log source "AdMng Watchdog",
      6. grants "Log on as a service" (SeServiceLogonRight) to the gMSA,
      7. grants the gMSA Read on the private key of the TLS certificate the service will select (by thumbprint, else
         by subject - the same rule as the service; Kestrel only),
      8. excludes AdMng.Watchdog.exe from Windows Error Reporting dumps (they could hold secrets / directory data),
      9. creates or reconfigures the service (delayed-auto, gMSA without password, recovery actions, sidtype),
     10. creates the inbound firewall rule for the admin and monitoring subnets (Domain profile),
     11. checks that SPN HTTP/<host> is registered on the gMSA only,
     12. starts the service and runs "AdMng.Watchdog.exe --health".

.EXAMPLE
    .\Install-AdMngWatchdog.ps1 -ServiceAccount 'CONTOSO\svc-admngwd$' -Url 'https://admng-wd.contoso.com:8443' `
        -CertificateThumbprint 'A1B2...' -ViewerGroupSid 'S-1-5-21-...-1301' -AdminGroupSid 'S-1-5-21-...-1302' `
        -HostGroupSid 'S-1-5-21-...-1303' -AllowedSubnets '10.10.0.0/16' -MonitoringSubnets '10.20.30.0/24'
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
Import-Module (Join-Path $PSScriptRoot 'AdMngWatchdog.Install.psm1') -Force

$ServiceName = 'AdMngWatchdog'
$DisplayName = 'AdMng Watchdog'
$EventSource = 'AdMng Watchdog'
$ExeName = 'AdMng.Watchdog.exe'
$Parent = Join-Path $env:ProgramData 'AdMng'
$Root = Join-Path $Parent 'Watchdog'
$ConfigDir = Join-Path $Root 'config'
$ConfigFile = Join-Path $ConfigDir 'appsettings.json'
$SystemSid = 'S-1-5-18'
$AdminsSid = 'S-1-5-32-544'
$UsersSid = 'S-1-5-32-545'

function Write-Step([string] $Text) { Write-Host "==> $Text" -ForegroundColor Cyan }

function Get-AccountSid([string] $Account) {
    ([System.Security.Principal.NTAccount] $Account).Translate([System.Security.Principal.SecurityIdentifier]).Value
}

function Invoke-Native([string] $File, [string[]] $Arguments) {
    & $File @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$File $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
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
        throw "Test-ADServiceAccount failed for $ServiceAccount - add this computer to PrincipalsAllowedToRetrieveManagedPassword."
    }
}
else {
    Write-Warning 'The ActiveDirectory module is not installed - the gMSA could not be tested.'
}
$serviceSid = Get-AccountSid $ServiceAccount

# The bootstrap settings: the existing file merged with the parameters given (re-running the script is the update path,
# so nothing an administrator set by hand - DeniedSids above all - may disappear).
$before = Read-AdMngBootstrap -Path $ConfigFile
$beforeLines = @(Get-AdMngSettingLines -Tree $before)
$set = [ordered]@{
    'Watchdog.Hosting.Urls'           = $Url
    'Watchdog.Authorization.AdminSids' = @($AdminGroupSid)
}
if ($PSBoundParameters.ContainsKey('DataDirectory')) { $set['Watchdog.DataDirectory'] = $DataDirectory }
if ($PSBoundParameters.ContainsKey('CertificateThumbprint')) { $set['Watchdog.Hosting.Certificate.Thumbprint'] = ($CertificateThumbprint -replace '\s', '') }
if ($PSBoundParameters.ContainsKey('ViewerGroupSid')) { $set['Watchdog.Authorization.ViewerSids'] = @($ViewerGroupSid) }
if ($PSBoundParameters.ContainsKey('HostGroupSid')) { $set['Watchdog.Authorization.WatchdogHostGroupSids'] = @($HostGroupSid) }
if ($PSBoundParameters.ContainsKey('AllowedSubnets')) { $set['Watchdog.Hosting.AllowedRemoteSubnets'] = @($AllowedSubnets) }
if ($PSBoundParameters.ContainsKey('MonitoringSubnets')) { $set['Watchdog.Hosting.MonitoringSubnets'] = @($MonitoringSubnets) }
$defaults = [ordered]@{
    'Watchdog.DataDirectory'                      = $DataDirectory
    'Watchdog.Authentication'                     = 'Negotiate'
    'Watchdog.Hosting.Server'                     = 'Kestrel'
    'Watchdog.Hosting.RequireKerberos'            = $true
    'Watchdog.Hosting.AllowedRemoteSubnets'       = @()
    'Watchdog.Hosting.MonitoringSubnets'          = @()
    'Watchdog.Authorization.ViewerSids'           = @()
    'Watchdog.Authorization.WatchdogHostGroupSids' = @()
}
if (-not (Test-AdMngSetting -Tree $before -Path 'Watchdog.Hosting.Certificate.Thumbprint') -and -not $set.Contains('Watchdog.Hosting.Certificate.Thumbprint')) {
    $defaults['Watchdog.Hosting.Certificate.Subject'] = $hostName
}
$bootstrap = Merge-AdMngBootstrap -Tree (Read-AdMngBootstrap -Path $ConfigFile) -Set $set -Defaults $defaults
$effectiveDataDirectory = [Environment]::ExpandEnvironmentVariables([string] $bootstrap['Watchdog']['DataDirectory'])

# 2. Files.
Write-Step "Copying the service to $InstallPath"
if (-not (Test-Path -LiteralPath (Join-Path $SourcePath $ExeName))) { throw "$ExeName was not found in $SourcePath (use -SourcePath)." }
if ($PSCmdlet.ShouldProcess($InstallPath, 'Copy service files')) {
    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existing -and $existing.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
    New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
    Copy-Item -Path (Join-Path $SourcePath '*') -Destination $InstallPath -Recurse -Force
}

# 3. Folders and ACLs: owner Administrators recursively, protected DACLs that replace every other ACE - a local user
# who created %ProgramData%\AdMng (Users may create folders there) before the installation keeps no rights.
Write-Step "Creating $Root with protected ACLs"
if ($PSCmdlet.ShouldProcess($Root, 'Create folders and set ACLs')) {
    Set-AdMngProtectedAcl -Path $Parent -Grants @("${SystemSid}|FullControl|true", "${AdminsSid}|FullControl|true", "${UsersSid}|ReadAndExecute|false", "${serviceSid}|ReadAndExecute|false")
    Set-AdMngProtectedAcl -Path $Root -Grants @("${SystemSid}|FullControl|true", "${AdminsSid}|FullControl|true", "${serviceSid}|ReadAndExecute|false")
    Set-AdMngProtectedAcl -Path $ConfigDir -Grants @("${SystemSid}|FullControl|true", "${AdminsSid}|FullControl|true", "${serviceSid}|Read|true")
    Set-AdMngProtectedAcl -Path $effectiveDataDirectory -Grants @("${SystemSid}|FullControl|true", "${AdminsSid}|FullControl|true", "${serviceSid}|Modify|true")
}

# 4. Bootstrap configuration (read-only for the service), merged with the existing file.
Write-Step 'Writing config\appsettings.json'
$afterLines = @(Get-AdMngSettingLines -Tree $bootstrap)
$changed = @($afterLines | Where-Object { $beforeLines -notcontains $_ })
if ($PSCmdlet.ShouldProcess($ConfigFile, 'Write bootstrap configuration')) {
    Write-AdMngBootstrap -Path $ConfigFile -Tree $bootstrap
}
foreach ($line in $changed) { Write-Host "    set  $line" }
Write-Host ("    kept {0} other setting(s)" -f (@($afterLines).Count - $changed.Count))

# 5. Event source (New-EventLog does not exist in PowerShell 7).
Write-Step "Registering the Application log source '$EventSource'"
if (-not [System.Diagnostics.EventLog]::SourceExists($EventSource)) {
    if ($PSCmdlet.ShouldProcess($EventSource, 'Create event source')) {
        [System.Diagnostics.EventLog]::CreateEventSource($EventSource, 'Application')
    }
}

# 6. Log on as a service (a domain GPO that defines this right replaces local assignments - add the gMSA there too).
Write-Step 'Granting SeServiceLogonRight'
if ($PSCmdlet.ShouldProcess($ServiceAccount, 'Grant SeServiceLogonRight')) {
    [AdMngWatchdogInstall]::GrantRight($serviceSid, 'SeServiceLogonRight')
}

# 7. Private key of the TLS certificate the service will select (Kestrel; HTTP.sys uses the netsh sslcert binding).
$hosting = $bootstrap['Watchdog']['Hosting']
if ([string] $hosting['Server'] -eq 'HttpSys') {
    Write-Step 'Server=HttpSys: no private key access needed (bind the certificate with netsh http add sslcert)'
}
else {
    Write-Step 'Granting the service account Read on the certificate private key'
    $certificateSettings = if ($hosting.Contains('Certificate') -and ($hosting['Certificate'] -is [System.Collections.IDictionary])) { $hosting['Certificate'] } else { [ordered]@{} }
    $thumbprint = if ($certificateSettings.Contains('Thumbprint')) { [string] $certificateSettings['Thumbprint'] } else { '' }
    $subject = if ($certificateSettings.Contains('Subject')) { [string] $certificateSettings['Subject'] } else { '' }
    $storeName = if ($certificateSettings.Contains('Store') -and $certificateSettings['Store']) { [string] $certificateSettings['Store'] } else { 'My' }
    $storeLocation = if ($certificateSettings.Contains('Location') -and $certificateSettings['Location']) { [string] $certificateSettings['Location'] } else { 'LocalMachine' }
    $certificate = Find-AdMngCertificate -Thumbprint $thumbprint -Subject $subject -StoreName $storeName -StoreLocation $storeLocation
    if (-not $certificate) {
        throw "No valid TLS certificate with a private key matches the configuration (thumbprint '$thumbprint', subject '$subject', $storeLocation\$storeName, serverAuth) - install the certificate or pass -CertificateThumbprint."
    }

    Write-Host "    certificate $($certificate.Thumbprint) ($($certificate.Subject), valid until $($certificate.NotAfter))"
    if ($PSCmdlet.ShouldProcess($certificate.Thumbprint, 'Grant Read on the private key')) {
        Grant-AdMngPrivateKeyRead -Certificate $certificate -Sid $serviceSid
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
    Invoke-Native 'sc.exe' @('description', $ServiceName, 'AdMng Watchdog - monitoring of Active Directory changes with notifications.')
    Invoke-Native 'sc.exe' @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/60000/restart/60000/restart/300000')
    Invoke-Native 'sc.exe' @('failureflag', $ServiceName, '1')
    Invoke-Native 'sc.exe' @('sidtype', $ServiceName, 'unrestricted')
}

# 10. Firewall (Domain profile, admin and monitoring subnets only) - from the effective (merged) settings.
Write-Step 'Creating the firewall rule AdMngWatchdog-HTTPS'
$remote = @(@($hosting['AllowedRemoteSubnets']) + @($hosting['MonitoringSubnets']) | Where-Object { $_ })
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
    Write-Warning "SPN HTTP/$hostName is registered on more than one account - Kerberos will fail (setspn -Q HTTP/$hostName)."
}

# 12. Start and check.
Write-Step 'Starting the service'
if ($PSCmdlet.ShouldProcess($ServiceName, 'Start')) {
    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 5
    & (Join-Path $InstallPath $ExeName) --health
    if ($LASTEXITCODE -ne 0) {
        Write-Warning 'The health check failed - see the Application log (source "AdMng Watchdog", event ids 1000-1010).'
    }
    else {
        Write-Host 'AdMng Watchdog is running.' -ForegroundColor Green
    }
}
