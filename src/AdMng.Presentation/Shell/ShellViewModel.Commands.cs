using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Services;
using AdMng.Presentation.Views;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Shell;

/// <summary>Object commands of the shell. Every write goes through <see cref="IChangeCoordinator"/> (plan → execute).</summary>
public sealed partial class ShellViewModel
{
    /// <summary>Maximum length of an RDN value (cn/ou rangeUpper).</summary>
    public const int MaxNameLength = 64;

    /// <summary>Containers whose create permissions are remembered (current container, tree context menus).</summary>
    private const int MaxCreatePermissions = 32;

    /// <summary>allowedChildClassesEffective of containers (DnKey) in <see cref="_createPermissionsSession"/>.</summary>
    private readonly ConcurrentDictionary<string, EffectivePermissions> _createPermissions = new(StringComparer.Ordinal);
    private IDirectorySession? _createPermissionsSession;
    private string? _createPermissionsRequested;
    private IRelayCommand[]? _stateCommands;

    /// <summary>Raised by <see cref="BeginRenameCommand"/>: the view puts the row's name cell into edit mode and calls <see cref="RenameAsync"/>.</summary>
    public event EventHandler<ObjectRowViewModel>? RenameRequested;

    /// <summary>
    /// Container of the Find dialog and the Move picker (new objects: <see cref="DefaultCreateContainer"/>): the current
    /// container tab, else the selected tree container, else the domain root.
    /// </summary>
    public string? CurrentContainerDn => SelectedDocument?.Location is { Kind: ListLocationKind.Container } location
        ? location.BaseDn
        : (Tree.SelectedNode as ContainerNodeViewModel)?.Dn ?? Sessions.Current?.Info.DefaultNamingContext;

    private IReadOnlyList<ObjectRowViewModel> CurrentSelection => SelectedDocument?.Selection ?? [];

    private IRelayCommand[] StateCommands => _stateCommands ??=
    [
        ChangeDomainControllerCommand,
        RefreshCommand, PropertiesCommand, DeleteCommand, BeginRenameCommand, MoveCommand, EnableCommand, DisableCommand,
        UnlockCommand, ResetPasswordCommand, AddToGroupCommand, BatchEditCommand, NewUserCommand, NewGroupCommand,
        NewOrganizationalUnitCommand, NewComputerCommand, NewContactCommand, FindCommand, ExportListCommand, CopyDnCommand, .. CopyStateCommands,
        OpenInNewTabCommand, PinContainerCommand, SaveAsViewCommand, ChooseColumnsCommand, GoBackCommand, GoForwardCommand,
        GoUpCommand, NewTabCommand, DisconnectCommand, ImportCsvCommand, ShowRecycleBinCommand, ShowAccountReviewCommand, ShowWatchdogHistoryCommand,
        SendMessageCommand, CompareWithConnectionCommand, .. ComputerStateCommands,
    ];

    /// <summary>Re-evaluates CanExecute of all selection dependent commands.</summary>
    public void RefreshCommandStates()
    {
        RequestCreatePermissions();
        foreach (var command in StateCommands)
        {
            command.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Why the operator cannot create <paramref name="kind"/> in <paramref name="parentDn"/> (default: the current
    /// container) - localized, from allowedChildClassesEffective (ADR-0006) - or null when it is allowed or the
    /// permissions are not known (the DC decides then).
    /// </summary>
    public string? CreateBlockedReason(ObjectKind kind, string? parentDn = null)
    {
        if (CreateTarget(kind, parentDn) is not { } parent
            || Sessions.Current is not { } session
            || !ReferenceEquals(session, _createPermissionsSession)
            || !_createPermissions.TryGetValue(DnKey.For(parent), out var permissions)
            || permissions.CanCreate(CreatedClass(kind)))
        {
            return null;
        }

        return Localizer.Format(L.Permissions.CannotCreate, Localizer.ObjectKindName(kind));
    }

    /// <summary>
    /// Reads what the operator may create in <paramref name="containerDn"/> (e.g. when the tree's context menu opens
    /// on it) and updates the New commands. Never throws: unreadable permissions count as unknown.
    /// </summary>
    public async Task LoadCreatePermissionsAsync(string containerDn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerDn);
        if (Sessions.Current is not { } session)
        {
            return;
        }

        UseCreatePermissionsOf(session);
        EffectivePermissions permissions;
        try
        {
            permissions = await Task.Run(() => session.GetEffectivePermissionsAsync(containerDn, CancellationToken.None)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is DirectoryException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            permissions = EffectivePermissions.Unknown;
        }

        if (_disposed || !ReferenceEquals(session, _createPermissionsSession))
        {
            return;
        }

        if (_createPermissions.Count >= MaxCreatePermissions)
        {
            _createPermissions.Clear();
        }

        _createPermissions[DnKey.For(containerDn)] = permissions;
        foreach (var command in new IRelayCommand[] { NewUserCommand, NewGroupCommand, NewOrganizationalUnitCommand, NewComputerCommand, NewContactCommand })
        {
            command.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Inline rename (F2) through the change pipeline. Returns false when the name was rejected or the change failed.</summary>
    public async Task<bool> RenameAsync(ObjectRowViewModel row, string newName)
    {
        ArgumentNullException.ThrowIfNull(row);
        var name = (newName ?? string.Empty).Trim();
        if (name.Length == 0 || string.Equals(name, row.Name, StringComparison.Ordinal))
        {
            return false;
        }

        if (name.Length > MaxNameLength)
        {
            await _dialogs.ShowErrorAsync(Localizer[L.Commands.Rename], Localizer.Format(L.Rename.TooLong, MaxNameLength)).ConfigureAwait(true);
            return false;
        }

        // The display name follows only while it is the old name: one maintained separately ("Kowalski Jan (IT)", or the
        // full name of CN=jkowalski) is kept - F2 executes without a preview, ADUC would show it in its Rename dialog.
        var alsoDisplayName = row.Kind is ObjectKind.User or ObjectKind.InetOrgPerson or ObjectKind.Contact;
        var rename = new RenameOperation(TemplateText.Escape(name), alsoDisplayName) { KeepCustomDisplayName = alsoDisplayName };
        var outcome = await ApplyAsync([row.Dn], [rename], Localizer[L.Commands.Rename]).ConfigureAwait(true);
        return outcome is ChangeOutcome.Executed or ChangeOutcome.OpenedInEditor;
    }

    /// <summary>Drag &amp; drop move: confirms, then moves through the change pipeline.</summary>
    public async Task<ChangeOutcome> MoveAsync(IReadOnlyList<string> distinguishedNames, string targetDn)
    {
        ArgumentNullException.ThrowIfNull(distinguishedNames);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDn);
        if (!CanDrop(distinguishedNames, targetDn))
        {
            return ChangeOutcome.Blocked;
        }

        var domain = Sessions.Current is { } session ? Connections.ConfirmationText.Domain(session) : string.Empty;
        var message = Localizer.Format(L.Move.ConfirmDrop, distinguishedNames.Count, AddressResolver.ToDisplayPath(targetDn), domain);
        if (!await _dialogs.ConfirmAsync(Localizer[L.Commands.Move], message, destructive: false).ConfigureAwait(true))
        {
            return ChangeOutcome.Cancelled;
        }

        return await ApplyAsync(distinguishedNames, [new MoveOperation(targetDn)], Localizer[L.Commands.Move]).ConfigureAwait(true);
    }

    /// <summary>True when the objects may be dropped on the container (not on themselves or their own sub-tree).</summary>
    public bool CanDrop(IReadOnlyList<string> distinguishedNames, string targetDn)
    {
        ArgumentNullException.ThrowIfNull(distinguishedNames);
        if (distinguishedNames.Count == 0 || Sessions.Current is null || !DistinguishedName.TryParse(targetDn, out var target) || target.IsRoot)
        {
            return false;
        }

        foreach (var dn in distinguishedNames)
        {
            if (!DistinguishedName.TryParse(dn, out var source) || source.Equals(target) || target.IsDescendantOf(source))
            {
                return false;
            }
        }

        return !distinguishedNames.All(dn => DistinguishedName.TryParse(dn, out var s) && s.Parent is { } p && p.Equals(target));
    }

    [RelayCommand(CanExecute = nameof(HasTab), AllowConcurrentExecutions = true)]
    private async Task RefreshAsync()
    {
        if (SelectedDocument is not { } document)
        {
            // F5 on another tab (Watchdog) refreshes that tab.
            if (SelectedTab is { } tab)
            {
                await tab.RefreshAsync().ConfigureAwait(true);
            }

            return;
        }

        if (document.Location is { Kind: ListLocationKind.Container } location && Tree.FindNode(location.BaseDn) is { ChildrenLoaded: true } node)
        {
            node.RefreshAsync().Forget();
        }

        await document.LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task PropertiesAsync()
    {
        var selection = CurrentSelection;
        return selection.Count switch
        {
            0 => Task.CompletedTask,
            1 => _dialogs.ShowPropertiesAsync(selection[0].Dn),
            _ => _dialogs.ShowBatchEditorAsync(new BatchEditorRequest(Dns(selection)) { Title = Localizer[L.Commands.BatchEdit] }),
        };
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        var selection = CurrentSelection;
        var session = Sessions.Current;
        if (selection.Count == 0 || session is null)
        {
            return;
        }

        var title = Localizer[L.Commands.Delete];
        var dns = Dns(selection);
        if (selection.Count > Settings.Current.ConfirmationThreshold)
        {
            // Large deletes are reviewed in the batch editor (preview + typed confirmation there).
            await ApplyAsync(dns, [new DeleteOperation()], title).ConfigureAwait(true);
            return;
        }

        // Not only OUs and containers hold children: a computer has its BitLocker recovery information
        // (msFVE-RecoveryInformation) or print queues below it, a user Exchange ActiveSync devices. Like ADUC, such an
        // object is deleted as a tree after a typed confirmation - a plain delete is refused by the planner.
        bool withChildren;
        try
        {
            withChildren = await Task.Run(() => AnyHasChildrenAsync(session, dns)).ConfigureAwait(true);
        }
        catch (DirectoryException ex)
        {
            // E.g. the object was deleted meanwhile or listing it is denied - never guess "empty" for a delete.
            await _dialogs.ShowErrorAsync(title, Errors.Translate(ex)).ConfigureAwait(true);
            return;
        }

        // The domain of the session the delete is planned in (named in the sentence, not only in the dialog header).
        var domain = Connections.ConfirmationText.Domain(session);
        var message = selection.Count == 1
            ? Localizer.Format(L.Delete.ConfirmOne, selection[0].Name, selection[0].TypeText, domain)
            : Localizer.Format(L.Delete.ConfirmMany, selection.Count, domain);
        bool confirmed;
        if (withChildren)
        {
            var expected = selection.Count == 1 ? selection[0].Name : selection.Count.ToString(CultureInfo.InvariantCulture);
            message = $"{message}{Environment.NewLine}{Environment.NewLine}{Localizer[L.Delete.SubtreeWarning]}{Environment.NewLine}"
                + Localizer.Format(L.Delete.TypeToConfirm, expected);
            confirmed = await _dialogs.ConfirmTypedAsync(title, message, expected).ConfigureAwait(true);
        }
        else
        {
            confirmed = await _dialogs.ConfirmAsync(title, message, destructive: true).ConfigureAwait(true);
        }

        if (confirmed)
        {
            await ApplyAsync(dns, [new DeleteOperation(IncludeSubtree: withChildren)], title).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSingleSelection))]
    private void BeginRename()
    {
        if (CurrentSelection is [var row])
        {
            RenameRequested?.Invoke(this, row);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Move()
    {
        var selection = CurrentSelection;
        var session = Sessions.Current;
        if (selection.Count == 0 || session is null)
        {
            return;
        }

        var target = await _dialogs.PickContainerAsync(session, CurrentContainerDn).ConfigureAwait(true);
        if (target is null)
        {
            return;
        }

        var dns = Dns(selection);
        if (!CanDrop(dns, target))
        {
            await _dialogs.ShowErrorAsync(Localizer[L.Commands.Move], Localizer[L.Move.InvalidTarget]).ConfigureAwait(true);
            return;
        }

        await ApplyAsync(dns, [new MoveOperation(target)], Localizer[L.Commands.Move]).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanEnable))]
    private Task EnableAsync() =>
        ApplyAsync(Dns(CurrentSelection.Where(r => r.IsAccount && r.IsDisabled)), [new SetAccountEnabledOperation(true)], Localizer[L.Commands.Enable]);

    private bool CanEnable() => CurrentSelection.Any(r => r.IsAccount && r.IsDisabled);

    [RelayCommand(CanExecute = nameof(CanDisable))]
    private Task DisableAsync() =>
        ApplyAsync(Dns(CurrentSelection.Where(r => r.IsAccount && !r.IsDisabled)), [new SetAccountEnabledOperation(false)], Localizer[L.Commands.Disable]);

    private bool CanDisable() => CurrentSelection.Any(r => r.IsAccount && !r.IsDisabled);

    [RelayCommand(CanExecute = nameof(CanUnlock))]
    private Task UnlockAsync() =>
        ApplyAsync(Dns(CurrentSelection.Where(r => r.IsAccount && r.IsLocked)), [new UnlockAccountOperation()], Localizer[L.Commands.Unlock]);

    private bool CanUnlock() => CurrentSelection.Any(r => r.IsAccount && r.IsLocked);

    [RelayCommand(CanExecute = nameof(CanResetPassword))]
    private Task ResetPasswordAsync() =>
        _dialogs.ShowResetPasswordAsync(Dns(CurrentSelection.Where(r => IsPasswordAccount(r.Kind))));

    private bool CanResetPassword() => CurrentSelection.Any(r => IsPasswordAccount(r.Kind));

    [RelayCommand(CanExecute = nameof(CanAddToGroup))]
    private async Task AddToGroupAsync()
    {
        var session = Sessions.Current;
        var members = Dns(CurrentSelection.Where(r => CanBeMember(r.Kind)));
        if (session is null || members.Count == 0)
        {
            return;
        }

        var groups = await _dialogs.PickObjectsAsync(session, [ObjectKind.Group], multiSelect: true).ConfigureAwait(true);
        if (groups.Count > 0)
        {
            await ApplyAsync(members, [new AddToGroupsOperation(groups)], Localizer[L.Commands.AddToGroup]).ConfigureAwait(true);
        }
    }

    private bool CanAddToGroup() => CurrentSelection.Any(r => CanBeMember(r.Kind));

    /// <summary>Opens the batch editor in CSV import mode (bulk modification from a spreadsheet).</summary>
    [RelayCommand(CanExecute = nameof(IsConnected))]
    private Task ImportCsvAsync() => _dialogs.ShowBatchEditorAsync(new BatchEditorRequest([]));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task BatchEditAsync() =>
        _dialogs.ShowBatchEditorAsync(new BatchEditorRequest(Dns(CurrentSelection)) { Title = Localizer[L.Commands.BatchEdit] });

    // The New commands take an optional parent DN: the tree's context menu passes the right-clicked container (which
    // need not be the current tab's); menus, toolbar, shortcuts and the palette pass none (CurrentContainerDn). They
    // are disabled where the operator's effective permissions do not allow the class (unknown permissions: enabled).
    [RelayCommand(CanExecute = nameof(CanCreateUser))]
    private Task NewUserAsync(string? parentDn) => CreateObjectAsync(ObjectKind.User, parentDn);

    [RelayCommand(CanExecute = nameof(CanCreateGroup))]
    private Task NewGroupAsync(string? parentDn) => CreateObjectAsync(ObjectKind.Group, parentDn);

    [RelayCommand(CanExecute = nameof(CanCreateOrganizationalUnit))]
    private Task NewOrganizationalUnitAsync(string? parentDn) => CreateObjectAsync(ObjectKind.OrganizationalUnit, parentDn);

    [RelayCommand(CanExecute = nameof(CanCreateComputer))]
    private Task NewComputerAsync(string? parentDn) => CreateObjectAsync(ObjectKind.Computer, parentDn);

    [RelayCommand(CanExecute = nameof(CanCreateContact))]
    private Task NewContactAsync(string? parentDn) => CreateObjectAsync(ObjectKind.Contact, parentDn);

    private bool CanCreateUser(string? parentDn) => CanCreate(ObjectKind.User, parentDn);

    private bool CanCreateGroup(string? parentDn) => CanCreate(ObjectKind.Group, parentDn);

    private bool CanCreateOrganizationalUnit(string? parentDn) => CanCreate(ObjectKind.OrganizationalUnit, parentDn);

    private bool CanCreateComputer(string? parentDn) => CanCreate(ObjectKind.Computer, parentDn);

    private bool CanCreateContact(string? parentDn) => CanCreate(ObjectKind.Contact, parentDn);

    private bool CanCreate(ObjectKind kind, string? parentDn) =>
        IsConnected && CreateTarget(kind, parentDn) is not null && CreateBlockedReason(kind, parentDn) is null;

    private static string CreatedClass(ObjectKind kind) => kind switch
    {
        ObjectKind.Group => ObjectClasses.Group,
        ObjectKind.OrganizationalUnit => ObjectClasses.OrganizationalUnit,
        ObjectKind.Computer => ObjectClasses.Computer,
        ObjectKind.Contact => ObjectClasses.Contact,
        _ => ObjectClasses.User,
    };

    /// <summary>Forgets the permissions of another (closed) session.</summary>
    private void UseCreatePermissionsOf(IDirectorySession? session)
    {
        if (!ReferenceEquals(session, _createPermissionsSession))
        {
            _createPermissions.Clear();
            _createPermissionsRequested = null;
            _createPermissionsSession = session;
        }
    }

    /// <summary>
    /// Reads the create permissions of the New commands' default targets (the current container, the well-known Users
    /// and Computers containers) once they changed - the New commands follow them.
    /// </summary>
    private void RequestCreatePermissions()
    {
        var session = Sessions.Current;
        UseCreatePermissionsOf(session);
        if (session is null || _disposed || CreateTargets() is not { Count: > 0 } targets)
        {
            return;
        }

        var key = string.Join('\n', targets.Select(DnKey.For));
        if (!string.Equals(key, _createPermissionsRequested, StringComparison.Ordinal))
        {
            _createPermissionsRequested = key;
            foreach (var target in targets)
            {
                LoadCreatePermissionsAsync(target).Forget();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private Task FindAsync() => _dialogs.ShowFindAsync(SelectedDocument?.Location is { Kind: ListLocationKind.Container } ? CurrentContainerDn : null);

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportListAsync()
    {
        if (SelectedDocument is not { } document)
        {
            return;
        }

        var snapshot = document.CreateExportSnapshot();
        var title = Localizer[L.Commands.ExportList];

        // Columns with passwords, LAPS or other confidential values (added in the column chooser or by an imported saved
        // view) are written only after the same warning as exported passwords; declined, they are left out.
        var secret = snapshot.SecretColumns(Sessions.Current?.Schema);
        if (secret.Count > 0)
        {
            var message = Localizer.Format(L.Files.ExportSecretColumns, string.Join(", ", secret.Select(c => c.Header)));
            if (!await _dialogs.ConfirmAsync(Localizer[L.Batch.ExportPasswordsTitle], message, destructive: true).ConfigureAwait(true))
            {
                snapshot = snapshot.WithoutColumns(secret.Select(c => c.Attribute));
                if (snapshot.Columns.Count == 0)
                {
                    return;
                }
            }
        }

        var path = await _files.SaveFileAsync(
            title,
            ListExportSnapshot.SuggestFileName(document.Title, _lists.Time.GetLocalNow()),
            [new FileFilter(Localizer[L.Files.CsvFilter], "*.csv")]).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        // Excel of the operator's region (';' in Poland), whatever the UI language of the app.
        var delimiter = CsvFiles.ListSeparator(_regional.Culture);
        try
        {
            await Task.Run(() =>
            {
                using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                _csv.Write(writer, [.. snapshot.Columns.Select(c => c.Header)], snapshot.FormatRows(), delimiter, protectFormulas: true);
            }).ConfigureAwait(true);
            _notifications.Show(NotificationSeverity.Success, Localizer.Format(L.Files.Exported, snapshot.Rows.Count, Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowErrorAsync(title, Errors.Translate(ex)).ConfigureAwait(true);
        }
    }

    private bool CanExport() => SelectedDocument is { Items.Count: > 0 };

    /// <summary>
    /// "Kopiuj jako ▸ Nazwa wyróżniająca" (Ctrl+Shift+C): the DNs of the selection in the list's order, like every other
    /// "Copy as" format; not confirmed when the clipboard was busy.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasCopySelection))]
    private void CopyDn()
    {
        var selection = OrderedSelection();
        if (selection.Count > 0)
        {
            ClipboardCopy.Text(
                _clipboard,
                _notifications,
                string.Join(Environment.NewLine, selection.Select(r => r.Dn)),
                Localizer.Format(L.Shell.CopiedDn, selection.Count),
                LoadingRowsNote());
        }
    }

    [RelayCommand(CanExecute = nameof(HasDocument), AllowConcurrentExecutions = true)]
    private Task OpenInNewTabAsync()
    {
        if (CurrentSelection is [{ IsContainer: true } row])
        {
            return OpenLocationInNewTabAsync(ListLocation.ForContainer(row.Dn, row.Name, row.Icon));
        }

        return SelectedDocument?.Location is { } location ? OpenLocationInNewTabAsync(location) : Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanPin))]
    private async Task PinContainerAsync()
    {
        var (dn, name) = PinCandidate();
        if (dn is null || _views.IsPinned(dn))
        {
            return;
        }

        await _views.PinAsync(dn, name ?? dn).ConfigureAwait(true);
        _notifications.Show(NotificationSeverity.Success, Localizer.Format(L.Views.Pinned, name));
    }

    private bool CanPin() => PinCandidate().Dn is { } dn && !_views.IsPinned(dn);

    [RelayCommand]
    private Task UnpinContainerAsync(ContainerNodeViewModel? node)
    {
        var dn = node?.Dn ?? PinCandidate().Dn;
        return dn is null ? Task.CompletedTask : _views.UnpinAsync(dn);
    }

    [RelayCommand(CanExecute = nameof(CanSaveAsView))]
    private async Task SaveAsViewAsync()
    {
        var session = Sessions.Current;
        if (SelectedDocument is not { Location: { } location } document || session is null)
        {
            return;
        }

        var sort = document.Columns.FirstOrDefault(c => c.SortDirection != SortDirection.None);
        var filter = location.Filter;
        var chips = document.FilterChips.Where(c => c.IsActive).Select(c => c.Kind).ToList();
        var savedFilters = document.SavedFilterChips.Where(c => c.IsActive).Select(c => c.Id).ToList();
        if (location.Kind == ListLocationKind.View && BuiltInViews.Find(location.ViewId) is { } builtIn)
        {
            // The built-in filter holds today's dates ("expiring in 30 days" from now) and some views a post-filter
            // ("locked out" without expired lockouts): save the definition as a static filter plus its chip instead.
            var (staticFilter, chip) = builtIn.ToSavedDefinition(_lists.Time.GetUtcNow(), Settings.Current.InactiveDays);
            filter = staticFilter;
            if (chip is { } definitionChip && !chips.Contains(definitionChip))
            {
                chips.Insert(0, definitionChip);
            }
        }

        var view = new SavedView
        {
            Name = location.Title,
            BaseDn = SavedView.ToPortableDn(document.LastSearch?.BaseDn ?? location.BaseDn, session.Info.DefaultNamingContext),
            Scope = location.Scope,
            Filter = filter,
            Columns = [.. document.VisibleColumns.Select(c => c.Attribute)],
            QuickFilters = chips,
            SavedFilters = savedFilters,
            SortAttribute = sort?.Attribute,
            SortDescending = sort?.SortDirection == SortDirection.Descending,
        };
        await EditAndSaveViewAsync(view, isNew: true).ConfigureAwait(true);
    }

    private bool CanSaveAsView() => SelectedDocument?.Location is not null && IsConnected;

    [RelayCommand]
    private Task EditViewAsync(ViewNodeViewModel? node) =>
        node?.SavedView is { } view ? EditAndSaveViewAsync(view, isNew: false) : Task.CompletedTask;

    [RelayCommand]
    private async Task DeleteViewAsync(ViewNodeViewModel? node)
    {
        if (node?.SavedView is not { } view)
        {
            return;
        }

        if (await _dialogs.ConfirmAsync(Localizer[L.Views.DeleteTitle], Localizer.Format(L.Views.DeleteConfirm, view.Name), destructive: true).ConfigureAwait(true))
        {
            await _views.DeleteViewAsync(view.Id).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private Task MoveViewUpAsync(ViewNodeViewModel? node) => node?.SavedView is { } v ? _views.MoveViewAsync(v.Id, -1) : Task.CompletedTask;

    [RelayCommand]
    private Task MoveViewDownAsync(ViewNodeViewModel? node) => node?.SavedView is { } v ? _views.MoveViewAsync(v.Id, 1) : Task.CompletedTask;

    /// <summary>Reorders a pinned container shortcut in the Views section.</summary>
    [RelayCommand]
    private Task MovePinnedUpAsync(ContainerNodeViewModel? node) =>
        node is { IsPinnedEntry: true } ? _views.MovePinnedAsync(node.Dn, -1) : Task.CompletedTask;

    [RelayCommand]
    private Task MovePinnedDownAsync(ContainerNodeViewModel? node) =>
        node is { IsPinnedEntry: true } ? _views.MovePinnedAsync(node.Dn, 1) : Task.CompletedTask;

    [RelayCommand]
    private async Task ExportViewsAsync()
    {
        var title = Localizer[L.Views.ExportTitle];
        var path = await _files.SaveFileAsync(title, "views.json", [new FileFilter(Localizer[L.Files.JsonFilter], "*.json")]).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, _views.ExportJson(), new UTF8Encoding(false)).ConfigureAwait(true);
            _notifications.Show(NotificationSeverity.Success, Localizer.Format(L.Views.Exported, _views.SavedViews.Count, Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowErrorAsync(title, Errors.Translate(ex)).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ImportViewsAsync()
    {
        var title = Localizer[L.Views.ImportTitle];
        var path = await _files.OpenFileAsync(title, [new FileFilter(Localizer[L.Files.JsonFilter], "*.json")]).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        try
        {
            var parsed = ViewLibrary.ParseImport(await File.ReadAllTextAsync(path).ConfigureAwait(true));
            var added = await _views.ImportAsync(parsed.Views).ConfigureAwait(true);
            _notifications.Show(
                parsed.Rejected > 0 ? NotificationSeverity.Warning : NotificationSeverity.Success,
                Localizer.Format(L.Views.Imported, added, parsed.Rejected));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowErrorAsync(title, Errors.Translate(ex)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or NotSupportedException)
        {
            await _dialogs.ShowErrorAsync(title, Localizer[L.Views.InvalidFile]).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task ChooseColumnsAsync()
    {
        if (SelectedDocument is not { } document)
        {
            return;
        }

        var chooser = document.CreateColumnChooser();
        if (await _dialogs.ShowColumnChooserAsync(chooser).ConfigureAwait(true))
        {
            await document.ApplyColumnsAsync(chooser.Result).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// View → Advanced Features: only stores the setting. Every window's shell (this one included) reacts to the settings
    /// change and reloads its tree and container tabs once; this command waits for its own window's reload.
    /// </summary>
    [RelayCommand]
    private async Task ToggleAdvancedFeaturesAsync()
    {
        await Settings.UpdateAsync(s => s.ShowAdvancedFeatures = !s.ShowAdvancedFeatures).ConfigureAwait(true);
        await _advancedFeaturesReload.ConfigureAwait(true);
    }

    /// <summary>Shows/hides the details pane; <see cref="OnSettingsChanged"/> fills or clears it.</summary>
    [RelayCommand]
    private Task ToggleDetailsPaneAsync() => Settings.UpdateAsync(s => s.ShowDetailsPane = !s.ShowDetailsPane);

    [RelayCommand]
    private Task ShowSessionLogAsync() => _dialogs.ShowSessionLogAsync();

    /// <summary>The AD Recycle Bin of the connected domain (deleted objects, restore).</summary>
    [RelayCommand(CanExecute = nameof(IsConnected))]
    private Task ShowRecycleBinAsync() => _dialogs.ShowRecycleBinAsync();

    /// <summary>The account review of the connected domain (README U2, U4).</summary>
    [RelayCommand(CanExecute = nameof(IsConnected))]
    private Task ShowAccountReviewAsync() => _dialogs.ShowAccountReviewAsync();

    /// <summary>The saved filters of the filter builder (README U4): chips of the lists, the review's scope and rules.</summary>
    [RelayCommand]
    private Task ShowSavedFiltersAsync() => _dialogs.ShowSavedFiltersAsync();

    [RelayCommand]
    private Task ShowSettingsAsync() => _dialogs.ShowSettingsAsync();

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task DisconnectAsync()
    {
        if (_activities.HasRunning)
        {
            var domain = Sessions.Current is { } session ? Connections.ConfirmationText.Domain(session) : string.Empty;
            if (!await _dialogs.ConfirmAsync(Localizer[L.Commands.Disconnect], Localizer.Format(L.Shell.DisconnectWhileRunning, domain), destructive: true).ConfigureAwait(true))
            {
                return;
            }

            // Cancel before the session closes: writes in flight complete and are reported, the rest is reported as
            // cancelled - instead of failing on a closed connection with an unknown outcome (and no undo).
            await _activities.CancelRunningAsync().ConfigureAwait(true);
        }

        await Sessions.DisconnectAsync().ConfigureAwait(true);
        await _dialogs.ShowConnectAsync().ConfigureAwait(true);
    }

    private bool HasDocument() => SelectedDocument is not null;

    private bool HasTab() => SelectedTab is not null;

    /// <summary>
    /// "Historia zmian (Watchdog)" of the selected object: its objectGUID is read through the session and the Watchdog tab
    /// shows the events of that object. Available when a watchdog service address is configured.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanShowWatchdogHistory))]
    private async Task ShowWatchdogHistoryAsync()
    {
        if (_historyLauncher is null || CurrentSelection is not [var row] || Sessions.Current is not { } session)
        {
            return;
        }

        try
        {
            var obj = await Task.Run(() => session.GetAsync(row.Dn, [Attr.ObjectGuid, Attr.Name], CancellationToken.None)).ConfigureAwait(true);
            if (obj?.ObjectGuid is { } guid)
            {
                _historyLauncher.Show(guid, row.Name);
            }
        }
        catch (DirectoryException ex)
        {
            await _dialogs.ShowErrorAsync(Localizer[L.Commands.WatchdogHistory], Errors.Translate(ex)).ConfigureAwait(true);
        }
    }

    /// <summary>"Wyślij wiadomość…" (README U1): the message window for the selected user accounts (one message each).</summary>
    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private Task SendMessageAsync()
    {
        var users = CurrentSelection.Where(r => r.Kind == ObjectKind.User).Select(r => r.Dn).ToList();
        return _messages is null || users.Count == 0 ? Task.CompletedTask : _messages.ShowComposerAsync(users, null);
    }

    /// <summary>The message window is available (U1 registered its launcher) and the selection has user accounts.</summary>
    public bool CanSendMessages => _messages is not null;

    private bool CanSendMessage() => _messages is not null && IsConnected && CurrentSelection.Any(r => r.Kind == ObjectKind.User);

    private bool CanShowWatchdogHistory() => _historyLauncher?.IsAvailable == true && CurrentSelection.Count == 1 && IsConnected;

    private bool HasSelection() => CurrentSelection.Count > 0;

    private bool HasSingleSelection() => CurrentSelection.Count == 1;

    private Task<ChangeOutcome> ApplyAsync(IReadOnlyList<string> dns, IReadOnlyList<BatchOperation> operations, string title) =>
        dns.Count == 0 ? Task.FromResult(ChangeOutcome.NothingToDo) : _changes.ApplyAsync(new ChangeRequest(dns, operations, title));

    private async Task CreateObjectAsync(ObjectKind kind, string? parentDn)
    {
        if (CreateTarget(kind, parentDn) is not { } parent)
        {
            return;
        }

        var created = await _dialogs.ShowNewObjectAsync(kind, parent).ConfigureAwait(true);
        if (created is not null)
        {
            SelectedDocument?.SelectAfterLoad(created);
        }
    }

    private async Task EditAndSaveViewAsync(SavedView view, bool isNew)
    {
        var editor = new SavedViewEditorViewModel(Sessions, _dialogs, Localizer, Errors, view, isNew);
        if (await _dialogs.ShowSavedViewEditorAsync(editor).ConfigureAwait(true) && editor.Result is { } saved)
        {
            await _views.SaveViewAsync(saved).ConfigureAwait(true);
            _notifications.Show(NotificationSeverity.Success, Localizer.Format(L.Views.Saved, saved.Name));

            // Tabs showing the edited view run its new definition right away (F5 would repeat the old query, and a click
            // on its tree node - re-selected silently - would change nothing).
            if (Sessions.Current is { } session)
            {
                var location = saved.ToLocation(session.Info.DefaultNamingContext);
                foreach (var document in ListDocuments.Where(d => string.Equals(d.Location?.ViewId, saved.Id, StringComparison.Ordinal)).ToList())
                {
                    document.ReplaceLocationAsync(location).Forget();
                }
            }
        }
    }

    private (string? Dn, string? Name) PinCandidate()
    {
        if (CurrentSelection is [{ IsContainer: true } row])
        {
            return (row.Dn, row.Name);
        }

        return SelectedDocument?.Location is { Kind: ListLocationKind.Container } location ? (location.BaseDn, location.Title) : (null, null);
    }

    private static List<string> Dns(IEnumerable<ObjectRowViewModel> rows) => [.. rows.Select(r => r.Dn)];

    private static bool IsPasswordAccount(ObjectKind kind) => kind is ObjectKind.User or ObjectKind.InetOrgPerson;

    private static bool CanBeMember(ObjectKind kind) => kind.IsSecurityPrincipal() || kind == ObjectKind.Contact;

    private static async Task<bool> AnyHasChildrenAsync(IDirectorySession session, IReadOnlyList<string> distinguishedNames)
    {
        foreach (var dn in distinguishedNames)
        {
            var search = new DirectorySearch { BaseDn = dn, Scope = SearchScope.OneLevel, Attributes = [], SizeLimit = 1 };
            try
            {
                await foreach (var unused in session.SearchAsync(search, CancellationToken.None).ConfigureAwait(false))
                {
                    return true;
                }
            }
            catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.SizeLimitExceeded)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Escaping of literal text for <see cref="Core.Templates.ITemplateEngine"/> templates.</summary>
public static class TemplateText
{
    /// <summary>Doubles braces so the text renders literally (<c>{</c> → <c>{{</c>).</summary>
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);
    }
}
