using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Csv;
using AdMng.Core.Model;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Dialogs;

public class ResetPasswordTests
{
    [Fact]
    public async Task OneAccount_FixedPassword_ValidatesAndResets()
    {
        var harness = await B2Harness.CreateAsync();
        var dialog = harness.Factory.CreateResetPassword([harness.Company.ZanetaSzymanska]);
        await dialog.InitializeAsync();
        Assert.Equal("Reset Password - Żaneta Szymańska", dialog.WindowTitle);
        Assert.True(dialog.IsSingle);
        Assert.True(dialog.IsAnyLocked);
        Assert.False(dialog.GeneratePassword);
        var password = new TestPasswordInput("Nowe!Haslo2026");
        var confirm = new TestPasswordInput("Inne!Haslo2026");
        dialog.PasswordInput = password;
        dialog.ConfirmPasswordInput = confirm;

        await dialog.ResetCommand.ExecuteAsync(null);
        Assert.Equal("The passwords do not match.", dialog.Error);
        Assert.Empty(harness.Session.PasswordResets);

        password.Password = "Nowe!Haslo2026";
        confirm.Password = "Nowe!Haslo2026";
        await dialog.ResetCommand.ExecuteAsync(null);

        Assert.Null(dialog.Error);
        Assert.True(dialog.IsCompleted);
        Assert.Equal("Nowe!Haslo2026", Assert.Single(harness.Session.PasswordResets).Password);
        Assert.Equal("0", harness.Value(harness.Company.ZanetaSzymanska, Attr.PwdLastSet));
        Assert.Equal("0", harness.Value(harness.Company.ZanetaSzymanska, Attr.LockoutTime));
        Assert.True(password.Cleared && confirm.Cleared);
        Assert.False(Assert.Single(dialog.Results).HasPassword);
        Assert.Empty(harness.Dialogs.TypedConfirmations);
        Assert.DoesNotContain(harness.AuditLog.Entries, e => (e.NewValue ?? string.Empty).Contains("Nowe!Haslo2026", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ManyAccounts_GetUniqueGeneratedPasswords_ShownOnRequest()
    {
        var harness = await B2Harness.CreateAsync();
        var dialog = harness.Factory.CreateResetPassword([harness.Company.JanKowalski, harness.Company.LukaszWisniewski]);
        await dialog.InitializeAsync();
        Assert.True(dialog.GeneratePassword);
        dialog.GeneratePassword = false;
        Assert.True(dialog.GeneratePassword);
        Assert.False(dialog.CanTypePassword);

        harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await dialog.ResetCommand.ExecuteAsync(null);

        Assert.Equal("2", Assert.Single(harness.Dialogs.TypedConfirmations).Expected);
        Assert.Equal(2, harness.Session.PasswordResets.Count);
        Assert.NotEqual(harness.Session.PasswordResets[0].Password, harness.Session.PasswordResets[1].Password);
        var row = dialog.Results.Single(r => r.Name == "Jan Kowalski");
        Assert.Equal("••••••••", row.DisplayedPassword);

        // Closing before the passwords were shown, copied or exported asks first (they cannot be shown again).
        bool? closed = null;
        dialog.CloseRequested += (_, result) => closed = result;
        Assert.True(dialog.HasUnsavedGeneratedPasswords);
        await dialog.CancelCommand.ExecuteAsync(null);
        Assert.Null(closed);

        row.IsRevealed = true;
        var reset = harness.Session.PasswordResets.Single(r => r.DistinguishedName.StartsWith("CN=Jan Kowalski", StringComparison.Ordinal));
        Assert.Equal(reset.Password, row.DisplayedPassword);
        Assert.DoesNotContain(reset.Password, row.ToString(), StringComparison.Ordinal);

        dialog.CopyPasswordCommand.Execute(row);
        Assert.Equal(reset.Password, harness.Clipboard.Text);
        Assert.True(harness.Clipboard.IsSecret);
        Assert.True(dialog.HasUnsavedGeneratedPasswords);
        dialog.CopyPasswordCommand.Execute(dialog.Results.Single(r => r.Name != "Jan Kowalski"));
        Assert.False(dialog.HasUnsavedGeneratedPasswords);
        await dialog.CancelCommand.ExecuteAsync(null);
        Assert.True(closed);
        dialog.Dispose();
        Assert.Empty(dialog.Results);
    }

    [Fact]
    public async Task CopyPassword_WhenTheClipboardIsBusy_DoesNotCountAsHandedOut()
    {
        var harness = await B2Harness.CreateAsync();
        var dialog = harness.Factory.CreateResetPassword([harness.Company.JanKowalski, harness.Company.LukaszWisniewski]);
        await dialog.InitializeAsync();
        harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await dialog.ResetCommand.ExecuteAsync(null);
        var row = dialog.Results.Single(r => r.Name == "Jan Kowalski");
        var other = dialog.Results.Single(r => r.Name != "Jan Kowalski");
        other.IsRevealed = true;
        var copied = harness.Localizer[AdMng.Presentation.Localization.L.ResetPassword.PasswordCopied];

        harness.Clipboard.IsBusy = true;
        dialog.CopyPasswordCommand.Execute(row);

        Assert.Null(harness.Clipboard.Text);
        Assert.DoesNotContain(harness.Notifications.Items, n => n.Message == copied);
        Assert.True(dialog.HasUnsavedGeneratedPasswords);

        harness.Clipboard.IsBusy = false;
        dialog.CopyPasswordCommand.Execute(row);

        Assert.True(harness.Clipboard.IsSecret);
        Assert.Contains(harness.Notifications.Items, n => n.Message == copied);
        Assert.False(dialog.HasUnsavedGeneratedPasswords);
    }

    [Fact]
    public async Task Export_ContainsOnlyThePasswordsOfResetsThatSucceeded()
    {
        var harness = await B2Harness.CreateAsync();
        var dialog = harness.Factory.CreateResetPassword([harness.Company.JanKowalski, harness.Company.LukaszWisniewski]);
        await dialog.InitializeAsync();
        harness.Session.FailNext(FakeOperationKind.ResetPassword, harness.Company.LukaszWisniewski, Core.Directory.DirectoryErrorKind.AccessDenied);
        harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await dialog.ResetCommand.ExecuteAsync(null);
        var succeeded = Assert.Single(harness.Session.PasswordResets);
        Assert.StartsWith("CN=Jan Kowalski", succeeded.DistinguishedName, StringComparison.Ordinal);
        var path = Path.Combine(Path.GetTempPath(), $"admng-reset-{Guid.NewGuid():N}.csv");
        harness.Files.NextSavePath = path;
        harness.Dialogs.ConfirmAnswers.Enqueue(true);
        try
        {
            await dialog.ExportCommand.ExecuteAsync(null);

            var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains(succeeded.Password, text, StringComparison.Ordinal);
            var lines = text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
            var lukasz = Assert.Single(lines, l => l.Contains("ResetPassword", StringComparison.Ordinal) && l.Contains("Wiśniewski", StringComparison.Ordinal));
            Assert.EndsWith(",", lukasz, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DeclinedConfirmation_ResetsNothing()
    {
        var harness = await B2Harness.CreateAsync();
        var dialog = harness.Factory.CreateResetPassword([harness.Company.JanKowalski, harness.Company.LukaszWisniewski]);

        harness.Dialogs.TypedConfirmAnswers.Enqueue(false);
        await dialog.ResetCommand.ExecuteAsync(null);

        Assert.Empty(harness.Session.PasswordResets);
        Assert.False(dialog.IsCompleted);
    }
}

public class NewObjectTests
{
    [Fact]
    public async Task User_DerivesNames_ChecksAvailability_AndCreatesThroughThePipeline()
    {
        var harness = await B2Harness.CreateAsync();
        var dialog = harness.Factory.CreateNewObject(ObjectKind.User, harness.Company.ItOu);
        dialog.AvailabilityCheckDelay = TimeSpan.Zero;
        await dialog.InitializeAsync();
        Assert.Equal("New Object - User", dialog.WindowTitle);
        Assert.Equal("contoso.test/Firma/Pracownicy/IT", dialog.ParentPath);

        dialog.FirstName = "Łucja";
        dialog.LastName = "Żak-Wąsowska";
        await dialog.PendingAvailabilityCheck;

        Assert.Equal("Łucja Żak-Wąsowska", dialog.FullName);
        Assert.Equal("lucja.zak-wasowska", dialog.SamAccountName);
        Assert.Equal("lucja.zak-wasowska", dialog.UpnPrefix);
        Assert.Equal(NameAvailability.Available, dialog.SamAvailability);
        Assert.Equal($"CN=Łucja Żak-Wąsowska,{harness.Company.ItOu}", dialog.TargetDn);

        dialog.PasswordInput = new TestPasswordInput("Start!Haslo2026");
        dialog.ConfirmPasswordInput = new TestPasswordInput("Start!Haslo2026");
        bool? closed = null;
        dialog.CloseRequested += (_, result) => closed = result;
        await dialog.CreateCommand.ExecuteAsync(null);

        Assert.Null(dialog.Error);
        Assert.True(closed);
        var dn = dialog.CreatedDn!;
        Assert.True(harness.Session.Exists(dn));
        Assert.Equal("512", harness.Value(dn, Attr.UserAccountControl));
        Assert.Equal("0", harness.Value(dn, Attr.PwdLastSet));
        Assert.Equal("lucja.zak-wasowska@contoso.test", harness.Value(dn, Attr.UserPrincipalName));
        Assert.Equal("Start!Haslo2026", Assert.Single(harness.Session.PasswordResets).Password);
        Assert.Contains(harness.AuditLog.Entries, e => e.Action == nameof(ChangeAction.Create) && e.Succeeded);
        Assert.Contains(harness.PowerShell.Entries, e => e.ObjectCount == 1);
    }

    [Fact]
    public async Task User_TakenLogonName_IsReported()
    {
        var harness = await B2Harness.CreateAsync();
        var dialog = harness.Factory.CreateNewObject(ObjectKind.User, harness.Company.ItOu);
        dialog.AvailabilityCheckDelay = TimeSpan.Zero;

        dialog.FirstName = "Jan";
        dialog.LastName = "Kowalski";
        await dialog.PendingAvailabilityCheck;

        Assert.Equal(NameAvailability.Taken, dialog.SamAvailability);
        Assert.Equal("'jan.kowalski' is already used by Jan Kowalski.", dialog.SamAvailabilityText);
        dialog.PasswordInput = new TestPasswordInput("Start!Haslo2026");
        dialog.ConfirmPasswordInput = new TestPasswordInput("Start!Haslo2026");
        await dialog.CreateCommand.ExecuteAsync(null);
        Assert.Equal("An object named 'Jan Kowalski' already exists in this container.", dialog.Error);

        dialog.FullName = "Jan Kowalski (2)";
        await dialog.CreateCommand.ExecuteAsync(null);
        Assert.Equal("'jan.kowalski' is already used by Jan Kowalski.", dialog.Error);
        Assert.Null(dialog.CreatedDn);
    }

    [Fact]
    public async Task User_PasswordRules()
    {
        var harness = await B2Harness.CreateAsync();
        var dialog = harness.Factory.CreateNewObject(ObjectKind.User, harness.Company.ItOu);
        dialog.FirstName = "Anna";
        dialog.LastName = "Nowa";

        await dialog.CreateCommand.ExecuteAsync(null);
        Assert.Equal("Enter a password (or create the account disabled).", dialog.Error);

        dialog.MustChangePassword = true;
        dialog.PasswordNeverExpires = true;
        Assert.NotNull(dialog.PasswordOptionsWarning);

        dialog.AccountDisabled = true;
        await dialog.CreateCommand.ExecuteAsync(null);
        Assert.Null(dialog.Error);
        var uac = (UserAccountControl)int.Parse(harness.Value(dialog.CreatedDn!, Attr.UserAccountControl)!, CultureInfo.InvariantCulture);
        Assert.True(uac.HasFlag(UserAccountControl.AccountDisable));
        Assert.True(uac.HasFlag(UserAccountControl.DontExpirePassword));
        Assert.Empty(harness.Session.PasswordResets);
    }

    [Fact]
    public async Task Group_UsesScopeAndType()
    {
        var harness = await B2Harness.CreateAsync();
        var dialog = harness.Factory.CreateNewObject(ObjectKind.Group, harness.Company.GroupsOu);
        dialog.AvailabilityCheckDelay = TimeSpan.Zero;

        dialog.Name = "UG_Projekty";
        Assert.Equal("UG_Projekty", dialog.SamAccountName);
        dialog.GroupScope = GroupScope.Universal;
        dialog.IsSecurityGroup = false;
        await dialog.CreateCommand.ExecuteAsync(null);

        Assert.Null(dialog.Error);
        Assert.Equal(GroupTypes.Compose(GroupScope.Universal, security: false).ToString(CultureInfo.InvariantCulture),
            harness.Value(dialog.CreatedDn!, Attr.GroupType));
    }

    [Fact]
    public async Task Computer_GetsUpperCaseSamWithDollar_AndWorkstationFlag()
    {
        var harness = await B2Harness.CreateAsync();
        var dialog = harness.Factory.CreateNewObject(ObjectKind.Computer, harness.Company.ComputersOu);

        dialog.Name = "gda-pc-0000000001";
        Assert.Equal("GDA-PC-00000000$", dialog.SamAccountName);
        await dialog.CreateCommand.ExecuteAsync(null);

        Assert.Null(dialog.Error);
        Assert.Equal("4096", harness.Value(dialog.CreatedDn!, Attr.UserAccountControl));
        Assert.Equal("GDA-PC-00000000$", harness.Value(dialog.CreatedDn!, Attr.SamAccountName));
    }

    [Fact]
    public async Task OrganizationalUnit_AndContact()
    {
        var harness = await B2Harness.CreateAsync();
        var ou = harness.Factory.CreateNewObject(ObjectKind.OrganizationalUnit, harness.Company.CompanyOu);
        Assert.False(ou.IsProtectionSupported);
        ou.Name = "Marketing";
        ou.Description = "Dział marketingu";
        await ou.CreateCommand.ExecuteAsync(null);
        Assert.Equal($"OU=Marketing,{harness.Company.CompanyOu}", ou.CreatedDn);
        Assert.Equal("Dział marketingu", harness.Value(ou.CreatedDn!, Attr.Description));

        var contact = harness.Factory.CreateNewObject(ObjectKind.Contact, harness.Company.SalesOu);
        contact.FirstName = "Zofia";
        contact.LastName = "Źródłowska";
        Assert.Equal("Zofia Źródłowska", contact.DisplayName);
        await contact.CreateCommand.ExecuteAsync(null);
        Assert.Null(contact.Error);
        Assert.Equal("Zofia Źródłowska", harness.Value(contact.CreatedDn!, Attr.DisplayName));
    }

    [Fact]
    public async Task EmptyName_IsRejected()
    {
        var harness = await B2Harness.CreateAsync();
        var dialog = harness.Factory.CreateNewObject(ObjectKind.OrganizationalUnit, harness.Company.CompanyOu);

        await dialog.CreateCommand.ExecuteAsync(null);

        Assert.Equal("Enter a name.", dialog.Error);
        Assert.Null(dialog.CreatedDn);
    }

    [Fact]
    public async Task Closing_IsRefusedWhileTheObjectIsBeingCreated()
    {
        var harness = await B2Harness.CreateAsync();
        var dialog = harness.Factory.CreateNewObject(ObjectKind.OrganizationalUnit, harness.Company.CompanyOu);
        using var release = new ManualResetEventSlim();
        var adding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Add)
            {
                adding.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };
        Assert.True(await dialog.CanCloseAsync());

        dialog.Name = "Projekty";
        var create = dialog.CreateCommand.ExecuteAsync(null);
        await adding.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var closable = await dialog.CanCloseAsync();
        release.Set();
        await create;

        Assert.False(closable);
        Assert.NotNull(dialog.CreatedDn);
        Assert.True(await dialog.CanCloseAsync());
    }
}

public class FindTests
{
    [Fact]
    public async Task NameAndConditions_BuildAnEscapedFilter_AndListResults()
    {
        var harness = await B2Harness.CreateAsync();
        using var find = harness.Factory.CreateFind(harness.Company.EmployeesOu);
        Assert.Equal("Find Users, Contacts, and Groups", find.WindowTitle);
        Assert.Equal("contoso.test/Firma/Pracownicy", find.SearchBaseText);

        find.Kind = find.Kinds.Single(k => k.Value == FindKind.Users);
        find.NameText = "kowal";
        find.AddConditionCommand.Execute(null);
        var condition = find.Conditions[0];
        condition.Field = condition.Fields.Single(f => f.Value == Attr.Department);
        condition.Operator = condition.Operators.Single(o => o.Value == FindOperator.Is);
        condition.Value = "IT*";

        Assert.Equal("(&(sAMAccountType=805306368)(anr=kowal)(department=IT\\2a))", find.FilterText);

        condition.Value = "IT";
        await find.FindNowCommand.ExecuteAsync(null);
        Assert.True(find.HasSearched);
        Assert.Equal(["Jan Kowalski"], find.Results.Items.Select(r => r.Name));
    }

    [Fact]
    public async Task RawFilter_IsValidated_AndCanBeReset()
    {
        var harness = await B2Harness.CreateAsync();
        using var find = harness.Factory.CreateFind(null);

        find.FilterText = "(&(objectClass=user)";
        Assert.True(find.IsRawFilter);
        Assert.NotNull(find.FilterError);
        Assert.False(find.FindNowCommand.CanExecute(null));

        find.FilterText = "(&(objectCategory=computer)(location=Warszawa*))";
        await find.FindNowCommand.ExecuteAsync(null);
        Assert.Equal(["WAW-PC001", "WAW-PC002"], find.Results.Items.Select(r => r.Name).Order());

        find.ResetFilterCommand.Execute(null);
        Assert.False(find.IsRawFilter);
        Assert.StartsWith("(|(sAMAccountType=805306368)", find.FilterText, StringComparison.Ordinal);

        // Leaving "Custom search" for a predefined kind generates the filter of that kind again.
        find.Kind = Assert.Single(find.Kinds, k => k.Value == FindKind.Custom);
        Assert.True(find.IsRawFilter);
        find.Kind = Assert.Single(find.Kinds, k => k.Value == FindKind.Computers);
        Assert.False(find.IsRawFilter);
        Assert.Equal(ObjectKind.Computer.ToLdapFilter(), find.FilterText);
    }
}

public class PickerTests
{
    [Fact]
    public async Task CheckNames_ResolvesUniqueNames_AndListsAmbiguousOnes()
    {
        var harness = await B2Harness.CreateAsync();
        using var picker = harness.Factory.CreateObjectPicker(harness.Session, [ObjectKind.User, ObjectKind.Group], multiSelect: true);
        Assert.Equal("Select Users, Groups", picker.WindowTitle);

        picker.SearchText = "Kowalski; GG_; nikt";
        await picker.CheckNamesCommand.ExecuteAsync(null);

        Assert.Equal(["Jan Kowalski"], picker.Chosen.Select(c => c.Name));
        Assert.Equal("GG_; nikt", picker.SearchText);
        Assert.Equal(["GG_IT", "GG_Ksiegowosc", "GG_Sprzedaz"], picker.Results.Select(r => r.Name));
        Assert.Contains("No object found for 'nikt'.", picker.StatusText, StringComparison.Ordinal);

        // Choosing a match answers the ambiguous name; the name that was not found stays to be corrected.
        picker.SelectedResult = picker.Results[0];
        picker.AddSelectedCommand.Execute(null);
        Assert.Equal("nikt", picker.SearchText);
        picker.SearchText = string.Empty;
        bool? closed = null;
        picker.CloseRequested += (_, r) => closed = r;
        await picker.OkCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Equal([harness.Company.JanKowalski, harness.Company.GroupIt], picker.Result);
    }

    [Fact]
    public async Task Ok_WithAMatchOfAnAmbiguousNameSelected_ChoosesItAndCloses()
    {
        var harness = await B2Harness.CreateAsync();
        using var picker = harness.Factory.CreateObjectPicker(harness.Session, [ObjectKind.Group], multiSelect: false);
        bool? closed = null;
        picker.CloseRequested += (_, r) => closed = r;

        // OK with an ambiguous name lists its matches and stays open.
        picker.SearchText = "GG_";
        await picker.OkCommand.ExecuteAsync(null);
        Assert.Null(closed);
        Assert.Equal(3, picker.Results.Count);

        // Selecting one of them and pressing OK again answers the name (ADUC "Multiple Names Found").
        picker.SelectedResult = picker.Results.Single(r => r.Name == "GG_Sprzedaz");
        await picker.OkCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Equal(string.Empty, picker.SearchText);
        Assert.Equal([picker.Results.Single(r => r.Name == "GG_Sprzedaz").Dn], picker.Result);
    }

    [Fact]
    public async Task Ok_IsEnabledAsSoonAsANameIsTyped()
    {
        var harness = await B2Harness.CreateAsync();
        using var picker = harness.Factory.CreateObjectPicker(harness.Session, [ObjectKind.User], multiSelect: false);
        var notifications = 0;
        picker.OkCommand.CanExecuteChanged += (_, _) => notifications++;
        Assert.False(picker.OkCommand.CanExecute(null));

        // ADUC: type a name and press OK (the names are checked on OK).
        picker.SearchText = "Kowalski";

        Assert.True(notifications > 0);
        Assert.True(picker.OkCommand.CanExecute(null));
        await picker.OkCommand.ExecuteAsync(null);
        Assert.Equal([harness.Company.JanKowalski], picker.Result);
    }

    [Fact]
    public async Task FindNow_RespectsObjectTypesAndLocation()
    {
        var harness = await B2Harness.CreateAsync();
        using var picker = harness.Factory.CreateObjectPicker(harness.Session, [ObjectKind.Group, ObjectKind.Computer], multiSelect: false);
        picker.LocationDn = harness.Company.CompanyOu;
        picker.ObjectTypes.Single(o => o.Kind == ObjectKind.Computer).IsChecked = false;

        await picker.FindNowCommand.ExecuteAsync(null);

        Assert.Equal(["DL_Wszyscy", "GG_IT", "GG_Ksiegowosc", "GG_Sprzedaz"], picker.Results.Select(r => r.Name));
        Assert.Equal("Objects found: 4", picker.StatusText);
        picker.SelectedResult = picker.Results[1];
        picker.AddSelectedCommand.Execute(null);
        picker.SelectedResult = picker.Results[2];
        picker.AddSelectedCommand.Execute(null);
        Assert.Equal(["GG_Ksiegowosc"], picker.Chosen.Select(c => c.Name));
    }

    [Fact]
    public async Task ContainerPicker_ExpandsToTheStartContainer()
    {
        var harness = await B2Harness.CreateAsync();
        using var picker = harness.Factory.CreateContainerPicker(harness.Session, harness.Company.ItOu);

        await picker.InitializeAsync();

        Assert.Equal(harness.Company.ItOu, picker.SelectedContainer?.Dn, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("contoso.test/Firma/Pracownicy/IT", picker.SelectedPath);
        var domain = Assert.IsType<ContainerNodeViewModel>(Assert.Single(picker.Roots));
        Assert.Equal("contoso.test", domain.Name);

        domain.IsSelected = true;
        picker.OkCommand.Execute(null);
        Assert.Equal(harness.Builder.DomainDn, picker.Result);
    }
}

public class SessionLogAndHistoryTests
{
    [Fact]
    public async Task SessionLog_ShowsChangesLive_FiltersAndExports()
    {
        var harness = await B2Harness.CreateAsync();
        using var log = harness.Factory.CreateSessionLog();
        Assert.Empty(log.Entries);

        await harness.Coordinator.ApplyAsync(new ChangeRequest(
            [harness.Company.JanKowalski, harness.Company.LukaszWisniewski], [new SetAttributeOperation(Attr.Title, "Analityk")], "test"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, log.Entries.Count);
        Assert.Equal("Entries: 2 of 2", log.CountText);
        Assert.All(log.Entries, e => Assert.Equal("Modify", e.Action));
        log.FilterText = "lukasz";
        Assert.Equal(["Łukasz Wiśniewski"], log.Entries.Select(e => e.Name));
        log.ShowFailuresOnly = true;
        Assert.Empty(log.Entries);
        log.ShowFailuresOnly = false;
        log.FilterText = string.Empty;

        var path = Path.Combine(Path.GetTempPath(), $"admng-test-{Guid.NewGuid():N}.csv");
        try
        {
            harness.Files.NextSavePath = path;
            await log.ExportCommand.ExecuteAsync(null);
            var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(3, lines.Length);
            Assert.Contains("Analityk", lines[1], StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SessionLog_AddsABurstOfBackgroundEntriesInOneDispatcherCall()
    {
        var harness = await B2Harness.CreateAsync();
        var dispatcher = new QueuedUiDispatcher();
        var services = new DialogServices(harness.Sessions, harness.Localizer, dispatcher, harness.Settings, harness.Errors, harness.Changes,
            harness.Time, harness.Dialogs, harness.Files, harness.Clipboard, harness.Notifications, harness.Coordinator, harness.Activities, harness.Lists,
            harness.Regional);
        harness.AuditLog.Record(Entry(0));
        using var log = new SessionLogViewModel(services, harness.AuditLog, new AuditLogExporter(new CsvCodec()));
        Assert.Single(log.Entries);

        // A large batch records one entry per object and attribute on worker threads.
        await Task.WhenAll(Enumerable.Range(0, 4).Select(t => Task.Run(
            () =>
            {
                for (var i = 1; i <= 250; i++)
                {
                    harness.AuditLog.Record(Entry((t * 1000) + i));
                }
            },
            TestContext.Current.CancellationToken)));

        Assert.Equal(1, dispatcher.Pending);
        dispatcher.RunAll();
        Assert.Equal(1001, log.Entries.Count);
        Assert.Equal("Entries: 1001 of 1001", log.CountText);

        harness.AuditLog.Record(Entry(5000));
        dispatcher.RunAll();
        Assert.Equal(1002, log.Entries.Count);

        AuditEntry Entry(int n) => new(harness.Time.GetUtcNow(), "admin", "dc01", $"CN=Użytkownik {n},DC=contoso,DC=test", "Modify",
            Attr.Title, null, "Analityk", true, null);
    }

    [Fact]
    public async Task PowerShellHistory_ListsExecutedChangesNewestFirst()
    {
        var harness = await B2Harness.CreateAsync();
        using var history = harness.Factory.CreatePowerShellHistory();

        await harness.Coordinator.ApplyAsync(new ChangeRequest([harness.Company.JanKowalski], [new SetAttributeOperation(Attr.Title, "A")], "Pierwsza"),
            TestContext.Current.CancellationToken);
        await harness.Coordinator.ApplyAsync(new ChangeRequest([harness.Company.JanKowalski], [new SetAccountEnabledOperation(false)], "Druga"),
            TestContext.Current.CancellationToken);

        Assert.Equal(["Druga", "Pierwsza"], history.Items.Select(i => i.Title));
        history.SelectedItem = history.Items[1];
        history.CopyCommand.Execute(null);
        Assert.Contains("Set-ADObject", harness.Clipboard.Text, StringComparison.Ordinal);
    }
}

public class SettingsDialogTests
{
    [Fact]
    public async Task Save_AppliesLanguageThemeAndValues()
    {
        var harness = await B2Harness.CreateAsync();
        using var settings = harness.Factory.CreateSettings();
        Assert.Equal("Settings (shared by all windows)", settings.WindowTitle);

        settings.Language = settings.Languages.Single(l => l.Value == "pl");
        settings.Theme = settings.Themes.Single(t => t.Value == ThemePreference.Dark);
        settings.ConfirmationThreshold = 50;
        settings.PageSize = 10;
        Assert.NotNull(settings.Error);
        Assert.False(settings.SaveCommand.CanExecute(null));
        settings.PageSize = 1000;
        await settings.SaveCommand.ExecuteAsync(null);

        Assert.Equal("pl", harness.Settings.Current.Language);
        Assert.Equal(50, harness.Settings.Current.ConfirmationThreshold);
        Assert.Equal(1000, harness.Settings.Current.PageSize);
        Assert.Equal("pl-PL", harness.Localizer.Culture.Name);
        Assert.Equal(ThemePreference.Dark, harness.Theme.Applied);
        Assert.Equal("Ustawienia (wspólne dla wszystkich okien)", settings.WindowTitle);
    }

    [Fact]
    public async Task AdvancedFeatures_AreOnlyStored_EveryShellAppliesThem()
    {
        // The settings are shared by every connection window: the dialog stores the switch, and each window's shell reacts
        // to the settings change (ShellTests cover the reload).
        var harness = await B2Harness.CreateAsync();
        var changes = 0;
        harness.Settings.Changed += (_, _) => changes++;
        using var settings = harness.Factory.CreateSettings();

        settings.ShowAdvancedFeatures = true;
        await settings.SaveCommand.ExecuteAsync(null);

        Assert.True(harness.Settings.Current.ShowAdvancedFeatures);
        Assert.Equal(1, changes);
        Assert.Equal("Settings (shared by all windows)", settings.WindowTitle);

        using var again = harness.Factory.CreateSettings();
        Assert.True(again.ShowAdvancedFeatures);
        again.ShowAdvancedFeatures = false;
        await again.SaveCommand.ExecuteAsync(null);
        Assert.False(harness.Settings.Current.ShowAdvancedFeatures);
    }
}

/// <summary>Dispatcher double that queues work until the test runs it (like a busy UI thread).</summary>
internal sealed class QueuedUiDispatcher : IUiDispatcher
{
    private readonly Queue<Action> _queue = new();
    private readonly Lock _gate = new();

    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    public bool CheckAccess() => false;

    public Task InvokeAsync(Action action)
    {
        lock (_gate)
        {
            _queue.Enqueue(action);
        }

        return Task.CompletedTask;
    }

    public void RunAll()
    {
        while (true)
        {
            Action action;
            lock (_gate)
            {
                if (!_queue.TryDequeue(out action!))
                {
                    return;
                }
            }

            action();
        }
    }
}
