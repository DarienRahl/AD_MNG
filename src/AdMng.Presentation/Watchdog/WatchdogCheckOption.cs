using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Watchdog;

/// <summary>A value with its localized text and a check box (filter lists, kind pickers, recipient choices).</summary>
public sealed partial class WatchdogCheckOption<T> : ObservableObject
{
    private readonly Action<WatchdogCheckOption<T>>? _changed;

    public WatchdogCheckOption(T value, string text, bool isChecked = false, Action<WatchdogCheckOption<T>>? changed = null)
    {
        Value = value;
        Text = text ?? throw new ArgumentNullException(nameof(text));
        IsChecked = isChecked;
        _changed = changed;
    }

    public T Value { get; }

    public string Text { get; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>Optional secondary text (e.g. the category of an event kind).</summary>
    public string? Detail { get; init; }

    partial void OnIsCheckedChanged(bool value) => _changed?.Invoke(this);

    public override string ToString() => Text;
}
