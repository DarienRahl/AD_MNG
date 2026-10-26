using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.Views;
using AdMng.Core.Csv;
using AdMng.Core.Model;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.UserMessages;
using AdMng.Presentation.Views;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AdMng.App;

/// <summary>
/// <c>AdMng.exe --smoke-test</c> (CI on Windows): builds the real host with every service, renders the connect window,
/// the main window (disconnected) with the Watchdog tab (not configured), the Watchdog certificate confirmation and the
/// dialogs of this release off-screen in English and Polish, and fails on any exception or WPF binding error. Exit codes: 0 = passed, 1 = failures (printed
/// to stderr), 2 = did not finish within 30 seconds. Settings go to a temporary folder that is deleted afterwards;
/// nothing touches a directory or a watchdog service.
/// </summary>
internal static class SmokeTest
{
    public const int Passed = 0;
    public const int Failed = 1;
    public const int TimedOut = 2;

    private const int AttachParentProcess = -1;
    private const int StdErrorHandle = -12;
    private static readonly TimeSpan FailSafe = TimeSpan.FromSeconds(30);

    // Rooted here: a timer referenced only by the (possibly stuck) async state machine could be collected and never fire.
    private static Timer? _failSafe;

    public static void Start(App app, StartupOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);

        // Best effort: show the report in the console the test was started from (AdMng.exe is a GUI application) -
        // unless the error output is already redirected (CI captures it into a file).
        if (GetStdHandle(StdErrorHandle) is 0 or -1)
        {
            _ = AttachConsole(AttachParentProcess);
        }

        var failSafe = _failSafe = new Timer(
            static _ =>
            {
                Console.Error.WriteLine("SMOKE TEST TIMED OUT after 30 s.");
                Environment.Exit(TimedOut);
            },
            null,
            FailSafe,
            Timeout.InfiniteTimeSpan);
        Run(app, options, failSafe);
    }

    private static async void Run(App app, StartupOptions options, Timer failSafe)
    {
        var failures = new List<string>();
        var settingsDirectory = Path.Combine(Path.GetTempPath(), "AdMng-smoke-" + Guid.NewGuid().ToString("N"));
        using var bindings = BindingErrorListener.Install();
        IHost? host = null;
        GlobalExceptionHandler? exceptions = null;
        try
        {
            host = AppHost.Build(options with { SettingsDirectory = settingsDirectory });
            exceptions = host.Services.GetRequiredService<GlobalExceptionHandler>();
            exceptions.Recorder = ex => failures.Add($"Unhandled exception: {ex}");
            exceptions.Attach(app);
            await AppHost.InitializeAsync(host.Services, options).ConfigureAwait(true);

            var localizer = host.Services.GetRequiredService<ILocalizer>();
            foreach (var language in (string[])["en", "pl"])
            {
                localizer.SetCulture(ResxLocalizer.ResolveCulture(language));
                await RenderWindowsAsync(host.Services, localizer).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            failures.Add($"Exception: {ex}");
        }
        finally
        {
            UiTestSupport.CloseAllWindows();
        }

        failures.AddRange(bindings.Errors.Select(e => $"Binding error: {e}"));
        exceptions?.Detach();
        Report(failures);
        if (host is not null)
        {
            try
            {
                await host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
                if (host is IAsyncDisposable disposable)
                {
                    await disposable.DisposeAsync().ConfigureAwait(true);
                }
                else
                {
                    host.Dispose();
                }
            }
            catch (Exception ex)
            {
                // The exception handler is detached: report it here instead of crashing with an unrelated exit code.
                failures.Add($"Host shutdown: {ex}");
                Report([failures[^1]]);
            }
        }

        TryDelete(settingsDirectory);

        // Only now: a host that hangs while stopping is still caught by the fail-safe.
        await failSafe.DisposeAsync().ConfigureAwait(true);
        app.Shutdown(failures.Count == 0 ? Passed : Failed);
    }

    private static async Task RenderWindowsAsync(IServiceProvider services, ILocalizer localizer)
    {
        var windows = services.GetRequiredService<IWindowService>();

        var connect = services.GetRequiredService<ConnectWindow>();
        UiTestSupport.ShowOffScreen(connect);
        connect.ViewModel.Authentication = Core.Directory.AuthenticationMode.Credentials;
        connect.ViewModel.SaveProfile = true;

        var main = services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        await main.Shell.InitializeAsync().ConfigureAwait(true);
        await RenderWatchdogTabAsync(main).ConfigureAwait(true);
        main.Shell.CommandPalette.OpenCommand.Execute(null);

        var dialog = windows.CreateWindow(MessageDialogViewModel.TypedConfirmation(
            localizer[L.Commands.Delete], localizer.Format(L.Delete.ConfirmMany, 3), "3", localizer));
        UiTestSupport.ShowOffScreen(dialog);

        var editor = windows.CreateWindow(services.GetRequiredService<SavedViewEditorViewModel>());
        UiTestSupport.ShowOffScreen(editor);

        var chooser = windows.CreateWindow(new ColumnChooserViewModel(localizer, null, ColumnSets.Search, ColumnSets.Container));
        UiTestSupport.ShowOffScreen(chooser);

        // Logon Hours: a sample schedule (no directory involved), editable and read-only.
        var office = Core.ActiveDirectory.LogonHours.FromLocalGrid(
            Core.ActiveDirectory.LogonHoursGrid.Create((day, hour) => day is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && hour is >= 8 and < 16), 0);
        UiTestSupport.ShowOffScreen(windows.CreateWindow(new LogonHoursViewModel(localizer, office, TimeZoneInfo.Local, DateTimeOffset.UtcNow, "Jan Kowalski")));
        UiTestSupport.ShowOffScreen(windows.CreateWindow(new LogonHoursViewModel(localizer, null, TimeZoneInfo.Local, DateTimeOffset.UtcNow, null, localizer[L.Fields.ReadOnlyPermission])));

        // Watchdog: the certificate confirmation on the first connection and after a change (stage B design 18 item 1).
        RenderWatchdogCertificateWindows(windows, localizer);

        // ---- C2a: object dialogs in the disconnected state the factory produces without a DC (the property sheet and
        // Reset Password show "not connected"). The object and container pickers need a session: UI tests cover them.
        RenderObjectDialogs(windows, services.GetRequiredService<IDialogViewModelFactory>());
        RenderToolWindows(services, windows); // C2b

        await UiTestSupport.WaitForIdleAsync().ConfigureAwait(true);
        main.Shell.CommandPalette.CloseCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2).ConfigureAwait(true);
        UiTestSupport.CloseAllWindows();
        await UiTestSupport.WaitForIdleAsync(rounds: 2).ConfigureAwait(true);
    }

    /// <summary>
    /// "Potwierdź certyfikat usługi Watchdog" for a sample self-signed certificate: not trusted yet, and changed since it was
    /// pinned (old and new fingerprint). No service is contacted.
    /// </summary>
    private static void RenderWatchdogCertificateWindows(IWindowService windows, ILocalizer localizer)
    {
        var now = DateTimeOffset.UtcNow;
        var certificate = new WatchdogServerCertificate
        {
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("admng-wd.contoso.com"))),
            Subject = "CN=admng-wd.contoso.com",
            DnsNames = ["admng-wd.contoso.com", "admng-wd"],
            NotBefore = now.AddDays(-1),
            NotAfter = now.AddYears(2),
        };
        var previous = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("previous")));
        UiTestSupport.ShowOffScreen(windows.CreateWindow(new WatchdogCertificateTrustViewModel(localizer, certificate, null, "admng-wd.contoso.com:8443", now, TimeZoneInfo.Local)));
        UiTestSupport.ShowOffScreen(windows.CreateWindow(new WatchdogCertificateTrustViewModel(localizer, certificate, previous, "admng-wd.contoso.com:8443", now, TimeZoneInfo.Local)));
    }

    /// <summary>
    /// The Watchdog tab (Tools → Watchdog) of a fresh installation: no service address in the settings, so the tab shows
    /// the connection banner and the address editor without any network call; "Change address" is exercised too.
    /// </summary>
    private static async Task RenderWatchdogTabAsync(MainWindow main)
    {
        var shell = main.Shell;
        if (!shell.ShowWatchdogCommand.CanExecute(null))
        {
            throw new InvalidOperationException("Tools → Watchdog is not available.");
        }

        shell.ShowWatchdogCommand.Execute(null);
        var watchdog = shell.Documents.OfType<WatchdogDocumentViewModel>().SingleOrDefault()
            ?? throw new InvalidOperationException("Tools → Watchdog did not open the Watchdog tab.");
        await watchdog.Connecting.ConfigureAwait(true);
        await UiTestSupport.WaitForIdleAsync(rounds: 2).ConfigureAwait(true);
        if (watchdog.ConnectionState != WatchdogConnectionState.NotConfigured || !watchdog.ShowAddressEditor)
        {
            throw new InvalidOperationException($"The Watchdog tab should wait for an address, but is {watchdog.ConnectionState}.");
        }

        // An address that is not https:// is refused before anything is stored or sent.
        watchdog.ServiceUrlInput = "http://admng-wd.contoso.com:8443";
        await watchdog.ConnectCommand.ExecuteAsync(null).ConfigureAwait(true);
        if (watchdog.ServiceUrlError is null || watchdog.ConnectionState != WatchdogConnectionState.NotConfigured)
        {
            throw new InvalidOperationException("The Watchdog tab accepted an http:// address.");
        }

        watchdog.ServiceUrlInput = string.Empty;
        await UiTestSupport.WaitForIdleAsync(rounds: 2).ConfigureAwait(true);
    }

    /// <summary>C2a: property sheet, New Object (every kind), Reset Password (one and many accounts), Find and Change Domain Controller.</summary>
    private static void RenderObjectDialogs(IWindowService windows, IDialogViewModelFactory factory)
    {
        const string Parent = "OU=IT,OU=Pracownicy,OU=Firma,DC=contoso,DC=test";
        const string User = "CN=Jan Kowalski," + Parent;
        IWindowViewModel[] dialogs =
        [
            factory.CreatePropertySheet(User),
            factory.CreateNewObject(ObjectKind.User, Parent),
            factory.CreateNewObject(ObjectKind.Group, Parent),
            factory.CreateNewObject(ObjectKind.OrganizationalUnit, Parent),
            factory.CreateNewObject(ObjectKind.Computer, Parent),
            factory.CreateNewObject(ObjectKind.Contact, Parent),
            factory.CreateResetPassword([User]),
            factory.CreateResetPassword([User, "CN=Łukasz Wiśniewski," + Parent]),
            factory.CreateFind(null),
            factory.CreateChangeDomainController(),
        ];
        foreach (var dialog in dialogs)
        {
            UiTestSupport.ShowOffScreen(windows.CreateWindow(dialog));
        }
    }

    // ---- C2b: batch and tool windows ----------------------------------------------------------------------------

    /// <summary>
    /// Settings (both tabs), session log, PowerShell history, the AD Recycle Bin, the account review (both tabs), the saved
    /// filters and the filter builder, the message window, the
    /// CSV import with a parsed table and the batch editor with every operation editor. None of them needs a directory: without a session the batch
    /// editor and the Recycle Bin show "not connected", the account review cannot run.
    /// </summary>
    private static void RenderToolWindows(IServiceProvider services, IWindowService windows)
    {
        var factory = services.GetRequiredService<IDialogViewModelFactory>();
        var settings = (SettingsWindow)windows.CreateWindow(factory.CreateSettings());
        UiTestSupport.ShowOffScreen(settings);
        settings.SettingsTabs.SelectedItem = settings.MessagesTab;
        settings.UpdateLayout();
        UiTestSupport.ShowOffScreen(windows.CreateWindow(factory.CreateSessionLog()));
        UiTestSupport.ShowOffScreen(windows.CreateWindow(factory.CreatePowerShellHistory()));

        // AD Recycle Bin, disconnected: the banner and the error say "not connected", the commands are disabled.
        UiTestSupport.ShowOffScreen(windows.CreateWindow(factory.CreateRecycleBin()));

        // Account review (U2), disconnected: scope, parameters and checks; "Sprawdź" is disabled. Then the "Uprawnienia" tab
        // (U4) with its settings expanded: the built-in groups with their levels, marks and rules.
        var review = (AccountReviewWindow)windows.CreateWindow(factory.CreateAccountReview());
        UiTestSupport.ShowOffScreen(review);
        review.ViewModel.SelectedTabIndex = Presentation.AccountReview.AccountReviewViewModel.PrivilegesTab;
        review.PrivilegesSettingsExpander.IsExpanded = true;
        review.UpdateLayout();

        // Saved filters (U4) and the filter builder with a condition and a second group.
        UiTestSupport.ShowOffScreen(windows.CreateWindow(factory.CreateSavedFilters()));
        var dialogServices = services.GetRequiredService<DialogServices>();
        var editor = new Presentation.Filters.FilterEditorViewModel(
            dialogServices.Sessions, dialogServices.Dialogs, dialogServices.Localizer, dialogServices.Errors, dialogServices.Time);
        editor.Groups[0].Conditions[0].Attribute = Core.ActiveDirectory.Attr.Department;
        editor.Groups[0].Conditions[0].Value = "Księgowość";
        editor.AddGroupCommand.Execute(null);
        UiTestSupport.ShowOffScreen(windows.CreateWindow(editor));

        // Messages to users (U1), disconnected: the editor with a built-in template; "Przygotuj podgląd" is disabled.
        UiTestSupport.ShowOffScreen(windows.CreateWindow(factory.CreateUserMessageComposer(
            new UserMessageRequest(["CN=Jan Kowalski,OU=IT,DC=contoso,DC=test"], Core.Messaging.BuiltInMessageTemplates.PasswordExpiring))));

        var import = factory.CreateBatchEditor(new BatchEditorRequest([]));
        import.CsvImport!.Load(new CsvTable(
            ["sAMAccountName", "department", "title"],
            [["jan.kowalski", "IT", "Kierownik zespołu"], ["zaneta.szymanska", "Sprzedaż", "Handlowiec"]],
            ';'));
        import.CsvImport.Mappings[1].Attribute = "department";
        UiTestSupport.ShowOffScreen(windows.CreateWindow(import));

        var batch = factory.CreateBatchEditor(new BatchEditorRequest(["CN=Jan Kowalski,OU=IT,DC=contoso,DC=test"]));
        foreach (var type in Enum.GetValues<OperationType>())
        {
            batch.AddOperationCommand.Execute(type);
        }

        UiTestSupport.ShowOffScreen(windows.CreateWindow(batch));
    }

    private static void Report(IReadOnlyList<string> failures)
    {
        var report = new StringBuilder();
        if (failures.Count == 0)
        {
            report.AppendLine("SMOKE TEST PASSED");
            Console.Out.Write(report.ToString());
            return;
        }

        report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"SMOKE TEST FAILED ({failures.Count} problems):"));
        foreach (var failure in failures)
        {
            report.AppendLine(failure);
        }

        Console.Error.Write(report.ToString());
        System.Diagnostics.Debug.Write(report.ToString());
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temporary folder left behind is harmless.
        }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int standardHandle);
}
