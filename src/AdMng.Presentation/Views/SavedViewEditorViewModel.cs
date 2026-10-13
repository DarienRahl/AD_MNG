using System.Diagnostics;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Views;

/// <summary>
/// Editor of a saved view: name, search base (container picker), scope, LDAP filter with live validation and a
/// test run that counts matching objects on the DC.
/// </summary>
public sealed partial class SavedViewEditorViewModel : ObservableObject
{
    private const string ListFilterDefault = "(objectClass=*)";

    private readonly ISessionManager _sessions;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _localizer;
    private readonly ErrorTranslator _errors;
    private readonly SavedView _original;

    public SavedViewEditorViewModel(
        ISessionManager sessions,
        IDialogService dialogs,
        ILocalizer localizer,
        ErrorTranslator errors,
        SavedView? existing = null,
        bool? isNew = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _errors = errors ?? throw new ArgumentNullException(nameof(errors));
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

    /// <summary>Localized filter syntax error, or null.</summary>
    [ObservableProperty]
    public partial string? FilterError { get; private set; }

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

    [RelayCommand(CanExecute = nameof(CanTest), IncludeCancelCommand = true)]
    private async Task TestAsync(CancellationToken cancellationToken)
    {
        var session = _sessions.Current;
        if (session is null)
        {
            TestResult = _localizer[L.Errors.NotConnected];
            return;
        }

        TestResult = _localizer[L.Views.Testing];
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
                    await foreach (var unused in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
                    {
                        n++;
                    }

                    return n;
                },
                cancellationToken).ConfigureAwait(true);
            TestResult = _localizer.Format(L.Views.TestResult, count, stopwatch.Elapsed.TotalSeconds.ToString("0.00", _localizer.Culture));
        }
        catch (OperationCanceledException)
        {
            TestResult = _localizer[L.Errors.Cancelled];
        }
        catch (Exception ex)
        {
            TestResult = _errors.Translate(ex);
        }
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

    private void ValidateFilter()
    {
        FilterError = LdapFilter.Validate(Filter) is null ? null : _localizer[L.Views.InvalidFilter];
        TestResult = null;
        SaveCommand.NotifyCanExecuteChanged();
        TestCommand.NotifyCanExecuteChanged();
    }

    private string ResolvedBaseDn()
    {
        var domain = _sessions.Current?.Info.DefaultNamingContext;
        return domain is null ? BaseDn : BaseDn.Replace(SavedView.DomainPlaceholder, domain, StringComparison.OrdinalIgnoreCase);
    }
}
