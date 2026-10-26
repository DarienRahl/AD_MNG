using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Shell;
using AdMng.Presentation.UserMessages;
using AdMng.Presentation.Views;
using AdMng.Presentation.Watchdog;

namespace AdMng.Presentation.Tests.Connections;

/// <summary>
/// The one list of services that exist once per connection window (design note docs/dev/multi-window.md 2.2/2.3). The
/// registration tests assert that each is registered Scoped - a singleton registered before <c>AddAdMngPresentation</c>
/// would win over the TryAdd and silently share, for example, one audit log between all windows - and that two scopes
/// get two instances. The UI tests link this file and add the App services.
/// </summary>
internal static class ConnectionLifetimes
{
    /// <summary>Presentation services of one connection window.</summary>
    public static IReadOnlyList<Type> PerConnection { get; } =
    [
        typeof(ISessionManager),
        typeof(IDirectoryChangeNotifier),
        typeof(IAuditLog),
        typeof(IBatchExecutor),
        typeof(IChangeCoordinator),
        typeof(ActivityCenterViewModel),
        typeof(ObjectListServices),
        typeof(PowerShellHistory),
        typeof(DialogServices),
        typeof(IDialogViewModelFactory),
        typeof(UserMessageServices),
        typeof(WatchdogServices),
        typeof(WatchdogObjectHistoryLauncher),
        typeof(Func<WatchdogDocumentViewModel>),
        typeof(ConnectionContext),
        typeof(ShellViewModel),
    ];

    /// <summary>Application-wide services: the same instance in every connection window.</summary>
    public static IReadOnlyList<Type> Shared { get; } =
    [
        typeof(ISettingsStore),
        typeof(ViewLibrary),
        typeof(Core.Messaging.MessageRateLimiter),
        typeof(SmtpCredentialCache),
        typeof(WatchdogConnectionService),
        typeof(ConnectionRegistry),
    ];
}
