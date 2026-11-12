using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using AdMng.Core.Directory;
using AdMng.Core.Filters;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AdMng.Presentation.Filters;

/// <summary>One condition row of the filter editor: attribute, operator, value.</summary>
public sealed partial class FilterConditionViewModel : ObservableObject
{
    internal FilterConditionViewModel(FilterGroupViewModel group, IReadOnlyList<Option<FilterOperator>> operators, FilterCondition? condition = null)
    {
        Group = group;
        Operators = operators;
        Attribute = condition?.Attribute ?? string.Empty;
        Operator = operators.FirstOrDefault(o => o.Value == (condition?.Operator ?? FilterOperator.Equals)) ?? operators[0];
        Value = condition?.Value ?? string.Empty;
    }

    /// <summary>Raised when the condition changed (the editor regenerates the preview).</summary>
    public event EventHandler? Changed;

    public FilterGroupViewModel Group { get; }

    public IReadOnlyList<Option<FilterOperator>> Operators { get; }

    /// <summary>LDAP display name, typed or picked from the schema's attributes.</summary>
    [ObservableProperty]
    public partial string Attribute { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsValue), nameof(IsMembership), nameof(NeedsAttribute))]
    public partial Option<FilterOperator> Operator { get; set; }

    [ObservableProperty]
    public partial string Value { get; set; }

    /// <summary>Localized problem of this condition, or null.</summary>
    [ObservableProperty]
    public partial string? Issue { get; internal set; }

    /// <summary>False for "exists" / "does not exist".</summary>
    public bool NeedsValue => FilterCompiler.NeedsValue(Operator.Value);

    /// <summary>"member of": the value is a group DN chosen with "Wybierz grupę…".</summary>
    public bool IsMembership => FilterCompiler.IsMembership(Operator.Value);

    /// <summary>"member of" ignores the attribute (memberOf).</summary>
    public bool NeedsAttribute => !IsMembership;

    public FilterCondition ToCondition() => new(IsMembership ? string.Empty : Attribute.Trim(), Operator.Value, NeedsValue ? Value.Trim() : string.Empty);

    partial void OnAttributeChanged(string value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnOperatorChanged(Option<FilterOperator> value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnValueChanged(string value) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>A group of conditions combined with "I" or "LUB".</summary>
public sealed partial class FilterGroupViewModel : ObservableObject
{
    internal FilterGroupViewModel(IReadOnlyList<Option<FilterCombinator>> combinators, FilterCombinator combinator)
    {
        Combinators = combinators;
        Combinator = combinators.First(c => c.Value == combinator);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<Option<FilterCombinator>> Combinators { get; }

    [ObservableProperty]
    public partial Option<FilterCombinator> Combinator { get; set; }

    public ObservableCollection<FilterConditionViewModel> Conditions { get; } = [];

    /// <summary>1-based number of the group.</summary>
    [ObservableProperty]
    public partial int Number { get; internal set; }

    /// <summary>"Grupa 2" in the UI language.</summary>
    [ObservableProperty]
    public partial string Header { get; internal set; } = string.Empty;

    public FilterConditionGroup ToGroup() => new() { Combinator = Combinator.Value, Conditions = [.. Conditions.Select(c => c.ToCondition())] };

    partial void OnCombinatorChanged(Option<FilterCombinator> value) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// The filter builder (README U4, "Własne filtry"): conditions on any attribute of the schema, grouped and combined with
/// I/LUB, a live preview of the LDAP filter built only through <see cref="FilterCompiler"/> (CLAUDE.md rule 3), a raw LDAP
/// filter for advanced users (validated), a test count on the DC and Save by name. Modal; <see cref="Result"/> after Save.
/// </summary>
/// <remarks>
/// The window has one status line (<see cref="Status"/>; track V, stage V2b, design note
/// docs/dev/v-interface-renewal.md "V2b-2 as built"). Its one operation is the test of the filter on the domain
/// controller: the line says that the matching objects are being counted (with Stop - the Test button is its Cancel),
/// how many there are, that the count was stopped or why it failed, and why a test was not started. What is wrong with
/// the filter itself (<see cref="Error"/>) is no status: it is computed from the conditions while they are shown and
/// stands next to the filter as a note. The editor is not pinned to a session - a filter can be edited and saved
/// without one - so it has no state that lasts.
/// </remarks>
public sealed partial class FilterEditorViewModel : ObservableObject, IHasPageStatus
{
    private readonly ISessionManager _sessions;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _localizer;
    private readonly ErrorTranslator _errors;
    private readonly TimeProvider _time;
    private readonly SavedFilter _original;
    private bool _settingRaw;

    /// <remarks>
    /// <c>dispatcher</c>: the UI thread, for the status line; <c>statusLog</c>: the diagnostic log the status line
    /// writes its value-free copies to (null in hosts without logging).
    /// </remarks>
    public FilterEditorViewModel(
        ISessionManager sessions,
        IDialogService dialogs,
        ILocalizer localizer,
        ErrorTranslator errors,
        TimeProvider time,
        IUiDispatcher dispatcher,
        SavedFilter? existing = null,
        ILogger<PageStatusHolder>? statusLog = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _errors = errors ?? throw new ArgumentNullException(nameof(errors));
        _time = time ?? throw new ArgumentNullException(nameof(time));

        // Before the first Regenerate: a change of the filter takes what the line said about a test with it.
        Status = new PageStatusHolder(localizer, dispatcher ?? throw new ArgumentNullException(nameof(dispatcher)), statusLog);
        IsNew = existing is null;
        _original = existing ?? new SavedFilter();
        Operators = [.. Enum.GetValues<FilterOperator>().Select(o => new Option<FilterOperator>(o, localizer[FilterTexts.OperatorKey(o)]))];
        Combinators = [.. Enum.GetValues<FilterCombinator>().Select(c => new Option<FilterCombinator>(c, localizer[FilterTexts.CombinatorKey(c)]))];
        Attributes = [.. (sessions.Current?.Schema.Attributes ?? [])
            .Where(a => !a.IsBinary)
            .Select(a => a.LdapName)
            .Order(StringComparer.OrdinalIgnoreCase)];
        Name = _original.Name;
        Description = _original.Description ?? string.Empty;
        var definition = _original.Definition;
        Combinator = Combinators.First(c => c.Value == definition.Combinator);
        foreach (var group in definition.Groups)
        {
            var model = AddGroupCore(group.Combinator);
            foreach (var condition in group.Conditions)
            {
                AddConditionCore(model, condition);
            }
        }

        if (Groups.Count == 0)
        {
            AddConditionCore(AddGroupCore(FilterCombinator.All), null);
        }

        Groups.CollectionChanged += OnGroupsChanged;
        if (definition.IsRaw)
        {
            _settingRaw = true;
            IsRaw = true;
            RawFilter = definition.RawLdap!;
            _settingRaw = false;
        }

        Regenerate();
    }

    /// <summary>Raised by <see cref="SaveCommand"/> (true) and <see cref="CancelCommand"/> (false) so the dialog closes.</summary>
    public event EventHandler<bool>? CloseRequested;

    public bool IsNew { get; }

    public string WindowTitle => _localizer[IsNew ? L.Filters.NewTitle : L.Filters.EditTitle];

    /// <summary>
    /// The status of the window - what its one status line shows: the running test of the filter (with Stop) and how it
    /// ended. Empty at rest.
    /// </summary>
    public PageStatusHolder Status { get; }

    public IReadOnlyList<Option<FilterOperator>> Operators { get; }

    public IReadOnlyList<Option<FilterCombinator>> Combinators { get; }

    /// <summary>The attributes of the session's schema (text values only), for the attribute picker.</summary>
    public IReadOnlyList<string> Attributes { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    /// <summary>How the groups combine.</summary>
    [ObservableProperty]
    public partial Option<FilterCombinator> Combinator { get; set; }

    public ObservableCollection<FilterGroupViewModel> Groups { get; } = [];

    /// <summary>The operator types the LDAP filter by hand (advanced).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBuilder))]
    public partial bool IsRaw { get; set; }

    public bool IsBuilder => !IsRaw;

    [ObservableProperty]
    public partial string RawFilter { get; set; } = string.Empty;

    /// <summary>The LDAP filter of the conditions (or the raw filter), empty while invalid.</summary>
    [ObservableProperty]
    public partial string Preview { get; private set; } = string.Empty;

    /// <summary>Localized problems of the filter, or null: the note next to the filter (why Save and Test are not possible).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestCommand))]
    public partial string? Error { get; private set; }

    /// <summary>"Constructed attributes are checked after the search: …", or null.</summary>
    [ObservableProperty]
    public partial string? ClientCheckNote { get; private set; }

    /// <summary>
    /// How the last test ended, as plain text ("Matching objects: 4 (0.02 s)"; the status line says it - kept for
    /// callers that read it); null once the filter changed.
    /// </summary>
    [ObservableProperty]
    public partial string? TestResult { get; private set; }

    /// <summary>The saved filter after <see cref="SaveCommand"/> ran.</summary>
    public SavedFilter? Result { get; private set; }

    /// <summary>The definition as edited now.</summary>
    public FilterDefinition Definition => IsRaw
        ? FilterDefinition.Raw(RawFilter.Trim())
        : new FilterDefinition { Combinator = Combinator.Value, Groups = [.. Groups.Select(g => g.ToGroup())] };

    [RelayCommand]
    private void AddGroup() => AddConditionCore(AddGroupCore(FilterCombinator.All), null);

    [RelayCommand]
    private void RemoveGroup(FilterGroupViewModel? group)
    {
        if (group is not null && Groups.Count > 1 && Groups.Remove(group))
        {
            group.Changed -= OnPartChanged;
            foreach (var condition in group.Conditions)
            {
                condition.Changed -= OnPartChanged;
            }
        }
    }

    [RelayCommand]
    private void AddCondition(FilterGroupViewModel? group) => AddConditionCore(group ?? Groups[^1], null);

    [RelayCommand]
    private void RemoveCondition(FilterConditionViewModel? condition)
    {
        if (condition is null || !condition.Group.Conditions.Remove(condition))
        {
            return;
        }

        condition.Changed -= OnPartChanged;
        if (condition.Group.Conditions.Count == 0 && Groups.Count > 1)
        {
            RemoveGroup(condition.Group);
        }

        Regenerate();
    }

    /// <summary>"Wybierz grupę…": the object picker (groups) of the session; the DN is stored in the portable form.</summary>
    [RelayCommand]
    private async Task PickGroupAsync(FilterConditionViewModel? condition)
    {
        if (condition is null || _sessions.Current is not { } session)
        {
            return;
        }

        var picked = await _dialogs.PickObjectsAsync(session, [ObjectKind.Group], multiSelect: false).ConfigureAwait(true);
        if (picked.Count > 0)
        {
            condition.Value = Views.SavedView.ToPortableDn(picked[0], session.Info.DefaultNamingContext);
        }
    }

    /// <summary>
    /// Counts the objects the filter matches on the domain controller. The test is the operation of the window: its
    /// status line says so with Stop - the Test button is its Cancel as well, and so is the cancel of this command, which
    /// the view uses when the window closes - and says how many objects match, that the count was stopped, or why it
    /// failed. A filter that changes while it is tested ends the test without a word: the count would be about another
    /// filter.
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
                PageStatusText.Failure(_localizer[L.FilterEditorStatus.NotTested], TestResult), new PageStatusLog(L.Errors.NotConnected)));
            return;
        }

        SavedFilterPlan plan;
        try
        {
            plan = SavedFilterPlan.Create([Definition], Context(session));
        }
        catch (FilterDefinitionException)
        {
            return;
        }

        var domain = ConfirmationText.Domain(session);
        TestResult = _localizer[L.Views.Testing];

        // "Working" from here on, with Stop; however this method is left, the line does not stay in "working".
        using var testing = Status.Begin(_localizer.Format(L.FilterEditorStatus.Testing, domain), new PageStatusLog(L.FilterEditorStatus.Testing));
        using var stop = cancellationToken.Register(testing.Cancel);
        var token = testing.Token;
        var search = new DirectorySearch
        {
            BaseDn = session.Info.DefaultNamingContext,
            Scope = SearchScope.Subtree,
            Filter = plan.ServerFilter ?? Lists.ListLocation.AllObjectsFilter,
            Attributes = plan.Attributes,
        };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var count = await Task.Run(
                async () =>
                {
                    var n = 0;
                    await foreach (var obj in session.SearchAsync(search, token).ConfigureAwait(false))
                    {
                        if (plan.Matches(obj))
                        {
                            n++;
                        }
                    }

                    return n;
                },
                token).ConfigureAwait(true);
            if (testing.IsEnded)
            {
                // The filter changed meanwhile: the count is about another filter.
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
                _localizer.Format(L.FilterEditorStatus.Tested, domain, count, seconds), new PageStatusLog(L.FilterEditorStatus.Tested, count));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Only a test the operator stopped says so; one that ended with its filter says nothing.
            if (testing.IsCancelling)
            {
                TestStopped(testing);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (testing.IsEnded)
            {
                return;
            }

            // The editor is as it was: a result that can be closed, with the way to try again.
            TestResult = _errors.Translate(ex);
            var code = ResultCode.Of(ex);
            testing.CompleteError(
                PageStatusText.Failure(_localizer.Format(L.FilterEditorStatus.TestFailed, domain), TestResult, _localizer[L.Feedback.RetryNext], code),
                new PageStatusLog(L.FilterEditorStatus.TestFailed).WithCode(code),
                new PageStatusAction(_localizer[L.Common.Retry], TestCommand));
        }
    }

    /// <summary>The operator stopped the test: what was not done, and the button that tests again.</summary>
    private void TestStopped(OperationScope testing)
    {
        TestResult = _localizer[L.Errors.Cancelled];
        var again = _localizer[L.Views.Test];
        testing.CompleteAttention(
            _localizer.Format(L.FilterEditorStatus.TestStopped, again),
            new PageStatusLog(L.FilterEditorStatus.TestStopped),
            new PageStatusAction(again, TestCommand));
    }

    private bool CanTest() => Error is null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        Result = _original with
        {
            Name = Name.Trim(),
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            Definition = Definition,
        };
        CloseRequested?.Invoke(this, true);
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(Name) && Error is null;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    partial void OnCombinatorChanged(Option<FilterCombinator> value) => Regenerate();

    partial void OnIsRawChanged(bool value)
    {
        if (value && !_settingRaw)
        {
            // Switching to the raw filter starts from the generated one.
            RawFilter = Preview;
        }

        Regenerate();
    }

    partial void OnRawFilterChanged(string value) => Regenerate();

    private FilterGroupViewModel AddGroupCore(FilterCombinator combinator)
    {
        var group = new FilterGroupViewModel(Combinators, combinator);
        group.Changed += OnPartChanged;
        group.Conditions.CollectionChanged += (_, _) => Regenerate();
        Groups.Add(group);
        Renumber();
        return group;
    }

    private void AddConditionCore(FilterGroupViewModel group, FilterCondition? condition)
    {
        var model = new FilterConditionViewModel(group, Operators, condition);
        model.Changed += OnPartChanged;
        group.Conditions.Add(model);
    }

    private void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Renumber();
        Regenerate();
    }

    private void OnPartChanged(object? sender, EventArgs e) => Regenerate();

    private void Renumber()
    {
        for (var i = 0; i < Groups.Count; i++)
        {
            Groups[i].Number = i + 1;
            Groups[i].Header = _localizer.Format(L.Filters.Group, i + 1);
        }
    }

    private FilterContext Context(IDirectorySession? session) => new()
    {
        Now = _time.GetUtcNow(),
        DefaultNamingContext = session?.Info.DefaultNamingContext,
        Schema = session?.Schema,
    };

    /// <summary>Validates the definition, shows the problems next to the conditions and regenerates the preview.</summary>
    private void Regenerate()
    {
        var session = _sessions.Current;
        var context = Context(session);
        var definition = Definition;
        var issues = FilterCompiler.Validate(definition, context);
        foreach (var condition in Groups.SelectMany(g => g.Conditions))
        {
            condition.Issue = null;
        }

        foreach (var issue in issues.Where(i => i.GroupIndex >= 0 && !IsRaw))
        {
            Groups[issue.GroupIndex].Conditions[issue.ConditionIndex].Issue = _localizer[FilterTexts.IssueKey(issue.Code)];
        }

        // What the line said about a test - and a test that still runs - was about the filter of before.
        TestResult = null;
        Status.Clear();
        if (issues.Count > 0)
        {
            Preview = string.Empty;
            ClientCheckNote = null;
            Error = string.Join(Environment.NewLine, issues.Select(i => FilterTexts.Issue(_localizer, i)));
            return;
        }

        Error = null;
        Preview = FilterCompiler.ToLdap(definition, context);
        var constructed = definition.AllConditions
            .Select(FilterCompiler.AttributeOf)
            .Where(a => FilterCompiler.IsConstructed(a, context.Schema))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        ClientCheckNote = !IsRaw && constructed.Count > 0 ? _localizer.Format(L.Filters.ClientCheck, string.Join(", ", constructed)) : null;
    }
}

/// <summary>Localization keys and texts of the filter builder.</summary>
public static class FilterTexts
{
    public static string OperatorKey(FilterOperator op) => $"Filters.Operator.{op}";

    public static string CombinatorKey(FilterCombinator combinator) => $"Filters.Combinator.{combinator}";

    public static string IssueKey(FilterIssueCode code) => $"Filters.Issue.{code}";

    /// <summary>"Grupa 2, warunek 1: Wpisz wartość." (or only the text for problems of the whole filter).</summary>
    public static string Issue(ILocalizer localizer, FilterIssue issue)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(issue);
        var text = localizer[IssueKey(issue.Code)];
        return issue.GroupIndex < 0 ? text : localizer.Format(L.Filters.IssueAt, issue.GroupIndex + 1, issue.ConditionIndex + 1, text);
    }

    /// <summary>A one-line summary of a definition for lists ("department równa się IT I …" or the raw filter).</summary>
    public static string Summary(ILocalizer localizer, FilterDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.IsRaw)
        {
            return definition.RawLdap!.Trim();
        }

        string Condition(FilterCondition c) => FilterCompiler.NeedsValue(c.Operator)
            ? FilterCompiler.IsMembership(c.Operator)
                ? $"{localizer[OperatorKey(c.Operator)]} {DistinguishedNameText(c.Value)}"
                : $"{c.Attribute} {localizer[OperatorKey(c.Operator)]} {c.Value}"
            : $"{c.Attribute} {localizer[OperatorKey(c.Operator)]}";

        string Join(FilterCombinator combinator, IEnumerable<string> parts) =>
            string.Join($" {localizer[combinator == FilterCombinator.Any ? L.Filters.Or : L.Filters.And]} ", parts);

        var groups = definition.Groups.Where(g => g.Conditions.Count > 0).ToList();
        return Join(definition.Combinator, groups.Select(g =>
        {
            var text = Join(g.Combinator, g.Conditions.Select(Condition));
            return groups.Count > 1 && g.Conditions.Count > 1 ? $"({text})" : text;
        }));
    }

    private static string DistinguishedNameText(string dn) =>
        DistinguishedName.TryParse(dn.Replace(FilterCompiler.DomainPlaceholder, "DC=x", StringComparison.OrdinalIgnoreCase), out var parsed) && !parsed.IsRoot
            ? parsed.Name
            : dn;
}
