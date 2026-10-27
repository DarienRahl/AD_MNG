using System.Windows.Threading;
using AdMng.App.Connections;
using AdMng.App.Localization;
using AdMng.App.Services;
using AdMng.App.Views;
using AdMng.App.Views.Watchdog;
using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.Formatting;
using AdMng.Core.GroupPolicy;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Html;
using AdMng.Core.PrivilegedAccess;
using AdMng.Core.Security;
using AdMng.Core.Templates;
using AdMng.Directory;
using AdMng.Mail;
using AdMng.Presentation;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Comparison;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PropertySheets;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Presentation.UserMessages;
using AdMng.Presentation.Views;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdMng.App;

/// <summary>
/// Composition root: the Generic Host with Core engines, the LDAP connector, the presentation layer and the WPF
/// implementations of the UI services. Used by the application, the smoke test and the UI tests (which replace single
/// services, e.g. the directory connector, through <c>configure</c>).
/// </summary>
/// <remarks>
/// Every connection window is one DI scope (design note docs/dev/multi-window.md 2): its session, change pipeline,
/// dialogs, toasts, clipboard, file dialogs, tool windows and window service are scoped and created by
/// <see cref="ConnectionWindowManager"/>; they are never resolved from the root provider. Settings, localization, theme,
/// the directory connector, the engines and the Watchdog service connection are shared singletons.
/// </remarks>
public static class AppHost
{
    /// <summary>Builds the host. Must be called on the UI thread (the dispatcher of the calling thread is captured).</summary>
    /// <param name="options">Command line options.</param>
    /// <param name="configure">Last-chance changes of the registrations (tests).</param>
    public static IHost Build(StartupOptions options, Action<IServiceCollection>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "AdMng",
            DisableDefaults = true,
        });

        // Diagnostics go to the debugger output only - never to files, and never with directory values.
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        builder.Services.AddAdMngApp(options, Dispatcher.CurrentDispatcher);
        configure?.Invoke(builder.Services);
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        }));
        return builder.Build();
    }

    /// <summary>Registers every service of the application.</summary>
    public static IServiceCollection AddAdMngApp(this IServiceCollection services, StartupOptions options, Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dispatcher);

        // Core engines: stateless or internally synchronized, shared by all windows. The audit log and the batch executor
        // are per connection window: AddAdMngPresentation registers them scoped (registering them here first would make
        // them singletons shared by all windows).
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ITemplateEngine, TemplateEngine>();
        services.AddSingleton<ICsvCodec, CsvCodec>();
        services.AddSingleton<PasswordGenerator>();
        services.AddSingleton<IChangePlanner, ChangePlanner>();
        services.AddSingleton<IRollbackPlanner, RollbackPlanner>();
        services.AddSingleton<IPowerShellExporter, PowerShellExporter>();
        services.AddSingleton<ICsvImportPlanner, CsvImportPlanner>();
        services.AddSingleton<BatchReportExporter>();
        services.AddSingleton<AuditLogExporter>();

        // Directory access (System.DirectoryServices.Protocols).
        services.AddSingleton<IDirectoryConnector>(sp =>
            new LdapDirectoryConnector(sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<TimeProvider>()));

        // Privileged access review (README U4c): local rights from GPOs, read from SYSVOL of the session's DC in memory.
        // Application-wide and stateless: every run hands in the session of the connection window that runs the review, and
        // SYSVOL is opened on that session's DC with that session's credential (never another window's).
        services.AddSingleton<ISysvolFileReader, WindowsSysvolFileReader>();
        services.AddSingleton<IPrivilegeGrantSource>(sp => new GroupPolicyGrantSource(sp.GetRequiredService<ISysvolFileReader>()));

        // AdMng Watchdog service (HTTPS + Kerberos; configured from the settings by WatchdogConnectionService).
        services.AddSingleton<IWatchdogClient, WatchdogHttpClient>();

        // Presentation layer.
        services.AddAdMngPresentation();
        services.AddAttributeFormatter(schema => new AttributeFormatter(schema));

        // WPF implementations of the UI services: application-wide.
        var uiDispatcher = new WpfUiDispatcher(dispatcher);
        services.AddSingleton(uiDispatcher);
        services.AddSingleton<IUiDispatcher>(uiDispatcher);
        services.AddSingleton<IAppPaths>(new AppPaths(options.SettingsDirectory));
        services.AddSingleton<IAppShutdown, ApplicationShutdown>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<IThemeService>(sp => sp.GetRequiredService<ThemeService>());
        services.AddSingleton<AppearanceService>();

        // Connection windows: one manager for the application, one scope per window.
        services.AddSingleton<ConnectionWindowManager>();
        services.AddSingleton<IConnectionWindows>(sp => sp.GetRequiredService<ConnectionWindowManager>());
        services.AddSingleton<IAppErrorPresenter>(sp => sp.GetRequiredService<ConnectionWindowManager>());
        services.AddSingleton<IConnectionAttention>(sp => sp.GetRequiredService<ConnectionWindowManager>());
        services.AddSingleton<GlobalExceptionHandler>();

        // WPF implementations of the UI services: per connection window (the windows, dialogs, toasts, clipboard and file
        // dialogs of a connection never reach another connection's windows). The clipboard service is not disposable:
        // its timer still clears a copied password after its window closed.
        services.AddScoped<IWindowService, WindowService>();
        services.AddScoped<ConnectionNavigator>();
        services.AddScoped<DialogService>();
        services.AddScoped<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddScoped<IFileDialogService, FileDialogService>();
        services.AddScoped<IClipboardService, ClipboardService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<INotificationService>(sp => sp.GetRequiredService<NotificationService>());

        // Windows. The connect and main window of a connection are created by its navigator (transient, from its scope);
        // dialogs through IWindowService by view model.
        services.AddTransient<ConnectWindow>();
        services.AddTransient<MainWindow>();
        services.AddWindow<MessageDialogViewModel, MessageDialogWindow>();
        services.AddWindow<SavedViewEditorViewModel, SavedViewEditorWindow>();
        services.AddWindow<ColumnChooserViewModel, ColumnChooserWindow>();
        services.AddWindow<LogonHoursViewModel, LogonHoursWindow>();

        // ---- C2a: object dialogs (view models from IDialogViewModelFactory, opened by DialogService) ----
        services.AddWindow<PropertySheetViewModel, PropertiesWindow>();
        services.AddWindow<NewObjectViewModel, NewObjectWindow>();
        services.AddWindow<ResetPasswordViewModel, ResetPasswordWindow>();
        services.AddWindow<ObjectPickerViewModel, ObjectPickerWindow>();
        services.AddWindow<ContainerPickerViewModel, ContainerPickerWindow>();
        services.AddWindow<FindViewModel, FindWindow>();
        services.AddWindow<ChangeDomainControllerViewModel, ChangeDomainControllerWindow>();

        // ---- C2b: batch and tool windows (opened through ToolWindows, one per connection window) ----
        services.AddScoped<ToolWindows>();
        services.AddWindow<BatchEditorViewModel, BatchEditorWindow>();
        services.AddWindow<SessionLogViewModel, SessionLogWindow>();
        services.AddWindow<PowerShellHistoryViewModel, PowerShellHistoryWindow>();
        services.AddWindow<SettingsViewModel, SettingsWindow>();
        // ---- end C2b ----

        // ---- AD Recycle Bin (opened through ToolWindows) ----
        services.AddWindow<RecycleBinViewModel, RecycleBinWindow>();

        // ---- Account review, README U2 and U4 (opened through ToolWindows) ----
        services.AddWindow<AccountReviewViewModel, AccountReviewWindow>();

        // ---- Saved filters of the filter builder, README U4 (the list through ToolWindows, the editor modal) ----
        services.AddWindow<Presentation.Filters.SavedFiltersViewModel, SavedFiltersWindow>();
        services.AddWindow<Presentation.Filters.FilterEditorViewModel, FilterEditorWindow>();

        // ---- Messages to users, README U1: direct SMTP (AdMng.Mail), the window opened by DialogService ----
        services.AddSingleton<IDirectSmtpTransportFactory>(sp => new SmtpTransportFactory(sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<IUserMessageLauncher>(sp => sp.GetRequiredService<DialogService>());
        services.AddWindow<UserMessageComposerViewModel, UserMessagesWindow>();

        // ---- U1c: formatted messages (design note docs/dev/u1c-u1d-rich-html-and-salutations.md) – application-wide and
        // without per-connection state: the one WebView2 environment of the rendered previews (its temp folder is deleted at
        // exit) and the reader of Outlook's clipboard images that expands 8.3 names of the temp folder. ----
        services.AddSingleton<WebView2Host>();
        services.Replace(ServiceDescriptor.Singleton<IPastedImageSource>(_ => new PastedImageFileSource(expandShortNames: ShortPathNames.Expand)));

        // ---- Comparison of objects between connections (design note docs/dev/multi-window.md 8), opened by DialogService ----
        services.AddScoped<IObjectComparisonLauncher>(sp => sp.GetRequiredService<DialogService>());
        services.AddWindow<ObjectComparisonViewModel, CompareObjectsWindow>();

        // ---- Watchdog: certificate confirmation on first connection (stage B design 18 item 1) ----
        services.AddWindow<WatchdogCertificateTrustViewModel, WatchdogCertificateTrustWindow>();
        return services;
    }

    /// <summary>
    /// Loads the settings and applies language and theme. Call on the UI thread after <see cref="Build"/> and before
    /// the first window is created (XAML texts bind to the localizer set here).
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider services, StartupOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        TranslationSource.Localizer = services.GetRequiredService<ILocalizer>();
        await services.GetRequiredService<ISettingsStore>().LoadAsync().ConfigureAwait(true);
        services.GetRequiredService<AppearanceService>().Start(options.Language);
    }
}
