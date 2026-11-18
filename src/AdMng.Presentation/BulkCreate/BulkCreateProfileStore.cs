using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;

namespace AdMng.Presentation.BulkCreate;

/// <summary>
/// The naming profiles of the bulk creation wizard (docs/dev/phase3-bulk-create.md 2.6, owner B3): the built-in profiles
/// (code, never stored) – users <em>Standardowy</em> and <em>Konta numerowane (kd201…)</em>, contacts, groups and computers
/// <em>Standardowy</em> – and the saved profiles of <see cref="AppSettings.BulkCreate"/> (patterns and options only: never
/// a row value, a rendered value, a counter position or a password; rule 1, rule 2), the last profile used per kind and the
/// default profile of a container (portable DNs).
/// </summary>
/// <remarks>
/// Every profile handed out is a copy: the settings object is replaced by updates and may be read by another window, so a
/// profile being edited never aliases it. Writes go through <see cref="ISettingsStore.UpdateAsync"/> (normalized there).
/// </remarks>
public sealed class BulkCreateProfileStore
{
    /// <summary>The prefix of the ids of the built-in profiles (never stored).</summary>
    public const string BuiltInPrefix = "builtin:";

    /// <summary>The id of the built-in <em>Konta numerowane (kd201…)</em> (users).</summary>
    public const string NumberedId = BuiltInPrefix + "User:numbered";

    /// <summary>The counter rule of <em>Konta numerowane</em>.</summary>
    public const string NumberedLoginPattern = "kd{$nr:201}";

    /// <summary>The name rule of every built-in profile for people (<c>cn</c> and <c>displayName</c>: the person's name).</summary>
    public const string PersonNamePattern = "{givenName} {sn}";

    private readonly ISettingsStore _settings;
    private readonly ILocalizer _localizer;

    public BulkCreateProfileStore(ISettingsStore settings, ILocalizer localizer)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }

    /// <summary>The id of the built-in <em>Standardowy</em> of a kind.</summary>
    public static string StandardId(ObjectKind kind) => $"{BuiltInPrefix}{kind}:standard";

    /// <summary>A built-in profile (code, never stored, never deleted).</summary>
    public static bool IsBuiltIn(string? id) => id is not null && id.StartsWith(BuiltInPrefix, StringComparison.Ordinal);

    /// <summary>The built-in profiles of a kind (<em>Standardowy</em> first).</summary>
    public IReadOnlyList<BulkCreateProfile> BuiltIns(ObjectKind kind)
    {
        var standard = new BulkCreateProfile
        {
            Id = StandardId(kind),
            Name = _localizer[L.BulkCreate.ProfileStandard],
            Kind = kind,
            CounterMode = CounterMode.FillGaps,
        };
        switch (kind)
        {
            case ObjectKind.User:
                standard.Rules =
                [
                    new(Attr.Cn, PersonNamePattern, false),
                    new(Attr.DisplayName, PersonNamePattern, false),
                    new(Attr.SamAccountName, "{givenName|sam|lower}.{sn|sam|lower}", false),
                    new(Attr.UserPrincipalName, "{sAMAccountName}", false),
                ];
                var numbered = new BulkCreateProfile
                {
                    Id = NumberedId,
                    Name = _localizer[L.BulkCreate.ProfileNumbered],
                    Kind = kind,
                    CounterMode = CounterMode.AfterHighest,
                    ReuseRecycleBinNumbers = false,
                    Rules =
                    [
                        new(Attr.Cn, PersonNamePattern, false),
                        new(Attr.DisplayName, PersonNamePattern, false),
                        new(Attr.SamAccountName, NumberedLoginPattern, false),
                        new(Attr.UserPrincipalName, "{sAMAccountName}", false),
                        new(Attr.Mail, "{sAMAccountName}@{$mailDomain}", false),
                    ],
                };
                return [standard, numbered];
            case ObjectKind.Contact:
                standard.Rules = [new(Attr.Cn, PersonNamePattern, false), new(Attr.DisplayName, PersonNamePattern, false)];
                return [standard];
            default:
                // Groups and computers: cn from the column (Nazwa, Nazwa komputera …), the logon name from cn (a computer's $
                // is appended by the value rules).
                standard.Rules = [new(Attr.SamAccountName, "{cn}", false)];
                return [standard];
        }
    }

    /// <summary>The built-ins of the kind followed by its saved profiles (copies), in the order of the settings.</summary>
    public IReadOnlyList<BulkCreateProfile> ProfilesFor(ObjectKind kind) =>
        [.. BuiltIns(kind), .. Saved().Where(p => p.Kind == kind).Select(Clone)];

    /// <summary>A copy of the profile with <paramref name="id"/> (built-in or saved), or null.</summary>
    public BulkCreateProfile? Find(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        if (IsBuiltIn(id))
        {
            return Enum.GetValues<ObjectKind>().Where(k => id.StartsWith($"{BuiltInPrefix}{k}:", StringComparison.Ordinal))
                .SelectMany(BuiltIns)
                .FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        }

        return Saved().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal)) is { } saved ? Clone(saved) : null;
    }

    /// <summary>
    /// The profile a new wizard starts with (2.6): the default profile of <paramref name="containerDn"/>, else the last
    /// profile used for the kind, else <em>Standardowy</em>.
    /// </summary>
    public BulkCreateProfile Select(ObjectKind kind, string? containerDn, string? namingContext)
    {
        if (DefaultFor(kind, containerDn, namingContext) is { } byContainer)
        {
            return byContainer;
        }

        if (_settings.Current.BulkCreate.LastProfileIds.TryGetValue(kind.ToString(), out var last) && Find(last) is { } lastUsed && lastUsed.Kind == kind)
        {
            return lastUsed;
        }

        return BuiltIns(kind)[0];
    }

    /// <summary>The saved profile of the kind that is the default of the container (a copy), or null.</summary>
    public BulkCreateProfile? DefaultFor(ObjectKind kind, string? containerDn, string? namingContext)
    {
        if (string.IsNullOrEmpty(containerDn) || string.IsNullOrEmpty(namingContext))
        {
            return null;
        }

        var profile = Saved().FirstOrDefault(p => p.Kind == kind
            && p.DefaultForContainers.Any(d => DistinguishedName.AreEqual(DistinguishedName.FromPortable(d, namingContext), containerDn)));
        return profile is null ? null : Clone(profile);
    }

    /// <summary>
    /// Saves <paramref name="profile"/> (a new one or a saved one with the same id; never a built-in). False when
    /// <see cref="BulkCreateSettings.MaxProfiles"/> profiles are saved already.
    /// </summary>
    public async Task<bool> SaveAsync(BulkCreateProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (IsBuiltIn(profile.Id))
        {
            throw new ArgumentException("A built-in profile is never stored.", nameof(profile));
        }

        var copy = Clone(profile);
        var saved = Saved();
        if (!saved.Any(p => string.Equals(p.Id, copy.Id, StringComparison.Ordinal)) && saved.Count >= BulkCreateSettings.MaxProfiles)
        {
            return false;
        }

        await _settings.UpdateAsync(settings =>
        {
            var profiles = settings.BulkCreate.Profiles;
            var index = profiles.FindIndex(p => string.Equals(p.Id, copy.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                profiles[index] = copy;
            }
            else
            {
                profiles.Add(copy);
            }

            settings.BulkCreate.LastProfileIds[copy.Kind.ToString()] = copy.Id;
        }).ConfigureAwait(true);
        return true;
    }

    /// <summary>Deletes a saved profile (the last-used entries pointing to it go too).</summary>
    public Task DeleteAsync(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (IsBuiltIn(id))
        {
            return Task.CompletedTask;
        }

        return _settings.UpdateAsync(settings =>
        {
            settings.BulkCreate.Profiles.RemoveAll(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            foreach (var kind in settings.BulkCreate.LastProfileIds.Where(e => string.Equals(e.Value, id, StringComparison.Ordinal)).Select(e => e.Key).ToList())
            {
                settings.BulkCreate.LastProfileIds.Remove(kind);
            }
        });
    }

    /// <summary>
    /// Makes the saved profile <paramref name="id"/> the default of the container (no other profile of its kind stays the
    /// default there), or removes it as the default.
    /// </summary>
    public Task SetDefaultForContainerAsync(string id, string containerDn, string namingContext, bool isDefault)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(containerDn);
        ArgumentException.ThrowIfNullOrEmpty(namingContext);
        var portable = DistinguishedName.ToPortable(containerDn, namingContext);
        return _settings.UpdateAsync(settings =>
        {
            var profiles = settings.BulkCreate.Profiles;
            var target = profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            if (target is null)
            {
                return;
            }

            foreach (var profile in profiles.Where(p => p.Kind == target.Kind))
            {
                profile.DefaultForContainers.RemoveAll(d => DistinguishedName.AreEqual(DistinguishedName.FromPortable(d, namingContext), containerDn));
            }

            if (isDefault)
            {
                target.DefaultForContainers.Add(portable);
            }
        });
    }

    /// <summary>Remembers the profile last used for the kind (the next wizard starts with it unless the container has a default).</summary>
    public Task RememberLastAsync(ObjectKind kind, string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (_settings.Current.BulkCreate.LastProfileIds.TryGetValue(kind.ToString(), out var last) && string.Equals(last, id, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        return _settings.UpdateAsync(settings => settings.BulkCreate.LastProfileIds[kind.ToString()] = id);
    }

    /// <summary>A deep copy (lists copied; the records are immutable).</summary>
    public static BulkCreateProfile Clone(BulkCreateProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new BulkCreateProfile
        {
            Id = profile.Id,
            Name = profile.Name,
            Kind = profile.Kind,
            Rules = [.. profile.Rules ?? []],
            UpnSuffix = profile.UpnSuffix,
            MailDomain = profile.MailDomain,
            CounterMode = profile.CounterMode,
            ReuseRecycleBinNumbers = profile.ReuseRecycleBinNumbers,
            Collisions = profile.Collisions,
            SanitizeIdentifiers = profile.SanitizeIdentifiers,
            TruncateNames = profile.TruncateNames,
            AddPrimarySmtp = profile.AddPrimarySmtp,
            Phones = profile.Phones,
            InitialMode = profile.InitialMode,
            InitialLength = profile.InitialLength,
            MustChangeAtLogon = profile.MustChangeAtLogon,
            CreateEnabled = profile.CreateEnabled,
            ExpiresAfterDays = profile.ExpiresAfterDays,
            MatchExisting = profile.MatchExisting,
            GroupDns = [.. profile.GroupDns ?? []],
            GroupDomain = profile.GroupDomain,
            GroupScope = profile.GroupScope,
            SecurityGroup = profile.SecurityGroup,
            HeaderMappings = [.. profile.HeaderMappings ?? []],
            DefaultForContainers = [.. profile.DefaultForContainers ?? []],
        };
    }

    private List<BulkCreateProfile> Saved() => [.. _settings.Current.BulkCreate.Profiles];

    public override string ToString() => $"Bulk create profiles: {_settings.Current.BulkCreate.Profiles.Count} saved";
}
