using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Computers;
using AdMng.Core.Computers.Remote;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;

namespace AdMng.Presentation.Computers;

/// <summary>What the "Komputer" submenu, the palette and the review can do with selected computers.</summary>
public enum ComputerAction
{
    RemoteActions,
    RemoteDesktop,
    Ping,
    ComputerManagement,
    EventViewer,
    ResetAccount,
}

/// <summary>
/// The computer windows of a connection window, implemented by the WPF dialog service (K1). The main window reaches them
/// through its <see cref="IDialogService"/> (<c>dialogs as IComputerDialogs</c>); a host without them hides nothing but
/// the commands do nothing.
/// </summary>
public interface IComputerDialogs
{
    /// <summary>"Przegląd komputerów" (modeless, one per connection window).</summary>
    Task ShowComputerReviewAsync();

    /// <summary>"Zdalne akcje" of one computer (modeless, one per computer per connection window).</summary>
    Task ShowRemoteActionsAsync(string distinguishedName);

    /// <summary>The restart dialog (modal): delay, message, force; null when the operator cancelled.</summary>
    Task<RestartRequest?> AskRestartAsync(RemoteTarget target);

    /// <summary>Runs an action for the selected computers (tools, ping, reset).</summary>
    Task RunComputerActionAsync(ComputerAction action, IReadOnlyList<string> distinguishedNames);

    /// <summary>The computer actions of this connection window (the property sheet's "Stan" page uses them).</summary>
    ComputerActions Actions { get; }
}

/// <summary>What the restart dialog returns.</summary>
/// <param name="Delay">Delay before the restart.</param>
/// <param name="Message">The message shown to the logged-on users (verbatim).</param>
/// <param name="Force">Close applications without asking.</param>
public sealed record RestartRequest(TimeSpan Delay, string Message, bool Force);

/// <summary>
/// The computer actions every entry point shares (K1 design note 4.5, 4.6, 4.8): the remote target of a DN, the local tools
/// (with a confirmation when they use the window's credential), ping and "Resetuj konto" – which is planned, confirmed
/// with the domain typed and executed through the pipeline, never through the batch editor, so that the machine password
/// is never offered for export. One per connection window (scoped).
/// </summary>
public sealed class ComputerActions(
    DialogServices services,
    IChangePlanner planner,
    ILocalToolLauncher? launcher = null,
    IPingService? ping = null,
    IHostAddressResolver? resolver = null)
{
    /// <summary>Echoes of a ping.</summary>
    public const int PingCount = 4;

    /// <summary>Timeout of one echo.</summary>
    public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(2);

    private static readonly string[] TargetAttributes =
        [Attr.ObjectClass, Attr.Cn, Attr.Name, Attr.DnsHostName, Attr.UserAccountControl, Attr.PrimaryGroupId, Attr.OperatingSystem];

    private readonly DialogServices _services = services ?? throw new ArgumentNullException(nameof(services));
    private readonly IChangePlanner _planner = planner ?? throw new ArgumentNullException(nameof(planner));

    private ILocalizer Localizer => _services.Localizer;

    /// <summary>The local tools can be started here (a launcher is registered).</summary>
    public bool CanLaunchTools => launcher is not null;

    public bool CanPing => ping is not null;

    /// <summary>"Ustal lokację" is possible (a resolver is registered).</summary>
    public bool CanResolveSites => resolver is not null;

    /// <summary>The site of one computer (its DC server object, or DNS and the subnets); null without a resolver.</summary>
    public async Task<SiteResolution?> ResolveSiteAsync(IDirectorySession session, ComputerFacts computer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(computer);
        if (resolver is null)
        {
            return null;
        }

        var topology = await Task.Run(() => ComputerSites.ReadTopologyAsync(session, cancellationToken), cancellationToken).ConfigureAwait(true);
        var resolved = await Task.Run(() => ComputerSites.ResolveAsync(resolver, topology, [computer], cancellationToken), cancellationToken).ConfigureAwait(true);
        return resolved.Values.FirstOrDefault();
    }

    /// <summary>The remote target of a computer DN, or null after the operator was told why there is none.</summary>
    public async Task<RemoteTarget?> ResolveTargetAsync(IDirectorySession session, string distinguishedName, string title, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        DirectoryObject? computer;
        try
        {
            computer = await session.GetAsync(distinguishedName, TargetAttributes, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is DirectoryException or InvalidOperationException or ObjectDisposedException)
        {
            await _services.Dialogs.ShowErrorAsync(title, _services.Errors.Translate(ex)).ConfigureAwait(true);
            return null;
        }

        if (computer is null || computer.Kind != ObjectKind.Computer)
        {
            await _services.Dialogs.ShowErrorAsync(title, Localizer[L.Computers.NotAComputer]).ConfigureAwait(true);
            return null;
        }

        var facts = ComputerFacts.From(computer);
        if (!HostNames.IsValid(facts.DnsHostName))
        {
            await _services.Dialogs.ShowErrorAsync(title, Localizer.Format(L.Computers.NoDnsName, facts.Name)).ConfigureAwait(true);
            return null;
        }

        return new RemoteTarget(facts.DistinguishedName, facts.Name, facts.DnsHostName!, facts.Type);
    }

    /// <summary>Runs <paramref name="action"/> for the computers (tools and ping for the first one only).</summary>
    public async Task RunAsync(ComputerAction action, IReadOnlyList<string> distinguishedNames, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(distinguishedNames);
        if (distinguishedNames.Count == 0 || _services.Sessions.Current is not { } session)
        {
            return;
        }

        switch (action)
        {
            case ComputerAction.ResetAccount:
                await ResetAccountsAsync(session, distinguishedNames, cancellationToken).ConfigureAwait(true);
                break;
            case ComputerAction.Ping:
                await PingAsync(session, distinguishedNames[0], cancellationToken).ConfigureAwait(true);
                break;
            case ComputerAction.RemoteDesktop:
                await LaunchAsync(session, LocalTool.RemoteDesktop, distinguishedNames[0], cancellationToken).ConfigureAwait(true);
                break;
            case ComputerAction.ComputerManagement:
                await LaunchAsync(session, LocalTool.ComputerManagement, distinguishedNames[0], cancellationToken).ConfigureAwait(true);
                break;
            case ComputerAction.EventViewer:
                await LaunchAsync(session, LocalTool.EventViewer, distinguishedNames[0], cancellationToken).ConfigureAwait(true);
                break;
            case ComputerAction.RemoteActions when _services.Dialogs is IComputerDialogs dialogs:
                await dialogs.ShowRemoteActionsAsync(distinguishedNames[0]).ConfigureAwait(true);
                break;
        }
    }

    /// <summary>Starts a local tool for the computer; asks first when it runs with the window's credential.</summary>
    public async Task LaunchAsync(IDirectorySession session, LocalTool tool, string distinguishedName, CancellationToken cancellationToken = default)
    {
        var title = Localizer[ToolKey(tool)];
        if (launcher is null)
        {
            await _services.Dialogs.ShowErrorAsync(title, Localizer[L.Computers.ToolsNotAvailable]).ConfigureAwait(true);
            return;
        }

        if (await ResolveTargetAsync(session, distinguishedName, title, cancellationToken).ConfigureAwait(true) is not { } target)
        {
            return;
        }

        if (launcher.UsesWindowCredential(tool, session)
            && !await _services.Dialogs.ConfirmAsync(title, Localizer.Format(L.Computers.ToolCredentialConfirm, target.DnsHostName, session.Info.BoundIdentity), destructive: false).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await launcher.LaunchAsync(tool, target.DnsHostName, session, cancellationToken).ConfigureAwait(true);
        }
        catch (RemoteActionException ex)
        {
            await _services.Dialogs.ShowErrorAsync(title, RemoteText.Error(Localizer, ex.Kind)).ConfigureAwait(true);
        }
    }

    /// <summary>Pings the computer (4 echoes) and shows the result as a notification.</summary>
    public async Task<PingResult?> PingAsync(IDirectorySession session, string distinguishedName, CancellationToken cancellationToken = default)
    {
        var title = Localizer[L.Computers.Ping];
        if (ping is null)
        {
            await _services.Dialogs.ShowErrorAsync(title, Localizer[L.Computers.ToolsNotAvailable]).ConfigureAwait(true);
            return null;
        }

        if (await ResolveTargetAsync(session, distinguishedName, title, cancellationToken).ConfigureAwait(true) is not { } target)
        {
            return null;
        }

        try
        {
            var result = await ping.PingAsync(target.DnsHostName, PingCount, PingTimeout, cancellationToken).ConfigureAwait(true);
            _services.Notifications.Show(
                result.Succeeded ? NotificationSeverity.Success : NotificationSeverity.Warning,
                RemoteText.Ping(Localizer, result));
            return result;
        }
        catch (RemoteActionException ex)
        {
            _services.Notifications.Show(NotificationSeverity.Warning, RemoteText.Error(Localizer, ex.Kind));
            return null;
        }
    }

    /// <summary>
    /// "Resetuj konto": plans the resets (DCs and non-computers are refused by the planner), asks for the domain typed –
    /// the computers lose their trust relationship – and executes the plan in the session it was planned in.
    /// </summary>
    public async Task<BatchResult?> ResetAccountsAsync(IDirectorySession session, IReadOnlyList<string> distinguishedNames, CancellationToken cancellationToken = default)
    {
        var title = Localizer[L.Computers.ResetAccount];
        ChangePlan plan;
        try
        {
            var request = new BatchRequest(distinguishedNames, [new ResetComputerAccountOperation()]) { Title = title };
            var options = new PlanningOptions { MaxDegreeOfParallelism = _services.Settings.Current.BatchMaxParallelism };
            plan = await Task.Run(() => _planner.PlanAsync(session, request, options, null, cancellationToken), cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is DirectoryException or InvalidOperationException or ObjectDisposedException)
        {
            await _services.Dialogs.ShowErrorAsync(title, _services.Errors.Translate(ex)).ConfigureAwait(true);
            return null;
        }

        // Refused (DCs) and skipped (not computers) objects are named in the confirmation.
        var refused = plan.Objects.Where(o => o.HasErrors || o.IsNoOp).ToList();
        if (plan.ExecutableCount == 0)
        {
            var reasons = refused.SelectMany(o => o.Issues.Concat(o.Changes.SelectMany(c => c.Issues)))
                .Where(i => i.Severity == IssueSeverity.Error)
                .Select(i => _services.Errors.TranslateIssue(i, session.Schema))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            await _services.Dialogs.ShowErrorAsync(title, reasons.Count > 0 ? string.Join(Environment.NewLine, reasons) : Localizer[L.Computers.NotAComputer]).ConfigureAwait(true);
            return null;
        }

        var domain = ConfirmationText.Domain(session);
        var executable = plan.Objects.Where(o => !o.HasErrors && !o.IsNoOp).ToList();
        var message = executable.Count == 1
            ? Localizer.Format(L.Computers.ResetConfirmOne, executable[0].DisplayName, domain)
            : Localizer.Format(L.Computers.ResetConfirmMany, executable.Count, domain);
        if (refused.Count > 0)
        {
            message += Environment.NewLine + Localizer.Format(L.Computers.ResetSkipped, string.Join(", ", refused.Select(r => r.DisplayName)));
        }

        message += Environment.NewLine + Environment.NewLine + Localizer[L.Computers.ResetWarning] + Environment.NewLine
            + Localizer.Format(L.Computers.TypeDomainToConfirm, domain);
        if (!await _services.Dialogs.ConfirmTypedAsync(title, message, domain).ConfigureAwait(true))
        {
            return null;
        }

        try
        {
            var result = await _services.Coordinator.ExecutePlanAsync(plan, title, session, null, cancellationToken).ConfigureAwait(true);
            var failed = result.FailedCount;
            _services.Notifications.Show(
                failed == 0 ? NotificationSeverity.Success : NotificationSeverity.Warning,
                Localizer.Format(L.Computers.ResetDone, result.SucceededCount, failed));
            return result;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await _services.Dialogs.ShowErrorAsync(title, _services.Errors.Translate(ex)).ConfigureAwait(true);
            return null;
        }
    }

    /// <summary>The localization key of a tool's title.</summary>
    public static string ToolKey(LocalTool tool) => tool switch
    {
        LocalTool.RemoteDesktop => L.Computers.RemoteDesktop,
        LocalTool.ComputerManagement => L.Computers.ComputerManagement,
        _ => L.Computers.EventViewer,
    };
}
