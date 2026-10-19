using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Model;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.PropertySheets;

/// <summary>
/// "Logon Hours…" of the user's Account tab: the schedule's summary in local time and a button opening the Logon Hours
/// dialog (<see cref="LogonHoursViewModel"/>). The button always opens the dialog - read-only when the operator may not
/// write logonHours. The pending schedule is applied by the sheet as a <see cref="SetLogonHoursOperation"/> (preview
/// rules, audit and rollback of the change pipeline); an absent attribute and "all hours" are the same schedule.
/// </summary>
public sealed partial class LogonHoursFieldViewModel : PropertyFieldViewModel
{
    private LogonHours? _original;
    private int _storedLength;
    private string _name = string.Empty;

    internal LogonHoursFieldViewModel(FieldContext context)
        : base(context, Attr.LogonHours, context.Localizer[L.Labels.LogonHours])
    {
    }

    /// <summary>The pending schedule (UTC); null only while an invalid stored value is shown unchanged.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial LogonHours? Hours { get; private set; }

    /// <summary>"pon–pt 8–16" in the operator's local time, "Wszystkie godziny", or the invalid stored value.</summary>
    public string Summary => Hours is null
        ? Localizer.Format(L.LogonHours.Invalid, _storedLength)
        : LogonHoursText.Describe(Hours, Localizer, Context.Services.Time.LocalTimeZone, Context.Services.Time.GetUtcNow());

    /// <summary>Opens the Logon Hours dialog; OK takes its schedule (unless the field is read-only).</summary>
    [RelayCommand]
    private async Task EditAsync()
    {
        var time = Context.Services.Time;
        var dialog = new LogonHoursViewModel(
            Localizer,
            Hours,
            time.LocalTimeZone,
            time.GetUtcNow(),
            _name,
            IsReadOnly ? ReadOnlyReason ?? Localizer[L.Fields.ReadOnlyPermission] : null);
        if (await Context.Services.Dialogs.ShowLogonHoursAsync(dialog).ConfigureAwait(true) && !IsReadOnly)
        {
            Hours = dialog.Result;
            OnEdited();
        }
    }

    protected override void LoadCore(DirectoryObject obj)
    {
        var stored = obj.GetBytes(Attr.LogonHours);
        _storedLength = stored?.Length ?? 0;
        _original = LogonHours.FromAttribute(stored);
        _name = obj.Name;
        Hours = _original;

        // An invalid value read again with another length keeps Hours null (no change notification) but not the text.
        OnPropertyChanged(nameof(Summary));
    }

    protected override void RevertCore() => Hours = _original;

    protected override bool ComputeIsDirty() => Hours is not null && !Hours.Equals(_original);

    protected override IEnumerable<BatchOperation> CreateOperations() => Hours is null ? [] : [new SetLogonHoursOperation(Hours)];
}
