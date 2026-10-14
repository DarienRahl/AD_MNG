using System.Windows.Threading;
using AdMng.App.Localization;
using AdMng.App.Services;
using AdMng.App.Views;
using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.Formatting;
using AdMng.Core.Security;
using AdMng.Core.Templates;
using AdMng.Directory;
using AdMng.Presentation;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PropertySheets;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdMng.App;

/// <summary>
/// Composition root: the Generic Host with Core engines, the LDAP connector, the presentation layer and the WPF
/// implementations of the UI services. Used by the application, the smoke test and the UI tests (which replace single
/// services, e.g. the directory connector, through <c>configure</c>).
/// </summary>
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

        // Core engines: stateless or internally synchronized, shared by all windows.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ITemplateEngine, TemplateEngine>();
        services.AddSingleton<ICsvCodec, CsvCodec>();
        services.AddSingleton<PasswordGenerator>();
        services.AddSingleton<IChangePlanner, ChangePlanner>();
        services.AddSingleton<IAuditLog, InMemoryAuditLog>();
        services.AddSingleton<IBatchExecutor, BatchExecutor>();
        services.AddSingleton<IRollbackPlanner, RollbackPlanner>();
        services.AddSingleton<IPowerShellExporter, PowerShellExporter>();
        services.AddSingleton<ICsvImportPlanner, CsvImportPlanner>();
        services.AddSingleton<BatchReportExporter>();
        services.AddSingleton<AuditLogExporter>();

        // Directory access (System.DirectoryServices.Protocols).
        services.AddSingleton<IDirectoryConnector>(sp =>
            new LdapDirectoryConnector(sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<TimeProvider>()));

        // Presentation layer.
        services.AddAdMngPresentation();
        services.AddAttributeFormatter(schema => new AttributeFormatter(schema));

        // WPF implementations of the UI services.
        var uiDispatcher = new WpfUiDispatcher(dispatcher);
        services.AddSingleton(uiDispatcher);
        services.AddSingleton<IUiDispatcher>(uiDispatcher);
        services.AddSingleton<IAppPaths>(new AppPaths(options.SettingsDirectory));
        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<IAppShutdown, ApplicationShutdown>();
        services.AddSingleton<AppNavigator>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IFileDialogService, FileDialogService>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<NotificationService>());
        services.AddSingleton<ThemeService>();
        services.AddSingleton<IThemeService>(sp => sp.GetRequiredService<ThemeService>());
        services.AddSingleton<AppearanceService>();
        services.AddSingleton(sp => new GlobalExceptionHandler(
            sp.GetRequiredService<WpfUiDispatcher>(),
            () => sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<ErrorTranslator>(),
            sp.GetRequiredService<ILocalizer>(),
            sp.GetRequiredService<ILogger<GlobalExceptionHandler>>()));

        // Windows. The two shell windows are created by the navigator; dialogs through IWindowService by view model.
        services.AddTransient<ConnectWindow>();
        services.AddTransient<MainWindow>();
        services.AddWindow<MessageDialogViewModel, MessageDialogWindow>();
        services.AddWindow<SavedViewEditorViewModel, SavedViewEditorWindow>();
        services.AddWindow<ColumnChooserViewModel, ColumnChooserWindow>();

        // ---- C2a: object dialogs (view models from IDialogViewModelFactory, opened by DialogService) ----
        services.AddWindow<PropertySheetViewModel, PropertiesWindow>();
        services.AddWindow<NewObjectViewModel, NewObjectWindow>();
        services.AddWindow<ResetPasswordViewModel, ResetPasswordWindow>();
        services.AddWindow<ObjectPickerViewModel, ObjectPickerWindow>();
        services.AddWindow<ContainerPickerViewModel, ContainerPickerWindow>();
        services.AddWindow<FindViewModel, FindWindow>();

        // ---- C2b: batch and tool windows (opened through ToolWindows) ----
        services.AddSingleton<ToolWindows>();
        services.AddWindow<BatchEditorViewModel, BatchEditorWindow>();
        services.AddWindow<SessionLogViewModel, SessionLogWindow>();
        services.AddWindow<PowerShellHistoryViewModel, PowerShellHistoryWindow>();
        services.AddWindow<SettingsViewModel, SettingsWindow>();
        // ---- end C2b ----

        // ---- AD Recycle Bin (opened through ToolWindows) ----
        services.AddWindow<RecycleBinViewModel, RecycleBinWindow>();
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
