using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Presentation.Dialogs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.BatchEditing;

/// <summary>
/// "Set logon hours": one weekly schedule for all user accounts of the batch, edited in the same Logon Hours dialog as
/// on the Account tab (<see cref="LogonHoursViewModel"/>). Starts with every hour permitted; the planner leaves accounts
/// that already have the schedule (an absent attribute counts as "all hours") unchanged and skips non-user objects.
/// </summary>
public sealed partial class LogonHoursEditorViewModel : OperationEditorViewModel
{
    internal LogonHoursEditorViewModel(OperationEditorContext context)
        : base(context, OperationType.LogonHours)
    {
    }

    /// <summary>The schedule to set (UTC).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial LogonHours Hours { get; set; } = LogonHours.All;

    /// <summary>The schedule in the operator's local time ("pon–pt 8–16").</summary>
    public string Summary =>
        LogonHoursText.Describe(Hours, Localizer, Context.Services.Time.LocalTimeZone, Context.Services.Time.GetUtcNow());

    /// <summary>Opens the Logon Hours dialog with the current schedule; OK takes the new one.</summary>
    [RelayCommand]
    private async Task EditAsync()
    {
        var time = Context.Services.Time;
        var dialog = new LogonHoursViewModel(Localizer, Hours, time.LocalTimeZone, time.GetUtcNow());
        if (await Context.Services.Dialogs.ShowLogonHoursAsync(dialog).ConfigureAwait(true))
        {
            Hours = dialog.Result;
        }
    }

    partial void OnHoursChanged(LogonHours value) => OnChanged();

    protected override string? Validate() => null;

    protected override BatchOperation BuildCore() => new SetLogonHoursOperation(Hours);

    protected override void Fill(BatchOperation operation) => Hours = ((SetLogonHoursOperation)operation).Hours;
}
