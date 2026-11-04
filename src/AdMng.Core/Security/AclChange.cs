using System.Globalization;

namespace AdMng.Core.Security;

/// <summary>Whether an ACE grants or denies its rights.</summary>
public enum AceQualifier
{
    Allow,
    Deny,
}

/// <summary>
/// One explicit DACL entry the pipeline adds or removes (<see cref="Batch.ChangeAction.ModifySecurity"/>): the writable
/// subset of <see cref="SecurityDescriptorAce"/>. An ACE with <see cref="ObjectType"/> or <see cref="InheritedObjectType"/>
/// is written as an object ACE (types 0x05 / 0x06), otherwise as a plain one (0x00 / 0x01). Inherited ACEs are never
/// written: <see cref="Flags"/> must not carry <see cref="SecurityDescriptorAce.InheritedFlag"/>.
/// </summary>
public sealed record AceSpec
{
    /// <summary>The ACE flags a spec may carry: object/container inherit, no propagate, inherit only.</summary>
    public const byte AllowedFlags = SecurityDescriptorAce.ObjectInheritFlag | SecurityDescriptorAce.ContainerInheritFlag
        | SecurityDescriptorAce.NoPropagateInheritFlag | SecurityDescriptorAce.InheritOnlyFlag;

    public required AceQualifier Qualifier { get; init; }

    /// <summary>Inheritance flags (<see cref="AllowedFlags"/> only).</summary>
    public byte Flags { get; init; }

    /// <summary>Access mask (ADS_RIGHT_* bits, e.g. 0x100 control access, 0x20 write property, 0x08 validated write).</summary>
    public required int Mask { get; init; }

    /// <summary>Trustee SID (S-1-…).</summary>
    public required string TrusteeSid { get; init; }

    /// <summary>Extended right, validated write, property or property set (object ACEs).</summary>
    public Guid? ObjectType { get; init; }

    /// <summary>Object class the ACE is inherited to (object ACEs).</summary>
    public Guid? InheritedObjectType { get; init; }

    /// <summary>
    /// Display name of the trustee for the preview (e.g. <c>CONTOSO\Pomoc techniczna</c>) – shown, never written and not part
    /// of the identity of the ACE (<see cref="SameAce"/>).
    /// </summary>
    public string? TrusteeName { get; init; }

    /// <summary>Object ACE (has an object type or an inherited object type).</summary>
    public bool IsObjectAce => ObjectType is not null || InheritedObjectType is not null;

    /// <summary>The binary ACE type: 0x00 / 0x01 or, for object ACEs, 0x05 / 0x06.</summary>
    public byte AceType => (Qualifier, IsObjectAce) switch
    {
        (AceQualifier.Allow, false) => 0x00,
        (AceQualifier.Deny, false) => 0x01,
        (AceQualifier.Allow, true) => 0x05,
        _ => 0x06,
    };

    /// <summary>An allow object ACE for an extended right / validated write / property (no inheritance).</summary>
    public static AceSpec Allow(string trusteeSid, int mask, Guid? objectType = null, string? trusteeName = null) => new()
    {
        Qualifier = AceQualifier.Allow,
        Mask = mask,
        TrusteeSid = trusteeSid,
        ObjectType = objectType,
        TrusteeName = trusteeName,
    };

    /// <summary>The spec of an explicit ACE read from a descriptor (null for inherited ACEs and non allow/deny types).</summary>
    public static AceSpec? FromAce(SecurityDescriptorAce ace)
    {
        ArgumentNullException.ThrowIfNull(ace);
        if (ace.IsInherited || (!ace.IsAllow && !ace.IsDeny) || ace.Type is not (0x00 or 0x01 or 0x05 or 0x06))
        {
            return null;
        }

        return new AceSpec
        {
            Qualifier = ace.IsAllow ? AceQualifier.Allow : AceQualifier.Deny,
            Flags = (byte)(ace.Flags & AllowedFlags),
            Mask = ace.Mask,
            TrusteeSid = ace.Sid,
            ObjectType = ace.ObjectType,
            InheritedObjectType = ace.InheritedObjectType,
        };
    }

    /// <summary>
    /// Same ACE as <paramref name="other"/>: qualifier, flags, mask, trustee SID (ordinal, case-insensitive) and object
    /// types. <see cref="TrusteeName"/> is ignored.
    /// </summary>
    public bool SameAce(AceSpec other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Qualifier == other.Qualifier && Flags == other.Flags && Mask == other.Mask
            && string.Equals(TrusteeSid, other.TrusteeSid, StringComparison.OrdinalIgnoreCase)
            && ObjectType == other.ObjectType && InheritedObjectType == other.InheritedObjectType;
    }

    /// <summary>The ACE as <see cref="SecurityDescriptorAce"/> (for <see cref="SecurityDescriptorSummary.Describe(SecurityDescriptorAce)"/>).</summary>
    public SecurityDescriptorAce ToAce() => new()
    {
        Type = AceType,
        Flags = Flags,
        Mask = Mask,
        Sid = TrusteeSid,
        ObjectType = ObjectType,
        InheritedObjectType = InheritedObjectType,
    };

    /// <summary>Language-neutral one-line description (<c>allow User-Force-Change-Password S-1-…</c>), for the audit log.</summary>
    public string Describe() => SecurityDescriptorSummary.Describe(ToAce());

    /// <summary>Validates the spec: SID syntax, flags, a non-zero mask.</summary>
    /// <exception cref="ArgumentException">The spec cannot be written.</exception>
    public void Validate()
    {
        if (!IsValidSid(TrusteeSid))
        {
            throw new ArgumentException("The trustee is not a valid SID.", nameof(TrusteeSid));
        }

        if ((Flags & ~AllowedFlags) != 0)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"ACE flags 0x{Flags:X2} cannot be written."), nameof(Flags));
        }

        if (Mask == 0)
        {
            throw new ArgumentException("An ACE needs at least one right.", nameof(Mask));
        }
    }

    /// <summary>Whether <paramref name="sid"/> is a SID string that serializes (revision 1, at most 15 sub-authorities).</summary>
    public static bool IsValidSid(string? sid)
    {
        if (string.IsNullOrWhiteSpace(sid) || !sid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var bytes = ActiveDirectory.AdConvert.SidToBytes(sid);
            return bytes[1] <= 15;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return false;
        }
    }
}

/// <summary>
/// A delta on the DACL of one object: explicit ACEs to add and to remove (<see cref="Batch.ChangeAction.ModifySecurity"/>,
/// applied by <see cref="DaclEditor"/> to the DACL read at execution time). The planner keeps only the effective delta
/// (ACEs already present are not added, absent ones not removed), so the inverse delta is the rollback.
/// </summary>
public sealed record AclChange(IReadOnlyList<AceSpec> Added, IReadOnlyList<AceSpec> Removed)
{
    /// <summary>No ACE added or removed.</summary>
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0;

    /// <summary>
    /// The explicit DACL entries at plan time (<see cref="SecurityDescriptorAce.ToSummaryValue"/> values) for the preview.
    /// Never written back: the executor applies the delta to the DACL it reads.
    /// </summary>
    public IReadOnlyList<string> PlannedFrom { get; init; } = [];

    /// <summary>
    /// The explicit ACEs become exactly <see cref="Added"/>: the executor also removes every explicit ACE it reads that
    /// <see cref="Added"/> does not hold. Only for an object the same plan creates - a GPO copied or restored with its
    /// permissions, whose new container starts with the schema default DACL (Authenticated Users' Apply Group Policy
    /// among it) that the source's security filtering may lack.
    /// </summary>
    public bool ReplacesExplicit { get; init; }

    /// <summary>The inverse delta (added ↔ removed) – the rollback.</summary>
    public AclChange Inverse() => new(Removed, Added);

    /// <summary>Counts only (no SIDs).</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"AclChange {{ Added = {Added.Count}, Removed = {Removed.Count} }}");
}
