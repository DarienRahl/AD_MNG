using System.Globalization;

namespace AdMng.Watchdog.Core.Model;

/// <summary>A parsed Security-log record (values of secret attributes already masked).</summary>
public sealed record SecurityEvent
{
    public required string Source { get; init; }               // DC FQDN or collector (cursor key)
    public required string Computer { get; init; }             // System/Computer – the DC that logged it
    public required long RecordId { get; init; }
    public required int EventCode { get; init; }
    public int Version { get; init; }
    public required DateTimeOffset TimeCreated { get; init; }  // UTC
    /// <summary>EventData by Data/@Name; "-" and empty values omitted; %% codes decoded where documented.</summary>
    public IReadOnlyDictionary<string, string> Data { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The EventData value of <paramref name="name"/>, or null when absent.</summary>
    public string? Get(string name) => Data.TryGetValue(name, out var value) ? value : null;

    public string? SubjectSid => Get("SubjectUserSid");

    /// <summary>SubjectDomainName\SubjectUserName (just the user name when the domain is absent); null without a user.</summary>
    public string? SubjectAccount
    {
        get
        {
            var user = Get("SubjectUserName");
            if (string.IsNullOrEmpty(user))
            {
                return null;
            }

            var domain = Get("SubjectDomainName");
            return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
        }
    }

    /// <summary>SubjectLogonId normalized to "0x…" lower-case hex without leading zeros; null when absent or not hex.</summary>
    public string? SubjectLogonId
    {
        get
        {
            var value = Get("SubjectLogonId")?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
            return ulong.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var logonId)
                ? "0x" + logonId.ToString("x", CultureInfo.InvariantCulture)
                : null;
        }
    }

    public string? TargetSid => Get("TargetSid");

    /// <summary>51xx ObjectGUID "{…}".</summary>
    public Guid? ObjectGuid => Guid.TryParse(Get("ObjectGUID"), out var guid) ? guid : null;

    /// <summary>51xx OpCorrelationID "{…}" (one LDAP operation, e.g. the Value Deleted / Value Added pair).</summary>
    public Guid? OpCorrelationId => Guid.TryParse(Get("OpCorrelationID"), out var guid) ? guid : null;
}
