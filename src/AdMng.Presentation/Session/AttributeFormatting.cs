using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Formatting;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Session;

/// <summary>Creates the per-session <see cref="IAttributeFormatter"/> (it depends on the session's schema).</summary>
/// <remarks>The app registers the Core implementation, e.g. <c>services.AddAttributeFormatter(s =&gt; new AttributeFormatter(s))</c>.</remarks>
public interface IAttributeFormatterFactory
{
    IAttributeFormatter Create(ISchemaCatalog schema);
}

/// <summary><see cref="IAttributeFormatterFactory"/> over a delegate.</summary>
public sealed class DelegateAttributeFormatterFactory(Func<ISchemaCatalog, IAttributeFormatter> factory) : IAttributeFormatterFactory
{
    public IAttributeFormatter Create(ISchemaCatalog schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return factory(schema);
    }
}

/// <summary>
/// Decorates the Core formatter with UI localization: FILETIME "never/not set" sentinels (0 and
/// <see cref="long.MaxValue"/>) and the formatter's English "never" text become the localized
/// <see cref="L.Format.Never"/>. Everything else is delegated unchanged.
/// </summary>
public sealed class LocalizedAttributeFormatter(IAttributeFormatter inner, ILocalizer localizer) : IAttributeFormatter
{
    public IAttributeFormatter Inner { get; } = inner;

    public string Format(string attribute, AttributeValues? values, CultureInfo culture)
    {
        if (values is { Count: > 0 } && Attr.FileTimeAttributes.Contains(attribute) && values.All(IsFileTimeSentinel))
        {
            return localizer[L.Format.Never];
        }

        return Localize(Inner.Format(attribute, values, culture));
    }

    public string FormatValue(string attribute, object value, CultureInfo culture)
    {
        if (Attr.FileTimeAttributes.Contains(attribute) && IsFileTimeSentinel(value))
        {
            return localizer[L.Format.Never];
        }

        return Localize(Inner.FormatValue(attribute, value, culture));
    }

    public bool TryParse(string attribute, string input, CultureInfo culture, out object? value, out string? error)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (Attr.FileTimeAttributes.Contains(attribute)
            && string.Equals(input.Trim(), localizer[L.Format.Never], StringComparison.CurrentCultureIgnoreCase))
        {
            input = "never";
        }

        return Inner.TryParse(attribute, input, culture, out value, out error);
    }

    private static bool IsFileTimeSentinel(object value) =>
        value is string s && AdConvert.ParseInt64(s) is 0 or AdConvert.FileTimeNever;

    private string Localize(string text) =>
        text.Trim().Trim('(', ')').Equals("never", StringComparison.OrdinalIgnoreCase) ? localizer[L.Format.Never] : text;
}
