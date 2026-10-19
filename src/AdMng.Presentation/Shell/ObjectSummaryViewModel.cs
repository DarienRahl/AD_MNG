using AdMng.Core.ActiveDirectory;
using AdMng.Core.Formatting;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Session;

namespace AdMng.Presentation.Shell;

/// <summary>A label/value line in the details pane.</summary>
public sealed record SummaryField(string Label, string Value)
{
    /// <summary>What the line's copy button puts on the clipboard; null for lines that are not copied (states, counts).</summary>
    public string? CopyText { get; init; }

    /// <summary>The line has a copy button.</summary>
    public bool CanCopy => !string.IsNullOrEmpty(CopyText);

    /// <summary>A line whose value is copied as shown.</summary>
    public static SummaryField Copyable(string label, string value) => new(label, value) { CopyText = value };
}

/// <summary>
/// Details pane content for the selected object: name, type, DN, description, key account state (disabled,
/// locked, password expiry, last logon) and a few kind specific fields. Read live with a base read when the
/// selection settles (includes constructed attributes that list searches cannot return).
/// </summary>
public sealed class ObjectSummaryViewModel
{
    /// <summary>Attributes of the details read (constructed ones are valid on base reads).</summary>
    public static IReadOnlyList<string> Attributes { get; } =
    [
        Attr.Name, Attr.DisplayName, Attr.ObjectClass, Attr.Description, Attr.UserAccountControl, Attr.UserAccountControlComputed,
        Attr.LockoutTime, Attr.PwdLastSet, Attr.PasswordExpiryComputed, Attr.AccountExpires, Attr.LastLogonTimestamp,
        Attr.Mail, Attr.SamAccountName, Attr.UserPrincipalName, Attr.Title, Attr.Department, Attr.TelephoneNumber,
        Attr.WhenCreated, Attr.WhenChanged, Attr.OperatingSystem, Attr.OperatingSystemVersion, Attr.DnsHostName,
        Attr.GroupType, Attr.ManagedBy, Attr.Company, Attr.AdminCount, Attr.Mobile, Attr.EmployeeId, Attr.EmployeeNumber,
        Attr.Manager,
    ];

    private ObjectSummaryViewModel(string name, string typeText, ObjectIcon icon, string? dn, IReadOnlyList<SummaryField> fields, ILocalizer localizer)
    {
        Name = name;
        TypeText = typeText;
        Icon = icon;
        DistinguishedName = dn;
        Fields = fields;

        // Name, path and DN have their own places in the pane; as fields they get copy buttons like the other values.
        var single = dn is not null;
        NameField = new SummaryField(localizer[L.Copy.Name], name) { CopyText = single ? name : null };
        PathField = new SummaryField(localizer[L.Details.Path], Path) { CopyText = single ? Path : null };
        DnField = new SummaryField(localizer[L.Details.DistinguishedName], dn ?? string.Empty) { CopyText = dn };
    }

    public string Name { get; }

    public string TypeText { get; }

    public ObjectIcon Icon { get; }

    /// <summary>DN of the object (null for a multi-selection summary).</summary>
    public string? DistinguishedName { get; }

    public string Path => DistinguishedName is null ? string.Empty : AddressResolver.ToDisplayPath(DistinguishedName);

    public string? Description { get; private init; }

    public ObjectKind Kind { get; private init; }

    public bool IsDisabled { get; private init; }

    public bool IsLocked { get; private init; }

    public bool IsProtected { get; private init; }

    /// <summary>Number of selected objects (1 for a single object summary).</summary>
    public int Count { get; private init; } = 1;

    public IReadOnlyList<SummaryField> Fields { get; }

    /// <summary>The object's name with its copy text (not copyable for a multi-selection).</summary>
    public SummaryField NameField { get; }

    /// <summary>Canonical path of the object with its copy text.</summary>
    public SummaryField PathField { get; }

    /// <summary>Distinguished name with its copy text.</summary>
    public SummaryField DnField { get; }

    /// <summary>Summary of a single object read with <see cref="Attributes"/>.</summary>
    /// <param name="obj">The object.</param>
    /// <param name="localizer">UI language.</param>
    /// <param name="formatter">Value formatter of the session.</param>
    /// <param name="schema">Schema of the session (attribute names, confidential attributes).</param>
    /// <param name="policy">Lockout and password policy of the domain.</param>
    /// <param name="now">Reference time of the lockout and expiry states.</param>
    /// <param name="netBiosName">NetBIOS domain name: users and groups get a NETBIOS\login line (null: no such line).</param>
    public static ObjectSummaryViewModel Create(
        DirectoryObject obj,
        ILocalizer localizer,
        IAttributeFormatter formatter,
        ISchemaCatalog? schema,
        DomainPolicy policy,
        DateTimeOffset now,
        string? netBiosName = null)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(formatter);
        ArgumentNullException.ThrowIfNull(policy);
        var fields = new List<SummaryField>();
        var culture = localizer.Culture;

        void AddAttribute(string attribute)
        {
            if (obj.TryGet(attribute, out var values) && values.Count > 0)
            {
                var text = attribute == Attr.GroupType && obj.GetInt32(attribute) is { } gt
                    ? localizer.GroupTypeName(gt)
                    : formatter.Format(attribute, values, culture);
                // Values of secret attributes are never shown (and so never copied) in the pane.
                if (!string.IsNullOrWhiteSpace(text) && !SecretAttributes.IsSecret(attribute, schema))
                {
                    fields.Add(SummaryField.Copyable(localizer.AttributeName(attribute, schema), text));
                }
            }
        }

        void AddDomainLogon()
        {
            if (ObjectCopyFormats.Qualify(netBiosName, obj.GetString(Attr.SamAccountName)) is { } logon)
            {
                fields.Add(SummaryField.Copyable(localizer[L.Copy.DomainLogon], logon));
            }
        }

        void AddManager()
        {
            // The manager's name (RDN value), not the whole DN: that is what goes into a ticket or an e-mail.
            if (obj.GetString(Attr.Manager) is { } manager
                && Core.Ldap.DistinguishedName.TryParse(manager, out var managerDn)
                && !managerDn.IsRoot)
            {
                fields.Add(SummaryField.Copyable(localizer.AttributeName(Attr.Manager, schema), managerDn.Name));
            }
        }

        var locked = obj.Has(Attr.UserAccountControlComputed)
            ? obj.IsLockedOut
            : policy.GetLockState(obj.GetInt64(Attr.LockoutTime), now) != LockState.NotLocked;

        if (obj.Kind.IsAccount())
        {
            var state = locked ? L.Details.StateLocked : obj.IsDisabled ? L.Details.StateDisabled : L.Details.StateEnabled;
            fields.Add(new SummaryField(localizer[L.Details.State], localizer[state]));
        }

        switch (obj.Kind)
        {
            case ObjectKind.User or ObjectKind.InetOrgPerson:
                AddAttribute(Attr.DisplayName);
                AddAttribute(Attr.UserPrincipalName);
                AddAttribute(Attr.SamAccountName);
                AddDomainLogon();
                AddAttribute(Attr.Title);
                AddAttribute(Attr.Department);
                AddAttribute(Attr.Company);
                AddManager();
                AddAttribute(Attr.Mail);
                AddAttribute(Attr.TelephoneNumber);
                AddAttribute(Attr.Mobile);
                AddAttribute(Attr.EmployeeId);
                AddAttribute(Attr.EmployeeNumber);
                fields.Add(new SummaryField(localizer[L.Details.PasswordExpires], PasswordExpiry(obj, localizer, policy)));
                AddAttribute(Attr.PwdLastSet);
                AddAttribute(Attr.AccountExpires);
                AddAttribute(Attr.LastLogonTimestamp);
                break;
            case ObjectKind.Computer:
                AddAttribute(Attr.DnsHostName);
                AddAttribute(Attr.OperatingSystem);
                AddAttribute(Attr.OperatingSystemVersion);
                AddAttribute(Attr.LastLogonTimestamp);
                AddAttribute(Attr.ManagedBy);
                break;
            case ObjectKind.Group:
                AddAttribute(Attr.GroupType);
                AddAttribute(Attr.SamAccountName);
                AddDomainLogon();
                AddAttribute(Attr.Mail);
                AddAttribute(Attr.ManagedBy);
                break;
            case ObjectKind.Contact:
                AddAttribute(Attr.DisplayName);
                AddAttribute(Attr.Mail);
                AddAttribute(Attr.TelephoneNumber);
                AddAttribute(Attr.Mobile);
                AddAttribute(Attr.Company);
                break;
            default:
                AddAttribute(Attr.ManagedBy);
                break;
        }

        AddAttribute(Attr.WhenCreated);
        AddAttribute(Attr.WhenChanged);

        return new ObjectSummaryViewModel(obj.Name, localizer.TypeName(obj), ObjectIcons.For(obj.Kind, obj.IsDisabled, locked,
            obj.GetInt32(Attr.GroupType) is not { } g || GroupTypes.IsSecurity(g)), obj.DistinguishedName, fields, localizer)
        {
            Description = obj.GetString(Attr.Description),
            Kind = obj.Kind,
            IsDisabled = obj.IsDisabled,
            IsLocked = locked,
            IsProtected = obj.IsProtectedByAdminSdHolder,
        };
    }

    /// <summary>Summary of a multi-selection ("12 objects selected" + counts per type).</summary>
    public static ObjectSummaryViewModel ForMany(IReadOnlyCollection<(ObjectKind Kind, bool Disabled)> items, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(localizer);
        var fields = items
            .GroupBy(i => i.Kind)
            .OrderByDescending(g => g.Count())
            .Select(g => new SummaryField(localizer.ObjectKindName(g.Key), g.Count().ToString(localizer.Culture)))
            .ToList();
        var disabled = items.Count(i => i.Disabled);
        if (disabled > 0)
        {
            fields.Add(new SummaryField(localizer[L.Details.StateDisabled], disabled.ToString(localizer.Culture)));
        }

        return new ObjectSummaryViewModel(localizer.Format(L.Details.ManySelected, items.Count), string.Empty, ObjectIcon.Folder, null, fields, localizer)
        {
            Count = items.Count,
        };
    }

    private static string PasswordExpiry(DirectoryObject obj, ILocalizer localizer, DomainPolicy policy)
    {
        var uac = obj.AccountControl ?? UserAccountControl.None;
        if (uac.HasFlag(UserAccountControl.DontExpirePassword))
        {
            return localizer[L.Details.PasswordNeverExpires];
        }

        var pwdLastSet = obj.GetInt64(Attr.PwdLastSet);
        if (pwdLastSet == 0)
        {
            return localizer[L.Details.MustChangePassword];
        }

        var computed = obj.GetInt64(Attr.PasswordExpiryComputed);
        if (computed is long.MaxValue)
        {
            return localizer[L.Details.PasswordNeverExpires];
        }

        var expires = computed is { } c ? AdConvert.FromFileTime(c)
            : pwdLastSet is { } p && policy.MaxPasswordAge is { } age && AdConvert.FromFileTime(p) is { } set ? set + age
            : null;
        return expires is null
            ? localizer[L.Common.Unknown]
            : expires.Value.ToLocalTime().ToString("g", localizer.Culture);
    }
}

/// <summary>One entry of the global quick search drop-down.</summary>
public sealed record SearchSuggestionViewModel(string Dn, string Name, ObjectKind Kind, ObjectIcon Icon, string TypeText, string Location, string? Description)
{
    public bool IsContainer => Kind.IsContainer();
}
