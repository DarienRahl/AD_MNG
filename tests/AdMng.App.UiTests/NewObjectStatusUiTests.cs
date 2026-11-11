using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Session;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// The status line of "New Object" (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-2 as built"), through the
/// real window over the fake directory: one line at the top - empty at rest, why nothing was created, "creating" without
/// Stop, a refused creation as an error, the end of the session - while what belongs to one field stays a note next to
/// it. A slow domain controller is a write the test holds and releases, a refusing one an injected failure; every state
/// that is asserted lasts until the test changes it. How objects are created is covered by <see cref="ObjectDialogTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class NewObjectStatusUiTests
{
    /// <summary>How long a held write waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    [Fact]
    public Task TheWindow_HasOneStatusLine_AtTheTop_AndWhatBelongsToAFieldIsANote() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, dialog) = await OpenAsync(context, ObjectKind.User, context.Company.ItOu);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");

        // One status line, named PageStatus, over the status of the window, at the top - above "Create in" and the
        // fields - of the fixed height.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        Assert.True(line.IsVisible);
        Assert.IsAssignableFrom<IHasPageStatus>(dialog);
        Assert.Same(dialog.Status.Current, line.Status);
        Assert.Same(dialog.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(line, window) < TopOf(window.KindIcon, window), "the status line is not above the content");
        Assert.True(TopOf(window.KindIcon, window) < TopOf(window.FirstNameBox, window), "the fields are not under \"Create in\"");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // Nothing was asked for: the line is there, and empty.
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(string.Empty, line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);

        // OK stays a plain button - a creation is not stopped from this window - and the default one; Cancel is still
        // the cancel button of the dialog.
        Assert.IsNotType<ActionButton>(window.CreateButton);
        Assert.Same(dialog.CreateCommand, window.CreateButton.Command);
        Assert.True(window.CreateButton.IsDefault);
        Assert.True(window.CreateButton.IsEnabled);
        Assert.True(window.CancelButton.IsCancel);
        Assert.True(window.CancelButton.IsEnabled);

        // What two password options mean together belongs to those options: a note next to them, shown for as long as
        // both are ticked - not a banner, and not the status line.
        Assert.False(window.PasswordOptionsNote.IsVisible);
        dialog.MustChangePassword = true;
        dialog.PasswordNeverExpires = true;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.True(window.PasswordOptionsNote.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), window.PasswordOptionsNote.Style);
        Assert.True(window.PasswordOptionsNoteText.IsVisible);
        Assert.Equal(dialog.PasswordOptionsWarning, window.PasswordOptionsNoteText.Text);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(window.PasswordOptionsNoteText));
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(string.Empty, line.Message);
        dialog.PasswordNeverExpires = false;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.False(window.PasswordOptionsNote.IsVisible);

        // OK without a name: nothing is created, and the line says why - an attention that can be closed. The fields
        // moved down by as much as the line grew.
        var fieldsTop = TopOf(window.FirstNameBox, window);
        ControlHost.Press(window.CreateButton);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Attention, "what is missing in the status line");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal("Attention", line.Title);
        Assert.Equal("Nothing was created: Enter a name.", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Null(dialog.CreatedDn);
        Assert.True(window.CreateButton.IsEnabled);
        window.UpdateLayout();
        Assert.Equal(fieldsTop + GrowthOf(line), TopOf(window.FirstNameBox, window), 0.5);

        // Closed: the line is empty and one line high again.
        ControlHost.Press(close);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Idle, "the result was closed");
        Assert.Equal(string.Empty, line.Message);
        window.UpdateLayout();
        Assert.Equal(fieldsTop, TopOf(window.FirstNameBox, window), 0.5);

        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task Creating_SaysSoWithoutStop_AndAnObjectThatWasCreatedClosesTheDialog() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, dialog) = await OpenAsync(context, ObjectKind.OrganizationalUnit, context.Company.CompanyOu);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var session = context.Directory.Session;
        var domain = session.Info.DomainDnsName;
        dialog.Name = "Projekty";
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var nameTop = TopOf(window.OuNameBox, window);

        // The write waits for the test, so "creating" lasts.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hold = 1;
        session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Add && Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Set();
                release.Wait(HoldLimit);
            }
        };
        try
        {
            ControlHost.Press(window.CreateButton);
            await UiWait.UntilAsync(() => held.IsSet && dialog.IsBusy && line.Kind == PageStatusKind.Working, "the object is being created");

            // Working: what is created and where - without Stop and without a close button: a creation is not stopped
            // half way from this window, and the window cannot be closed meanwhile.
            Assert.Equal("Working", line.Title);
            Assert.Equal($"Creating “Projekty” in {domain}…", line.Message);
            Assert.True(ring.IsActive);
            Assert.Equal(Visibility.Collapsed, action.Visibility);
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            Assert.False(window.CreateButton.IsEnabled);
            Assert.False(window.CancelButton.IsEnabled);
            AssertNoOldFeedback(window);

            // The line was there before: the fields did not move.
            window.UpdateLayout();
            Assert.Equal(nameTop, TopOf(window.OuNameBox, window));
        }
        finally
        {
            release.Set();
        }

        // Created: the dialog closes with its result - there is nobody left to read a "done" in it.
        await UiWait.UntilAsync(() => !window.IsVisible, "the dialog closed");
        session.BeforeOperation = null;
        Assert.True(window.Result);
        Assert.NotNull(dialog.CreatedDn);
        Assert.True(session.Exists(dialog.CreatedDn!));
        Assert.Null(dialog.Error);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task ACreationThatTheDomainControllerRefused_IsAnError_AndTheEndOfTheSessionLasts() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (window, dialog) = await OpenAsync(context, ObjectKind.OrganizationalUnit, context.Company.CompanyOu);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var session = context.Directory.Session;
        dialog.Name = "Projekty";
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        session.FailNext(FakeOperationKind.Add, dialog.TargetDn, DirectoryErrorKind.AccessDenied);

        ControlHost.Press(window.CreateButton);
        await UiWait.UntilAsync(() => !dialog.IsBusy && line.Kind == PageStatusKind.Error, "the creation was refused");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // What was tried, why it failed, the code - and the dialog stays open, as it was: it can be closed, corrected
        // and pressed again.
        Assert.Equal("Error", line.Title);
        Assert.Equal($"Could not create “Projekty”: {dialog.Error} (AccessDenied)", line.Message);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.True(window.IsVisible);
        Assert.Null(dialog.CreatedDn);
        Assert.True(window.CreateButton.IsEnabled);
        Assert.True(window.CancelButton.IsEnabled);
        AssertNoOldFeedback(window);

        // The session of the window ends: an error that cannot be closed takes the line, and nothing can be created.
        await context.Services.GetRequiredService<ISessionManager>().DisconnectAsync();
        await UiWait.UntilAsync(() => dialog.IsSessionEnded && line.Message == context.Localizer()[L.WindowSession.Ended], "the end of the session in the line");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(PageStatusKind.Error, line.Kind);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.False(window.CreateButton.IsEnabled);
        Assert.True(window.CancelButton.IsEnabled);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>The dialog for a new object of <paramref name="kind"/> in <paramref name="parentDn"/>, shown off-screen and initialized.</summary>
    private static async Task<(NewObjectWindow Window, NewObjectViewModel Dialog)> OpenAsync(UiTestContext context, ObjectKind kind, string parentDn)
    {
        await context.ConnectAsync();
        var dialog = context.Services.GetRequiredService<IDialogViewModelFactory>().CreateNewObject(kind, parentDn);
        var window = (NewObjectWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(dialog);
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();
        return (window, dialog);
    }

    /// <summary>The old places of feedback are gone from the window: no progress bar and no banner of the styles of before.</summary>
    private static void AssertNoOldFeedback(NewObjectWindow window)
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
