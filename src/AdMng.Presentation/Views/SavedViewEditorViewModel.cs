using System.Diagnostics;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AdMng.Presentation.Views;

/// <summary>
/// Editor of a saved view: name, search base (container picker), scope, LDAP filter with live validation and a
/// test run that counts matching objects on the DC.
/// </summary>
/// <remarks>
/// The window has one status line (<see cref="Status"/>; track V, stage V2b, design note
/// docs/dev/v-interface-renewal.md "V2b-2 as built"). Its one operation is the test of the view on the domain
/// controller: the line says that the matching objects are being counted (with Stop - the Test button is its Cancel),
/// how many there are in the search base, that the count was stopped or why it failed, and why a test was not started.
/// A filter that cannot be read (<see cref="FilterError"/>) is no status: it is computed from the text while it is
/// shown and stands under the filter as a note. The editor is not pinned to a session - a view can be edited and saved
/// without one - so it has no state that lasts.
/// </remarks>
public sealed partial class SavedViewEditorViewModel : ObservableObject, IHasPageStatus
{
    private const string ListFilterDefault = "(objectClass=*)";

    private readonly ISessionManager _sessions;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _localizer;
    private readonly ErrorTranslator _errors;
    private readonly SavedView _original;

    /// <remarks>
    /// <c>dispatcher</c>: the UI thread, for the status line; <c>statusLog</c>: the diagnostic log the status line
    /// writes its value-free copies to (null in hosts without logging).
    /// </remarks>
    public SavedViewEditorViewModel(
        ISessionManager sessions,
        IDialogService dialogs,
        ILocalizer localizer,
        ErrorTranslator errors,
        IUiDispatcher dispatcher,
        SavedView? existing = null,
        bool? isNew = null,
        ILogger<PageStatusHolder>? statusLog = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _errors = errors ?? throw new ArgumentNullException(nameof(errors));

        // Before the fields are set: a change of the view takes what the line said about a test with it.
        Status = new PageStatusHolder(localizer, dispatcher ?? throw new ArgumentNullException(nameof(dispatcher)), statusLog);
        _original = existing ?? new SavedView();
        IsNew = isNew ?? existing is null;
        Name = _original.Name;
        BaseDn = _original.BaseDn;
        Scope = _original.Scope;
        Filter = _original.Filter;
        Icon = _original.Icon;
        Scopes =
        [
            new(SearchScope.OneLevel, localizer[DisplayNames.ScopeKey(SearchScope.OneLevel)]),
            new(SearchScope.Subtree, localizer[DisplayNames.ScopeKey(SearchScope.Subtree)]),
        ];
        ValidateFilter();
    }

    /// <summary>Raised by <see cref="SaveCommand"/> (true) and <see cref="CancelCommand"/> (false) so the dialog closes.</summary>
    public event EventHandler<bool>? CloseRequested;

    public bool IsNew { get; }

    public string WindowTitle => _localizer[IsNew ? L.Views.NewViewTitle : L.Views.EditViewTitle];

    /// <summary>
    /// The status of the window - what its one status line shows: the running test of the view (with Stop) and how it
    /// ended. Empty at rest.
    /// </summary>
    public PageStatusHolder Status { get; }

    public IReadOnlyList<Option<SearchScope>> Scopes { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>Search base; may contain {domain}.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestCommand))]
    [NotifyPropertyChangedFor(nameof(BaseDnDisplay))]
    public partial string BaseDn { get; set; } = SavedView.DomainPlaceholder;

    [ObservableProperty]
    public partial SearchScope Scope { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestCommand))]
    public partial string Filter { get; set; } = ListFilterDefault;

    [ObservableProperty]
    public partial ObjectIcon Icon { get; set; }

    /// <summary>Localized filter syntax error, or null: the note under the filter (why Save and Test are not possible).</summary>
    [ObservableProperty]
    public partial string? FilterError { get; private set; }

    /// <summary>
    /// How the last test ended, as plain text ("Matching objects: 4 (0.02 s)"; the status line says it - kept for
    /// callers that read it); null once the filter changed.
    /// </summary>
    [ObservableProperty]
    public partial string? TestResult { get; private set; }

    /// <summary>Canonical form of the base for display ("contoso.test/Firma").</summary>
    public string BaseDnDisplay
    {
        get
        {
            var resolved = ResolvedBaseDn();
            return DistinguishedName.TryParse(resolved, out var dn) && !dn.IsRoot ? dn.ToCanonicalName().TrimEnd('/') : BaseDn;
        }
    }

    /// <summary>The saved view after <see cref="SaveCommand"/> ran.</summary>
    public SavedView? Result { get; private set; }

    [RelayCommand]
    private async Task BrowseBaseDnAsync()
    {
        var session = _sessions.Current;
        if (session is null)
        {
            return;
        }

        var picked = await _dialogs.PickContainerAsync(session, ResolvedBaseDn()).ConfigureAwait(true);
        if (picked is not null)
        {
            BaseDn = SavedView.ToPortableDn(picked, session.Info.DefaultNamingContext);
        }
    }

    /// <summary>
    /// Counts the objects the view lists - its search base, scope and filter - on the domain controller. The test is
    /// the operation of the window: its status line says so with Stop - the Test button is its Cancel as well, and so is
    /// the cancel of this command, which the view uses when the window closes - and says how many objects match, that
    /// the count was stopped, or why it failed. A view that changes while it is tested ends the test without a word: the
    /// count would be about another view.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTest), IncludeCancelCommand = true)]
    private async Task TestAsync(CancellationToken cancellationToken)
    {
        // A new attempt takes what the line said about the one before with it.
        Status.Clear();
        var session = _sessions.Current;
        if (session is null)
        {
            TestResult = _localizer[L.Errors.NotConnected];
            Status.Show(PageStatus.Attention(
                PageStatusText.Failure(_localizer[L.SavedViewEditorStatus.NotTested], TestResult), new PageStatusLog(L.Errors.NotConnected)));
            return;
        }

        var location = BaseDnDisplay;
        TestResult = _localizer[L.Views.Testing];

        // "Working" from here on, with Stop; however this method is left, the line does not stay in "working".
        using var testing = Status.Begin(
            _localizer.Format(L.SavedViewEditorStatus.Testing, location), new PageStatusLog(L.SavedViewEditorStatus.Testing));
        using var stop = cancellationToken.Register(testing.Cancel);
        var token = testing.Token;
        var search = new DirectorySearch
        {
            BaseDn = ResolvedBaseDn(),
            Scope = Scope,
            Filter = LdapFilter.Wrap(Filter),
            Attributes = [],
        };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var count = await Task.Run(
                async () =>
                {
                    var n = 0;
                    await foreach (var unused in session.SearchAsync(search, token).ConfigureAwait(false))
                    {
                        n++;
                    }

                    return n;
                },
                token).ConfigureAwait(true);
            if (testing.IsEnded)
            {
                // The view changed meanwhile: the count is about another view.
                return;
            }

            if (testing.IsCancelling)
            {
                // The domain controller answered although Stop was pressed: stopped is what the operator asked for.
                TestStopped(testing);
                return;
            }

            var seconds = stopwatch.Elapsed.TotalSeconds.ToString("0.00", _localizer.Culture);
            TestResult = _localizer.Format(L.Views.TestResult, count, seconds);
            testing.CompleteDone(
                _localizer.Format(L.SavedViewEditorStatus.Tested, location, count, seconds),
                new PageStatusLog(L.SavedViewEditorStatus.Tested, count));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Only a test the operator stopped says so; one that ended with its view says nothing.
            if (testing.IsCancelling)
            {
                TestStopped(testing);
            }
        }
        catch (Exception ex)
        {
            if (testing.IsEnded)
            {
                return;
            }

            // The editor is as it was: a result that can be closed, with the way to try again.
            TestResult = _errors.Translate(ex);
            var code = ResultCode.Of(ex);
            testing.CompleteError(
                PageStatusText.Failure(
                    _localizer.Format(L.SavedViewEditorStatus.TestFailed, location), TestResult, _localizer[L.Feedback.RetryNext], code),
                new PageStatusLog(L.SavedViewEditorStatus.TestFailed).WithCode(code),
                new PageStatusAction(_localizer[L.Common.Retry], TestCommand));
        }
    }

    /// <summary>The operator stopped the test: what was not done, and the button that tests again.</summary>
    private void TestStopped(OperationScope testing)
    {
        TestResult = _localizer[L.Errors.Cancelled];
        var again = _localizer[L.Views.Test];
        testing.CompleteAttention(
            _localizer.Format(L.SavedViewEditorStatus.TestStopped, again),
            new PageStatusLog(L.SavedViewEditorStatus.TestStopped),
            new PageStatusAction(again, TestCommand));
    }

    private bool CanTest() => FilterError is null && !string.IsNullOrWhiteSpace(BaseDn);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        Result = _original with
        {
            Name = Name.Trim(),
            BaseDn = BaseDn.Trim(),
            Scope = Scope,
            Filter = LdapFilter.Wrap(Filter),
            Icon = Icon,
        };
        CloseRequested?.Invoke(this, true);
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(Name) && FilterError is null && !string.IsNullOrWhiteSpace(BaseDn);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    partial void OnFilterChanged(string value) => ValidateFilter();

    // What the line said about a test - and a test that still runs - was about the view of before.
    partial void OnBaseDnChanged(string value) => Status.Clear();

    partial void OnScopeChanged(SearchScope value) => Status.Clear();

    private void ValidateFilter()
    {
        FilterError = LdapFilter.Validate(Filter) is null ? null : _localizer[L.Views.InvalidFilter];
        TestResult = null;
        Status.Clear();
        SaveCommand.NotifyCanExecuteChanged();
        TestCommand.NotifyCanExecuteChanged();
    }

    private string ResolvedBaseDn()
    {
        var domain = _sessions.Current?.Info.DefaultNamingContext;
        return domain is null ? BaseDn : BaseDn.Replace(SavedView.DomainPlaceholder, domain, StringComparison.OrdinalIgnoreCase);
    }
}
