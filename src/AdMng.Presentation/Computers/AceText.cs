using System.Globalization;
using AdMng.Core.Security;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Computers;

/// <summary>
/// Localized text of one ACE of a <see cref="Core.Batch.ChangeAction.ModifySecurity"/> step for the preview: allow/deny,
/// the rights (known extended rights, validated writes and property sets by name, others by their schema name or GUID)
/// and the inheritance. The trustee is shown in its own column.
/// </summary>
public static class AceText
{
    private const int GenericRead = 0x20094;
    private const int GenericWrite = 0x20028;
    private const int FullControl = 0xF01FF;

    /// <summary>The names of the rights ADUC grants, valid only with exactly that mask (control access, read+write, validated write).</summary>
    private static readonly Dictionary<Guid, (string Key, int Mask)> KnownObjectTypes = new()
    {
        [ExtendedRightIds.ResetPassword] = (L.Computers.AclRightResetPassword, 0x100),
        [ExtendedRightIds.AccountRestrictions] = (L.Computers.AclRightAccountRestrictions, 0x30),
        [ExtendedRightIds.ValidatedWriteDnsHostName] = (L.Computers.AclRightDnsHostName, 0x08),
        [ExtendedRightIds.ValidatedWriteServicePrincipalName] = (L.Computers.AclRightServicePrincipalName, 0x08),
        [ExtendedRightIds.ApplyGroupPolicy] = (L.Computers.AclRightApplyGroupPolicy, 0x100),
    };

    private static readonly (int Bit, string Key, string? KeyOf)[] Bits =
    [
        (0x100, L.Computers.AclRightExtendedRight, L.Computers.AclRightExtendedRightOf),
        (0x10, L.Computers.AclRightReadProperty, L.Computers.AclRightReadPropertyOf),
        (0x20, L.Computers.AclRightWriteProperty, L.Computers.AclRightWritePropertyOf),
        (0x08, L.Computers.AclRightSelf, L.Computers.AclRightSelfOf),
        (0x01, L.Computers.AclRightCreateChild, null),
        (0x02, L.Computers.AclRightDeleteChild, null),
        (0x04, L.Computers.AclRightListChildren, null),
        (0x80, L.Computers.AclRightListObject, null),
        (0x40, L.Computers.AclRightDeleteTree, null),
        (0x10000, L.Computers.AclRightDelete, null),
        (0x20000, L.Computers.AclRightReadControl, null),
        (0x40000, L.Computers.AclRightWriteDacl, null),
        (0x80000, L.Computers.AclRightWriteOwner, null),
    ];

    /// <summary><c>Zezwalaj – resetowanie hasła</c>, <c>Odmawiaj – zapis description (także dla obiektów podrzędnych)</c>.</summary>
    public static string Describe(AceSpec ace, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(ace);
        ArgumentNullException.ThrowIfNull(localizer);
        var rights = Rights(ace, localizer);
        if ((ace.Flags & SecurityDescriptorAce.ContainerInheritFlag) != 0)
        {
            rights += " " + localizer[L.Computers.AclInherited];
        }

        return localizer.Format(ace.Qualifier == AceQualifier.Allow ? L.Computers.AclAllow : L.Computers.AclDeny, rights);
    }

    /// <summary>The rights of the mask, separated by commas.</summary>
    public static string Rights(AceSpec ace, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(ace);
        ArgumentNullException.ThrowIfNull(localizer);
        var mask = ace.Mask;
        if ((mask & FullControl) == FullControl || (mask & 0x10000000) != 0)
        {
            return localizer[L.Computers.AclRightFullControl];
        }

        if (ace.ObjectType is { } objectType && KnownObjectTypes.TryGetValue(objectType, out var known) && mask == known.Mask)
        {
            // The rights ADUC grants for the right/property set (control access, read+write, validated write); any
            // other mask (read only, write only …) is described bit by bit below.
            return localizer[known.Key];
        }

        var parts = new List<string>();
        if (ace.ObjectType is null && (mask & GenericRead) == GenericRead)
        {
            parts.Add(localizer[L.Computers.AclRightGenericRead]);
            mask &= ~GenericRead;
        }

        if (ace.ObjectType is null && (mask & GenericWrite) == GenericWrite)
        {
            parts.Add(localizer[L.Computers.AclRightGenericWrite]);
            mask &= ~GenericWrite;
        }

        var target = ace.ObjectType is { } type ? SecurityDescriptorSummary.KnownRightName(type) ?? type.ToString("D") : null;
        foreach (var (bit, key, keyOf) in Bits)
        {
            if ((mask & bit) == 0)
            {
                continue;
            }

            parts.Add(target is not null && keyOf is not null ? localizer.Format(keyOf, target) : localizer[key]);
            mask &= ~bit;
        }

        if (mask != 0)
        {
            parts.Add(localizer.Format(L.Computers.AclRightOther, mask.ToString("X8", CultureInfo.InvariantCulture)));
        }

        return string.Join(", ", parts);
    }
}
