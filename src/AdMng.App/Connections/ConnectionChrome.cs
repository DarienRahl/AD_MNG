using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Localization;

namespace AdMng.App.Connections;

/// <summary>
/// The connection a window belongs to and its identity chrome (design note docs/dev/multi-window.md 2.4, 4.2, 4.4):
/// <list type="bullet">
/// <item>the attached <c>ConnectionId</c>, set on every window a connection's <c>WindowService</c> prepares and on the
/// connect and main window by their constructors. Window lookups (owner, "one per connection" tool windows, property
/// sheets) only ever match windows of the same id;</item>
/// <item><see cref="Apply"/>: the title suffix " – contoso.test" and a 4 px colour strip with a one-line header
/// (domain · account) above the content of a child window. The identity is a snapshot of the moment the window opened:
/// a property sheet of the old domain keeps naming it after the main window reconnected to another domain.</item>
/// </list>
/// Colour is never the only cue: the domain is always written next to it.
/// </summary>
public static class ConnectionChrome
{
    /// <summary>Height of the identity strip (main window, child windows, message dialogs).</summary>
    public const double StripHeight = 4;

    public static readonly DependencyProperty ConnectionIdProperty = DependencyProperty.RegisterAttached(
        "ConnectionId", typeof(Guid?), typeof(ConnectionChrome), new PropertyMetadata(null));

    /// <summary>The identity snapshot a child window was opened with (null: not connected, or no chrome).</summary>
    public static readonly DependencyProperty IdentityProperty = DependencyProperty.RegisterAttached(
        "Identity", typeof(ConnectionIdentity), typeof(ConnectionChrome), new PropertyMetadata(null));

    private static readonly DependencyProperty AppliedProperty = DependencyProperty.RegisterAttached(
        "Applied", typeof(bool), typeof(ConnectionChrome), new PropertyMetadata(false));

    public static Guid? GetConnectionId(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (Guid?)element.GetValue(ConnectionIdProperty);
    }

    public static void SetConnectionId(DependencyObject element, Guid? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ConnectionIdProperty, value);
    }

    public static ConnectionIdentity? GetIdentity(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (ConnectionIdentity?)element.GetValue(IdentityProperty);
    }

    /// <summary>True when <paramref name="window"/> belongs to the connection <paramref name="connectionId"/>.</summary>
    public static bool BelongsTo(Window window, Guid connectionId) => window is not null && GetConnectionId(window) == connectionId;

    /// <summary>
    /// Gives a child window the identity of its connection: the title suffix and, unless <paramref name="withStrip"/> is
    /// false, the colour strip and header line above its content. Applied once per window; without an identity (not
    /// connected) nothing changes.
    /// </summary>
    public static void Apply(Window window, ConnectionIdentity? identity, ILocalizer localizer, bool withStrip = true)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(localizer);
        if (identity is null || (bool)window.GetValue(AppliedProperty))
        {
            return;
        }

        window.SetValue(AppliedProperty, true);
        window.SetValue(IdentityProperty, identity);
        AppendTitleSuffix(window, $" – {identity.DomainDnsName}");
        if (withStrip && window.Content is UIElement content)
        {
            window.Content = null;
            var panel = new DockPanel { LastChildFill = true };
            var strip = Strip(identity.Color);
            DockPanel.SetDock(strip, Dock.Top);
            panel.Children.Add(strip);
            var header = Header(identity, localizer);
            DockPanel.SetDock(header, Dock.Top);
            panel.Children.Add(header);
            panel.Children.Add(content);
            window.Content = panel;
        }
    }

    /// <summary>A colour strip (<see cref="StripHeight"/>) in the connection colour; grey when not connected.</summary>
    public static Border Strip(ConnectionColor? color) => new()
    {
        Height = StripHeight,
        Background = Brush(color),
        Tag = "ConnectionStrip",
    };

    /// <summary>The solid brush of a connection colour (frozen); the neutral grey for null (not connected).</summary>
    public static SolidColorBrush Brush(ConnectionColor? color)
    {
        var (r, g, b) = color is { } value ? ConnectionColors.Rgb(value) : ConnectionColors.NotConnectedRgb;
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>"Domena: contoso.test (CONTOSO\jan.kowalski)" in the connection colour, above the window content.</summary>
    private static Border Header(ConnectionIdentity identity, ILocalizer localizer)
    {
        var text = new TextBlock
        {
            Text = HeaderText(identity, localizer),
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        return new Border
        {
            Background = Brush(identity.Color),
            Padding = new Thickness(12, 2, 12, 3),
            Child = text,
            Tag = "ConnectionHeader",
        };
    }

    /// <summary>The header line of a connection (also used by message dialogs).</summary>
    public static string HeaderText(ConnectionIdentity identity, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(localizer);
        return identity.Account.Length > 0
            ? localizer.Format(L.Connections.DomainHeader, identity.DomainDnsName, identity.Account)
            : localizer.Format(L.Connections.DomainHeaderNoAccount, identity.DomainDnsName);
    }

    /// <summary>
    /// Appends <paramref name="suffix"/> to the window title. A bound title (most windows bind WindowTitle, some a
    /// translated text) becomes a MultiBinding of the same source and the suffix, so it still follows renames and
    /// language switches; a literal title gets the suffix appended.
    /// </summary>
    private static void AppendTitleSuffix(Window window, string suffix)
    {
        if (BindingOperations.GetBindingBase(window, Window.TitleProperty) is Binding binding)
        {
            var multi = new MultiBinding { Mode = BindingMode.OneWay, Converter = TitleWithSuffix.Instance, ConverterParameter = suffix };
            multi.Bindings.Add(Clone(binding));
            window.SetBinding(Window.TitleProperty, multi);
            return;
        }

        if (BindingOperations.GetBindingBase(window, Window.TitleProperty) is null
            && !(window.Title ?? string.Empty).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            window.Title += suffix;
        }
    }

    /// <summary>A one-way copy of a title binding (a binding in use may not be shared by another binding expression).</summary>
    private static Binding Clone(Binding source)
    {
        var copy = new Binding
        {
            Path = source.Path,
            Mode = BindingMode.OneWay,
            Converter = source.Converter,
            ConverterParameter = source.ConverterParameter,
            ConverterCulture = source.ConverterCulture,
            StringFormat = source.StringFormat,
            TargetNullValue = source.TargetNullValue,
            FallbackValue = source.FallbackValue,
        };
        if (source.Source is not null)
        {
            copy.Source = source.Source;
        }
        else if (source.RelativeSource is not null)
        {
            copy.RelativeSource = source.RelativeSource;
        }
        else if (!string.IsNullOrEmpty(source.ElementName))
        {
            copy.ElementName = source.ElementName;
        }

        return copy;
    }

    /// <summary>Title text + suffix (unless the title already ends with it: "Kosz Active Directory – contoso.test").</summary>
    private sealed class TitleWithSuffix : IMultiValueConverter
    {
        public static readonly TitleWithSuffix Instance = new();

        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        {
            var title = values.Length > 0 && values[0] is string text ? text : string.Empty;
            var suffix = parameter as string ?? string.Empty;
            return title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? title : title + suffix;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
