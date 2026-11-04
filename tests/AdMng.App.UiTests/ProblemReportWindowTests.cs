using System.Windows;
using AdMng.App.Behaviors;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Presentation.Diagnostics;
using AdMng.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AdMng.App.UiTests;

/// <summary>
/// "Pomoc → Zgłoś problem…" (design note s-pilot-readiness.md 3): the Help menu opens one value-free report per connection
/// window; what the application logged reaches it through AppHost's logging as a template, never with its values.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ProblemReportWindowTests
{
    private const string Dn = "CN=Łukasz Wiśniewski,OU=Księgowość,DC=contoso,DC=test";
    private const string Password = "Zażółć-gęślą-jaźń!2026";

    [Fact]
    public Task HelpMenu_OpensOneProblemReport_WithTheLoggedTemplate_AndNoValue() => WpfTestHost.RunAsync(async () =>
    {
        var clipboard = new RecordingClipboard();
        await using var context = await UiTestContext.CreateAsync(
            configure: services => services.Replace(ServiceDescriptor.Singleton<IClipboardService>(clipboard)));
        await context.ConnectAsync();
        Assert.True(context.Services.GetRequiredService<IWindowService>().IsRegistered(typeof(ProblemReportViewModel)));
        Assert.Same(context.Services.GetRequiredService<WebView2Host>(), context.Services.GetRequiredService<IWebView2Runtime>());

        // Logged through the application's logging: AppHost feeds the diagnostic buffer next to the debugger output.
        context.Services.GetRequiredService<ILogger<ProblemReportWindowTests>>()
            .LogWarning(new InvalidOperationException($"Brak dostępu do {Dn}"), "Modify of {Dn} failed with {Password}", Dn, Password);

        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        await main.Shell.InitializeAsync();
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal("help.reportProblem", CommandMenu.GetId(main.ReportProblemMenuItem));
        Assert.Same(main.Shell.ShowProblemReportCommand, main.ReportProblemMenuItem.Command);

        main.ReportProblemMenuItem.Command.Execute(null);
        main.ReportProblemMenuItem.Command.Execute(null);
        await UiWait.UntilAsync(() => Application.Current.Windows.OfType<ProblemReportWindow>().Any(), "the problem report opened");
        await UiTestSupport.WaitForIdleAsync();

        var window = Assert.Single(Application.Current.Windows.OfType<ProblemReportWindow>());
        Assert.Same(window, context.Services.GetRequiredService<IWindowService>().FindOpen(ToolWindows.ProblemReportKey));
        var text = window.ReportBox.Text;
        Assert.Equal(window.ViewModel.Text, text);
        Assert.True(window.ReportBox.IsReadOnly);
        Assert.Contains("Modify of {Dn} failed with {Password}", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException", text, StringComparison.Ordinal);
        Assert.Contains("[SignAndSeal]", text, StringComparison.Ordinal);
        foreach (var value in new[] { Dn, Password, "Wiśniewski", "contoso", "dc01", "Administrator" })
        {
            Assert.DoesNotContain(value, text, StringComparison.OrdinalIgnoreCase);
        }

        window.CopyButton.Command.Execute(null);
        Assert.Equal(text, clipboard.Text);
        window.ViewModel.CloseCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.False(window.IsVisible);
        Assert.True(window.ViewModel.IsDisposed);
        context.AssertHealthy();
    });

    private sealed class RecordingClipboard : IClipboardService
    {
        public string? Text { get; private set; }

        public bool SetText(string text)
        {
            Text = text;
            return true;
        }

        public bool SetSecret(string secret)
        {
            Text = secret;
            return true;
        }
    }
}
