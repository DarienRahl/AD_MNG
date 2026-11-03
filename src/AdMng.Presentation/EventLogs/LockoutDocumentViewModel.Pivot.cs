using AdMng.Core.Computers;
using AdMng.Core.Computers.Remote;
using AdMng.Core.Directory;
using AdMng.Core.EventLogs.Analysis;
using AdMng.Core.Model;
using AdMng.Presentation.Computers;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.EventLogs;

/// <summary>
/// "Sprawdź komputer źródłowy" (design note e-event-logs.md 4.1): the computer of the selected source (a workstation or caller
/// name, or an address through the names seen with it and reverse DNS) is found in AD and asked over K1 (read only, the
/// window's credential) for the account's sessions, the services and the scheduled tasks that run as the account.
/// </summary>
public sealed partial class LockoutDocumentViewModel
{
    private readonly SerialCancellation _pivot = new();
    private string? _pivotComputer;
    private LockoutAccount? _pivotAccount;

    /// <summary>The findings of the pivot, one line each (null before it ran).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPivot))]
    public partial IReadOnlyList<string>? PivotLines { get; private set; }

    public bool HasPivot => PivotLines is not null;

    /// <summary>"Komputer źródłowy: WAW-PC001 (waw-pc001.contoso.test)".</summary>
    [ObservableProperty]
    public partial string PivotHeading { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PivotCommand))]
    public partial bool IsPivotRunning { get; private set; }

    /// <summary>The computer the pivot found (its DN): "Otwórz zdalne akcje" opens the K1 window for it.</summary>
    public string? PivotComputerDn => _pivotComputer;

    /// <summary>"Otwórz zdalne akcje" is offered (the window has the K1 windows).</summary>
    public bool CanOpenRemoteActions => Services.Ui.Dialogs is IComputerDialogs;

    /// <summary>"Sprawdź komputer źródłowy" of the selected source.</summary>
    [RelayCommand(CanExecute = nameof(CanPivot))]
    private async Task PivotAsync()
    {
        if (SelectedSource is not { } row || Session is not { } session || _report is not { } report)
        {
            return;
        }

        _pivotAccount = report.Account;
        var token = _pivot.Next();
        IsPivotRunning = true;
        _pivotComputer = null;
        OpenRemoteActionsCommand.NotifyCanExecuteChanged();
        PivotHeading = Localizer.Format(L.EventLogsLockout.PivotResolving, row.Value);
        PivotLines = [];
        try
        {
            var lines = await Task.Run(() => PivotCoreAsync(session, row.Source, token), token).ConfigureAwait(true);
            if (!token.IsCancellationRequested)
            {
                PivotLines = lines;
            }
        }
        catch (OperationCanceledException)
        {
            // Another source was checked or the tab closed.
        }
        catch (Exception ex) when (ex is DirectoryException or InvalidOperationException or ObjectDisposedException)
        {
            PivotLines = [Services.Ui.Errors.Translate(ex)];
        }
        finally
        {
            if (_pivot.IsCurrent(token))
            {
                IsPivotRunning = false;
                OpenRemoteActionsCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private bool CanPivot() => SelectedSource is { CanPivot: true } && !IsPivotRunning && HasSession && _report is not null;

    partial void OnSelectedSourceChanged(LockoutSourceRow? value)
    {
        _pivot.Cancel();
        IsPivotRunning = false;
        PivotLines = null;
        PivotHeading = string.Empty;
        _pivotComputer = null;
        OpenRemoteActionsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>"Otwórz zdalne akcje" of the computer the pivot found.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenRemote))]
    private Task OpenRemoteActionsAsync() =>
        _pivotComputer is { } dn && Services.Ui.Dialogs is IComputerDialogs dialogs ? dialogs.ShowRemoteActionsAsync(dn) : Task.CompletedTask;

    private bool CanOpenRemote() => _pivotComputer is not null && CanOpenRemoteActions;

    private async Task<IReadOnlyList<string>> PivotCoreAsync(IDirectorySession session, LockoutSource source, CancellationToken token)
    {
        var lines = new List<string>();
        var names = new List<string>();
        if (source.Source.Kind is AttemptSourceKind.Workstation or AttemptSourceKind.CallerComputer)
        {
            names.Add(source.Source.Value);
        }
        else if (source.Source.Kind == AttemptSourceKind.IpAddress)
        {
            names.AddRange(source.RelatedNames);
            if (await Services.LookupNameAsync(source.Source.Value, token).ConfigureAwait(false) is { } dns)
            {
                names.Add(dns);
                lines.Add(Localizer.Format(L.EventLogsLockout.PivotResolvedFromIp, source.Source.Value, dns));
            }
        }

        DirectoryObject? computer = null;
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            computer = await Services.FindComputerAsync(session, name, token).ConfigureAwait(false);
            if (computer is not null)
            {
                break;
            }
        }

        if (computer is null)
        {
            await SetHeadingAsync(Localizer.Format(L.EventLogsLockout.PivotNotFound, source.Source.Value)).ConfigureAwait(false);
            lines.Add(Localizer[L.EventLogsLockout.PivotNotFoundHint]);
            return lines;
        }

        var facts = ComputerFacts.From(computer);
        _pivotComputer = computer.DistinguishedName;
        await SetHeadingAsync(Localizer.Format(L.EventLogsLockout.PivotComputer, facts.Name, facts.DnsHostName ?? string.Empty)).ConfigureAwait(false);
        if (Services.Remote is not { } remote)
        {
            lines.Add(Localizer[L.EventLogsLockout.PivotNotAvailable]);
            return lines;
        }

        if (string.IsNullOrWhiteSpace(facts.DnsHostName) || !HostNames.IsValid(facts.DnsHostName))
        {
            lines.Add(Localizer.Format(L.EventLogs.ComputerNoDns, facts.Name));
            return lines;
        }

        try
        {
            var target = new RemoteTarget(facts.DistinguishedName, facts.Name, facts.DnsHostName, facts.Type);
            await using var remoteSession = await remote.ConnectAsync(target, session, Services.Ui.Settings.Current.Computers.RemoteOptions(), token).ConfigureAwait(false);
            lines.Add(await PartAsync(async () =>
            {
                var users = (await remoteSession.GetLoggedOnUsersAsync(token).ConfigureAwait(false)).Where(u => MatchesAccount(u.Account)).ToList();
                return users.Count == 0
                    ? Localizer[L.EventLogsLockout.PivotNoSessions]
                    : Localizer.Format(L.EventLogsLockout.PivotSessions, string.Join(", ", users.Select(u => u.IsConsole
                        ? Localizer.Format(L.EventLogsLockout.PivotConsole, u.Account)
                        : u.Account)));
            }).ConfigureAwait(false));
            lines.Add(await PartAsync(async () =>
            {
                var services = (await remoteSession.GetServicesAsync(token).ConfigureAwait(false)).Where(s => MatchesAccount(s.Account)).ToList();
                return services.Count == 0
                    ? Localizer[L.EventLogsLockout.PivotNoServices]
                    : Localizer.Format(L.EventLogsLockout.PivotServices, string.Join(", ", services.Select(s => s.DisplayName is { Length: > 0 } d ? $"{d} ({s.Name})" : s.Name)));
            }).ConfigureAwait(false));
            lines.Add(await PartAsync(async () =>
            {
                var tasks = (await remoteSession.GetScheduledTasksAsync(token).ConfigureAwait(false)).Where(t => MatchesAccount(t.UserId)).ToList();
                return tasks.Count == 0
                    ? Localizer[L.EventLogsLockout.PivotNoTasks]
                    : Localizer.Format(L.EventLogsLockout.PivotTasks, string.Join(", ", tasks.Select(t => t.StoresPassword
                        ? Localizer.Format(L.EventLogsLockout.PivotTaskStoresPassword, t.FullName)
                        : t.FullName)));
            }).ConfigureAwait(false));
        }
        catch (RemoteActionException ex)
        {
            lines.Add(Localizer.Format(L.EventLogsLockout.PivotFailed, RemoteText.Error(Localizer, ex.Kind)));
        }

        return lines;
    }

    private async Task<string> PartAsync(Func<Task<string>> part)
    {
        try
        {
            return await part().ConfigureAwait(false);
        }
        catch (RemoteActionException ex)
        {
            return Localizer.Format(L.EventLogsLockout.PivotFailed, RemoteText.Error(Localizer, ex.Kind));
        }
    }

    private Task SetHeadingAsync(string text) => Dispatcher.RunAsync(() =>
    {
        PivotHeading = text;
        OpenRemoteActionsCommand.NotifyCanExecuteChanged();
    });

    /// <summary>
    /// A value names the investigated account: its SID, <c>DOMAIN\sam</c> or <c>sam</c> (case-insensitive), the UPN, or
    /// <c>sam@realm</c>.
    /// </summary>
    internal bool MatchesAccount(string? value)
    {
        if (_pivotAccount is not { } account || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (account.Sid is { } sid && string.Equals(text, sid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (account.UserPrincipalName is { } upn && string.Equals(text, upn, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var slash = text.LastIndexOf('\\');
        if (slash >= 0)
        {
            text = text[(slash + 1)..];
        }

        var at = text.IndexOf('@', StringComparison.Ordinal);
        if (at > 0)
        {
            text = text[..at];
        }

        return string.Equals(text, account.SamAccountName, StringComparison.OrdinalIgnoreCase);
    }
}
