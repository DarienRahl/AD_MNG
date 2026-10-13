using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Templates;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.PropertySheets;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Dialogs;

/// <summary>Result of the live sAMAccountName / UPN availability check.</summary>
public enum NameAvailability
{
    Unknown,
    Checking,
    Available,
    Taken,
    Invalid,
}

/// <summary>
/// "New Object" for users, groups, OUs, computers and contacts. The object is created through the change pipeline:
/// a <see cref="ChangePlan"/> with a <see cref="ChangeAction.Create"/> step (plus, for users, the password reset and
/// the enabling of the account in the same plan) executed by <see cref="Changes.IChangeCoordinator"/> - audited, shown as an
/// activity, published so lists refresh, and reversible (rollback = delete).
/// </summary>
/// <remarks>
/// User accounts are created disabled (userAccountControl 514) without a password, then the password is set and
/// the account is enabled (512) - the order AD requires when the password policy forbids empty passwords.
/// </remarks>
public sealed partial class NewObjectViewModel : WindowViewModelBase
{
    /// <summary>Default sAMAccountName template for new users.</summary>
    public const string DefaultSamTemplate = "{givenName|nodiacritics|lower}.{sn|nodiacritics|lower}";

    /// <summary>Maximum length of an RDN value (cn/ou rangeUpper).</summary>
    public const int MaxNameLength = 64;

    private const int NormalAccount = (int)UserAccountControl.NormalAccount;
    private const int DisabledNormalAccount = (int)(UserAccountControl.NormalAccount | UserAccountControl.AccountDisable);

    private readonly ITemplateEngine _templates;
    private readonly Debouncer _samCheck;
    private bool _fullNameEdited;
    private bool _samEdited;
    private bool _displayNameEdited;
    private bool _upnEdited;
    private bool _updating;

    public NewObjectViewModel(DialogServices services, ITemplateEngine templates, ObjectKind kind, string parentDn)
        : base(services)
    {
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDn);
        if (kind is not (ObjectKind.User or ObjectKind.InetOrgPerson or ObjectKind.Group or ObjectKind.OrganizationalUnit or ObjectKind.Computer or ObjectKind.Contact))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Objects of this kind cannot be created here.");
        }

        Kind = kind == ObjectKind.InetOrgPerson ? ObjectKind.User : kind;
        ParentDn = parentDn;
        _samCheck = new Debouncer(services.Time, TimeSpan.FromMilliseconds(300));
        GroupScopes =
        [
            new(GroupScope.DomainLocal, Localizer.GroupScopeName(GroupScope.DomainLocal)),
            new(GroupScope.Global, Localizer.GroupScopeName(GroupScope.Global)),
            new(GroupScope.Universal, Localizer.GroupScopeName(GroupScope.Universal)),
        ];
        if (services.Sessions.Current is { } session)
        {
            UpnSuffix = session.Info.DomainDnsName;
            UpnSuffixes = [session.Info.DomainDnsName];
        }
    }

    /// <summary>"New Object - User" / "Nowy obiekt - Użytkownik".</summary>
    public override string WindowTitle => Localizer.Format(L.NewObject.WindowTitle, Localizer.ObjectKindName(Kind));

    public ObjectKind Kind { get; }

    public bool IsUser => Kind == ObjectKind.User;

    public bool IsGroup => Kind == ObjectKind.Group;

    public bool IsOrganizationalUnit => Kind == ObjectKind.OrganizationalUnit;

    public bool IsComputer => Kind == ObjectKind.Computer;

    public bool IsContact => Kind == ObjectKind.Contact;

    public string ParentDn { get; }

    /// <summary>"Create in: contoso.test/Firma/IT".</summary>
    public string ParentPath => AddressResolver.ToDisplayPath(ParentDn);

    /// <summary>The DN of the created object (after a successful <see cref="CreateCommand"/>).</summary>
    public string? CreatedDn { get; private set; }

    // ---- Person (user, contact) ---------------------------------------------------------------------------

    [ObservableProperty]
    public partial string FirstName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Initials { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LastName { get; set; } = string.Empty;

    /// <summary>Full name = the object's name (cn). Follows "{givenName} {sn}" until edited.</summary>
    [ObservableProperty]
    public partial string FullName { get; set; } = string.Empty;

    /// <summary>Contacts: display name (follows the full name until edited).</summary>
    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    // ---- User ---------------------------------------------------------------------------------------------

    /// <summary>UPN prefix (follows the sAMAccountName until edited).</summary>
    [ObservableProperty]
    public partial string UpnPrefix { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UpnSuffix { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<string> UpnSuffixes { get; private set; } = [];

    /// <summary>sAMAccountName (users: generated from the template until edited; groups: the name; computers: NAME$).</summary>
    [ObservableProperty]
    public partial string SamAccountName { get; set; } = string.Empty;

    /// <summary>Template generating the sAMAccountName of users.</summary>
    [ObservableProperty]
    public partial string SamTemplate { get; set; } = DefaultSamTemplate;

    [ObservableProperty]
    public partial NameAvailability SamAvailability { get; private set; }

    /// <summary>Localized availability text ("available", "already used by Jan Kowalski").</summary>
    [ObservableProperty]
    public partial string SamAvailabilityText { get; private set; } = string.Empty;

    /// <summary>Password boxes (set by the view; read only when creating).</summary>
    public IPasswordInput? PasswordInput { get; set; }

    public IPasswordInput? ConfirmPasswordInput { get; set; }

    [ObservableProperty]
    public partial bool MustChangePassword { get; set; } = true;

    /// <summary>Not supported yet (needs ACL editing): always false and disabled.</summary>
    public bool CannotChangePassword => false;

    public bool IsCannotChangePasswordSupported => false;

    [ObservableProperty]
    public partial bool PasswordNeverExpires { get; set; }

    [ObservableProperty]
    public partial bool AccountDisabled { get; set; }

    /// <summary>Hint when "password never expires" overrides "must change password" (ADUC warning).</summary>
    public string? PasswordOptionsWarning => MustChangePassword && PasswordNeverExpires ? Localizer[L.NewObject.NeverExpiresWarning] : null;

    // ---- Group, OU, computer ------------------------------------------------------------------------------

    /// <summary>Name of a group, OU or computer.</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    public IReadOnlyList<Option<GroupScope>> GroupScopes { get; }

    [ObservableProperty]
    public partial GroupScope GroupScope { get; set; } = GroupScope.Global;

    [ObservableProperty]
    public partial bool IsSecurityGroup { get; set; } = true;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    /// <summary>Protection from accidental deletion needs ACL support (coming later): disabled.</summary>
    public bool IsProtectionSupported => false;

    public string ProtectionNote => Localizer[L.NewObject.ProtectionComingSoon];

    // ---- State --------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>Localized validation or creation error.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Delay of the live sAMAccountName check (tests use zero).</summary>
    internal TimeSpan AvailabilityCheckDelay
    {
        get => _samCheck.Delay;
        set => _samCheck.Delay = value;
    }

    /// <summary>The pending availability check (tests await it).</summary>
    internal Task PendingAvailabilityCheck { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The window stays open while the object is being created: closing it then would drop <see cref="CreatedDn"/>
    /// although the object gets created (the Cancel button is disabled for the same reason).
    /// </summary>
    public override Task<bool> CanCloseAsync() => Task.FromResult(!IsBusy);

    public override async Task InitializeAsync()
    {
        if (Kind == ObjectKind.User && Services.Sessions.Current is { } session)
        {
            UpnSuffixes = await UpnFieldViewModel.ReadUpnSuffixesAsync(session, CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>The DN the new object will get.</summary>
    public string? TargetDn
    {
        get
        {
            var name = ObjectName.Trim();
            if (name.Length == 0 || !DistinguishedName.TryParse(ParentDn, out var parent))
            {
                return null;
            }

            return parent.Child(Kind == ObjectKind.OrganizationalUnit ? "OU" : "CN", name).ToString();
        }
    }

    private string ObjectName => Kind is ObjectKind.User or ObjectKind.Contact ? FullName : Name;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        var session = Services.Sessions.Current;
        if (session is null)
        {
            Error = Localizer[L.Errors.NotConnected];
            return;
        }

        Error = Validate();
        if (Error is not null)
        {
            return;
        }

        string? password = null;
        if (Kind == ObjectKind.User)
        {
            password = PasswordInput?.GetPassword() ?? string.Empty;
            if (!string.Equals(password, ConfirmPasswordInput?.GetPassword() ?? string.Empty, StringComparison.Ordinal))
            {
                Error = Localizer[L.Batch.PasswordMismatch];
                return;
            }

            if (password.Length == 0 && !AccountDisabled)
            {
                Error = Localizer[L.NewObject.PasswordRequired];
                return;
            }
        }

        IsBusy = true;
        try
        {
            var dn = TargetDn!;
            if (await Task.Run(() => session.GetAsync(dn, [Attr.DistinguishedName], CancellationToken.None)).ConfigureAwait(true) is not null)
            {
                Error = Localizer.Format(L.NewObject.AlreadyExists, ObjectName.Trim());
                return;
            }

            if (Kind is ObjectKind.User or ObjectKind.Computer or ObjectKind.Group
                && await FindOwnerAsync(session, Attr.SamAccountName, SamValue(), CancellationToken.None).ConfigureAwait(true) is { } owner)
            {
                Error = Localizer.Format(L.NewObject.SamTaken, SamValue(), owner);
                return;
            }

            // The DC rejects duplicate UPNs only with a generic constraint violation; say which object uses it.
            var upn = Kind == ObjectKind.User && UpnPrefix.Trim().Length > 0 ? $"{UpnPrefix.Trim()}@{UpnSuffix}" : null;
            if (upn is not null
                && await FindOwnerAsync(session, Attr.UserPrincipalName, upn, CancellationToken.None).ConfigureAwait(true) is { } upnOwner)
            {
                Error = Localizer.Format(L.NewObject.SamTaken, upn, upnOwner);
                return;
            }

            var plan = BuildPlan(session, password);
            password = null;
            if (plan.Objects[0].HasErrors)
            {
                Error = string.Join(Environment.NewLine, plan.Objects[0].Changes.SelectMany(c => c.Issues).Select(i => Errors.TranslateIssue(i, session.Schema)));
                return;
            }

            var title = Localizer.Format(L.NewObject.ChangeTitle, Localizer.ObjectKindName(Kind), ObjectName.Trim());
            var result = await Services.Coordinator.ExecutePlanAsync(plan, title).ConfigureAwait(true);
            var objectResult = result.Objects[0];
            var created = objectResult.Changes.Any(c => c.Succeeded && c.Change.Action == ChangeAction.Create);
            if (!created)
            {
                var failure = objectResult.Changes.FirstOrDefault(c => !c.Succeeded);
                Error = failure is null ? Localizer[L.NewObject.Failed] : Errors.TranslateFailure(failure.ErrorKind, failure.ErrorMessage);
                return;
            }

            CreatedDn = objectResult.FinalDn;
            if (objectResult.Status != ObjectResultStatus.Succeeded)
            {
                // ADUC behaviour: the object exists (disabled); report why the password or the options were not set.
                var failure = objectResult.Changes.FirstOrDefault(c => !c.Succeeded);
                var reason = failure is null ? string.Empty : Errors.TranslateFailure(failure.ErrorKind, failure.ErrorMessage);
                await Services.Dialogs.ShowErrorAsync(WindowTitle, Localizer.Format(L.NewObject.PartiallyCreated, ObjectName.Trim(), reason))
                    .ConfigureAwait(true);
            }
            else
            {
                Services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.NewObject.Created, ObjectName.Trim()));
            }

            PasswordInput?.Clear();
            ConfirmPasswordInput?.Clear();
            RequestClose(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = Errors.Translate(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCreate() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => RequestClose(false);

    private bool CanCancel() => !IsBusy;

    /// <summary>Builds the creation plan (internal for tests). The password is only copied into the plan.</summary>
    internal ChangePlan BuildPlan(IDirectorySession session, string? password)
    {
        var dn = TargetDn ?? throw new InvalidOperationException("The name is missing.");
        var name = ObjectName.Trim();
        var attributes = new List<(string Attribute, string Value)>();
        string objectClass;
        switch (Kind)
        {
            case ObjectKind.User:
                objectClass = ObjectClasses.User;
                attributes.Add((Attr.GivenName, FirstName.Trim()));
                attributes.Add((Attr.Initials, Initials.Trim()));
                attributes.Add((Attr.Sn, LastName.Trim()));
                attributes.Add((Attr.DisplayName, name));
                attributes.Add((Attr.SamAccountName, SamValue()));
                attributes.Add((Attr.UserPrincipalName, UpnPrefix.Trim().Length == 0 ? string.Empty : $"{UpnPrefix.Trim()}@{UpnSuffix}"));
                attributes.Add((Attr.UserAccountControl, DisabledNormalAccount.ToString(CultureInfo.InvariantCulture)));
                break;
            case ObjectKind.Contact:
                objectClass = ObjectClasses.Contact;
                attributes.Add((Attr.GivenName, FirstName.Trim()));
                attributes.Add((Attr.Initials, Initials.Trim()));
                attributes.Add((Attr.Sn, LastName.Trim()));
                attributes.Add((Attr.DisplayName, DisplayName.Trim().Length > 0 ? DisplayName.Trim() : name));
                break;
            case ObjectKind.Group:
                objectClass = ObjectClasses.Group;
                attributes.Add((Attr.SamAccountName, SamValue()));
                attributes.Add((Attr.GroupType, GroupTypes.Compose(GroupScope, IsSecurityGroup).ToString(CultureInfo.InvariantCulture)));
                attributes.Add((Attr.Description, Description.Trim()));
                break;
            case ObjectKind.OrganizationalUnit:
                objectClass = ObjectClasses.OrganizationalUnit;
                attributes.Add((Attr.Description, Description.Trim()));
                break;
            default:
                objectClass = ObjectClasses.Computer;
                attributes.Add((Attr.SamAccountName, SamValue()));
                attributes.Add((Attr.UserAccountControl, ((int)UserAccountControl.WorkstationTrustAccount).ToString(CultureInfo.InvariantCulture)));
                attributes.Add((Attr.Description, Description.Trim()));
                break;
        }

        var createAttributes = new List<AttributeChange> { new(Attr.ObjectClass, AttributeChangeKind.Replace, [], [objectClass]) };
        createAttributes.AddRange(attributes
            .Where(a => a.Value.Length > 0 && (session.Schema.Attributes.Count == 0 || session.Schema.FindAttribute(a.Attribute) is not null))
            .Select(a => new AttributeChange(a.Attribute, AttributeChangeKind.Replace, [], [a.Value])));

        var issues = ValidateMandatory(session, objectClass, dn, createAttributes);
        var changes = new List<PlannedChange>
        {
            new() { Action = ChangeAction.Create, Attributes = createAttributes, Issues = issues },
        };
        if (Kind == ObjectKind.User)
        {
            changes.AddRange(UserSteps(password));
        }

        return new ChangePlan
        {
            Objects =
            [
                new ObjectPlan
                {
                    DistinguishedName = dn,
                    DisplayName = name,
                    Kind = Kind,
                    ObjectClasses = AttributeOption.ClassesOf(Kind),
                    Changes = changes,
                },
            ],
            Title = Localizer.Format(L.NewObject.ChangeTitle, Localizer.ObjectKindName(Kind), name),
            CreatedAt = Services.Time.GetUtcNow(),
            Server = session.Info.Server,
        };
    }

    partial void OnFirstNameChanged(string value) => UpdateDerivedNames();

    partial void OnInitialsChanged(string value) => UpdateDerivedNames();

    partial void OnLastNameChanged(string value) => UpdateDerivedNames();

    partial void OnFullNameChanged(string value)
    {
        if (!_updating)
        {
            _fullNameEdited = true;
        }

        if (!_displayNameEdited)
        {
            SetQuietly(() => DisplayName = value);
        }
    }

    partial void OnDisplayNameChanged(string value)
    {
        if (!_updating)
        {
            _displayNameEdited = true;
        }
    }

    partial void OnSamAccountNameChanged(string value)
    {
        if (!_updating)
        {
            _samEdited = true;
        }

        if (Kind == ObjectKind.User && (UpnPrefix.Length == 0 || !_upnEdited))
        {
            SetQuietly(() => UpnPrefix = value);
        }

        ScheduleAvailabilityCheck();
    }

    partial void OnUpnPrefixChanged(string value)
    {
        if (!_updating)
        {
            _upnEdited = true;
        }
    }

    partial void OnSamTemplateChanged(string value)
    {
        _samEdited = false;
        UpdateDerivedNames();
    }

    partial void OnNameChanged(string value)
    {
        if (_samEdited)
        {
            return;
        }

        var sam = Kind switch
        {
            ObjectKind.Group => value.Trim(),
            ObjectKind.Computer => ComputerSam(value),
            _ => SamAccountName,
        };
        SetQuietly(() => SamAccountName = sam);
    }

    partial void OnMustChangePasswordChanged(bool value) => OnPropertyChanged(nameof(PasswordOptionsWarning));

    partial void OnPasswordNeverExpiresChanged(bool value) => OnPropertyChanged(nameof(PasswordOptionsWarning));

    private static string ComputerSam(string name)
    {
        var trimmed = name.Trim().ToUpperInvariant();
        return trimmed.Length == 0 ? string.Empty : trimmed[..Math.Min(trimmed.Length, AccountNames.MaxComputerNameLength)] + "$";
    }

    private string SamValue() => SamAccountName.Trim();

    private void UpdateDerivedNames()
    {
        if (Kind is not (ObjectKind.User or ObjectKind.Contact))
        {
            return;
        }

        if (!_fullNameEdited)
        {
            var parts = new[] { FirstName.Trim(), Initials.Trim().Length > 0 ? Initials.Trim() + "." : string.Empty, LastName.Trim() };
            SetQuietly(() => FullName = string.Join(' ', parts.Where(p => p.Length > 0)));
        }

        if (Kind == ObjectKind.User && !_samEdited)
        {
            string sam;
            try
            {
                sam = _templates.Render(SamTemplate, new TemplateContext
                {
                    Attribute = a => a.ToUpperInvariant() switch
                    {
                        "GIVENNAME" => FirstName.Trim().Length == 0 ? [] : [FirstName.Trim()],
                        "SN" => LastName.Trim().Length == 0 ? [] : [LastName.Trim()],
                        "INITIALS" => Initials.Trim().Length == 0 ? [] : [Initials.Trim()],
                        _ => [],
                    },
                    Domain = Services.Sessions.Current?.Info.DomainDnsName,
                    Now = Services.Time.GetLocalNow(),
                });
            }
            catch (FormatException)
            {
                sam = string.Empty;
            }

            sam = sam.Trim('.', ' ');
            if (sam.Length > AccountNames.MaxSamAccountNameLength)
            {
                sam = sam[..AccountNames.MaxSamAccountNameLength].TrimEnd('.', ' ');
            }

            // OnSamAccountNameChanged keeps the UPN prefix in sync and schedules the availability check.
            SetQuietly(() => SamAccountName = sam);
        }
    }

    private void SetQuietly(Action set)
    {
        var previous = _updating;
        _updating = true;
        try
        {
            set();
        }
        finally
        {
            _updating = previous;
        }
    }

    private void ScheduleAvailabilityCheck()
    {
        if (Kind is not (ObjectKind.User or ObjectKind.Group or ObjectKind.Computer))
        {
            return;
        }

        var sam = SamValue();
        var maxLength = Kind == ObjectKind.Computer ? AccountNames.MaxComputerNameLength + 1 : AccountNames.MaxSamAccountNameLength;
        if (sam.Length == 0)
        {
            _samCheck.Cancel();
            SamAvailability = NameAvailability.Unknown;
            SamAvailabilityText = string.Empty;
            return;
        }

        if (!AccountNames.IsValidSamAccountName(sam, Kind == ObjectKind.Group ? 256 : maxLength))
        {
            _samCheck.Cancel();
            SamAvailability = NameAvailability.Invalid;
            SamAvailabilityText = Localizer.Format(L.NewObject.SamInvalid, maxLength);
            return;
        }

        SamAvailability = NameAvailability.Checking;
        SamAvailabilityText = Localizer[L.NewObject.SamChecking];
        PendingAvailabilityCheck = _samCheck.RunAsync(async token =>
        {
            var session = Services.Sessions.Current;
            if (session is null)
            {
                return;
            }

            try
            {
                var owner = await FindOwnerAsync(session, Attr.SamAccountName, sam, token).ConfigureAwait(true);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                SamAvailability = owner is null ? NameAvailability.Available : NameAvailability.Taken;
                SamAvailabilityText = owner is null ? Localizer[L.NewObject.SamAvailable] : Localizer.Format(L.NewObject.SamTaken, sam, owner);
            }
            catch (DirectoryException)
            {
                SamAvailability = NameAvailability.Unknown;
                SamAvailabilityText = string.Empty;
            }
        });
    }

    /// <summary>Name of the object that already uses the value of a unique attribute, or null.</summary>
    private static async Task<string?> FindOwnerAsync(IDirectorySession session, string attribute, string value, CancellationToken cancellationToken)
    {
        var search = new DirectorySearch
        {
            BaseDn = session.Info.DefaultNamingContext,
            Scope = SearchScope.Subtree,
            Filter = LdapFilter.Equal(attribute, value),
            Attributes = [Attr.Name],
            SizeLimit = 1,
        };
        return await Task.Run(
            async () =>
            {
                await foreach (var found in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
                {
                    return found.Name;
                }

                return null;
            },
            cancellationToken).ConfigureAwait(true);
    }

    private string? Validate()
    {
        var name = ObjectName.Trim();
        if (name.Length == 0)
        {
            return Localizer[L.NewObject.NameRequired];
        }

        if (name.Length > MaxNameLength)
        {
            return Localizer.Format(L.Fields.TooLong, MaxNameLength);
        }

        switch (Kind)
        {
            case ObjectKind.User:
                if (!AccountNames.IsValidSamAccountName(SamValue()))
                {
                    return Localizer.Format(L.NewObject.SamInvalid, AccountNames.MaxSamAccountNameLength);
                }

                if (UpnPrefix.Trim().Length > 0 && (!AccountNames.IsValidUpnPrefix(UpnPrefix.Trim()) || string.IsNullOrWhiteSpace(UpnSuffix)))
                {
                    return Localizer[L.Fields.InvalidUpn];
                }

                break;
            case ObjectKind.Group:
                if (!AccountNames.IsValidSamAccountName(SamValue(), 256))
                {
                    return Localizer.Format(L.NewObject.SamInvalid, 256);
                }

                break;
            case ObjectKind.Computer:
                if (Name.Trim().Any(c => char.IsWhiteSpace(c) || AccountNames.IsInvalidSamCharacter(c) || c == '.'))
                {
                    return Localizer[L.Fields.InvalidComputerName];
                }

                if (!AccountNames.IsValidSamAccountName(SamValue(), AccountNames.MaxComputerNameLength + 1) || !SamValue().EndsWith('$'))
                {
                    return Localizer.Format(L.NewObject.SamInvalid, AccountNames.MaxComputerNameLength + 1);
                }

                break;
        }

        return null;
    }

    private IEnumerable<PlannedChange> UserSteps(string? password)
    {
        if (!string.IsNullOrEmpty(password))
        {
            yield return new PlannedChange { Action = ChangeAction.ResetPassword, Password = password };
        }

        var uac = NormalAccount;
        if (AccountDisabled)
        {
            uac |= (int)UserAccountControl.AccountDisable;
        }

        if (PasswordNeverExpires)
        {
            uac |= (int)UserAccountControl.DontExpirePassword;
        }

        var attributes = new List<AttributeChange>();
        if (uac != DisabledNormalAccount)
        {
            attributes.Add(new AttributeChange(Attr.UserAccountControl, AttributeChangeKind.Replace,
                [DisabledNormalAccount.ToString(CultureInfo.InvariantCulture)], [uac.ToString(CultureInfo.InvariantCulture)]));
        }

        if (MustChangePassword && !PasswordNeverExpires)
        {
            // Setting the password set pwdLastSet to now; 0 forces the change at the first logon.
            attributes.Add(new AttributeChange(Attr.PwdLastSet, AttributeChangeKind.Replace, [], ["0"]));
        }

        if (attributes.Count > 0)
        {
            yield return new PlannedChange { Action = ChangeAction.Modify, Attributes = attributes };
        }
    }

    private List<ValidationIssue> ValidateMandatory(IDirectorySession session, string objectClass, string dn, IReadOnlyList<AttributeChange> attributes)
    {
        var issues = new List<ValidationIssue>();
        if (session.Schema.Attributes.Count == 0)
        {
            return issues;
        }

        // Filled in by the domain controller (or by the RDN of the DN).
        var provided = new HashSet<string>(attributes.Select(a => a.Attribute), StringComparer.OrdinalIgnoreCase)
        {
            Attr.Cn, Attr.Ou, Attr.Name, Attr.ObjectSid, Attr.ObjectCategory, Attr.NtSecurityDescriptor, "instanceType",
        };
        if (DistinguishedName.TryParse(dn, out var parsed) && !parsed.IsRoot)
        {
            provided.Add(parsed.Leaf.Type);
        }

        foreach (var mandatory in session.Schema.GetMandatoryAttributes(AttributeOption.ClassesOf(Kind).Append(objectClass)))
        {
            if (!provided.Contains(mandatory))
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, IssueCodes.MandatoryAttribute, $"'{mandatory}' is required.", mandatory));
            }
        }

        return issues;
    }
}
