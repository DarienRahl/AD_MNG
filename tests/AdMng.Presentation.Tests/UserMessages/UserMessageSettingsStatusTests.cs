using System.ComponentModel;
using System.Globalization;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Core.Messaging;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;
using AdMng.Watchdog.Testing.Certificates;
using static AdMng.Presentation.Tests.UserMessages.MessageScenario;

namespace AdMng.Presentation.Tests.UserMessages;

/// <summary>
/// The status line of the settings window (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-2 as built"): the
/// window has nothing to say of its own and shows what the tab in front says - the "E-mail messages" tab with its
/// three operations: the connection test, the test message and the read of the Outlook accounts, each with Stop, and
/// how they ended. Why Save is not possible stays a note (<see cref="SettingsViewModel.Error"/>). The SMTP password is
/// in no text and no log copy; an address is never in a log copy. How the settings are validated, tested and saved is
/// covered, unchanged, by <see cref="UserMessageSettingsTests"/> and <see cref="OutlookTransportChoiceTests"/>.
/// </summary>
public sealed class UserMessageSettingsStatusTests
{
    private const string SmtpPassword = "Haslo-Testowe-1";

    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    /// <summary>How long a held call waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheWindow_ShowsWhatTheTabInFrontSays_AndNothingOnTheOtherTabs()
    {
        var scenario = await CreateAsync();
        using var settings = scenario.Factory.CreateSettings();
        var tab = settings.Messages!;
        Assert.IsAssignableFrom<IHasPageStatus>(settings);
        Assert.IsAssignableFrom<IHasPageStatus>(tab);
        Assert.NotSame(settings.Status, tab.Status);

        // The window opens on "General": nothing in front, nothing to say.
        Assert.Equal(0, settings.SelectedTabIndex);
        Assert.Null(settings.Status.Front);
        Assert.Same(PageStatus.Empty, settings.Status.Current);
        Assert.Null(settings.Status.Lasting);

        // A test of the "E-mail messages" tab ended while another tab was shown (its commands can run from code): the
        // tab keeps what it said, the window says nothing.
        await tab.TestConnectionCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Done, tab.Status.Current.Kind);
        Assert.Same(PageStatus.Empty, settings.Status.Current);

        // The tab comes to the front: the window shows what it says, and closes it there.
        settings.SelectedTabIndex = SettingsViewModel.MessagesTab;
        Assert.Same(tab.Status, settings.Status.Front);
        Assert.Same(tab.Status.Current, settings.Status.Current);
        Assert.Equal(PageStatusKind.Done, settings.Status.Current.Kind);
        Assert.True(settings.Status.CloseCommand.CanExecute(null));

        // Another tab: nothing in front again; back on the tab its result is still there.
        settings.SelectedTabIndex = SettingsViewModel.MessagesTab + 1;
        Assert.Null(settings.Status.Front);
        Assert.Same(PageStatus.Empty, settings.Status.Current);
        settings.SelectedTabIndex = SettingsViewModel.MessagesTab;
        Assert.Equal(PageStatusKind.Done, settings.Status.Current.Kind);
        settings.Status.CloseCommand.Execute(null);
        Assert.Same(PageStatus.Empty, settings.Status.Current);
        Assert.Same(PageStatus.Empty, tab.Status.Current);

        // Why Save is not possible is not a status: the note of the window, computed from the fields.
        tab.FromAddress = "nie adres";
        Assert.Equal("Sender address: not a valid e-mail address.", settings.Error);
        Assert.Same(PageStatus.Empty, settings.Status.Current);

        // Settings without the messages tab: the same index has nothing in front.
        var harness = await B2Harness.CreateAsync();
        using var plain = harness.Factory.CreateSettings();
        Assert.Null(plain.Messages);
        plain.SelectedTabIndex = SettingsViewModel.MessagesTab;
        Assert.Null(plain.Status.Front);
        Assert.Same(PageStatus.Empty, plain.Status.Current);
    }

    [Theory]
    [InlineData(
        "en-US",
        "Testing the connection to relay.contoso.test:465…",
        "The connection to relay.contoso.test:465 works: encrypted (Tls13), authentication: USER.",
        "Authentication mechanisms offered: LOGIN, PLAIN")]
    [InlineData(
        "pl-PL",
        "Testowanie połączenia z relay.contoso.test:465…",
        "Połączenie z relay.contoso.test:465 działa: szyfrowane (Tls13), uwierzytelnianie: USER.",
        "Oferowane mechanizmy uwierzytelniania: LOGIN, PLAIN")]
    public async Task TheConnectionTest_IsAnOperationOfTheTab_WithStop_AndSaysThatTheConnectionWorks(
        string culture, string working, string done, string mechanisms)
    {
        var scenario = await CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var settings = scenario.Factory.CreateSettings();
        settings.SelectedTabIndex = SettingsViewModel.MessagesTab;
        var tab = settings.Messages!;
        tab.SmtpHost = "relay.contoso.test";
        tab.SmtpPort = 465;
        tab.Tls = tab.TlsModes.Single(m => m.Value == SmtpTlsMode.ImplicitTls);
        var fingerprint = TestCertificates.Sha256(TestCertificates.Localhost);
        scenario.Transport.TestResult = new SmtpConnectionTest
        {
            Success = true,
            Stage = SmtpTestStage.Done,
            IsEncrypted = true,
            TlsProtocol = "Tls13",
            AuthenticationMechanisms = ["LOGIN", "PLAIN"],
            AuthenticatedWith = "USER",
            CertificateSha256 = fingerprint,
        };
        using var test = new HeldCall();
        scenario.Transport.BeforeTestConnection = test.HoldOnce;
        var seen = Record(settings.Status);
        Task testing;
        try
        {
            testing = tab.TestConnectionCommand.ExecuteAsync(null);
            Assert.True(await test.Held.WaitAsync(Limit, Ct));

            // Working: which server is tested - with Stop, the tab's own; the other operations of the tab wait.
            var current = settings.Status.Current;
            Assert.Same(tab.Status.Current, current);
            Assert.Equal(PageStatusKind.Working, current.Kind);
            Assert.Equal(working, current.Message);
            Assert.Equal("Settings.Status.Testing", current.Log.ToString());
            Assert.Same(tab.Status.CancelCommand, Assert.IsType<PageStatusAction>(current.Action).Command);
            Assert.True(tab.Status.IsWorking);
            Assert.False(settings.Status.IsWorking);
            Assert.True(tab.IsTesting);
            Assert.False(tab.TestConnectionCommand.CanExecute(null));
            Assert.False(tab.SendTestMessageCommand.CanExecute(null));
            Assert.False(tab.LoadOutlookAccountsCommand.CanExecute(null));
            Assert.Null(tab.TestDetails);
        }
        finally
        {
            test.Release();
        }

        await testing.WaitAsync(Limit, Ct);

        // Done: that the connection works, how - in the line; what the server showed stays in the tab, for the pin.
        Assert.Equal([(PageStatusKind.Working, working), (PageStatusKind.Done, done)], seen);
        var result = settings.Status.Current;
        Assert.Equal("Settings.Status.TestOk", result.Log.ToString());
        Assert.True(result.CanClose);
        Assert.Null(result.Action);
        Assert.True(tab.TestSucceeded);
        Assert.False(tab.IsTesting);
        Assert.NotNull(tab.TestDetails);
        var details = tab.TestDetails.Split(Environment.NewLine);
        Assert.Equal(2, details.Length);
        Assert.Equal(mechanisms, details[0]);
        Assert.EndsWith(fingerprint, details[1], StringComparison.Ordinal);
        Assert.DoesNotContain(fingerprint, result.Message, StringComparison.Ordinal);
        Assert.Equal(fingerprint, tab.SeenCertificate);
        Assert.True(tab.PinSeenCertificateCommand.CanExecute(null));

        // The text of before is still made (tests and code read it): the first line and the same facts.
        Assert.EndsWith(tab.TestDetails, tab.TestResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConnectionTestThatFails_IsAnError_WithTheStageAndTheReasonOfTheTransport()
    {
        var scenario = await CreateAsync();
        using var settings = scenario.Factory.CreateSettings();
        settings.SelectedTabIndex = SettingsViewModel.MessagesTab;
        var tab = settings.Messages!;
        scenario.Transport.TestResult = new SmtpConnectionTest
        {
            Stage = SmtpTestStage.Tls,
            Failure = new UserMessageDelivery(UserMessageDeliveryOutcome.TransportFailed, "smtp.notSupported", Hint: "smtp.startTlsMissing"),
        };

        await tab.TestConnectionCommand.ExecuteAsync(null);

        var failed = settings.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith(
            "The connection to smtp.contoso.test:587 does not work: Stage \"TLS\" failed: smtp.notSupported – The server does not offer STARTTLS. – ",
            failed.Message,
            StringComparison.Ordinal);
        Assert.EndsWith(".", failed.Message, StringComparison.Ordinal);
        Assert.Equal("Settings.Status.TestFailed", failed.Log.ToString());
        Assert.True(failed.CanClose);
        Assert.Null(failed.Action);
        Assert.False(tab.TestSucceeded);
        Assert.Null(tab.TestDetails);
        Assert.StartsWith("Stage \"TLS\" failed: smtp.notSupported – ", tab.TestResult, StringComparison.Ordinal);

        // The next test takes the result of this one off the line.
        scenario.Transport.TestResult = new SmtpConnectionTest { Success = true, Stage = SmtpTestStage.Done, IsEncrypted = false };
        await tab.TestConnectionCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Done, settings.Status.Current.Kind);
        Assert.Equal("The connection to smtp.contoso.test:587 works: NOT encrypted, authentication: None (relay).", settings.Status.Current.Message);
    }

    [Fact]
    public async Task StopWhileTesting_SaysStoppingAtOnce_AndEndsAsAttention()
    {
        var scenario = await CreateAsync();
        using var settings = scenario.Factory.CreateSettings();
        settings.SelectedTabIndex = SettingsViewModel.MessagesTab;
        var tab = settings.Messages!;
        using var test = new HeldCall();
        scenario.Transport.BeforeTestConnection = test.HoldOnce;
        var seen = Record(settings.Status);
        Task testing;
        try
        {
            testing = tab.TestConnectionCommand.ExecuteAsync(null);
            Assert.True(await test.Held.WaitAsync(Limit, Ct));

            // Stop of the line - the command of the tab that owns the operation: "Stopping…" at once.
            Assert.IsType<PageStatusAction>(settings.Status.Current.Action).Command.Execute(null);
            Assert.True(tab.Status.IsCancelling);
            Assert.Equal(PageStatusKind.Working, settings.Status.Current.Kind);
            Assert.Equal("Stopping…", settings.Status.Current.Message);
        }
        finally
        {
            test.Release();
        }

        await testing.WaitAsync(Limit, Ct);

        const string StoppedText = "Testing the connection to smtp.contoso.test:587 was stopped.";
        Assert.Equal(
            [
                (PageStatusKind.Working, "Testing the connection to smtp.contoso.test:587…"),
                (PageStatusKind.Working, "Stopping…"),
                (PageStatusKind.Attention, StoppedText),
            ],
            seen);
        Assert.Equal("Settings.Status.TestStopped", settings.Status.Current.Log.ToString());
        Assert.False(tab.IsTesting);
        Assert.False(tab.TestSucceeded);
        Assert.Null(tab.TestDetails);
        Assert.True(tab.TestConnectionCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(
        "en-US",
        "The connection was not tested: Sender address: not a valid e-mail address.",
        "The connection was not tested: Enter the SMTP password (kept in memory only until AD Manager closes).",
        "The test message was not sent: Enter the SMTP password (kept in memory only until AD Manager closes).")]
    [InlineData(
        "pl-PL",
        "Nie przetestowano połączenia: Adres nadawcy: nieprawidłowy adres e-mail.",
        "Nie przetestowano połączenia: Wpisz hasło SMTP (jest przechowywane tylko w pamięci do zamknięcia AD Managera).",
        "Nie wysłano wiadomości testowej: Wpisz hasło SMTP (jest przechowywane tylko w pamięci do zamknięcia AD Managera).")]
    public async Task WhatStopsATestBeforeItStarts_IsAttention_AndThePasswordIsInNoText(string culture, string invalid, string noPassword, string noPasswordForMessage)
    {
        var scenario = await CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var settings = scenario.Factory.CreateSettings();
        settings.SelectedTabIndex = SettingsViewModel.MessagesTab;
        var tab = settings.Messages!;
        tab.Authentication = tab.Authentications.Single(a => a.Value == SmtpAuthentication.Basic);
        tab.SmtpUserName = "helpdesk@contoso.test";
        var seen = Record(settings.Status);

        // A value of the tab is not valid: the sentence of the window's note, as why nothing was tested.
        tab.FromAddress = "nie adres";
        await tab.TestConnectionCommand.ExecuteAsync(null);
        var first = settings.Status.Current;
        Assert.Equal(PageStatusKind.Attention, first.Kind);
        Assert.Equal(invalid, first.Message);
        Assert.Equal("Settings.Status.NotTested", first.Log.ToString());
        Assert.True(first.CanClose);
        Assert.Equal(settings.Error, tab.TestResult);
        tab.FromAddress = string.Empty;

        // No password in the box: nothing is tested and nothing is sent.
        await tab.TestConnectionCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Attention, settings.Status.Current.Kind);
        Assert.Equal(noPassword, settings.Status.Current.Message);
        Assert.Equal("UserMessages.PasswordRequired", settings.Status.Current.Log.ToString());
        await tab.SendTestMessageCommand.ExecuteAsync(null);
        Assert.Equal(noPasswordForMessage, settings.Status.Current.Message);
        Assert.Empty(scenario.Transport.Attempts);

        // With the password the test runs - and no text the line ever said holds it or the user name.
        tab.SmtpPasswordInput = new TestPasswordInput(SmtpPassword);
        await tab.TestConnectionCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Done, settings.Status.Current.Kind);
        Assert.Equal(SmtpPassword, scenario.Transport.LastCredential!.Password);
        Assert.DoesNotContain(seen, s => s.Message.Contains(SmtpPassword, StringComparison.Ordinal));
        Assert.DoesNotContain(seen, s => s.Message.Contains("Haslo", StringComparison.Ordinal));
        Assert.DoesNotContain(seen, s => s.Message.Contains("helpdesk@", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheTestMessage_IsAnOperationOfTheTab_WithStop_AndSaysHowItEnded()
    {
        var scenario = await CreateAsync();
        using var settings = scenario.Factory.CreateSettings();
        settings.SelectedTabIndex = SettingsViewModel.MessagesTab;
        var tab = settings.Messages!;
        using var send = new HeldCall();
        scenario.Transport.BeforeSend = _ => send.HoldOnce();
        var seen = Record(settings.Status);
        Task sending;
        try
        {
            sending = tab.SendTestMessageCommand.ExecuteAsync(null);
            Assert.True(await send.Held.WaitAsync(Limit, Ct));
            var current = settings.Status.Current;
            Assert.Equal(PageStatusKind.Working, current.Kind);
            Assert.Equal("Sending the test message to operator@contoso.test…", current.Message);
            Assert.Equal("MessageWindow.Status.TestSending", current.Log.ToString());
            Assert.Same(tab.Status.CancelCommand, Assert.IsType<PageStatusAction>(current.Action).Command);
            Assert.True(tab.IsTesting);
        }
        finally
        {
            send.Release();
        }

        await sending.WaitAsync(Limit, Ct);
        scenario.Transport.BeforeSend = null;
        const string SentText = "The server accepted the test message for operator@contoso.test.";
        Assert.Equal([(PageStatusKind.Working, "Sending the test message to operator@contoso.test…"), (PageStatusKind.Done, SentText)], seen);
        Assert.Equal("UserMessages.TestSent", settings.Status.Current.Log.ToString());
        Assert.Equal(SentText, tab.TestResult);
        Assert.True(tab.TestSucceeded);
        Assert.False(tab.IsTesting);

        // Refused by the server: an error with the reason of the transport.
        scenario.Transport.Script("operator@contoso.test", FakeUserMessageTransport.Rejected(550, "5.1.1"));
        await tab.SendTestMessageCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Error, settings.Status.Current.Kind);
        Assert.Equal("The test message was not sent: smtp.550.5.1.1.", settings.Status.Current.Message);
        Assert.Equal("MessageWindow.Status.TestNotSent", settings.Status.Current.Log.ToString());
        Assert.False(tab.TestSucceeded);

        // Stopped before anything reached the server: attention.
        using var connect = new HeldCall();
        scenario.Transport.BeforeConnect = connect.HoldOnce;
        try
        {
            sending = tab.SendTestMessageCommand.ExecuteAsync(null);
            Assert.True(await connect.Held.WaitAsync(Limit, Ct));
            tab.Status.CancelCommand.Execute(null);
            Assert.Equal("Stopping…", settings.Status.Current.Message);
        }
        finally
        {
            connect.Release();
        }

        await sending.WaitAsync(Limit, Ct);
        Assert.Equal(PageStatusKind.Attention, settings.Status.Current.Kind);
        Assert.Equal("Sending the test message was stopped: the message was not sent.", settings.Status.Current.Message);
        Assert.Equal("MessageWindow.Status.TestStopped", settings.Status.Current.Log.ToString());
        Assert.False(tab.IsTesting);
    }

    [Theory]
    [InlineData("en-US", "Connecting to Outlook…", "The Outlook profile “Outlook” has 2 accounts. Choose the account to send from in the list.")]
    [InlineData("pl-PL", "Łączę z Outlookiem…", "Profil Outlooka „Outlook” ma 2 konta. Wybierz z listy konto, z którego będą wysyłane wiadomości.")]
    public async Task ReadingTheOutlookAccounts_IsAnOperationOfTheTab_AndTheLineSaysHowMany_NotWhich(string culture, string working, string done)
    {
        var scenario = await OutlookScenarioAsync(CultureInfo.GetCultureInfo(culture));
        using var settings = scenario.Factory.CreateSettings();
        settings.SelectedTabIndex = SettingsViewModel.MessagesTab;
        var tab = settings.Messages!;
        using var held = new SemaphoreSlim(0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Outlook.BeforeConnect = async _ =>
        {
            held.Release();
            await release.Task.WaitAsync(HoldLimit, CancellationToken.None);
        };
        var seen = Record(settings.Status);
        Task reading;
        try
        {
            reading = tab.LoadOutlookAccountsCommand.ExecuteAsync(null);
            Assert.True(await held.WaitAsync(Limit, Ct));

            // Working, with Stop; the tests of the tab wait for it.
            var current = settings.Status.Current;
            Assert.Equal(PageStatusKind.Working, current.Kind);
            Assert.Equal(working, current.Message);
            Assert.Equal("UserMessages.Outlook.LoadingAccounts", current.Log.ToString());
            Assert.Same(tab.Status.CancelCommand, Assert.IsType<PageStatusAction>(current.Action).Command);
            Assert.True(tab.IsLoadingOutlookAccounts);
            Assert.False(tab.LoadOutlookAccountsCommand.CanExecute(null));
            Assert.False(tab.TestConnectionCommand.CanExecute(null));
            Assert.False(tab.SendTestMessageCommand.CanExecute(null));
        }
        finally
        {
            release.TrySetResult();
        }

        await reading.WaitAsync(Limit, Ct);

        // Done: how many accounts the profile has - they are the list of the box; no address in the line or its log copy.
        Assert.Equal([(PageStatusKind.Working, working), (PageStatusKind.Done, done)], seen);
        Assert.Equal("Settings.Status.OutlookRead (2)", settings.Status.Current.Log.ToString());
        Assert.DoesNotContain("@", settings.Status.Current.Message, StringComparison.Ordinal);
        Assert.Equal(["jan.kowalski@contoso.test", "sekretariat.lodz@contoso.test"], tab.OutlookAccounts);
        Assert.Contains("sekretariat.lodz@contoso.test", tab.OutlookAccountsText, StringComparison.Ordinal);
        Assert.False(tab.IsLoadingOutlookAccounts);
        Assert.True(tab.TestConnectionCommand.CanExecute(null));
        Assert.True(Assert.Single(scenario.Outlook.Sessions).IsDisposed);
    }

    [Fact]
    public async Task OutlookAccountsThatCouldNotBeRead_AreAnError_AndAStoppedReadIsAttention()
    {
        var scenario = await OutlookScenarioAsync(CultureInfo.GetCultureInfo("en-US"));
        using var settings = scenario.Factory.CreateSettings();
        settings.SelectedTabIndex = SettingsViewModel.MessagesTab;
        var tab = settings.Messages!;

        // Outlook cannot be reached: what was tried and why - the list stays as it was.
        scenario.Outlook.ConnectFailure = FakeOutlookClient.Failure(OutlookFailureKind.Elevated, "connect");
        await tab.LoadOutlookAccountsCommand.ExecuteAsync(null);
        var failed = settings.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith("Could not read the accounts from Outlook: AD Manager runs as administrator and Outlook does not", failed.Message, StringComparison.Ordinal);
        Assert.Equal("Settings.Status.OutlookFailed", failed.Log.ToString());
        Assert.True(failed.CanClose);
        Assert.Empty(tab.OutlookAccounts);
        Assert.False(tab.IsLoadingOutlookAccounts);

        // A profile without accounts: attention.
        scenario.Outlook.ConnectFailure = null;
        scenario.Outlook.Profile = FakeOutlookClient.SampleProfile with { Accounts = [] };
        await tab.LoadOutlookAccountsCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Attention, settings.Status.Current.Kind);
        Assert.Equal("The Outlook profile has no accounts.", settings.Status.Current.Message);
        Assert.Equal("UserMessages.Outlook.NoAccounts", settings.Status.Current.Log.ToString());

        // Stopped while Outlook was asked: the list was not changed.
        scenario.Outlook.Profile = FakeOutlookClient.SampleProfile;
        using var held = new SemaphoreSlim(0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Outlook.BeforeConnect = async _ =>
        {
            held.Release();
            await release.Task.WaitAsync(HoldLimit, CancellationToken.None);
        };
        var seen = Record(settings.Status);
        Task reading;
        try
        {
            reading = tab.LoadOutlookAccountsCommand.ExecuteAsync(null);
            Assert.True(await held.WaitAsync(Limit, Ct));
            tab.Status.CancelCommand.Execute(null);
            Assert.True(tab.Status.IsCancelling);
            Assert.Equal("Stopping…", settings.Status.Current.Message);
        }
        finally
        {
            release.TrySetResult();
        }

        await reading.WaitAsync(Limit, Ct);
        const string StoppedText = "Reading the accounts from Outlook was stopped: the list was not changed.";
        Assert.Equal(
            [(PageStatusKind.Working, "Connecting to Outlook…"), (PageStatusKind.Working, "Stopping…"), (PageStatusKind.Attention, StoppedText)],
            seen);
        Assert.Equal("Settings.Status.OutlookStopped", settings.Status.Current.Log.ToString());
        Assert.Empty(tab.OutlookAccounts);
        Assert.False(tab.IsLoadingOutlookAccounts);
        Assert.True(tab.LoadOutlookAccountsCommand.CanExecute(null));
    }

    [Fact]
    public async Task ClosingTheWindow_EndsATestThatRuns_WithoutAWord()
    {
        var scenario = await CreateAsync();
        var settings = scenario.Factory.CreateSettings();
        settings.SelectedTabIndex = SettingsViewModel.MessagesTab;
        var tab = settings.Messages!;
        using var test = new HeldCall();
        scenario.Transport.BeforeTestConnection = test.HoldOnce;
        var seen = Record(tab.Status);
        Task testing;
        try
        {
            testing = tab.TestConnectionCommand.ExecuteAsync(null);
            Assert.True(await test.Held.WaitAsync(Limit, Ct));
            settings.Dispose();
            Assert.Null(settings.Status.Front);
            Assert.False(tab.Status.IsWorking);
        }
        finally
        {
            test.Release();
        }

        await testing.WaitAsync(Limit, Ct);
        Assert.Equal([(PageStatusKind.Working, "Testing the connection to smtp.contoso.test:587…"), (PageStatusKind.Idle, string.Empty)], seen);
        Assert.False(tab.IsTesting);
    }

    [Fact]
    public async Task TheLogCopies_CarryKeysAndNumbers_NeverAServerAnAddressOrThePassword()
    {
        var scenario = await OutlookScenarioAsync(CultureInfo.GetCultureInfo("en-US"));
        var harness = scenario.Harness;
        var logger = new ListLogger<PageStatusHolder>();
        var lists = new ObjectListServices(harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes, harness.Time, logger);
        var services = new DialogServices(
            harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes, harness.Time, harness.Dialogs, harness.Files,
            harness.Clipboard, harness.Notifications, harness.Coordinator, harness.Activities, lists, harness.Regional);
        using var settings = new SettingsViewModel(services, harness.Theme, scenario.Services);
        settings.SelectedTabIndex = SettingsViewModel.MessagesTab;
        var tab = settings.Messages!;
        tab.Authentication = tab.Authentications.Single(a => a.Value == SmtpAuthentication.Basic);
        tab.SmtpUserName = "helpdesk@contoso.test";
        scenario.Transport.TestResult = new SmtpConnectionTest
        {
            Success = true,
            Stage = SmtpTestStage.Done,
            IsEncrypted = true,
            TlsProtocol = "Tls13",
            AuthenticationMechanisms = ["LOGIN"],
            AuthenticatedWith = "USER",
            CertificateSha256 = TestCertificates.Sha256(TestCertificates.Localhost),
        };

        await tab.TestConnectionCommand.ExecuteAsync(null);
        tab.SmtpPasswordInput = new TestPasswordInput(SmtpPassword);
        await tab.TestConnectionCommand.ExecuteAsync(null);
        await tab.SendTestMessageCommand.ExecuteAsync(null);
        await tab.LoadOutlookAccountsCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Done, settings.Status.Current.Kind);

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("UserMessages.PasswordRequired", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Settings.Status.Testing", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Settings.Status.TestOk", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("MessageWindow.Status.TestSending", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("UserMessages.TestSent", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Settings.Status.OutlookRead (2)", StringComparison.Ordinal));
        Assert.DoesNotContain(SmtpPassword, logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("@", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("contoso", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("smtp.", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Tls13", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(TestCertificates.Sha256(TestCertificates.Localhost), logger.AllText, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------

    /// <summary>The message scenario with Outlook as the transport of the settings (the fake Outlook is installed).</summary>
    private static async Task<MessageScenario> OutlookScenarioAsync(CultureInfo culture)
    {
        var scenario = await CreateAsync(culture);
        await scenario.Harness.Settings.UpdateAsync(s => s.UserMessages.Transport = UserMessageTransportChoice.Outlook, Ct);
        return scenario;
    }

    private static List<(PageStatusKind Kind, string Message)> Record(PageStatusHolder status)
    {
        var seen = new List<(PageStatusKind Kind, string Message)>();
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current))
            {
                lock (seen)
                {
                    seen.Add((status.Current.Kind, status.Current.Message));
                }
            }
        };
        status.PropertyChanged += handler;
        return seen;
    }

    /// <summary>A call the test holds once and releases deliberately: what a slow server is.</summary>
    private sealed class HeldCall : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private int _hold = 1;

        /// <summary>Signalled when the call arrived and waits.</summary>
        public SemaphoreSlim Held { get; } = new(0);

        /// <summary>Called by the code under test: the first call waits for <see cref="Release"/>, later ones pass.</summary>
        public void HoldOnce()
        {
            if (Interlocked.Exchange(ref _hold, 0) == 1)
            {
                Held.Release();
                _release.Wait(HoldLimit);
            }
        }

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
            Held.Dispose();
        }
    }
}
