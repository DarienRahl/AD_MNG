using System.Globalization;
using System.Text;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Core.Directory;
using AdMng.Presentation.Diagnostics;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Readiness;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Tests.Connections;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using static AdMng.Presentation.Tests.Diagnostics.DiagnosticLogBufferTests;

namespace AdMng.Presentation.Tests.Diagnostics;

/// <summary>
/// "Pomoc → Zgłoś problem…" (design note s-pilot-readiness.md 3): a value-free report – versions, the logon type and
/// transport flags of the window's connection, the optional components, readiness codes and log templates – that the
/// operator copies or saves; no server, domain or account name and no logged value ever appears in it.
/// </summary>
public sealed class ProblemReportTests
{
    [Fact]
    public async Task Report_NamesNoServerDomainOrAccount_AndNoLoggedValue()
    {
        await using var host = new ConnectionScopeHost(services =>
        {
            services.Replace(ServiceDescriptor.Scoped<IReadinessResults, FakeReadinessResults>());
            services.AddSingleton<IOutlookClient>(new FakeOutlookClient { Availability = OutlookAvailability.NotInstalled });
            services.AddSingleton<IWebView2Runtime>(new FakeWebView2Runtime(OptionalFeatureState.Disabled));
        });
        await using var window = host.CreateConnection();
        var session = await window.ConnectAsync(user: "łukasz.wiśniewski");
        await host.Provider.GetRequiredService<ISettingsStore>().UpdateAsync(s => s.Watchdog.ServiceUrl = "https://admng-wd.contoso.test:8443/", TestContext.Current.CancellationToken);

        // What the application logged: values as arguments, inside exception messages and in a scope.
        var logger = new DiagnosticLoggerProvider(host.Provider.GetRequiredService<DiagnosticLogBuffer>()).CreateLogger("AdMng.Directory.LdapDirectorySession");
        using (logger.BeginScope("Session {Server}", session.Info.Server))
        {
            logger.LogError(new InvalidOperationException($"Brak dostępu do {Dn} na {session.Info.Server}", new IOException(Password)),
                "Modify of {Dn} as {Account} with {Password} failed", Dn, Account, Password);
        }

        using var report = window.Get<IDialogViewModelFactory>().CreateProblemReport();
        var text = report.Text;

        Assert.Contains("Modify of {Dn} as {Account} with {Password} failed", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException", text, StringComparison.Ordinal);
        Assert.Contains("System.IO.IOException", text, StringComparison.Ordinal);
        Assert.Contains(AppVersion.Display, text, StringComparison.Ordinal);
        Assert.Contains("[CurrentUser]", text, StringComparison.Ordinal);
        Assert.Contains("[SignAndSeal]", text, StringComparison.Ordinal);
        Assert.Contains("Signing: Yes · Sealing: Yes · LDAPS: No · StartTLS: No", text, StringComparison.Ordinal);
        Assert.Contains("Classic Outlook installed: No", text, StringComparison.Ordinal);
        Assert.Contains("WebView2 runtime available (formatted previews): No (switched off)", text, StringComparison.Ordinal);
        Assert.Contains("AdMng Watchdog service configured: Yes", text, StringComparison.Ordinal);
        Assert.Contains("Connection.Sealing: Ok", text, StringComparison.Ordinal);
        Assert.Contains("EventLogs.SecurityReadable: Error", text, StringComparison.Ordinal);

        var names = new[]
        {
            session.Info.Server, session.Info.DomainDnsName, session.Info.DomainNetBiosName!, session.Info.BoundIdentity,
            session.Info.DefaultNamingContext, "łukasz", "admng-wd", "contoso",
        };
        foreach (var value in names.Concat(Values))
        {
            Assert.DoesNotContain(value, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Report_WhileNotConnected_SaysSo_WithoutComponentsOrResults()
    {
        await using var host = new ConnectionScopeHost();
        await using var window = host.CreateConnection();

        using var report = window.Get<IDialogViewModelFactory>().CreateProblemReport();

        Assert.Contains("Not connected.", report.Text, StringComparison.Ordinal);
        Assert.Contains("Classic Outlook installed: Not checked", report.Text, StringComparison.Ordinal);
        Assert.Contains("AdMng Watchdog service configured: No", report.Text, StringComparison.Ordinal);
        Assert.Contains("No results – run the readiness check in this window first.", report.Text, StringComparison.Ordinal);
        Assert.Contains("No entries.", report.Text, StringComparison.Ordinal);
        Assert.Equal("Report a problem", report.WindowTitle);
    }

    [Fact]
    public void Builder_WritesTheFlagsOfEveryLogonAndProtection_AndTheLogTemplates()
    {
        var localizer = new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"));
        var content = Content() with
        {
            Connection = new ProblemReportConnection(AuthenticationMode.SimpleBind, TransportSecurity.Tls, null, IsGlobalCatalog: true),
            Outlook = OptionalFeatureState.Available,
            WebView2 = OptionalFeatureState.NotAvailable,
            Log =
            [
                new DiagnosticLogEntry(new DateTimeOffset(2026, 11, 4, 9, 30, 15, 120, TimeSpan.FromHours(1)), LogLevel.Warning,
                    "AdMng.App.Services.WebView2Host", 0, null, "WebView2 could not be started ({Error})\nthe preview shows the source", []),
                new DiagnosticLogEntry(new DateTimeOffset(2026, 11, 4, 8, 31, 0, TimeSpan.Zero), LogLevel.Error, "AdMng.Tests", 7, "ResetFailed", null,
                    [new DiagnosticException("System.TimeoutException", unchecked((int)0x80131505))]),
            ],
            LogTotal = 250,
        };

        var text = ProblemReportBuilder.Build(content, localizer);

        Assert.Contains("Logon: Simple bind (TLS only) [SimpleBind]", text, StringComparison.Ordinal);
        Assert.Contains("Authentication: LDAP simple bind", text, StringComparison.Ordinal);
        Assert.Contains("[Tls]", text, StringComparison.Ordinal);
        Assert.Contains("Signing: No · Sealing: No · LDAPS: Yes · StartTLS: No", text, StringComparison.Ordinal);
        Assert.Contains("Global catalog: Yes", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SASL", text, StringComparison.Ordinal);
        Assert.Contains("Classic Outlook installed: Yes", text, StringComparison.Ordinal);
        Assert.Contains("WebView2 runtime available (formatted previews): No", text, StringComparison.Ordinal);
        Assert.Contains("Diagnostic log (last 2 of 250 entries; templates only, without values)", text, StringComparison.Ordinal);
        Assert.Contains("2026-11-04T08:30:15.120Z Warning AdMng.App.Services.WebView2Host [0] WebView2 could not be started ({Error}) the preview shows the source",
            text, StringComparison.Ordinal);
        Assert.Contains("2026-11-04T08:31:00.000Z Error AdMng.Tests [7 ResetFailed] -", text, StringComparison.Ordinal);
        Assert.Contains("System.TimeoutException (HResult 0x80131505)", text, StringComparison.Ordinal);

        var kerberos = ProblemReportBuilder.Build(Content() with
        {
            Connection = new ProblemReportConnection(AuthenticationMode.Credentials, TransportSecurity.SignAndSeal, 256, IsGlobalCatalog: false),
        }, localizer);
        Assert.Contains("Authentication: Negotiate (Kerberos or NTLM)", kerberos, StringComparison.Ordinal);
        Assert.Contains("SASL security strength factor: 256", kerberos, StringComparison.Ordinal);
        Assert.Contains("Created (UTC): 2026-11-04T10:00:00.000Z", kerberos, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_InPolish_TranslatesTheLabels_AndKeepsTheCodes()
    {
        var text = ProblemReportBuilder.Build(Content() with
        {
            Connection = new ProblemReportConnection(AuthenticationMode.CurrentUser, TransportSecurity.StartTls, null, IsGlobalCatalog: false),
            Readiness = [new ReadinessResultCode("Sysvol.WritePermission", "Warning")],
        }, new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL")));

        Assert.Contains("AD Manager – raport problemu", text, StringComparison.Ordinal);
        Assert.Contains("Podpisywanie: Nie · Szyfrowanie (sealing): Nie · LDAPS: Nie · StartTLS: Tak", text, StringComparison.Ordinal);
        Assert.Contains("[StartTls]", text, StringComparison.Ordinal);
        Assert.Contains("Sysvol.WritePermission: Warning", text, StringComparison.Ordinal);
        Assert.Contains("Klasyczny Outlook zainstalowany: Nie sprawdzono", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_PutsTheReportOnTheClipboard_AndSave_WritesOnlyTheChosenFile()
    {
        var harness = await B2Harness.CreateAsync();
        var buffer = new DiagnosticLogBuffer(harness.Time);
        using var report = new ProblemReportViewModel(harness.Services, new ProblemReportServices(buffer, new FakeReadinessResults()));

        report.CopyCommand.Execute(null);
        Assert.Equal(report.Text, harness.Clipboard.Text);
        Assert.Contains("The problem report was copied to the clipboard.", harness.Notifications.Items.Select(m => m.Message));

        // Cancelled: nothing is written.
        harness.Files.NextSavePath = null;
        await report.SaveCommand.ExecuteAsync(null);
        Assert.Equal("admng-problem-report-20261013-120000.txt", harness.Files.LastDefaultFileName);

        var folder = System.IO.Directory.CreateTempSubdirectory("admng-problem-report-");
        try
        {
            var path = Path.Combine(folder.FullName, "zgłoszenie.txt");
            harness.Files.NextSavePath = path;
            await report.SaveCommand.ExecuteAsync(null);

            Assert.Equal(report.Text, await File.ReadAllTextAsync(path, Encoding.UTF8, TestContext.Current.CancellationToken));
            Assert.Single(folder.GetFiles());
            Assert.Contains("The problem report was saved: zgłoszenie.txt", harness.Notifications.Items.Select(m => m.Message));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Refresh_ShowsNewEntries_AndTheLanguageOfTheWindow()
    {
        var harness = await B2Harness.CreateAsync();
        var buffer = new DiagnosticLogBuffer(harness.Time);
        using var report = new ProblemReportViewModel(harness.Services, new ProblemReportServices(buffer, new FakeReadinessResults()));
        Assert.DoesNotContain("Bind retried", report.Text, StringComparison.Ordinal);

        new DiagnosticLoggerProvider(buffer).CreateLogger("AdMng.Directory").LogInformation("Bind retried on {Server}", "dc02.contoso.test");
        report.RefreshCommand.Execute(null);
        Assert.Contains("Bind retried on {Server}", report.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("dc02", report.Text, StringComparison.OrdinalIgnoreCase);

        harness.Localizer.SetCulture(CultureInfo.GetCultureInfo("pl-PL"));
        Assert.Contains("Połączenie tego okna", report.Text, StringComparison.Ordinal);
        Assert.Equal("Zgłoś problem", report.WindowTitle);
    }

    [Fact]
    public async Task Collect_TakesTheFlagsOfTheWindowsSession_AndTheOptionalComponents()
    {
        var harness = await B2Harness.CreateAsync();
        var outlook = new FakeOutlookClient { Availability = OutlookAvailability.Installed };
        using var report = new ProblemReportViewModel(harness.Services,
            new ProblemReportServices(new DiagnosticLogBuffer(harness.Time), new FakeReadinessResults(), outlook, new FakeWebView2Runtime(OptionalFeatureState.Available)));

        var content = report.Collect();

        Assert.Equal(new ProblemReportConnection(AuthenticationMode.CurrentUser, TransportSecurity.SignAndSeal, null, false), content.Connection);
        Assert.Equal((OptionalFeatureState.Available, OptionalFeatureState.Available, false), (content.Outlook, content.WebView2, content.WatchdogConfigured));
        Assert.Equal(harness.Time.GetUtcNow(), content.CreatedAt);
        Assert.Equal("en-US", content.UiCulture);
        Assert.Equal(2, content.Readiness.Count);

        await harness.Sessions.DisconnectAsync();
        Assert.Null(report.Collect().Connection);
    }

    [Fact]
    public async Task Registration_DefaultsToNoReadinessResults_AndAHostRegistrationWins()
    {
        await using var host = new ConnectionScopeHost();
        await using var window = host.CreateConnection();
        Assert.Empty(window.Get<IReadinessResults>().Last);
        Assert.Same(host.Provider.GetRequiredService<DiagnosticLogBuffer>(), window.Get<ProblemReportServices>().Log);

        var services = new ServiceCollection();
        services.AddScoped<IReadinessResults, FakeReadinessResults>();
        services.AddAdMngPresentation();
        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IReadinessResults));
        Assert.Equal((typeof(FakeReadinessResults), ServiceLifetime.Scoped), (descriptor.ImplementationType, descriptor.Lifetime));
    }

    [Fact]
    public async Task PaletteCommand_OpensTheProblemReport()
    {
        var harness = await B2Harness.CreateAsync();
        var shell = harness.CreateShell();

        var descriptor = shell.CommandPalette.Find("help.reportProblem");
        Assert.NotNull(descriptor);
        Assert.Equal(L.ProblemReport.Command, descriptor.TitleKey);
        await shell.ShowProblemReportCommand.ExecuteAsync(null);

        Assert.Contains("problemReport", harness.Dialogs.Shown);
    }

    private static ProblemReportContent Content() => new()
    {
        CreatedAt = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero),
        Version = "0.12.0 (9def9bf)",
        OperatingSystem = "Microsoft Windows 10.0.26100",
        OperatingSystemArchitecture = "X64",
        Runtime = ".NET 10.0.1",
        ProcessArchitecture = "X64",
        UiCulture = "pl-PL",
        FormatCulture = "pl-PL",
    };

    private sealed class FakeReadinessResults : IReadinessResults
    {
        public IReadOnlyList<ReadinessResultCode> Last { get; } =
        [
            new("Connection.Sealing", "Ok"),
            new("EventLogs.SecurityReadable", "Error"),
        ];
    }

    private sealed class FakeWebView2Runtime(OptionalFeatureState availability) : IWebView2Runtime
    {
        public OptionalFeatureState Availability => availability;
    }
}
