using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Csv;
using AdMng.Core.Formatting;
using AdMng.Core.Schema;
using AdMng.Core.Security;
using AdMng.Core.Templates;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Connection;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Errors;
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
    /// <item>UI services implemented by the WPF app: <see cref="Services.IUiDispatcher"/>, <see cref="Services.IDialogService"/>,
    /// <see cref="Services.IFileDialogService"/>, <see cref="Services.IClipboardService"/>, <see cref="Services.INotificationService"/>,
    /// <see cref="Services.IThemeService"/>, <see cref="Services.IAppPaths"/>;</item>
    /// <item>Core/Directory implementations: <see cref="Core.Directory.IDirectoryConnector"/>, <see cref="Core.Batch.IChangePlanner"/>,
    /// <see cref="Core.Batch.IBatchExecutor"/>, <see cref="Core.Csv.ICsvCodec"/> and the attribute formatter
    /// (<see cref="AddAttributeFormatter"/>);</item>
    /// <item>the watchdog client <see cref="AdMng.Watchdog.Client.IWatchdogClient"/> (singleton; the app registers
    /// <c>WatchdogHttpClient</c>).</item>
    /// </list>
    /// The pure AdMng.Core engines (planner, executor, CSV codec and import planner, rollback, PowerShell export,
    /// templates, password generator, report/audit exporters) are registered with their default implementations
    /// unless the host registered them before. Window view models are created through <see cref="IDialogViewModelFactory"/>.
    /// </summary>
    public static IServiceCollection AddAdMngPresentation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Infrastructure (singletons shared by all windows).
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ILocalizer, ResxLocalizer>();
        services.TryAddSingleton<Services.IRegionalSettings, Services.SystemRegionalSettings>();
        services.TryAddSingleton<ISettingsStore, JsonSettingsStore>();
        services.TryAddSingleton<ISessionManager, SessionManager>();
        services.TryAddSingleton<ErrorTranslator>();
        services.TryAddSingleton<IDirectoryChangeNotifier, DirectoryChangeNotifier>();
        services.TryAddSingleton<IAuditLog, InMemoryAuditLog>();
        services.TryAddSingleton<ActivityCenterViewModel>();
        services.TryAddSingleton<IChangeCoordinator, ChangeCoordinator>();
        services.TryAddSingleton<ViewLibrary>();
        services.TryAddSingleton<Filters.FilterLibrary>();
        services.TryAddSingleton<ObjectListServices>();

        // Pure Core engines (defaults; a host registration made before this call wins).
        services.TryAddSingleton<ITemplateEngine, TemplateEngine>();
        services.TryAddSingleton<PasswordGenerator>();
        services.TryAddSingleton<ICsvCodec, CsvCodec>();
        services.TryAddSingleton<IChangePlanner, ChangePlanner>();
        services.TryAddSingleton<IBatchExecutor, BatchExecutor>();
        services.TryAddSingleton<IRollbackPlanner, RollbackPlanner>();
        services.TryAddSingleton<IPowerShellExporter, PowerShellExporter>();
        services.TryAddSingleton<ICsvImportPlanner, CsvImportPlanner>();
        services.TryAddSingleton<BatchReportExporter>();
        services.TryAddSingleton<AuditLogExporter>();

        // Dialogs and secondary windows (B2).
        services.TryAddSingleton<PowerShellHistory>();
        services.TryAddSingleton<BatchEngines>();
        services.TryAddSingleton<DialogServices>();

        // Messages to users (README U1). Direct SMTP needs the host's IDirectSmtpTransportFactory (AdMng.Mail).
        services.TryAddSingleton<UserMessages.SmtpCredentialCache>();
        services.TryAddSingleton<UserMessages.WatchdogUserMessageTransport>();
        services.TryAddSingleton<UserMessages.UserMessageTransportSelector>();
        services.TryAddSingleton<UserMessages.UserMessageTemplateLibrary>();
        services.TryAddSingleton<UserMessages.UserMessageServices>();
        services.TryAddSingleton<IDialogViewModelFactory, DialogViewModelFactory>();

        // View models (one per window / dialog).
        services.TryAddTransient<ConnectViewModel>();
        services.TryAddTransient<NavigationTreeViewModel>();
        services.TryAddTransient<ShellViewModel>();
        services.TryAddTransient<CommandPaletteViewModel>();
        services.TryAddTransient<SavedViewEditorViewModel>();
        services.TryAddTransient<SessionLogViewModel>();
        services.TryAddTransient<PowerShellHistoryViewModel>();
        services.TryAddTransient<RecycleBinViewModel>();
        services.TryAddTransient<SettingsViewModel>();
        services.TryAddTransient<CsvImportViewModel>();
        services.TryAddTransient(sp => sp.GetRequiredService<ObjectListServices>().CreateList());

        // Watchdog tab (the host registers IWatchdogClient). The factory creates tabs with "new": a transient resolved from
        // the root provider would be kept alive by the container until exit.
        services.TryAddSingleton<WatchdogConnectionService>();
        services.TryAddSingleton<WatchdogServices>();
        services.TryAddSingleton<WatchdogObjectHistoryLauncher>();
        services.TryAddSingleton<Func<WatchdogDocumentViewModel>>(sp =>
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
