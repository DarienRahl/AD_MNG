using AdMng.Core.Messaging;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.UserMessages;
using AdMng.Watchdog.Testing.Certificates;
using static AdMng.Presentation.Tests.UserMessages.MessageScenario;

namespace AdMng.Presentation.Tests.UserMessages;

/// <summary>The "E-mail messages" tab of the settings: validation, insecure TLS, saving, connection test, test message.</summary>
public sealed class UserMessageSettingsTests
{
    [Fact]
    public async Task Tab_ValidatesAddressesRangesTlsAndPin_AndSaves()
    {
        var scenario = await CreateAsync();
        var settings = scenario.Factory.CreateSettings();
        var tab = Assert.IsType<UserMessageSettingsViewModel>(settings.Messages);
        Assert.Equal("smtp.contoso.test", tab.SmtpHost);
        Assert.Null(settings.Error);

        tab.FromAddress = "nie adres";
        Assert.Equal("Sender address: not a valid e-mail address.", settings.Error);
        Assert.False(settings.SaveCommand.CanExecute(null));
        tab.FromAddress = "it-noreply@contoso.test";
        tab.MessagesPerMinute = 0;
        Assert.NotNull(settings.Error);
        tab.MessagesPerMinute = 20;
        tab.Tls = tab.TlsModes.Single(m => m.Value == SmtpTlsMode.None);
        Assert.True(tab.IsInsecureTls);
        Assert.Equal("Confirm the warning about sending without encryption.", settings.Error);
        tab.InsecureTlsConfirmed = true;
        tab.Certificate = tab.CertificateChecks.Single(c => c.Value == SmtpCertificateCheck.PinnedSha256);
        Assert.Equal("The pinned SHA-256 must have 64 hexadecimal digits.", settings.Error);
        tab.PinnedSha256 = TestCertificates.Sha256(TestCertificates.Localhost);
        tab.AllowedDomainsText = "Intranet.Contoso.test\nhttps://sspr.contoso.com/, contoso.test";
        tab.Language = tab.Languages.Single(l => l.Value == "en");
        Assert.Null(settings.Error);

        await settings.SaveCommand.ExecuteAsync(null);

        var saved = scenario.Harness.Settings.Current.UserMessages;
        Assert.Equal("it-noreply@contoso.test", saved.FromAddress);
        Assert.Equal(20, saved.MessagesPerMinute);
        Assert.Equal(["intranet.contoso.test", "sspr.contoso.com", "contoso.test"], saved.AllowedLinkDomains);
        Assert.Equal("en", saved.DefaultLanguage);
        Assert.Equal((SmtpTlsMode.None, true), (saved.Smtp.Tls, saved.Smtp.InsecureTlsConfirmed));
        Assert.Equal(SmtpCertificateCheck.PinnedSha256, saved.Smtp.Certificate);
    }

    [Fact]
    public void UnconfirmedInsecureTls_FromAHandEditedFile_FallsBackToStartTls()
    {
        var settings = new AppSettings();
        settings.UserMessages.Smtp.Tls = SmtpTlsMode.None;
        settings.UserMessages.MessagesPerMinute = 100_000;
        settings.UserMessages.Templates.Add(new MessageTemplate { Id = BuiltInMessageTemplates.PasswordExpiring, Name = "podszyty" });

        settings.Normalize();

        Assert.Equal(SmtpTlsMode.StartTlsRequired, settings.UserMessages.Smtp.Tls);
        Assert.Equal(600, settings.UserMessages.MessagesPerMinute);
        Assert.Empty(settings.UserMessages.Templates);
    }

    [Fact]
    public async Task TestConnection_UsesTheTypedValues_AndThePasswordOnlyInMemory()
    {
        var scenario = await CreateAsync();
        var tab = scenario.Factory.CreateSettings().Messages!;
        tab.SmtpHost = "relay.contoso.test";
        tab.SmtpPort = 465;
        tab.Tls = tab.TlsModes.Single(m => m.Value == SmtpTlsMode.ImplicitTls);
        tab.Authentication = tab.Authentications.Single(a => a.Value == SmtpAuthentication.Basic);
        tab.SmtpUserName = "helpdesk@contoso.test";
        scenario.Transport.TestResult = new SmtpConnectionTest
        {
            Success = true,
            Stage = SmtpTestStage.Done,
            IsEncrypted = true,
            TlsProtocol = "Tls13",
            AuthenticationMechanisms = ["LOGIN", "PLAIN"],
            AuthenticatedWith = "USER",
            CertificateSha256 = TestCertificates.Sha256(TestCertificates.Localhost),
        };

        await tab.TestConnectionCommand.ExecuteAsync(null);
        Assert.Equal("Enter the SMTP password (kept in memory only until AD Manager closes).", tab.TestResult);

        var box = new TestPasswordInput("Haslo-Testowe-1");
        tab.SmtpPasswordInput = box;
        await tab.TestConnectionCommand.ExecuteAsync(null);

        Assert.True(tab.TestSucceeded);
        Assert.StartsWith("The connection works: encrypted (Tls13), authentication: USER.", tab.TestResult, StringComparison.Ordinal);
        Assert.Contains("LOGIN, PLAIN", tab.TestResult, StringComparison.Ordinal);
        Assert.Equal(("relay.contoso.test", 465, SmtpTlsMode.ImplicitTls), (scenario.Transport.LastOptions!.Host, scenario.Transport.LastOptions.Port, scenario.Transport.LastOptions.Tls));
        Assert.Equal("Haslo-Testowe-1", scenario.Transport.LastCredential!.Password);
        Assert.True(box.Cleared);
        Assert.Equal("smtp.contoso.test", scenario.Harness.Settings.Current.UserMessages.Smtp.Host);

        tab.PinSeenCertificateCommand.Execute(null);
        Assert.True(tab.IsPinned);
        Assert.Equal(TestCertificates.Sha256(TestCertificates.Localhost), tab.PinnedSha256);

        // A test message goes to the operator's address over the same connection, then Save stores no password.
        await tab.SendTestMessageCommand.ExecuteAsync(null);
        var sample = Assert.Single(scenario.Transport.Accepted);
        Assert.Equal("operator@contoso.test", sample.To[0].Address);
        Assert.Equal("AD Manager – test message", sample.Subject);
        Assert.Contains("Nigdy nie prosimy o podanie hasła", sample.TextBody, StringComparison.Ordinal);
        Assert.Equal("The server accepted the test message for operator@contoso.test.", tab.TestResult);
        var settings = scenario.Factory.CreateSettings();
        settings.Messages!.SmtpUserName = "helpdesk@contoso.test";
        settings.Messages.Authentication = settings.Messages.Authentications.Single(a => a.Value == SmtpAuthentication.Basic);
        await settings.SaveCommand.ExecuteAsync(null);
        var json = JsonSettingsStore.Serialize(scenario.Harness.Settings.Current);
        Assert.DoesNotContain("Haslo-Testowe-1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailedTest_ShowsTheStageCodeAndHint()
    {
        var scenario = await CreateAsync();
        var tab = scenario.Factory.CreateSettings().Messages!;
        scenario.Transport.TestResult = new SmtpConnectionTest
        {
            Stage = SmtpTestStage.Tls,
            Failure = new UserMessageDelivery(UserMessageDeliveryOutcome.TransportFailed, "smtp.notSupported", Hint: "smtp.startTlsMissing"),
        };

        await tab.TestConnectionCommand.ExecuteAsync(null);

        Assert.False(tab.TestSucceeded);
        Assert.StartsWith("Stage \"TLS\" failed: smtp.notSupported – The server does not offer STARTTLS. – ", tab.TestResult, StringComparison.Ordinal);
        Assert.Null(tab.SeenCertificate);
        Assert.False(tab.PinSeenCertificateCommand.CanExecute(null));
    }

    [Fact]
    public async Task WithoutMessages_TheSettingsHaveNoTab()
    {
        var harness = await B2Harness.CreateAsync();

        Assert.Null(harness.Factory.CreateSettings().Messages);
    }
}
