using System.Globalization;
using System.Net;

namespace AdMng.Core.EventLogs.Analysis;

/// <summary>
/// The event IDs the analyses read and the classification of a record (design note e-event-logs.md 4), on top of the
/// public code catalog: the result code of a record is <see cref="EventCodes.ResultOf"/> and what it means is
/// <see cref="EventCode.FailureKind"/> – one classification of NTSTATUS and Kerberos codes for the reader and the analyses.
/// </summary>
internal static class AnalysisCodes
{
    public const string SecurityChannel = "Security";

    public const int LogonSucceeded = 4624;
    public const int LogonFailed = 4625;
    public const int AccountLockedOut = 4740;
    public const int AccountUnlocked = 4767;
    public const int TicketGrantingTicket = 4768;
    public const int PreAuthenticationFailed = 4771;
    public const int CredentialValidation = 4776;

    /// <summary>The NULL SID 4625 carries as <c>TargetUserSid</c> on failures.</summary>
    public const string NullSid = "S-1-0-0";

    /// <summary>
    /// The failure kind and the code of <paramref name="record"/> (<see cref="LogonFailureKind.None"/> and null for a
    /// success): the result of <see cref="EventCodes.ResultOf"/> – for 4625 the sub-status behind the generic
    /// <c>0xC000006D</c> / <c>0xC000006E</c> – with its <see cref="EventCode.FailureKind"/> (a failure whose code says
    /// success counts as <see cref="LogonFailureKind.Other"/>) and its <see cref="EventCode.Display"/>.
    /// </summary>
    public static (LogonFailureKind Kind, string? Code) Classify(EventLogRecord record)
    {
        if (!IsFailure(record))
        {
            return (LogonFailureKind.None, null);
        }

        if (EventCodes.ResultOf(record) is not { } result)
        {
            return (LogonFailureKind.Other, null);
        }

        return (result.FailureKind == LogonFailureKind.None ? LogonFailureKind.Other : result.FailureKind, result.Display);
    }

    /// <summary>
    /// The record is a failed attempt: 4625 and 4771 always; 4768/4776 by the audit keyword, else by a non-zero status;
    /// any other event by the audit failure keyword.
    /// </summary>
    public static bool IsFailure(EventLogRecord record) => record.EventId switch
    {
        LogonFailed or PreAuthenticationFailed => true,
        LogonSucceeded => false,
        TicketGrantingTicket or CredentialValidation => record.IsAuditFailure
            || (!record.IsAuditSuccess && EventCodes.ResultOf(record) is { IsFailure: true }),
        _ => record.IsAuditFailure,
    };

    /// <summary>The record is a successful logon or ticket (4624; 4768/4776 that are not failures).</summary>
    public static bool IsSuccess(EventLogRecord record) => record.EventId switch
    {
        LogonSucceeded => true,
        TicketGrantingTicket or CredentialValidation => !IsFailure(record),
        LogonFailed or PreAuthenticationFailed => false,
        _ => record.IsAuditSuccess,
    };

    /// <summary>A password was tried: wrong, or rejected because the account is locked (what a guessing source produces).</summary>
    public static bool IsPasswordAttempt(LogonFailureKind kind) =>
        kind is LogonFailureKind.BadPassword or LogonFailureKind.AccountLocked or LogonFailureKind.AccountRevoked;

    /// <summary>
    /// <see cref="IsPasswordAttempt(LogonFailureKind)"/> for a record of <paramref name="eventId"/>, except a 4768 revoked
    /// (Kerberos <c>0x12</c>): the KDC answers it for a disabled, expired or locked account before any password is checked –
    /// a disabled computer that keeps asking for tickets is not guessing.
    /// </summary>
    public static bool IsPasswordAttempt(int eventId, LogonFailureKind kind) =>
        IsPasswordAttempt(kind) && !(eventId == TicketGrantingTicket && kind == LogonFailureKind.AccountRevoked);

    /// <summary>The answer says the account is locked (NTLM) or revoked (Kerberos – locked, disabled or expired).</summary>
    public static bool IsWhileLocked(LogonFailureKind kind) => kind is LogonFailureKind.AccountLocked or LogonFailureKind.AccountRevoked;
}

/// <summary>Normalization of the values the analyses compare (accounts, computers, addresses, hosts).</summary>
internal static class AnalysisValues
{
    /// <summary>An address as compared: <see cref="EventCodes.NormalizeIpAddress"/> (trimmed, the IPv4-mapped prefix
    /// stripped; null for none or <c>-</c>) in lower case.</summary>
    public static string? Address(string? value) => EventCodes.NormalizeIpAddress(value)?.ToLowerInvariant();

    /// <summary>A loopback or unspecified address (<c>::1</c>, <c>127.0.0.1</c>, <c>::</c>, <c>0.0.0.0</c>): the attempt
    /// came from the logging computer (<see cref="EventCodes.IsLocalAddress"/>, <see cref="EventCodes.IsUnspecifiedAddress"/>).</summary>
    public static bool IsLocalAddress(string address) => EventCodes.IsLocalAddress(address) || EventCodes.IsUnspecifiedAddress(address);

    /// <summary>A computer name as compared: <see cref="EventCodes.NormalizeWorkstation"/> (trimmed, without leading
    /// backslashes and a trailing dot) without a trailing <c>$</c>, upper case; null for none or <c>-</c>.</summary>
    public static string? Computer(string? value) =>
        EventCodes.NormalizeWorkstation(value)?.TrimEnd('$') is { Length: > 0 } name ? name.ToUpperInvariant() : null;

    /// <summary>The first label of a computer or host name.</summary>
    public static string ShortName(string name)
    {
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>An account name as logged without a <c>DOMAIN\</c> prefix, trimmed; null for none or <c>-</c>.</summary>
    public static string? AccountDisplay(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text == "-")
        {
            return null;
        }

        var slash = text.LastIndexOf('\\');
        if (slash >= 0)
        {
            text = text[(slash + 1)..].Trim();
        }

        return text.Length == 0 ? null : text;
    }

    /// <summary>The grouping key of an account name: <see cref="AccountDisplay"/> in lower case.</summary>
    public static string? AccountKey(string? value) => AccountDisplay(value)?.ToLowerInvariant();

    /// <summary>A host name as compared: trimmed, without a trailing dot, lower case.</summary>
    public static string Host(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>A time argument of a note: ISO 8601 round-trip, invariant.</summary>
    public static string Time(DateTimeOffset time) => time.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>A number argument of a note, invariant.</summary>
    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>*</c> and <c>?</c> wildcards, case-insensitive (invariant).</summary>
    public static bool WildcardMatch(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    /// <summary>
    /// A value a structured query can carry as one XPath literal: no control character and not both quote kinds (the
    /// rule of the query builder, CLAUDE.md rule 3; the builder checks again).
    /// </summary>
    public static bool IsQueryable(string value) =>
        value.Length > 0 && !value.Any(char.IsControl) && !(value.Contains('\'', StringComparison.Ordinal) && value.Contains('"', StringComparison.Ordinal));
}

/// <summary>The source of one attempt and the identifiers behind it (an address and a workstation name may both be known).</summary>
internal readonly record struct ParsedSource(AttemptSource Source, string? Address, string? Workstation)
{
    /// <summary>
    /// 4740: the caller computer; 4771/4768: the address (loopback → local); 4776: the workstation; 4625/4624 and others:
    /// a non-loopback address, else the workstation, else local (a loopback address always means local).
    /// </summary>
    public static ParsedSource Of(EventLogRecord record)
    {
        switch (record.EventId)
        {
            case AnalysisCodes.AccountLockedOut:
                return AnalysisValues.Computer(record.Get("TargetDomainName")) is { } caller
                    ? new ParsedSource(new AttemptSource(AttemptSourceKind.CallerComputer, caller), null, caller)
                    : new ParsedSource(new AttemptSource(AttemptSourceKind.Unknown, string.Empty), null, null);
            case AnalysisCodes.TicketGrantingTicket or AnalysisCodes.PreAuthenticationFailed:
            {
                var address = AnalysisValues.Address(record.Get("IpAddress"));
                if (address is null)
                {
                    return new ParsedSource(new AttemptSource(AttemptSourceKind.Unknown, string.Empty), null, null);
                }

                return AnalysisValues.IsLocalAddress(address)
                    ? new ParsedSource(new AttemptSource(AttemptSourceKind.Local, AnalysisValues.Host(record.Host)), address, null)
                    : new ParsedSource(new AttemptSource(AttemptSourceKind.IpAddress, address), address, null);
            }

            case AnalysisCodes.CredentialValidation:
                return AnalysisValues.Computer(record.Get("Workstation")) is { } client
                    ? new ParsedSource(new AttemptSource(AttemptSourceKind.Workstation, client), null, client)
                    : new ParsedSource(new AttemptSource(AttemptSourceKind.Unknown, string.Empty), null, null);
            default:
            {
                var address = AnalysisValues.Address(record.Get("IpAddress"));
                var workstation = AnalysisValues.Computer(record.Get("WorkstationName") ?? record.Get("Workstation"));
                if (address is not null && AnalysisValues.IsLocalAddress(address))
                {
                    return new ParsedSource(new AttemptSource(AttemptSourceKind.Local, AnalysisValues.Host(record.Host)), address, workstation);
                }

                if (address is not null)
                {
                    return new ParsedSource(new AttemptSource(AttemptSourceKind.IpAddress, address), address, workstation);
                }

                return workstation is not null
                    ? new ParsedSource(new AttemptSource(AttemptSourceKind.Workstation, workstation), null, workstation)
                    : new ParsedSource(new AttemptSource(AttemptSourceKind.Local, AnalysisValues.Host(record.Host)), null, null);
            }
        }
    }

    public override string ToString() => Source.ToString();
}

/// <summary>Domain controllers by short name and address – to recognize attempts a DC relayed or made.</summary>
internal sealed class DomainControllerNames
{
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _addresses = new(StringComparer.OrdinalIgnoreCase);

    public DomainControllerNames(IEnumerable<string> namesOrAddresses)
    {
        foreach (var value in namesOrAddresses)
        {
            Add(value);
        }
    }

    public bool IsEmpty => _names.Count == 0 && _addresses.Count == 0;

    public void Add(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var text = value.Trim().TrimEnd('.');
        if (IPAddress.TryParse(text, out _))
        {
            if (AnalysisValues.Address(text) is { } address)
            {
                _addresses.Add(address);
            }
        }
        else if (AnalysisValues.Computer(text) is { } name)
        {
            _names.Add(AnalysisValues.ShortName(name));
        }
    }

    /// <summary>The source names or is a domain controller (computer kinds by short name, addresses exactly).</summary>
    public bool Contains(AttemptSource source) => source.Kind switch
    {
        AttemptSourceKind.CallerComputer or AttemptSourceKind.Workstation => _names.Contains(AnalysisValues.ShortName(source.Value)),
        AttemptSourceKind.IpAddress => _addresses.Contains(source.Value),
        _ => false,
    };

    public override string ToString() => "domain controller names";
}
