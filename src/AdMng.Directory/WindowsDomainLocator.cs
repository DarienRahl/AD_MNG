using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AdMng.Core.Directory;

namespace AdMng.Directory;

/// <summary>Result of a DC locator call (DsGetDcName).</summary>
internal sealed record LocatedDomainController(
    string HostName,
    string? DomainName,
    string? DcSite,
    string? ClientSite,
    bool IsGlobalCatalog,
    bool IsWritable,
    bool IsClosest);

/// <summary>Windows DC locator (netapi32): site aware discovery of domain controllers.</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsDomainLocator
{
    private const string NetApi = "netapi32.dll";

    // DsGetDcName flags.
    private const uint DsForceRediscovery = 0x00000001;
    private const uint DsDirectoryServiceRequired = 0x00000010;
    private const uint DsGcServerRequired = 0x00000040;
    private const uint DsIsDnsName = 0x00020000;
    private const uint DsTryNextClosestSite = 0x00040000;
    private const uint DsReturnDnsName = 0x40000000;

    // DOMAIN_CONTROLLER_INFO.Flags.
    private const uint DsGcFlag = 0x00000004;
    private const uint DsClosestFlag = 0x00000080;
    private const uint DsWritableFlag = 0x00000100;

    // DsGetDcOpen options.
    private const uint DsNotifyAfterSiteRecords = 0x00000002;

    private const int ErrorSuccess = 0;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorFilemarkDetected = 1101;

    // DSROLE_MACHINE_ROLE.
    private const int RoleStandaloneWorkstation = 0;
    private const int RoleStandaloneServer = 2;
    private const int DsRolePrimaryDomainInfoBasic = 1;

    /// <summary>Calls DsGetDcName for a DC running the directory service.</summary>
    /// <returns>The DC, or the Win32 error code.</returns>
    public static (LocatedDomainController? Dc, int Error) Locate(string domain, bool globalCatalog, bool forceRediscovery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        var flags = DsDirectoryServiceRequired | DsIsDnsName | DsReturnDnsName | DsTryNextClosestSite;
        if (globalCatalog)
        {
            flags |= DsGcServerRequired;
        }

        if (forceRediscovery)
        {
            flags |= DsForceRediscovery;
        }

        var error = DsGetDcName(null, domain, IntPtr.Zero, null, flags, out var buffer);
        if (error != ErrorSuccess)
        {
            return (null, error);
        }

        try
        {
            var info = Marshal.PtrToStructure<NativeDomainControllerInfo>(buffer);
            var host = TrimUnc(Marshal.PtrToStringUni(info.DomainControllerName));
            if (string.IsNullOrEmpty(host))
            {
                return (null, ErrorNoMoreItems);
            }

            return (new LocatedDomainController(
                host,
                Marshal.PtrToStringUni(info.DomainName),
                Marshal.PtrToStringUni(info.DcSiteName),
                Marshal.PtrToStringUni(info.ClientSiteName),
                (info.Flags & DsGcFlag) != 0,
                (info.Flags & DsWritableFlag) != 0,
                (info.Flags & DsClosestFlag) != 0), ErrorSuccess);
        }
        finally
        {
            _ = NetApiBufferFree(buffer);
        }
    }

    /// <summary>
    /// Enumerates the DCs registered in DNS for the domain (DsGetDcOpen/DsGetDcNext). DCs of the client's
    /// site come first; the flag tells whether an entry was returned before the site/non-site file mark.
    /// </summary>
    public static IReadOnlyList<(string HostName, bool InClientSite)> Enumerate(string domain, string? clientSite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        var result = new List<(string, bool)>();
        var error = DsGetDcOpen(domain, DsNotifyAfterSiteRecords, clientSite, IntPtr.Zero, null, 0, out var context);
        if (error != ErrorSuccess)
        {
            return result;
        }

        try
        {
            var inSite = !string.IsNullOrEmpty(clientSite);
            while (true)
            {
                error = DsGetDcNext(context, out _, out var sockets, out var hostPtr);
                if (error == ErrorFilemarkDetected)
                {
                    inSite = false;
                    continue;
                }

                if (error != ErrorSuccess)
                {
                    // ERROR_NO_MORE_ITEMS or a DNS failure - return what we have.
                    break;
                }

                try
                {
                    var host = TrimUnc(Marshal.PtrToStringUni(hostPtr));
                    if (!string.IsNullOrEmpty(host))
                    {
                        result.Add((host, inSite));
                    }
                }
                finally
                {
                    if (sockets != IntPtr.Zero)
                    {
                        _ = LocalFree(sockets);
                    }

                    if (hostPtr != IntPtr.Zero)
                    {
                        _ = NetApiBufferFree(hostPtr);
                    }
                }
            }
        }
        finally
        {
            DsGetDcClose(context);
        }

        return result;
    }

    /// <summary>DNS name of the domain this computer is joined to, or null for a workgroup computer.</summary>
    public static string? GetJoinedDomain()
    {
        if (DsRoleGetPrimaryDomainInformation(null, DsRolePrimaryDomainInfoBasic, out var buffer) != ErrorSuccess)
        {
            return null;
        }

        try
        {
            var info = Marshal.PtrToStructure<PrimaryDomainInfoBasic>(buffer);
            if (info.MachineRole is RoleStandaloneWorkstation or RoleStandaloneServer)
            {
                return null;
            }

            var dns = Marshal.PtrToStringUni(info.DomainNameDns);
            return string.IsNullOrWhiteSpace(dns) ? null : dns;
        }
        finally
        {
            DsRoleFreeMemory(buffer);
        }
    }

    /// <summary>Creates the connection dialog entries: located DC first, then the DNS registered DCs.</summary>
    public static IReadOnlyList<DomainControllerInfo> FindDomainControllers(string domain)
    {
        var (located, _) = Locate(domain, globalCatalog: false, forceRediscovery: false);
        var list = new List<DomainControllerInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (located is not null)
        {
            seen.Add(located.HostName);
            list.Add(new DomainControllerInfo(
                located.HostName,
                located.DcSite,
                located.IsGlobalCatalog,
                IsReadOnly: !located.IsWritable,
                IsInClientSite: located.IsClosest
                    || (located.ClientSite is not null && string.Equals(located.ClientSite, located.DcSite, StringComparison.OrdinalIgnoreCase))));
        }

        foreach (var (host, inSite) in Enumerate(domain, located?.ClientSite))
        {
            if (seen.Add(host))
            {
                // DNS SRV records do not tell GC / RODC roles; the dialog shows them as unknown (false).
                list.Add(new DomainControllerInfo(host, inSite ? located?.ClientSite : null, false, false, inSite));
            }
        }

        return list;
    }

    private static string? TrimUnc(string? name) => name?.TrimStart('\\');

    [LibraryImport(NetApi, EntryPoint = "DsGetDcNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DsGetDcName(
        string? computerName,
        string? domainName,
        IntPtr domainGuid,
        string? siteName,
        uint flags,
        out IntPtr domainControllerInfo);

    [LibraryImport(NetApi, EntryPoint = "DsGetDcOpenW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DsGetDcOpen(
        string dnsName,
        uint optionFlags,
        string? siteName,
        IntPtr domainGuid,
        string? dnsForestName,
        uint dcFlags,
        out IntPtr context);

    [LibraryImport(NetApi, EntryPoint = "DsGetDcNextW")]
    private static partial int DsGetDcNext(IntPtr context, out uint socketAddressCount, out IntPtr socketAddresses, out IntPtr dnsHostName);

    [LibraryImport(NetApi, EntryPoint = "DsGetDcCloseW")]
    private static partial void DsGetDcClose(IntPtr context);

    [LibraryImport(NetApi, EntryPoint = "NetApiBufferFree")]
    private static partial int NetApiBufferFree(IntPtr buffer);

    [LibraryImport(NetApi, EntryPoint = "DsRoleGetPrimaryDomainInformation", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DsRoleGetPrimaryDomainInformation(string? server, int infoLevel, out IntPtr buffer);

    [LibraryImport(NetApi, EntryPoint = "DsRoleFreeMemory")]
    private static partial void DsRoleFreeMemory(IntPtr buffer);

    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static partial IntPtr LocalFree(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeDomainControllerInfo
    {
        public IntPtr DomainControllerName;
        public IntPtr DomainControllerAddress;
        public uint DomainControllerAddressType;
        public Guid DomainGuid;
        public IntPtr DomainName;
        public IntPtr DnsForestName;
        public uint Flags;
        public IntPtr DcSiteName;
        public IntPtr ClientSiteName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrimaryDomainInfoBasic
    {
        public int MachineRole;
        public uint Flags;
        public IntPtr DomainNameFlat;
        public IntPtr DomainNameDns;
        public IntPtr DomainForestName;
        public Guid DomainGuid;
    }
}
