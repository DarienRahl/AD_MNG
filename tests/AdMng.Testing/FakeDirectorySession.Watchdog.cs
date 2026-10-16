using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Schema;

namespace AdMng.Testing;

/// <summary>
/// AdMng Watchdog emulation (watchdog specification 4.3), mirroring the Samba lab findings: RootDSE, explicitly seeded
/// domain controllers with their own USN spaces and invocationIds (restore, USN rollback, failover/failback), naming
/// context boundaries, per-attribute replication metadata (Windows-style msDS-ReplAttributeMetaData XML), Extended DN
/// values, lockout expiry and the logon reset of lockoutTime, constructed password expiry and resultant PSO, and read
/// restrictions (hidden attributes, no access to CN=Deleted Objects).
/// <para>Everything is opt-in: a fake built by existing tests has no DC objects, no lockout expiry and no new
/// constructed values, and behaves as before.</para>
/// </summary>
public sealed partial class FakeDirectorySession
{
    /// <summary>invocationId reported while no domain controller is seeded.</summary>
    public static readonly Guid DefaultInvocationId = new("5e1f0000-0000-4000-8000-0000000000dc");

    /// <summary>Distance between the USN spaces of two seeded domain controllers.</summary>
    public const long DomainControllerUsnSpacing = 10_000_000;

    private const string NtdsSettingsName = "NTDS Settings";
    private const string ReplAttributeMetaData = "msDS-ReplAttributeMetaData";
    private const string UserPasswordExpiryTimeComputed = "msDS-UserPasswordExpiryTimeComputed";
    private const string ResultantPso = "msDS-ResultantPSO";

    /// <summary>Attributes without replication metadata: non-replicated bookkeeping and linked values (LVR).</summary>
    private static readonly HashSet<string> NonReplicated = new(StringComparer.OrdinalIgnoreCase)
    {
        Attr.UsnChanged, Attr.UsnCreated, Attr.WhenChanged, Attr.BadPwdCount, Attr.BadPasswordTime, Attr.LastLogon,
        Attr.LogonCount, "distinguishedName",
    };

    private readonly Dictionary<string, DomainControllerState> _domainControllers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Entry, Dictionary<string, MetadataEntry>> _metadata = [];
    private readonly Dictionary<Entry, HashSet<string>> _hiddenAttributes = [];
    private readonly Dictionary<Entry, string> _resultantPsos = [];
    private long? _highestUsnOverride;
    private int _restoreCounter;

    /// <summary>RootDSE isSynchronized (default TRUE).</summary>
    public bool IsSynchronized { get; set; } = true;

    /// <summary>
    /// Subtree and one-level searches stay inside the naming context of their base (the configuration and schema
    /// partitions are separate NCs although their DNs are lexically below the domain). Switched on by
    /// <see cref="SeedDomainController"/>.
    /// </summary>
    public bool EnforceNamingContextBoundaries { get; set; }

    /// <summary>Show Deleted searches under CN=Deleted Objects return nothing (a read-only account without LCRP); a
    /// base read of the container returns it without attributes, like Samba for odczyt.test.</summary>
    public bool DenyDeletedObjects { get; set; }

    /// <summary>
    /// Effective lockout duration. Null (default) keeps the old semantics: lockoutTime &gt; 0 means locked and never
    /// expires. When set, msDS-User-Account-Control-Computed reports the lock bit only while
    /// <c>now &lt; lockoutTime + LockoutDuration</c>, and <see cref="SimulateSuccessfulLogon"/> resets lockoutTime to 0
    /// after the lockout expired (DC behaviour).
    /// </summary>
    public TimeSpan? LockoutDuration { get; set; }

    /// <summary>Emulates msDS-UserPasswordExpiryTimeComputed (pwdLastSet + <see cref="MaxPasswordAge"/>).</summary>
    public bool EmulateConstructedPasswordExpiry { get; set; }

    /// <summary>Domain maxPwdAge used by the constructed password expiry (default 42 days).</summary>
    public TimeSpan MaxPasswordAge { get; set; } = TimeSpan.FromDays(42);

    /// <summary>highestCommittedUSN of the current DC (in its USN space), or the override.</summary>
    public long HighestCommittedUsn
    {
        get
        {
            lock (_gate)
            {
                return _highestUsnOverride ?? _usn + CurrentUsnOffset;
            }
        }
    }

    /// <summary>invocationId of the DC the session is pinned to (<see cref="DefaultInvocationId"/> when none is seeded).</summary>
    public Guid CurrentInvocationId
    {
        get
        {
            lock (_gate)
            {
                return CurrentDomainController?.InvocationId ?? DefaultInvocationId;
            }
        }
    }

    /// <summary>RootDSE dsServiceName: DN of the NTDS Settings object of the current DC.</summary>
    public string DsServiceName
    {
        get
        {
            lock (_gate)
            {
                return CurrentDomainController?.NtdsSettingsDn.ToString() ?? SyntheticNtdsSettingsDn(Info.Server).ToString();
            }
        }
    }

    private DomainControllerState? CurrentDomainController =>
        _domainControllers.TryGetValue(Info.Server, out var dc) ? dc : null;

    private long CurrentUsnOffset => CurrentDomainController?.UsnOffset ?? 0;

    // ---------------------------------------------------------------- domain controllers

    /// <summary>
    /// Seeds a domain controller in the configuration NC (creating the configuration containers when missing):
    /// <c>CN=&lt;HOST&gt;,CN=Servers,CN=Default-First-Site-Name,CN=Sites,&lt;config&gt;</c> (server, dNSHostName) with its
    /// <c>CN=NTDS Settings</c> (nTDSDSA or nTDSDSARO category, invocationId, objectGUID, msDS-HasDomainNCs). The first
    /// seeded DC shares the fake's USN space; every further DC gets its own USN offset, so failback to a known DC can be
    /// tested. Switches on <see cref="EnforceNamingContextBoundaries"/>. The domain root must exist.
    /// </summary>
    /// <returns>The DN of the NTDS Settings object.</returns>
    public string SeedDomainController(string hostName, Guid invocationId, bool isPdc, bool isReadOnly = false, string? domainNc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        lock (_gate)
        {
            if (_domainControllers.ContainsKey(hostName))
            {
                throw new InvalidOperationException($"The domain controller '{hostName}' is already seeded.");
            }

            var configuration = FakeDn.Parse(Info.ConfigurationNamingContext);
            var schema = FakeDn.Parse(Info.SchemaNamingContext);
            EnsureSeeded(configuration, "configuration");
            var sites = EnsureSeeded(configuration.Child("CN", "Sites"), "sitesContainer");
            var site = EnsureSeeded(sites.Child("CN", "Default-First-Site-Name"), "site");
            var servers = EnsureSeeded(site.Child("CN", "Servers"), "serversContainer");
            var label = hostName.Split('.')[0].ToUpperInvariant();
            var server = servers.Child("CN", label);
            SeedCore(server, new Dictionary<string, object?>
            {
                [Attr.ObjectClass] = new[] { "top", "server" },
                [Attr.ObjectGuid] = DeterministicGuid("server:" + hostName),
                [Attr.DnsHostName] = hostName,
            });
            var ntds = server.Child("CN", NtdsSettingsName);
            SeedCore(ntds, new Dictionary<string, object?>
            {
                [Attr.ObjectClass] = new[] { "top", "applicationSettings", "nTDSDSA" },
                [Attr.ObjectCategory] = schema.Child("CN", isReadOnly ? "NTDS-DSA-RO" : "NTDS-DSA").ToString(),
                [Attr.ObjectGuid] = DeterministicGuid("ntds:" + hostName),
                ["invocationId"] = invocationId.ToByteArray(),
                ["msDS-HasDomainNCs"] = domainNc ?? Info.DefaultNamingContext,
            });
            if (isPdc && _entries.TryGetValue(FakeDn.Parse(Info.DefaultNamingContext), out var domainHead))
            {
                domainHead.Attributes["fSMORoleOwner"] = [ntds.ToString()];
                RecordMetadata(domainHead, ["fSMORoleOwner"], _usn);
            }

            _domainControllers[hostName] = new DomainControllerState(hostName, ntds, _domainControllers.Count * DomainControllerUsnSpacing)
            {
                InvocationId = invocationId,
            };
            EnforceNamingContextBoundaries = true;
            return ntds.ToString();
        }
    }

    /// <summary>The current DC was restored from a backup: it gets a new invocationId (Resync expected).</summary>
    /// <returns>The new invocationId.</returns>
    public Guid SimulateRestore()
    {
        lock (_gate)
        {
            var dc = CurrentDomainController ?? throw new InvalidOperationException("No domain controller is seeded for the current server.");
            dc.InvocationId = DeterministicGuid($"restore:{dc.HostName}:{++_restoreCounter}");
            if (_entries.TryGetValue(dc.NtdsSettingsDn, out var ntds))
            {
                ntds.Attributes["invocationId"] = [dc.InvocationId.ToByteArray()];
            }

            return dc.InvocationId;
        }
    }

    /// <summary>Reports this highestCommittedUSN instead of the real one (USN rollback); null restores the real value.</summary>
    public void OverrideHighestCommittedUsn(long? value)
    {
        lock (_gate)
        {
            _highestUsnOverride = value;
        }
    }

    /// <summary>Pins the session to another seeded DC (failover / failback): Info.Server, dsServiceName and the USN space change.</summary>
    public void SwitchServer(string hostName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        lock (_gate)
        {
            if (!_domainControllers.TryGetValue(hostName, out var dc))
            {
                throw new InvalidOperationException($"The domain controller '{hostName}' is not seeded.");
            }

            Info = Info with { Server = dc.HostName };
        }
    }

    // ---------------------------------------------------------------- logon noise and lockouts

    /// <summary>
    /// A successful logon: lastLogonTimestamp is written (USN bump, no change the watchdog reports) and badPwdCount reset.
    /// With <see cref="LockoutDuration"/> set and the lockout expired, lockoutTime is reset to 0 in the same write
    /// (Samba <c>authsam_logon_success_accounting</c>, Windows likewise).
    /// </summary>
    public void SimulateSuccessfulLogon(string distinguishedName)
    {
        lock (_gate)
        {
            var entry = FindExisting(distinguishedName);
            var now = _timeProvider.GetUtcNow();
            var changed = new List<string> { Attr.LastLogonTimestamp };
            entry.Attributes[Attr.LastLogonTimestamp] = [FileTimeNow()];
            entry.Attributes[Attr.BadPwdCount] = ["0"];
            if (LockoutDuration is { } duration && LockoutTimeOf(entry) is { } lockedAt && now >= lockedAt + duration)
            {
                entry.Attributes[Attr.LockoutTime] = ["0"];
                changed.Add(Attr.LockoutTime);
            }

            Touch(entry, changed);
        }
    }

    /// <summary>A failed logon: badPwdCount/badPasswordTime change, uSNChanged does not (both are not replicated).</summary>
    public void SimulateBadPassword(string distinguishedName)
    {
        lock (_gate)
        {
            var entry = FindExisting(distinguishedName);
            var count = int.TryParse(entry.First(Attr.BadPwdCount), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 0;
            entry.Attributes[Attr.BadPwdCount] = [(count + 1).ToString(CultureInfo.InvariantCulture)];
            entry.Attributes[Attr.BadPasswordTime] = [FileTimeNow()];
        }
    }

    /// <summary>The account is locked out now: lockoutTime = now (USN bump).</summary>
    public void SimulateLockout(string distinguishedName)
    {
        lock (_gate)
        {
            var entry = FindExisting(distinguishedName);
            entry.Attributes[Attr.LockoutTime] = [FileTimeNow()];
            entry.Attributes[Attr.BadPasswordTime] = [FileTimeNow()];
            Touch(entry, [Attr.LockoutTime]);
        }
    }

    /// <summary>What msDS-ResultantPSO returns for the object (null removes it).</summary>
    public void SetResultantPso(string distinguishedName, string? passwordSettingsDn)
    {
        lock (_gate)
        {
            var entry = FindExisting(distinguishedName);
            if (passwordSettingsDn is null)
            {
                _resultantPsos.Remove(entry);
            }
            else
            {
                _resultantPsos[entry] = FakeDn.Parse(passwordSettingsDn).ToString();
            }
        }
    }

    /// <summary>
    /// Adds values to an existing object as part of its initial state (test setup): no USN bump, replication metadata
    /// as if the values were set at creation. Values follow the rules of <see cref="Seed"/>; null removes an attribute.
    /// </summary>
    public void SeedAttributes(string distinguishedName, IReadOnlyDictionary<string, object?> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        lock (_gate)
        {
            var entry = FindExisting(distinguishedName);
            foreach (var (attribute, value) in attributes)
            {
                var name = Canonical(attribute);
                var values = value is null ? [] : ToValues(name, value);
                if (values.Count == 0)
                {
                    entry.Attributes.Remove(name);
                }
                else
                {
                    entry.Attributes[name] = values;
                }
            }

            var usn = long.TryParse(entry.First(Attr.UsnChanged), NumberStyles.Integer, CultureInfo.InvariantCulture, out var changed) ? changed : _usn;
            RecordMetadata(entry, attributes.Keys, usn);
        }
    }

    /// <summary>
    /// Emulates a DC that has not received the latest originating write of <paramref name="attribute"/> yet: the value
    /// goes back to <paramref name="values"/> and its replication metadata version one below the current one (no USN
    /// bump). Used for the version-aware Resync on a stale DC (section 10.4).
    /// </summary>
    public void SimulateStaleReplica(string distinguishedName, string attribute, params object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        lock (_gate)
        {
            var entry = FindExisting(distinguishedName);
            var name = Canonical(attribute);
            entry.Attributes[name] = [.. values.SelectMany(v => ToValues(name, v))];
            if (_metadata.TryGetValue(entry, out var metadata) && metadata.TryGetValue(name, out var current))
            {
                metadata[name] = current with { Version = Math.Max(1, current.Version - 1) };
            }
        }
    }

    // ---------------------------------------------------------------- read restrictions

    /// <summary>
    /// Simulates a hardened DACL: the attributes of this object are neither returned nor matched by filters (hiding
    /// uSNChanged makes the object vanish from <c>(uSNChanged&gt;=x)</c>). Follows the object through renames.
    /// </summary>
    public void HideAttributes(string distinguishedName, params string[] attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        lock (_gate)
        {
            var entry = FindExisting(distinguishedName);
            if (!_hiddenAttributes.TryGetValue(entry, out var hidden))
            {
                _hiddenAttributes[entry] = hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            foreach (var attribute in attributes)
            {
                hidden.Add(Canonical(attribute));
            }
        }
    }

    // ---------------------------------------------------------------- hooks used by the main file

    private DirectoryObject ReadRootDse(IReadOnlyList<string> requested)
    {
        var dc = CurrentDomainController;
        var ntds = dc?.NtdsSettingsDn ?? SyntheticNtdsSettingsDn(Info.Server);
        var values = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["highestCommittedUSN"] = [(_highestUsnOverride ?? _usn + CurrentUsnOffset).ToString(CultureInfo.InvariantCulture)],
            ["dsServiceName"] = [ntds.ToString()],
            [Attr.ServerName] = [ntds.Parent!.ToString()],
            [Attr.DnsHostNameRootDse] = [Info.Server],
            ["isSynchronized"] = [IsSynchronized ? "TRUE" : "FALSE"],
            [Attr.CurrentTime] = [AdConvert.ToGeneralizedTime(_timeProvider.GetUtcNow())],
            [Attr.DefaultNamingContext] = [Info.DefaultNamingContext],
            [Attr.ConfigurationNamingContext] = [Info.ConfigurationNamingContext],
            [Attr.SchemaNamingContext] = [Info.SchemaNamingContext],
            [Attr.NamingContexts] = [Info.DefaultNamingContext, Info.ConfigurationNamingContext, Info.SchemaNamingContext],
            [Attr.SupportedControl] = [.. Info.SupportedControls.Order(StringComparer.Ordinal)],
        };
        if (Info.RootDomainNamingContext is { } root)
        {
            values[Attr.RootDomainNamingContext] = [root];
        }

        var all = requested.Count == 0 || requested.Contains("*", StringComparer.Ordinal);
        var attributes = values
            .Where(kv => all || requested.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
            .Select(kv => new AttributeValues(kv.Key, kv.Value));
        return new DirectoryObject(string.Empty, attributes);
    }

    /// <summary>Constructed and translated values of the watchdog emulation (true when <paramref name="name"/> is one).</summary>
    private bool TryGetWatchdogValues(Entry entry, string name, out IReadOnlyList<object> values)
    {
        values = [];
        if (string.Equals(name, Attr.UsnChanged, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, Attr.UsnCreated, StringComparison.OrdinalIgnoreCase))
        {
            var offset = CurrentUsnOffset;
            if (offset == 0 || !long.TryParse(entry.First(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var usn))
            {
                return false;
            }

            values = [(usn + offset).ToString(CultureInfo.InvariantCulture)];
            return true;
        }

        if (string.Equals(name, ReplAttributeMetaData, StringComparison.OrdinalIgnoreCase))
        {
            values = _metadata.TryGetValue(entry, out var metadata)
                ? metadata.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => (object)MetadataXml(kv.Key, kv.Value)).ToList()
                : [];
            return true;
        }

        if (string.Equals(name, UserPasswordExpiryTimeComputed, StringComparison.OrdinalIgnoreCase))
        {
            if (!EmulateConstructedPasswordExpiry)
            {
                return false;
            }

            values = PasswordExpiry(entry) is { } expiry ? [expiry.ToString(CultureInfo.InvariantCulture)] : [];
            return true;
        }

        if (string.Equals(name, ResultantPso, StringComparison.OrdinalIgnoreCase))
        {
            values = _resultantPsos.TryGetValue(entry, out var pso) ? [pso] : [];
            return true;
        }

        return false;
    }

    private long? PasswordExpiry(Entry entry)
    {
        if (!entry.HasClass(ObjectClasses.User))
        {
            return null;
        }

        var uac = int.TryParse(entry.First(Attr.UserAccountControl), NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? u : 0;
        if (entry.HasClass(ObjectClasses.Computer) || (uac & (int)UserAccountControl.DontExpirePassword) != 0)
        {
            return long.MaxValue;
        }

        if (!long.TryParse(entry.First(Attr.PwdLastSet), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pwdLastSet) || pwdLastSet == 0)
        {
            return 0;
        }

        return pwdLastSet + MaxPasswordAge.Ticks;
    }

    /// <summary>Locked according to lockoutTime and, when set, <see cref="LockoutDuration"/>.</summary>
    private bool IsLockedOut(Entry entry)
    {
        if (LockoutTimeOf(entry) is not { } lockedAt)
        {
            return false;
        }

        return LockoutDuration is not { } duration || _timeProvider.GetUtcNow() < lockedAt + duration;
    }

    private static DateTimeOffset? LockoutTimeOf(Entry entry) =>
        long.TryParse(entry.First(Attr.LockoutTime), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? AdConvert.FromFileTime(value)
            : null;

    private bool IsHidden(Entry entry, string name) =>
        _hiddenAttributes.TryGetValue(entry, out var hidden) && hidden.Contains(name);

    /// <summary>Same naming context (only enforced with <see cref="EnforceNamingContextBoundaries"/>).</summary>
    private bool IsInSameNamingContext(DistinguishedName dn, DistinguishedName baseDn) =>
        !EnforceNamingContextBoundaries || NamingContextRootOf(dn) == NamingContextRootOf(baseDn);

    /// <summary>The NC head containing <paramref name="dn"/>: schema, configuration or the domain.</summary>
    private DistinguishedName NamingContextRootOf(DistinguishedName dn)
    {
        if (EnforceNamingContextBoundaries)
        {
            var schema = FakeDn.Parse(Info.SchemaNamingContext);
            if (FakeDn.IsSameOrBelow(dn, schema))
            {
                return schema;
            }

            var configuration = FakeDn.Parse(Info.ConfigurationNamingContext);
            if (FakeDn.IsSameOrBelow(dn, configuration))
            {
                return configuration;
            }
        }

        return dn.DomainComponent;
    }

    private bool IsDeniedDeletedObject(Entry entry, DirectorySearch search) =>
        DenyDeletedObjects && search.IncludeDeleted && entry.IsDeleted && !IsDeletedObjectsContainer(entry);

    private bool IsDeniedDeletedObjectsContainer(Entry entry, DirectorySearch search) =>
        DenyDeletedObjects && search.IncludeDeleted && IsDeletedObjectsContainer(entry);

    private static bool IsDeletedObjectsContainer(Entry entry) =>
        entry.IsDeleted && string.Equals(entry.Dn.Name, DeletedObjectsName, StringComparison.OrdinalIgnoreCase);

    /// <summary>An existing object, deleted ones included (Show Deleted), unless <see cref="DenyDeletedObjects"/>.</summary>
    private Entry FindIncludingDeleted(string distinguishedName)
    {
        var dn = FakeDn.Parse(distinguishedName);
        if (!_entries.TryGetValue(dn, out var entry) || (entry.IsDeleted && DenyDeletedObjects))
        {
            throw Error(32, $"The object '{distinguishedName}' does not exist.", distinguishedName);
        }

        return entry;
    }

    /// <summary>Extended DN form (<c>&lt;GUID=…&gt;;&lt;SID=…&gt;;dn</c>) of DN and DN-Binary values.</summary>
    private object ToExtendedValue(string attribute, object value)
    {
        if (value is not string text)
        {
            return value;
        }

        switch (Schema.FindAttribute(attribute)?.Syntax)
        {
            case AttributeSyntax.DistinguishedName:
                return ExtendDn(text);
            case AttributeSyntax.DnWithBinary when TrySplitDnBinary(text, out var prefix, out var dn):
                return prefix + ExtendDn(dn);
            default:
                return value;
        }
    }

    private string ExtendDn(string text)
    {
        if (!DistinguishedName.TryParse(text, out var dn) || dn.IsRoot || !_entries.TryGetValue(dn, out var target))
        {
            return text;
        }

        var guid = target.Attributes.TryGetValue(Attr.ObjectGuid, out var guidValues) && guidValues is [byte[] { Length: 16 } guidBytes]
            ? new Guid(guidBytes)
            : (Guid?)null;
        var sid = target.Attributes.TryGetValue(Attr.ObjectSid, out var sidValues) && sidValues is [byte[] sidBytes]
            ? AdConvert.SidToString(sidBytes)
            : null;
        return new ExtendedDistinguishedName(guid, sid, text).ToString();
    }

    /// <summary>Splits <c>B:&lt;len&gt;:&lt;hex&gt;:&lt;dn&gt;</c> into the binary prefix (with the trailing colon) and the DN.</summary>
    private static bool TrySplitDnBinary(string value, out string prefix, out string dn)
    {
        prefix = dn = string.Empty;
        if (!value.StartsWith("B:", StringComparison.Ordinal))
        {
            return false;
        }

        var lengthEnd = value.IndexOf(':', 2);
        if (lengthEnd < 0 || !int.TryParse(value.AsSpan(2, lengthEnd - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var length))
        {
            return false;
        }

        var dnStart = lengthEnd + 1 + length;
        if (dnStart >= value.Length || value[dnStart] != ':')
        {
            return false;
        }

        prefix = value[..(dnStart + 1)];
        dn = value[(dnStart + 1)..];
        return true;
    }

    // ---------------------------------------------------------------- replication metadata

    /// <summary>Metadata of a new object: version 1 for every replicated attribute (seeded accounts with a password
    /// also get the unicodePwd entry a real DC would have).</summary>
    private void RecordCreationMetadata(Entry entry, bool seeded)
    {
        var usn = long.TryParse(entry.First(Attr.UsnCreated), NumberStyles.Integer, CultureInfo.InvariantCulture, out var created) ? created : _usn;
        var attributes = entry.Attributes.Keys.ToList();
        if (seeded && entry.HasClass(ObjectClasses.User) && entry.First(Attr.PwdLastSet) is { } pwdLastSet && pwdLastSet != "0")
        {
            attributes.Add(Attr.UnicodePwd);
        }

        RecordMetadata(entry, attributes, usn);
    }

    /// <summary>The Touch hook: originating write of <paramref name="changedAttributes"/> on the current DC.</summary>
    private void RecordMetadata(Entry entry, IEnumerable<string> changedAttributes, long usn)
    {
        if (!_metadata.TryGetValue(entry, out var metadata))
        {
            _metadata[entry] = metadata = new Dictionary<string, MetadataEntry>(StringComparer.OrdinalIgnoreCase);
        }

        var invocationId = CurrentDomainController?.InvocationId ?? DefaultInvocationId;
        var now = _timeProvider.GetUtcNow();
        foreach (var attribute in changedAttributes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = Canonical(attribute);
            if (NonReplicated.Contains(name) || Schema.FindAttribute(name) is { IsLinked: true } or { IsConstructed: true })
            {
                continue;
            }

            var version = metadata.TryGetValue(name, out var previous) ? previous.Version + 1 : 1;
            metadata[name] = new MetadataEntry(version, now, invocationId, usn);
        }
    }

    private string MetadataXml(string attribute, MetadataEntry entry)
    {
        var originating = _domainControllers.Values.FirstOrDefault(d => d.InvocationId == entry.InvocationId);
        var originatingDsa = originating?.NtdsSettingsDn ?? CurrentDomainController?.NtdsSettingsDn ?? SyntheticNtdsSettingsDn(Info.Server);
        var sb = new StringBuilder();
        sb.Append("<DS_REPL_ATTR_META_DATA>\n")
            .Append("\t<pszAttributeName>").Append(attribute).Append("</pszAttributeName>\n")
            .Append("\t<dwVersion>").Append(entry.Version.ToString(CultureInfo.InvariantCulture)).Append("</dwVersion>\n")
            .Append("\t<ftimeLastOriginatingChange>")
            .Append(entry.OriginatingTime.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
            .Append("</ftimeLastOriginatingChange>\n")
            .Append("\t<uuidLastOriginatingDsaInvocationID>").Append(entry.InvocationId.ToString("D"))
            .Append("</uuidLastOriginatingDsaInvocationID>\n")
            .Append("\t<usnOriginatingChange>").Append((entry.Usn + (originating?.UsnOffset ?? 0)).ToString(CultureInfo.InvariantCulture))
            .Append("</usnOriginatingChange>\n")
            .Append("\t<usnLocalChange>").Append((entry.Usn + CurrentUsnOffset).ToString(CultureInfo.InvariantCulture)).Append("</usnLocalChange>\n")
            // Windows leaves '&' in DNs unescaped and terminates the value with a NUL character.
            .Append("\t<pszLastOriginatingDsaDN>").Append(originatingDsa.ToString()).Append("</pszLastOriginatingDsaDN>\n")
            .Append("</DS_REPL_ATTR_META_DATA>\n\0");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- helpers

    private DistinguishedName EnsureSeeded(DistinguishedName dn, string objectClass)
    {
        if (!_entries.ContainsKey(dn))
        {
            SeedCore(dn, new Dictionary<string, object?>
            {
                [Attr.ObjectClass] = new[] { "top", objectClass },
                [Attr.ObjectGuid] = DeterministicGuid("container:" + dn.ToString().ToUpperInvariant()),
            });
        }

        return dn;
    }

    private DistinguishedName SyntheticNtdsSettingsDn(string hostName) =>
        FakeDn.Parse(Info.ConfigurationNamingContext)
            .Child("CN", "Sites").Child("CN", "Default-First-Site-Name").Child("CN", "Servers")
            .Child("CN", hostName.Split('.')[0].ToUpperInvariant()).Child("CN", NtdsSettingsName);

    internal static Guid DeterministicGuid(string seed) => new(MD5.HashData(Encoding.UTF8.GetBytes(seed)));

    /// <summary>One seeded DC: its NTDS Settings object, USN space and (changeable) invocationId.</summary>
    private sealed class DomainControllerState(string hostName, DistinguishedName ntdsSettingsDn, long usnOffset)
    {
        public string HostName { get; } = hostName;

        public DistinguishedName NtdsSettingsDn { get; } = ntdsSettingsDn;

        public long UsnOffset { get; } = usnOffset;

        public Guid InvocationId { get; set; }
    }

    /// <summary>Replication metadata of one attribute (USN in the fake's global space).</summary>
    private sealed record MetadataEntry(int Version, DateTimeOffset OriginatingTime, Guid InvocationId, long Usn);
}
