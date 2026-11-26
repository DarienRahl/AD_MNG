using System.IO;
using System.Windows;
using System.Windows.Input;
using AdMng.App.PowerShell;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.Core.PowerShell;
using AdMng.Presentation.PowerShell;
using AdMng.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests.PowerShell;

/// <summary>What the tests of the PowerShell window (track R, stage R1) share: opening it over the fake directory, running a command, reading the scrollback.</summary>
internal static class PowerShellUiSupport
{
    /// <summary>
    /// The PowerShell window of a connection to the sample company, with the real engine (a runspace of the test process) and the real
    /// window services; the questions of the program are answered by <see cref="TestDialogService"/> and the files by <see cref="ScriptedFiles"/>.
    /// </summary>
    public static async Task<(UiTestContext Context, TestDialogService Dialogs, ScriptedFiles Files, PowerShellWindow Window)> OpenAsync(
        Action<IServiceCollection>? configure = null, string language = "en")
    {
        var files = new ScriptedFiles();
        var (context, dialogs) = await TestDialogService.CreateContextAsync(language, services =>
        {
            services.Replace(ServiceDescriptor.Scoped<IFileDialogService>(_ => files));
            configure?.Invoke(services);
        });
        await context.ConnectAsync();
        dialogs.RecordPropertiesOnly = true;
        await dialogs.ShowPowerShellAsync();
        await UiTestSupport.WaitForIdleAsync();
        var window = Assert.Single(Application.Current.Windows.OfType<PowerShellWindow>());
        await UiWait.InitializedAsync(window.Initialization);
        await UiWait.UntilAsync(() => window.ViewModel.IsReady, "the engine of the window is ready");
        return (context, dialogs, files, window);
    }

    /// <summary>Types a command into the editor of the window and runs it; completes when it ended.</summary>
    public static async Task RunAsync(PowerShellWindow window, string script)
    {
        window.EditorControl.Box.Text = script;
        await window.ViewModel.RunCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
    }

    /// <summary>The text of the scrollback, one line each.</summary>
    public static string[] Lines(PowerShellWindow window) => [.. window.ViewModel.Output.Lines.Select(line => line.Text)];

    /// <summary>The lines of one kind (errors, warnings...).</summary>
    public static string[] Lines(PowerShellWindow window, PowerShellLineKind kind) =>
        [.. window.ViewModel.Output.Lines.Where(line => line.Kind == kind).Select(line => line.Text)];

    /// <summary>A key press on the box of the editor (the window's own handler sees it first, as it does for the operator).</summary>
    public static void Press(PowerShellWindow window, Key key)
    {
        var box = window.EditorControl.Box;
        box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }

    /// <summary>A file name in the temporary folder that no other test uses.</summary>
    public static string TempFile(string name) => Path.Combine(Path.GetTempPath(), "admng-ps-" + Guid.NewGuid().ToString("N") + "-" + name);
}

/// <summary>The file dialogs of the tests: <c>Save As</c> answers <see cref="NextSavePath"/> (null: the operator cancelled), nothing opens a window.</summary>
internal sealed class ScriptedFiles : IFileDialogService
{
    public string? NextSavePath { get; set; }

    public List<string> SaveAsAsked { get; } = [];

    public Task<string?> OpenFileAsync(string title, IReadOnlyList<FileFilter> filters) => Task.FromResult<string?>(null);

    public Task<string?> SaveFileAsync(string title, string defaultFileName, IReadOnlyList<FileFilter> filters)
    {
        SaveAsAsked.Add(defaultFileName);
        return Task.FromResult(NextSavePath);
    }
}

/// <summary>The reads of the policies of a workstation, as a test says them (the real source reads the registry of the machine the tests run on).</summary>
internal sealed class ScriptedPolicySource : IPowerShellPolicySource
{
    private readonly Dictionary<(PowerShellPolicyScope Scope, string Key, string Name), object> _values = [];

    public PowerShellLockdownState Lockdown { get; set; }

    /// <summary>Transcription switched on by the computer policy, with the directory of the transcripts.</summary>
    public ScriptedPolicySource WithTranscription(string directory)
    {
        var key = PowerShellPolicyKeys.Sub(PowerShellPolicyKeys.CoreRoot, PowerShellPolicyKeys.TranscriptionKey);
        _values[(PowerShellPolicyScope.Computer, key, PowerShellPolicyKeys.EnableTranscripting)] = 1;
        _values[(PowerShellPolicyScope.Computer, key, PowerShellPolicyKeys.OutputDirectory)] = directory;
        return this;
    }

    public object? GetValue(PowerShellPolicyScope scope, string subKey, string valueName) =>
        _values.TryGetValue((scope, subKey, valueName), out var value) ? value : null;

    public IReadOnlyList<string>? GetValueNames(PowerShellPolicyScope scope, string subKey) => null;

    public string? ReadConfigFile(PowerShellPolicyScope scope) => null;

    public PowerShellLockdownState GetLockdownState() => Lockdown;
}
