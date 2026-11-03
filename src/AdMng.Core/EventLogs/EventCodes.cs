using System.Collections.Frozen;
using System.Globalization;
using System.Net;

namespace AdMng.Core.EventLogs;

/// <summary>The catalogs of <see cref="EventCodes"/>.</summary>
public enum EventCodeKind
{
    /// <summary>NTSTATUS of a logon or credential validation (4625 <c>Status</c>/<c>SubStatus</c>, 4776 <c>Status</c>).</summary>
    NtStatus,

    /// <summary>Kerberos result code (4768/4769/4771 <c>Status</c>, RFC 4120 error codes).</summary>
    KerberosResult,

    /// <summary>Kerberos ticket or session key encryption type (4768/4769 <c>TicketEncryptionType</c>).</summary>
    KerberosEncryptionType,

    /// <summary>Kerberos pre-authentication type (4768/4771 <c>PreAuthType</c>).</summary>
    KerberosPreAuthType,

    /// <summary>Logon type (4624/4625 <c>LogonType</c>).</summary>
    LogonType,

    /// <summary>The <c>%%23xx</c> failure reason of 4625 (<c>FailureReason</c>).</summary>
    FailureReason,
}

/// <summary>What a code means for the analyses (design note e-event-logs.md 4.2); texts are in the Presentation.</summary>
public enum EventCodeCategory
{
    /// <summary>Not a result: a type (logon, encryption, pre-authentication) without a finding.</summary>
    None,

    Success,

    /// <summary>The account does not exist (<c>0xC0000064</c>, Kerberos <c>0x6</c>).</summary>
    UnknownAccount,

    /// <summary>Wrong password (<c>0xC000006A</c>, Kerberos <c>0x18</c>).</summary>
    BadPassword,

    AccountLockedOut,

    AccountDisabled,

    AccountExpired,

    /// <summary>Disabled, expired or locked out – Kerberos does not say which (<c>0x12</c>).</summary>
    AccountRevoked,

    PasswordExpired,

    PasswordMustChange,

    /// <summary>Logon hours, workstation, logon type not granted, authentication firewall, KDC policy.</summary>
    LogonRestriction,

    /// <summary>The clocks differ too much (<c>0xC0000133</c>, Kerberos <c>0x25</c>).</summary>
    ClockSkew,

    /// <summary>No logon server, broken trust or secure channel, Netlogon not running.</summary>
    TrustFailure,

    /// <summary>DES or RC4 (an encryption type, not a failure).</summary>
    WeakEncryption,

    /// <summary>A TGT requested without pre-authentication (pre-authentication type 0, AS-REP roastable).</summary>
    NoPreAuthentication,

    /// <summary>A failure the catalog does not classify further (or an unknown code).</summary>
    Other,
}

/// <summary>
/// One entry of <see cref="EventCodes"/>: a code, its stable localization <see cref="Key"/> (the Presentation translates
/// it) and its <see cref="Category"/>. <see cref="IsKnown"/> is false for a code that is not in the catalog
/// (<see cref="EventCodes.Describe(EventCodeKind, long)"/>), whose key then has no translation.
/// </summary>
public sealed record EventCode(EventCodeKind Kind, long Value, EventCodeCategory Category)
{
    /// <summary>False for a code outside the catalog.</summary>
    public bool IsKnown { get; init; } = true;

    /// <summary>The localization key, e.g. <c>EventCode.NtStatus.C000006A</c> (see <see cref="EventCodes.KeyOf"/>).</summary>
    public string Key => EventCodes.KeyOf(Kind, Value);

    /// <summary>The code as the event shows it: <c>0xC000006A</c>, <c>0x18</c>, <c>0x12</c>, <c>2</c>, <c>10</c>, <c>%%2313</c>.</summary>
    public string Display => EventCodes.Format(Kind, Value);

    /// <summary>A result code (NTSTATUS, Kerberos result, failure reason) other than success.</summary>
    public bool IsFailure => (Kind is EventCodeKind.NtStatus or EventCodeKind.KerberosResult or EventCodeKind.FailureReason)
        && Category != EventCodeCategory.Success;

    public override string ToString() => Key;
}

/// <summary>
/// Catalogs of the codes in authentication events (design note e-event-logs.md 3): NTSTATUS logon codes, Kerberos
/// result codes, ticket encryption types, pre-authentication types, logon types and the 4625 failure reasons – data only,
/// each with a stable key the Presentation translates (<c>EventCode.NtStatus.C000006A</c>,
/// <c>EventCode.KerberosResult.0x18</c>, <c>EventCode.EncryptionType.0x17</c>, <c>EventCode.PreAuthType.2</c>,
/// <c>EventCode.LogonType.10</c>, <c>EventCode.FailureReason.2313</c>). Also the normalization of the values these
/// events carry: codes written as <c>0xC000006A</c>, <c>0xc000006a</c>, <c>C000006A</c>, <c>%%2313</c> or decimal, IP
/// addresses with the IPv4-mapped prefix (<c>::ffff:10.0.0.5</c>) and workstation names (<c>\\WAW-PC001</c>).
/// </summary>
public static class EventCodes
{
    /// <summary>The prefix of every key.</summary>
    public const string KeyPrefix = "EventCode.";

    public const long StatusSuccess = 0x0;
    public const long StatusNoLogonServers = 0xC000005E;
    public const long StatusNoSuchUser = 0xC0000064;
    public const long StatusWrongPassword = 0xC000006A;
    public const long StatusLogonFailure = 0xC000006D;
    public const long StatusAccountRestriction = 0xC000006E;
    public const long StatusInvalidLogonHours = 0xC000006F;
    public const long StatusInvalidWorkstation = 0xC0000070;
    public const long StatusPasswordExpired = 0xC0000071;
    public const long StatusAccountDisabled = 0xC0000072;
    public const long StatusTimeDifferenceAtDc = 0xC0000133;
    public const long StatusLogonTypeNotGranted = 0xC000015B;
    public const long StatusAccountExpired = 0xC0000193;
    public const long StatusPasswordMustChange = 0xC0000224;
    public const long StatusAccountLockedOut = 0xC0000234;
    public const long StatusAuthenticationFirewallFailed = 0xC0000413;

    public const long KerberosSuccess = 0x0;
    public const long KerberosClientUnknown = 0x6;
    public const long KerberosServerUnknown = 0x7;
    public const long KerberosPolicy = 0xC;
    public const long KerberosEncryptionTypeNotSupported = 0xE;
    public const long KerberosClientRevoked = 0x12;
    public const long KerberosKeyExpired = 0x17;
    public const long KerberosPreAuthFailed = 0x18;
    public const long KerberosClockSkew = 0x25;

    public const long EncryptionDesCbcCrc = 0x1;
    public const long EncryptionDesCbcMd5 = 0x3;
    public const long EncryptionAes128 = 0x11;
    public const long EncryptionAes256 = 0x12;
    public const long EncryptionRc4Hmac = 0x17;
    public const long EncryptionRc4HmacExp = 0x18;

    /// <summary>The encryption type of a failed request (no ticket issued).</summary>
    public const long EncryptionNone = 0xFFFFFFFF;

    private static readonly FrozenDictionary<(EventCodeKind Kind, long Value), EventCode> ByCode;

    static EventCodes()
    {
        NtStatuses =
        [
            Code(EventCodeKind.NtStatus, StatusSuccess, EventCodeCategory.Success),
            Code(EventCodeKind.NtStatus, StatusNoLogonServers, EventCodeCategory.TrustFailure),
            Code(EventCodeKind.NtStatus, StatusNoSuchUser, EventCodeCategory.UnknownAccount),
            Code(EventCodeKind.NtStatus, StatusWrongPassword, EventCodeCategory.BadPassword),
            Code(EventCodeKind.NtStatus, StatusLogonFailure, EventCodeCategory.Other),
            Code(EventCodeKind.NtStatus, StatusAccountRestriction, EventCodeCategory.LogonRestriction),
            Code(EventCodeKind.NtStatus, StatusInvalidLogonHours, EventCodeCategory.LogonRestriction),
            Code(EventCodeKind.NtStatus, StatusInvalidWorkstation, EventCodeCategory.LogonRestriction),
            Code(EventCodeKind.NtStatus, StatusPasswordExpired, EventCodeCategory.PasswordExpired),
            Code(EventCodeKind.NtStatus, StatusAccountDisabled, EventCodeCategory.AccountDisabled),
            Code(EventCodeKind.NtStatus, 0xC00000DC, EventCodeCategory.Other),               // SAM server in the wrong state
            Code(EventCodeKind.NtStatus, StatusTimeDifferenceAtDc, EventCodeCategory.ClockSkew),
            Code(EventCodeKind.NtStatus, StatusLogonTypeNotGranted, EventCodeCategory.LogonRestriction),
            Code(EventCodeKind.NtStatus, 0xC000018C, EventCodeCategory.TrustFailure),         // trust between domains failed
            Code(EventCodeKind.NtStatus, 0xC000018D, EventCodeCategory.TrustFailure),         // computer's secure channel failed
            Code(EventCodeKind.NtStatus, 0xC0000192, EventCodeCategory.TrustFailure),         // Netlogon not started
            Code(EventCodeKind.NtStatus, StatusAccountExpired, EventCodeCategory.AccountExpired),
            Code(EventCodeKind.NtStatus, StatusPasswordMustChange, EventCodeCategory.PasswordMustChange),
            Code(EventCodeKind.NtStatus, 0xC0000225, EventCodeCategory.Other),               // not found (Windows defect, no risk)
            Code(EventCodeKind.NtStatus, StatusAccountLockedOut, EventCodeCategory.AccountLockedOut),
            Code(EventCodeKind.NtStatus, 0xC00002EE, EventCodeCategory.Other),               // an error occurred during logon
            Code(EventCodeKind.NtStatus, 0xC0000371, EventCodeCategory.Other),               // local store has no secret for the account
            Code(EventCodeKind.NtStatus, 0xC0000380, EventCodeCategory.Other),               // smart card: wrong PIN
            Code(EventCodeKind.NtStatus, StatusAuthenticationFirewallFailed, EventCodeCategory.LogonRestriction),
        ];
        KerberosResults =
        [
            Code(EventCodeKind.KerberosResult, KerberosSuccess, EventCodeCategory.Success),
            Code(EventCodeKind.KerberosResult, 0x1, EventCodeCategory.AccountExpired),        // client's entry expired
            Code(EventCodeKind.KerberosResult, 0x2, EventCodeCategory.Other),                 // server's entry expired
            Code(EventCodeKind.KerberosResult, 0x3, EventCodeCategory.Other),                 // bad protocol version
            Code(EventCodeKind.KerberosResult, KerberosClientUnknown, EventCodeCategory.UnknownAccount),
            Code(EventCodeKind.KerberosResult, KerberosServerUnknown, EventCodeCategory.Other),
            Code(EventCodeKind.KerberosResult, 0x8, EventCodeCategory.Other),                 // principal not unique (duplicate SPN)
            Code(EventCodeKind.KerberosResult, 0x9, EventCodeCategory.Other),                 // no key for the principal
            Code(EventCodeKind.KerberosResult, KerberosPolicy, EventCodeCategory.LogonRestriction),
            Code(EventCodeKind.KerberosResult, 0xD, EventCodeCategory.Other),                 // bad option (delegation)
            Code(EventCodeKind.KerberosResult, KerberosEncryptionTypeNotSupported, EventCodeCategory.Other),
            Code(EventCodeKind.KerberosResult, 0xF, EventCodeCategory.Other),                 // checksum type not supported
            Code(EventCodeKind.KerberosResult, 0x10, EventCodeCategory.Other),                // pre-authentication data type not supported
            Code(EventCodeKind.KerberosResult, KerberosClientRevoked, EventCodeCategory.AccountRevoked),
            Code(EventCodeKind.KerberosResult, KerberosKeyExpired, EventCodeCategory.PasswordExpired),
            Code(EventCodeKind.KerberosResult, KerberosPreAuthFailed, EventCodeCategory.BadPassword),
            Code(EventCodeKind.KerberosResult, 0x19, EventCodeCategory.Other),                // pre-authentication required
            Code(EventCodeKind.KerberosResult, 0x1B, EventCodeCategory.Other),                // must use user-to-user
            Code(EventCodeKind.KerberosResult, 0x1F, EventCodeCategory.Other),                // integrity check failed
            Code(EventCodeKind.KerberosResult, 0x20, EventCodeCategory.Other),                // ticket expired
            Code(EventCodeKind.KerberosResult, KerberosClockSkew, EventCodeCategory.ClockSkew),
            Code(EventCodeKind.KerberosResult, 0x29, EventCodeCategory.Other),                // message modified
            Code(EventCodeKind.KerberosResult, 0x3C, EventCodeCategory.Other),                // generic error
            Code(EventCodeKind.KerberosResult, 0x3E, EventCodeCategory.Other),                // client certificate not trusted
            Code(EventCodeKind.KerberosResult, 0x3F, EventCodeCategory.Other),                // KDC certificate not trusted
            Code(EventCodeKind.KerberosResult, 0x40, EventCodeCategory.Other),                // invalid signature
            Code(EventCodeKind.KerberosResult, 0x41, EventCodeCategory.Other),                // key too weak
            Code(EventCodeKind.KerberosResult, 0x44, EventCodeCategory.Other),                // wrong realm
        ];
        KerberosEncryptionTypes =
        [
            Code(EventCodeKind.KerberosEncryptionType, EncryptionDesCbcCrc, EventCodeCategory.WeakEncryption),
            Code(EventCodeKind.KerberosEncryptionType, EncryptionDesCbcMd5, EventCodeCategory.WeakEncryption),
            Code(EventCodeKind.KerberosEncryptionType, EncryptionAes128, EventCodeCategory.None),
            Code(EventCodeKind.KerberosEncryptionType, EncryptionAes256, EventCodeCategory.None),
            Code(EventCodeKind.KerberosEncryptionType, EncryptionRc4Hmac, EventCodeCategory.WeakEncryption),
            Code(EventCodeKind.KerberosEncryptionType, EncryptionRc4HmacExp, EventCodeCategory.WeakEncryption),
            Code(EventCodeKind.KerberosEncryptionType, EncryptionNone, EventCodeCategory.None),
        ];
        KerberosPreAuthTypes =
        [
            Code(EventCodeKind.KerberosPreAuthType, 0, EventCodeCategory.NoPreAuthentication),
            Code(EventCodeKind.KerberosPreAuthType, 2, EventCodeCategory.None),               // PA-ENC-TIMESTAMP (password)
            Code(EventCodeKind.KerberosPreAuthType, 11, EventCodeCategory.None),              // PA-ETYPE-INFO
            Code(EventCodeKind.KerberosPreAuthType, 15, EventCodeCategory.None),              // PA-PK-AS-REP_OLD (smart card)
            Code(EventCodeKind.KerberosPreAuthType, 16, EventCodeCategory.None),              // PA-PK-AS-REQ (smart card)
            Code(EventCodeKind.KerberosPreAuthType, 17, EventCodeCategory.None),              // PA-PK-AS-REP (smart card)
            Code(EventCodeKind.KerberosPreAuthType, 19, EventCodeCategory.None),              // PA-ETYPE-INFO2
            Code(EventCodeKind.KerberosPreAuthType, 20, EventCodeCategory.None),              // PA-SVR-REFERRAL-INFO
            Code(EventCodeKind.KerberosPreAuthType, 138, EventCodeCategory.None),             // PA-ENCRYPTED-CHALLENGE (FAST)
        ];
        LogonTypes =
        [
            Code(EventCodeKind.LogonType, 2, EventCodeCategory.None),     // interactive
            Code(EventCodeKind.LogonType, 3, EventCodeCategory.None),     // network
            Code(EventCodeKind.LogonType, 4, EventCodeCategory.None),     // batch
            Code(EventCodeKind.LogonType, 5, EventCodeCategory.None),     // service
            Code(EventCodeKind.LogonType, 7, EventCodeCategory.None),     // unlock
            Code(EventCodeKind.LogonType, 8, EventCodeCategory.None),     // network cleartext
            Code(EventCodeKind.LogonType, 9, EventCodeCategory.None),     // new credentials
            Code(EventCodeKind.LogonType, 10, EventCodeCategory.None),    // remote interactive
            Code(EventCodeKind.LogonType, 11, EventCodeCategory.None),    // cached interactive
            Code(EventCodeKind.LogonType, 12, EventCodeCategory.None),    // cached remote interactive
            Code(EventCodeKind.LogonType, 13, EventCodeCategory.None),    // cached unlock
        ];
        FailureReasons =
        [
            Code(EventCodeKind.FailureReason, 2304, EventCodeCategory.Other),               // an error occurred during logon
            Code(EventCodeKind.FailureReason, 2305, EventCodeCategory.AccountExpired),
            Code(EventCodeKind.FailureReason, 2306, EventCodeCategory.TrustFailure),        // Netlogon not active
            Code(EventCodeKind.FailureReason, 2307, EventCodeCategory.AccountLockedOut),
            Code(EventCodeKind.FailureReason, 2308, EventCodeCategory.LogonRestriction),    // logon type not granted
            Code(EventCodeKind.FailureReason, 2309, EventCodeCategory.PasswordExpired),
            Code(EventCodeKind.FailureReason, 2310, EventCodeCategory.AccountDisabled),
            Code(EventCodeKind.FailureReason, 2311, EventCodeCategory.LogonRestriction),    // logon hours
            Code(EventCodeKind.FailureReason, 2312, EventCodeCategory.LogonRestriction),    // workstation
            Code(EventCodeKind.FailureReason, 2313, EventCodeCategory.Other),               // unknown user name or bad password
        ];

        var credentialValidation = new HashSet<long>
        {
            StatusSuccess, StatusNoSuchUser, StatusWrongPassword, StatusLogonFailure, StatusInvalidLogonHours, StatusInvalidWorkstation,
            StatusPasswordExpired, StatusAccountDisabled, StatusAccountExpired, StatusPasswordMustChange, StatusAccountLockedOut, 0xC0000371,
        };
        CredentialValidationStatuses = [.. NtStatuses.Where(c => credentialValidation.Contains(c.Value))];
        All = [.. NtStatuses, .. KerberosResults, .. KerberosEncryptionTypes, .. KerberosPreAuthTypes, .. LogonTypes, .. FailureReasons];
        ByCode = All.ToFrozenDictionary(c => (c.Kind, c.Value));
    }

    /// <summary>NTSTATUS codes of logons and credential validation (4625, 4776).</summary>
    public static IReadOnlyList<EventCode> NtStatuses { get; }

    /// <summary>The 4776 status codes (a subset of <see cref="NtStatuses"/>, same keys).</summary>
    public static IReadOnlyList<EventCode> CredentialValidationStatuses { get; }

    /// <summary>Kerberos result codes (4768, 4769, 4771).</summary>
    public static IReadOnlyList<EventCode> KerberosResults { get; }

    /// <summary>Ticket encryption types (4768, 4769).</summary>
    public static IReadOnlyList<EventCode> KerberosEncryptionTypes { get; }

    /// <summary>Pre-authentication types (4768, 4771).</summary>
    public static IReadOnlyList<EventCode> KerberosPreAuthTypes { get; }

    /// <summary>Logon types 2–13 (4624, 4625).</summary>
    public static IReadOnlyList<EventCode> LogonTypes { get; }

    /// <summary>The <c>%%2304</c>–<c>%%2313</c> failure reasons of 4625.</summary>
    public static IReadOnlyList<EventCode> FailureReasons { get; }

    /// <summary>Every catalog entry (each key once).</summary>
    public static IReadOnlyList<EventCode> All { get; }

    /// <summary>The catalog entry of <paramref name="value"/>, null when the code is not in the catalog.</summary>
    public static EventCode? Find(EventCodeKind kind, long value) => ByCode.GetValueOrDefault((kind, value));

    /// <summary>The catalog entry of a code as an event writes it (<see cref="TryParseCode"/>), null when it cannot be
    /// parsed or is not in the catalog.</summary>
    public static EventCode? Find(EventCodeKind kind, string? value) =>
        TryParseCode(value, out var code) ? Find(kind, code) : null;

    /// <summary>The catalog entry of <paramref name="value"/>, or an entry with <see cref="EventCode.IsKnown"/> false
    /// (category <see cref="EventCodeCategory.Other"/> for result codes, <see cref="EventCodeCategory.None"/> for types).</summary>
    public static EventCode Describe(EventCodeKind kind, long value) =>
        Find(kind, value) ?? new EventCode(kind, value, IsResultKind(kind) ? EventCodeCategory.Other : EventCodeCategory.None) { IsKnown = false };

    /// <summary><see cref="Describe(EventCodeKind, long)"/> of a code as an event writes it; null when it cannot be parsed.</summary>
    public static EventCode? Describe(EventCodeKind kind, string? value) =>
        TryParseCode(value, out var code) ? Describe(kind, code) : null;

    /// <summary>
    /// The result of an authentication record: 4625 <c>SubStatus</c> when <c>Status</c> is the generic
    /// <c>0xC000006D</c>/<c>0xC000006E</c> and a sub-status is given, else <c>Status</c> (NTSTATUS); 4776 <c>Status</c>
    /// (NTSTATUS); 4768, 4769, 4771 <c>Status</c> (Kerberos). Null for other events or when the code is missing or
    /// cannot be parsed.
    /// </summary>
    public static EventCode? ResultOf(EventLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        switch (record.EventId)
        {
            case 4625:
                var status = Describe(EventCodeKind.NtStatus, record.Get("Status"));
                if ((status?.Value is null or StatusLogonFailure or StatusAccountRestriction)
                    && Describe(EventCodeKind.NtStatus, record.Get("SubStatus")) is { Value: not StatusSuccess } sub)
                {
                    return sub;
                }

                return status;
            case 4776:
                return Describe(EventCodeKind.NtStatus, record.Get("Status"));
            case 4768 or 4769 or 4771:
                return Describe(EventCodeKind.KerberosResult, record.Get("Status"));
            default:
                return null;
        }
    }

    /// <summary>
    /// Parses a code as events and documents write it: <c>0xC000006A</c> / <c>0xc000006a</c> / <c>0x18</c> (hex),
    /// <c>%%2313</c> (insertion string, decimal), <c>C000006A</c> (hex digits with at least one letter), <c>10</c>
    /// (decimal). At most 64 bits; the bit pattern is kept (<c>0xFFFFFFFF</c> = 4294967295).
    /// </summary>
    public static bool TryParseCode(string? value, out long code)
    {
        code = 0;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 20)
        {
            return false;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseHex(text.AsSpan(2), out code);
        }

        if (text.StartsWith("%%", StringComparison.Ordinal))
        {
            return TryParseDecimal(text.AsSpan(2), out code);
        }

        if (text.All(char.IsAsciiDigit))
        {
            return TryParseDecimal(text, out code);
        }

        return text.All(char.IsAsciiHexDigit) && TryParseHex(text, out code);
    }

    /// <summary>The localization key of a code (also for codes outside the catalog).</summary>
    public static string KeyOf(EventCodeKind kind, long value) => kind switch
    {
        EventCodeKind.NtStatus => string.Create(CultureInfo.InvariantCulture, $"{KeyPrefix}NtStatus.{value:X8}"),
        EventCodeKind.KerberosResult => string.Create(CultureInfo.InvariantCulture, $"{KeyPrefix}KerberosResult.0x{value:X}"),
        EventCodeKind.KerberosEncryptionType => string.Create(CultureInfo.InvariantCulture, $"{KeyPrefix}EncryptionType.0x{value:X}"),
        EventCodeKind.KerberosPreAuthType => string.Create(CultureInfo.InvariantCulture, $"{KeyPrefix}PreAuthType.{value}"),
        EventCodeKind.LogonType => string.Create(CultureInfo.InvariantCulture, $"{KeyPrefix}LogonType.{value}"),
        EventCodeKind.FailureReason => string.Create(CultureInfo.InvariantCulture, $"{KeyPrefix}FailureReason.{value}"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The code as the events write it: <c>0xC000006A</c>, <c>0x18</c>, <c>0x17</c>, <c>2</c>, <c>10</c>, <c>%%2313</c>.</summary>
    public static string Format(EventCodeKind kind, long value) => kind switch
    {
        EventCodeKind.NtStatus => string.Create(CultureInfo.InvariantCulture, $"0x{value:X8}"),
        EventCodeKind.KerberosResult or EventCodeKind.KerberosEncryptionType => string.Create(CultureInfo.InvariantCulture, $"0x{value:X}"),
        EventCodeKind.KerberosPreAuthType or EventCodeKind.LogonType => value.ToString(CultureInfo.InvariantCulture),
        EventCodeKind.FailureReason => string.Create(CultureInfo.InvariantCulture, $"%%{value}"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// An IP address from an event (<c>IpAddress</c>): trimmed, the IPv4-mapped prefix removed (<c>::ffff:10.0.0.5</c> →
    /// <c>10.0.0.5</c>); null for empty values and <c>-</c>. Otherwise unchanged (the same rule as the Watchdog's logon
    /// source resolver).
    /// </summary>
    public static string? NormalizeIpAddress(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text == "-")
        {
            return null;
        }

        return text.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase) && text.Contains('.', StringComparison.Ordinal)
            ? text[7..]
            : text;
    }

    /// <summary>True for a loopback address (<c>127.0.0.1</c>, <c>::1</c>, also IPv4-mapped): the logon came from the
    /// computer itself.</summary>
    public static bool IsLocalAddress(string? value) =>
        NormalizeIpAddress(value) is { } address && IPAddress.TryParse(address, out var ip) && IPAddress.IsLoopback(ip);

    /// <summary>
    /// A computer name the client supplied (4776 <c>Workstation</c>, 4625 <c>WorkstationName</c>, the 4740 caller in
    /// <c>TargetDomainName</c>): trimmed, without leading <c>\\</c> and a trailing dot; null for empty values and
    /// <c>-</c>. The case is kept (compare with <see cref="EventHostNames.SameComputer"/>).
    /// </summary>
    public static string? NormalizeWorkstation(string? value)
    {
        var text = value?.Trim().TrimStart('\\').TrimEnd('.').Trim();
        return string.IsNullOrEmpty(text) || text == "-" ? null : text;
    }

    private static bool IsResultKind(EventCodeKind kind) =>
        kind is EventCodeKind.NtStatus or EventCodeKind.KerberosResult or EventCodeKind.FailureReason;

    private static EventCode Code(EventCodeKind kind, long value, EventCodeCategory category) => new(kind, value, category);

    private static bool TryParseHex(ReadOnlySpan<char> digits, out long code)
    {
        code = 0;
        if (digits.IsEmpty || digits.Length > 16
            || !ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        code = unchecked((long)number);
        return true;
    }

    private static bool TryParseDecimal(ReadOnlySpan<char> digits, out long code) =>
        long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out code);
}
