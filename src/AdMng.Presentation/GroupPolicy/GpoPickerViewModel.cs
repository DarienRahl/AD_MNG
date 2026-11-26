using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>A GPO in <see cref="GpoPickerViewModel"/>.</summary>
public sealed partial class GpoPickerItem(GpoInfo gpo, string statusText) : ObservableObject
{
    public GpoInfo Gpo { get; } = gpo;

    public string Name => Gpo.DisplayName;

    public string StatusText { get; } = statusText;

    /// <summary>Checked (multi-select).</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

/// <summary>
/// Choose GPOs of the window's domain ("Połącz istniejący GPO…", "Porównaj z…"): the GPOs read on the session's DC, a name
/// filter, single or multiple selection; GPOs in <c>exclude</c> are not offered.
/// </summary>
/// <remarks>
/// The dialog has one status line (<see cref="Status"/>; track V, stage V2b-3, design note
/// docs/dev/v-interface-renewal.md "V2b-3 as built"). Reading the GPOs when it opens is its operation, with Stop; the
/// list is the result, so at rest the line says how many GPOs it shows (with the filter: how many of them). A read that
/// was stopped or failed leaves an empty list - the state of the dialog until it is read again, with Retry. As the state
/// that lasts: the session of the dialog ended, or it was opened without one.
/// </remarks>
public sealed partial class GpoPickerViewModel : WindowViewModelBase, IHasPageStatus
{
    private readonly GroupPolicyReader _reader;
    private readonly HashSet<Guid> _exclude;
    private readonly string _title;
    private IReadOnlyList<GpoPickerItem> _all = [];

    /// <summary>The GPOs were read at least once: the line has their count as the summary.</summary>
    private bool _listed;

    /// <summary>What the last read left, when it left no list: it was stopped or failed (null: nothing wrong).</summary>
    private PageStatus? _readState;

    public GpoPickerViewModel(DialogServices services, GroupPolicyReader reader, string title, bool multiSelect, IEnumerable<Guid> exclude)
        : base(services)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _title = title ?? string.Empty;
        MultiSelect = multiSelect;
        _exclude = [.. exclude ?? []];
        Status = new PageStatusHolder(Localizer, services.Dispatcher, services.Lists.StatusLog);
        UpdateLasting();
    }

    /// <summary>
    /// The status of the dialog - what its one status line shows: the running read of the GPOs (with Stop), at rest how
    /// many GPOs the list shows, why the list is empty after a read that was stopped or failed, and that the session of
    /// the dialog ended.
    /// </summary>
    public PageStatusHolder Status { get; }

    public override string WindowTitle => _title;

    public bool MultiSelect { get; }

    [ObservableProperty]
    public partial IReadOnlyList<GpoPickerItem> Items { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    public partial GpoPickerItem? SelectedItem { get; set; }

    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    /// <summary>The chosen GPOs (set by OK).</summary>
    public IReadOnlyList<GpoInfo> Result { get; private set; } = [];

    public override Task InitializeAsync() => LoadAsync();

    /// <summary>
    /// Reads the GPOs of the domain - the operation of the dialog: its line says "Reading the group policy objects of …"
    /// with Stop. The list is the result, so a read that ended says nothing and the line shows the count; a read that was
    /// stopped or failed leaves the list empty, which is the state of the dialog until it is read again (Retry). Closing
    /// the window and the end of its session end the read without a word. Never throws.
    /// </summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        if (Session is not { } session)
        {
            UpdateLasting();
            return;
        }

        var domain = session.Info.DomainDnsName;
        using var reading = Status.Begin(Localizer.Format(L.GpoPickerStatus.Reading, domain), new PageStatusLog(L.GpoPickerStatus.Reading));

        // What the last read left is over: this one decides.
        _readState = null;
        UpdateLasting();
        try
        {
            var gpos = await Task.Run(() => _reader.ReadPoliciesAsync(session, reading.Token), reading.Token).ConfigureAwait(true);
            if (reading.IsEnded)
            {
                return;
            }

            // The list is the result - also when Stop was pressed and the domain controller answered all the same.
            _all = [.. gpos.Where(g => !_exclude.Contains(g.Id)).Select(g => new GpoPickerItem(g, GroupPolicyText.Status(Localizer, g.Status)))];
            foreach (var item in _all)
            {
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(GpoPickerItem.IsChecked))
                    {
                        OkCommand.NotifyCanExecuteChanged();
                    }
                };
            }

            _listed = true;
            ApplyFilter();
        }
        catch (OperationCanceledException) when (reading.Token.IsCancellationRequested)
        {
            // Only a read the operator stopped says so; the end of the session and closing the window say nothing.
            if (reading.IsCancelling)
            {
                var retry = Localizer[L.Common.Retry];
                _readState = PageStatus.Attention(
                    Localizer.Format(L.GpoPickerStatus.Stopped, retry),
                    new PageStatusLog(L.GpoPickerStatus.Stopped),
                    new PageStatusAction(retry, LoadCommand));
            }
        }
        catch (Exception ex) when (GroupPolicyText.IsExpected(ex))
        {
            if (reading.IsEnded)
            {
                return;
            }

            var code = ResultCode.Of(ex);
            _readState = PageStatus.Error(
                PageStatusText.Failure(
                    Localizer.Format(L.GpoPickerStatus.ReadFailed, domain), GroupPolicyText.Error(Localizer, Errors, ex), Localizer[L.Feedback.RetryNext], code),
                new PageStatusLog(L.GpoPickerStatus.ReadFailed).WithCode(code),
                new PageStatusAction(Localizer[L.Common.Retry], LoadCommand),
                canClose: false);
        }
        finally
        {
            UpdateLasting();
        }
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var filter = FilterText?.Trim() ?? string.Empty;
        Items = filter.Length == 0 ? _all : [.. _all.Where(i => i.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase))];
        if (SelectedItem is not null && !Items.Contains(SelectedItem))
        {
            SelectedItem = null;
        }

        UpdateSummary();
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();

        // The dialog's own sentences in the new language (why a read was stopped or failed stays as it was said).
        UpdateLasting();
        UpdateSummary();
    }

    protected override void OnSessionEnded()
    {
        // The line says for good that the session ended; a read that still runs ends without a word of its own (the
        // state first, so the line goes straight from "working" to it).
        UpdateLasting();
        Status.Operation?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // What still runs says nothing any more.
            Status.Operation?.Dispose();
        }
    }

    /// <summary>
    /// The state of the dialog that lasts, the most important first: its session ended, or it was opened without one (an
    /// error that cannot be closed - nothing can be read), else why the list is empty after a read that did not end.
    /// </summary>
    private void UpdateLasting() => Status.SetLasting(
        SessionEndedStatus ?? (Session is null ? PageStatus.Error(NoSessionMessage, new PageStatusLog(L.Errors.NotConnected), canClose: false) : _readState));

    /// <summary>The summary of the list: how many GPOs it shows, of how many with the filter (nothing before it was read once).</summary>
    private void UpdateSummary() => Status.SetIdle(!_listed ? null
        : Items.Count == _all.Count ? Localizer.Format(L.GpoPickerStatus.Found, _all.Count)
        : Localizer.Format(L.GroupPolicyEditing.GposShown, Items.Count, _all.Count));

    [RelayCommand(CanExecute = nameof(CanOk))]
    private void Ok()
    {
        Result = MultiSelect ? [.. _all.Where(i => i.IsChecked).Select(i => i.Gpo)] : SelectedItem is { } item ? [item.Gpo] : [];
        if (Result.Count > 0)
        {
            RequestClose(true);
        }
    }

    private bool CanOk() => MultiSelect ? _all.Any(i => i.IsChecked) : SelectedItem is not null;

    [RelayCommand]
    private void Cancel() => RequestClose(false);
}
