# Helper functions of Install-AdMngWatchdog.ps1 (docs/dev/watchdog-spec.md, section 19.6).
# Windows PowerShell 5.1 and PowerShell 7. Keep this file pure ASCII: Windows PowerShell 5.1 reads scripts without a
# BOM in the ANSI code page.

Set-StrictMode -Version 3.0

function ConvertTo-AdMngTree {
    <#
    .SYNOPSIS
        Converts the output of ConvertFrom-Json (PSCustomObject, arrays, scalars) into ordered, case-insensitive
        dictionaries and arrays that can be edited and written back with ConvertTo-Json.
    #>
    param($Value)

    if ($null -eq $Value) { return $null }
    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        $tree = [ordered]@{}
        foreach ($property in $Value.PSObject.Properties) {
            $tree[$property.Name] = ConvertTo-AdMngTree $property.Value
        }

        return $tree
    }

    if ($Value -is [System.Collections.IDictionary]) {
        $tree = [ordered]@{}
        foreach ($key in @($Value.Keys)) {
            $tree[[string] $key] = ConvertTo-AdMngTree $Value[$key]
        }

        return $tree
    }

    if (($Value -is [System.Collections.IEnumerable]) -and -not ($Value -is [string])) {
        $items = New-Object System.Collections.Generic.List[object]
        foreach ($item in $Value) { $items.Add((ConvertTo-AdMngTree $item)) }
        return , $items.ToArray()
    }

    return $Value
}

function Read-AdMngBootstrap {
    <#
    .SYNOPSIS
        The bootstrap file as an ordered tree; an empty tree when the file does not exist.
    #>
    param([Parameter(Mandatory = $true)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) { return [ordered]@{} }
    $text = [System.IO.File]::ReadAllText($Path)
    if ([string]::IsNullOrWhiteSpace($text)) { return [ordered]@{} }
    try {
        $parsed = $text | ConvertFrom-Json
    }
    catch {
        throw "$Path is not valid JSON for this script (comments and trailing commas are not supported) - fix it first: $($_.Exception.Message)"
    }

    $tree = ConvertTo-AdMngTree $parsed
    if (-not ($tree -is [System.Collections.IDictionary])) { throw "$Path does not contain a JSON object." }
    return $tree
}

function Test-AdMngSetting {
    <#
    .SYNOPSIS
        True when the dotted path (e.g. Watchdog.Hosting.Urls) exists in the tree (case-insensitive).
    #>
    param([Parameter(Mandatory = $true)] $Tree, [Parameter(Mandatory = $true)] [string] $Path)

    $node = $Tree
    foreach ($name in $Path.Split('.')) {
        if (-not ($node -is [System.Collections.IDictionary]) -or -not $node.Contains($name)) { return $false }
        $node = $node[$name]
    }

    return $true
}

function Set-AdMngSetting {
    <#
    .SYNOPSIS
        Sets the dotted path in the tree, creating the missing objects on the way.
    #>
    param([Parameter(Mandatory = $true)] $Tree, [Parameter(Mandatory = $true)] [string] $Path, $Value)

    $names = $Path.Split('.')
    $node = $Tree
    for ($i = 0; $i -lt $names.Length - 1; $i++) {
        $name = $names[$i]
        if (-not $node.Contains($name) -or -not ($node[$name] -is [System.Collections.IDictionary])) {
            $node[$name] = [ordered]@{}
        }

        $node = $node[$name]
    }

    # An assignment never unrolls: an array (also an empty one) stays an array in the JSON.
    $node[$names[-1]] = $Value
}

function Merge-AdMngBootstrap {
    <#
    .SYNOPSIS
        Applies the settings the install script was given ($Set: always written) and its defaults ($Defaults: written
        only where the file has no value) to the existing bootstrap tree. Every other setting - DeniedSids,
        ViewerDetailLevel, Notifications, Directory, a Server=HttpSys choice, certificate store - is kept.
    #>
    param(
        [Parameter(Mandatory = $true)] $Tree,
        [System.Collections.IDictionary] $Set = @{},
        [System.Collections.IDictionary] $Defaults = @{}
    )

    foreach ($path in @($Set.Keys)) { Set-AdMngSetting -Tree $Tree -Path $path -Value $Set[$path] }
    foreach ($path in @($Defaults.Keys)) {
        if (-not (Test-AdMngSetting -Tree $Tree -Path $path)) { Set-AdMngSetting -Tree $Tree -Path $path -Value $Defaults[$path] }
    }

    return $Tree
}

function Get-AdMngSettingLines {
    <#
    .SYNOPSIS
        "path = json" for every leaf of the tree (for the change summary).
    #>
    param($Tree, [string] $Prefix = '')

    $lines = @()
    if ($Tree -is [System.Collections.IDictionary]) {
        foreach ($key in @($Tree.Keys)) {
            $path = if ($Prefix) { "$Prefix.$key" } else { [string] $key }
            $lines += Get-AdMngSettingLines -Tree $Tree[$key] -Prefix $path
        }

        return $lines
    }

    return @("$Prefix = " + (ConvertTo-Json -InputObject $Tree -Compress -Depth 10))
}

function Set-AdMngProtectedAcl {
    <#
    .SYNOPSIS
        Replaces the whole DACL of a folder with a protected one (no inherited and no other explicit ACEs) and makes
        Administrators the owner of the folder and of everything below it - so nobody who created the folder or a file
        in it before the installation keeps the owner's implicit right to change permissions.
    .PARAMETER Grants
        Entries "SID|Rights|Inherit", e.g. "S-1-5-18|FullControl|true".
    #>
    param([Parameter(Mandatory = $true)] [string] $Path, [Parameter(Mandatory = $true)] [string[]] $Grants)

    if (-not (Test-Path -LiteralPath $Path)) { New-Item -ItemType Directory -Path $Path -Force | Out-Null }
    & icacls.exe $Path /setowner '*S-1-5-32-544' /T /C /Q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "icacls /setowner failed on $Path with exit code $LASTEXITCODE." }

    $security = New-Object System.Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner((New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-32-544'))
    foreach ($grant in $Grants) {
        $sid, $rights, $inherit = $grant.Split('|')
        $inheritance = if ($inherit -eq 'true') { 'ContainerInherit, ObjectInherit' } else { 'None' }
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            (New-Object System.Security.Principal.SecurityIdentifier $sid),
            [System.Security.AccessControl.FileSystemRights] $rights,
            [System.Security.AccessControl.InheritanceFlags] $inheritance,
            [System.Security.AccessControl.PropagationFlags]::None,
            [System.Security.AccessControl.AccessControlType]::Allow)
        $security.AddAccessRule($rule)
    }

    Set-Acl -LiteralPath $Path -AclObject $security
}

function Write-AdMngBootstrap {
    <#
    .SYNOPSIS
        Writes the bootstrap file as a NEW file (UTF-8 without BOM), so it carries only the ACEs it inherits from the
        protected config folder - never the owner or ACL of a file someone created there before.
    #>
    param([Parameter(Mandatory = $true)] [string] $Path, [Parameter(Mandatory = $true)] $Tree)

    $json = $Tree | ConvertTo-Json -Depth 10
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }
    [System.IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding $false))
}

function Find-AdMngCertificate {
    <#
    .SYNOPSIS
        The TLS certificate the service will select (CertificateSelector, spec 19.3): by thumbprint, else the valid
        certificate with the serverAuth EKU, a private key and a matching subject that expires last.
    #>
    param([string] $Thumbprint, [string] $Subject, [string] $StoreName = 'My', [string] $StoreLocation = 'LocalMachine')

    $now = Get-Date
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($StoreName, $StoreLocation)
    $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
    try {
        $usable = @($store.Certificates | Where-Object { $_.HasPrivateKey -and $_.NotBefore -le $now -and $_.NotAfter -gt $now })
    }
    finally {
        $store.Close()
    }

    if ($Thumbprint) {
        $wanted = ($Thumbprint -replace '[^0-9A-Fa-f]', '')
        return $usable | Where-Object { $_.Thumbprint -eq $wanted } | Select-Object -First 1
    }

    if (-not $Subject) { return $null }
    $name = $Subject.Trim()
    return $usable |
        Where-Object {
            $certificate = $_
            $serverAuth = @($certificate.Extensions | Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
                ForEach-Object { $_.EnhancedKeyUsages } | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.1' }).Count -gt 0
            $simple = $certificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
            $serverAuth -and ($simple -eq $name -or $certificate.Subject -eq $name -or $certificate.Subject -eq "CN=$name")
        } |
        Sort-Object -Property NotAfter -Descending |
        Select-Object -First 1
}

function Grant-AdMngPrivateKeyRead {
    <#
    .SYNOPSIS
        Grants the service account Read on the private key file of the certificate (CNG or CAPI machine key).
    #>
    param([Parameter(Mandatory = $true)] $Certificate, [Parameter(Mandatory = $true)] [string] $Sid)

    $keyName = $null
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    if ($rsa -is [System.Security.Cryptography.RSACng]) { $keyName = $rsa.Key.UniqueName }
    elseif ($rsa) { $keyName = $rsa.CspKeyContainerInfo.UniqueKeyContainerName }
    else {
        $ecdsa = [System.Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPrivateKey($Certificate)
        if ($ecdsa -is [System.Security.Cryptography.ECDsaCng]) { $keyName = $ecdsa.Key.UniqueName }
    }

    if (-not $keyName) { throw "The certificate $($Certificate.Thumbprint) has no accessible private key." }
    $keyFile = @(
        (Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$keyName"),
        (Join-Path $env:ProgramData "Microsoft\Crypto\RSA\MachineKeys\$keyName")
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $keyFile) { throw "The private key file $keyName was not found." }
    & icacls.exe $keyFile /grant "*${Sid}:R" /Q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "icacls /grant failed on $keyFile with exit code $LASTEXITCODE." }
}

Export-ModuleMember -Function ConvertTo-AdMngTree, Read-AdMngBootstrap, Test-AdMngSetting, Set-AdMngSetting, Merge-AdMngBootstrap,
    Get-AdMngSettingLines, Set-AdMngProtectedAcl, Write-AdMngBootstrap, Find-AdMngCertificate, Grant-AdMngPrivateKeyRead
