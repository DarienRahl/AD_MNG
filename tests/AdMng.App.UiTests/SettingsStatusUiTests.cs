using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Messaging;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Settings;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests;

/// <summary>
/// The status line of the settings window (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-2 as built"),
/// through the real window with a fake SMTP transport: one line above the tabs - empty at rest and on the tabs that
/// have nothing to say, the tests of the "E-mail messages" tab with Stop and how they ended while that tab is shown -
/// and the two banners of before as notes: why Save is not possible, what sending without TLS means. A slow mail
/// server is a test the test holds and releases; every state that is asserted lasts until the test changes it. The
/// SMTP password is never in the line. What the tabs hold is covered by <see cref="UserMessagesWindowTests"/> and
/// <see cref="ToolWindowTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class SettingsStatusUiTests
{
    private const string SmtpPassword = "Haslo-Testowe-1";

    /// <summary>How long a held call waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    [Fact]
    public Task TheWindow_HasOneStatusLine_AboveTheTabs_AndItsTwoBannersAreNotes() => WpfTestHost.RunAsync(async () =>
    {
        var (context, _) = await CreateAsync();
        await using var disposal = context;
        var (window, settings) = await OpenAsync(context);
        var messages = settings.Messages!;
        var line = window.PageStatus;

        // One status line, named PageStatus, over the status of the window, above the tabs, of the fixed height - and
        // empty: the window has nothing to say of its own.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        Assert.True(line.IsVisible);
        Assert.IsAssignableFrom<IHasPageStatus>(settings);
        Assert.Same(settings.Status.Current, line.Status);
        Assert.Same(settings.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(line, window) < TopOf(window.SettingsTabs, window), "the status line is not above the tabs");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(string.Empty, line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);

        // The view tells the view model which tab is shown: the "E-mail messages" tab is the page in front while it is.
        Assert.Equal(SettingsViewModel.MessagesTab, window.SettingsTabs.Items.IndexOf(window.MessagesTab));
        Assert.Equal(0, settings.SelectedTabIndex);
        Assert.Null(settings.Status.Front);
        window.SettingsTabs.SelectedItem = window.MessagesTab;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(SettingsViewModel.MessagesTab, settings.SelectedTabIndex);
        Assert.Same(messages.Status, settings.Status.Front);
        Assert.Equal(PageStatusKind.Idle, line.Kind);

        // The tests stay plain buttons - they never had a Cancel of their own; a test is stopped in the line. Save is
        // still the default button and Cancel the cancel button of the dialog.
        Assert.IsNotType<ActionButton>(window.TestConnectionButton);
        Assert.IsNotType<ActionButton>(window.SendTestMessageButton);
        Assert.True(window.SaveButton.IsDefault);
        Assert.True(window.CancelButton.IsCancel);

        // A value that is not valid: why Save is not possible is a note above the buttons - computed from the fields,
        // not a status - and the line still says nothing.
        Assert.False(window.ErrorNote.IsVisible);
        window.FromAddressBox.Text = "not an address";
        await UiWait.UntilAsync(() => settings.Error is not null && window.ErrorNote.IsVisible, "the note of the window");
        await UiTestSupport.WaitForIdleAsync();
        Assert.Same(Application.Current.FindResource("Ad.Note.Error"), window.ErrorNote.Style);
        Assert.True(window.ErrorMessage.IsVisible);
        Assert.True(window.ErrorMessage.IsDescendantOf(window.ErrorNote));
        Assert.Equal("Sender address: not a valid e-mail address.", window.ErrorMessage.Text);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(window.ErrorMessage));
        Assert.False(window.SaveButton.IsEnabled);
        Assert.True(TopOf(window.SettingsTabs, window) < TopOf(window.ErrorNote, window), "the note is not under the tabs");
        Assert.True(TopOf(window.ErrorNote, window) < TopOf(window.SaveButton, window), "the note is not above the buttons");
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(string.Empty, line.Message);
        window.FromAddressBox.Text = string.Empty;
        await UiWait.UntilAsync(() => settings.Error is null && !window.ErrorNote.IsVisible, "the note is gone with its reason");

        // Sending without TLS: what the choice means is a note next to it, with its confirmation under it.
        Assert.False(window.InsecureTlsPanel.IsVisible);
        messages.Tls = messages.TlsModes.Single(m => m.Value == SmtpTlsMode.None);
        await UiWait.UntilAsync(() => window.InsecureTlsPanel.IsVisible, "the note of the TLS choice");
        await UiTestSupport.WaitForIdleAsync();
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), window.InsecureTlsNote.Style);
        Assert.True(window.InsecureTlsText.IsVisible);
        Assert.StartsWith("Without TLS the messages travel unencrypted", window.InsecureTlsText.Text, StringComparison.Ordinal);
        Assert.True(window.InsecureTlsConfirmBox.IsVisible);
        Assert.False(window.InsecureTlsConfirmBox.IsDescendantOf(window.InsecureTlsNote));
        Assert.True(TopOf(window.InsecureTlsNote, window) < TopOf(window.InsecureTlsConfirmBox, window), "the confirmation is not under the note");
        Assert.Equal("Confirm the warning about sending without encryption.", window.ErrorMessage.Text);
        window.InsecureTlsConfirmBox.IsChecked = true;
        await UiWait.UntilAsync(() => settings.Error is null, "the confirmation");
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.SaveButton.IsEnabled);

        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task ATestOfTheConnection_SaysWorkingWithStop_ThenHowItEnded_AndOnlyWhileItsTabIsShown() => WpfTestHost.RunAsync(async () =>
    {
        var (context, transport) = await CreateAsync();
        await using var disposal = context;
        var (window, settings) = await OpenAsync(context);
        var messages = settings.Messages!;
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        window.SettingsTabs.SelectedItem = window.MessagesTab;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.True(window.TestConnectionButton.IsEnabled);
        var tabsTop = TopOf(window.SettingsTabs, window);

        // The test waits for the test, so "working" lasts - and "stopping" after Stop.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hold = 1;
        transport.BeforeTestConnection = () =>
        {
            if (Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Set();
                release.Wait(HoldLimit);
            }
        };
        try
        {
            ControlHost.Press(window.TestConnectionButton);
            await UiWait.UntilAsync(() => held.IsSet && messages.IsTesting && line.Kind == PageStatusKind.Working, "the connection is being tested");

            // Working: which server is tested - with Stop, and nothing to close; the tests of the tab wait.
            Assert.Equal("Working", line.Title);
            Assert.Equal("Testing the connection to smtp.contoso.test:587…", line.Message);
            Assert.True(ring.IsActive);
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Same(messages.Status.CancelCommand, action.Command);
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            Assert.False(window.TestConnectionButton.IsEnabled);
            Assert.False(window.SendTestMessageButton.IsEnabled);
            AssertNoOldFeedback(window);

            // The line was there before: the tabs did not move.
            window.UpdateLayout();
            Assert.Equal(tabsTop, TopOf(window.SettingsTabs, window));

            // Stop, pressed in the line: "Stopping…" at once - the test that is on its way still has to return.
            ControlHost.Press(action);
            await UiWait.UntilAsync(() => messages.Status.IsCancelling && line.Message == "Stopping…", "the test is stopping");
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal(Visibility.Collapsed, action.Visibility);
        }
        finally
        {
            release.Set();
        }

        await UiWait.UntilAsync(() => !messages.IsTesting && line.Kind == PageStatusKind.Attention, "the test was stopped");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: an attention that can be closed. The tabs moved down by as much as the line grew.
        Assert.Equal("Attention", line.Title);
        Assert.Equal("Testing the connection to smtp.contoso.test:587 was stopped.", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.True(window.TestConnectionButton.IsEnabled);
        Assert.False(window.TestDetailsText.IsVisible);
        window.UpdateLayout();
        Assert.Equal(tabsTop + GrowthOf(line), TopOf(window.SettingsTabs, window), 0.5);

        // Tested again, and the server answers: done - how, in the line; what it showed, in the tab.
        transport.TestResult = new SmtpConnectionTest
        {
            Success = true,
            Stage = SmtpTestStage.Done,
            IsEncrypted = true,
            TlsProtocol = "Tls13",
            AuthenticationMechanisms = ["LOGIN", "PLAIN"],
            AuthenticatedWith = "USER",
        };
        ControlHost.Press(window.TestConnectionButton);
        await UiWait.UntilAsync(() => !messages.IsTesting && line.Kind == PageStatusKind.Done, "the connection works");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal("Done", line.Title);
        Assert.Equal("The connection to smtp.contoso.test:587 works: encrypted (Tls13), authentication: USER.", line.Message);
        Assert.Equal(AppIcons.StatusOk, line.IconKey);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.True(window.TestDetailsText.IsVisible);
        Assert.Equal("Authentication mechanisms offered: LOGIN, PLAIN", window.TestDetailsText.Text);
        Assert.Equal(messages.TestDetails, window.TestDetailsText.Text);

        // Another tab has nothing to say: the line is empty there; back on the tab, its result is still said.
        window.SettingsTabs.SelectedItem = window.GeneralTab;
        await UiWait.UntilAsync(() => settings.Status.Front is null && line.Kind == PageStatusKind.Idle, "the line of the General tab");
        Assert.Equal(string.Empty, line.Message);
        window.SettingsTabs.SelectedItem = window.MessagesTab;
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Done, "the result of the tab is back");
        Assert.Equal("The connection to smtp.contoso.test:587 works: encrypted (Tls13), authentication: USER.", line.Message);

        // Closed in the line: empty again.
        ControlHost.Press(close);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Idle, "the result was closed");
        Assert.Equal(string.Empty, line.Message);
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task ATestThatFails_IsAnError_AndWhatIsMissingIsAttention_ThePasswordIsNeverInTheLine() => WpfTestHost.RunAsync(async () =>
    {
        var (context, transport) = await CreateAsync();
        await using var disposal = context;
        var (window, settings) = await OpenAsync(context);
        var messages = settings.Messages!;
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        window.SettingsTabs.SelectedItem = window.MessagesTab;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // The server does not offer STARTTLS: an error with the stage and the reason of the transport.
        transport.TestResult = new SmtpConnectionTest
        {
            Stage = SmtpTestStage.Tls,
            Failure = new UserMessageDelivery(UserMessageDeliveryOutcome.TransportFailed, "smtp.notSupported", Hint: "smtp.startTlsMissing"),
        };
        ControlHost.Press(window.TestConnectionButton);
        await UiWait.UntilAsync(() => !messages.IsTesting && line.Kind == PageStatusKind.Error, "the test failed");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal("Error", line.Title);
        Assert.StartsWith(
            "The connection to smtp.contoso.test:587 does not work: Stage \"TLS\" failed: smtp.notSupported – The server does not offer STARTTLS.",
            line.Message,
            StringComparison.Ordinal);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.False(window.TestDetailsText.IsVisible);

        // A user name without a password in the box: nothing is tested, and the line says what is missing.
        messages.Authentication = messages.Authentications.Single(a => a.Value == SmtpAuthentication.Basic);
        messages.SmtpUserName = "helpdesk@contoso.test";
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        ControlHost.Press(window.TestConnectionButton);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Attention, "what is missing in the status line");
        Assert.Equal("The connection was not tested: Enter the SMTP password (kept in memory only until AD Manager closes).", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(Visibility.Visible, close.Visibility);

        // With the password the test runs; the box is cleared and neither the password nor the user name is in the line.
        transport.TestResult = new SmtpConnectionTest { Success = true, Stage = SmtpTestStage.Done, IsEncrypted = true, TlsProtocol = "Tls13", AuthenticatedWith = "USER" };
        window.MessagesPasswordBox.Password = SmtpPassword;
        ControlHost.Press(window.TestConnectionButton);
        await UiWait.UntilAsync(() => !messages.IsTesting && line.Kind == PageStatusKind.Done, "the connection works");
        Assert.Equal(SmtpPassword, transport.LastCredential!.Password);
        Assert.Equal(string.Empty, window.MessagesPasswordBox.Password);
        Assert.DoesNotContain(SmtpPassword, line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("helpdesk", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SmtpPassword, AutomationProperties.GetName(line), StringComparison.Ordinal);
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>A context whose direct SMTP transport is a fake, with an SMTP server and a test address in the settings.</summary>
    private static async Task<(UiTestContext Context, FakeUserMessageTransport Transport)> CreateAsync()
    {
        var transport = new FakeUserMessageTransport();
        var context = await UiTestContext.CreateAsync(configure: services =>
            services.Replace(ServiceDescriptor.Singleton<IDirectSmtpTransportFactory>(transport)));
        await context.Services.GetRequiredService<ISettingsStore>().UpdateAsync(s =>
        {
            s.UserMessages.Smtp.Host = "smtp.contoso.test";
            s.UserMessages.TestAddress = "operator@contoso.test";
        }, TestContext.Current.CancellationToken);
        return (context, transport);
    }

    private static async Task<(SettingsWindow Window, SettingsViewModel Settings)> OpenAsync(UiTestContext context)
    {
        var settings = context.Services.GetRequiredService<IDialogViewModelFactory>().CreateSettings();
        var window = (SettingsWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(settings);
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.NotNull(settings.Messages);
        Assert.Null(settings.Error);
        return (window, settings);
    }

    /// <summary>The old places of feedback are gone from the window: no progress bar and no banner of the styles of before.</summary>
    private static void AssertNoOldFeedback(SettingsWindow window)
    {
        var errorBar = Application.Current.FindResource("Ad.ErrorBar");
        var warningBar = Application.Current.FindResource("Ad.WarningBar");
        Assert.Empty(UiWait.Descendants<ProgressBar>(window));
        Assert.DoesNotContain(
            UiWait.Descendants<Border>(window),
            border => ReferenceEquals(border.Style, errorBar) || ReferenceEquals(border.Style, warningBar));
        Assert.Same(window.PageStatus, Assert.Single(UiWait.Descendants<StatusLine>(window)));
    }

    /// <summary>
    /// How much higher than one line the status line is right now: a result that does not fit one line wraps (to three
    /// lines at most), and what lies under the line moves down by as much - by nothing when the result fits.
    /// </summary>
    private static double GrowthOf(StatusLine line)
    {
        var growth = line.ActualHeight - (double)Application.Current.FindResource("Ad.Size.StatusLine");
        Assert.InRange(growth, -0.5, 40.5);
        return growth;
    }

    private static double TopOf(FrameworkElement element, UIElement relativeTo) =>
        element.TranslatePoint(new Point(0, 0), relativeTo).Y;
}
