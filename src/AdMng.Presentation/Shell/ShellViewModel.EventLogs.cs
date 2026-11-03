using AdMng.Core.Model;
using AdMng.Presentation.EventLogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Shell;

/// <summary>
/// Dzienniki zdarzeń (track E, design note docs/dev/e-event-logs.md 5): "Narzędzia → Dzienniki zdarzeń…" (palette
/// <c>events.open</c>), "Nieudane logowania…" (<c>events.failedLogons</c>), "Skąd blokada konta…" for the selected user or one
/// picked from AD (<c>events.lockout</c>) and "Dziennik zdarzeń tego komputera" for the selected computer or one picked from AD
/// (<c>events.computer</c>). The documents open as tabs of this window: the reader and the failed logons once, the lockout
/// source once per account; the entry points of property sheets and the account review reach them through
/// <see cref="EventLogLauncher"/>.
/// </summary>
public sealed partial class ShellViewModel
{
    private IRelayCommand[]? _eventLogStateCommands;

    /// <summary>The event log documents can be opened in this window.</summary>
    public bool IsEventLogsAvailable => _eventLogs is not null;

    /// <summary>The open event log tabs.</summary>
    public IEnumerable<EventLogDocumentBase> EventLogDocuments => Documents.OfType<EventLogDocumentBase>();

    private IRelayCommand[] EventLogStateCommands => _eventLogStateCommands ??=
    [
        OpenEventLogsCommand, OpenFailedLogonsCommand, OpenLockoutSourceCommand, OpenComputerEventLogCommand,
    ];

    private IEnumerable<CommandDescriptor> EventLogCommandDescriptors()
    {
        const string tools = L.CommandCategories.Tools;
        const string obj = L.CommandCategories.Object;
        return
        [
            new("events.open", L.EventLogs.Command, tools, string.Empty, OpenEventLogsCommand),
            new("events.failedLogons", L.EventLogs.FailedLogonsCommand, tools, string.Empty, OpenFailedLogonsCommand),
            new("events.lockout", L.EventLogs.LockoutCommand, obj, string.Empty, OpenLockoutSourceCommand),
            new("events.computer", L.EventLogs.ComputerCommand, obj, string.Empty, OpenComputerEventLogCommand),
        ];
    }

    /// <summary>
    /// Opens the document a request names, or selects it when it is open (the reader takes the request's computer as its
    /// source). Null when the window has no event log services.
    /// </summary>
    public EventLogDocumentBase? OpenEventLogDocument(EventLogDocumentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_eventLogs is not { } services || _disposed)
        {
            return null;
        }

        EventLogDocumentBase? document = request.Kind switch
        {
            EventLogDocumentKind.Reader => EventLogDocuments.OfType<EventLogReaderDocumentViewModel>().FirstOrDefault(),
            EventLogDocumentKind.FailedLogons => EventLogDocuments.OfType<FailedLogonsDocumentViewModel>().FirstOrDefault(),
            EventLogDocumentKind.Lockout => EventLogDocuments.OfType<LockoutDocumentViewModel>()
                .FirstOrDefault(d => Core.Ldap.DistinguishedName.AreEqual(d.UserDn, request.UserDn ?? string.Empty)),
            _ => null,
        };
        var isNew = document is null;
        switch (request.Kind)
        {
            case EventLogDocumentKind.Reader:
                var reader = document as EventLogReaderDocumentViewModel ?? services.CreateReader();
                document = reader;
                if (isNew)
                {
                    Documents.Add(reader);
                }

                reader.OpenAsync(request.ComputerDn is null && request.Host is null ? null : request).Forget();
                break;
            case EventLogDocumentKind.FailedLogons:
                if (isNew)
                {
                    document = services.CreateFailedLogons();
                    Documents.Add(document);
                }

                break;
            case EventLogDocumentKind.Lockout when !string.IsNullOrWhiteSpace(request.UserDn):
                if (isNew)
                {
                    var lockout = services.CreateLockout(request.UserDn, request.Name);
                    document = lockout;
                    Documents.Add(lockout);
                    if (lockout.IsSupported)
                    {
                        lockout.RunAsync().Forget();
                    }
                }

                break;
            default:
                return null;
        }

        SelectedTab = document;
        return document;
    }

    /// <summary>Subscribes to the requests of the window's entry points (constructor).</summary>
    private void AttachEventLogs()
    {
        if (_eventLogs is { } services)
        {
            services.Launcher.Requested += OnEventLogRequested;
        }
    }

    /// <summary>Ends the subscription (Dispose).</summary>
    private void DetachEventLogs()
    {
        if (_eventLogs is { } services)
        {
            services.Launcher.Requested -= OnEventLogRequested;
        }
    }

    private void OnEventLogRequested(object? sender, EventLogDocumentRequest request) =>
        _ = _lists.Dispatcher.RunAsync(() => OpenEventLogDocument(request));

    /// <summary>"Dzienniki zdarzeń…".</summary>
    [RelayCommand(CanExecute = nameof(CanUseEventLogs))]
    private void OpenEventLogs() => OpenEventLogDocument(new EventLogDocumentRequest(EventLogDocumentKind.Reader));

    /// <summary>"Nieudane logowania…".</summary>
    [RelayCommand(CanExecute = nameof(CanUseEventLogs))]
    private void OpenFailedLogons() => OpenEventLogDocument(new EventLogDocumentRequest(EventLogDocumentKind.FailedLogons));

    /// <summary>"Skąd blokada konta…": the one selected user, else a user picked from AD.</summary>
    [RelayCommand(CanExecute = nameof(CanUseEventLogs))]
    private async Task OpenLockoutSourceAsync(object? parameter)
    {
        var target = parameter switch
        {
            string dn when !string.IsNullOrWhiteSpace(dn) => (dn, (string?)null),
            ObjectRowViewModel row when IsKind(row.Kind, ObjectKind.User) => (row.Dn, row.Name),
            _ when CurrentSelection is [var selected] && IsKind(selected.Kind, ObjectKind.User) => (selected.Dn, selected.Name),
            _ => ((string?)null, (string?)null),
        };
        if (target.Item1 is null && Sessions.Current is { } session)
        {
            var picked = await _dialogs.PickObjectsAsync(session, [ObjectKind.User], multiSelect: false).ConfigureAwait(true);
            target = picked.Count > 0 ? (picked[0], null) : (null, null);
        }

        if (target.Item1 is { } userDn)
        {
            OpenEventLogDocument(new EventLogDocumentRequest(EventLogDocumentKind.Lockout) { UserDn = userDn, Name = target.Item2 });
        }
    }

    /// <summary>"Dziennik zdarzeń tego komputera": the one selected computer, else a computer picked from AD.</summary>
    [RelayCommand(CanExecute = nameof(CanUseEventLogs))]
    private async Task OpenComputerEventLogAsync(object? parameter)
    {
        var dn = parameter switch
        {
            string text when !string.IsNullOrWhiteSpace(text) => text,
            ObjectRowViewModel { Kind: ObjectKind.Computer } row => row.Dn,
            _ when CurrentSelection is [{ Kind: ObjectKind.Computer } selected] => selected.Dn,
            _ => null,
        };
        if (dn is null && Sessions.Current is { } session)
        {
            var picked = await _dialogs.PickObjectsAsync(session, [ObjectKind.Computer], multiSelect: false).ConfigureAwait(true);
            dn = picked.Count > 0 ? picked[0] : null;
        }

        if (dn is not null)
        {
            OpenEventLogDocument(new EventLogDocumentRequest(EventLogDocumentKind.Reader) { ComputerDn = dn });
        }
    }

    private bool CanUseEventLogs() => _eventLogs is not null && Sessions.Current is not null;
}
