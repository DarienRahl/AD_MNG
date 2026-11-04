using System.Net;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Computers.Remote;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.EventLogs;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Computers;
using AdMng.Presentation.Connection;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Watchdog;

namespace AdMng.Presentation.EventLogs;

/// <summary>The DNS name of an address (reverse lookup) – for the pivot from an IP address source to its computer.</summary>
public interface IHostNameLookup
{
    /// <summary>The host name of <paramref name="address"/>, null when it has none.</summary>
    Task<string?> LookupAsync(IPAddress address, CancellationToken cancellationToken);
}

/// <summary><see cref="IHostNameLookup"/> through the system resolver (no credential; the name only, nothing kept).</summary>
public sealed class DnsHostNameLookup : IHostNameLookup
{
    public async Task<string?> LookupAsync(IPAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        try
        {
            var entry = await Dns.GetHostEntryAsync(address.ToString(), cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(entry.HostName) || IPAddress.TryParse(entry.HostName, out _) ? null : entry.HostName;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return null;
        }
    }
}

/// <summary>A domain controller of the window's domain as the event log documents list it.</summary>
/// <param name="Host">The DNS host name.</param>
/// <param name="IsPdcEmulator">Holds the PDC emulator role.</param>
public sealed record EventLogDomainController(string Host, bool IsPdcEmulator)
{
    public override string ToString() => IsPdcEmulator ? "domain controller (PDC emulator)" : "domain controller";
}

/// <summary>
/// Services of the event log documents of one connection window (scoped, track E): the window's UI services and change
/// pipeline, the connector (<see cref="IEventLogConnector"/>, Windows only), the per-DC reads (rule 4), and the optional
/// links to Group Policy (K5 model, K6 diagnostics), the computers (K1 remote actions) and the Watchdog history. Creates the
/// documents; everything they read stays in memory while they are open (rule 1).
/// </summary>
public sealed class EventLogServices(
    DialogServices ui,
    ICsvCodec csv,
    EventLogLauncher launcher,
    IEventLogConnector? connector = null,
    IDirectoryConnector? directoryConnector = null,
    GroupPolicyServices? groupPolicy = null,
    ComputerActions? computers = null,
    IRemoteComputerClient? remote = null,
    IHostAddressResolver? resolver = null,
    IHostNameLookup? nameLookup = null,
    WatchdogObjectHistoryLauncher? history = null)
{
    private const int MaxNameLookups = 8;

    public DialogServices Ui { get; } = ui ?? throw new ArgumentNullException(nameof(ui));

    public ICsvCodec Csv { get; } = csv ?? throw new ArgumentNullException(nameof(csv));

    public EventLogLauncher Launcher { get; } = launcher ?? throw new ArgumentNullException(nameof(launcher));

    /// <summary>The reader of event logs (null in hosts without one: the documents show the "not supported" banner).</summary>
    public IEventLogConnector? Connector { get; } = connector;

    /// <summary>Reads of every DC with short-lived sessions (lockout state).</summary>
    public DomainControllerReader DomainControllers { get; } = new(directoryConnector, ui.Errors);

    public GroupPolicyServices? GroupPolicy { get; } = groupPolicy;

    public ComputerActions? Computers { get; } = computers;

    public IRemoteComputerClient? Remote { get; } = remote;

    public IHostAddressResolver? Resolver { get; } = resolver;

    public IHostNameLookup? NameLookup { get; } = nameLookup;

    public WatchdogObjectHistoryLauncher? History { get; } = history;

    public ILocalizer Localizer => Ui.Localizer;

    public TimeZoneInfo Zone => Ui.Time.LocalTimeZone;

    /// <summary>
    /// Why the event logs cannot be read in this window (localized), null when they can: not Windows (no connector or one
    /// that is not supported here) or a window bound with a simple bind (no Windows logon for Kerberos).
    /// </summary>
    public string? NotSupportedReason(IDirectorySession? session)
    {
        if (Connector is not { IsSupported: true })
        {
            return Localizer[L.EventLogs.NotSupported];
        }

        return session?.Info.Authentication == AuthenticationMode.SimpleBind ? Localizer[L.EventLogs.NotSupportedSimpleBind] : null;
    }

    /// <summary>A runner over the connector (never null once <see cref="NotSupportedReason"/> is null).</summary>
    public EventLogHostRunner CreateRunner(int? maxRecords = null) =>
        new(Connector ?? throw new InvalidOperationException("No event log connector is registered.")) { MaxRecords = maxRecords ?? EventLogLimits.MaxRecordsInMemory };

    /// <summary>The documents of a request.</summary>
    public EventLogReaderDocumentViewModel CreateReader() => new(this);

    public LockoutDocumentViewModel CreateLockout(string userDn, string? name) => new(this, userDn, name);

    public FailedLogonsDocumentViewModel CreateFailedLogons() => new(this);

    /// <summary>
    /// The DCs of the session's domain (configuration partition; the PDC emulator marked), the session's DC when the list
    /// cannot be read. Ordered: PDC emulator first, then by name.
    /// </summary>
    public async Task<IReadOnlyList<EventLogDomainController>> ReadDomainControllersAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        IReadOnlyList<DirectoryDomainController> controllers;
        try
        {
            controllers = await DomainControllerCatalog.ReadAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryException)
        {
            controllers = [];
        }

        var list = controllers
            .Where(c => !string.IsNullOrWhiteSpace(c.HostName))
            .DistinctBy(c => c.HostName.Trim().TrimEnd('.'), StringComparer.OrdinalIgnoreCase)
            .Select(c => new EventLogDomainController(c.HostName.Trim().TrimEnd('.'), c.IsPdcEmulator))
            .ToList();
        if (!list.Any(c => DomainControllerCatalog.IsSameHost(c.Host, session.Info.Server)) && list.Count == 0)
        {
            list.Add(new EventLogDomainController(session.Info.Server, false));
        }

        return [.. list.OrderByDescending(c => c.IsPdcEmulator).ThenBy(c => c.Host, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The addresses of the hosts (through the resolver when one is registered; empty otherwise), best effort.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ResolveAddressesAsync(IEnumerable<string> hosts, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (Resolver is null)
        {
            return result;
        }

        foreach (var host in hosts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var addresses = await Resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
                result[host] = [.. addresses.Select(a => a.ToString())];
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or RemoteActionException or ArgumentException or InvalidOperationException)
            {
                // Best effort: a DC whose address is unknown (no answer, or none within the resolver's timeout) is still read.
            }
        }

        return result;
    }

    /// <summary>
    /// The AD object a value of an event names: a SID (<c>objectSid</c>) or an account name (<c>sAMAccountName</c>, or the UPN),
    /// searched through the window's DC. A name is resolved only when its domain – <paramref name="domain"/> (the record's
    /// <c>…DomainName</c> field) and a <c>DOMAIN\</c> prefix – is empty or the window's domain (<see cref="IsWindowDomain"/>):
    /// a local account (<c>SRV-APP01\Administrator</c>, <c>.\svc</c>), <c>NT AUTHORITY</c> or another domain never links to a
    /// same-named account of this domain. <c>sam@realm</c> matches the sAMAccountName only for the window's DNS domain (the
    /// UPN itself always). Null when nothing matches.
    /// </summary>
    public async Task<(string Dn, string Name)?> ResolveObjectAsync(IDirectorySession session, string value, bool isSid, string? domain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        string filter;
        if (isSid)
        {
            if (!value.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            filter = LdapFilter.Equal(Attr.ObjectSid, value);
        }
        else
        {
            var name = value.Trim();
            var slash = name.LastIndexOf('\\');
            if (slash >= 0)
            {
                if (!IsWindowDomain(session.Info, name[..slash]))
                {
                    return null;
                }

                name = name[(slash + 1)..];
            }

            if (name.Length == 0 || name == "-" || name.Length > 256 || !IsWindowDomain(session.Info, domain))
            {
                return null;
            }

            var at = name.IndexOf('@', StringComparison.Ordinal);
            filter = at < 0
                ? LdapFilter.Equal(Attr.SamAccountName, name)
                : string.Equals(name[(at + 1)..], session.Info.DomainDnsName, StringComparison.OrdinalIgnoreCase)
                    ? LdapFilter.Or([LdapFilter.Equal(Attr.UserPrincipalName, name), LdapFilter.Equal(Attr.SamAccountName, name[..at])])
                    : LdapFilter.Equal(Attr.UserPrincipalName, name);
        }

        var search = new DirectorySearch
        {
            BaseDn = session.Info.DefaultNamingContext,
            Scope = SearchScope.Subtree,
            Filter = filter,
            Attributes = [Attr.Name, Attr.DisplayName],
            SizeLimit = 1,
        };
        await foreach (var found in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
        {
            return (found.DistinguishedName, found.GetString(Attr.DisplayName) is { Length: > 0 } display ? display : found.Name);
        }

        return null;
    }

    /// <summary>
    /// The domain part of an account name as an event or a remote computer writes it (<c>DOMAIN\</c>, <c>TargetDomainName</c>)
    /// names the window's domain: its NetBIOS name (the first label of the DNS name when the NetBIOS name is not known) or
    /// its DNS name, case-insensitive. Empty, null and <c>-</c> (not logged) count as the window's domain; <c>.</c> (the
    /// local computer), a computer name, <c>NT AUTHORITY</c> and other domains do not.
    /// </summary>
    internal static bool IsWindowDomain(SessionInfo info, string? domain)
    {
        ArgumentNullException.ThrowIfNull(info);
        var text = domain?.Trim();
        if (string.IsNullOrEmpty(text) || text == "-")
        {
            return true;
        }

        var dns = info.DomainDnsName.Trim().TrimEnd('.');
        var netBios = string.IsNullOrWhiteSpace(info.DomainNetBiosName) ? dns.Split('.')[0] : info.DomainNetBiosName.Trim();
        return string.Equals(text.TrimEnd('.'), dns, StringComparison.OrdinalIgnoreCase) || string.Equals(text, netBios, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The computer object of a host name (dNSHostName, else the NetBIOS name as sAMAccountName), null when none.</summary>
    public async Task<DirectoryObject?> FindComputerAsync(IDirectorySession session, string host, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var name = host.Trim().TrimStart('\\').TrimEnd('.').TrimEnd('$');
        if (name.Length == 0 || name.Length > 255)
        {
            return null;
        }

        var shortName = name.Split('.')[0];
        var filter = LdapFilter.And([
            LdapFilter.Equal(Attr.ObjectClass, "computer"),
            LdapFilter.Or([LdapFilter.Equal(Attr.DnsHostName, name), LdapFilter.Equal(Attr.SamAccountName, shortName + "$")]),
        ]);
        var search = new DirectorySearch
        {
            BaseDn = session.Info.DefaultNamingContext,
            Scope = SearchScope.Subtree,
            Filter = filter,
            Attributes = [Attr.Name, Attr.DnsHostName, Attr.ObjectClass, Attr.OperatingSystem, Attr.UserAccountControl, Attr.PrimaryGroupId],
            SizeLimit = 2,
        };
        DirectoryObject? first = null;
        await foreach (var found in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(found.GetString(Attr.DnsHostName), name, StringComparison.OrdinalIgnoreCase))
            {
                return found;
            }

            first ??= found;
        }

        return first;
    }

    /// <summary>The DNS name of an address through <see cref="NameLookup"/>, null without one or when it has none.</summary>
    public async Task<string?> LookupNameAsync(string address, CancellationToken cancellationToken)
    {
        if (NameLookup is null || !IPAddress.TryParse(address, out var ip))
        {
            return null;
        }

        try
        {
            return await NameLookup.LookupAsync(ip, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>"Pokaż w modelu": the K5 model of the computer object of <paramref name="host"/> (a DC) in the window's shell.</summary>
    public async Task<bool> ShowInModelAsync(IDirectorySession session, string host, CancellationToken cancellationToken)
    {
        if (GroupPolicy is not { } gp || await FindComputerAsync(session, host, cancellationToken).ConfigureAwait(true) is not { } computer)
        {
            return false;
        }

        return gp.OpenDocument(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Modeling) { ComputerDn = computer.DistinguishedName, Name = computer.Name });
    }

    /// <summary>"Diagnostyka zasad grupy" (K6) of the window's domain.</summary>
    public bool OpenGroupPolicyDiagnostics() =>
        GroupPolicy?.OpenDocument(global::AdMng.Presentation.GroupPolicy.Diagnostics.GroupPolicyDiagnosticsEntry.Request) == true;

    /// <summary>The names of up to <see cref="MaxNameLookups"/> SIDs and accounts (in parallel order), for the details of a record.</summary>
    internal static int NameLookupLimit => MaxNameLookups;
}
