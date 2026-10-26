using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Formatting;
using AdMng.Core.Schema;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.PropertySheets;

/// <summary>
/// What the fields and pages of one property sheet share: services, the session, the schema and the operator's
/// effective permissions on the object (refreshed on every load).
/// </summary>
internal sealed class FieldContext(DialogServices services, IDirectorySession session)
{
    private static readonly HashSet<AttributeSyntax> TextSyntaxes =
    [
        AttributeSyntax.UnicodeString, AttributeSyntax.CaseIgnoreString, AttributeSyntax.CaseExactString,
        AttributeSyntax.PrintableString, AttributeSyntax.IA5String, AttributeSyntax.NumericString,
    ];

    public DialogServices Services { get; } = services;

    public IDirectorySession Session { get; } = session;

    /// <summary>Reads of all domain controllers ("Sprawdź na wszystkich DC" of the account state), null = current DC only.</summary>
    public AccountReview.DomainControllerReader? DomainControllers { get; init; }

    /// <summary>"Wyślij wiadomość…" (U1); null when the host registered no message launcher.</summary>
    public AccountReview.IUserMessageLauncher? Messages { get; init; }

    public ILocalizer Localizer => Services.Localizer;

    public ISchemaCatalog Schema => Session.Schema;

    public IAttributeFormatter Formatter => Services.Sessions.Formatter;

    public CultureInfo Culture => Localizer.Culture;

    /// <summary>What the bound identity may write (allowedAttributesEffective); Unknown lets the server decide.</summary>
    public EffectivePermissions Permissions { get; set; } = EffectivePermissions.Unknown;

    /// <summary>Mandatory attributes of the object's classes (cannot be cleared).</summary>
    public IReadOnlySet<string> Mandatory { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool HasSchema => Schema.Attributes.Count > 0;

    /// <summary>False when the schema is known and does not define the attribute (e.g. Exchange attributes without Exchange).</summary>
    public bool IsKnown(string attribute) => !HasSchema || Schema.FindAttribute(attribute) is not null;

    /// <summary>Friendly attribute name (display specifier, localized fallback, LDAP name).</summary>
    public string Label(string attribute) => Localizer.AttributeName(attribute, Schema);

    /// <summary>Localized reason why the attribute cannot be changed, or null when it is writable.</summary>
    public string? ReadOnlyReason(string attribute)
    {
        if (Schema.FindAttribute(attribute) is { IsReadOnly: true })
        {
            return Localizer[L.Fields.ReadOnlySystem];
        }

        return Permissions.CanWrite(attribute) ? null : Localizer[L.Fields.ReadOnlyPermission];
    }

    /// <summary>True for attributes edited as plain text (lengths are counted in characters).</summary>
    public bool IsTextAttribute(string attribute) =>
        Schema.FindAttribute(attribute) is not { } definition || TextSyntaxes.Contains(definition.Syntax);

    /// <summary>Maximum length in characters for text attributes (0 = unknown / unlimited).</summary>
    public int MaxLength(string attribute) =>
        Schema.FindAttribute(attribute) is { RangeUpper: { } upper } definition && TextSyntaxes.Contains(definition.Syntax) && upper < int.MaxValue
            ? (int)upper
            : 0;

    /// <summary>
    /// Converts operator input into the wire value of the attribute. Returns false with a localized error when the
    /// input is not valid for the attribute's syntax or range. Empty input is not handled here (it means "clear").
    /// </summary>
    public bool TryParse(string attribute, string input, out string wireValue, out string? error)
    {
        wireValue = input;
        error = null;
        var definition = Schema.FindAttribute(attribute);
        if (definition is not null && TextSyntaxes.Contains(definition.Syntax))
        {
            if (definition.RangeUpper is { } upper && input.Length > upper)
            {
                error = Localizer.Format(L.Fields.TooLong, upper);
                return false;
            }

            if (definition.RangeLower is { } lower && input.Length < lower)
            {
                error = Localizer.Format(L.Fields.TooShort, lower);
                return false;
            }
        }

        // Length limits differ per object type (user and computer accounts 20, groups 256); the planner reports them.
        if (string.Equals(attribute, Attr.SamAccountName, StringComparison.OrdinalIgnoreCase)
            && !AccountNames.IsValidSamAccountName(input, int.MaxValue))
        {
            error = Localizer[L.Fields.InvalidSamAccountName];
            return false;
        }

        if (!Formatter.TryParse(attribute, input, Culture, out var parsed, out _))
        {
            error = SyntaxError(definition?.Syntax ?? AttributeSyntax.Unknown, attribute);
            return false;
        }

        if (parsed is not string text)
        {
            // Binary values are never edited as text in property pages.
            error = Localizer[L.Fields.InvalidValue];
            return false;
        }

        wireValue = text;
        return true;
    }

    private string SyntaxError(AttributeSyntax syntax, string attribute)
    {
        if (Attr.FileTimeAttributes.Contains(attribute))
        {
            return Localizer[L.Fields.InvalidDate];
        }

        return syntax switch
        {
            AttributeSyntax.Integer or AttributeSyntax.Enumeration or AttributeSyntax.LargeInteger => Localizer[L.Fields.InvalidNumber],
            AttributeSyntax.Boolean => Localizer[L.Fields.InvalidBoolean],
            AttributeSyntax.DistinguishedName or AttributeSyntax.DnWithBinary or AttributeSyntax.DnWithString => Localizer[L.Fields.InvalidDn],
            AttributeSyntax.GeneralizedTime or AttributeSyntax.UtcTime => Localizer[L.Fields.InvalidDate],
            AttributeSyntax.NumericString => Localizer[L.Fields.DigitsOnly],
            AttributeSyntax.IA5String => Localizer[L.Fields.AsciiOnly],
            AttributeSyntax.PrintableString => Localizer[L.Fields.PrintableOnly],
            _ => Localizer[L.Fields.InvalidValue],
        };
    }
}
