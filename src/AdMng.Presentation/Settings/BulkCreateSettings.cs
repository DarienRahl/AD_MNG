using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Model;
using AdMng.Core.Security;

namespace AdMng.Presentation.Settings;

/// <summary>One naming rule of a profile (docs/dev/phase3-bulk-create.md 2.6): attribute, pattern, precedence over a column.</summary>
/// <param name="Attribute">The LDAP display name (for the UPN: the prefix pattern).</param>
/// <param name="Pattern">The pattern in the template syntax.</param>
/// <param name="OverridesColumn"><em>Reguła zastępuje kolumnę</em>.</param>
public sealed record BulkCreateRuleSetting(string Attribute, string Pattern, bool OverridesColumn)
{
    /// <summary>The attribute and the pattern's length only.</summary>
    public override string ToString() => $"Rule setting {Attribute}: {Pattern?.Length ?? 0} characters, overrides column={OverridesColumn}";
}

/// <summary>
/// A remembered column mapping (2.6): the folded header – or <c>#</c> and the Excel letter of a table without a header –
/// and its target. Stored only under the header rule of <see cref="BulkCreateSettings.IsStorableHeader"/>.
/// </summary>
/// <param name="Header">The folded header (<c>telefonkomorkowy</c>) or <c>#C</c>.</param>
/// <param name="Target">What the column feeds.</param>
/// <param name="Attribute">The LDAP display name for <see cref="ColumnTargetKind.Attribute"/>.</param>
/// <param name="Order">The name order of <see cref="ColumnTargetKind.FullName"/>.</param>
/// <param name="Case">The case choice.</param>
public sealed record BulkCreateHeaderMapping(string Header, ColumnTargetKind Target, string? Attribute, NameOrder Order, TextCase Case)
{
    /// <summary>The target only – a header is operator text.</summary>
    public override string ToString() => $"Header mapping → {Target}{(Attribute is null ? string.Empty : " " + Attribute)} ({Order}, {Case})";
}

/// <summary>
/// A saved naming profile of the bulk creation wizard (2.6): like a saved view – names, patterns, options and portable DNs,
/// never a row value, a rendered value, a counter position or a password (rule 1, rule 2). Property names avoid
/// <em>password</em>, <em>secret</em> and <em>credential</em> (<c>SerializedSettings_NeverContainPasswords</c>).
/// </summary>
public sealed class BulkCreateProfile
{
    /// <summary>A stable id (<see cref="BulkCreateSettings.LastProfileIds"/>, the container defaults).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    /// <summary>The kind the profile is for (user, contact, group, computer).</summary>
    public ObjectKind Kind { get; set; } = ObjectKind.User;

    public List<BulkCreateRuleSetting> Rules { get; set; } = [];

    /// <summary>The UPN suffix the operator picked (a DNS name, like a connection profile's domain); null = the window's domain.</summary>
    public string? UpnSuffix { get; set; }

    /// <summary>The mail domain (a DNS name); null = the UPN suffix.</summary>
    public string? MailDomain { get; set; }

    public CounterMode CounterMode { get; set; } = CounterMode.FillGaps;

    public bool ReuseRecycleBinNumbers { get; set; }

    public CollisionStrategy Collisions { get; set; } = CollisionStrategy.AppendNumber;

    public bool SanitizeIdentifiers { get; set; } = true;

    public bool TruncateNames { get; set; } = true;

    /// <summary>The primary <c>SMTP:</c> address in <c>proxyAddresses</c> (off by default, 2.14).</summary>
    public bool AddPrimarySmtp { get; set; }

    public PhoneFormat Phones { get; set; } = PhoneFormat.AsIs;

    /// <summary>How created users get their first password (the mode only – never a password).</summary>
    public InitialPasswordMode InitialMode { get; set; } = InitialPasswordMode.Generate;

    /// <summary>The length of generated first passwords.</summary>
    public int InitialLength { get; set; } = PasswordGenerator.DefaultLength;

    public bool MustChangeAtLogon { get; set; } = true;

    public bool CreateEnabled { get; set; } = true;

    /// <summary>The accounts expire this many days after the run (end of that day); null = never.</summary>
    public int? ExpiresAfterDays { get; set; }

    /// <summary><em>Szukaj istniejących kont tej osoby</em>.</summary>
    public bool MatchExisting { get; set; } = true;

    /// <summary>The option groups as portable DNs (<see cref="Views.SavedView.ToPortableDn"/>).</summary>
    public List<string> GroupDns { get; set; } = [];

    /// <summary>The DNS name of the domain <see cref="GroupDns"/> were picked in (in another domain they are dropped with a warning).</summary>
    public string? GroupDomain { get; set; }

    public GroupScope GroupScope { get; set; } = GroupScope.Global;

    public bool SecurityGroup { get; set; } = true;

    public List<BulkCreateHeaderMapping> HeaderMappings { get; set; } = [];

    /// <summary>The containers (portable DNs) this profile is the default of.</summary>
    public List<string> DefaultForContainers { get; set; } = [];

    /// <summary>The kind and the counts only.</summary>
    public override string ToString() =>
        $"Bulk create profile ({Kind}, {Rules.Count} rules, {HeaderMappings.Count} mappings, {GroupDns.Count} groups)";
}

/// <summary>
/// What the bulk creation wizard remembers (2.6; complete in the contract): the saved naming profiles and the last profile
/// used per kind. Profiles are managed inside the wizard (no Settings-window section).
/// </summary>
public sealed class BulkCreateSettings
{
    /// <summary>Saved profiles kept at most.</summary>
    public const int MaxProfiles = 50;

    /// <summary>The longest profile name kept.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The longest pattern kept.</summary>
    public const int MaxPatternLength = 1024;

    /// <summary>The longest stored (folded) header.</summary>
    public const int MaxHeaderLength = 64;

    /// <summary>Password-like folded headers (substrings): the same list as <c>ColumnSynonyms.IsPasswordLike</c> (2.1).</summary>
    private static readonly string[] PasswordSubstrings = ["haslo", "hasla", "password", "passwd", "pwd", "kennwort"];

    private static readonly string[] PasswordHeaders = ["pin", "kodpin", "pass"];

    public List<BulkCreateProfile> Profiles { get; set; } = [];

    /// <summary>The id of the last used profile per kind (key: the <see cref="ObjectKind"/> name).</summary>
    public Dictionary<string, string> LastProfileIds { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The header rule (2.6): a remembered mapping's header is a folded header (lower-case letters and digits) of at most
    /// <see cref="MaxHeaderLength"/> characters with fewer than 7 digits and no <c>@</c> – so it cannot be an address or a
    /// phone number – and not password-like; or <c>#</c> and an Excel letter (a table without a header).
    /// </summary>
    public static bool IsStorableHeader(string? header)
    {
        if (string.IsNullOrEmpty(header) || header.Length > MaxHeaderLength)
        {
            return false;
        }

        if (header[0] == '#')
        {
            return header.Length is >= 2 and <= 4 && header.Skip(1).All(c => c is >= 'A' and <= 'Z');
        }

        return header.All(c => char.IsLetterOrDigit(c) && !char.IsUpper(c))
            && header.Count(char.IsDigit) < 7
            && !PasswordSubstrings.Any(s => header.Contains(s, StringComparison.Ordinal))
            && !PasswordHeaders.Contains(header, StringComparer.Ordinal);
    }

    /// <summary>
    /// Repairs a hand-edited or older settings file: profiles without a name or with an unknown kind are dropped, rules with
    /// an empty or blocked attribute (<see cref="CreationValues.IsNamedBlockedAttribute"/>) and mappings that break the header
    /// rule are dropped, the generated length is clamped, ids are de-duplicated, at most <see cref="MaxProfiles"/> are kept.
    /// </summary>
    public void Normalize()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var profiles = new List<BulkCreateProfile>();
        foreach (var profile in Profiles ?? [])
        {
            if (profile is null || string.IsNullOrWhiteSpace(profile.Name) || !IsCreatableKind(profile.Kind))
            {
                continue;
            }

            Normalize(profile);
            if (!ids.Add(profile.Id))
            {
                profile.Id = Guid.NewGuid().ToString("N");
                ids.Add(profile.Id);
            }

            profiles.Add(profile);
            if (profiles.Count == MaxProfiles)
            {
                break;
            }
        }

        Profiles = profiles;
        var last = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (kind, id) in LastProfileIds ?? [])
        {
            if (Enum.TryParse<ObjectKind>(kind, ignoreCase: false, out var parsed) && IsCreatableKind(parsed) && !string.IsNullOrWhiteSpace(id))
            {
                last[parsed.ToString()] = id;
            }
        }

        LastProfileIds = last;
    }

    private static bool IsCreatableKind(ObjectKind kind) => kind is ObjectKind.User or ObjectKind.Contact or ObjectKind.Group or ObjectKind.Computer;

    private static void Normalize(BulkCreateProfile profile)
    {
        profile.Id = string.IsNullOrWhiteSpace(profile.Id) ? Guid.NewGuid().ToString("N") : profile.Id.Trim();
        profile.Name = Clip(profile.Name, MaxNameLength);
        profile.Rules = [.. (profile.Rules ?? [])
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Attribute) && !CreationValues.IsNamedBlockedAttribute(r.Attribute.Trim()))
            .Select(r => r with { Attribute = r.Attribute.Trim(), Pattern = Clip(r.Pattern, MaxPatternLength) })
            .DistinctBy(r => r.Attribute, StringComparer.OrdinalIgnoreCase)];
        profile.UpnSuffix = DnsNameOrNull(profile.UpnSuffix);
        profile.MailDomain = DnsNameOrNull(profile.MailDomain);
        profile.CounterMode = Enum.IsDefined(profile.CounterMode) ? profile.CounterMode : CounterMode.FillGaps;
        profile.Collisions = Enum.IsDefined(profile.Collisions) ? profile.Collisions : CollisionStrategy.AppendNumber;
        profile.Phones = Enum.IsDefined(profile.Phones) ? profile.Phones : PhoneFormat.AsIs;
        profile.InitialMode = Enum.IsDefined(profile.InitialMode) ? profile.InitialMode : InitialPasswordMode.Generate;
        profile.InitialLength = Math.Clamp(profile.InitialLength, PasswordGenerator.MinimumLength, PasswordGenerator.MaximumLength);
        profile.ExpiresAfterDays = profile.ExpiresAfterDays is { } days ? Math.Clamp(days, 0, 3650) : null;
        profile.GroupDns = [.. (profile.GroupDns ?? []).Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];
        profile.GroupDomain = DnsNameOrNull(profile.GroupDomain);
        profile.GroupScope = profile.GroupScope is GroupScope.Global or GroupScope.DomainLocal or GroupScope.Universal ? profile.GroupScope : GroupScope.Global;
        profile.HeaderMappings = [.. (profile.HeaderMappings ?? [])
            .Where(m => m is not null && IsStorableHeader(m.Header) && Enum.IsDefined(m.Target) && Enum.IsDefined(m.Order) && Enum.IsDefined(m.Case))
            .Where(m => m.Target != ColumnTargetKind.Attribute
                || (!string.IsNullOrWhiteSpace(m.Attribute) && !CreationValues.IsNamedBlockedAttribute(m.Attribute)))
            .DistinctBy(m => m.Header, StringComparer.Ordinal)];
        profile.DefaultForContainers = [.. (profile.DefaultForContainers ?? []).Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string Clip(string? text, int max)
    {
        var trimmed = (text ?? string.Empty).Trim();
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }

    private static string? DnsNameOrNull(string? name)
    {
        var trimmed = name?.Trim().TrimEnd('.').ToLower(CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > 255 || !trimmed.All(c => char.IsLetterOrDigit(c) || c is '.' or '-')
            ? null
            : trimmed;
    }
}
