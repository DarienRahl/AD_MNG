using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Batch.Planning;
using AdMng.Core.Computers;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Dialogs;

/// <summary>
/// Prestaging a computer account (K1 design note 4.6): the user or group that may join the computer (the four ACEs ADUC
/// writes, added by a <see cref="ChangeAction.ModifySecurity"/> step after the create step of the same plan),
/// <c>managedBy</c> and <c>location</c>, and the summary of the KB5020276 join hardening. The ADUC option "pre-Windows
/// 2000 computer" (a password derived from the name) is deliberately not offered.
/// </summary>
public sealed partial class NewObjectViewModel
{
    private string? _joinerSid;
    private string? _joinerName;
    private string? _managedByName;
    private bool? _ownerIsAdministrator;

    /// <summary>The user or group that may join the computer; null = only administrators (the default).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasJoiner), nameof(JoinerText), nameof(JoinSummary))]
    public partial string? JoinerDn { get; private set; }

    public bool HasJoiner => JoinerDn is not null;

    /// <summary>"CONTOSO\helpdesk" or "brak – tylko administratorzy".</summary>
    public string JoinerText => _joinerName ?? Localizer[L.Computers.PrestageJoinerNone];

    /// <summary>managedBy of the new computer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasManagedBy), nameof(ManagedByText))]
    public partial string? ManagedByDn { get; private set; }

    public bool HasManagedBy => ManagedByDn is not null;

    public string ManagedByText => _managedByName ?? Localizer[L.Computers.PrestageManagedByNone];

    /// <summary>location of the new computer.</summary>
    [ObservableProperty]
    public partial string Location { get; set; } = string.Empty;

    /// <summary>Why the "pre-Windows 2000 computer" option of ADUC is missing.</summary>
    public string PreWindows2000Note => Localizer[L.Computers.PrestageNoPreWindows2000];

    /// <summary>
    /// The KB5020276 rules for a join that reuses this account, the owner (the window's account) and – when known –
    /// whether the chosen joiner will succeed without the reuse policy.
    /// </summary>
    public string JoinSummary
    {
        get
        {
            var owner = Session?.Info.BoundIdentity ?? string.Empty;
            var text = Localizer.Format(L.Computers.PrestageHardening, owner);
            if (JoinerDn is null)
            {
                return text + " " + Localizer[L.Computers.PrestageOnlyAdmins];
            }

            if (Session?.Info.BoundUserDn is { } ownerDn && string.Equals(DnKey.For(ownerDn), DnKey.For(JoinerDn), StringComparison.Ordinal))
            {
                // The joiner creates the account: the join is allowed.
                return text;
            }

            return _ownerIsAdministrator switch
            {
                true => text + " " + Localizer.Format(L.Computers.PrestageOwnerIsAdmin, owner, JoinerText),
                false => text + " " + Localizer.Format(L.Computers.PrestageJoinFails, owner, JoinerText),
                _ => text,
            };
        }
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task ChangeJoinerAsync()
    {
        if (Session is not { } session)
        {
            return;
        }

        var picked = await Services.Dialogs.PickObjectsAsync(session, [ObjectKind.User, ObjectKind.Group], multiSelect: false).ConfigureAwait(true);
        if (picked.Count == 0)
        {
            return;
        }

        try
        {
            var principal = await Task.Run(() => session.GetAsync(picked[0], [Attr.ObjectSid, Attr.SamAccountName, Attr.Name], CancellationToken.None)).ConfigureAwait(true);
            if (principal?.ObjectSid is not { } sid)
            {
                Error = Localizer[L.Computers.PrestageJoinerNotFound];
                return;
            }

            _joinerSid = sid;
            _joinerName = principal.GetString(Attr.SamAccountName) is { Length: > 0 } login
                ? $"{session.Info.DomainNetBiosName ?? session.Info.DomainDnsName}\\{login}"
                : principal.Name;
            JoinerDn = principal.DistinguishedName;
            Error = null;
        }
        catch (Exception ex) when (ex is DirectoryException or InvalidOperationException or ObjectDisposedException)
        {
            Error = Errors.Translate(ex);
        }
    }

    [RelayCommand]
    private void ClearJoiner()
    {
        _joinerSid = null;
        _joinerName = null;
        JoinerDn = null;
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task ChangeManagedByAsync()
    {
        if (Session is not { } session)
        {
            return;
        }

        var picked = await Services.Dialogs.PickObjectsAsync(session, [ObjectKind.User, ObjectKind.Group, ObjectKind.Contact], multiSelect: false).ConfigureAwait(true);
        if (picked.Count == 0)
        {
            return;
        }

        _managedByName = Core.Ldap.DistinguishedName.TryParse(picked[0], out var parsed) ? parsed.Leaf.Value : picked[0];
        ManagedByDn = picked[0];
    }

    [RelayCommand]
    private void ClearManagedBy()
    {
        _managedByName = null;
        ManagedByDn = null;
    }

    /// <summary>
    /// Whether the window's account is an owner KB5020276 trusts – Domain Admins, Enterprise Admins or
    /// BUILTIN\Administrators, not Schema Admins (best effort; unknown when it cannot be read).
    /// </summary>
    private async Task InitializeComputerAsync(IDirectorySession session)
    {
        if (session.Info.BoundUserDn is not { Length: > 0 } ownerDn)
        {
            return;
        }

        try
        {
            _ownerIsAdministrator = await Task.Run(() => PrivilegeCheck.IsJoinOwnerAsync(session, ownerDn, CancellationToken.None)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is DirectoryException or InvalidOperationException or ObjectDisposedException)
        {
            _ownerIsAdministrator = null;
        }

        OnPropertyChanged(nameof(JoinSummary));
    }

    /// <summary>managedBy and location of a computer (empty values are left out of the create step).</summary>
    private IEnumerable<(string Attribute, string Value)> ComputerAttributes() =>
    [
        (Attr.ManagedBy, ManagedByDn ?? string.Empty),
        (Attr.Location, Location.Trim()),
    ];

    /// <summary>The join right as a security step after the create step (null without a joiner).</summary>
    private PlannedChange? JoinRightsChange() =>
        Kind == ObjectKind.Computer && _joinerSid is { } sid ? AclPlanning.ForNewObject(ComputerJoinRights.Grant(sid, _joinerName)) : null;

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(JoinerText));
        OnPropertyChanged(nameof(ManagedByText));
        OnPropertyChanged(nameof(JoinSummary));
        OnPropertyChanged(nameof(PreWindows2000Note));
    }
}
