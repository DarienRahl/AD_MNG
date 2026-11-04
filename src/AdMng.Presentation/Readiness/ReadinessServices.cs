using AdMng.Core.Computers;
using AdMng.Core.Computers.Remote;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Core.Csv;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.EventLogs;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Watchdog;

namespace AdMng.Presentation.Readiness;

/// <summary>
/// The services the readiness check of one connection window reuses (scoped parameter object, design note
/// docs/dev/s-pilot-readiness.md 2): the window's UI services and session (<see cref="DialogServices"/>), the store of the
/// last results (<see cref="IReadinessResults"/>), the SYSVOL connection of the Group Policy documents, the event log
/// services (connector, DC list, computer lookup), the K1 remote client and resolver, the Watchdog connection, classic
/// Outlook (the diagnostics of Korespondencja), the facts of this computer and the Windows release catalogue. Every
/// optional service missing in a host (tests, Linux) turns its checks into "not checked". Everything is read-only.
/// </summary>
public sealed class ReadinessServices(
    DialogServices ui,
    ReadinessResults results,
    ICsvCodec csv,
    WindowsReleaseCatalog releases,
    GroupPolicyReader reader,
    GroupPolicyServices? groupPolicy = null,
    EventLogServices? eventLogs = null,
    IRemoteComputerClient? remote = null,
    IHostAddressResolver? resolver = null,
    WatchdogConnectionService? watchdog = null,
    IOutlookClient? outlook = null,
    ILocalComputerInfo? computer = null)
{
    /// <summary>Session, dialogs, files, clipboard, notifications, settings, clock of the connection window.</summary>
    public DialogServices Ui { get; } = ui ?? throw new ArgumentNullException(nameof(ui));

    /// <summary>The last results of this window (the problem report reads them through <see cref="IReadinessResults"/>).</summary>
    public ReadinessResults Results { get; } = results ?? throw new ArgumentNullException(nameof(results));

    public ICsvCodec Csv { get; } = csv ?? throw new ArgumentNullException(nameof(csv));

    /// <summary>The end-of-support dates of Windows releases (this computer's support state).</summary>
    public WindowsReleaseCatalog Releases { get; } = releases ?? throw new ArgumentNullException(nameof(releases));

    /// <summary>The Group Policy reader (default GPOs, PDC emulator).</summary>
    public GroupPolicyReader Reader { get; } = reader ?? throw new ArgumentNullException(nameof(reader));

    /// <summary>The SYSVOL connection of the Group Policy documents (null: SYSVOL is not checked).</summary>
    public GroupPolicyServices? GroupPolicy { get; } = groupPolicy;

    /// <summary>The event log services (null: the event logs are not checked).</summary>
    public EventLogServices? EventLogs { get; } = eventLogs;

    /// <summary>WinRM/CIM with the window's credential (null: not checked).</summary>
    public IRemoteComputerClient? Remote { get; } = remote;

    /// <summary>The DNS resolver of the remote actions (null: not checked).</summary>
    public IHostAddressResolver? Resolver { get; } = resolver;

    /// <summary>The Watchdog connection (null: the host has no Watchdog client).</summary>
    public WatchdogConnectionService? Watchdog { get; } = watchdog;

    /// <summary>Classic Outlook of Korespondencja (null: not checked).</summary>
    public IOutlookClient? Outlook { get; } = outlook;

    /// <summary>This computer (null: the runtime's own facts).</summary>
    public ILocalComputerInfo Computer { get; } = computer ?? new RuntimeComputerInfo();

    public ILocalizer Localizer => Ui.Localizer;

    public TimeProvider Time => Ui.Time;

    /// <summary>The connection window's lifetime: a check stops when the window closes.</summary>
    public CancellationToken Lifetime => Ui.Connection?.Lifetime ?? CancellationToken.None;
}
