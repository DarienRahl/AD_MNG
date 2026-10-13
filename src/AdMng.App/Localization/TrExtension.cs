using System.Windows;
using System.Windows.Markup;

namespace AdMng.App.Localization;

/// <summary>
/// <c>{l:Tr Shell.MenuFile}</c>: a localized text bound to the application localizer, so switching the language
/// updates the UI without a restart. On a dependency property, in a setter or in a template it yields a
/// <see cref="TrBinding"/>; on a plain CLR property it yields the current text.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key)
    {
        Key = key;
    }

    /// <summary>Resource key (see <c>AdMng.Presentation.Localization.L</c>).</summary>
    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrWhiteSpace(Key))
        {
            throw new InvalidOperationException("Tr requires a resource key.");
        }

        if (serviceProvider?.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget
            {
                TargetObject: DependencyObject,
                TargetProperty: not DependencyProperty,
            })
        {
            // A CLR property of an element cannot hold a binding: hand out the current text.
            return TranslationSource.Localizer[Key];
        }

        return new TrBinding(Key).ProvideValue(serviceProvider!);
    }
}
