using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Directory;
using AdMng.Core.Messaging;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;
using AdMng.Presentation.UserMessages;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests;

/// <summary>
/// The status line of the message window (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-2 as built"),
/// through the real window over the fake directory and a fake SMTP transport: one line at the top - empty in the
/// editor, the preparation of the preview with Stop and the Preview button as its Cancel, the counts of the preview,
/// the send with the part done and the Send button as its Cancel, how it ended and the counts of the report, a read
/// that failed, the end of the session - and the findings of the template check as a note. A slow domain controller or
/// mail server is a call the test holds and releases; every state that is asserted lasts until the test changes it.
/// How messages are planned and sent is covered by <see cref="UserMessagesWindowTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class UserMessagesStatusUiTests
{
    /// <summary>How long a held call waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    [Fact]
    public Task TheWindow_HasOneStatusLine_AtTheTop_EmptyInTheEditor_AndTheFindingsOfTheTemplateAreANote() => WpfTestHost.RunAsync(async () =>
    {
        var (context, _, _) = await CreateAsync();
        await using var disposal = context;
        var (window, composer) = await OpenAsync(context);
        var line = window.PageStatus;

        // One status line, named PageStatus, over the status of the window, at the top - above where the messages go
        // and the editor - of the fixed height.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        Assert.True(line.IsVisible);
        Assert.IsAssignableFrom<IHasPageStatus>(composer);
        Assert.Same(composer.Status.Current, line.Status);
        Assert.Same(composer.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(line, window) < TopOf(window.TransportText, window), "the status line is not above the transport");
        Assert.True(TopOf(window.TransportText, window) < TopOf(window.ComposePanel, window), "the transport is not above the editor");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // Nothing was asked for: the line is there, and empty.
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(string.Empty, line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));

        // "Preview" and "Send" are action buttons over the commands of before and the Cancel of the window's operation;
        // the paste button is one over the editor's. The window's own Close is still its cancel button.
        Assert.Equal(ActionButtonState.Idle, window.PlanButton.State);
        Assert.Same(composer.PlanCommand, window.PlanButton.Command);
        Assert.Same(composer.Status.CancelCommand, window.PlanButton.CancelCommand);
        Assert.Same(Application.Current.FindResource("Ad.AccentDialogActionButton"), window.PlanButton.Style);
        Assert.True(window.PlanButton.IsVisible);
        Assert.True(window.PlanButton.IsEnabled);
        Assert.Same(composer.SendCommand, window.SendButton.Command);
        Assert.Same(composer.Status.CancelCommand, window.SendButton.CancelCommand);
        Assert.False(window.SendButton.IsVisible);
        Assert.Equal(ActionButtonState.Idle, window.PasteFormattedButton.State);
        Assert.Same(composer.PasteFormattedCommand, window.PasteFormattedButton.Command);
        Assert.Same(composer.EditorStatus.CancelCommand, window.PasteFormattedButton.CancelCommand);
        Assert.True(window.CloseButton.IsCancel);

        // No images: neither the strip nor its "busy" mark nor the note about missing ones.
        Assert.False(composer.ShowTemplateImages);
        Assert.Equal(Visibility.Collapsed, window.TemplateImagesSection.Visibility);
        Assert.False(window.ImagesRing.IsActive);
        Assert.Equal(Visibility.Collapsed, window.TransportFitBar.Visibility);

        // A field that is never sent: the finding of the template check is a note of the editor - with the note
        // style, next to the text it is about - and the line still says nothing.
        Assert.False(window.TemplateErrorsNote.IsVisible);
        window.BodyBox.Text = "Hasło: {unicodePwd}";
        await UiWait.UntilAsync(() => composer.TemplateErrors is not null && window.TemplateErrorsNote.IsVisible, "the finding of the template check");
        Assert.Same(Application.Current.FindResource("Ad.Note.Error"), window.TemplateErrorsNote.Style);
        Assert.True(window.TemplateErrorsText.IsVisible);
        Assert.Equal(composer.TemplateErrors, window.TemplateErrorsText.Text);
        Assert.Contains("{unicodePwd}", window.TemplateErrorsText.Text, StringComparison.Ordinal);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(string.Empty, line.Message);

        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task PreparingThePreview_SaysWorkingWithStop_ThePreviewButtonIsItsCancel_AndThenTheLineShowsTheCounts() => WpfTestHost.RunAsync(async () =>
    {
        var (context, _, _) = await CreateAsync();
        await using var disposal = context;
        var (window, composer) = await OpenAsync(context);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var session = context.Directory.Session;
        var editorTop = TopOf(window.ComposePanel, window);

        // The first read of the accounts waits for the test, so "working" lasts - and "stopping" after Cancel.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hold = 1;
        session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Search && Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Set();
                release.Wait(HoldLimit);
            }
        };
        try
        {
            ControlHost.Press(window.PlanButton);
            await UiWait.UntilAsync(() => held.IsSet && line.Kind == PageStatusKind.Working, "the preview is being prepared");

            // Working: what is read and how many accounts - with Stop, and nothing to close.
            Assert.Equal("Working", line.Title);
            Assert.Equal("Reading the accounts (2) and preparing the messages…", line.Message);
            Assert.True(ring.IsActive);
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Equal(Visibility.Collapsed, close.Visibility);

            // The Preview button is the Cancel of the preparation: enabled, and there is no separate Stop button or bar.
            Assert.Equal(ActionButtonState.Running, window.PlanButton.State);
            Assert.True(window.PlanButton.IsEnabled);
            Assert.DoesNotContain(
                UiWait.Descendants<Button>(window),
                other => other.IsVisible && other is not ActionButton && !other.IsDescendantOf(line)
                    && (AutomationProperties.GetName(other) == "Stop" || other.Content as string == "Stop"));
            AssertNoOldFeedback(window);

            // The line was there before: the editor did not move.
            window.UpdateLayout();
            Assert.Equal(editorTop, TopOf(window.ComposePanel, window));

            // Cancel where you started: "Stopping…" on the button and in the line at once - the read that is on its
            // way to the domain controller still has to return.
            ControlHost.Press(window.PlanButton);
            await UiWait.UntilAsync(() => window.PlanButton.State == ActionButtonState.Cancelling && composer.Status.IsCancelling, "the preparation is stopping");
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Stopping…", line.Message);
            Assert.Equal(Visibility.Collapsed, action.Visibility);
            Assert.True(window.PlanButton.IsEnabled);
        }
        finally
        {
            release.Set();
        }

        await UiWait.UntilAsync(() => !composer.IsBusy && line.Kind == PageStatusKind.Attention, "the preparation was stopped");
        session.BeforeOperation = null;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: what was stopped, what that means, and Retry. The editor is as it was, moved down by as much as the
        // line grew.
        Assert.Equal("Attention", line.Title);
        Assert.Equal("Preparing the messages was stopped: no message was prepared. Press Retry to try again.", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, window.PlanButton.State);
        Assert.True(window.PlanButton.IsEnabled);
        Assert.Equal(UserMessageStage.Compose, composer.Stage);
        Assert.True(window.ComposePanel.IsVisible);
        window.UpdateLayout();
        Assert.Equal(editorTop + GrowthOf(line), TopOf(window.ComposePanel, window), 0.5);

        // Retry, pressed in the line, prepares the preview: the line shows its counts - and they are not a second
        // time in the page - and "Send" took the place of "Preview".
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => composer.Stage == UserMessageStage.Preview && !composer.IsBusy && line.Kind == PageStatusKind.Idle, "the preview");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(composer.SummaryText, line.Message);
        Assert.StartsWith("Ready: ", line.Message, StringComparison.Ordinal);
        Assert.Equal(2, composer.SendCount);
        Assert.Equal(AppIcons.StatusInfo, line.IconKey);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.True(window.PreviewPanel.IsVisible);
        Assert.Equal(2, window.ItemsList.Items.Count);
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == composer.SummaryText);
        Assert.False(window.PlanButton.IsVisible);
        Assert.True(window.SendButton.IsVisible);
        Assert.Equal(ActionButtonState.Idle, window.SendButton.State);
        Assert.Equal("Send (2)", window.SendButton.Content);
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task Sending_ShowsThePartDone_TheSendButtonIsItsCancel_AndTheLineSaysHowItEnded_ThenTheCountsOfTheReport() => WpfTestHost.RunAsync(async () =>
    {
        var (context, dialogs, transport) = await CreateAsync();
        await using var disposal = context;
        var (window, composer) = await OpenAsync(context);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var domain = context.Directory.Session.Info.DomainDnsName;
        await composer.PlanCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => composer.Stage == UserMessageStage.Preview && window.SendButton.IsVisible, "the preview");
        Assert.Equal(2, composer.SendCount);
        var headerTop = TopOf(window.TransportText, window);

        // The second message waits for the test, so "1 of 2" lasts.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hold = 1;
        transport.BeforeSend = message =>
        {
            if (message.To[0].Address.StartsWith("lukasz", StringComparison.Ordinal) && Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Set();
                release.Wait(HoldLimit);
            }
        };
        dialogs.ConfirmAnswer = true;
        try
        {
            ControlHost.Press(window.SendButton);
            await UiWait.UntilAsync(
                () => held.IsSet && composer.Stage == UserMessageStage.Sending && line.Message == "Sending the messages: 1 of 2…",
                "the first message was sent and the second one waits");

            // Working: how far the send is - the ring shows the part done and stands still - with Stop.
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Working", line.Title);
            Assert.Equal(0.5, line.ProgressFraction, 5);
            Assert.Equal(0.5, ring.Fraction, 5);
            Assert.True(ring.IsActive);
            Assert.False(ring.IsTurning, "a ring with a known part done turns");
            Assert.Equal("Stop", Assert.IsType<string>(action.Content));
            Assert.Equal(Visibility.Collapsed, close.Visibility);

            // The Send button stays and is the Cancel of the send; the stage says what it is. There is no separate
            // Stop button and no progress bar.
            Assert.True(window.SendingPanel.IsVisible);
            Assert.True(window.SendingHint.IsVisible);
            Assert.True(window.SendButton.IsVisible);
            Assert.Equal(ActionButtonState.Running, window.SendButton.State);
            Assert.True(window.SendButton.IsEnabled);
            Assert.Same(composer.Status.CancelCommand, window.SendButton.CancelCommand);
            Assert.DoesNotContain(
                UiWait.Descendants<Button>(window),
                other => other.IsVisible && other is not ActionButton && !other.IsDescendantOf(line)
                    && (AutomationProperties.GetName(other) == "Stop" || other.Content as string == "Stop"));
            AssertNoOldFeedback(window);

            // The line was there before: what lies under it did not move.
            window.UpdateLayout();
            Assert.Equal(headerTop, TopOf(window.TransportText, window));
        }
        finally
        {
            release.Set();
        }

        await UiWait.UntilAsync(() => composer.Stage == UserMessageStage.Report && line.Kind == PageStatusKind.Done, "the messages were sent");
        transport.BeforeSend = null;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Done: how many, for the users of which domain, and where the report is - until it is closed.
        Assert.Equal("Done", line.Title);
        Assert.Equal($"The server accepted 2 messages for users of {domain}. The report is in the list below.", line.Message);
        Assert.Equal(AppIcons.StatusOk, line.IconKey);
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(2, transport.Accepted.Count);
        Assert.True(window.ReportPanel.IsVisible);
        Assert.True(window.ReportHeader.IsVisible);
        Assert.Equal(2, window.ReportList.Items.Count);
        Assert.False(window.SendButton.IsVisible);
        window.UpdateLayout();
        Assert.Equal(headerTop + GrowthOf(line), TopOf(window.TransportText, window), 0.5);

        // Closed: the counts of the report - in the line, and not a second time in the page.
        ControlHost.Press(close);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Idle, "the result was closed");
        Assert.Equal(composer.ReportSummary, line.Message);
        Assert.StartsWith("Accepted by the server: 2 · ", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == composer.ReportSummary);
        window.UpdateLayout();
        Assert.Equal(headerTop, TopOf(window.TransportText, window), 0.5);
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task AReadThatFails_IsAnErrorWithRetry_AndTheEndOfTheSessionLasts() => WpfTestHost.RunAsync(async () =>
    {
        var (context, _, _) = await CreateAsync();
        await using var disposal = context;
        var (window, composer) = await OpenAsync(context);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var session = context.Directory.Session;

        // The domain controller does not answer the read of the accounts.
        var fail = 1;
        session.BeforeOperation = operation =>
        {
            if (Volatile.Read(ref fail) == 1 && operation.Kind == FakeOperationKind.Search)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer");
            }
        };
        ControlHost.Press(window.PlanButton);
        await UiWait.UntilAsync(() => !composer.IsBusy && line.Kind == PageStatusKind.Error, "the read failed");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // What was tried, the reason, what to do, the code - with Retry; a result that can be closed: the editor is
        // as it was.
        Assert.Equal("Error", line.Title);
        Assert.NotNull(composer.Error);
        Assert.Equal($"Could not read the accounts: {composer.Error} Press Retry to try again. (ServerUnavailable)", line.Message);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(UserMessageStage.Compose, composer.Stage);
        Assert.Equal(ActionButtonState.Idle, window.PlanButton.State);
        Assert.True(window.PlanButton.IsEnabled);
        AssertNoOldFeedback(window);

        // Retry, pressed in the line, once the domain controller answers again.
        Volatile.Write(ref fail, 0);
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => composer.Stage == UserMessageStage.Preview && !composer.IsBusy && line.Kind == PageStatusKind.Idle, "the preview");
        session.BeforeOperation = null;
        Assert.Equal(composer.SummaryText, line.Message);

        // The session of the window ends: an error that cannot be closed takes the line. The messages that are
        // prepared can still be sent; nothing can be read again.
        await context.Services.GetRequiredService<ISessionManager>().DisconnectAsync();
        await UiWait.UntilAsync(() => composer.IsSessionEnded && line.Kind == PageStatusKind.Error, "the end of the session in the line");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(context.Localizer()[L.WindowSession.Ended], line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.False(composer.PlanCommand.CanExecute(null));
        Assert.True(window.SendButton.IsEnabled);
        Assert.True(window.CloseButton.IsEnabled);
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>A connected context whose direct SMTP transport is a fake (messages go out at once, ten a second).</summary>
    private static async Task<(UiTestContext Context, TestDialogService Dialogs, FakeUserMessageTransport Transport)> CreateAsync()
    {
        var transport = new FakeUserMessageTransport();
        var (context, dialogs) = await TestDialogService.CreateContextAsync(configure: services =>
            services.Replace(ServiceDescriptor.Singleton<IDirectSmtpTransportFactory>(transport)));
        await context.ConnectAsync();
        await context.Services.GetRequiredService<ISettingsStore>().UpdateAsync(s =>
        {
            s.UserMessages.Smtp.Host = "smtp.contoso.test";
            s.UserMessages.TestAddress = "operator@contoso.test";
            s.UserMessages.MessagesPerMinute = 600;
        }, TestContext.Current.CancellationToken);
        return (context, dialogs, transport);
    }

    /// <summary>The message window for two accounts with a free message (both get one), in the editor.</summary>
    private static async Task<(UserMessagesWindow Window, UserMessageComposerViewModel Composer)> OpenAsync(UiTestContext context)
    {
        var company = context.Company;
        var composer = context.Services.GetRequiredService<IDialogViewModelFactory>()
            .CreateUserMessageComposer(new UserMessageRequest([company.JanKowalski, company.LukaszWisniewski], BuiltInMessageTemplates.Custom));
        var window = (UserMessagesWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(composer);
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        window.SubjectBox.Text = "Przerwa serwisowa w sobotę";
        window.BodyBox.Text = "Dzień dobry {givenName},\nw sobotę serwery będą niedostępne.";
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Null(composer.TemplateErrors);
        Assert.Equal(UserMessageStage.Compose, composer.Stage);
        return (window, composer);
    }

    /// <summary>The old places of feedback are gone from the window: no progress bar and no banner of the styles of before.</summary>
    private static void AssertNoOldFeedback(UserMessagesWindow window)
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
