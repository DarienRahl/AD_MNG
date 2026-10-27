using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;
using AdMng.Core.Schema;

namespace AdMng.Core.Computers;

/// <summary>
/// The facts of one computer account (K1 design note 4.1), read from one search result. In memory only; the LAPS expiry is
/// read for its presence – the LAPS password never is.
/// </summary>
public sealed record ComputerFacts
{
    /// <summary>Windows LAPS password expiration (large integer).</summary>
    public const string WindowsLapsExpiration = "msLAPS-PasswordExpirationTime";

    /// <summary>Legacy LAPS password expiration (large integer).</summary>
    public const string LegacyLapsExpiration = "ms-Mcs-AdmPwdExpirationTime";

    /// <summary>The creator SID of an account created by a user with the "add workstations to the domain" right.</summary>
    public const string CreatorSid = "mS-DS-CreatorSID";

    /// <summary>The attributes every computer read asks for (without the LAPS attributes the schema does not have).</summary>
    public static IReadOnlyList<string> Attributes(ISchemaCatalog? schema)
    {
        var attributes = new List<string>
        {
            Attr.ObjectClass, Attr.Name, Attr.Cn, Attr.DnsHostName, Attr.SamAccountName, Attr.ObjectSid, Attr.ObjectGuid,
            Attr.UserAccountControl, Attr.PrimaryGroupId, Attr.OperatingSystem, Attr.OperatingSystemVersion,
            Attr.OperatingSystemServicePack, Attr.LastLogonTimestamp, Attr.PwdLastSet, Attr.WhenCreated, Attr.ManagedBy,
            CreatorSid, Attr.Description, Attr.Location,
        };
        foreach (var laps in new[] { WindowsLapsExpiration, LegacyLapsExpiration })
        {
            if (schema is null || schema.Attributes.Count == 0 || schema.FindAttribute(laps) is not null)
            {
                attributes.Add(laps);
            }
        }

        return attributes;
    }

    public required string DistinguishedName { get; init; }

    public required string Name { get; init; }

    public string? DnsHostName { get; init; }

    public string? SamAccountName { get; init; }

    public string? Sid { get; init; }

    public Guid? ObjectGuid { get; init; }

    public ComputerType Type { get; init; }

    public UserAccountControl AccountControl { get; init; }

    public int? PrimaryGroupId { get; init; }

    public bool IsEnabled => !AccountControl.HasFlag(UserAccountControl.AccountDisable);

    public string? OperatingSystem { get; init; }

    /// <summary>The raw operatingSystemVersion value (<c>10.0 (26100)</c>).</summary>
    public string? OperatingSystemVersionText { get; init; }

    public OperatingSystemVersion? Version { get; init; }

    public string? ServicePack { get; init; }

    /// <summary>lastLogonTimestamp: replicated, up to ~14 days behind the real last logon.</summary>
    public DateTimeOffset? LastLogonTimestamp { get; init; }

    public DateTimeOffset? PasswordLastSet { get; init; }

    public DateTimeOffset? Created { get; init; }

    /// <summary>managedBy (the owner the operator assigned).</summary>
    public string? ManagedBy { get; init; }

    /// <summary>mS-DS-CreatorSID: set when a user joined the computer with the machine account quota.</summary>
    public string? CreatorSidValue { get; init; }

    public string? Description { get; init; }

    public string? Location { get; init; }

    /// <summary>The parent container (the OU the computer is in).</summary>
    public string? ParentDn { get; init; }

    /// <summary>The LAPS password expiration (Windows LAPS first), null when the computer has no LAPS password.</summary>
    public DateTimeOffset? LapsExpiration { get; init; }

    /// <summary>Which LAPS the expiration comes from (null without LAPS).</summary>
    public string? LapsKind { get; init; }

    /// <summary>The facts of a search result.</summary>
    public static ComputerFacts From(DirectoryObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var uac = obj.AccountControl ?? UserAccountControl.None;
        var os = obj.GetString(Attr.OperatingSystem);
        var versionText = obj.GetString(Attr.OperatingSystemVersion);
        _ = OperatingSystemVersion.TryParse(versionText, out var version);
        var primaryGroup = obj.GetInt32(Attr.PrimaryGroupId);
        var windowsLaps = obj.GetFileTime(WindowsLapsExpiration);
        var legacyLaps = obj.GetFileTime(LegacyLapsExpiration);
        var creator = obj.GetBytes(CreatorSid) is { Length: >= 8 } sidBytes ? SafeSid(sidBytes) : obj.GetString(CreatorSid) is { } text && text.StartsWith("S-", StringComparison.OrdinalIgnoreCase) ? text : null;
        return new ComputerFacts
        {
            DistinguishedName = obj.DistinguishedName,
            Name = obj.GetString(Attr.Cn) ?? obj.Name,
            DnsHostName = obj.GetString(Attr.DnsHostName),
            SamAccountName = obj.GetString(Attr.SamAccountName),
            Sid = obj.ObjectSid,
            ObjectGuid = obj.ObjectGuid,
            AccountControl = uac,
            PrimaryGroupId = primaryGroup,
            Type = ComputerClassifier.Classify(uac, primaryGroup, os),
            OperatingSystem = os,
            OperatingSystemVersionText = versionText,
            Version = version,
            ServicePack = obj.GetString(Attr.OperatingSystemServicePack),
            LastLogonTimestamp = obj.GetFileTime(Attr.LastLogonTimestamp),
            PasswordLastSet = obj.GetFileTime(Attr.PwdLastSet),
            Created = obj.GetGeneralizedTime(Attr.WhenCreated),
            ManagedBy = obj.GetString(Attr.ManagedBy),
            CreatorSidValue = creator,
            Description = obj.GetString(Attr.Description),
            Location = obj.GetString(Attr.Location),
            ParentDn = obj.Dn.Parent is { IsRoot: false } parent ? parent.ToString() : null,
            LapsExpiration = windowsLaps ?? legacyLaps,
            LapsKind = windowsLaps is not null ? WindowsLapsExpiration : legacyLaps is not null ? LegacyLapsExpiration : null,
        };
    }

    private static string? SafeSid(byte[] bytes)
    {
        try
        {
            return AdConvert.SidToString(bytes);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
