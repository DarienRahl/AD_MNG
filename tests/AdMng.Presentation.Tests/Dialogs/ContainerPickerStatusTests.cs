using System.ComponentModel;
using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Testing;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Tests.Dialogs;

/// <summary>
/// The status line of "Browse for Container" (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-2 as built"): the
/// read of the tree when the dialog opens is its operation - without Stop, the tree reads its containers itself - and
/// the line says, for as long as it lasts, that the dialog has no session or that the content of the selected container
/// could not be read, with Retry. How the tree is opened at the start container and a container is chosen is covered,
/// unchanged, by <see cref="PickerTests"/>.
/// </summary>
public sealed class ContainerPickerStatusTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    /// <summary>How long a held read waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("en-US", "Reading the containers of contoso.test…")]
    [InlineData("pl-PL", "Wczytywanie kontenerów domeny contoso.test…")]
    public async Task TheReadOfTheTree_IsTheOperationOfTheDialog_WithoutStop_AndTheLineIsEmptyAfterwards(string culture, string working)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var picker = harness.Factory.CreateContainerPicker(harness.Session, harness.Company.ItOu);
        Assert.IsAssignableFrom<IHasPageStatus>(picker);
        Assert.Same(PageStatus.Empty, picker.Status.Current);
        Assert.Null(picker.Status.Lasting);
        var seen = Record(picker.Status);

        // The first read of the tree - the containers of the domain - waits for the test.
        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var hold = 1;
        harness.Session.BeforeOperation = operation =>
        {
            if (operation is { Kind: FakeOperationKind.Search, Search.Scope: SearchScope.OneLevel } && Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Release();
                release.Wait(HoldLimit);
            }
        };
        Task opening;
        try
        {
            opening = picker.InitializeAsync();
            Assert.True(await held.WaitAsync(Limit, Ct));

            // Working: what is read - and no Stop: the dialog's own Cancel closes it at any time.
            Assert.True(picker.IsLoading);
            Assert.True(picker.Status.IsWorking);
            Assert.Equal(PageStatusKind.Working, picker.Status.Current.Kind);
            Assert.Equal(working, picker.Status.Current.Message);
            Assert.Equal("ContainerPicker.Status.Reading", picker.Status.Current.Log.ToString());
            Assert.Null(picker.Status.Current.Action);
            Assert.False(picker.Status.CancelCommand.CanExecute(null));
            Assert.False(picker.Status.CloseCommand.CanExecute(null));
            Assert.True(picker.CancelCommand.CanExecute(null));
        }
        finally
        {
            release.Set();
        }

        await opening.WaitAsync(Limit, Ct);
        harness.Session.BeforeOperation = null;

        // The tree stands at the start container: nothing more to say.
        Assert.Equal([(PageStatusKind.Working, working), (PageStatusKind.Idle, string.Empty)], seen);
        Assert.Same(PageStatus.Empty, picker.Status.Current);
        Assert.Null(picker.Status.Lasting);
        Assert.False(picker.IsLoading);
        Assert.False(picker.Status.IsWorking);
        Assert.Equal(harness.Company.ItOu, picker.SelectedContainer?.Dn, StringComparer.OrdinalIgnoreCase);
        Assert.True(picker.OkCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("en-US", "Could not read the containers in “Pracownicy”: ", " Press Retry to try again. (AccessDenied)", "Retry")]
    [InlineData("pl-PL", "Nie udało się wczytać kontenerów w „Pracownicy”: ", " Wybierz Ponów, aby spróbować ponownie. (AccessDenied)", "Ponów")]
    public async Task AContainerThatCouldNotBeRead_IsTheStateOfTheDialog_WhileItIsSelected_WithTheReasonTheCodeAndRetry(
        string culture, string lead, string tail, string retryLabel)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var picker = harness.Factory.CreateContainerPicker(harness.Session, harness.Company.ItOu);
        var fail = true;
        harness.Session.BeforeOperation = operation =>
        {
            if (fail && operation.Kind == FakeOperationKind.Search && DistinguishedName.AreEqual(operation.DistinguishedName, harness.Company.EmployeesOu))
            {
                throw new DirectoryException(DirectoryErrorKind.AccessDenied, "00002098: SecErr: DSID-03150F94, problem 4003 (INSUFF_ACCESS_RIGHTS)");
            }
        };
        var seen = Record(picker.Status);

        await picker.InitializeAsync();

        // The walk to the start container ended at the container that could not be read: it is selected, and the line
        // says why its content is not shown - an error that cannot be closed, with Retry.
        var employees = Assert.IsType<ContainerNodeViewModel>(picker.SelectedContainer);
        Assert.Equal(harness.Company.EmployeesOu, employees.Dn, StringComparer.OrdinalIgnoreCase);
        Assert.NotNull(employees.LoadError);
        Assert.Equal([PageStatusKind.Working, PageStatusKind.Error], seen.Select(s => s.Kind));
        var failed = picker.Status.Current;
        Assert.StartsWith(lead, failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(tail, failed.Message, StringComparison.Ordinal);
        Assert.Contains(employees.LoadError, failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("DSID", failed.Message, StringComparison.Ordinal);
        Assert.Equal("ContainerPicker.Status.ReadFailed [AccessDenied]", failed.Log.ToString());
        Assert.False(failed.CanClose);
        Assert.Same(picker.Status.Lasting, failed);
        Assert.False(picker.Status.CloseCommand.CanExecute(null));
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal(retryLabel, retry.Label);
        Assert.Same(employees.RefreshCommand, retry.Command);

        // The container itself can still be chosen.
        Assert.True(picker.OkCommand.CanExecute(null));

        // The state belongs to the container the operator looks at: another one has nothing wrong with it.
        var domain = Assert.IsType<ContainerNodeViewModel>(Assert.Single(picker.Roots));
        domain.IsSelected = true;
        Assert.Same(domain, picker.SelectedContainer);
        Assert.Same(PageStatus.Empty, picker.Status.Current);
        Assert.Null(picker.Status.Lasting);
        employees.IsSelected = true;
        Assert.Equal(PageStatusKind.Error, picker.Status.Current.Kind);
        Assert.Equal(failed.Message, picker.Status.Current.Message);

        // Retry, with a domain controller that answers: the content is there and the state is over.
        fail = false;
        await ((IAsyncRelayCommand)retry.Command).ExecuteAsync(retry.Parameter);
        Assert.Null(employees.LoadError);
        Assert.Contains(employees.Children.OfType<ContainerNodeViewModel>(), child => DistinguishedName.AreEqual(child.Dn, harness.Company.ItOu));
        Assert.Same(PageStatus.Empty, picker.Status.Current);
        Assert.Null(picker.Status.Lasting);
    }

    [Fact]
    public async Task ADomainThatCouldNotBeRead_LeavesTheTreeEmpty_AndTheLineSaysWhy()
    {
        var harness = await B2Harness.CreateAsync();
        using var picker = harness.Factory.CreateContainerPicker(harness.Session, harness.Company.ItOu);
        harness.Session.BeforeOperation = operation =>
        {
            if (operation is { Kind: FakeOperationKind.Search, Search.Scope: SearchScope.OneLevel })
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer");
            }
        };

        await picker.InitializeAsync();
        harness.Session.BeforeOperation = null;

        // Only the domain is there - selected, without its containers - and the line names it.
        var domain = Assert.IsType<ContainerNodeViewModel>(Assert.Single(picker.Roots));
        Assert.Same(domain, picker.SelectedContainer);
        Assert.Empty(domain.Children.OfType<ContainerNodeViewModel>());
        var failed = picker.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Equal($"Could not read the containers in “contoso.test”: {domain.LoadError} Press Retry to try again. (ServerUnavailable)", failed.Message);
        Assert.False(failed.CanClose);
        Assert.False(picker.IsLoading);

        // Retry reads the containers of the domain.
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        await ((IAsyncRelayCommand)retry.Command).ExecuteAsync(retry.Parameter);
        Assert.NotEmpty(domain.Children.OfType<ContainerNodeViewModel>());
        Assert.Same(PageStatus.Empty, picker.Status.Current);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pl-PL")]
    public async Task WithoutASession_TheLineSaysSoFromTheStart_AndNothingIsRead(string culture)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        var session = harness.Session;
        await harness.Sessions.DisconnectAsync();
        var before = session.Operations.Count;
        using var picker = harness.Factory.CreateContainerPicker(session, harness.Company.ItOu);

        // Said before anything is pressed: an error that cannot be closed.
        var state = picker.Status.Current;
        Assert.Equal(PageStatusKind.Error, state.Kind);
        Assert.Equal(harness.Localizer[L.Errors.NotConnected], state.Message);
        Assert.Equal("Errors.NotConnected", state.Log.ToString());
        Assert.False(state.CanClose);
        Assert.Null(state.Action);
        var seen = Record(picker.Status);

        await picker.InitializeAsync();

        Assert.Empty(seen);
        Assert.Empty(picker.Roots);
        Assert.False(picker.IsLoading);
        Assert.False(picker.OkCommand.CanExecute(null));
        Assert.Equal(before, session.Operations.Count);
    }

    [Fact]
    public async Task TheEndOfTheSession_TakesTheLine_AlsoDuringTheRead_AndIsSaidInTheLanguageOfTheMoment()
    {
        // While the tree is read: the read ends without a word, the line says for good that the session ended.
        var harness = await B2Harness.CreateAsync();
        using (var reading = harness.Factory.CreateContainerPicker(harness.Session, harness.Company.ItOu))
        {
            using var held = new SemaphoreSlim(0);
            using var release = new ManualResetEventSlim();
            var hold = 1;
            harness.Session.BeforeOperation = operation =>
            {
                if (operation is { Kind: FakeOperationKind.Search, Search.Scope: SearchScope.OneLevel } && Interlocked.Exchange(ref hold, 0) == 1)
                {
                    held.Release();
                    release.Wait(HoldLimit);
                }
            };
            Task opening;
            List<(PageStatusKind Kind, string Message)> seen;
            try
            {
                opening = reading.InitializeAsync();
                Assert.True(await held.WaitAsync(Limit, Ct));
                Assert.True(reading.Status.IsWorking);
                seen = Record(reading.Status);

                await harness.Sessions.DisconnectAsync();

                Assert.True(reading.IsSessionEnded);
                Assert.False(reading.Status.IsWorking);
                Assert.Equal(PageStatusKind.Error, reading.Status.Current.Kind);
                Assert.Equal(harness.Localizer[L.WindowSession.Ended], reading.Status.Current.Message);
                Assert.False(reading.Status.Current.CanClose);
            }
            finally
            {
                release.Set();
            }

            await opening.WaitAsync(Limit, Ct);
            harness.Session.BeforeOperation = null;

            // Whatever the read brought or failed with afterwards: one message, and it stays.
            Assert.Equal([(PageStatusKind.Error, harness.Localizer[L.WindowSession.Ended])], seen);
            Assert.Equal("WindowSession.Ended", reading.Status.Current.Log.ToString());
        }

        // At rest, and after a language switch.
        harness = await B2Harness.CreateAsync();
        using var picker = harness.Factory.CreateContainerPicker(harness.Session, harness.Company.ItOu);
        await picker.InitializeAsync();
        Assert.Same(PageStatus.Empty, picker.Status.Current);
        await harness.Sessions.DisconnectAsync();
        Assert.Equal(harness.Localizer[L.WindowSession.Ended], picker.Status.Current.Message);
        var english = picker.Status.Current.Message;
        harness.Localizer.SetCulture(Polish);
        Assert.Equal(PageStatusKind.Error, picker.Status.Current.Kind);
        Assert.Equal(harness.Localizer[L.WindowSession.Ended], picker.Status.Current.Message);
        Assert.NotEqual(english, picker.Status.Current.Message);
    }

    [Fact]
    public async Task TheLogCopies_CarryKeysAndCodes_NeverAContainerTheDomainOrAServerText()
    {
        var harness = await B2Harness.CreateAsync();
        var logger = new ListLogger<PageStatusHolder>();
        var lists = new ObjectListServices(harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes, harness.Time, logger);
        var services = new DialogServices(
            harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes, harness.Time, harness.Dialogs, harness.Files,
            harness.Clipboard, harness.Notifications, harness.Coordinator, harness.Activities, lists, harness.Regional);
        var tree = new NavigationTreeViewModel(harness.Sessions, harness.Settings, harness.Localizer, harness.Dispatcher, harness.Errors, harness.Views, harness.Changes);
        using var picker = new ContainerPickerViewModel(services, tree, harness.Session, harness.Company.ItOu);
        harness.Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Search && DistinguishedName.AreEqual(operation.DistinguishedName, harness.Company.EmployeesOu))
            {
                throw new DirectoryException(DirectoryErrorKind.AccessDenied, "00002098: SecErr: DSID-03150F94 for OU=Pracownicy");
            }
        };

        await picker.InitializeAsync();
        harness.Session.BeforeOperation = null;
        Assert.Contains("Pracownicy", picker.Status.Current.Message, StringComparison.Ordinal);

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("ContainerPicker.Status.Reading", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("ContainerPicker.Status.ReadFailed", StringComparison.Ordinal) && m.Contains("AccessDenied", StringComparison.Ordinal));
        Assert.DoesNotContain("Pracownicy", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("contoso", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DSID", logger.AllText, StringComparison.Ordinal);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

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
}
