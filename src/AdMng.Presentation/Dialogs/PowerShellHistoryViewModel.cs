using System.Text;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Dialogs;

/// <summary>One executed change in the PowerShell history list.</summary>
public sealed class PowerShellHistoryItemViewModel(PowerShellHistoryEntry entry, string time)
{
    public PowerShellHistoryEntry Entry { get; } = entry;

    public string Title => Entry.Title;

    /// <summary>Local time in the UI culture.</summary>
    public string Time { get; } = time;

    public string Server => Entry.Server;

    /// <summary>The domain the change was executed in (the window may have been connected to several).</summary>
    public string Domain => Entry.Domain;

    public int ObjectCount => Entry.ObjectCount;

    public string Script => Entry.Script;
}

/// <summary>
/// The equivalent PowerShell (ActiveDirectory module) of every change executed in this connection window, newest first,
/// each with the domain it ran in: the window keeps its history when it reconnects to another domain, so the history
/// window carries no single domain's chrome. Scripts can be copied or saved explicitly; nothing is stored automatically.
/// </summary>
public sealed partial class PowerShellHistoryViewModel : WindowViewModelBase
{
    private readonly PowerShellHistory _history;

    public PowerShellHistoryViewModel(DialogServices services, PowerShellHistory history)
        : base(services)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _history.EntryAdded += OnEntryAdded;
        Items.ReplaceAll(_history.Entries.Reverse().Select(Create));
        SelectedItem = Items.FirstOrDefault();
    }

    public override string WindowTitle => Localizer[L.PowerShellHistory.WindowTitle];

    /// <summary>Application-wide; never acts in the directory session, so it does not end with it.</summary>
    protected override bool UsesSession => false;

    /// <summary>Newest first.</summary>
    public BulkObservableCollection<PowerShellHistoryItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand), nameof(SaveCommand))]
    public partial PowerShellHistoryItemViewModel? SelectedItem { get; set; }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Copy()
    {
        if (SelectedItem is { } item)
        {
            ClipboardCopy.Text(Services.Clipboard, Services.Notifications, item.Script, Localizer[L.Batch.PowerShellCopied]);
        }
    }

    /// <summary>Saves the selected script as .ps1 (UTF-8 with BOM, which Windows PowerShell 5.1 needs for non-ASCII text).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task SaveAsync()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        var title = Localizer[L.PowerShellHistory.SaveTitle];
        var path = await Services.Files.SaveFileAsync(title, $"admng-{TimeZoneInfo.ConvertTime(item.Entry.ExecutedAt, Services.Time.LocalTimeZone):yyyyMMdd-HHmmss}.ps1",
            [new FileFilter(Localizer[L.PowerShellHistory.ScriptFilter], "*.ps1")]).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, item.Script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)).ConfigureAwait(true);
            Services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.Batch.Exported, Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Errors.Translate(ex)).ConfigureAwait(true);
        }
    }

    private bool HasSelection() => SelectedItem is not null;

    [RelayCommand]
    private void Close() => RequestClose(true);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _history.EntryAdded -= OnEntryAdded;
        }
    }

    private PowerShellHistoryItemViewModel Create(PowerShellHistoryEntry entry) =>
        new(entry, TimeZoneInfo.ConvertTime(entry.ExecutedAt, Services.Time.LocalTimeZone).ToString("G", Localizer.Culture));

    private void OnEntryAdded(object? sender, PowerShellHistoryEntry entry) => _ = Services.Dispatcher.InvokeAsync(() =>
    {
        if (!IsDisposed)
        {
            Items.Insert(0, Create(entry));
        }
    });
}
