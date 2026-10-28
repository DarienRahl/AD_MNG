using AdMng.Core.Correspondence.Outlook;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Tests.Connections;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// Where Korespondencja starts (design note 6.3): the three shell commands (only with a launcher), the request each one
/// sends, and the window built from the connection window's scope (6.2).
/// </summary>
public sealed class CorrespondenceShellCommandsTests
{
    private static readonly string[] CommandIds = ["tools.correspondence", "object.correspondence", "object.mailboxOwners"];

    [Fact]
    public async Task WithoutALauncher_TheShellHasNoCorrespondenceCommands()
    {
        var (host, connection, _) = await OpenAsync(withLauncher: false);
        await using var disposeHost = host;
        var shell = connection.Get<ShellViewModel>();
        await shell.InitializeAsync();

        Assert.False(shell.CanUseCorrespondence);
        Assert.DoesNotContain(shell.CommandDescriptors, c => CommandIds.Contains(c.Id));
        Assert.False(shell.ShowCorrespondenceCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheCommands_SendTheirRequests_ForTheObjectsTheyAccept()
    {
        var (host, connection, company) = await OpenAsync(withLauncher: true);
        await using var disposeHost = host;
        var shell = connection.Get<ShellViewModel>();
        var launcher = connection.Get<RecordingCorrespondenceLauncher>();
        await shell.InitializeAsync();
        Assert.True(shell.CanUseCorrespondence);
        Assert.All(CommandIds, id => Assert.Contains(shell.CommandDescriptors, c => c.Id == id));

        // "Narzędzia → Korespondencja (Outlook)…": an empty window.
        await shell.ShowCorrespondenceCommand.ExecuteAsync(null);
        var empty = Assert.Single(launcher.Requests);
        Assert.Empty(empty.DistinguishedNames);
        Assert.Equal(CorrespondenceStart.Recipients, empty.Start);

        // Nothing selected: the object commands wait for a selection.
        await shell.NavigateAsync(ListLocation.ForContainer(company.ItOu));
        Assert.False(shell.SendCorrespondenceCommand.CanExecute(null));
        Assert.False(shell.AskMailboxOwnersCommand.CanExecute(null));

        Select(shell, "Łukasz Wiśniewski");
        await shell.SendCorrespondenceCommand.ExecuteAsync(null);
        await shell.AskMailboxOwnersCommand.ExecuteAsync(null);
        Assert.Equal([company.LukaszWisniewski], launcher.Requests[1].DistinguishedNames);
        Assert.Equal(CorrespondenceStart.Recipients, launcher.Requests[1].Start);
        Assert.Equal([company.LukaszWisniewski], launcher.Requests[2].DistinguishedNames);
        Assert.Equal(CorrespondenceStart.MailboxOwners, launcher.Requests[2].Start);

        // A group: its members become recipients; it is no mailbox scope.
        await shell.NavigateAsync(ListLocation.ForContainer(company.GroupsOu));
        Select(shell, "GG_IT");
        Assert.True(shell.SendCorrespondenceCommand.CanExecute(null));
        Assert.False(shell.AskMailboxOwnersCommand.CanExecute(null));
        await shell.SendCorrespondenceCommand.ExecuteAsync(null);
        Assert.Equal([company.GroupIt], launcher.Requests[3].DistinguishedNames);
        Assert.Equal(4, launcher.Requests.Count);
    }

    [Fact]
    public async Task TheCommands_NeedAConnection()
    {
        await using var host = Host(withLauncher: true);
        var connection = host.CreateConnection();
        var shell = connection.Get<ShellViewModel>();

        Assert.True(shell.CanUseCorrespondence);
        Assert.False(shell.ShowCorrespondenceCommand.CanExecute(null));
        Assert.False(shell.SendCorrespondenceCommand.CanExecute(null));
        Assert.False(shell.AskMailboxOwnersCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheFactory_OfTheConnectionWindow_BuildsAWorkingWindow()
    {
        var (host, connection, company) = await OpenAsync(withLauncher: true);
        await using var disposeHost = host;
        var factory = connection.Get<CorrespondenceViewModelFactory>();
        Assert.Same(factory, connection.Get<CorrespondenceViewModelFactory>());
        Assert.Same(connection.Get<CorrespondenceServices>(), factory.Correspondence);
        Assert.Same(host.Provider.GetRequiredService<CorrespondenceJournal>(), factory.Correspondence.Journal);
        Assert.Same(host.Provider.GetRequiredService<IOutlookClient>(), factory.Correspondence.Outlook);

        using var window = factory.Create(new CorrespondenceRequest([company.JanKowalski]));
        await window.InitializeAsync();
        await (window.Outlook.Connecting ?? Task.FromResult<IOutlookSession?>(null));

        Assert.Equal("Correspondence – contoso.test", window.WindowTitle);
        Assert.Null(window.Error);
        Assert.True(factory.Correspondence.Journal.IsLoaded);
        Assert.Equal(OutlookState.Connected, window.Outlook.State);
    }

    [Fact]
    public async Task AHostWithoutAKeyProtector_ShowsThatTheJournalIsUnavailable()
    {
        await using var host = new ConnectionScopeHost(services => services.AddSingleton<IOutlookClient>(new FakeOutlookClient()));
        var connection = host.CreateConnection();
        await connection.ConnectAsync();

        using var window = connection.Get<CorrespondenceViewModelFactory>().Create(new CorrespondenceRequest([]));
        await window.InitializeAsync();

        Assert.Equal(
            "The \"already sent\" journal cannot be used (its key cannot be protected on this computer) – addresses sent to are not skipped by later runs.",
            window.Error);
    }

    private static ConnectionScopeHost Host(bool withLauncher, FakeDirectorySession? directory = null) =>
        new(services =>
        {
            services.AddSingleton<ICorrespondenceKeyProtector, CorrespondenceShellScenario.XorKeyProtector>();
            services.AddSingleton<IOutlookClient>(new FakeOutlookClient());
            if (withLauncher)
            {
                services.AddScoped<RecordingCorrespondenceLauncher>();
                services.AddScoped<ICorrespondenceLauncher>(sp => sp.GetRequiredService<RecordingCorrespondenceLauncher>());
            }
        }, directory is null ? null : [directory]);

    private static async Task<(ConnectionScopeHost Host, TestConnection Connection, SampleCompany Company)> OpenAsync(bool withLauncher)
    {
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        var host = Host(withLauncher, builder.Session);
        var connection = host.CreateConnection();
        await connection.ConnectAsync();
        return (host, connection, company);
    }

    private static void Select(ShellViewModel shell, string name)
    {
        var document = shell.SelectedDocument!;
        document.SelectedItems = null;
        document.SelectedItem = document.Items.Single(r => r.Name == name);
        shell.RefreshCommandStates();
    }

    /// <summary>Records the requests of the shell (the App opens the window).</summary>
    private sealed class RecordingCorrespondenceLauncher : ICorrespondenceLauncher
    {
        public List<CorrespondenceRequest> Requests { get; } = [];

        public Task ShowCorrespondenceAsync(CorrespondenceRequest request)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }
}
