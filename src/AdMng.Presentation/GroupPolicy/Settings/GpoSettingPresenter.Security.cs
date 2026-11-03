using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Security;
using AdMng.Presentation.GroupPolicy.SecuritySettings;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.GroupPolicy.Settings;

/// <summary>The security template (GptTmpl.inf) and the advanced audit policy (audit.csv) in GPMC's words.</summary>
public sealed partial class GpoSettingPresenter
{
    // ---- Account policies, Kerberos, security options ---------------------------------------------------------------------

    private GpoSettingText SecurityOption(GpoSettingKeyInfo info, IReadOnlyList<string> values)
    {
        var raw = Single(values);
        if (info.Option is not { } option)
        {
            return info.Area switch
            {
                GpoSettingArea.SystemAccess => Row(info, GpoSettingBranch.Policies, SecurityGroups(_l[L.GpoSettings.AccountPolicies]), info.Name ?? info.Key, raw),
                GpoSettingArea.KerberosPolicy => Row(info, GpoSettingBranch.Policies,
                    SecurityGroups(_l[L.GpoSettings.AccountPolicies], _l[SecuritySettingsText.OptionGroupKey(SecurityOptionGroup.Kerberos)]), info.Name ?? info.Key, raw),
                _ => UnknownSecurityRegistryValue(info, raw),
            };
        }

        var data = raw;
        if (option.Location == SecurityOptionLocation.RegistryValue)
        {
            if (!GpoSettingValue.TryParseRegistry(raw, out var action, out _, out var typed))
            {
                return Row(info, GpoSettingBranch.Policies, OptionGroups(option), _l[SecuritySettingsText.OptionKey(option.Id)], raw);
            }

            if (action is not (GpoRegistryAction.Set or GpoRegistryAction.SetIfMissing))
            {
                return Row(info, GpoSettingBranch.Policies, OptionGroups(option), _l[SecuritySettingsText.OptionKey(option.Id)], RegistryValueText(raw).Text);
            }

            data = typed;
        }

        IReadOnlyList<string> elements = [];
        string value;
        switch (option.Kind)
        {
            case SecurityValueKind.Number when TryNumber(data, out var number):
                if (option.Id == "SupportedEncryptionTypes")
                {
                    elements = EncryptionTypes(number);
                    value = elements.Count == 0 ? number.ToString(_l.Culture) : string.Join(", ", elements);
                }
                else
                {
                    value = Special(option.Id, number) ?? Unit(option.Unit, number);
                }

                break;
            case SecurityValueKind.Boolean or SecurityValueKind.Choice when TryNumber(data, out var choice):
                value = option.Kind == SecurityValueKind.Boolean && choice is 0 or 1
                    ? _l[choice == 1 ? L.SecuritySettings.Enabled : L.SecuritySettings.Disabled]
                    : Text(SecuritySettingsText.OptionChoiceKey(option.Id, choice), data.Trim());
                break;
            case SecurityValueKind.MultiText:
                elements = [.. data.Split(GpoSetting.ListSeparator, ',').Select(l => l.Trim()).Where(l => l.Length > 0)];
                value = elements.Count == 0 ? _l[L.SecuritySettings.EmptyText] : string.Join(" / ", elements);
                break;
            case SecurityValueKind.Text:
                var text = IniReader.Unquote(data.Trim());
                value = text.Length == 0 ? _l[L.SecuritySettings.EmptyText] : text;
                break;
            default:
                value = data;
                break;
        }

        return Row(info, GpoSettingBranch.Policies, OptionGroups(option), _l[SecuritySettingsText.OptionKey(option.Id)], value, elements,
            option.Id == "MaxClockSkew" ? _l[L.GpoSettings.ExplainMaxClockSkew] : null);
    }

    /// <summary>GPMC's folders of a catalog option: account policies by their group, security options below "Opcje zabezpieczeń".</summary>
    private string[] OptionGroups(SecurityOptionDefinition option) => option.Group switch
    {
        SecurityOptionGroup.PasswordPolicy or SecurityOptionGroup.AccountLockout or SecurityOptionGroup.Kerberos =>
            SecurityGroups(_l[L.GpoSettings.AccountPolicies], _l[SecuritySettingsText.OptionGroupKey(option.Group)]),
        _ => SecurityGroups(_l[L.GpoSettings.LocalPolicies], _l[L.GpoSettings.SecurityOptions], _l[SecuritySettingsText.OptionGroupKey(option.Group)]),
    };

    /// <summary>A <c>[Registry Values]</c> entry the catalog does not list: its registry path and typed data.</summary>
    private GpoSettingText UnknownSecurityRegistryValue(GpoSettingKeyInfo info, string raw)
    {
        var (value, elements) = RegistryValueText(raw);
        return Row(info, GpoSettingBranch.Policies, SecurityGroups(_l[L.GpoSettings.LocalPolicies], _l[L.GpoSettings.SecurityOptions], _l[L.GpoSettings.Other]),
            RegistryPathText(info.RegistryKey ?? info.Key, info.ValueName), value, elements);
    }

    /// <summary>The meaning of a special number ("Hasło nie wygasa"), null when the number has none.</summary>
    private string? Special(string id, long value) => id switch
    {
        "MaximumPasswordAge" when value <= 0 => _l[L.GpoSettings.PasswordNeverExpires],
        "MinimumPasswordAge" when value == 0 => _l[L.GpoSettings.PasswordChangeImmediately],
        "MinimumPasswordLength" when value == 0 => _l[L.GpoSettings.NoPasswordRequired],
        "PasswordHistorySize" when value == 0 => _l[L.GpoSettings.NoPasswordHistory],
        "LockoutBadCount" when value == 0 => _l[L.GpoSettings.AccountNeverLocked],
        "LockoutDuration" when value <= 0 => _l[L.GpoSettings.LockedUntilAdmin],
        "InactivityTimeoutSecs" when value == 0 => _l[L.GpoSettings.InactivityLimitOff],
        _ => null,
    };

    /// <summary>"42 dni", "10 minut", "5 nieudanych prób logowania" (a bare number without a unit).</summary>
    private string Unit(SecurityValueUnit unit, long value) => unit == SecurityValueUnit.None
        ? value.ToString("N0", _l.Culture)
        : Quantity(value, L.GpoSettings.UnitKey(unit, L.GpoSettings.PluralForm.One), L.GpoSettings.UnitKey(unit, L.GpoSettings.PluralForm.Few),
            L.GpoSettings.UnitKey(unit, L.GpoSettings.PluralForm.Many));

    /// <summary>The Kerberos encryption types of a <c>SupportedEncryptionTypes</c> mask.</summary>
    private IReadOnlyList<string> EncryptionTypes(long mask)
    {
        var names = new List<string>();
        (long Bit, string Name)[] known = [(1, "DES_CBC_CRC"), (2, "DES_CBC_MD5"), (4, "RC4_HMAC_MD5"), (8, "AES128_HMAC_SHA1"), (16, "AES256_HMAC_SHA1")];
        foreach (var (bit, name) in known)
        {
            if ((mask & bit) != 0)
            {
                names.Add(name);
            }
        }

        if ((mask & ~31L) != 0)
        {
            names.Add(_l[L.GpoSettings.EncryptionFuture]);
        }

        return names;
    }

    // ---- Audit ---------------------------------------------------------------------------------------------------------

    private GpoSettingText EventAudit(GpoSettingKeyInfo info, IReadOnlyList<string> values)
    {
        var name = info.Name ?? info.Key;
        return Row(info, GpoSettingBranch.Policies, SecurityGroups(_l[L.GpoSettings.LocalPolicies], _l[L.GpoSettings.AuditPolicy]),
            Text(SecuritySettingsText.EventAuditKey(name), name), AuditWords(Single(values)));
    }

    /// <summary>"Bez inspekcji", "Powodzenie", "Niepowodzenie", "Powodzenie i niepowodzenie" (the parsers' English words or 0–3).</summary>
    private string AuditWords(string value) => value.Trim() switch
    {
        "No Auditing" or "0" => _l[L.SecuritySettings.AuditNone],
        "Success" or "1" => _l[L.SecuritySettings.AuditSuccess],
        "Failure" or "2" => _l[L.SecuritySettings.AuditFailure],
        "Success and Failure" or "3" => _l[L.SecuritySettings.AuditBoth],
        var other => other,
    };

    private GpoSettingText AdvancedAudit(GpoSettingKeyInfo info, IReadOnlyList<string> values)
    {
        const string ExclusionMarker = "; exclusion: ";
        var raw = Single(values);
        var marker = raw.IndexOf(ExclusionMarker, StringComparison.Ordinal);
        var value = AuditWords(marker < 0 ? raw : raw[..marker]);
        IReadOnlyList<string> elements = marker < 0 ? [] : [_l.Format(L.GpoSettings.AuditExclusion, AuditWords(raw[(marker + ExclusionMarker.Length)..]))];
        var groups = info.Subcategory is { } subcategory
            ? SecurityGroups(_l[L.GpoSettings.AdvancedAudit], _l[L.GpoSettings.AuditPolicy], _l[SecuritySettingsText.AuditCategoryKey(subcategory.Category)])
            : SecurityGroups(_l[L.GpoSettings.AdvancedAudit], _l[L.GpoSettings.AuditPolicy]);
        var name = info.Subcategory is { } known ? _l[SecuritySettingsText.AuditSubcategoryKey(known.Id)] : info.Name ?? info.Key;
        return Row(info, GpoSettingBranch.Policies, groups, name, elements.Count == 0 ? value : value + "; " + elements[0], elements);
    }

    private GpoSettingText AuditOption(GpoSettingKeyInfo info, IReadOnlyList<string> values)
    {
        var option = info.Name ?? info.Key;
        var value = Single(values).Trim();
        var text = value switch
        {
            "1" or "Enabled" => _l[L.SecuritySettings.Enabled],
            "0" or "Disabled" => _l[L.SecuritySettings.Disabled],
            _ => value,
        };
        return Row(info, GpoSettingBranch.Policies, SecurityGroups(_l[L.GpoSettings.AdvancedAudit], _l[L.GpoSettings.AuditOptions]),
            Text(L.GpoSettings.AuditOptionKey(option), option), text);
    }

    private GpoSettingText GlobalSacl(GpoSettingKeyInfo info, IReadOnlyList<string> values)
    {
        var target = info.Section ?? string.Empty;
        var name = target.Trim().ToUpperInvariant() switch
        {
            "FILE SYSTEM" or "FILESYSTEM" => _l[L.GpoSettings.FileSystem],
            "REGISTRY" => _l[L.GpoSettings.Registry],
            _ => target.Length == 0 ? info.Name ?? info.Key : target,
        };
        var sddl = Single(values);
        return Row(info, GpoSettingBranch.Policies,
            SecurityGroups(_l[L.GpoSettings.AdvancedAudit], _l[L.GpoSettings.AuditPolicy], _l[SecuritySettingsText.AuditCategoryKey("GlobalObjectAccess")]),
            name, sddl.Length == 0 ? _l[L.SecuritySettings.NoSecurityDescriptor] : _l[L.GpoSettings.SaclDefined], sddl.Length == 0 ? [] : [sddl]);
    }

    // ---- User rights, Restricted Groups -----------------------------------------------------------------------------------

    private GpoSettingText UserRight(GpoSettingKeyInfo info, IReadOnlyList<string> values)
    {
        var constant = info.Name ?? info.Key;
        var names = Principals(values);
        return Row(info, GpoSettingBranch.Policies, SecurityGroups(_l[L.GpoSettings.LocalPolicies], _l[L.GpoSettings.UserRightsAssignment]),
            Text(SecuritySettingsText.RightKey(constant), constant), names.Count == 0 ? _l[L.SecuritySettings.Nobody] : string.Join(", ", names), names);
    }

    private GpoSettingText RestrictedGroup(GpoSettingKeyInfo info, IReadOnlyList<string> values)
    {
        var group = PrincipalName(info.Name ?? string.Empty);
        var members = string.Equals(info.Field, "Members", StringComparison.OrdinalIgnoreCase);
        var memberOf = string.Equals(info.Field, "MemberOf", StringComparison.OrdinalIgnoreCase);
        var name = members ? _l.Format(L.GpoSettings.RestrictedMembers, group)
            : memberOf ? _l.Format(L.GpoSettings.RestrictedMemberOf, group)
            : info.Field is { Length: > 0 } field ? Part(group, field) : group;
        var names = Principals(values);
        var value = names.Count > 0 ? string.Join(", ", names)
            : memberOf ? _l[L.GpoSettings.NoGroups]
            : _l[L.SecuritySettings.NoMembers];
        return Row(info, GpoSettingBranch.Policies, SecurityGroups(_l[L.GpoSettings.RestrictedGroups]), name, value, names,
            members && names.Count == 0 ? _l[L.SecuritySettings.NoMembersWarning] : null);
    }

    // ---- Services, registry and file security, event logs ---------------------------------------------------------------

    private GpoSettingText Service(GpoSettingKeyInfo info, IReadOnlyList<string> values)
    {
        var service = info.Name ?? info.Key;
        var groups = SecurityGroups(_l[L.GpoSettings.SystemServices]);
        return string.Equals(info.Field, "Security", StringComparison.OrdinalIgnoreCase)
            ? Permissions(info, groups, Part(service, _l[L.GpoSettings.Permissions]), Single(values))
            : Row(info, GpoSettingBranch.Policies, groups, service, ServiceMode(Single(values)));
    }

    /// <summary>"Automatyczny", "Ręczny", "Wyłączony", "Rozruch", "System".</summary>
    private string ServiceMode(string mode) => mode.Trim().ToUpperInvariant() switch
    {
        "AUTOMATIC" or "2" => _l[L.SecuritySettings.ServiceAutomatic],
        "MANUAL" or "3" => _l[L.SecuritySettings.ServiceManual],
        "DISABLED" or "4" => _l[L.SecuritySettings.ServiceDisabled],
        "BOOT" or "0" => _l[L.GpoSettings.ServiceBoot],
        "SYSTEM" or "1" => _l[L.GpoSettings.ServiceSystem],
        _ => mode,
    };

    private GpoSettingText ObjectSecurity(GpoSettingKeyInfo info, IReadOnlyList<string> values)
    {
        var path = info.Name ?? info.Key;
        var groups = SecurityGroups(_l[info.Area == GpoSettingArea.RegistryKeySecurity ? L.GpoSettings.Registry : L.GpoSettings.FileSystem]);
        return string.Equals(info.Field, "Security", StringComparison.OrdinalIgnoreCase)
            ? Permissions(info, groups, Part(path, _l[L.GpoSettings.Permissions]), Single(values))
            : Row(info, GpoSettingBranch.Policies, groups, path, InheritanceMode(Single(values)));
    }

    private string InheritanceMode(string mode) => mode.Trim().ToUpperInvariant() switch
    {
        "PROPAGATE" or "0" => _l[L.GpoSettings.ModePropagate],
        "DONOTREPLACE" or "1" => _l[L.GpoSettings.ModeDoNotReplace],
        "REPLACE" or "2" => _l[L.GpoSettings.ModeReplace],
        _ => mode,
    };

    /// <summary>An SDDL value: whether permissions are set, the SDDL itself as the element (no ACL parsing here).</summary>
    private GpoSettingText Permissions(GpoSettingKeyInfo info, string[] groups, string name, string sddl) =>
        Row(info, GpoSettingBranch.Policies, groups, name,
            sddl.Trim().Length == 0 ? _l[L.SecuritySettings.NoSecurityDescriptor] : _l[L.GpoSettings.PermissionsDefined], sddl.Trim().Length == 0 ? [] : [sddl.Trim()]);

    private GpoSettingText EventLog(GpoSettingKeyInfo info, IReadOnlyList<string> values)
    {
        var log = _l[(info.Section ?? string.Empty).ToUpperInvariant() switch
        {
            "SECURITY LOG" => L.GpoSettings.LogSecurity,
            "APPLICATION LOG" => L.GpoSettings.LogApplication,
            _ => L.GpoSettings.LogSystem,
        }];
        var key = info.Name ?? info.Key;
        var raw = Single(values).Trim();
        var number = TryNumber(raw, out var n) ? n : (long?)null;
        var (name, value) = key.ToUpperInvariant() switch
        {
            "MAXIMUMLOGSIZE" => (_l.Format(L.GpoSettings.LogMaximumSize, log), number is { } size ? _l.Format(L.GpoSettings.Kilobytes, size.ToString("N0", _l.Culture)) : raw),
            "AUDITLOGRETENTIONPERIOD" => (_l.Format(L.GpoSettings.LogRetentionMethod, log), number switch
            {
                0 => _l[L.GpoSettings.RetentionAsNeeded],
                1 => _l[L.GpoSettings.RetentionByDays],
                2 => _l[L.GpoSettings.RetentionManual],
                _ => raw,
            }),
            "RETENTIONDAYS" => (_l.Format(L.GpoSettings.LogRetentionDays, log), number is { } days ? Unit(SecurityValueUnit.Days, days) : raw),
            "RESTRICTGUESTACCESS" => (_l.Format(L.GpoSettings.LogRestrictGuests, log), number switch
            {
                1 => _l[L.SecuritySettings.Enabled],
                0 => _l[L.SecuritySettings.Disabled],
                _ => raw,
            }),
            _ => (Part(info.Section ?? log, key), raw),
        };
        return Row(info, GpoSettingBranch.Policies, SecurityGroups(_l[L.GpoSettings.EventLog]), name, value);
    }

    private GpoSettingText SecuritySection(GpoSettingKeyInfo info, IReadOnlyList<string> values) =>
        Row(info, GpoSettingBranch.Policies, SecurityGroups(info.Section ?? _l[L.GpoSettings.Other]), info.Name ?? info.Key, Single(values));
}
