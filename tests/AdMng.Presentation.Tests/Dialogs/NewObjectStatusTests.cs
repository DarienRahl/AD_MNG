using System.ComponentModel;
using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Dialogs;

/// <summary>
/// The status line of "New Object" (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-2 as built"): creating the
/// object is the one operation of the window - without Stop - and the line says why nothing was created or why the
/// creation failed; an object that was created says nothing (the dialog closes); as the state that lasts, the window
/// has no session. A password is never part of a text or of its log copy. How objects are created is covered,
/// unchanged, by <see cref="NewObjectTests"/>.
/// </summary>
public sealed class NewObjectStatusTests
{
    private const string Typed = "Start!Haslo2026";

    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    /// <summary>How long a held call waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("en-US", "Nothing was created: Enter a name.")]
    [InlineData("pl-PL", "Niczego nie utworzono: Wprowadź nazwę.")]
    public async Task WhatWasNotStarted_IsSaidAsNothingWasCreated_WithTheReason(string culture, string expected)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var dialog = harness.Factory.CreateNewObject(ObjectKind.OrganizationalUnit, harness.Company.CompanyOu);
        Assert.IsAssignableFrom<IHasPageStatus>(dialog);
        await dialog.InitializeAsync();

        // At rest the line says nothing, and nothing lasts.
        Assert.Same(PageStatus.Empty, dialog.Status.Current);
        Assert.Null(dialog.Status.Lasting);
        var seen = Record(dialog.Status);

        await dialog.CreateCommand.ExecuteAsync(null);

        // An attention the operator can close - never "working" in between.
        var said = dialog.Status.Current;
        Assert.Equal(PageStatusKind.Attention, said.Kind);
        Assert.Equal(expected, said.Message);
        Assert.Equal("NewObject.Status.NotCreated", said.Log.ToString());
        Assert.Null(said.Action);
        Assert.True(said.CanClose);
        Assert.Equal([(PageStatusKind.Attention, expected)], seen);
        Assert.EndsWith(dialog.Error!, said.Message, StringComparison.Ordinal);
        Assert.False(dialog.IsBusy);
        Assert.Null(dialog.CreatedDn);

        // Closed: the line is empty again.
        dialog.Status.CloseCommand.Execute(null);
        Assert.Same(PageStatus.Empty, dialog.Status.Current);
    }

    [Fact]
    public async Task APasswordThatIsMissingOrDoesNotMatch_IsSaidTheSameWay_WithoutThePassword()
    {
        var harness = await B2Harness.CreateAsync();
        using var dialog = harness.Factory.CreateNewObject(ObjectKind.User, harness.Company.ItOu);
        dialog.FirstName = "Anna";
        dialog.LastName = "Nowa";
        var seen = Record(dialog.Status);

        await dialog.CreateCommand.ExecuteAsync(null);
        Assert.Equal("Nothing was created: Enter a password (or create the account disabled).", dialog.Status.Current.Message);

        dialog.PasswordInput = new TestPasswordInput(Typed);
        dialog.ConfirmPasswordInput = new TestPasswordInput("Inne!Haslo2026");
        await dialog.CreateCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Attention, dialog.Status.Current.Kind);
        Assert.Equal("Nothing was created: The passwords do not match.", dialog.Status.Current.Message);

        Assert.All(seen, s =>
        {
            Assert.Equal(PageStatusKind.Attention, s.Kind);
            Assert.DoesNotContain("Haslo", s.Message, StringComparison.Ordinal);
        });
        Assert.Null(dialog.CreatedDn);
    }

    [Theory]
    [InlineData("en-US", "Creating “Projekty” in contoso.test…")]
    [InlineData("pl-PL", "Tworzenie obiektu „Projekty” w domenie contoso.test…")]
    public async Task Creating_IsTheOperationOfTheWindow_WithoutStop_AndAnObjectThatWasCreatedSaysNothing(string culture, string working)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var dialog = harness.Factory.CreateNewObject(ObjectKind.OrganizationalUnit, harness.Company.CompanyOu);
        bool? closed = null;
        dialog.CloseRequested += (_, result) => closed = result;
        dialog.Name = "Projekty";

        // The write waits for the test.
        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var hold = 1;
        harness.Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Add && Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Release();
                release.Wait(HoldLimit);
            }
        };
        var seen = Record(dialog.Status);
        Task creating;
        try
        {
            creating = dialog.CreateCommand.ExecuteAsync(null);
            Assert.True(await held.WaitAsync(Limit, Ct));

            // Working: what is created and where. No Stop - a creation is not stopped half way from this window.
            Assert.True(dialog.IsBusy);
            Assert.True(dialog.Status.IsWorking);
            var status = dialog.Status.Current;
            Assert.Equal(PageStatusKind.Working, status.Kind);
            Assert.Equal(working, status.Message);
            Assert.Equal("NewObject.Status.Creating", status.Log.ToString());
            Assert.Null(status.Action);
            Assert.False(status.CanClose);
            Assert.False(dialog.Status.CancelCommand.CanExecute(null));
            Assert.False(await dialog.CanCloseAsync());
        }
        finally
        {
            release.Set();
        }

        await creating.WaitAsync(Limit, Ct);
        harness.Session.BeforeOperation = null;

        // Created: the dialog closes and its line says nothing - the main window shows the toast, as before.
        Assert.True(closed);
        Assert.NotNull(dialog.CreatedDn);
        Assert.True(harness.Session.Exists(dialog.CreatedDn!));
        Assert.Equal([(PageStatusKind.Working, working), (PageStatusKind.Idle, string.Empty)], seen);
        Assert.Same(PageStatus.Empty, dialog.Status.Current);
        Assert.False(dialog.IsBusy);
        Assert.Contains(harness.Notifications.Items, n => n.Message == harness.Localizer.Format(L.NewObject.Created, "Projekty"));
    }

    [Fact]
    public async Task ANameThatIsTaken_IsFoundByTheCreation_AndSaidAsNothingWasCreated()
    {
        var harness = await B2Harness.CreateAsync();
        using var dialog = harness.Factory.CreateNewObject(ObjectKind.User, harness.Company.ItOu);
        dialog.AvailabilityCheckDelay = TimeSpan.Zero;
        dialog.FirstName = "Jan";
        dialog.LastName = "Kowalski";
        await dialog.PendingAvailabilityCheck;
        dialog.PasswordInput = new TestPasswordInput(Typed);
        dialog.ConfirmPasswordInput = new TestPasswordInput(Typed);
        var seen = Record(dialog.Status);

        await dialog.CreateCommand.ExecuteAsync(null);

        // The check is part of the creation: working, then what stands in the way - the window is as it was.
        Assert.Equal(
            [
                (PageStatusKind.Working, "Creating “Jan Kowalski” in contoso.test…"),
                (PageStatusKind.Attention, "Nothing was created: An object named 'Jan Kowalski' already exists in this container."),
            ],
            seen);
        Assert.True(dialog.Status.Current.CanClose);
        Assert.Equal("NewObject.Status.NotCreated", dialog.Status.Current.Log.ToString());
        Assert.False(dialog.IsBusy);

        dialog.FullName = "Jan Kowalski (2)";
        await dialog.CreateCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Attention, dialog.Status.Current.Kind);
        Assert.Equal("Nothing was created: 'jan.kowalski' is already used by Jan Kowalski.", dialog.Status.Current.Message);
        Assert.Null(dialog.CreatedDn);
        Assert.Empty(harness.Session.PasswordResets);
        Assert.All(seen, s => Assert.DoesNotContain(Typed, s.Message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("en-US", "Could not create “Projekty”: ")]
    [InlineData("pl-PL", "Nie udało się utworzyć obiektu „Projekty”: ")]
    public async Task ACreationThatFailed_IsAnErrorWithTheReasonAndTheCode(string culture, string lead)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var dialog = harness.Factory.CreateNewObject(ObjectKind.OrganizationalUnit, harness.Company.CompanyOu);
        bool? closed = null;
        dialog.CloseRequested += (_, result) => closed = result;
        dialog.Name = "Projekty";
        harness.Session.FailNext(FakeOperationKind.Add, dialog.TargetDn, DirectoryErrorKind.AccessDenied);
        var seen = Record(dialog.Status);

        await dialog.CreateCommand.ExecuteAsync(null);

        // The domain controller refused: what was tried, why, the code. The dialog stays open, as it was.
        var failed = dialog.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Equal($"{lead}{harness.Errors.Translate(DirectoryErrorKind.AccessDenied)} (AccessDenied)", failed.Message);
        Assert.Equal("NewObject.Status.Failed [AccessDenied]", failed.Log.ToString());
        Assert.True(failed.CanClose);
        Assert.Null(failed.Action);
        Assert.Equal([PageStatusKind.Working, PageStatusKind.Error], seen.Select(s => s.Kind));
        Assert.Null(closed);
        Assert.Null(dialog.CreatedDn);
        Assert.False(dialog.IsBusy);
        Assert.True(dialog.CreateCommand.CanExecute(null));

        // OK again creates it: the error goes with the next attempt, and the dialog closes without another word.
        await dialog.CreateCommand.ExecuteAsync(null);
        Assert.True(closed);
        Assert.Same(PageStatus.Empty, dialog.Status.Current);
    }

    [Fact]
    public async Task ADomainControllerThatDoesNotAnswerTheChecks_IsAnErrorWithTheCode_NotTheWordsOfTheServer()
    {
        var harness = await B2Harness.CreateAsync();
        using var dialog = harness.Factory.CreateNewObject(ObjectKind.OrganizationalUnit, harness.Company.CompanyOu);
        dialog.Name = "Projekty";
        harness.Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Get)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "dc01.contoso.test does not answer") { LdapResultCode = 81 };
            }
        };

        await dialog.CreateCommand.ExecuteAsync(null);
        harness.Session.BeforeOperation = null;

        var failed = dialog.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Equal($"Could not create “Projekty”: {harness.Errors.Translate(DirectoryErrorKind.ServerUnavailable)} (LDAP 81)", failed.Message);
        Assert.Equal("NewObject.Status.Failed [LDAP 81]", failed.Log.ToString());
        Assert.DoesNotContain("does not answer", failed.Message, StringComparison.Ordinal);
        Assert.False(dialog.IsBusy);
    }

    [Fact]
    public async Task AnAccountThatWasCreatedOnlyInPart_ClosesTheDialog_AndIsExplainedWhereItWasBefore()
    {
        var harness = await B2Harness.CreateAsync();
        using var dialog = harness.Factory.CreateNewObject(ObjectKind.User, harness.Company.ItOu);
        dialog.AvailabilityCheckDelay = TimeSpan.Zero;
        bool? closed = null;
        dialog.CloseRequested += (_, result) => closed = result;
        dialog.FirstName = "Łucja";
        dialog.LastName = "Żak-Wąsowska";
        await dialog.PendingAvailabilityCheck;
        dialog.PasswordInput = new TestPasswordInput(Typed);
        dialog.ConfirmPasswordInput = new TestPasswordInput(Typed);
        harness.Session.FailNext(FakeOperationKind.ResetPassword, dialog.TargetDn, DirectoryErrorKind.PasswordPolicy);
        var seen = Record(dialog.Status);

        await dialog.CreateCommand.ExecuteAsync(null);

        // The account exists (disabled): the dialog of before explains it, the window closes, the line says no result.
        Assert.True(closed);
        Assert.NotNull(dialog.CreatedDn);
        Assert.Single(harness.Dialogs.Errors);
        Assert.Equal([PageStatusKind.Working, PageStatusKind.Idle], seen.Select(s => s.Kind));
        Assert.All(seen, s => Assert.DoesNotContain(Typed, s.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWindowWithoutASession_AndTheEndOfTheSession_AreTheStateOfTheWindow()
    {
        // Opened while disconnected: said from the start, not only when OK is pressed.
        var harness = await B2Harness.CreateAsync();
        var parent = harness.Company.CompanyOu;
        await harness.Sessions.DisconnectAsync();
        using (var disconnected = harness.Factory.CreateNewObject(ObjectKind.OrganizationalUnit, parent))
        {
            var state = disconnected.Status.Current;
            Assert.Equal(PageStatusKind.Error, state.Kind);
            Assert.Equal("Not connected to a domain.", state.Message);
            Assert.Equal("Errors.NotConnected", state.Log.ToString());
            Assert.Same(disconnected.Status.Lasting, state);
            Assert.False(state.CanClose);
            disconnected.Name = "Projekty";
            await disconnected.CreateCommand.ExecuteAsync(null);
            Assert.Same(disconnected.Status.Lasting, disconnected.Status.Current);
            Assert.Equal("Not connected to a domain.", disconnected.Error);
        }

        // The session ends while the window is open: what the line said before is covered for good.
        harness = await B2Harness.CreateAsync();
        using var dialog = harness.Factory.CreateNewObject(ObjectKind.OrganizationalUnit, harness.Company.CompanyOu);
        await dialog.CreateCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Attention, dialog.Status.Current.Kind);

        await harness.Sessions.DisconnectAsync();

        var ended = dialog.Status.Current;
        Assert.Equal(PageStatusKind.Error, ended.Kind);
        Assert.Equal(harness.Localizer[L.WindowSession.Ended], ended.Message);
        Assert.Equal("WindowSession.Ended", ended.Log.ToString());
        Assert.Same(dialog.Status.Lasting, ended);
        Assert.False(ended.CanClose);
        Assert.False(dialog.Status.CloseCommand.CanExecute(null));
        Assert.False(dialog.CreateCommand.CanExecute(null));

        // The state follows the language.
        harness.Localizer.SetCulture(Polish);
        Assert.Equal(harness.Localizer[L.WindowSession.Ended], dialog.Status.Current.Message);
        Assert.StartsWith("Sesja, w której otwarto to okno", dialog.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAccountThatMayJoinAComputer_ThatCannotBeUsedOrRead_IsSaidInTheLine_AndAGoodChoiceTakesItOff()
    {
        var harness = await B2Harness.CreateAsync();
        using var dialog = harness.Factory.CreateNewObject(ObjectKind.Computer, harness.Company.ComputersOu);
        await dialog.InitializeAsync();

        // An object without a security identifier cannot join a computer.
        harness.Dialogs.PickedObjects.Enqueue([harness.Company.ItOu]);
        await dialog.ChangeJoinerCommand.ExecuteAsync(null);
        var notSet = dialog.Status.Current;
        Assert.Equal(PageStatusKind.Attention, notSet.Kind);
        Assert.Equal(
            "The account that may join the computer was not changed: The chosen user or group has no security identifier.",
            notSet.Message);
        Assert.Equal("Computers.Prestage.JoinerNotFound", notSet.Log.ToString());
        Assert.True(notSet.CanClose);
        Assert.False(dialog.HasJoiner);

        // The chosen account could not be read.
        harness.Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Get)
            {
                throw new DirectoryException(DirectoryErrorKind.AccessDenied, "00002098: SecErr: DSID-03150F94");
            }
        };
        harness.Dialogs.PickedObjects.Enqueue([harness.Company.JanKowalski]);
        await dialog.ChangeJoinerCommand.ExecuteAsync(null);
        harness.Session.BeforeOperation = null;
        var failed = dialog.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Equal($"Could not read the chosen account: {harness.Errors.Translate(DirectoryErrorKind.AccessDenied)} (AccessDenied)", failed.Message);
        Assert.Equal("NewObject.Status.JoinerFailed [AccessDenied]", failed.Log.ToString());
        Assert.DoesNotContain("DSID", failed.Message, StringComparison.Ordinal);

        // A choice that works is the operator's next step: the line is empty again.
        harness.Dialogs.PickedObjects.Enqueue([harness.Company.JanKowalski]);
        await dialog.ChangeJoinerCommand.ExecuteAsync(null);
        Assert.True(dialog.HasJoiner);
        Assert.Same(PageStatus.Empty, dialog.Status.Current);
        Assert.Null(dialog.Error);
    }

    [Fact]
    public async Task TheLogCopies_CarryKeysAndCodes_NeverTheNameOfTheObjectTheDomainOrThePassword()
    {
        var harness = await B2Harness.CreateAsync();
        var logger = new ListLogger<PageStatusHolder>();
        var lists = new ObjectListServices(harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes, harness.Time, logger);
        var services = new DialogServices(
            harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes, harness.Time, harness.Dialogs, harness.Files,
            harness.Clipboard, harness.Notifications, harness.Coordinator, harness.Activities, lists, harness.Regional);
        using var dialog = new NewObjectViewModel(services, harness.Engines.Templates, ObjectKind.User, harness.Company.ItOu);
        dialog.AvailabilityCheckDelay = TimeSpan.Zero;

        // Not started, then refused by the domain controller, then created.
        await dialog.CreateCommand.ExecuteAsync(null);
        dialog.FirstName = "Łucja";
        dialog.LastName = "Żak-Wąsowska";
        await dialog.PendingAvailabilityCheck;
        dialog.PasswordInput = new TestPasswordInput(Typed);
        dialog.ConfirmPasswordInput = new TestPasswordInput(Typed);
        harness.Session.FailNext(FakeOperationKind.Add, dialog.TargetDn, DirectoryErrorKind.AccessDenied);
        await dialog.CreateCommand.ExecuteAsync(null);
        Assert.Contains("Łucja Żak-Wąsowska", dialog.Status.Current.Message, StringComparison.Ordinal);
        dialog.PasswordInput = new TestPasswordInput(Typed);
        dialog.ConfirmPasswordInput = new TestPasswordInput(Typed);
        await dialog.CreateCommand.ExecuteAsync(null);
        Assert.NotNull(dialog.CreatedDn);

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("NewObject.Status.NotCreated", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("NewObject.Status.Creating", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("NewObject.Status.Failed", StringComparison.Ordinal) && m.Contains("AccessDenied", StringComparison.Ordinal));
        Assert.DoesNotContain(Typed, logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Łucja", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Wąsowska", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("lucja", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contoso", logger.AllText, StringComparison.OrdinalIgnoreCase);
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
}
