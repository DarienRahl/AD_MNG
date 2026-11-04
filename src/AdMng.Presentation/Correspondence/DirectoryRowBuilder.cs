using AdMng.Core.ActiveDirectory;
using AdMng.Core.Correspondence.Fields;
using AdMng.Core.Correspondence.Import;
using AdMng.Core.Directory;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Salutations;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Core.Templates;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Settings;

namespace AdMng.Presentation.Correspondence;

/// <summary>Why a <see cref="DirectoryFieldDefinition"/> cannot be used.</summary>
internal enum DirectoryFieldProblem
{
    None,

    /// <summary>The name is blank.</summary>
    EmptyName,

    /// <summary>The name is longer than <see cref="FieldTemplate.MaxFieldNameLength"/> or holds braces or '|'.</summary>
    InvalidName,

    /// <summary>Another definition of the same target has the same name (<see cref="FieldKey"/>).</summary>
    DuplicateName,

    /// <summary>The expression is blank.</summary>
    EmptyExpression,

    /// <summary>The expression is no valid U1 template (detail: the engine's English diagnostic).</summary>
    Syntax,

    /// <summary>The expression reads a secret attribute (detail: the attribute).</summary>
    Forbidden,

    /// <summary>The expression uses a token that has no value here – <c>{$manager:…}</c>, <c>{$input:…}</c> and the
    /// manager salutations (detail: the token name).</summary>
    Unsupported,
}

/// <summary>What the fields of directory rows are evaluated with (one window, one moment).</summary>
/// <param name="Now">The reference time of dates (<c>{$lastLogon}</c>, <c>{$today}</c> …).</param>
/// <param name="TimeZone">The time zone of dates (the U1 setting).</param>
/// <param name="Salutations">The salutation options of the U1 settings (<c>{$salutation}</c>, <c>{$honorific}</c> …).</param>
/// <param name="Domain">The DNS domain (<c>{$domain}</c>).</param>
/// <param name="Schema">The session's schema (secret and optional attributes).</param>
internal sealed record DirectoryRowContext(DateTimeOffset Now, TimeZoneInfo TimeZone, SalutationOptions Salutations, string? Domain, ISchemaCatalog? Schema)
{
    public override string ToString() => nameof(DirectoryRowContext);
}

/// <summary>One object of the directory as a recipient: address, name, validity and the field values.</summary>
/// <param name="Email">The normalized address, or "" when the object has none.</param>
/// <param name="Name">displayName, else the RDN value.</param>
/// <param name="DistinguishedName">The object.</param>
/// <param name="Fields">The values of the field definitions by field name.</param>
internal sealed record DirectoryRow(string Email, string Name, string DistinguishedName, IReadOnlyDictionary<string, string> Fields)
{
    /// <summary>The object has a valid address.</summary>
    public bool IsValid => Email.Length > 0;

    public override string ToString() => nameof(DirectoryRow);
}

/// <summary>
/// Turns directory objects into recipient rows (design note 6.4, decision D2): every <see cref="DirectoryFieldDefinition"/>
/// of a target is a U1 template expression evaluated with <see cref="UserMessageFields.Render"/> for the object (its
/// <see cref="AccountStatus"/>, the U1 salutation options, Polish), the address is <c>mail</c> else the primary SMTP proxy
/// address, the name <c>displayName</c> else the RDN. Definitions that do not validate are left out. Pure and thread-safe
/// (the engine is); the values exist in memory only (rule 1).
/// </summary>
internal sealed class DirectoryRowBuilder
{
    private readonly UserMessageFields _fields;
    private readonly DirectoryRowContext _context;

    /// <param name="fields">The U1 field engine.</param>
    /// <param name="definitions">The field definitions (invalid ones are skipped).</param>
    /// <param name="context">Time, time zone, salutations, domain and schema.</param>
    public DirectoryRowBuilder(UserMessageFields fields, IEnumerable<DirectoryFieldDefinition> definitions, DirectoryRowContext context)
    {
        _fields = fields ?? throw new ArgumentNullException(nameof(fields));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        ArgumentNullException.ThrowIfNull(definitions);
        var valid = new List<DirectoryFieldDefinition>();
        foreach (var definition in definitions)
        {
            if (definition is not null && Check(fields, definition, context.Schema).Problem == DirectoryFieldProblem.None
                && !valid.Any(d => d.Target == definition.Target && FieldKey.Same(d.Name, definition.Name)))
            {
                valid.Add(definition with { Name = definition.Name.Trim(), Expression = definition.Expression.Trim() });
            }
        }

        Definitions = valid;
    }

    /// <summary>
    /// The context of a window: now, the U1 time zone and salutation options of the settings, the session's domain and
    /// schema.
    /// </summary>
    public static DirectoryRowContext ContextFor(DialogServices services, IDirectorySession session)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(session);
        var settings = services.Settings.Current.UserMessages;
        return new DirectoryRowContext(
            services.Time.GetUtcNow(),
            UserMessageFields.ResolveTimeZone(settings.TimeZone),
            settings.Salutations.ToOptions(),
            session.Info.DomainDnsName,
            session.Schema);
    }

    /// <summary>The attributes every row read needs: the U1 account attributes (state, names, addresses, objectClass).</summary>
    public static IReadOnlyList<string> BaseAttributes { get; } = UserMessagePlanner.BaseAccountAttributes;

    /// <summary>The definitions evaluated (valid ones, first of a name per target).</summary>
    public IReadOnlyList<DirectoryFieldDefinition> Definitions { get; }

    /// <summary>The field names of <paramref name="target"/> in definition order.</summary>
    public IReadOnlyList<string> FieldNames(DirectoryFieldTarget target) =>
        [.. Definitions.Where(d => d.Target == target).Select(d => d.Name)];

    /// <summary>
    /// The attributes to read for objects of <paramref name="target"/>: <see cref="BaseAttributes"/>, the attributes the
    /// expressions reference and the salutation attributes when a salutation token is used – secret attributes never.
    /// </summary>
    public IReadOnlyList<string> AttributesFor(DirectoryFieldTarget target)
    {
        var parsed = Definitions.Where(d => d.Target == target).Select(d => _fields.Engine.Parse(d.Expression)).ToList();
        return
        [
            .. BaseAttributes
                .Concat(parsed.SelectMany(p => p.ReferencedAttributes))
                .Concat(SalutationFields.AccountAttributes(parsed.SelectMany(p => p.SpecialTokens), _context.Salutations))
                .Where(a => !string.IsNullOrWhiteSpace(a) && !UserMessageRules.IsForbiddenAttribute(a, _context.Schema))
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>The values of the definitions of <paramref name="target"/> for <paramref name="account"/> (empty on failure).</summary>
    public IReadOnlyDictionary<string, string> Evaluate(DirectoryObject account, DirectoryFieldTarget target)
    {
        ArgumentNullException.ThrowIfNull(account);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var definitions = Definitions.Where(d => d.Target == target).ToList();
        if (definitions.Count == 0)
        {
            return values;
        }

        var data = new UserMessageFieldData
        {
            Account = account,
            Status = AccountStatus.Evaluate(account, _context.Now, _context.TimeZone),
            TimeZone = _context.TimeZone,
            Language = MessageLanguage.Polish,
            Domain = _context.Domain,
            Salutation = new MessageSalutations(account, null, _context.Salutations, schema: _context.Schema, language: MessageLanguage.Polish),
        };
        foreach (var definition in definitions)
        {
            string value;
            try
            {
                value = _fields.Render(definition.Expression, data);
            }
            catch (FormatException)
            {
                value = string.Empty;
            }

            values[definition.Name] = value.Trim();
        }

        return values;
    }

    /// <summary>The object as a recipient: address, name and the values of the person definitions.</summary>
    public DirectoryRow Recipient(DirectoryObject account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return new DirectoryRow(AddressOf(account) ?? string.Empty, NameOf(account), account.DistinguishedName,
            Evaluate(account, DirectoryFieldTarget.Person));
    }

    /// <summary><c>mail</c> when it is a valid address, else the primary <c>SMTP:</c> proxy address when valid; normalized.</summary>
    public static string? AddressOf(DirectoryObject account)
    {
        ArgumentNullException.ThrowIfNull(account);
        foreach (var candidate in new[] { account.GetString(Attr.Mail), UserMessageRules.PrimarySmtpAddress(account.GetStrings(Attr.ProxyAddresses)) })
        {
            var address = EmailAddressText.Normalize(candidate);
            if (EmailAddressText.IsValid(address))
            {
                return address;
            }
        }

        return null;
    }

    /// <summary><c>displayName</c>, else the RDN value.</summary>
    public static string NameOf(DirectoryObject account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var display = account.GetString(Attr.DisplayName);
        return string.IsNullOrWhiteSpace(display) ? account.Dn.Name : display.Trim();
    }

    /// <summary>Whether a definition can be used: name, expression syntax, secret attributes, tokens without a value here.</summary>
    public static (DirectoryFieldProblem Problem, string? Detail) Check(UserMessageFields fields, DirectoryFieldDefinition definition, ISchemaCatalog? schema)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(definition);
        var name = definition.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return (DirectoryFieldProblem.EmptyName, null);
        }

        if (name.Length > FieldTemplate.MaxFieldNameLength || name.IndexOfAny(['{', '}', '|']) >= 0)
        {
            return (DirectoryFieldProblem.InvalidName, null);
        }

        var expression = definition.Expression?.Trim() ?? string.Empty;
        if (expression.Length == 0)
        {
            return (DirectoryFieldProblem.EmptyExpression, null);
        }

        TemplateParseResult parsed;
        try
        {
            parsed = fields.Engine.Parse(expression);
        }
        catch (FormatException ex)
        {
            return (DirectoryFieldProblem.Syntax, ex.Message);
        }

        if (!parsed.IsValid)
        {
            return (DirectoryFieldProblem.Syntax, parsed.Errors.FirstOrDefault());
        }

        if (parsed.ReferencedAttributes.FirstOrDefault(a => UserMessageRules.IsForbiddenAttribute(a, schema)) is { } secret)
        {
            return (DirectoryFieldProblem.Forbidden, secret);
        }

        if (parsed.SpecialTokens.FirstOrDefault(t => IsUnsupported(t.Name)) is { } token)
        {
            return (DirectoryFieldProblem.Unsupported, token.Name);
        }

        return (DirectoryFieldProblem.None, null);
    }

    public override string ToString() => $"{nameof(DirectoryRowBuilder)} ({Definitions.Count})";

    /// <summary>Tokens that have no value for directory rows: the manager (not read) and inputs (no prompt).</summary>
    private static bool IsUnsupported(string token) =>
        string.Equals(token, UserMessageFields.Manager, StringComparison.OrdinalIgnoreCase)
        || string.Equals(token, UserMessageFields.Input, StringComparison.OrdinalIgnoreCase)
        || SalutationFields.IsManagerField(token);
}
