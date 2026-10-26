using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;
using AdMng.Core.Templates;

namespace AdMng.Core.Messaging;

/// <summary>What the fields of one message are computed from.</summary>
public sealed record UserMessageFieldData
{
    /// <summary>The account, read with the attributes the template references.</summary>
    public required DirectoryObject Account { get; init; }

    /// <summary>The account state at the plan time (U2).</summary>
    public required AccountStatus Status { get; init; }

    /// <summary>The manager (<c>{$manager:attribute}</c>), if any.</summary>
    public DirectoryObject? Manager { get; init; }

    public MessageLanguage Language { get; init; }

    /// <summary>Time zone of the dates (Europe/Warsaw by default, see <see cref="UserMessageFields.DefaultTimeZoneId"/>).</summary>
    public required TimeZoneInfo TimeZone { get; init; }

    /// <summary>Values typed by the operator (<c>{$input:name}</c>), by name (case-insensitive).</summary>
    public IReadOnlyDictionary<string, string> Inputs { get; init; } = new Dictionary<string, string>();

    /// <summary>DNS domain ({$domain}).</summary>
    public string? Domain { get; init; }
}

/// <summary>
/// The fields of user messages: everything the batch editor's template engine offers (<c>{givenName}</c>,
/// <c>{sAMAccountName|lower}</c>, <c>{$domain}</c> …) plus computed values of the account state, all dates in the message
/// language and the message time zone:
/// <list type="bullet">
/// <item><c>{$passwordExpires}</c> / <c>{$passwordExpires:format}</c>, <c>{$passwordExpiresInDays}</c> – from
/// <c>msDS-UserPasswordExpiryTimeComputed</c> (FGPP); empty for "never expires" and "must change".</item>
/// <item><c>{$accountExpires:format}</c> (the last valid day), <c>{$accountExpiresInDays}</c>.</item>
/// <item><c>{$lastLogon:format}</c> (lastLogonTimestamp), <c>{$lockedSince:format}</c>, <c>{$today:format}</c>.</item>
/// <item><c>{$manager:attribute}</c> – an attribute of the manager, e.g. <c>{$manager:displayName}</c>.</item>
/// <item><c>{$input:name}</c> – a value typed by the operator when sending (one for all recipients).</item>
/// </list>
/// A date without a format uses <see cref="DefaultDateFormat"/> ("5 listopada 2026"). Thread-safe.
/// </summary>
public sealed class UserMessageFields
{
    public const string PasswordExpires = "passwordExpires";
    public const string PasswordExpiresInDays = "passwordExpiresInDays";
    public const string AccountExpires = "accountExpires";
    public const string AccountExpiresInDays = "accountExpiresInDays";
    public const string LastLogon = "lastLogon";
    public const string LockedSince = "lockedSince";
    public const string Today = "today";
    public const string Manager = "manager";
    public const string Input = "input";

    /// <summary>Format of dates without an explicit one (with genitive month names in Polish).</summary>
    public const string DefaultDateFormat = "d MMMM yyyy";

    /// <summary>The time zone of message dates unless the settings say otherwise.</summary>
    public const string DefaultTimeZoneId = "Europe/Warsaw";

    /// <summary>The extension tokens of messages.</summary>
    public static IReadOnlyList<TemplateExtensionToken> Tokens { get; } =
    [
        new(PasswordExpires, TemplateTokenArgument.Optional, IsDateFormat: true),
        new(PasswordExpiresInDays, TemplateTokenArgument.None),
        new(AccountExpires, TemplateTokenArgument.Optional, IsDateFormat: true),
        new(AccountExpiresInDays, TemplateTokenArgument.None),
        new(LastLogon, TemplateTokenArgument.Optional, IsDateFormat: true),
        new(LockedSince, TemplateTokenArgument.Optional, IsDateFormat: true),
        new(Today, TemplateTokenArgument.Optional, IsDateFormat: true),
        new(Manager, TemplateTokenArgument.Required),
        new(Input, TemplateTokenArgument.Required),
    ];

    /// <summary>Field examples for the editor's "insert field" list.</summary>
    public static IReadOnlyList<string> Examples { get; } =
    [
        "{givenName}", "{sn}", "{displayName}", "{sAMAccountName}", "{userPrincipalName}", "{mail}", "{department}",
        "{$passwordExpires:d MMMM yyyy}", "{$passwordExpiresInDays}", "{$accountExpires:d MMMM yyyy}", "{$accountExpiresInDays}",
        "{$lastLogon:d MMMM yyyy}", "{$lockedSince:d MMMM yyyy, HH:mm}", "{$today:d MMMM yyyy}", "{$manager:displayName}",
        "{$manager:mail}", "{$input:termin}",
    ];

    /// <summary>The template engine with the message tokens (separate from the batch editor's instance).</summary>
    public TemplateEngine Engine { get; } = new(Tokens);

    /// <summary>The time zone of <paramref name="id"/> (IANA or Windows id); Central European time or the local zone as fallbacks.</summary>
    public static TimeZoneInfo ResolveTimeZone(string? id)
    {
        foreach (var candidate in new[] { id, DefaultTimeZoneId, "Central European Standard Time" })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && TimeZoneInfo.TryFindSystemTimeZoneById(candidate.Trim(), out var zone))
            {
                return zone;
            }
        }

        return TimeZoneInfo.Local;
    }

    /// <summary>Renders <paramref name="template"/> for one account.</summary>
    /// <param name="template">Subject, text or HTML with fields.</param>
    /// <param name="data">Account, state, manager, language, inputs.</param>
    /// <param name="encode">Applied to every field value (HTML encoding, header-safe text), or null.</param>
    /// <param name="emptyField">Called with the source of every field that rendered empty.</param>
    /// <exception cref="FormatException">The template is invalid.</exception>
    public string Render(string template, UserMessageFieldData data, Func<string, string>? encode = null, Action<string>? emptyField = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(data);
        var context = new TemplateContext
        {
            Attribute = a => data.Account.GetStrings(a),
            DistinguishedName = data.Account.DistinguishedName,
            Domain = data.Domain,
            Now = data.Status.Now,
            Extension = (name, argument) => Evaluate(name, argument, data),
            Encode = encode,
            EmptyValue = emptyField,
        };
        return Engine.Render(template, context);
    }

    /// <summary>The value of one extension token.</summary>
    public static string Evaluate(string name, string? argument, UserMessageFieldData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var culture = MessageLanguages.Culture(data.Language);
        var status = data.Status;
        return name switch
        {
            PasswordExpires => status.Password is PasswordState.Expires or PasswordState.Expired
                ? Date(status.PasswordExpiresAt, argument, data.TimeZone, culture)
                : string.Empty,
            PasswordExpiresInDays => status.Password == PasswordState.Expires && status.PasswordDaysLeft is { } left
                ? left.ToString(culture)
                : string.Empty,
            AccountExpires => status.AccountLastDay is { } last
                ? last.ToDateTime(TimeOnly.MinValue).ToString(argument ?? DefaultDateFormat, culture)
                : string.Empty,
            AccountExpiresInDays => status.AccountDaysLeft is { } accountLeft && accountLeft >= 0 ? accountLeft.ToString(culture) : string.Empty,
            LastLogon => Date(status.LastLogon, argument, data.TimeZone, culture),
            LockedSince => Date(status.LockedSince, argument, data.TimeZone, culture),
            Today => Date(status.Now, argument, data.TimeZone, culture),
            Manager => argument is { Length: > 0 } attribute && data.Manager is { } manager
                ? manager.GetString(attribute.Trim()) ?? string.Empty
                : string.Empty,
            Input => argument is { Length: > 0 } input && FindInput(data.Inputs, input) is { } value ? value : string.Empty,
            _ => string.Empty,
        };
    }

    private static string? FindInput(IReadOnlyDictionary<string, string> inputs, string name)
    {
        var key = name.Trim();
        return inputs.TryGetValue(key, out var exact)
            ? exact
            : inputs.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static string Date(DateTimeOffset? instant, string? format, TimeZoneInfo timeZone, CultureInfo culture) =>
        instant is { } value ? TimeZoneInfo.ConvertTime(value, timeZone).DateTime.ToString(format ?? DefaultDateFormat, culture) : string.Empty;
}
