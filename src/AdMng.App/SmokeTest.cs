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
using AdMng.Presentation.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AdMng.App;

/// <summary>
/// <c>AdMng.exe --smoke-test</c> (CI on Windows): builds the real host with every service, renders the connect window,
/// the main window (disconnected) and the dialogs of this release off-screen in English and Polish, and fails on any
/// exception or WPF binding error. Exit codes: 0 = passed, 1 = failures (printed to stderr), 2 = did not finish within
/// 30 seconds. Settings go to a temporary folder that is deleted afterwards; nothing touches a directory.
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
        main.Shell.CommandPalette.OpenCommand.Execute(null);

        var dialog = windows.CreateWindow(MessageDialogViewModel.TypedConfirmation(
            localizer[L.Commands.Delete], localizer.Format(L.Delete.ConfirmMany, 3), "3", localizer));
        UiTestSupport.ShowOffScreen(dialog);

        var editor = windows.CreateWindow(services.GetRequiredService<SavedViewEditorViewModel>());
        UiTestSupport.ShowOffScreen(editor);

        var chooser = windows.CreateWindow(new ColumnChooserViewModel(localizer, null, ColumnSets.Search, ColumnSets.Container));
        UiTestSupport.ShowOffScreen(chooser);

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

    /// <summary>C2a: property sheet, New Object (every kind), Reset Password (one and many accounts) and Find.</summary>
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
        ];
        foreach (var dialog in dialogs)
        {
            UiTestSupport.ShowOffScreen(windows.CreateWindow(dialog));
        }
    }

    // ---- C2b: batch and tool windows ----------------------------------------------------------------------------

    /// <summary>
    /// Settings, session log, PowerShell history, the AD Recycle Bin, the CSV import with a parsed table and the batch
    /// editor with every operation editor. None of them needs a directory: without a session the batch editor and the
    /// Recycle Bin show "not connected".
    /// </summary>
    private static void RenderToolWindows(IServiceProvider services, IWindowService windows)
    {
        var factory = services.GetRequiredService<IDialogViewModelFactory>();
        UiTestSupport.ShowOffScreen(windows.CreateWindow(factory.CreateSettings()));
        UiTestSupport.ShowOffScreen(windows.CreateWindow(factory.CreateSessionLog()));
        UiTestSupport.ShowOffScreen(windows.CreateWindow(factory.CreatePowerShellHistory()));

        // AD Recycle Bin, disconnected: the banner and the error say "not connected", the commands are disabled.
        UiTestSupport.ShowOffScreen(windows.CreateWindow(factory.CreateRecycleBin()));

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
