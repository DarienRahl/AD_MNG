using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Core.Model;

/// <summary>
/// The service's own computer account (stage B design 9.3): the sAMAccountName of the bootstrap principal
/// (<c>ADMNG-WD$</c>) and, once the own-account monitor resolved it, its objectGUID and SID. An object is the account
/// when its GUID matches; while the GUID is unknown, when its SID or sAMAccountName matches (a renamed account keeps
/// matching by GUID, a re-created one by name).
/// </summary>
public sealed record WatchdogAccountIdentity
{
    public required string SamAccountName { get; init; }
    public Guid? ObjectGuid { get; init; }
    public string? Sid { get; init; }

    /// <summary>The sAMAccountName of a Kerberos principal (<c>ADMNG-WD$@CONTOSO.COM</c> → <c>ADMNG-WD$</c>); null when
    /// empty.</summary>
    public static string? SamAccountNameOf(string? principal)
    {
        if (string.IsNullOrWhiteSpace(principal))
        {
            return null;
        }

        var trimmed = principal.Trim();
        var at = trimmed.LastIndexOf('@');
        var name = at >= 0 ? trimmed[..at] : trimmed;
        return name.Length == 0 ? null : name;
    }

    /// <summary>True when an object with these identifiers is the own account – or carries its name (a re-created or
    /// renamed-to account is as alarming as a change of the real one).</summary>
    public bool Matches(Guid? objectGuid, string? sid, string? samAccountName) =>
        (ObjectGuid is { } guid && objectGuid == guid)
        || (Sid is { } own && sid is not null && string.Equals(own, sid, StringComparison.OrdinalIgnoreCase))
        || (samAccountName is not null && string.Equals(samAccountName.Trim(), SamAccountName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The keytab tool's <c>status.json</c> as far as the <c>watchdog-self</c> exclusion needs it (no secrets): key version,
/// time of the last change and the result (<see cref="KeytabStatusResults"/>).
/// </summary>
public sealed record KeytabRotationRecord(int? Kvno, DateTimeOffset? LastChange, string? Result);
