using System.Globalization;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// The Outlook connection of the Korespondencja window (design note 6.4): states and their texts, the accounts and the
/// account of the settings, one connection at a time, a new one after the session was lost, failures as code texts and
/// "Sprawdź Outlooka".
/// </summary>
public sealed class OutlookConnectionViewModelTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    private static OutlookConnectionViewModel Connection(CorrespondenceShellScenario scenario) =>
        new(scenario.Harness.Services, scenario.Services);

    [Fact]
    public async Task Connect_ShowsTheAccounts_InPolish()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        await using var outlook = Connection(scenario);
        Assert.Equal(OutlookState.Unknown, outlook.State);
        Assert.Equal("Outlook: nie połączono", outlook.StateText);
        Assert.True(outlook.IsAvailable);
        Assert.False(outlook.IsConnected);

        var session = await outlook.EnsureConnectedAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        Assert.Equal(OutlookState.Connected, outlook.State);
        Assert.Equal("Outlook: połączono (2 konta)", outlook.StateText);
        Assert.True(outlook.IsConnected);
        Assert.Equal(["jan.kowalski@contoso.test", "sekretariat.lodz@contoso.test"], outlook.Accounts.Select(a => a.SmtpAddress));
        Assert.Null(outlook.SelectedAccount);                    // the profile's default account
        Assert.Null(outlook.AccountAddress);
        Assert.Equal("jan.kowalski@contoso.test", outlook.OwnAddress);
        Assert.Same(session, outlook.Session);
        Assert.Equal(FakeOutlookClient.SampleProfile, outlook.Profile);
        Assert.Null(outlook.Failure);
        Assert.Equal(nameof(OutlookConnectionViewModel), outlook.ToString());
    }

    [Theory]
    [InlineData(1, "Outlook: połączono")]
    [InlineData(3, "Outlook: połączono (3 konta)")]
    [InlineData(5, "Outlook: połączono (5 kont)")]
    [InlineData(12, "Outlook: połączono (12 kont)")]
    [InlineData(22, "Outlook: połączono (22 konta)")]
    public async Task ConnectedText_UsesThePolishPlural(int accounts, string expected)
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        scenario.Outlook.Profile = FakeOutlookClient.SampleProfile with
        {
            Accounts = [.. Enumerable.Range(1, accounts).Select(i => new OutlookAccount($"Konto {i}", $"konto{i}@contoso.test"))],
        };
        await using var outlook = Connection(scenario);

        await outlook.EnsureConnectedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, outlook.StateText);
    }

    [Fact]
    public async Task TheAccountOfTheSettings_IsChosenAfterConnect_AndKeptWhenTheProfileLacksIt()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync();
        await using var outlook = Connection(scenario);
        outlook.LoadFrom(new CorrespondenceSettings { AccountAddress = "Sekretariat.Lodz@contoso.test", OnBehalfOf = " biuro@contoso.test " });
        Assert.Equal("biuro@contoso.test", outlook.OnBehalfOf);

        await outlook.EnsureConnectedAsync(TestContext.Current.CancellationToken);

        Assert.Equal("sekretariat.lodz@contoso.test", outlook.SelectedAccount!.SmtpAddress);
        Assert.Equal("sekretariat.lodz@contoso.test", outlook.OwnAddress);
        outlook.SelectedAccount = outlook.Accounts[0];
        var saved = new CorrespondenceSettings();
        outlook.SaveTo(saved);
        Assert.Equal(("jan.kowalski@contoso.test", "biuro@contoso.test"), (saved.AccountAddress, saved.OnBehalfOf));

        // A profile without the account of the settings: the default account is used, the setting is not lost.
        await using var other = Connection(scenario);
        other.LoadFrom(new CorrespondenceSettings { AccountAddress = "kadry@contoso.test" });
        await other.EnsureConnectedAsync(TestContext.Current.CancellationToken);
        Assert.Null(other.SelectedAccount);
        var kept = new CorrespondenceSettings();
        other.SaveTo(kept);
        Assert.Equal("kadry@contoso.test", kept.AccountAddress);
    }

    [Fact]
    public async Task OneConnectionAtATime_AndANewOneAfterTheSessionWasLost()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync();
        await using var outlook = Connection(scenario);

        var ct = TestContext.Current.CancellationToken;
        var sessions = await Task.WhenAll(outlook.EnsureConnectedAsync(ct), outlook.EnsureConnectedAsync(ct), outlook.EnsureConnectedAsync(ct));
        Assert.Equal(1, scenario.Outlook.Connections);
        Assert.All(sessions, s => Assert.Same(sessions[0], s));
        Assert.Same(sessions[0], await outlook.EnsureConnectedAsync(ct));
        Assert.Equal(1, scenario.Outlook.Connections);

        var first = Assert.Single(scenario.Outlook.Sessions);
        first.IsConnected = false;                                  // Outlook was closed
        var second = await outlook.EnsureConnectedAsync(TestContext.Current.CancellationToken);

        Assert.NotSame(first, second);
        Assert.True(first.IsDisposed);
        Assert.Equal(2, scenario.Outlook.Connections);
        Assert.Equal(OutlookState.Connected, outlook.State);
    }

    [Fact]
    public async Task ReportedLoss_ShowsInTheState_AndTheNextUseConnectsAgain()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        await using var outlook = Connection(scenario);
        await outlook.EnsureConnectedAsync(TestContext.Current.CancellationToken);

        outlook.ReportFailure(FakeOutlookClient.Failure(OutlookFailureKind.RecipientUnresolved, "recipients"));
        Assert.Equal(OutlookState.Connected, outlook.State);        // one item's problem

        var lost = FakeOutlookClient.Failure(OutlookFailureKind.Disconnected, "send");
        scenario.Outlook.Sessions[0].IsConnected = false;
        outlook.ReportFailure(lost);

        Assert.Equal(OutlookState.Failed, outlook.State);
        Assert.Equal("Outlook: brak połączenia – Połączenie z Outlookiem zostało przerwane (Outlook zamknięty?) – uruchom Outlooka i połącz ponownie.", outlook.StateText);
        Assert.False(outlook.IsConnected);
        Assert.True(outlook.ConnectCommand.CanExecute(null));

        await outlook.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(OutlookState.Connected, outlook.State);
        Assert.Equal(2, scenario.Outlook.Connections);
    }

    [Theory]
    [InlineData(OutlookFailureKind.Elevated, OutlookState.Failed,
        "Outlook: brak połączenia – AD Manager działa jako administrator, a Outlook nie – Windows nie pozwala im się połączyć. Uruchom AD Manager bez „Uruchom jako administrator” albo najpierw zamknij Outlooka.")]
    [InlineData(OutlookFailureKind.NotResponding, OutlookState.Failed,
        "Outlook: brak połączenia – Outlook nie odpowiada – sprawdź, czy nie czeka na hasło albo wybór profilu.")]
    [InlineData(OutlookFailureKind.NotInstalled, OutlookState.NotInstalled, "Outlook: brak klasycznego Outlooka")]
    [InlineData(OutlookFailureKind.NotSupported, OutlookState.NotSupported, "Outlook: niedostępny (tylko Windows)")]
    public async Task ConnectFailure_ShowsTheCodeText_WithoutAModalError(OutlookFailureKind kind, OutlookState state, string text)
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        scenario.Outlook.ConnectFailure = FakeOutlookClient.Failure(kind, "connect");
        await using var outlook = Connection(scenario);

        Assert.Null(await outlook.EnsureConnectedAsync(TestContext.Current.CancellationToken));

        Assert.Equal(state, outlook.State);
        Assert.Equal(text, outlook.StateText);
        Assert.Equal(kind, outlook.Failure!.Kind);
        Assert.NotNull(outlook.FailureText);
        Assert.Empty(scenario.Harness.Dialogs.Errors);
        Assert.Empty(scenario.Harness.Dialogs.Shown);
    }

    [Fact]
    public async Task UnknownFailure_ShowsItsHResult()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        scenario.Outlook.ConnectFailure = FakeOutlookClient.Failure(OutlookFailureKind.Unknown, "connect", unchecked((int)0x80004005));
        await using var outlook = Connection(scenario);

        await outlook.EnsureConnectedAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Nieznany błąd Outlooka. (kod 0x80004005)", outlook.FailureText);
        Assert.Equal("Outlook: brak połączenia – Nieznany błąd Outlooka. (kod 0x80004005)", outlook.StateText);
    }

    [Fact]
    public async Task AClientThatThrows_IsAnUnknownFailure_WithoutItsMessage()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync();
        var services = new CorrespondenceServices(scenario.Templates, scenario.Journal, new Core.Csv.CsvCodec(), scenario.Harness.AuditLog,
            scenario.Harness.Time, new ThrowingClient());
        await using var outlook = new OutlookConnectionViewModel(scenario.Harness.Services, services);

        Assert.Null(await outlook.EnsureConnectedAsync(TestContext.Current.CancellationToken));

        Assert.Equal(OutlookState.Failed, outlook.State);
        Assert.Equal(OutlookFailureCodes.Unknown, outlook.Failure!.Code);
        Assert.DoesNotContain("jan.kowalski", outlook.StateText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoOutlookClient_IsNotSupported_AndNothingIsTried()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(withOutlook: false);
        await using var outlook = Connection(scenario);

        Assert.Equal(OutlookState.NotSupported, outlook.State);
        Assert.Equal("Outlook: not available here (Windows only)", outlook.StateText);
        Assert.False(outlook.IsAvailable);
        Assert.False(outlook.ConnectCommand.CanExecute(null));
        outlook.ConnectInBackground();
        Assert.Null(outlook.Connecting);
        Assert.Null(await outlook.EnsureConnectedAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, scenario.Outlook.Connections);
    }

    [Fact]
    public async Task NotInstalled_IsKnownBeforeConnecting()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync();
        scenario.Outlook.Availability = OutlookAvailability.NotInstalled;
        await using var outlook = Connection(scenario);

        Assert.Equal(OutlookState.NotInstalled, outlook.State);
        Assert.Equal("Outlook: classic Outlook is not installed", outlook.StateText);
        Assert.Null(await outlook.EnsureConnectedAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, scenario.Outlook.Connections);
    }

    [Fact]
    public async Task Check_ShowsTheDiagnosticsWithTheFolders()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        await scenario.Journal.LoadAsync(TimeSpan.FromDays(365), TestContext.Current.CancellationToken);
        await scenario.Journal.AppendAsync("pawel.lewandowski@contoso.test", "Temat", TestContext.Current.CancellationToken);
        await using var outlook = Connection(scenario);

        await outlook.CheckCommand.ExecuteAsync(null);

        var (title, text) = Assert.Single(scenario.Dialogs.Reports);
        Assert.Equal("Sprawdzenie środowiska", title);
        Assert.Contains("Outlook: wersja 16.0.0.17928, profil: Outlook", text, StringComparison.Ordinal);
        Assert.Contains("Konta (2):", text, StringComparison.Ordinal);
        Assert.Contains("   • Sekretariat – Łódź <sekretariat.lodz@contoso.test>", text, StringComparison.Ordinal);
        Assert.Contains("Bieżąca skrzynka: jan.kowalski@contoso.test", text, StringComparison.Ordinal);
        Assert.Contains("Podpisy Outlooka (1):", text, StringComparison.Ordinal);
        Assert.Contains("   • Jan Kowalski – podpis", text, StringComparison.Ordinal);
        Assert.Contains("Test utworzenia wiadomości (bez wysyłania): OK", text, StringComparison.Ordinal);
        Assert.Contains("Dziennik „już wysłane”:", text, StringComparison.Ordinal);
        Assert.Contains($"   {scenario.Journal.Folder} (wpisy: 1)", text, StringComparison.Ordinal);
        Assert.Contains("Folder Pobrane:" + Environment.NewLine + "   " + scenario.Platform.DownloadsFolder, text, StringComparison.Ordinal);
        Assert.Contains("Folder podpisów Outlooka:" + Environment.NewLine + "   " + scenario.Signatures.Folder, text, StringComparison.Ordinal);
        Assert.Contains($"Komputer:   {Environment.MachineName}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("pawel.lewandowski", text, StringComparison.Ordinal);   // the journal never shows addresses
        Assert.Equal(OutlookState.Connected, outlook.State);
        Assert.False(outlook.IsChecking);
    }

    [Fact]
    public async Task Check_WithoutOutlook_ReportsWhy_AndTheLocalFolders()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        scenario.Outlook.ConnectFailure = FakeOutlookClient.Failure(OutlookFailureKind.Busy, "connect");
        scenario.Platform.DownloadsFolder = null;
        await using var outlook = Connection(scenario);

        await outlook.CheckCommand.ExecuteAsync(null);

        var text = Assert.Single(scenario.Dialogs.Reports).Text;
        Assert.Contains("Outlook: BRAK POŁĄCZENIA – Outlook jest zajęty (otwarte okno dialogowe?) – zamknij okna Outlooka i spróbuj ponownie.", text, StringComparison.Ordinal);
        Assert.Contains("Podpisy Outlooka (2):", text, StringComparison.Ordinal);              // from the signature folder itself
        Assert.Contains("   • Sekretariat", text, StringComparison.Ordinal);
        Assert.Contains("Folder Pobrane:" + Environment.NewLine + "   (nie znaleziono)", text, StringComparison.Ordinal);
        Assert.Contains("(nie wczytano)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Test utworzenia", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_ShowsAFailedTestItem()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        var profile = FakeOutlookClient.SampleProfile;
        scenario.Outlook.DiagnoseResult = new OutlookDiagnostics(profile.Version, profile.ProfileName, profile.Accounts, profile.CurrentUserAddress,
            [], false, false, FakeOutlookClient.Failure(OutlookFailureKind.SecurityDenied, "testItem"));
        scenario.Signatures.Names = [];
        await using var outlook = Connection(scenario);

        await outlook.CheckCommand.ExecuteAsync(null);

        var text = Assert.Single(scenario.Dialogs.Reports).Text;
        Assert.Contains("Test utworzenia wiadomości (bez wysyłania): BŁĄD – Outlook nie zgodził się na wysłanie wiadomości przez program", text, StringComparison.Ordinal);
        Assert.Contains("(proces 32-bitowy)", text, StringComparison.Ordinal);
        Assert.Contains($"Podpisy Outlooka (0):{Environment.NewLine}   (brak plików w {scenario.Signatures.Folder})", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_WithoutTheWindowDialogs_UsesTheInfoDialog()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync();
        var services = new CorrespondenceServices(scenario.Templates, scenario.Journal, new Core.Csv.CsvCodec(), scenario.Harness.AuditLog,
            scenario.Harness.Time, scenario.Outlook);
        await using var outlook = new OutlookConnectionViewModel(scenario.Harness.Services, services);

        await outlook.CheckCommand.ExecuteAsync(null);

        Assert.Contains(scenario.Harness.Dialogs.Shown, s => s.StartsWith("info:User:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dispose_ReleasesTheSession_AndRefusesNewConnections()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync();
        var outlook = Connection(scenario);
        await outlook.EnsureConnectedAsync(TestContext.Current.CancellationToken);

        await outlook.DisposeAsync();
        await outlook.DisposeAsync();

        Assert.True(Assert.Single(scenario.Outlook.Sessions).IsDisposed);
        Assert.Null(outlook.Session);
        Assert.Null(await outlook.EnsureConnectedAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, scenario.Outlook.Connections);
    }

    [Fact]
    public async Task LanguageChange_RefreshesTheTexts()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync();
        scenario.Outlook.ConnectFailure = FakeOutlookClient.Failure(OutlookFailureKind.Disconnected, "connect");
        await using var outlook = Connection(scenario);
        await outlook.EnsureConnectedAsync(TestContext.Current.CancellationToken);
        Assert.StartsWith("Outlook: not connected – The connection to Outlook was lost", outlook.StateText, StringComparison.Ordinal);

        scenario.Harness.Localizer.SetCulture(Polish);
        outlook.RefreshTexts();

        Assert.StartsWith("Outlook: brak połączenia – Połączenie z Outlookiem zostało przerwane", outlook.StateText, StringComparison.Ordinal);
        Assert.StartsWith("Połączenie z Outlookiem", outlook.FailureText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_WhenOutlookCannotAnswer_SaysSo_AndAFailureThatLosesOutlookShowsInTheState()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        var client = new GatedClient(diagnoseFailure: FakeOutlookClient.Failure(OutlookFailureKind.Disconnected, "diagnose"));
        client.Gate.SetResult();
        var services = new CorrespondenceServices(scenario.Templates, scenario.Journal, new Core.Csv.CsvCodec(), scenario.Harness.AuditLog,
            scenario.Harness.Time, client, scenario.Signatures, dialogs: scenario.Dialogs, platform: scenario.Platform);
        await using var outlook = new OutlookConnectionViewModel(scenario.Harness.Services, services);

        await outlook.CheckCommand.ExecuteAsync(null);

        var text = Assert.Single(scenario.Dialogs.Reports).Text;
        Assert.Contains("Nie udało się sprawdzić Outlooka: Połączenie z Outlookiem zostało przerwane", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BRAK POŁĄCZENIA", text, StringComparison.Ordinal);
        Assert.Contains("Folder podpisów Outlooka:", text, StringComparison.Ordinal);
        await B2Harness.WaitUntilAsync(() => outlook.State == OutlookState.Failed);
        Assert.False(outlook.IsChecking);
    }

    [Fact]
    public async Task WhileConnecting_TheStateSaysSo_AndEveryCallerGetsTheSameSession()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        var client = new GatedClient();
        var services = new CorrespondenceServices(scenario.Templates, scenario.Journal, new Core.Csv.CsvCodec(), scenario.Harness.AuditLog,
            scenario.Harness.Time, client);
        await using var outlook = new OutlookConnectionViewModel(scenario.Harness.Services, services);
        var ct = TestContext.Current.CancellationToken;

        var first = outlook.EnsureConnectedAsync(ct);
        var second = outlook.EnsureConnectedAsync(ct);

        Assert.Equal(OutlookState.Connecting, outlook.State);
        Assert.Equal("Outlook: łączę…", outlook.StateText);
        Assert.False(outlook.ConnectCommand.CanExecute(null));
        Assert.False(outlook.CheckCommand.CanExecute(null));
        Assert.False(first.IsCompleted);
        Assert.Same(outlook.Connecting, outlook.Connecting);

        // A caller that stops waiting does not stop the connection of the others.
        using var stop = new CancellationTokenSource();
        var impatient = outlook.EnsureConnectedAsync(stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impatient);

        client.Gate.SetResult();
        var session = await first;

        Assert.NotNull(session);
        Assert.Same(session, await second);
        Assert.Equal(1, client.Connections);
        Assert.Equal(OutlookState.Connected, outlook.State);
        Assert.True(outlook.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task DisposeWhileConnecting_ReleasesTheSessionThatArrivesLate()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync();
        var client = new GatedClient();
        var services = new CorrespondenceServices(scenario.Templates, scenario.Journal, new Core.Csv.CsvCodec(), scenario.Harness.AuditLog,
            scenario.Harness.Time, client);
        var outlook = new OutlookConnectionViewModel(scenario.Harness.Services, services);
        var connecting = outlook.EnsureConnectedAsync(TestContext.Current.CancellationToken);

        var disposing = outlook.DisposeAsync().AsTask();
        client.Gate.SetResult();
        await disposing;

        Assert.Null(await connecting);
        Assert.True(Assert.Single(client.Sessions).IsDisposed);
        Assert.Null(outlook.Session);
    }

    /// <summary>
    /// A client whose connection waits for <see cref="Gate"/> (and ignores cancellation, like a COM call that has started);
    /// its sessions can fail the diagnostics.
    /// </summary>
    private sealed class GatedClient(OutlookFailure? diagnoseFailure = null) : IOutlookClient
    {
        private readonly FakeOutlookClient _inner = new();
        private int _connections;

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<GatedSession> Sessions { get; } = [];

        public int Connections => Volatile.Read(ref _connections);

        public OutlookAvailability Availability => OutlookAvailability.Installed;

        public async Task<OutlookConnectResult> ConnectAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _connections);
            await Gate.Task.ConfigureAwait(false);
            var inner = (await _inner.ConnectAsync(CancellationToken.None).ConfigureAwait(false)).Session!;
            var session = new GatedSession(inner, diagnoseFailure);
            lock (Sessions)
            {
                Sessions.Add(session);
            }

            return new OutlookConnectResult(session, null);
        }
    }

    private sealed class GatedSession(IOutlookSession inner, OutlookFailure? diagnoseFailure) : IOutlookSession
    {
        public bool IsDisposed { get; private set; }

        public OutlookProfile Profile => inner.Profile;

        public bool IsConnected => !IsDisposed;

        public Task<OutlookItemResult> SendAsync(OutlookMessage message, OutlookSendMode mode, CancellationToken cancellationToken) =>
            inner.SendAsync(message, mode, cancellationToken);

        public Task<OutlookRenderedMessage> RenderAsync(OutlookMessage message, CancellationToken cancellationToken) =>
            inner.RenderAsync(message, cancellationToken);

        public Task<OutlookDiagnostics> DiagnoseAsync(bool createTestItem, CancellationToken cancellationToken) =>
            diagnoseFailure is { } failure ? Task.FromException<OutlookDiagnostics>(new OutlookException(failure)) : inner.DiagnoseAsync(createTestItem, cancellationToken);

        public Task<IReadOnlyList<OutlookCampaign>> FindCampaignsAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
            inner.FindCampaignsAsync(since, cancellationToken);

        public Task<IReadOnlyList<OutlookVoteResponse>> ReadResponsesAsync(string campaignId, CancellationToken cancellationToken) =>
            inner.ReadResponsesAsync(campaignId, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            IsDisposed = true;
            await inner.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>A client that breaks its contract (throws instead of returning a failure); the message holds an address.</summary>
    private sealed class ThrowingClient : IOutlookClient
    {
        public OutlookAvailability Availability => OutlookAvailability.Installed;

        public Task<OutlookConnectResult> ConnectAsync(CancellationToken cancellationToken) =>
            throw new System.Runtime.InteropServices.COMException("Brak dostępu do skrzynki jan.kowalski@contoso.test", unchecked((int)0x80004005));
    }
}
