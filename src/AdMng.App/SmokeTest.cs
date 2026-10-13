using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.Views;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
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
    private static readonly TimeSpan FailSafe = TimeSpan.FromSeconds(30);

    public static void Start(App app, StartupOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);

        // Best effort: show the report in the console the test was started from (AdMng.exe is a GUI application).
        _ = AttachConsole(AttachParentProcess);
        var failSafe = new Timer(
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
        await failSafe.DisposeAsync().ConfigureAwait(true);
        if (host is not null)
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

        TryDelete(settingsDirectory);
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

        await UiTestSupport.WaitForIdleAsync().ConfigureAwait(true);
        main.Shell.CommandPalette.CloseCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2).ConfigureAwait(true);
        UiTestSupport.CloseAllWindows();
        await UiTestSupport.WaitForIdleAsync(rounds: 2).ConfigureAwait(true);
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
}
