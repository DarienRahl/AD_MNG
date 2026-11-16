using System.Globalization;
using AdMng.Presentation.PowerShell;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.GroupPolicy;

namespace AdMng.Presentation.Tests.PowerShell;

/// <summary>
/// A PowerShell window over the real presentation services and the fake directory (<see cref="B2Harness"/>), with the engine
/// replaced by <see cref="FakePowerShellSession"/>, the policies by <see cref="FakePowerShellPolicyReader"/> and the window actions
/// by <see cref="FakePowerShellWindowActions"/>.
/// </summary>
internal sealed class PowerShellHarness
{
    private PowerShellHarness(B2Harness directory, FakePowerShellSessionFactory factory, FakePowerShellPolicyReader policies, FakePowerShellWindowActions actions)
    {
        Directory = directory;
        Factory = factory;
        Policies = policies;
        Actions = actions;
        Services = new PowerShellServices(factory, policies, actions);
    }

    public B2Harness Directory { get; }

    public FakePowerShellSessionFactory Factory { get; }

    public FakePowerShellSession Session => Factory.Session;

    public FakePowerShellPolicyReader Policies { get; }

    public FakePowerShellWindowActions Actions { get; }

    public PowerShellServices Services { get; }

    public static async Task<PowerShellHarness> CreateAsync(CultureInfo? culture = null, PowerShellPolicies? policies = null)
    {
        var directory = await B2Harness.CreateAsync(culture);
        return new PowerShellHarness(directory, new FakePowerShellSessionFactory(), new FakePowerShellPolicyReader(policies), new FakePowerShellWindowActions());
    }

    /// <summary>The window, not yet opened.</summary>
    public PowerShellWindowViewModel Create(PowerShellServices? services = null) => new(Directory.Services, services ?? Services);

    /// <summary>The window, opened: the engine started and the policies read.</summary>
    public async Task<PowerShellWindowViewModel> OpenAsync(PowerShellServices? services = null)
    {
        var window = Create(services);
        await window.InitializeAsync();
        return window;
    }

    /// <summary>Types <paramref name="script"/> and runs it; completes when the command ended.</summary>
    public static async Task RunAsync(PowerShellWindowViewModel window, string script)
    {
        Type(window, script);
        await window.RunCommand.ExecuteAsync(null);
    }

    /// <summary>Types <paramref name="text"/> into the editor.</summary>
    public static void Type(PowerShellWindowViewModel window, string text) => window.Input.SetText(text, text.Length);

    public StatusRecorder Record(PowerShellWindowViewModel window) => new(window.Status);
}
