using System.Globalization;
using System.Text;

namespace AdMng.Core.Security;

/// <summary>
/// One ACE of a parsed security descriptor (DACL), as used in the watchdog's comparison summary (watchdog specification
/// 9.4) and by the ACL delegations of the privileged access review (README U4b).
/// </summary>
public sealed record SecurityDescriptorAce
{
    /// <summary>OBJECT_INHERIT_ACE.</summary>
    public const byte ObjectInheritFlag = 0x01;

    /// <summary>CONTAINER_INHERIT_ACE: child containers (and through them the subtree) inherit the ACE.</summary>
    public const byte ContainerInheritFlag = 0x02;

    /// <summary>NO_PROPAGATE_INHERIT_ACE: only the direct children inherit the ACE.</summary>
    public const byte NoPropagateInheritFlag = 0x04;

    /// <summary>INHERIT_ONLY_ACE: the ACE applies to the children only, not to the object itself.</summary>
    public const byte InheritOnlyFlag = 0x08;

    /// <summary>INHERITED_ACE: the ACE was inherited from a parent (not set on the object itself).</summary>
    public const byte InheritedFlag = 0x10;

    public required byte Type { get; init; }
    public required byte Flags { get; init; }
    public required int Mask { get; init; }
    public required string Sid { get; init; }
    public Guid? ObjectType { get; init; }
    public Guid? InheritedObjectType { get; init; }

    /// <summary>Allowed (0x00, 0x05, 0x09, 0x0B) as opposed to denied or audit ACE types.</summary>
    public bool IsAllow => Type is 0x00 or 0x05 or 0x09 or 0x0B;

    /// <summary>Denied (0x01, 0x06, 0x0A, 0x0C).</summary>
    public bool IsDeny => Type is 0x01 or 0x06 or 0x0A or 0x0C;

    /// <summary>Inherited from a parent (<see cref="InheritedFlag"/>).</summary>
    public bool IsInherited => (Flags & InheritedFlag) != 0;

    /// <summary>Applies to the children only (<see cref="InheritOnlyFlag"/>).</summary>
    public bool IsInheritOnly => (Flags & InheritOnlyFlag) != 0;

    /// <summary>Child containers inherit it (<see cref="ContainerInheritFlag"/>).</summary>
    public bool IsContainerInherit => (Flags & ContainerInheritFlag) != 0;

    /// <summary>The summary value <c>ace:&lt;type&gt;/&lt;flags&gt;/&lt;mask hex&gt;/&lt;SID&gt;[/&lt;objectType&gt;][/&lt;inheritedObjectType&gt;]</c>.</summary>
    public string ToSummaryValue()
    {
        var sb = new StringBuilder(SecurityDescriptorSummary.AcePrefix)
            .Append(Type.ToString("X2", CultureInfo.InvariantCulture)).Append('/')
            .Append(Flags.ToString("X2", CultureInfo.InvariantCulture)).Append('/')
            .Append(Mask.ToString("X8", CultureInfo.InvariantCulture)).Append('/')
            .Append(Sid);
        if (ObjectType is not null || InheritedObjectType is not null)
        {
            sb.Append('/').Append(ObjectType?.ToString("D") ?? string.Empty);
        }

        if (InheritedObjectType is { } inherited)
        {
            sb.Append('/').Append(inherited.ToString("D"));
        }

        return sb.ToString();
    }

    /// <summary>Parses a summary value produced by <see cref="ToSummaryValue"/>; null when it is not an ACE value.</summary>
    public static SecurityDescriptorAce? FromSummaryValue(string? value)
    {
        if (value is null || !value.StartsWith(SecurityDescriptorSummary.AcePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = value[SecurityDescriptorSummary.AcePrefix.Length..].Split('/');
        if (parts.Length < 4
            || !byte.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var type)
            || !byte.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags)
            || !int.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var mask))
        {
            return null;
        }

        return new SecurityDescriptorAce
        {
            Type = type,
            Flags = flags,
            Mask = mask,
            Sid = parts[3],
            ObjectType = parts.Length > 4 && Guid.TryParse(parts[4], out var objectType) ? objectType : null,
            InheritedObjectType = parts.Length > 5 && Guid.TryParse(parts[5], out var inherited) ? inherited : null,
        };
    }
}
