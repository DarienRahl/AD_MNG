using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.BulkCreate;

/// <summary>
/// The change plan, the accumulated results and the undo of the bulk creation wizard (docs/dev/phase3-bulk-create.md 2.7,
/// owner B4): <see cref="PlanAsync"/> computes again with the check step's values frozen, reads the shared password last on
/// the UI thread and builds the plan; every execution of the window (canary and rest, retries, a second plan after
/// <em>Wstecz</em>) is merged into <see cref="Accumulated"/>; <see cref="PlanUndoAsync"/> deletes everything this window
/// created. The batch editor's create branches call these members.
/// </summary>
/// <remarks>
/// Rule 2: the passwords exist only in the plans, in <see cref="Accumulated"/> and in the <see cref="Accounts"/>; the shared
/// password is read from the boxes at plan time and cleared once the plan was built. Everything here is dropped on
/// <see cref="Dispose"/> (rule 1).
/// </remarks>
public sealed partial class BulkCreateViewModel
{
    /// <summary>Canaries tried at most when a canary's Create fails with a value problem of its own row (2.7).</summary>
    internal const int MaxCanaries = 3;

    private readonly List<CreatedAccountViewModel> _accounts = [];
    private readonly Dictionary<int, string> _createdRows = [];
    private readonly HashSet<string> _released = new(StringComparer.OrdinalIgnoreCase);

    // Snapshots handed to the computations (they run on the thread pool while the UI thread may mark rows created).
    private IReadOnlyDictionary<int, string> _createdSnapshot = new Dictionary<int, string>();
    private IReadOnlyCollection<string> _releasedSnapshot = [];

    // The computation of the last create plan: the basis of the retry plan (2.5) and of the run notes.
    private BulkCreateComputation? _planned;
    private string? _plannedTitle;
    private ChangePlan? _samplePlan;
    private Task _readingCreated = Task.CompletedTask;
    private readonly CancellationTokenSource _planLifetime = new();

    /// <summary>Every execution of this window merged (<see cref="BulkCreateResults.Merge"/>); null before the first.</summary>
    public BatchResult? Accumulated { get; private set; }

    /// <summary>The objects this window created over all executions (GUIDs read after each execution; memory only).</summary>
    public IReadOnlyList<CreatedObject> CreatedObjects { get; private set; } = [];

    /// <summary>The password mode of the last plan (captured at plan time; <c>GeneratesPasswords</c> of the batch editor).</summary>
    public InitialPasswordMode? PasswordModeAtPlan { get; private set; }

    /// <summary>
    /// A created account with a generated initial password was neither exported, nor copied, nor shown (the close guard;
    /// a shared password was typed by the operator, so it is not guarded).
    /// </summary>
    public bool HasUnhandedPasswords => _accounts.Any(a => a.HasPassword && a.IsGeneratedPassword && !a.WasHandedOut && !a.IsUndone);

    /// <summary>The created accounts (computers never; the batch editor shows them as <c>CreatedAccounts</c>).</summary>
    internal IReadOnlyList<CreatedAccountViewModel> Accounts => _accounts;

    /// <summary>Rows created by an earlier execution: row number → final DN (<see cref="BulkCreateInput.CreatedRows"/>).</summary>
    internal IReadOnlyDictionary<int, string> CreatedRows => _createdSnapshot;

    /// <summary>Identifiers this window created and its own undo deleted (<see cref="BulkCreateInput.ReleasedValues"/>).</summary>
    internal IReadOnlyCollection<string> ReleasedValues => _releasedSnapshot;

    /// <summary>
    /// The title of the change (activity, audit, the batch editor's header): <em>Utworzenie kont (114) w jednostce „Nowi
    /// pracownicy”</em> – fixed when a plan is built, else from the check step's counts.
    /// </summary>
    public string ChangeTitle => _plannedTitle ?? TitleFor(PlannedCount(Check.Computation));

    /// <summary>The next execution starts with one canary object (a new create plan, a retry after a canary stop or with a new password).</summary>
    internal bool UsesCanary { get; private set; }

    /// <summary>The last create execution stopped after its canary: the rows that did not run stay not created.</summary>
    [ObservableProperty]
    public partial bool WasStoppedAfterCanary { get; private set; }

    /// <summary>The notes above the created accounts (canary stop, replication, accounts left disabled, the forest UPN hint).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> ResultNotes { get; private set; } = [];

    /// <summary>
    /// The domain refused the shared password: the results show <em>Nowe hasło dla kont bez hasła</em> and the retry reads
    /// <see cref="RetryPasswordInput"/> and <see cref="RetryConfirmPasswordInput"/>.
    /// </summary>
    [ObservableProperty]
    public partial bool NeedsRetryPassword { get; private set; }

    /// <summary>The retry's new shared password (set by <c>BulkResultsView</c>, never bound, read and cleared at retry time).</summary>
    public IPasswordInput? RetryPasswordInput { get; set; }

    /// <summary>The confirmation box of the retry's new shared password.</summary>
    public IPasswordInput? RetryConfirmPasswordInput { get; set; }

    /// <summary>The sheet's columns the operator can add to <em>Kopiuj tabelę…</em> (phone and pattern-only columns preselected).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SheetColumnOption> CopyColumns { get; private set; } = [];

    /// <summary>The objects the shown undo plan deletes (<c>UndoNote</c> of the batch editor); null before an undo was planned.</summary>
    internal int? UndoObjectCount { get; private set; }

    /// <summary>The localized reason why the last plan was not built (the batch editor shows it instead of an unexpected error).</summary>
    internal string? LastPlanError { get; private set; }

    /// <summary>Reads the shared password of step 3 (default: <see cref="BulkRulesViewModel.ReadSharedPassword"/>; tests replace it).</summary>
    internal Func<string?>? SharedPasswordReader { get; set; }

    /// <summary>The key of the typed confirmation of the next execution (a new create plan with or without initial passwords).</summary>
    internal string ConfirmationKey =>
        IsUserKind(_planned?.Input.Rules.Kind ?? ObjectKind) && PasswordModeAtPlan is InitialPasswordMode.Generate or InitialPasswordMode.SameForAll
            ? L.BulkCreateCheck.ConfirmCreate
            : L.BulkCreateCheck.ConfirmCreateWithoutPasswords;

    /// <summary>
    /// Shows a finished computation in the check step and keeps <paramref name="plan"/> as the plan of the next Preview –
    /// the smoke test and the screenshots of a window without a directory (no planner, no session, no execution).
    /// </summary>
    public void ShowSample(BulkCreateComputation computation, ChangePlan plan)
    {
        ArgumentNullException.ThrowIfNull(computation);
        ArgumentNullException.ThrowIfNull(plan);
        if (_disposed)
        {
            return;
        }

        _samplePlan = plan;
        Step = BulkCreateStep.Check;
        Check.Show(computation);
        NotifyStateChanged();
    }

    /// <summary>Goes to the check step and computes (tests that drive the wizard without steps 1–3).</summary>
    internal async Task EnterCheckStepAsync()
    {
        Step = BulkCreateStep.Check;
        await Check.OnEnteredAsync().ConfigureAwait(true);
        NotifyStateChanged();
    }

    /// <summary>
    /// The create plan (preview in create mode): refreshed lookups and a computation with <see cref="BulkCreateInput.FrozenValues"/>
    /// on the thread pool, the shared password read last on the UI thread, <see cref="IBulkCreatePlanner.BuildPlan"/> on the
    /// thread pool, the boxes cleared only after the plan was built.
    /// </summary>
    public async Task<ChangePlan> PlanAsync(IProgress<int> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        LastPlanError = null;
        if (_samplePlan is { } sample && Session is null)
        {
            _plannedTitle = sample.Title;
            PasswordModeAtPlan = InitialPasswordMode.Generate;
            return sample;
        }

        var session = RequireSession();

        // (1) A pending edit is computed first, then everything once more with the check's values frozen (2.4): nothing is
        // renumbered or disambiguated, a value taken since the check becomes an error of its row.
        await Check.EnsureCurrentAsync(cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (Check.Computation is not { } checkedRows)
        {
            throw PlanError(Check.Error ?? Services.Localizer[L.BulkCreateCheck.InputIncomplete]);
        }

        var frozen = new Dictionary<int, IReadOnlyList<BulkValue>>(checkedRows.Rows.Count);
        foreach (var row in checkedRows.Rows)
        {
            if (row.Status is BulkRowStatus.Ok or BulkRowStatus.Warning or BulkRowStatus.Error)
            {
                frozen[row.Number] = row.Values;
            }
        }

        var input = checkedRows.Input with
        {
            FrozenValues = frozen,
            PinnedNumbers = checkedRows.Numbers,
            CreatedRows = _createdSnapshot,
            ReleasedValues = _releasedSnapshot,
        };
        await Check.WaitForGlobalCatalogAsync(cancellationToken).ConfigureAwait(true);
        var lookups = Check.Lookups;
        lookups.Refresh();
        var planner = Engines.BulkCreate;
        var computation = await Task.Run(() => planner.ComputeAsync(session, input, lookups, progress, cancellationToken), cancellationToken)
            .ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        Check.ApplyStatuses(computation);

        // (2) The shared password last, on the UI thread: a failed or cancelled computation leaves it in the boxes.
        var kind = computation.Input.Rules.Kind;
        var mode = computation.Input.Options.PasswordMode;
        string? shared = null;
        if (IsUserKind(kind) && mode == InitialPasswordMode.SameForAll)
        {
            if (Rules.NeedsSharedPassword)
            {
                throw PlanError(Services.Localizer[L.BulkCreateCheck.ReenterPassword]);
            }

            shared = (SharedPasswordReader ?? Rules.ReadSharedPassword)();
            if (string.IsNullOrEmpty(shared))
            {
                throw PlanError(Rules.Error ?? Services.Localizer[L.BulkCreateCheck.ReenterPassword]);
            }
        }

        // (3) The plan (pure, quick): not cancelled once the password was read, so it is never lost half way.
        var title = TitleFor(PlannedCount(computation));
        var password = shared;
        var plan = await Task.Run(() => planner.BuildPlan(computation, password, title), CancellationToken.None).ConfigureAwait(true);
        shared = null;
        password = null;

        _planned = computation;
        _plannedTitle = title;
        PasswordModeAtPlan = IsUserKind(kind) ? mode : InitialPasswordMode.None;
        UsesCanary = true;
        UndoObjectCount = null;
        Changed?.Invoke(this, EventArgs.Empty);
        return plan;
    }

    /// <summary>
    /// The retry plan of <see cref="Accumulated"/> (<see cref="IBulkCreatePlanner.BuildRetryPlan"/>): after a refused shared
    /// password the new one is read from the retry boxes and given to every password step of the retry.
    /// </summary>
    public async Task<ChangePlan> PlanRetryAsync(CancellationToken cancellationToken)
    {
        LastPlanError = null;
        if (_planned is not { } computation || Accumulated is not { } accumulated)
        {
            throw PlanError(Services.Localizer[L.Batch.NothingToUndo]);
        }

        string? shared = null;
        if (NeedsRetryPassword)
        {
            var first = RetryPasswordInput?.GetPassword() ?? string.Empty;
            var second = RetryConfirmPasswordInput?.GetPassword() ?? string.Empty;
            if (first.Length == 0 || second.Length == 0)
            {
                throw PlanError(Services.Localizer[L.BulkCreateCheck.RetryPasswordRequired]);
            }

            if (!string.Equals(first, second, StringComparison.Ordinal))
            {
                throw PlanError(Services.Localizer[L.Batch.PasswordMismatch]);
            }

            shared = first;
        }

        var title = _plannedTitle ?? ChangeTitle;
        var planner = Engines.BulkCreate;
        var password = shared;
        var plan = await Task.Run(() => planner.BuildRetryPlan(computation, accumulated, password, title), CancellationToken.None).ConfigureAwait(true);
        if (password is not null)
        {
            // Rows the canary stopped never tried their password: they get the new one too (one password for all).
            plan = WithSharedPassword(plan, password);
            RetryPasswordInput?.Clear();
            RetryConfirmPasswordInput?.Clear();
        }

        // A new password, or rows that never ran after a canary stop: the retry starts with one canary again.
        UsesCanary = password is not null || WasStoppedAfterCanary;
        UndoObjectCount = null;
        cancellationToken.ThrowIfCancellationRequested();
        return plan;
    }

    /// <summary>The undo of everything this window created (<see cref="IBulkCreatePlanner.PlanUndoAsync"/>); null when nothing was created.</summary>
    public async Task<ChangePlan?> PlanUndoAsync(CancellationToken cancellationToken)
    {
        LastPlanError = null;
        await _readingCreated.ConfigureAwait(true);
        var session = RequireSession();
        if (_createdRows.Count == 0)
        {
            return null;
        }

        // Rows whose GUIDs could not be read right after their execution are read now (their undo is refused without one).
        if (Accumulated is { } accumulated && CreatedObjects.Any(o => o.ObjectGuid is null))
        {
            var missing = CreatedObjects.Where(o => o.ObjectGuid is null).Select(o => o.Row).ToHashSet();
            var subset = accumulated with { Objects = [.. accumulated.Objects.Where(o => o.Plan.SourceRow is { } row && missing.Contains(row))] };
            await ReadCreatedAsync(session, subset, cancellationToken).ConfigureAwait(true);
        }

        var created = CreatedObjects.Where(o => _createdRows.ContainsKey(o.Row)).ToList();
        if (created.Count == 0)
        {
            return null;
        }

        var title = Services.Localizer.Format(L.BulkCreateCheck.UndoTitle, _plannedTitle ?? ChangeTitle);
        var planner = Engines.BulkCreate;
        var plan = await Task.Run(() => planner.PlanUndoAsync(session, created, title, cancellationToken), cancellationToken).ConfigureAwait(true);
        UndoObjectCount = plan.Objects.Count(o => !o.HasErrors);
        return plan;
    }

    /// <summary>
    /// Executes a plan of the wizard through the coordinator (rule 5): a create plan starts with one <b>canary</b> object; only
    /// when every step of it succeeded the rest runs as a second plan (two activities and audit entries). A canary whose Create
    /// failed with <c>AlreadyExists</c> or <c>ConstraintViolation</c> (a value of its own row) hands over to the next object,
    /// at most <see cref="MaxCanaries"/>; any other failure stops: the rows that did not run are reported as not executed.
    /// </summary>
    internal async Task<BatchResult> ExecuteAsync(ChangePlan plan, string title, IDirectorySession session, Action<ActivityViewModel>? started)
    {
        ArgumentNullException.ThrowIfNull(plan);
        WasStoppedAfterCanary = false;
        var coordinator = Services.Coordinator;
        var executable = plan.IsBlocked ? [] : plan.Objects.Where(o => !o.HasErrors && !o.IsNoOp).ToList();
        if (!UsesCanary || executable.Count <= 1)
        {
            return await coordinator.ExecutePlanAsync(plan, title, session, started).ConfigureAwait(true);
        }

        var ran = new HashSet<ObjectPlan>(ReferenceEqualityComparer.Instance);
        BatchResult? done = null;
        var passed = false;
        for (var attempt = 0; attempt < MaxCanaries && attempt < executable.Count; attempt++)
        {
            var canary = executable[attempt];
            ran.Add(canary);
            var result = await coordinator.ExecutePlanAsync(plan with { Objects = [canary] }, title, session, started).ConfigureAwait(true);
            done = BulkCreateResults.Merge(done, result);
            if (result.Objects.FirstOrDefault() is not { } outcome || result.WasCancelled)
            {
                return WithNotRun(done, plan, ran);
            }

            if (outcome.Status == ObjectResultStatus.Succeeded)
            {
                passed = true;
                break;
            }

            var create = outcome.Changes.FirstOrDefault(c => c.Change.Action == ChangeAction.Create);
            if (create is not { Succeeded: false, ErrorKind: DirectoryErrorKind.AlreadyExists or DirectoryErrorKind.ConstraintViolation })
            {
                // Systematic (no encryption, no reset right, a refused password …): stop after this one account.
                break;
            }
        }

        if (!passed || IsDisposedState)
        {
            WasStoppedAfterCanary = true;
            return WithNotRun(done!, plan, ran);
        }

        var rest = plan with { Objects = [.. plan.Objects.Where(o => !ran.Contains(o))] };
        var restResult = await coordinator.ExecutePlanAsync(rest, title, session, started).ConfigureAwait(true);
        return BulkCreateResults.Merge(done, restResult);
    }

    /// <summary>An execution of a create or retry plan finished: merges it, marks the created rows, reads the GUIDs, updates the results.</summary>
    public void OnExecuted(BatchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_disposed)
        {
            return;
        }

        Accumulated = BulkCreateResults.Merge(Accumulated, result);
        var created = new List<int>();
        foreach (var obj in result.Objects)
        {
            if (obj.Plan.SourceRow is { } row && obj.Changes.Any(c => c.Succeeded && c.Change.Action == ChangeAction.Create))
            {
                _createdRows[row] = obj.FinalDn;
                created.Add(row);
            }
        }

        _createdSnapshot = new Dictionary<int, string>(_createdRows);
        Check.MarkCreated(created);
        UpdateAccounts();
        UpdateRunState();
        if (Session is { } session && created.Count + result.Objects.Count > 0)
        {
            var previous = _readingCreated;
            _readingCreated = ReadAfterAsync(previous, session, result);
        }

        NotifyStateChanged();
    }

    /// <summary>The undo executed: clears the created marks of the deleted objects and releases their identifiers.</summary>
    public void OnUndone(BatchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_disposed)
        {
            return;
        }

        var deleted = new HashSet<int>();
        foreach (var obj in result.Objects)
        {
            if (obj.Plan.SourceRow is { } row && obj.Changes.Any(c => c.Succeeded && c.Change.Action == ChangeAction.Delete))
            {
                deleted.Add(row);
            }
        }

        foreach (var account in _accounts.Where(a => deleted.Contains(a.Row)))
        {
            foreach (var value in new[] { account.Login, account.Upn, account.Mail, account.Name })
            {
                if (!string.IsNullOrEmpty(value))
                {
                    _released.Add(value);
                }
            }

            account.IsUndone = true;
            account.ClearPassword();
            account.Status = Services.Localizer[L.BulkCreateCheck.AccountUndone];
            account.Outcome = ObjectResultStatus.Skipped;
            account.Message = null;
        }

        foreach (var row in deleted)
        {
            _createdRows.Remove(row);
        }

        _createdSnapshot = new Dictionary<int, string>(_createdRows);
        _releasedSnapshot = [.. _released];

        CreatedObjects = [.. CreatedObjects.Where(o => !deleted.Contains(o.Row))];

        // The deleted objects (and their passwords) leave the accumulated results: a later execution or export never shows them.
        if (Accumulated is { } accumulated)
        {
            var remaining = accumulated.Objects.Where(o => o.Plan.SourceRow is not { } row || !deleted.Contains(row)).ToList();
            Accumulated = remaining.Count == 0
                ? null
                : accumulated with { Objects = remaining, Plan = accumulated.Plan with { Objects = [.. remaining.Select(o => o.Plan)] } };
        }

        UndoObjectCount = null;
        Check.OnUndone(deleted);
        UpdateRunState();
        NotifyStateChanged();
    }

    /// <summary><em>Kopiuj tabelę z hasłami…</em>: asks, then the table through <c>SetSecret</c>; the copied passwords count as handed out.</summary>
    [RelayCommand]
    private async Task CopyTableWithPasswordsAsync()
    {
        var accounts = _accounts.Where(a => !a.IsUndone).ToList();
        if (accounts.Count == 0)
        {
            Services.Notifications.Show(NotificationSeverity.Info, Services.Localizer[L.BulkCreateCheck.NoAccounts]);
            return;
        }

        if (accounts.Any(a => a.HasPassword)
            && !await Services.Dialogs.ConfirmAsync(Services.Localizer[L.BulkCreateCheck.CopyPasswordsTitle], Services.Localizer[L.BulkCreateCheck.CopyPasswordsWarning], destructive: true)
                .ConfigureAwait(true))
        {
            return;
        }

        var text = AccountsTable(accounts, withPasswords: true);
        if (ClipboardCopy.Secret(Services.Clipboard, Services.Notifications, text, Services.Localizer.Format(L.BulkCreateCheck.TableCopiedWithPasswords, accounts.Count)))
        {
            foreach (var account in accounts.Where(a => a.HasPassword))
            {
                account.WasHandedOut = true;
            }
        }
    }

    /// <summary><em>Kopiuj bez haseł</em>: the same table without the password column through <c>SetText</c> (nothing is handed out).</summary>
    [RelayCommand]
    private void CopyTableWithoutPasswords()
    {
        var accounts = _accounts.Where(a => !a.IsUndone).ToList();
        if (accounts.Count == 0)
        {
            Services.Notifications.Show(NotificationSeverity.Info, Services.Localizer[L.BulkCreateCheck.NoAccounts]);
            return;
        }

        ClipboardCopy.Text(Services.Clipboard, Services.Notifications, AccountsTable(accounts, withPasswords: false),
            Services.Localizer.Format(L.BulkCreateCheck.TableCopied, accounts.Count));
    }

    // ------------------------------------------------------------------ internals

    private static bool IsUserKind(ObjectKind kind) => kind is ObjectKind.User or ObjectKind.InetOrgPerson;

    private bool IsDisposedState => _disposed;

    private InvalidOperationException PlanError(string message)
    {
        LastPlanError = message;
        return new InvalidOperationException(message);
    }

    private IDirectorySession RequireSession() =>
        Session ?? throw PlanError(Services.Localizer[L.Errors.NotConnected]);

    /// <summary>The objects a computation would create: OK and warning rows.</summary>
    private static int PlannedCount(BulkCreateComputation? computation) =>
        computation is null ? 0 : computation.Count(BulkRowStatus.Ok) + computation.Count(BulkRowStatus.Warning);

    /// <summary><em>Utworzenie kont (114) w jednostce „Nowi pracownicy”</em> (per kind; a container or the domain itself named as such).</summary>
    private string TitleFor(int count)
    {
        var localizer = Services.Localizer;
        string place;
        if (ContainerDn is { } dn && DistinguishedName.TryParse(dn, out var parsed) && !parsed.IsRoot)
        {
            var type = parsed.Rdns[0].Type;
            place = string.Equals(type, "OU", StringComparison.OrdinalIgnoreCase) ? localizer.Format(L.BulkCreateCheck.InUnit, parsed.Name)
                : string.Equals(type, "DC", StringComparison.OrdinalIgnoreCase) ? localizer.Format(L.BulkCreateCheck.InDomain, Session?.Info.DomainDnsName ?? parsed.Name)
                : localizer.Format(L.BulkCreateCheck.InContainer, parsed.Name);
        }
        else
        {
            place = localizer[L.BulkCreate.NoContainer];
        }

        return localizer.Format(L.BulkCreateCheck.ChangeTitleKey(ObjectKind), count, place);
    }

    /// <summary>Every password step of <paramref name="plan"/> gets <paramref name="password"/> (users only; computers keep their random one).</summary>
    private static ChangePlan WithSharedPassword(ChangePlan plan, string password) => plan with
    {
        Objects = [.. plan.Objects.Select(o => IsUserKind(o.Kind)
            ? o with { Changes = [.. o.Changes.Select(c => c.Action == ChangeAction.ResetPassword ? c with { Password = password } : c)] }
            : o)],
    };

    /// <summary>The canary result plus the objects that did not run (not executed: cancelled, or skipped for error rows).</summary>
    private BatchResult WithNotRun(BatchResult done, ChangePlan plan, HashSet<ObjectPlan> ran)
    {
        var now = Services.Time.GetUtcNow();
        var notRun = plan.Objects
            .Where(o => !ran.Contains(o))
            .Select(o => new ObjectResult(o, o.HasErrors || o.IsNoOp ? ObjectResultStatus.Skipped : ObjectResultStatus.Cancelled, [], o.DistinguishedName, now, now))
            .ToList();
        return notRun.Count == 0
            ? done
            : done with { Objects = [.. done.Objects, .. notRun], Plan = done.Plan with { Objects = [.. done.Plan.Objects, .. notRun.Select(o => o.Plan)] } };
    }

    private async Task ReadAfterAsync(Task previous, IDirectorySession session, BatchResult result)
    {
        try
        {
            await previous.ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An earlier read failed: its rows are read again by the undo.
        }

        try
        {
            await ReadCreatedAsync(session, result, _planLifetime.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is DirectoryException or OperationCanceledException or ObjectDisposedException or SessionEndedException)
        {
            // Read again by the undo (rows without a GUID).
        }
    }

    /// <summary>
    /// objectGUID and whenCreated of the objects <paramref name="result"/> created (memory only); a retry that set
    /// <c>pwdLastSet = 0</c> later marks its row too, so the undo's in-use check knows it.
    /// </summary>
    private async Task ReadCreatedAsync(IDirectorySession session, BatchResult result, CancellationToken cancellationToken)
    {
        var planner = Engines.BulkCreate;
        var read = await Task.Run(() => planner.ReadCreatedAsync(session, result, cancellationToken), cancellationToken).ConfigureAwait(true);
        if (_disposed)
        {
            return;
        }

        var byRow = CreatedObjects.ToDictionary(o => o.Row);
        foreach (var created in read)
        {
            if (_createdRows.ContainsKey(created.Row))
            {
                byRow[created.Row] = created;
            }
        }

        foreach (var obj in result.Objects)
        {
            if (obj.Plan.SourceRow is { } row && byRow.TryGetValue(row, out var existing) && !existing.MustChangeSet
                && obj.Changes.Any(c => c.Succeeded && c.Change.Action == ChangeAction.Modify
                    && c.Change.Attributes.Any(a => string.Equals(a.Attribute, Attr.PwdLastSet, StringComparison.OrdinalIgnoreCase) && a.NewValues is ["0"])))
            {
                byRow[row] = existing with { MustChangeSet = true };
            }
        }

        CreatedObjects = [.. byRow.Values.OrderBy(o => o.Row)];
    }

    /// <summary>The created accounts from <see cref="Accumulated"/> (computers never; rows created by an earlier execution keep their marks).</summary>
    private void UpdateAccounts()
    {
        if (Accumulated is not { } accumulated)
        {
            return;
        }

        var localizer = Services.Localizer;
        var byRow = _accounts.ToDictionary(a => a.Row);
        var ordered = new List<CreatedAccountViewModel>();
        var generated = PasswordModeAtPlan == InitialPasswordMode.Generate;
        foreach (var obj in accumulated.Objects)
        {
            if (obj.Plan.Kind == ObjectKind.Computer || obj.Plan.SourceRow is not { } row)
            {
                continue;
            }

            var create = obj.Changes.FirstOrDefault(c => c.Succeeded && c.Change.Action == ChangeAction.Create);
            if (create is null)
            {
                continue;
            }

            var password = obj.Changes.LastOrDefault(c => c.Succeeded && c.Change.Action == ChangeAction.ResetPassword)?.Change.Password;
            if (!byRow.TryGetValue(row, out var account) || account.IsUndone)
            {
                account = new CreatedAccountViewModel(row, password) { IsGeneratedPassword = generated, CopyRequested = CopyPassword };
            }
            else if (!string.Equals(account.Password, password, StringComparison.Ordinal))
            {
                account.SetPassword(password);
                account.IsGeneratedPassword = generated;
            }

            var attributes = create.Change.Attributes;
            account.ExcelRow = Check.ExcelRow(row);
            account.Name = obj.Plan.DisplayName;
            account.DistinguishedName = obj.FinalDn;
            account.Login = First(attributes, Attr.SamAccountName);
            account.Upn = First(attributes, Attr.UserPrincipalName);
            account.Mail = First(attributes, Attr.Mail);
            account.Outcome = obj.Status;
            account.Status = localizer[obj.Status == ObjectResultStatus.Succeeded ? L.BulkCreateCheck.AccountCreated : L.BulkCreateCheck.AccountPartiallyCreated];
            account.Message = MessageOf(obj, password is not null);
            ordered.Add(account);
        }

        // Undone accounts stay listed (Usunięte (cofnięte)) after the live ones.
        ordered.AddRange(_accounts.Where(a => a.IsUndone && !ordered.Contains(a)));
        _accounts.Clear();
        _accounts.AddRange(ordered);
        UpdateCopyColumns();
    }

    private string? MessageOf(ObjectResult obj, bool hasPassword)
    {
        var failures = obj.Changes
            .Where(c => !c.Succeeded && !obj.Changes.Any(later => later.Succeeded && ReferenceEquals(later.Change, c.Change)))
            .Select(c => Services.Errors.TranslateFailure(c.ErrorKind, c.ErrorMessage))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (obj.Status == ObjectResultStatus.Succeeded)
        {
            return null;
        }

        if (!hasPassword && IsUserKind(obj.Plan.Kind) && obj.Plan.Changes.Concat(obj.Changes.Select(c => c.Change)).Any(c => c.Action == ChangeAction.ResetPassword))
        {
            failures.Add(Services.Localizer[L.BulkCreateCheck.DisabledWithoutPassword]);
        }

        return failures.Count == 0 ? null : string.Join(" ", failures);
    }

    private static string? First(IReadOnlyList<AttributeChange> attributes, string name) =>
        attributes.FirstOrDefault(a => string.Equals(a.Attribute, name, StringComparison.OrdinalIgnoreCase))?.NewValues.FirstOrDefault();

    private bool CopyPassword(CreatedAccountViewModel account) =>
        ClipboardCopy.Secret(Services.Clipboard, Services.Notifications, account.Password, Services.Localizer[L.BulkCreateCheck.PasswordCopied]);

    /// <summary>The canary note, the replication note, accounts left disabled, the forest UPN hint, the retry password.</summary>
    private void UpdateRunState()
    {
        var localizer = Services.Localizer;
        var notes = new List<string>();
        var accumulated = Accumulated;
        if (WasStoppedAfterCanary)
        {
            notes.Add(localizer[L.BulkCreateCheck.CanaryStopped]);
        }

        if (_accounts.Any(a => !a.IsUndone) && accumulated is not null)
        {
            var pdc = _planned?.IsPdcEmulator switch
            {
                true => localizer[L.BulkCreateCheck.PdcYes],
                false => localizer[L.BulkCreateCheck.PdcNo],
                _ => localizer[L.BulkCreateCheck.PdcUnknown],
            };
            notes.Add(localizer.Format(L.BulkCreateCheck.ReplicationNote, accumulated.Server, pdc));
        }

        if (_accounts.Any(a => !a.IsUndone && !a.HasPassword && a.Outcome != ObjectResultStatus.Succeeded && IsUserKind(ObjectKind)))
        {
            notes.Add(localizer[L.BulkCreateCheck.DisabledWithoutPassword]);
        }

        var domainOnly = _planned is { Scope: UniquenessScope.Domain };
        if (domainOnly && accumulated is not null && accumulated.Objects.Any(o => o.Changes.Any(c =>
                c.Change.Action == ChangeAction.Create && !c.Succeeded && c.ErrorKind == DirectoryErrorKind.ConstraintViolation)))
        {
            notes.Add(localizer[L.BulkCreateCheck.UpnMaybeInOtherDomain]);
        }

        ResultNotes = notes;
        NeedsRetryPassword = PasswordModeAtPlan == InitialPasswordMode.SameForAll
            && accumulated is not null
            && accumulated.Objects.Any(o => o.Status is ObjectResultStatus.Failed or ObjectResultStatus.PartiallySucceeded or ObjectResultStatus.Cancelled
                && o.Changes.LastOrDefault(c => c.Change.Action == ChangeAction.ResetPassword) is { Succeeded: false, ErrorKind: DirectoryErrorKind.PasswordPolicy });
    }

    /// <summary>The sheet's columns for the copied table: phone columns and pattern-only columns (private address, mobile) preselected.</summary>
    private void UpdateCopyColumns()
    {
        if (_planned?.Input is not { } input)
        {
            return;
        }

        var previous = CopyColumns.ToDictionary(c => c.Index);
        var options = new List<SheetColumnOption>();
        foreach (var column in input.Table.Columns)
        {
            if (column.Header.Length > 0 && ColumnSynonyms.IsPasswordLike(column.Header))
            {
                continue;
            }

            var mapping = input.Mappings.FirstOrDefault(m => m.Column == column.Index);
            var preselected = mapping is { Kind: ColumnTargetKind.PatternOnly }
                || (mapping is { Kind: ColumnTargetKind.Attribute, Attribute: { } attribute } && CreationValues.IsPhoneAttribute(attribute));
            options.Add(previous.TryGetValue(column.Index, out var kept) && kept.Header == column.Name
                ? kept
                : new SheetColumnOption(column.Index, column.Name) { IsSelected = preselected });
        }

        CopyColumns = options;
    }

    /// <summary>
    /// The table of the created accounts as TSV for Excel: <em>Nr</em>, name, login, UPN, e-mail, the chosen sheet columns and
    /// (with <paramref name="withPasswords"/>) the password – formula-protected, so a cell starting with <c>= + - @</c> stays text.
    /// </summary>
    private string AccountsTable(IReadOnlyList<CreatedAccountViewModel> accounts, bool withPasswords)
    {
        var localizer = Services.Localizer;
        var table = _planned?.Input.Table;
        var extra = CopyColumns.Where(c => c.IsSelected).ToList();
        var rows = new Dictionary<int, TableRow>();
        if (table is not null)
        {
            foreach (var row in table.Rows)
            {
                rows.TryAdd(row.Number, row);
            }
        }

        var text = new StringBuilder();
        var header = new List<string>
        {
            localizer[L.BulkCreateCheck.ColumnNumber],
            localizer[L.BulkCreateCheck.ColumnName],
            localizer[L.BulkCreateCheck.ColumnLogin],
            localizer[L.BulkCreateCheck.ColumnUpn],
            localizer[L.BulkCreateCheck.ColumnMail],
        };
        header.AddRange(extra.Select(c => c.Header));
        if (withPasswords)
        {
            header.Add(localizer[L.BulkCreateCheck.ColumnPassword]);
        }

        AppendTsv(text, header);
        foreach (var account in accounts)
        {
            var cells = new List<string>
            {
                account.ExcelRow.ToString(localizer.Culture),
                account.Name,
                account.Login ?? string.Empty,
                account.Upn ?? string.Empty,
                account.Mail ?? string.Empty,
            };
            cells.AddRange(extra.Select(c => rows.TryGetValue(account.Row, out var source) ? source.Cell(c.Index) : string.Empty));
            if (withPasswords)
            {
                cells.Add(account.Password ?? string.Empty);
            }

            AppendTsv(text, cells);
        }

        return text.ToString();
    }

    private static void AppendTsv(StringBuilder text, IEnumerable<string> cells)
    {
        text.Append(string.Join('\t', cells.Select(c => CsvCodec.FormatField(c, '\t', protectFormulas: true))));
        text.Append("\r\n");
    }

    /// <summary>Wires the Excel row of step 1 into the check step once the wizard moves (the host's constructor is the contract's).</summary>
    partial void OnStepChanged(BulkCreateStep value) => Check.ExcelRowOffset ??= () => Source.ExcelRowOffset;

    partial void OnDisposingPlan()
    {
        _planLifetime.Cancel();
        _planLifetime.Dispose();
        foreach (var account in _accounts)
        {
            account.CopyRequested = null;
            account.ClearPassword();
        }

        _accounts.Clear();
        _createdRows.Clear();
        _released.Clear();
        _createdSnapshot = new Dictionary<int, string>();
        _releasedSnapshot = [];
        Accumulated = null;
        CreatedObjects = [];
        _planned = null;
        _samplePlan = null;
        RetryPasswordInput = null;
        RetryConfirmPasswordInput = null;
        SharedPasswordReader = null;
    }
}
