using System.Diagnostics;
using System.Globalization;

namespace AdMng.App.Infrastructure;

/// <summary>
/// Collects WPF data binding errors (<see cref="PresentationTraceSources.DataBindingSource"/>) for the smoke test and
/// UI tests. Installing it enables WPF binding tracing for the process; it is never used by the running application.
/// </summary>
public sealed class BindingErrorListener : TraceListener
{
    private readonly List<string> _errors = [];
    private readonly Lock _gate = new();

    private BindingErrorListener()
        : base(nameof(BindingErrorListener))
    {
    }

    /// <summary>Errors recorded so far.</summary>
    public IReadOnlyList<string> Errors
    {
        get
        {
            lock (_gate)
            {
                return [.. _errors];
            }
        }
    }

    /// <summary>Enables binding tracing (warnings and errors) and starts recording.</summary>
    public static BindingErrorListener Install()
    {
        // Refresh() turns WPF tracing on without a debugger or registry switch; it must precede the configuration.
        PresentationTraceSources.Refresh();
        var listener = new BindingErrorListener();
        var source = PresentationTraceSources.DataBindingSource;
        source.Switch.Level = SourceLevels.Warning;
        source.Listeners.Add(listener);
        return listener;
    }

    /// <summary>Forgets the recorded errors.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _errors.Clear();
        }
    }

    /// <summary>
    /// Errors that come from the Fluent theme's own templates, not from application XAML, and are ignored:
    /// <list type="bullet">
    /// <item>item container styles – ListBoxItem / MenuItem bind their content alignment to the ancestor ItemsControl
    /// and WPF reports the lookup while a container is not yet attached;</item>
    /// <item>the DatePicker template – its DatePickerTextBox binds its accessible name to the ancestor DatePicker, and
    /// when the theme is switched while a date picker is open (light/dark at run time, the smoke test does it) WPF
    /// reports the lookup for the text box of the replaced template.</item>
    /// </list>
    /// </summary>
    public static bool IsThemeNoise(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return (message.Contains("AncestorType='System.Windows.Controls.ItemsControl'", StringComparison.Ordinal)
                && (message.Contains("Path=HorizontalContentAlignment", StringComparison.Ordinal)
                    || message.Contains("Path=VerticalContentAlignment", StringComparison.Ordinal)))
            || (message.Contains("AncestorType='System.Windows.Controls.DatePicker'", StringComparison.Ordinal)
                && message.Contains("target element is 'DatePickerTextBox'", StringComparison.Ordinal)
                && message.Contains("target property is 'Name'", StringComparison.Ordinal));
    }

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message) =>
        Record(eventType, id, message ?? string.Empty);

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args) =>
        Record(eventType, id, format is null ? string.Empty : args is { Length: > 0 } ? string.Format(CultureInfo.InvariantCulture, format, args) : format);

    public override void Write(string? message)
    {
        // TraceSource reports through TraceEvent; raw writes (headers) carry no extra information.
    }

    public override void WriteLine(string? message)
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
        }

        base.Dispose(disposing);
    }

    private void Record(TraceEventType eventType, int id, string message)
    {
        if (eventType > TraceEventType.Error || IsThemeNoise(message))
        {
            return;
        }

        lock (_gate)
        {
            _errors.Add(string.Create(CultureInfo.InvariantCulture, $"[{id}] {message}"));
        }
    }
}
