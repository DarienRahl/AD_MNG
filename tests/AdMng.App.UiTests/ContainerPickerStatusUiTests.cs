using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Session;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// The status line of "Browse for Container" (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-2 as built"),
/// through the real window over the fake directory: one line at the top - the read of the tree when the dialog opens
/// (without Stop), empty once the tree stands, a container whose content could not be read with Retry, the end of the
/// session. A slow domain controller is a read the test holds and releases, a refusing one an exception of the fake
/// session; every state that is asserted lasts until the test changes it. How a container is chosen is covered by
/// <see cref="ObjectDialogTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ContainerPickerStatusUiTests
{
    /// <summary>How long a held read waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    [Fact]
    public Task TheWindow_HasOneStatusLine_AtTheTop_EmptyOnceTheTreeStands() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var session = await context.ConnectAsync();
        var (window, picker) = Open(context, session);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();
        var line = window.PageStatus;

        // One status line, named PageStatus, over the status of the dialog, at the top - above the prompt and the
        // tree - of the fixed height.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        Assert.True(line.IsVisible);
        Assert.IsAssignableFrom<IHasPageStatus>(picker);
        Assert.Same(picker.Status.Current, line.Status);
        Assert.Same(picker.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(line, window) < TopOf(window.PromptText, window), "the status line is not above the prompt");
        Assert.True(TopOf(window.PromptText, window) < TopOf(window.ContainerTree, window), "the prompt is not above the tree");
        Assert.True(TopOf(window.ContainerTree, window) < TopOf(window.SelectedPathText, window), "the tree is not above the selected path");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // The tree stands at the start container: the line has nothing to say; what is selected is said under the tree.
        Assert.Equal(context.Company.ItOu, picker.SelectedContainer?.Dn, ignoreCase: true);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(string.Empty, line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(picker.SelectedPath, window.SelectedPathText.Text);

        // OK is still the default button and can be pressed.
        Assert.True(window.OkButton.IsDefault);
        Assert.True(window.OkButton.IsEnabled);
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheReadOfTheTree_IsSaidInTheLine_WithoutStop_AndTheDialogCanBeUsedMeanwhile() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var session = await context.ConnectAsync();
        var directory = context.Directory.Session;
        var domain = session.Info.DomainDnsName;

        // The first read of the tree - the containers of the domain - waits for the test, so "reading" lasts.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hold = 1;
        directory.BeforeOperation = operation =>
        {
            if (operation is { Kind: FakeOperationKind.Search, Search.Scope: SearchScope.OneLevel } && Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Set();
                release.Wait(HoldLimit);
            }
        };
        ContainerPickerWindow window;
        ContainerPickerViewModel picker;
        StatusLine line;
        double treeTop;
        try
        {
            (window, picker) = Open(context, session);
            line = window.PageStatus;
            await UiWait.UntilAsync(() => held.IsSet && line.Kind == PageStatusKind.Working, "the tree is being read");
            var action = ControlHost.Part<Button>(line, "PART_ActionButton");
            var close = ControlHost.Part<Button>(line, "PART_CloseButton");
            var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");

            // Working: what is read - no Stop (the tree reads its containers itself) and nothing to close. The dialog's
            // own Cancel is there for as long as the read lasts.
            Assert.Equal("Working", line.Title);
            Assert.Equal($"Reading the containers of {domain}…", line.Message);
            Assert.True(ring.IsActive);
            Assert.False(line.HasAction);
            Assert.Equal(Visibility.Collapsed, action.Visibility);
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            Assert.True(picker.CancelCommand.CanExecute(null));
            AssertNoOldFeedback(window);
            window.UpdateLayout();
            treeTop = TopOf(window.ContainerTree, window);
        }
        finally
        {
            release.Set();
        }

        await UiWait.InitializedAsync(window.Initialization);
        directory.BeforeOperation = null;
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Idle, "the tree stands");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // The tree stands: the line is empty, and the tree did not move when the read ended.
        Assert.Equal(string.Empty, line.Message);
        Assert.Equal(context.Company.ItOu, picker.SelectedContainer?.Dn, ignoreCase: true);
        window.UpdateLayout();
        Assert.Equal(treeTop, TopOf(window.ContainerTree, window));
        context.AssertHealthy();
    });

    [Fact]
    public Task AContainerThatCouldNotBeRead_IsAnErrorWithRetry_WhileItIsSelected_AndTheEndOfTheSessionLasts() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var session = await context.ConnectAsync();
        var directory = context.Directory.Session;

        // The domain controller refuses the content of OU=Pracownicy - on the way to the start container.
        var fail = 1;
        directory.BeforeOperation = operation =>
        {
            if (Volatile.Read(ref fail) == 1 && operation.Kind == FakeOperationKind.Search
                && DistinguishedName.AreEqual(operation.DistinguishedName, context.Company.EmployeesOu))
            {
                throw new DirectoryException(DirectoryErrorKind.AccessDenied, "access to the container was denied");
            }
        };
        var (window, picker) = Open(context, session);
        await UiWait.InitializedAsync(window.Initialization);
        var line = window.PageStatus;
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Error, "the container could not be read");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");

        // The walk ended at the container that could not be read: it is selected, and the line says why - what was
        // tried, the reason, what to do, the code - with Retry, and it cannot be closed.
        var employees = Assert.IsType<ContainerNodeViewModel>(picker.SelectedContainer);
        Assert.Equal(context.Company.EmployeesOu, employees.Dn, ignoreCase: true);
        Assert.NotNull(employees.LoadError);
        Assert.Equal("Error", line.Title);
        Assert.Equal($"Could not read the containers in “Pracownicy”: {employees.LoadError} Press Retry to try again. (AccessDenied)", line.Message);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Same(employees.RefreshCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.True(window.OkButton.IsEnabled);
        AssertNoOldFeedback(window);

        // Retry, pressed in the line, reads the container: its content is there and the line is empty again.
        Volatile.Write(ref fail, 0);
        await UiWait.UntilAsync(() => action.IsEnabled, "the action of the line can be pressed");
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => employees.LoadError is null && !employees.IsLoading && line.Kind == PageStatusKind.Idle, "the container was read");
        directory.BeforeOperation = null;
        Assert.Equal(string.Empty, line.Message);
        Assert.Contains(employees.Children.OfType<ContainerNodeViewModel>(), child => DistinguishedName.AreEqual(child.Dn, context.Company.ItOu));

        // The session of the dialog ends: an error that cannot be closed takes the line; the dialog can still be closed.
        await context.Services.GetRequiredService<ISessionManager>().DisconnectAsync();
        await UiWait.UntilAsync(() => picker.IsSessionEnded && line.Kind == PageStatusKind.Error, "the end of the session in the line");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(context.Localizer()[L.WindowSession.Ended], line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.True(picker.CancelCommand.CanExecute(null));
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>The picker with OU=IT as its start container, shown off-screen; its tree is being read.</summary>
    private static (ContainerPickerWindow Window, ContainerPickerViewModel Picker) Open(UiTestContext context, IDirectorySession session)
    {
        var picker = context.Services.GetRequiredService<IDialogViewModelFactory>().CreateContainerPicker(session, context.Company.ItOu);
        var window = (ContainerPickerWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(picker);
        UiTestSupport.ShowOffScreen(window);
        return (window, picker);
    }

    /// <summary>The old places of feedback are gone from the window: no progress bar and no banner of the styles of before.</summary>
    private static void AssertNoOldFeedback(ContainerPickerWindow window)
    {
        var errorBar = Application.Current.FindResource("Ad.ErrorBar");
        var warningBar = Application.Current.FindResource("Ad.WarningBar");
        Assert.Empty(UiWait.Descendants<ProgressBar>(window));
        Assert.DoesNotContain(
            UiWait.Descendants<Border>(window),
            border => ReferenceEquals(border.Style, errorBar) || ReferenceEquals(border.Style, warningBar));
        Assert.Same(window.PageStatus, Assert.Single(UiWait.Descendants<StatusLine>(window)));
    }

    private static double TopOf(FrameworkElement element, UIElement relativeTo) =>
        element.TranslatePoint(new Point(0, 0), relativeTo).Y;
}
