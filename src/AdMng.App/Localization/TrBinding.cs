using System.Windows;
using System.Windows.Data;
using AdMng.App.Infrastructure;

namespace AdMng.App.Localization;

/// <summary>
/// A one-way <see cref="Binding"/> to a localized text (<c>localizer[Key]</c>) that follows language switches.
/// Usable wherever a binding is: <c>&lt;MultiBinding&gt;&lt;l:TrBinding Key="Shell.ConnectedTo"/&gt;...</c>, in setters,
/// templates and from code (<c>element.SetBinding(prop, new TrBinding(key))</c>). For plain attributes prefer
/// <c>{l:Tr Key}</c>.
/// </summary>
public sealed class TrBinding : Binding
{
    private string _key = string.Empty;

    public TrBinding()
    {
        Mode = BindingMode.OneWay;
        Source = TranslationSource.Localizer;
    }

    public TrBinding(string key)
        : this()
    {
        Key = key;
    }

    /// <summary>Resource key (see <c>AdMng.Presentation.Localization.L</c>).</summary>
    public string Key
    {
        get => _key;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _key = value;
            Path = new PropertyPath(BindingPaths.Indexer(value));
        }
    }
}
