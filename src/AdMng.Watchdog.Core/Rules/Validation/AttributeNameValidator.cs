using System.Text.RegularExpressions;
using AdMng.Core.Schema;
using AdMng.Watchdog.Core.Detection;

namespace AdMng.Watchdog.Core.Rules.Validation;

/// <summary>
/// <c>invalidAttribute</c> (8.3) for <c>additionalWatchedAttributes</c>, <c>ignoredAttributes</c>, rule
/// <c>attributes</c> and exclusion attributes: plain LDAP display names (<c>^[A-Za-z][A-Za-z0-9-]{0,63}$</c> – no
/// <c>*</c>, <c>+</c>, <c>1.1</c> or <c>;options</c>), never a secret attribute (<see cref="SecretAttributes.IsSecret"/>)
/// or a noise attribute (9.3), at most 20 additional watched attributes. The live-schema check of additional
/// attributes (exists, not confidential, not constructed, not a back link) is the service's (17.2).
/// </summary>
internal static partial class AttributeNameValidator
{
    /// <summary>Maximum number of <c>directory.additionalWatchedAttributes</c>.</summary>
    public const int MaxAdditionalWatchedAttributes = 20;

    public static void Validate(ValidationRun run)
    {
        var c = run.Configuration;
        var additional = c.Directory.AdditionalWatchedAttributes;
        if (additional.Count > MaxAdditionalWatchedAttributes)
        {
            run.Error("directory.additionalWatchedAttributes", ConfigValidationCodes.InvalidAttribute,
                $"At most {MaxAdditionalWatchedAttributes} additional attributes can be watched.", ValidationRun.Arg(MaxAdditionalWatchedAttributes));
        }

        Names(run, "directory.additionalWatchedAttributes", additional);
        Names(run, "directory.ignoredAttributes", c.Directory.IgnoredAttributes);
        for (var i = 0; i < c.Rules.Count; i++)
        {
            var rule = c.Rules[i];
            if (rule is null)
            {
                continue;
            }

            var path = ValidationRun.Item("rules", i);
            Names(run, path + ".attributes", rule.Attributes);
            for (var j = 0; j < rule.Exclusions.Count; j++)
            {
                if (rule.Exclusions[j]?.Attribute is { } attribute)
                {
                    Name(run, ValidationRun.Item(path + ".exclusions", j) + ".attribute", attribute);
                }
            }
        }
    }

    /// <summary>True when <paramref name="attribute"/> is a plain, non-secret, non-noise LDAP display name.</summary>
    public static bool IsAllowed(string? attribute) =>
        attribute is not null
        && Syntax().IsMatch(attribute)
        && !SecretAttributes.IsSecret(attribute)
        && !NoiseAttributes.IsNoise(attribute);

    private static void Names(ValidationRun run, string listPath, IReadOnlyList<string> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            Name(run, ValidationRun.Item(listPath, i), values[i]);
        }
    }

    private static void Name(ValidationRun run, string path, string? value)
    {
        if (value is null || !Syntax().IsMatch(value))
        {
            run.Error(path, ConfigValidationCodes.InvalidAttribute, "Not an LDAP attribute name (letters, digits and '-', no options or wildcards).");
        }
        else if (SecretAttributes.IsSecret(value))
        {
            run.Error(path, ConfigValidationCodes.InvalidAttribute, "Secret attributes can never be watched or used in rules.");
        }
        else if (NoiseAttributes.IsNoise(value))
        {
            run.Error(path, ConfigValidationCodes.InvalidAttribute, "Noise attributes (logon times, replication data, back links) are never diffed.");
        }
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9-]{0,63}\\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Syntax();
}
