using System.DirectoryServices.Protocols;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Schema;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DirectoryException = AdMng.Core.Directory.DirectoryException;
using SearchScope = AdMng.Core.Directory.SearchScope;

namespace AdMng.Directory;

/// <summary>
/// <see cref="IDirectoryConnector"/> over System.DirectoryServices.Protocols. Works on Windows (Negotiate with
/// Kerberos sealing, DC locator) and on Linux/macOS (OpenLDAP: simple bind over TLS or Kerberos via the ticket
/// cache, the domain DNS name as DC candidate).
/// </summary>
public sealed class LdapDirectoryConnector : IDirectoryConnector
{
    private const string RecycleBinFeature = "Recycle Bin Feature";
    private const string EnabledFeature = "msDS-EnabledFeature";
    private const string NetBiosName = "nETBIOSName";
    private const string NamingContextName = "nCName";

    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    /// <param name="loggerFactory">Logging (no directory values are ever logged at Information or above).</param>
    /// <param name="timeProvider">Clock for timestamps, durations and idle connection recycling.</param>
    public LdapDirectoryConnector(ILoggerFactory? loggerFactory = null, TimeProvider? timeProvider = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<LdapDirectoryConnector>();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string? GetComputerDomain()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // Local call (no network): the AD domain the machine is joined to, null for workgroups.
                return WindowsDomainLocator.GetJoinedDomain();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // Fall through to the generic API (e.g. Windows Nano / Wine).
            }
        }

        try
        {
            var name = IPGlobalProperties.GetIPGlobalProperties().DomainName?.Trim().TrimEnd('.');
            return string.IsNullOrEmpty(name) || name == "(none)" ? null : name;
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<DomainControllerInfo>> FindDomainControllersAsync(string domain, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        var name = domain.Trim().TrimEnd('.');
        if (OperatingSystem.IsWindows())
        {
            var located = await FindOnWindowsAsync(name, cancellationToken).ConfigureAwait(false);
            if (located.Count > 0)
            {
                return located;
            }
        }

        // No locator: the domain name itself (DNS resolves it to the DCs' addresses).
        return [new DomainControllerInfo(name, null, false, false, false)];
    }

    public async Task<IDirectorySession> ConnectAsync(ConnectionOptions options, NetworkCredential? credential, CancellationToken cancellationToken)
    {
        var settings = ConnectionSettings.Create(options, credential, OperatingSystem.IsWindows());
        var started = _timeProvider.GetTimestamp();
        var sessionLogger = _loggerFactory.CreateLogger<LdapDirectorySession>();
        var domain = options.Domain?.Trim().TrimEnd('.');
        if (options.Server is null)
        {
            domain ??= GetComputerDomain();
            if (domain is null)
            {
                throw new DirectoryException(
                    DirectoryErrorKind.ServerUnavailable,
                    "This computer is not joined to a domain; enter a domain or a domain controller.");
            }
        }

        var (factory, connection) = await OpenFirstConnectionAsync(settings, domain, sessionLogger, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return await InitializeSessionAsync(settings, factory, connection, domain, started, sessionLogger, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Disposing twice is harmless when the session's pool already released it.
            connection.Dispose();
            throw;
        }
    }

    private async Task<(LdapConnectionFactory Factory, LdapConnection Connection)> OpenFirstConnectionAsync(
        ConnectionSettings settings,
        string? domain,
        ILogger sessionLogger,
        CancellationToken cancellationToken)
    {
        var options = settings.Options;
        DirectoryException? lastError = null;
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var round = 0; round < 2; round++)
        {
            IReadOnlyList<string> candidates = options.Server is { } server
                ? [server.Trim()]
                : await LocateAsync(domain!, options.GlobalCatalog, forceRediscovery: round > 0, cancellationToken).ConfigureAwait(false);
            foreach (var host in candidates)
            {
                if (round > 0 && !tried.Add(host))
                {
                    continue;
                }

                tried.Add(host);
                var factory = new LdapConnectionFactory(settings, host, domain, sessionLogger);
                Log.Connecting(_logger, host, settings.Port, options.Authentication, options.Security);
                try
                {
                    var connection = await factory.OpenAsync(probeReachability: true, cancellationToken).ConfigureAwait(false);
                    return (factory, connection);
                }
                catch (DirectoryException ex)
                {
                    Log.ConnectFailed(_logger, host, settings.Port, ex.Kind, ex.LdapResultCode);
                    if (ex.Kind is not (DirectoryErrorKind.ServerUnavailable or DirectoryErrorKind.Timeout))
                    {
                        // Credentials or policy problems are the same on every DC.
                        throw;
                    }

                    lastError = ex;
                }
            }

            if (options.Server is not null || !OperatingSystem.IsWindows())
            {
                break;
            }
        }

        throw lastError ?? new DirectoryException(DirectoryErrorKind.ServerUnavailable, "No domain controller could be located.");
    }

    private async Task<IReadOnlyList<string>> LocateAsync(string domain, bool globalCatalog, bool forceRediscovery, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var (dc, error) = await LocateOnWindowsAsync(domain, globalCatalog, forceRediscovery, cancellationToken)
                .ConfigureAwait(false);
            if (dc is not null)
            {
                Log.DomainControllerLocated(_logger, dc.HostName, domain, dc.DcSite);
                return [dc.HostName, domain];
            }

            Log.LocatorFailed(_logger, domain, error);
        }

        return [domain];
    }

    // The locator APIs block on DNS / network I/O - keep them off the caller's (UI) thread.
    [SupportedOSPlatform("windows")]
    private static Task<IReadOnlyList<DomainControllerInfo>> FindOnWindowsAsync(string domain, CancellationToken cancellationToken) =>
        Task.Run(() => WindowsDomainLocator.FindDomainControllers(domain), cancellationToken);

    [SupportedOSPlatform("windows")]
    private static Task<(LocatedDomainController? Dc, int Error)> LocateOnWindowsAsync(
        string domain,
        bool globalCatalog,
        bool forceRediscovery,
        CancellationToken cancellationToken) =>
        Task.Run(() => WindowsDomainLocator.Locate(domain, globalCatalog, forceRediscovery), cancellationToken);

    private async Task<LdapDirectorySession> InitializeSessionAsync(
        ConnectionSettings settings,
        LdapConnectionFactory factory,
        LdapConnection connection,
        string? domain,
        long started,
        ILogger sessionLogger,
        CancellationToken cancellationToken)
    {
        var rootDse = await ReadRootDseAsync(connection, settings, cancellationToken).ConfigureAwait(false);
        var domainDns = DistinguishedName.Parse(rootDse.DefaultNamingContext).DnsDomain;
        var pinned = await PinAsync(factory, rootDse, domain ?? domainDns, domainDns, sessionLogger, cancellationToken)
            .ConfigureAwait(false);

        var pool = new LdapConnectionPool(pinned, settings.Options.MaxConnections, sessionLogger, _timeProvider);
        pool.Add(connection);
        var info = new SessionInfo
        {
            Server = rootDse.DnsHostName ?? pinned.Host,
            Port = settings.Port,
            DomainDnsName = domainDns,
            DefaultNamingContext = rootDse.DefaultNamingContext,
            ConfigurationNamingContext = rootDse.ConfigurationNamingContext,
            SchemaNamingContext = rootDse.SchemaNamingContext,
            RootDomainNamingContext = rootDse.RootDomainNamingContext,
            BoundIdentity = FallbackIdentity(settings),
            Authentication = settings.Options.Authentication,
            Security = settings.Options.Security,
            IsGlobalCatalog = settings.Options.GlobalCatalog,
            DomainFunctionality = rootDse.DomainFunctionality,
            ForestFunctionality = rootDse.ForestFunctionality,
            SupportedControls = rootDse.SupportedControls,
            ConnectedAt = _timeProvider.GetUtcNow(),
        };

        var session = new LdapDirectorySession(info, pool, pinned.IsEncrypted(connection), sessionLogger, _timeProvider);
        try
        {
            // Independent reads run concurrently - the pool opens more connections in the background while the
            // first one is busy - so that connecting stays well below a second on a LAN.
            var netBiosTask = OptionalAsync("NetBIOS", () => ReadNetBiosNameAsync(session, rootDse, cancellationToken), null);
            var identityTask = OptionalAsync(
                "Identity",
                () => ResolveIdentityAsync(session, rootDse, netBiosTask, cancellationToken),
                (Identity: (string?)null, Dn: (string?)null));
            var recycleBinTask = OptionalAsync("RecycleBin", () => IsRecycleBinEnabledAsync(session, rootDse, cancellationToken), false);
            var schemaTask = OptionalAsync(
                "Schema",
                () => SchemaLoader.LoadAsync(
                    session.SearchAsync,
                    rootDse.SchemaNamingContext,
                    rootDse.ConfigurationNamingContext,
                    sessionLogger,
                    _timeProvider,
                    cancellationToken),
                SchemaCatalog.Empty);

            await Task.WhenAll(netBiosTask, identityTask, recycleBinTask, schemaTask).ConfigureAwait(false);
            var schema = schemaTask.Result;
            var (identity, boundDn) = identityTask.Result;
            info = info with
            {
                DomainNetBiosName = netBiosTask.Result,
                BoundIdentity = identity ?? info.BoundIdentity,
                BoundUserDn = boundDn,
                RecycleBinEnabled = recycleBinTask.Result,
                HasExchangeSchema = schema.FindAttribute(Attr.HideFromAddressLists) is not null,
                HasLapsSchema = schema.FindAttribute("ms-Mcs-AdmPwd") is not null || schema.FindAttribute("msLAPS-Password") is not null,
            };
            session.Initialize(info, schema);
            Log.Connected(
                _logger,
                info.Server,
                info.Port,
                (long)_timeProvider.GetElapsedTime(started).TotalMilliseconds,
                schema.Attributes.Count,
                schema.Classes.Count);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        async Task<T> OptionalAsync<T>(string step, Func<Task<T>> action, T fallback)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (DirectoryException ex) when (!ex.IsTransient && ex.Kind != DirectoryErrorKind.Cancelled)
            {
                Log.ConnectStepFailed(_logger, step, ex.Kind, ex.LdapResultCode);
                return fallback;
            }
        }
    }

    private static async Task<RootDse> ReadRootDseAsync(
        LdapConnection connection,
        ConnectionSettings settings,
        CancellationToken cancellationToken)
    {
        var request = new SearchRequest(
            string.Empty,
            "(objectClass=*)",
            System.DirectoryServices.Protocols.SearchScope.Base,
            [.. RootDse.Attributes]);
        try
        {
            var response = await connection
                .SendAsync<SearchResponse>(request, settings.Options.RequestTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (response.Entries.Count == 0)
            {
                throw new DirectoryException(DirectoryErrorKind.UnwillingToPerform, "The server returned no RootDSE.");
            }

            return RootDse.Parse(EntryReader.ToDirectoryObject(response.Entries[0], SchemaCatalog.Empty));
        }
        catch (Exception ex) when (LdapErrors.Translate(ex, OperationKind.RootDse, null) is { } error && error != ex)
        {
            throw error;
        }
    }

    /// <summary>
    /// Keeps every connection of the session on the DC that answered the first bind. When the session was opened
    /// through the domain name (DNS round robin) the DC's own host name is used for further connections.
    /// </summary>
    internal static async Task<LdapConnectionFactory> PinAsync(
        LdapConnectionFactory factory,
        RootDse rootDse,
        string domain,
        string domainDns,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var dcHost = rootDse.DnsHostName;
        var connectedThroughDomain = string.Equals(factory.Host.TrimEnd('.'), domain, StringComparison.OrdinalIgnoreCase)
            || string.Equals(factory.Host.TrimEnd('.'), domainDns, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(dcHost)
            || !connectedThroughDomain
            || string.Equals(dcHost, factory.Host, StringComparison.OrdinalIgnoreCase))
        {
            return factory;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(dcHost, cancellationToken).ConfigureAwait(false);
            if (addresses.Length > 0)
            {
                Log.Pinned(logger, dcHost, factory.Host);
                return new LdapConnectionFactory(factory.Settings, dcHost, domain, logger);
            }
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            // The DC's name does not resolve here; keep using the name that worked (see the warning below).
        }

        // Further pooled connections go through the domain name again and may reach another DC (rule: one DC per
        // session). Make that visible; the operator can enter the DC explicitly.
        Log.PinningFailed(logger, dcHost, factory.Host);
        return factory;
    }

    private static async Task<string?> ReadNetBiosNameAsync(LdapDirectorySession session, RootDse rootDse, CancellationToken cancellationToken)
    {
        var search = new DirectorySearch
        {
            BaseDn = PartitionsDn(rootDse),
            Scope = SearchScope.OneLevel,
            Filter = LdapFilter.And(
                LdapFilter.Equal(Attr.ObjectClass, "crossRef"),
                LdapFilter.Equal(NamingContextName, rootDse.DefaultNamingContext),
                LdapFilter.Present(NetBiosName)),
            Attributes = [NetBiosName],
            SizeLimit = 1,
        };

        await foreach (var crossRef in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
        {
            return crossRef.GetString(NetBiosName);
        }

        return null;
    }

    private static async Task<bool> IsRecycleBinEnabledAsync(LdapDirectorySession session, RootDse rootDse, CancellationToken cancellationToken)
    {
        var partitions = await session.GetAsync(PartitionsDn(rootDse), [EnabledFeature], cancellationToken).ConfigureAwait(false);
        return partitions is not null && partitions.GetStrings(EnabledFeature).Any(dn =>
            DistinguishedName.TryParse(dn, out var parsed)
            && !parsed.IsRoot
            && string.Equals(parsed.Name, RecycleBinFeature, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// "Who am I?" -> <c>CONTOSO\jan.kowalski</c>, plus the account's DN when it lives in this domain.
    /// </summary>
    private static async Task<(string? Identity, string? Dn)> ResolveIdentityAsync(
        LdapDirectorySession session,
        RootDse rootDse,
        Task<string?> netBiosTask,
        CancellationToken cancellationToken)
    {
        // AD advertises the extension; Samba implements it without listing supportedExtension at all.
        if (rootDse.SupportedExtensions.Count > 0 && !rootDse.SupportedExtensions.Contains(RootDse.WhoAmIOid))
        {
            return (null, null);
        }

        var authzId = await session.WhoAmIAsync(cancellationToken).ConfigureAwait(false);
        var (identity, dn, filter) = ParseAuthorizationId(authzId);
        if (dn is not null || filter is null)
        {
            return (identity, dn);
        }

        // Only look the account up when it belongs to this domain (a sAMAccountName is unique per domain only).
        var backslash = identity!.IndexOf('\\', StringComparison.Ordinal);
        if (backslash > 0)
        {
            var netBios = await netBiosTask.ConfigureAwait(false);
            if (netBios is null || !string.Equals(identity[..backslash], netBios, StringComparison.OrdinalIgnoreCase))
            {
                return (identity, null);
            }
        }

        var search = new DirectorySearch
        {
            BaseDn = rootDse.DefaultNamingContext,
            Scope = SearchScope.Subtree,
            Filter = filter,
            SizeLimit = 1,
        };
        await foreach (var account in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
        {
            return (identity, account.DistinguishedName);
        }

        return (identity, null);
    }

    /// <summary>
    /// Parses an RFC 4513 authorization identity: <c>u:DOMAIN\user</c>, <c>u:user@domain</c> or <c>dn:...</c>.
    /// Returns the display identity, the DN (dn: form) and the filter to find the account (u: form).
    /// </summary>
    internal static (string? Identity, string? Dn, string? Filter) ParseAuthorizationId(string? authzId)
    {
        if (string.IsNullOrWhiteSpace(authzId))
        {
            return (null, null, null);
        }

        var value = authzId.Trim();
        if (value.StartsWith("dn:", StringComparison.OrdinalIgnoreCase))
        {
            var dn = value[3..].Trim();
            return dn.Length == 0 ? (null, null, null) : (dn, dn, null);
        }

        if (!value.StartsWith("u:", StringComparison.OrdinalIgnoreCase))
        {
            return (value, null, null);
        }

        var user = value[2..].Trim();
        if (user.Length == 0)
        {
            return (null, null, null);
        }

        var backslash = user.IndexOf('\\', StringComparison.Ordinal);
        if (backslash > 0 && backslash < user.Length - 1)
        {
            return (user, null, LdapFilter.Equal(Attr.SamAccountName, user[(backslash + 1)..]));
        }

        return user.Contains('@', StringComparison.Ordinal)
            ? (user, null, LdapFilter.Equal(Attr.UserPrincipalName, user))
            : (user, null, LdapFilter.Equal(Attr.SamAccountName, user));
    }

    private static string FallbackIdentity(ConnectionSettings settings) =>
        settings.Account?.ToString() ?? $"{Environment.UserDomainName}\\{Environment.UserName}";

    private static string PartitionsDn(RootDse rootDse) =>
        DistinguishedName.Parse(rootDse.ConfigurationNamingContext).Child("CN", "Partitions").ToString();
}
