using System.Text.Json;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Ldap;
using AdMng.Core.Security;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.BulkCreate;

/// <summary>
/// Step 3: the naming profiles (docs/dev/phase3-bulk-create.md 2.6) – applying one to the rules and options, <em>(zmieniony)</em>,
/// switching with a question, <em>Zapisz</em> (on a built-in: <em>Zapisz jako…</em> with <em>„… (kopia)”</em>), <em>Usuń</em> and
/// <em>Domyślny dla tego kontenera</em>. A profile holds patterns, options, header names and portable DNs only (rule 1).
/// </summary>
public sealed partial class BulkRulesViewModel
{
    private string? _profileId;
    private string? _baseline;
    private bool _upnSuffixPicked;
    private bool _mailDomainPicked;
    private bool _syncingNames;
    private bool _revertingProfile;

    /// <summary>The selected profile is a built-in (<em>Zapisz</em> becomes <em>Zapisz jako…</em>, <em>Usuń</em> is off).</summary>
    public bool IsBuiltInProfile => BulkCreateProfileStore.IsBuiltIn(Profile?.Value);

    /// <summary><em>(zmieniony)</em> next to the profile combo.</summary>
    public string? ModifiedText => IsModified ? Services.Localizer[L.BulkCreate.ProfileModified] : null;

    /// <summary>The selected saved profile is the default of the target container.</summary>
    [ObservableProperty]
    public partial bool IsDefaultForContainer { get; private set; }

    /// <summary>The inline <em>Zapisz jako…</em> panel is open.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmSaveProfileAsCommand))]
    public partial bool IsNamingProfile { get; private set; }

    /// <summary>The name of the new profile (<em>Konta numerowane (kopia)</em>).</summary>
    [ObservableProperty]
    public partial string NewProfileName { get; set; } = string.Empty;

    /// <summary><em>Domyślny dla tego kontenera</em> in the <em>Zapisz jako…</em> panel.</summary>
    [ObservableProperty]
    public partial bool SaveAsDefaultForContainer { get; set; }

    /// <summary>The headers the profile will remember (<em>Zapamiętane kolumny: Telefon komórkowy, Imię …</em>).</summary>
    [ObservableProperty]
    public partial string? SaveHeadersText { get; private set; }

    /// <summary>The result of the last profile command (<em>Zapisano profil „…”</em>) or its problem.</summary>
    [ObservableProperty]
    public partial string? ProfileStatus { get; private set; }

    /// <summary>The profile's groups were picked in another domain and were dropped.</summary>
    [ObservableProperty]
    public partial string? GroupsWarning { get; private set; }

    /// <summary>The target container can have a default profile (connected, chosen).</summary>
    public bool CanSetDefault => ContainerDn() is not null && Session()?.Info.DefaultNamingContext is not null;

    /// <summary><em>Zapisz</em>: saves the selected saved profile; on a built-in opens <em>Zapisz jako…</em>.</summary>
    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        if (IsBuiltInProfile || _store.Find(_profileId) is not { } existing)
        {
            BeginSaveAs();
            return;
        }

        var profile = Capture();
        profile.Id = existing.Id;
        profile.Name = existing.Name;
        profile.DefaultForContainers = existing.DefaultForContainers;
        if (!await _store.SaveAsync(profile).ConfigureAwait(true))
        {
            ProfileStatus = Services.Localizer.Format(L.BulkCreate.TooManyProfiles, BulkCreateSettings.MaxProfiles);
            return;
        }

        _baseline = Signature(profile);
        ProfileStatus = SavedText(profile.Name);
        Revalidate(raise: false);
    }

    /// <summary><em>Zapisz jako…</em>: opens the naming panel with <em>„… (kopia)”</em>.</summary>
    [RelayCommand]
    private Task SaveProfileAsAsync()
    {
        BeginSaveAs();
        return Task.CompletedTask;
    }

    /// <summary>Saves the current rules and options as a new profile (optionally the container's default) and selects it.</summary>
    [RelayCommand(CanExecute = nameof(IsNamingProfile))]
    private async Task ConfirmSaveProfileAsAsync()
    {
        var localizer = Services.Localizer;
        var name = (NewProfileName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            ProfileStatus = localizer[L.BulkCreate.ProfileNameRequired];
            return;
        }

        if (name.Length > BulkCreateSettings.MaxNameLength)
        {
            name = name[..BulkCreateSettings.MaxNameLength];
        }

        if (_store.ProfilesFor(Kind).Any(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            ProfileStatus = localizer.Format(L.BulkCreate.ProfileNameTaken, name);
            return;
        }

        var profile = Capture();
        profile.Id = Guid.NewGuid().ToString("N");
        profile.Name = name;
        profile.DefaultForContainers = [];
        if (!await _store.SaveAsync(profile).ConfigureAwait(true))
        {
            ProfileStatus = localizer.Format(L.BulkCreate.TooManyProfiles, BulkCreateSettings.MaxProfiles);
            return;
        }

        if (SaveAsDefaultForContainer && ContainerDn() is { } container && Session()?.Info.DefaultNamingContext is { } namingContext)
        {
            await _store.SetDefaultForContainerAsync(profile.Id, container, namingContext, isDefault: true).ConfigureAwait(true);
        }

        if (_disposed)
        {
            return;
        }

        _profileId = profile.Id;
        RefreshProfileList();
        _baseline = Signature(profile);
        IsNamingProfile = false;
        ProfileStatus = SavedText(name);
        RefreshProfileState();
        Revalidate(raise: false);
    }

    [RelayCommand]
    private void CancelSaveProfileAs()
    {
        IsNamingProfile = false;
        ProfileStatus = null;
    }

    /// <summary><em>Usuń</em>: deletes the selected saved profile (asks) and returns to <em>Standardowy</em>.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteProfile))]
    private async Task DeleteProfileAsync()
    {
        if (IsBuiltInProfile || _profileId is not { } id || Profile is not { } selected)
        {
            return;
        }

        var localizer = Services.Localizer;
        if (!await Services.Dialogs.ConfirmAsync(localizer[L.BulkCreate.DeleteProfileTitle], localizer.Format(L.BulkCreate.ConfirmDeleteProfile, selected.Text), destructive: true).ConfigureAwait(true)
            || _disposed)
        {
            return;
        }

        await _store.DeleteAsync(id).ConfigureAwait(true);
        if (_disposed)
        {
            return;
        }

        ApplyProfile(_store.BuiltIns(Kind)[0], warn: false);
        ProfileStatus = localizer.Format(L.BulkCreate.ProfileDeleted, selected.Text);
    }

    private bool CanDeleteProfile() => !IsBuiltInProfile && Profile is not null;

    /// <summary><em>Domyślny dla tego kontenera</em>: makes the selected saved profile the container's default, or removes it as the default.</summary>
    [RelayCommand]
    private async Task SetDefaultForContainerAsync()
    {
        var localizer = Services.Localizer;
        if (IsBuiltInProfile || _profileId is not { } id)
        {
            ProfileStatus = localizer[L.BulkCreate.DefaultNeedsSavedProfile];
            BeginSaveAs();
            SaveAsDefaultForContainer = true;
            return;
        }

        if (ContainerDn() is not { } container || Session()?.Info.DefaultNamingContext is not { } namingContext)
        {
            return;
        }

        var makeDefault = !IsDefaultForContainer;
        await _store.SetDefaultForContainerAsync(id, container, namingContext, makeDefault).ConfigureAwait(true);
        if (_disposed)
        {
            return;
        }

        RefreshProfileState();
        ProfileStatus = localizer.Format(makeDefault ? L.BulkCreate.ProfileIsDefault : L.BulkCreate.ProfileNotDefault, Profile?.Text ?? string.Empty);
    }

    private void BeginSaveAs()
    {
        var localizer = Services.Localizer;
        NewProfileName = localizer.Format(L.BulkCreate.ProfileCopyName, Profile?.Text ?? localizer[L.BulkCreate.ProfileStandard]);
        SaveAsDefaultForContainer = false;
        var headers = Columns.StorableHeaderNames();
        SaveHeadersText = headers.Count == 0
            ? localizer[L.BulkCreate.SaveNoHeaders]
            : localizer.Format(L.BulkCreate.SaveHeaders, string.Join(", ", headers));
        IsNamingProfile = true;
    }

    private string SavedText(string name)
    {
        var headers = Columns.StorableHeaderNames();
        return headers.Count == 0
            ? Services.Localizer.Format(L.BulkCreate.ProfileSaved, name)
            : Services.Localizer.Format(L.BulkCreate.ProfileSavedWithHeaders, name, string.Join(", ", headers));
    }

    // ---- Selection ----------------------------------------------------------------------------------------------

    partial void OnProfileChanged(Option<string>? oldValue, Option<string>? newValue)
    {
        OnPropertyChanged(nameof(IsBuiltInProfile));
        if (IsApplying || _revertingProfile || _disposed || newValue is null || string.Equals(oldValue?.Value, newValue.Value, StringComparison.Ordinal))
        {
            return;
        }

        SwitchProfileAsync(oldValue, newValue).Forget();
    }

    /// <summary>The operator picked another profile: asks when the current one was changed, then applies and remembers it.</summary>
    private async Task SwitchProfileAsync(Option<string>? oldValue, Option<string> newValue)
    {
        var localizer = Services.Localizer;
        if (IsModified
            && !await Services.Dialogs.ConfirmAsync(localizer[L.BulkCreate.ProfileTitle], localizer.Format(L.BulkCreate.ConfirmDiscardProfileChanges, oldValue?.Text ?? string.Empty), destructive: true).ConfigureAwait(true))
        {
            _revertingProfile = true;
            try
            {
                Profile = oldValue;
            }
            finally
            {
                _revertingProfile = false;
            }

            return;
        }

        if (_disposed || _store.Find(newValue.Value) is not { } profile)
        {
            return;
        }

        ApplyProfile(profile, warn: true);
        await _store.RememberLastAsync(Kind, profile.Id).ConfigureAwait(true);
        PreviewWork = RefreshPreviewAsync();
        await PreviewWork.ConfigureAwait(true);
    }

    /// <summary>The built-ins and the saved profiles of the kind; the selection is kept by id.</summary>
    private void RefreshProfileList()
    {
        _applying++;
        try
        {
            Profiles.Clear();
            foreach (var profile in _store.ProfilesFor(Kind))
            {
                Profiles.Add(new Option<string>(profile.Id, profile.Name));
            }

            Profile = Profiles.FirstOrDefault(p => string.Equals(p.Value, _profileId, StringComparison.Ordinal));
        }
        finally
        {
            _applying--;
        }

        OnPropertyChanged(nameof(IsBuiltInProfile));
        DeleteProfileCommand.NotifyCanExecuteChanged();
    }

    private void RefreshProfileState()
    {
        var namingContext = Session()?.Info.DefaultNamingContext;
        IsDefaultForContainer = !IsBuiltInProfile && _profileId is not null
            && _store.DefaultFor(Kind, ContainerDn(), namingContext)?.Id == _profileId;
        OnPropertyChanged(nameof(CanSetDefault));
        DeleteProfileCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Gives the rules and options the values of <paramref name="profile"/> (its baseline for <em>(zmieniony)</em>).</summary>
    private void ApplyProfile(BulkCreateProfile profile, bool warn)
    {
        var localizer = Services.Localizer;
        _applying++;
        try
        {
            _profileId = profile.Id;
            ActiveRule = null;
            Rules.Clear();
            var settings = (profile.Rules ?? []).Where(r => r is not null && r.Attribute.Trim().Length > 0).ToList();
            foreach (var attribute in CoreAttributes(Kind))
            {
                var setting = settings.FirstOrDefault(r => string.Equals(r.Attribute.Trim(), attribute, StringComparison.OrdinalIgnoreCase));
                Rules.Add(new AttributeRuleViewModel(attribute, RuleLabel(attribute))
                {
                    Owner = this,
                    IsCore = true,
                    Pattern = setting?.Pattern ?? string.Empty,
                    OverridesColumn = setting?.OverridesColumn ?? false,
                });
            }

            foreach (var setting in settings.Where(r => !CoreAttributes(Kind).Contains(r.Attribute.Trim(), StringComparer.OrdinalIgnoreCase)))
            {
                Rules.Add(new AttributeRuleViewModel(setting.Attribute.Trim(), RuleLabel(setting.Attribute.Trim()))
                {
                    Owner = this,
                    Pattern = setting.Pattern,
                    OverridesColumn = setting.OverridesColumn,
                });
            }

            ApplySuffixes(profile, warn);
            CounterMode = profile.CounterMode;
            AvoidRecycleBinNumbers = !profile.ReuseRecycleBinNumbers;
            Collisions = profile.Collisions;
            SanitizeIdentifiers = profile.SanitizeIdentifiers;
            TruncateNames = profile.TruncateNames;
            AddPrimarySmtp = profile.AddPrimarySmtp;
            Phones = profile.Phones;
            PasswordMode = profile.InitialMode;
            GeneratedLength = Math.Clamp(profile.InitialLength, PasswordGenerator.MinimumLength, PasswordGenerator.MaximumLength);
            MustChangeAtLogon = profile.MustChangeAtLogon;
            Enabled = profile.CreateEnabled && profile.InitialMode != InitialPasswordMode.None;
            HasExpiry = profile.ExpiresAfterDays is not null;
            ExpiryDate = profile.ExpiresAfterDays is { } days ? Today().AddDays(days) : null;
            MatchExisting = profile.MatchExisting;
            GroupScope = profile.GroupScope;
            IsSecurityGroup = profile.SecurityGroup;
            ApplyGroups(profile);
            Columns.HeaderMappings = [.. profile.HeaderMappings ?? []];
            if (!Profiles.Any(p => string.Equals(p.Value, profile.Id, StringComparison.Ordinal)))
            {
                RefreshProfileList();
            }

            Profile = Profiles.FirstOrDefault(p => string.Equals(p.Value, profile.Id, StringComparison.Ordinal));
            IsNamingProfile = false;
            OptionsError = null;
            PasswordError = null;
        }
        finally
        {
            _applying--;
        }

        OnPropertyChanged(nameof(IsBuiltInProfile));
        Recompile();
        RefreshFields();
        _baseline = Signature(Capture());
        RefreshProfileState();
        Revalidate(raise: true);
        RefreshSamples();
    }

    /// <summary>
    /// The UPN suffix (2.6): the profile's when it is still in the forest's list (else a warning and the window's domain), else
    /// the window's domain as ADUC; the mail domain is the profile's, else it follows the UPN suffix.
    /// </summary>
    private void ApplySuffixes(BulkCreateProfile profile, bool warn)
    {
        var domain = Session()?.Info.DomainDnsName;
        var fallback = UpnSuffixes.FirstOrDefault(s => string.Equals(s, domain, StringComparison.OrdinalIgnoreCase)) ?? UpnSuffixes.FirstOrDefault() ?? domain;
        UpnSuffixWarning = null;
        _syncingNames = true;
        try
        {
            if (profile.UpnSuffix is { Length: > 0 } wanted)
            {
                var found = UpnSuffixes.FirstOrDefault(s => string.Equals(s, wanted, StringComparison.OrdinalIgnoreCase));
                UpnSuffix = found ?? fallback;
                _upnSuffixPicked = found is not null;
                if (found is null && warn)
                {
                    UpnSuffixWarning = Services.Localizer.Format(L.BulkCreate.UpnSuffixMissing, wanted, fallback ?? string.Empty);
                }
            }
            else
            {
                UpnSuffix = fallback;
                _upnSuffixPicked = false;
            }

            _mailDomainPicked = profile.MailDomain is { Length: > 0 };
            MailDomain = _mailDomainPicked ? profile.MailDomain : UpnSuffix;
        }
        finally
        {
            _syncingNames = false;
        }
    }

    /// <summary>The profile's groups (portable DNs) in this window's domain; groups picked in another domain are dropped with a warning.</summary>
    private void ApplyGroups(BulkCreateProfile profile)
    {
        Groups.Clear();
        GroupsWarning = null;
        var dns = profile.GroupDns ?? [];
        if (dns.Count == 0)
        {
            return;
        }

        var session = Session();
        if (session?.Info.DefaultNamingContext is not { } namingContext)
        {
            return;
        }

        if (profile.GroupDomain is { Length: > 0 } groupDomain && !string.Equals(groupDomain, session.Info.DomainDnsName, StringComparison.OrdinalIgnoreCase))
        {
            GroupsWarning = Services.Localizer.Format(L.BulkCreate.GroupsFromOtherDomain, dns.Count, groupDomain);
            return;
        }

        var resolved = new List<string>();
        foreach (var portable in dns)
        {
            var dn = DistinguishedName.FromPortable(portable, namingContext);
            if (!Groups.Any(g => DistinguishedName.AreEqual(g.Dn, dn)))
            {
                Groups.Add(DirectoryItem.Unresolved(dn, Services.Localizer));
                resolved.Add(dn);
            }
        }

        ResolveGroupsAsync(session, resolved).Forget();
    }

    partial void OnUpnSuffixChanged(string? oldValue, string? newValue)
    {
        if (_syncingNames || IsApplying)
        {
            return;
        }

        _upnSuffixPicked = true;
        UpnSuffixWarning = null;
        if (!_mailDomainPicked)
        {
            _syncingNames = true;
            try
            {
                MailDomain = newValue;
            }
            finally
            {
                _syncingNames = false;
            }
        }

        OnRulesEdited();
    }

    partial void OnMailDomainChanged(string? oldValue, string? newValue)
    {
        if (_syncingNames || IsApplying)
        {
            return;
        }

        _mailDomainPicked = !string.Equals(newValue?.Trim(), UpnSuffix?.Trim(), StringComparison.OrdinalIgnoreCase);
        OnRulesEdited();
    }

    partial void OnIsModifiedChanged(bool value) => OnPropertyChanged(nameof(ModifiedText));

    /// <summary>The current rules and options as a profile (patterns, options, header names, portable DNs; never a row value).</summary>
    internal BulkCreateProfile Capture()
    {
        var session = Session();
        var namingContext = session?.Info.DefaultNamingContext;
        return new BulkCreateProfile
        {
            Id = _profileId ?? Guid.NewGuid().ToString("N"),
            Name = Profile?.Text ?? string.Empty,
            Kind = Kind,
            Rules = [.. Rules
                .Where(r => r.Attribute.Trim().Length > 0 && r.Pattern.Trim().Length > 0)
                .Select(r => new BulkCreateRuleSetting(r.Attribute.Trim(), r.Pattern, r.OverridesColumn))],
            UpnSuffix = _upnSuffixPicked && !string.IsNullOrWhiteSpace(UpnSuffix) ? UpnSuffix.Trim() : null,
            MailDomain = _mailDomainPicked && !string.IsNullOrWhiteSpace(MailDomain) ? MailDomain.Trim() : null,
            CounterMode = CounterMode,
            ReuseRecycleBinNumbers = !AvoidRecycleBinNumbers,
            Collisions = Collisions,
            SanitizeIdentifiers = SanitizeIdentifiers,
            TruncateNames = TruncateNames,
            AddPrimarySmtp = AddPrimarySmtp,
            Phones = Phones,
            InitialMode = PasswordMode,
            InitialLength = GeneratedLength,
            MustChangeAtLogon = MustChangeAtLogon,
            CreateEnabled = Enabled,
            ExpiresAfterDays = HasExpiry && ExpiryDate is { } date ? Math.Max(0, (date.Date - Today()).Days) : null,
            MatchExisting = MatchExisting,
            GroupDns = namingContext is null ? [] : [.. Groups.Select(g => DistinguishedName.ToPortable(g.Dn, namingContext))],
            GroupDomain = Groups.Count > 0 ? session?.Info.DomainDnsName : null,
            GroupScope = GroupScope,
            SecurityGroup = IsSecurityGroup,
            HeaderMappings = [.. Columns.ToHeaderMappings()],
        };
    }

    private DateTime Today() => TimeZoneInfo.ConvertTime(Services.Time.GetUtcNow(), Services.Time.LocalTimeZone).Date;

    /// <summary>What <em>(zmieniony)</em> compares: everything but the id, the name, the container defaults and the remembered headers.</summary>
    private static string Signature(BulkCreateProfile profile)
    {
        var copy = BulkCreateProfileStore.Clone(profile);
        copy.Id = string.Empty;
        copy.Name = string.Empty;
        copy.DefaultForContainers = [];
        copy.HeaderMappings = [];
        copy.Rules = [.. copy.Rules.OrderBy(r => r.Attribute, StringComparer.OrdinalIgnoreCase)];
        return JsonSerializer.Serialize(copy);
    }

    private void RefreshModified()
    {
        if (IsApplying || _baseline is null)
        {
            return;
        }

        IsModified = !string.Equals(Signature(Capture()), _baseline, StringComparison.Ordinal);
    }
}
