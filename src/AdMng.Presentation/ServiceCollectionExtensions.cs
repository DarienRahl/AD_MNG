using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Csv;
using AdMng.Core.Messaging;
using AdMng.Core.Formatting;
using AdMng.Core.Schema;
using AdMng.Core.Security;
using AdMng.Core.Templates;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Computers;
using AdMng.Presentation.Connection;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Errors;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Views;
using AdMng.Presentation.Watchdog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.Presentation;

/// <summary>DI registration of the presentation layer.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers view models and presentation services. The host must also register:
    /// <list type="bullet">
    /// <item>UI services implemented by the WPF app: <see cref="Services.IUiDispatcher"/>, <see cref="Services.IThemeService"/>,
    /// <see cref="Services.IAppPaths"/> (singletons) and, per connection window (scoped), <see cref="Services.IDialogService"/>,
    /// <see cref="Services.IFileDialogService"/>, <see cref="Services.IClipboardService"/>, <see cref="Services.INotificationService"/>;</item>
    /// <item>Core/Directory implementations: <see cref="Core.Directory.IDirectoryConnector"/>, <see cref="Core.Batch.IChangePlanner"/>,
    /// <see cref="Core.Csv.ICsvCodec"/> and the attribute formatter (<see cref="AddAttributeFormatter"/>);</item>
    /// <item>the watchdog client <see cref="AdMng.Watchdog.Client.IWatchdogClient"/> (singleton; the app registers
    /// <c>WatchdogHttpClient</c>).</item>
    /// </list>
    /// Every connection window is one DI scope: its session manager, connection context, change bus, audit log, batch
    /// executor, activities, coordinator, shell, dialog services and Watchdog tab services are scoped and must never be
    /// resolved from the root provider (or registered before this call with another lifetime). The pure AdMng.Core engines
    /// (planner, CSV codec and import planner, rollback, PowerShell export, templates, password generator, report/audit
    /// exporters) are singletons with their default implementations unless the host registered them before. Window view
    /// models are created through <see cref="IDialogViewModelFactory"/>.
    /// </summary>
    public static IServiceCollection AddAdMngPresentation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Infrastructure (singletons shared by all connection windows).
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ILocalizer, ResxLocalizer>();
        services.TryAddSingleton<Services.IRegionalSettings, Services.SystemRegionalSettings>();
        services.TryAddSingleton<ISettingsStore, JsonSettingsStore>();
        services.TryAddSingleton<ErrorTranslator>();
        services.TryAddSingleton<ViewLibrary>();
        services.TryAddSingleton<ConnectionRegistry>();

        // Saved filters (U4) are shared settings like saved views: one library for every connection window.
        services.TryAddSingleton<Filters.FilterLibrary>();
        services.AddAdMngComputers();

        // Grant sources of the privileged access review beyond the groups and settings (README U4b: ACL delegations).
        // Application-wide and stateless: a source reads only through the session of the scan context, i.e. the session of
        // the connection window that runs the review.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Core.PrivilegedAccess.IPrivilegeGrantSource, Core.PrivilegedAccess.DirectoryAclGrantSource>());
        services.AddAdMngGroupPolicy();

        // One connection window = one DI scope (design note docs/dev/multi-window.md 2.2): its session, change bus, audit
        // log, executor, activities and everything built on them. Never resolved from the root provider. A host that
        // registers one of these BEFORE this call keeps its own lifetime (TryAdd), so hosts must not pre-register them as
        // singletons: one audit log or executor would then be shared by all windows.
        services.TryAddScoped<ISessionManager, SessionManager>();
        services.TryAddScoped<ConnectionContext>();
        services.TryAddScoped<IDirectoryChangeNotifier, DirectoryChangeNotifier>();
        services.TryAddScoped<IAuditLog, InMemoryAuditLog>();
        services.TryAddScoped<IBatchExecutor, BatchExecutor>();
        services.TryAddScoped<ActivityCenterViewModel>();
        services.TryAddScoped<IChangeCoordinator, ChangeCoordinator>();
        services.TryAddScoped<ObjectListServices>();

        // Pure Core engines (defaults; a host registration made before this call wins).
        services.TryAddSingleton<ITemplateEngine, TemplateEngine>();
        services.TryAddSingleton<PasswordGenerator>();
        services.TryAddSingleton<ICsvCodec, CsvCodec>();
        services.TryAddSingleton<IChangePlanner, ChangePlanner>();
        services.TryAddSingleton<IRollbackPlanner, RollbackPlanner>();
        services.TryAddSingleton<IPowerShellExporter, PowerShellExporter>();
        services.TryAddSingleton<ICsvImportPlanner, CsvImportPlanner>();
        services.TryAddSingleton<BatchReportExporter>();
        services.TryAddSingleton<AuditLogExporter>();

        // Dialogs and secondary windows (B2): per connection window.
        services.TryAddScoped<PowerShellHistory>();
        services.TryAddSingleton<BatchEngines>();
        services.TryAddScoped<DialogServices>();

        // Messages to users (README U1). Direct SMTP needs the host's IDirectSmtpTransportFactory (AdMng.Mail). The SMTP
        // password cache and the daily budget are application-wide (one SMTP server, one budget for all windows).
        services.TryAddSingleton<UserMessages.SmtpCredentialCache>();
        services.TryAddSingleton<UserMessages.WatchdogUserMessageTransport>();
        services.TryAddSingleton<UserMessages.UserMessageTransportSelector>();
        services.TryAddSingleton<UserMessages.UserMessageTemplateLibrary>();
        services.TryAddSingleton(sp => new MessageRateLimiter(sp.GetRequiredService<TimeProvider>()));
        services.TryAddScoped<UserMessages.UserMessageServices>();
        services.TryAddScoped<IDialogViewModelFactory, DialogViewModelFactory>();

        // View models (one per window / dialog). The shell is per connection window: the window, its tools and the
        // navigator reach the same instance through the scope.
        services.TryAddTransient<ConnectViewModel>();
        services.TryAddTransient<NavigationTreeViewModel>();
        services.TryAddScoped<ShellViewModel>();
        services.TryAddTransient<CommandPaletteViewModel>();
        services.TryAddTransient<SavedViewEditorViewModel>();
        services.TryAddTransient<SessionLogViewModel>();
        services.TryAddTransient<PowerShellHistoryViewModel>();
        services.TryAddTransient<RecycleBinViewModel>();
        services.TryAddTransient<SettingsViewModel>();
        services.TryAddTransient<CsvImportViewModel>();
        services.TryAddTransient(sp => sp.GetRequiredService<ObjectListServices>().CreateList());

        // Watchdog tab (the host registers IWatchdogClient): one service connection for the application, a tab, a history
        // launcher and services per connection window. The factory creates tabs with "new": a transient resolved from the
        // scope would be kept alive by it until the window closes.
        services.TryAddSingleton<WatchdogConnectionService>();
        services.TryAddScoped<WatchdogServices>();
        services.TryAddScoped<WatchdogObjectHistoryLauncher>();
        services.TryAddScoped<Func<WatchdogDocumentViewModel>>(sp =>
        {
            var watchdog = sp.GetRequiredService<WatchdogServices>();
            return () => new WatchdogDocumentViewModel(watchdog);
        });
        return services;
    }

    /// <summary>Registers the attribute formatter factory (e.g. <c>s =&gt; new AttributeFormatter(s)</c> from AdMng.Core).</summary>
    public static IServiceCollection AddAttributeFormatter(this IServiceCollection services, Func<ISchemaCatalog, IAttributeFormatter> factory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);
        services.AddSingleton<IAttributeFormatterFactory>(new DelegateAttributeFormatterFactory(factory));
        return services;
    }
}
