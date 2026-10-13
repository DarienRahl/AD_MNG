using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Schema;
using AdMng.Core.Templates;

namespace AdMng.Core.Batch.Planning;

/// <summary>Validation shared by the change planner and the CSV import planner.</summary>
internal static class PlanValidation
{
    /// <summary>Attributes whose values must be unique in the domain (checked when <see cref="PlanningOptions.CheckUniqueness"/>).</summary>
    public static readonly IReadOnlyList<string> UniqueAttributes = [Attr.SamAccountName, Attr.UserPrincipalName, Attr.Mail];

    /// <summary>Maximum values per batched OR-filter lookup.</summary>
    public const int LookupBatchSize = 50;

    /// <summary>The SAM limit of sAMAccountName (the schema allows 256, the SAM only 20).</summary>
    public const int MaxSamAccountNameLength = 20;

    private static readonly HashSet<AttributeSyntax> LengthCheckedSyntaxes =
    [
        AttributeSyntax.UnicodeString, AttributeSyntax.CaseIgnoreString, AttributeSyntax.CaseExactString,
        AttributeSyntax.PrintableString, AttributeSyntax.IA5String, AttributeSyntax.NumericString,
    ];

    public static ValidationIssue Error(string code, string message, string? attribute = null) =>
        new(IssueSeverity.Error, code, message, attribute);

    public static ValidationIssue Warning(string code, string message, string? attribute = null) =>
        new(IssueSeverity.Warning, code, message, attribute);

    public static ValidationIssue Info(string code, string message, string? attribute = null) =>
        new(IssueSeverity.Info, code, message, attribute);

    /// <summary>Normalizes a value to its wire form where it is unambiguous (booleans to TRUE/FALSE).</summary>
    public static string NormalizeValue(ISchemaCatalog schema, string attribute, string value)
    {
        if (schema.FindAttribute(attribute)?.Syntax != AttributeSyntax.Boolean)
        {
            return value;
        }

        return value.Trim().ToUpperInvariant() switch
        {
            "TRUE" or "1" => "TRUE",
            "FALSE" or "0" => "FALSE",
            _ => value,
        };
    }

    /// <summary>
    /// The structural (most specific) class of an object: the listed class that no other listed class derives from
    /// (falls back to the last value, which is how AD orders objectClass).
    /// </summary>
    public static string? GetStructuralClass(IReadOnlyList<string> objectClasses, ISchemaCatalog schema)
    {
        if (objectClasses.Count == 0)
        {
            return null;
        }

        var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in objectClasses)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
            var current = schema.FindClass(name);
            while (current?.SubClassOf is { Length: > 0 } parent && visited.Add(parent))
            {
                ancestors.Add(parent);
                current = schema.FindClass(parent);
            }
        }

        var candidates = objectClasses
            .Where(c => !ancestors.Contains(c) && schema.FindClass(c)?.Category is not ClassCategory.Auxiliary and not ClassCategory.Abstract)
            .ToList();
        return candidates.Count > 0 ? candidates[^1] : objectClasses[^1];
    }

    /// <summary>Schema, syntax, reference and permission validation of the draft's attribute changes.</summary>
    public static async Task ValidateAttributeChangesAsync(PlanningContext context, ObjectPlanDraft draft, EffectivePermissions permissions)
    {
        foreach (var change in draft.AttributeChanges)
        {
            var issues = ValidateChange(context, draft, change).ToList();
            draft.AttributeIssues.AddRange(issues);
            if (issues.Any(i => i.Severity == IssueSeverity.Error && i.Code is IssueCodes.UnknownAttribute or IssueCodes.ReadOnlyAttribute))
            {
                continue;
            }

            if (!permissions.CanWrite(change.Attribute))
            {
                draft.AttributeIssues.Add(Error(IssueCodes.NotWritable,
                    $"You do not have permission to write '{change.Attribute}' on this object.", change.Attribute));
            }

            await ValidateReferencesAsync(context, draft, change).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Uniqueness of sAMAccountName / userPrincipalName / mail values being written: duplicates inside the batch and
    /// values already used by other objects in the domain (batched OR-filter searches).
    /// </summary>
    public static async Task ValidateUniquenessAsync(PlanningContext context, IReadOnlyList<ObjectPlanDraft> drafts)
    {
        var claims = new Dictionary<UniqueKey, List<ObjectPlanDraft>>();
        foreach (var draft in drafts.Where(d => d.Current is not null))
        {
            foreach (var change in draft.AttributeChanges)
            {
                var attribute = UniqueAttributes.FirstOrDefault(a => string.Equals(a, change.Attribute, StringComparison.OrdinalIgnoreCase));
                if (attribute is null || change.Kind is not (AttributeChangeKind.Replace or AttributeChangeKind.AddValues))
                {
                    continue;
                }

                foreach (var value in change.NewValues)
                {
                    var key = new UniqueKey(attribute, value);
                    if (!claims.TryGetValue(key, out var claimants))
                    {
                        claims[key] = claimants = [];
                    }

                    if (!claimants.Contains(draft))
                    {
                        claimants.Add(draft);
                    }
                }
            }
        }

        foreach (var (key, claimants) in claims.Where(c => c.Value.Count > 1))
        {
            foreach (var draft in claimants)
            {
                draft.AttributeIssues.Add(Error(IssueCodes.DuplicateInBatch,
                    $"The value '{key.Value}' of '{key.Attribute}' is assigned to {claimants.Count} objects in this batch.", key.Attribute));
            }
        }

        var reported = new HashSet<(ObjectPlanDraft, UniqueKey)>();
        foreach (var chunk in claims.Keys.Chunk(LookupBatchSize))
        {
            var search = new DirectorySearch
            {
                BaseDn = context.Session.Info.DefaultNamingContext,
                Scope = SearchScope.Subtree,
                Filter = LdapFilter.Or(chunk.Select(k => LdapFilter.Equal(k.Attribute, k.Value))),
                Attributes = [.. UniqueAttributes, Attr.DistinguishedName],
            };
            await foreach (var found in context.Session.SearchAsync(search, context.CancellationToken).ConfigureAwait(false))
            {
                foreach (var attribute in UniqueAttributes)
                {
                    foreach (var value in found.GetStrings(attribute))
                    {
                        var key = new UniqueKey(attribute, value);
                        if (!claims.TryGetValue(key, out var claimants))
                        {
                            continue;
                        }

                        foreach (var draft in claimants.Where(d => !DistinguishedName.AreEqual(d.DistinguishedName, found.DistinguishedName)))
                        {
                            if (reported.Add((draft, key)))
                            {
                                draft.AttributeIssues.Add(Error(IssueCodes.Duplicate,
                                    $"The value '{value}' of '{attribute}' is already used by '{found.DistinguishedName}'.", attribute));
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>Two objects of the batch must not end up with the same DN (rename / move collisions).</summary>
    public static void ValidateFinalDnsInBatch(IReadOnlyList<ObjectPlanDraft> drafts)
    {
        var groups = drafts
            .Where(d => d.FinalDn is not null && d.NamingStep is not null)
            .GroupBy(d => PlanningContext.Key(d.FinalDn!), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);
        foreach (var group in groups)
        {
            foreach (var draft in group)
            {
                draft.NamingStep!.Issues.Add(Error(IssueCodes.DuplicateInBatch,
                    $"{group.Count()} objects of this batch would be named '{draft.FinalDn}'."));
            }
        }
    }

    private static IEnumerable<ValidationIssue> ValidateChange(PlanningContext context, ObjectPlanDraft draft, AttributeChange change)
    {
        var attribute = change.Attribute;
        var writtenValues = change.Kind is AttributeChangeKind.Replace or AttributeChangeKind.AddValues ? change.NewValues : [];
        if (string.Equals(attribute, Attr.SamAccountName, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var issue in writtenValues.Select(ValidateSamAccountName).OfType<ValidationIssue>())
            {
                yield return issue;
            }
        }

        if (!context.HasSchema)
        {
            yield break;
        }

        var schema = context.Schema;
        var definition = schema.FindAttribute(attribute);
        if (definition is null)
        {
            yield return Error(IssueCodes.UnknownAttribute, $"The attribute '{attribute}' is not defined in the schema.", attribute);
            yield break;
        }

        if (IsNamingAttribute(attribute, draft))
        {
            yield return Error(IssueCodes.ReadOnlyAttribute,
                $"'{attribute}' is the naming attribute of the object and can only be changed by renaming it.", attribute);
            yield break;
        }

        if (definition.IsReadOnly)
        {
            var reason = definition.IsBackLink ? "a back link maintained by the directory"
                : definition.IsConstructed ? "computed by the domain controller"
                : "system-only";
            yield return Error(IssueCodes.ReadOnlyAttribute, $"'{attribute}' is read-only ({reason}).", attribute);
            yield break;
        }

        if (schema.IsBinary(attribute))
        {
            yield return Error(IssueCodes.BinaryNotSupported, $"'{attribute}' holds binary data, which cannot be edited in a batch.", attribute);
            yield break;
        }

        var classes = draft.ObjectClasses;
        var sets = classes.Count > 0 ? context.GetClassAttributes(classes) : null;
        if (sets is { Allowed.Count: > 0 } && !sets.Allowed.Contains(attribute))
        {
            yield return Warning(IssueCodes.UnknownAttribute,
                $"According to the schema '{attribute}' is not allowed for objects of class '{classes[^1]}'.", attribute);
        }

        var result = change.ResultValues;
        if (definition.IsSingleValued && result.Count > 1)
        {
            yield return Error(IssueCodes.SingleValued, $"'{attribute}' is single-valued but would get {result.Count} values.", attribute);
        }

        foreach (var issue in writtenValues.Select(v => ValidateValue(definition, v)).OfType<ValidationIssue>())
        {
            yield return issue;
        }

        if (result.Count == 0 && sets is not null && sets.Mandatory.Contains(attribute))
        {
            yield return Error(IssueCodes.MandatoryAttribute, $"'{attribute}' is mandatory and cannot be cleared.", attribute);
        }
    }

    private static bool IsNamingAttribute(string attribute, ObjectPlanDraft draft)
    {
        if (string.Equals(attribute, Attr.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(attribute, Attr.DistinguishedName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var dn = draft.Current?.Dn;
        return dn is { IsRoot: false } && string.Equals(dn.Leaf.Type, attribute, StringComparison.OrdinalIgnoreCase);
    }

    private static ValidationIssue? ValidateSamAccountName(string value)
    {
        if (value.Length > MaxSamAccountNameLength)
        {
            return Error(IssueCodes.TooLong,
                $"sAMAccountName '{value}' is {value.Length} characters long; the maximum is {MaxSamAccountNameLength}.", Attr.SamAccountName);
        }

        if (value.Any(c => TemplateEngine.IsInvalidSamCharacter(c) || char.IsControl(c)))
        {
            return Error(IssueCodes.InvalidValue,
                $"sAMAccountName '{value}' contains a character that is not allowed (\"/\\[]:;|=,+*?<>@).", Attr.SamAccountName);
        }

        return value.All(c => c is '.' or ' ')
            ? Error(IssueCodes.InvalidValue, "sAMAccountName cannot consist only of periods and spaces.", Attr.SamAccountName)
            : null;
    }

    /// <summary>Validates one wire value (string form) against the attribute's syntax and range.</summary>
    private static ValidationIssue? ValidateValue(AttributeDefinition definition, string value)
    {
        var attribute = definition.LdapName;
        if (value.Length == 0)
        {
            return Error(IssueCodes.InvalidValue, $"Empty values are not allowed for '{attribute}'.", attribute);
        }

        switch (definition.Syntax)
        {
            case AttributeSyntax.Boolean when value is not ("TRUE" or "FALSE"):
                return Error(IssueCodes.InvalidValue, $"'{value}' is not a valid boolean for '{attribute}' (use TRUE or FALSE).", attribute);
            case AttributeSyntax.Integer or AttributeSyntax.Enumeration:
                if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
                {
                    return Error(IssueCodes.InvalidValue, $"'{value}' is not a valid 32-bit integer for '{attribute}'.", attribute);
                }

                return CheckRange(definition, integer);
            case AttributeSyntax.LargeInteger:
                if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var large))
                {
                    return Error(IssueCodes.InvalidValue, $"'{value}' is not a valid integer for '{attribute}'.", attribute);
                }

                return CheckRange(definition, large);
            case AttributeSyntax.DistinguishedName when !DistinguishedName.TryParse(value, out var dn) || dn.IsRoot:
                return Error(IssueCodes.InvalidValue, $"'{value}' is not a valid distinguished name for '{attribute}'.", attribute);
            case AttributeSyntax.GeneralizedTime when AdConvert.FromGeneralizedTime(value) is null:
                return Error(IssueCodes.InvalidValue, $"'{value}' is not a valid GeneralizedTime for '{attribute}'.", attribute);
            case AttributeSyntax.UtcTime when !DateTime.TryParseExact(value, "yyMMddHHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.None, out _):
                return Error(IssueCodes.InvalidValue, $"'{value}' is not a valid UTC time for '{attribute}'.", attribute);
            case AttributeSyntax.NumericString when !value.All(c => char.IsAsciiDigit(c) || c == ' '):
                return Error(IssueCodes.InvalidValue, $"'{attribute}' only accepts digits and spaces.", attribute);
            case AttributeSyntax.IA5String or AttributeSyntax.PrintableString when !System.Text.Ascii.IsValid(value):
                return Error(IssueCodes.InvalidValue, $"'{attribute}' only accepts ASCII characters.", attribute);
        }

        if (!LengthCheckedSyntaxes.Contains(definition.Syntax))
        {
            return null;
        }

        if (definition.RangeUpper is { } upper && value.Length > upper)
        {
            return Error(IssueCodes.TooLong, $"The value of '{attribute}' is {value.Length} characters long; the maximum is {upper}.", attribute);
        }

        if (definition.RangeLower is { } lower && value.Length < lower)
        {
            return Error(IssueCodes.TooShort, $"The value of '{attribute}' is {value.Length} characters long; the minimum is {lower}.", attribute);
        }

        return null;
    }

    private static ValidationIssue? CheckRange(AttributeDefinition definition, long value)
    {
        if ((definition.RangeLower is { } lower && value < lower) || (definition.RangeUpper is { } upper && value > upper))
        {
            return Error(IssueCodes.InvalidValue,
                $"{value.ToString(CultureInfo.InvariantCulture)} is outside the allowed range of '{definition.LdapName}'.", definition.LdapName);
        }

        return null;
    }

    /// <summary>DN-syntax values must reference existing objects (AD enforces referential integrity).</summary>
    private static async Task ValidateReferencesAsync(PlanningContext context, ObjectPlanDraft draft, AttributeChange change)
    {
        if (!context.HasSchema
            || context.Schema.FindAttribute(change.Attribute)?.Syntax != AttributeSyntax.DistinguishedName
            || change.Kind is not (AttributeChangeKind.Replace or AttributeChangeKind.AddValues))
        {
            return;
        }

        foreach (var value in change.NewValues)
        {
            if (!DistinguishedName.TryParse(value, out var dn) || dn.IsRoot)
            {
                continue;
            }

            if (await context.GetReferenceAsync(value).ConfigureAwait(false) is null)
            {
                draft.AttributeIssues.Add(Error(IssueCodes.NotFound,
                    $"'{change.Attribute}' refers to '{value}', which does not exist.", change.Attribute));
            }
        }
    }

    /// <summary>Case-insensitive key of a unique attribute value.</summary>
    private readonly record struct UniqueKey(string Attribute, string Value)
    {
        public bool Equals(UniqueKey other) =>
            string.Equals(Attribute, other.Attribute, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(Attribute),
            StringComparer.OrdinalIgnoreCase.GetHashCode(Value));
    }
}
