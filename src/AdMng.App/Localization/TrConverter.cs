using System.Globalization;
using System.Windows;
using System.Windows.Data;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Views;

namespace AdMng.App.Localization;

/// <summary>
/// Converts an enum value (or a resource key string) to its localized display name, using the key families of
/// <see cref="DisplayNames"/> (<c>ObjectKind.User</c>, <c>Theme.Dark</c>...). <c>ConverterParameter</c> may give an
/// explicit key prefix (<c>"Severity"</c> → <c>Severity.Error</c>). Enums are never shown raw.
/// </summary>
/// <remarks>
/// A converter is not re-evaluated when the language changes; prefer view model texts (<c>Option&lt;T&gt;.Text</c>) or
/// <see cref="TrBinding"/> where a live switch matters.
/// </remarks>
[ValueConversion(typeof(object), typeof(string))]
public sealed class TrConverter : IValueConverter
{
    /// <summary>Shared instance for code-behind use.</summary>
    public static TrConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? string.Empty : TranslationSource.Localizer[KeyFor(value, parameter as string)];

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;

    /// <summary>The resource key of a value.</summary>
    public static string KeyFor(object value, string? prefix = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            return $"{prefix}.{value}";
        }

        return value switch
        {
            string key => key,
            ObjectKind kind => DisplayNames.ObjectKindKey(kind),
            DirectoryErrorKind kind => DisplayNames.ErrorKindKey(kind),
            IssueSeverity severity => DisplayNames.IssueSeverityKey(severity),
            UserAccountControl flag => DisplayNames.UacFlagKey(flag),
            GroupScope scope => DisplayNames.GroupScopeKey(scope),
            ThemePreference theme => DisplayNames.ThemeKey(theme),
            AuthenticationMode mode => DisplayNames.AuthenticationKey(mode),
            TransportSecurity security => DisplayNames.SecurityKey(security),
            SearchScope scope => DisplayNames.ScopeKey(scope),
            QuickFilterKind kind => DisplayNames.ChipKey(kind),
            BuiltInViewKind kind => DisplayNames.BuiltInViewKey(kind),
            Enum other => $"{other.GetType().Name}.{other}",
            _ => value.ToString() ?? string.Empty,
        };
    }
}
