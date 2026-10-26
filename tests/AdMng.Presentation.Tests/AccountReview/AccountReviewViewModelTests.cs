using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.Connection;
using AdMng.Presentation.Views;
using AdMng.Testing;
using static AdMng.Presentation.Tests.AccountReview.ReviewAssert;

namespace AdMng.Presentation.Tests.AccountReview;

/// <summary>
/// The account review window end to end: the real planner, executor and coordinator over the fake DC with the Polish
/// sample company prepared by <see cref="ReviewScenario"/>.
/// </summary>
public class AccountReviewViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Run_FindsTheAccountsOfEveryCheck()
    {
        var scenario = await ReviewScenario.CreateAsync();
        using var review = await scenario.RunAsync();

        Assert.Equal("Account review – contoso.test", review.WindowTitle);
        Assert.Null(review.Error);
        Assert.False(review.IsRunning);
        Assert.Equal(Enum.GetValues<AccountCheck>(), review.Tiles.Select(t => t.Check));
        Assert.All(review.Tiles, t => Assert.True(t.IsComplete));
        Assert.Equal(["Żaneta Szymańska"], Names(review, AccountCheck.Locked));
        Assert.Equal(["Małgorzata Wójcik"], Names(review, AccountCheck.PasswordExpired));
        Assert.Equal(["Jan Kowalski", "Łukasz Wiśniewski"], Names(review, AccountCheck.PasswordExpiring));
        Assert.Equal(["Bartłomiej Kozłowski"], Names(review, AccountCheck.MustChangePassword));
        Assert.Equal(["Agnieszka Żółć"], Names(review, AccountCheck.AccountExpired));
        Assert.Equal(["Paweł Lewandowski"], Names(review, AccountCheck.AccountExpiring));
        Assert.Equal(["Guest", "Zbigniew Kaźmierczak", "krbtgt"], Names(review, AccountCheck.Disabled));
        Assert.Equal(["Paweł Lewandowski"], Names(review, AccountCheck.Inactive));
        Assert.Equal(["Wiesław Pękala"], Names(review, AccountCheck.NeverLoggedOn));
        Assert.Equal(["Administrator", "Grażyna Dąbrowska"], Names(review, AccountCheck.PasswordNeverExpires));
        Assert.Equal(["Tadeusz Ślęzak"], Names(review, AccountCheck.PasswordNotRequired));
        Assert.Empty(Names(review, AccountCheck.ReversibleEncryption));
        Assert.True(review.IsEmpty);
        Assert.Empty(Names(review, AccountCheck.PasswordOld));
        Assert.Equal(["Administrator", "Wiesław Pękala"], Names(review, AccountCheck.NoMail));
        Assert.Equal(["Administrator", "Grażyna Dąbrowska", "Jan Kowalski", "Małgorzata Wójcik", "Paweł Lewandowski"], Names(review, AccountCheck.NoManager));
        Assert.Equal(["Wiesław Pękala"], Names(review, AccountCheck.ManagerDisabled));
        Assert.Equal(["Zbigniew Kaźmierczak"], Names(review, AccountCheck.DisabledInGroups));
        Assert.Equal(["Zbigniew Kaźmierczak"], Names(review, AccountCheck.DisabledInGal));
        Assert.Equal(["Administrator", "Tadeusz Ślęzak"], Names(review, AccountCheck.PrivilegedWithProblem));
        Assert.Equal(1, review.Tiles.Single(t => t.Check == AccountCheck.Locked).Count);
        Assert.Equal("5", review.Tiles.Single(t => t.Check == AccountCheck.NoManager).CountText);
        Assert.StartsWith($"As of {Noon("en-US")} · ", review.StatusText, StringComparison.Ordinal);
        Assert.EndsWith("· 14 accounts with findings", review.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Columns_ShowTheValuesOfTheCheck_PasswordExpiryFollowsTheFineGrainedPolicy()
    {
        var scenario = await ReviewScenario.CreateAsync();
        using var review = await scenario.RunAsync();

        var lukasz = Row(review, AccountCheck.PasswordExpiring, "Łukasz Wiśniewski");
        Assert.Equal(["Name", "Login", "PasswordExpires", "PasswordDays", "PasswordLastSet", "Mail", "Location"], review.Columns.Select(c => c.Id));
        Assert.Equal("Password expires", review.Columns[2].Header);
        Assert.Equal(new DateTime(2026, 10, 18, 12, 0, 0).ToString("g", CultureInfo.GetCultureInfo("en-US")), lukasz[AccountReviewColumns.PasswordExpires]);
        Assert.Equal("in 5 days", lukasz[AccountReviewColumns.PasswordDays]);
        Assert.Equal("lukasz.wisniewski", lukasz[AccountReviewColumns.Login]);
        Assert.Equal("contoso.test/Firma/Pracownicy/IT", lukasz[AccountReviewColumns.Location]);
        Assert.Equal("in 10 days", Row(review, AccountCheck.PasswordExpiring, "Jan Kowalski")[AccountReviewColumns.PasswordDays]);
        Assert.Equal("10 days ago", Row(review, AccountCheck.PasswordExpired, "Małgorzata Wójcik")[AccountReviewColumns.PasswordDays]);

        var pawel = Row(review, AccountCheck.AccountExpiring, "Paweł Lewandowski");
        Assert.Equal("End of 10/16/2026", pawel[AccountReviewColumns.AccountExpires]);
        Assert.Equal("in 3 days", pawel[AccountReviewColumns.AccountDays]);
        Assert.Equal("120 days ago", Row(review, AccountCheck.Inactive, "Paweł Lewandowski")[AccountReviewColumns.LogonDays]);
        Assert.Equal("Zbigniew Kaźmierczak (disabled)", Row(review, AccountCheck.ManagerDisabled, "Wiesław Pękala")[AccountReviewColumns.Manager]);
        Assert.Equal("1: GG_Ksiegowosc", Row(review, AccountCheck.DisabledInGroups, "Zbigniew Kaźmierczak")[AccountReviewColumns.Groups]);
        Assert.Equal("Password not required", Row(review, AccountCheck.PrivilegedWithProblem, "Tadeusz Ślęzak")[AccountReviewColumns.Problems]);
        Assert.Equal("agnieszka.zolc@contoso.test", Row(review, AccountCheck.AccountExpired, "Agnieszka Żółć")[AccountReviewColumns.Mail]);

        // Sorting by a date column (newest last), then descending.
        review.SelectedTile = review.Tiles.Single(t => t.Check == AccountCheck.PasswordExpiring);
        review.SortCommand.Execute(AccountReviewColumns.PasswordExpires);
        Assert.Equal(["Łukasz Wiśniewski", "Jan Kowalski"], review.Items.Select(r => r.Name));
        review.SortCommand.Execute(AccountReviewColumns.PasswordExpires);
        Assert.Equal(["Jan Kowalski", "Łukasz Wiśniewski"], review.Items.Select(r => r.Name));
    }

    [Fact]
    public async Task Query_IsOnePagedUserSearch_PreFilteredOnTheServer_TheConstructedAttributesAreOnlyRead()
    {
        var scenario = await ReviewScenario.CreateAsync();
        using var review = await scenario.RunAsync(r =>
        {
            foreach (var option in r.CheckOptions)
            {
                option.IsEnabled = option.Check is AccountCheck.Locked or AccountCheck.PasswordExpiring;
            }
        });

        var searches = scenario.Harness.Session.Operations.Where(o => o.Kind == FakeOperationKind.Search).Select(o => o.Search!).ToList();
        var main = Assert.Single(searches, s => s.Filter.Contains("(sAMAccountType=805306368)", StringComparison.Ordinal));
        Assert.Contains("(lockoutTime>=1)", main.Filter, StringComparison.Ordinal);
        Assert.Contains("(pwdLastSet>=1)", main.Filter, StringComparison.Ordinal);
        Assert.DoesNotContain(Attr.PasswordExpiryComputed, main.Filter, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Attr.UserAccountControlComputed, main.Filter, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Attr.PasswordExpiryComputed, main.Attributes);
        Assert.Contains(Attr.UserAccountControlComputed, main.Attributes);
        Assert.Equal(SearchScope.Subtree, main.Scope);
        Assert.Equal("DC=contoso,DC=test", main.BaseDn);
        Assert.DoesNotContain(searches, s => s.Filter.Contains("1.2.840.113556.1.4.1941", StringComparison.Ordinal));
        Assert.Equal([AccountCheck.Locked, AccountCheck.PasswordExpiring], review.Tiles.Select(t => t.Check));
        Assert.Equal(["Jan Kowalski", "Łukasz Wiśniewski"], Names(review, AccountCheck.PasswordExpiring));

        // The remembered choice: only these two checks run next time, with the parameters of this run.
        Assert.Equal(17, scenario.Harness.Settings.Current.AccountReview.DisabledChecks.Count);
        using var next = scenario.Harness.Factory.CreateAccountReview();
        Assert.Equal([AccountCheck.Locked, AccountCheck.PasswordExpiring], next.CheckOptions.Where(o => o.IsEnabled).Select(o => o.Check));
    }

    [Fact]
    public async Task Scopes_OrganizationalUnit_SavedView_AndNestedGroupMembers()
    {
        var scenario = await ReviewScenario.CreateAsync();
        var harness = scenario.Harness;
        using var review = harness.Factory.CreateAccountReview();
        await review.InitializeAsync();

        // OU (with subtree), chosen with "Wybierz…".
        review.ScopeKind = review.ScopeKinds.Single(k => k.Value == AccountReviewScopeKind.OrganizationalUnit);
        Assert.True(review.NeedsScopeTarget);
        Assert.False(review.RunCommand.CanExecute(null));
        Assert.Equal("Choose the organizational unit or the group first.", review.ScopeText);
        harness.Dialogs.PickedContainers.Enqueue(scenario.Company.SalesOu);
        await review.BrowseScopeCommand.ExecuteAsync(null);
        Assert.Equal("contoso.test/Firma/Pracownicy/Sprzedaż", review.ScopeText);
        await review.RunCommand.ExecuteAsync(null);
        Assert.Equal(["Żaneta Szymańska"], Names(review, AccountCheck.Locked));
        Assert.Equal(["Agnieszka Żółć"], Names(review, AccountCheck.AccountExpired));
        Assert.Empty(Names(review, AccountCheck.PasswordExpiring));
        Assert.Empty(Names(review, AccountCheck.Disabled));

        // Saved view: its base and filter.
        await harness.Views.SaveViewAsync(new SavedView
        {
            Id = "ksiegowosc",
            Name = "Księgowość",
            BaseDn = "OU=Księgowość,OU=Pracownicy,OU=Firma,{domain}",
            Filter = "(objectClass=user)",
        });
        review.ScopeKind = review.ScopeKinds.Single(k => k.Value == AccountReviewScopeKind.SavedView);
        review.SavedView = Assert.Single(review.SavedViews);
        await review.RunCommand.ExecuteAsync(null);
        Assert.Equal(["Małgorzata Wójcik"], Names(review, AccountCheck.PasswordExpired));
        Assert.Equal(["Zbigniew Kaźmierczak"], Names(review, AccountCheck.Disabled));
        Assert.Equal(["Wiesław Pękala"], Names(review, AccountCheck.NeverLoggedOn));
        Assert.Empty(Names(review, AccountCheck.Locked));

        // Members of Domain Admins, nested: Tadeusz is a member through GG_Admini_IT.
        review.ScopeKind = review.ScopeKinds.Single(k => k.Value == AccountReviewScopeKind.GroupMembers);
        Assert.Null(review.ScopeDn);
        harness.Dialogs.PickedObjects.Enqueue([harness.Builder.DomainAdminsDn]);
        await review.BrowseScopeCommand.ExecuteAsync(null);
        await review.RunCommand.ExecuteAsync(null);
        Assert.Null(review.Error);
        Assert.Equal(["Administrator", "Tadeusz Ślęzak"], Names(review, AccountCheck.PrivilegedWithProblem));
        Assert.Equal(["Tadeusz Ślęzak"], Names(review, AccountCheck.PasswordNotRequired));
        Assert.Empty(Names(review, AccountCheck.Locked));
        var main = harness.Session.Operations.Last(o => o.Kind == FakeOperationKind.Search && o.Search!.Filter.Contains("805306368", StringComparison.Ordinal)).Search!;
        Assert.Contains($"(memberOf:1.2.840.113556.1.4.1941:={harness.Builder.DomainAdminsDn})", main.Filter, StringComparison.Ordinal);
        Assert.Contains("(primaryGroupID=512)", main.Filter, StringComparison.Ordinal);

        // A group that disappeared: a clear error instead of an empty review.
        await harness.Session.DeleteAsync(scenario.AdminsIt, subtree: false, Ct);
        review.ScopeDn = scenario.AdminsIt;
        await review.RunCommand.ExecuteAsync(null);
        Assert.StartsWith("The group contoso.test/Firma/Grupy/GG_Admini_IT was not found", review.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unlock_AndDisable_GoThroughTheChangePlan_AndTheListsFollow()
    {
        var scenario = await ReviewScenario.CreateAsync();
        var harness = scenario.Harness;
        using var review = await scenario.RunAsync();

        review.SelectedItem = Row(review, AccountCheck.Locked, "Żaneta Szymańska");
        Assert.True(review.ShowUnlock);
        Assert.True(review.UnlockCommand.CanExecute(null));
        await review.UnlockCommand.ExecuteAsync(null);
        await review.Refreshing;

        Assert.Equal("0", harness.Value(scenario.Company.ZanetaSzymanska, Attr.LockoutTime));
        Assert.Empty(review.Items);
        Assert.Equal(0, review.Tiles.Single(t => t.Check == AccountCheck.Locked).Count);
        Assert.Contains(harness.AuditLog.Entries, e => e.DistinguishedName == scenario.Company.ZanetaSzymanska && e.Succeeded);

        // Disabling an inactive account moves it to the disabled lists (re-read and re-evaluated, no new run).
        review.SelectedItem = Row(review, AccountCheck.Inactive, "Paweł Lewandowski");
        Assert.True(review.ShowDisable);
        await review.DisableCommand.ExecuteAsync(null);
        await review.Refreshing;
        Assert.Empty(Names(review, AccountCheck.Inactive));
        Assert.Contains("Paweł Lewandowski", Names(review, AccountCheck.Disabled));
        Assert.Contains("Paweł Lewandowski", Names(review, AccountCheck.DisabledInGroups));
        Assert.Empty(Names(review, AccountCheck.AccountExpiring));
    }

    [Fact]
    public async Task Lists_HandOverToTheBatchEditor_WithTheOperationOfTheCheck()
    {
        var scenario = await ReviewScenario.CreateAsync();
        var harness = scenario.Harness;
        using var review = await scenario.RunAsync();

        // Whole list (nothing selected) to the batch editor.
        review.SelectedTile = review.Tiles.Single(t => t.Check == AccountCheck.NoManager);
        await review.BatchEditCommand.ExecuteAsync(null);
        var batch = harness.Dialogs.BatchEditorRequests[^1];
        Assert.Equal(5, batch.TargetDns.Count);
        Assert.Empty(batch.Operations);

        // Extend: the account expiration at the end of the day in 90 days (13 October + 90 = 11 January), editable.
        review.SelectedTile = review.Tiles.Single(t => t.Check == AccountCheck.AccountExpiring);
        Assert.True(review.ShowExtend);
        await review.ExtendCommand.ExecuteAsync(null);
        var extend = harness.Dialogs.BatchEditorRequests[^1];
        Assert.Equal([scenario.Company.PawelLewandowski], extend.TargetDns);
        Assert.False(extend.OpenPreview);
        Assert.Equal(new DateTimeOffset(2027, 1, 12, 0, 0, 0, TimeSpan.Zero), Assert.IsType<SetAccountExpirationOperation>(Assert.Single(extend.Operations)).ExpiresAt);

        // Leftovers: remove from all their groups, reviewed in the preview.
        review.SelectedTile = review.Tiles.Single(t => t.Check == AccountCheck.DisabledInGroups);
        await review.RemoveFromGroupsCommand.ExecuteAsync(null);
        var remove = harness.Dialogs.BatchEditorRequests[^1];
        Assert.True(remove.OpenPreview);
        Assert.Equal([scenario.Company.GroupAccounting], Assert.IsType<RemoveFromGroupsOperation>(Assert.Single(remove.Operations)).GroupDns);

        review.SelectedTile = review.Tiles.Single(t => t.Check == AccountCheck.DisabledInGal);
        Assert.True(review.ShowHideFromGal);
        await review.HideFromGalCommand.ExecuteAsync(null);
        var hide = Assert.IsType<SetAttributeOperation>(Assert.Single(harness.Dialogs.BatchEditorRequests[^1].Operations));
        Assert.Equal((Attr.HideFromAddressLists, "TRUE"), (hide.Attribute, hide.ValueTemplate));

        review.SelectedTile = review.Tiles.Single(t => t.Check == AccountCheck.PasswordNeverExpires);
        await review.ClearFlagCommand.ExecuteAsync(null);
        var flag = Assert.IsType<SetAccountFlagOperation>(Assert.Single(harness.Dialogs.BatchEditorRequests[^1].Operations));
        Assert.Equal((UserAccountControl.DontExpirePassword, false), (flag.Flag, flag.Value));
        Assert.Equal(2, harness.Dialogs.BatchEditorRequests[^1].TargetDns.Count);

        // Properties of the selected row.
        review.SelectedItem = review.Items[0];
        await review.OpenCommand.ExecuteAsync(null);
        Assert.Equal(review.Items[0].Dn, harness.Dialogs.PropertiesShown[^1]);
    }

    [Fact]
    public async Task ExportCsv_WritesTheListWithItsColumns_OnlyToTheChosenFile()
    {
        var scenario = await ReviewScenario.CreateAsync(CultureInfo.GetCultureInfo("pl-PL"));
        var harness = scenario.Harness;
        using var review = await scenario.RunAsync();
        review.SelectedTile = review.Tiles.Single(t => t.Check == AccountCheck.PasswordExpiring);
        var path = Path.Combine(Path.GetTempPath(), $"admng-review-{Guid.NewGuid():N}.csv");
        harness.Files.NextSavePath = path;
        try
        {
            await review.ExportCsvCommand.ExecuteAsync(null);

            Assert.StartsWith("Hasło wkrótce wygaśnie_", harness.Files.LastDefaultFileName, StringComparison.Ordinal);
            var lines = await File.ReadAllLinesAsync(path, Ct);
            Assert.Equal("Nazwa;Nazwa logowania;Hasło wygasa;Kiedy;Ostatnia zmiana hasła;E-mail;Lokalizacja;Nazwa wyróżniająca", lines[0]);
            Assert.Equal(3, lines.Length);
            Assert.Contains(lines, l => l.StartsWith("Łukasz Wiśniewski;lukasz.wisniewski;", StringComparison.Ordinal) && l.Contains(";za 5 dni;", StringComparison.Ordinal));
            Assert.Contains(harness.Notifications.Items, n => n.Severity == NotificationSeverity.Success);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CheckSets_AreSavedByName_WithChecksParametersAndScope_AndApplied()
    {
        var scenario = await ReviewScenario.CreateAsync();
        var harness = scenario.Harness;
        using (var review = harness.Factory.CreateAccountReview())
        {
            await review.InitializeAsync();
            Assert.Equal(14, review.PasswordExpiringDays);
            Assert.Equal(30, review.AccountExpiringDays);
            Assert.Equal(90, review.InactiveDays);
            Assert.Equal(365, review.PasswordAgeDays);
            review.PasswordExpiringDays = 7;
            review.PasswordAgeDays = 30;
            foreach (var option in review.CheckOptions)
            {
                option.IsEnabled = option.Check is AccountCheck.Locked or AccountCheck.PasswordExpiring or AccountCheck.PasswordOld;
            }

            review.ScopeKind = review.ScopeKinds.Single(k => k.Value == AccountReviewScopeKind.OrganizationalUnit);
            review.ScopeDn = scenario.Company.EmployeesOu;
            review.CheckSetName = "Poranny przegląd helpdesku";
            await review.SaveCheckSetCommand.ExecuteAsync(null);
            await review.RunCommand.ExecuteAsync(null);
            Assert.Equal(["Łukasz Wiśniewski"], Names(review, AccountCheck.PasswordExpiring));
            Assert.Contains("Jan Kowalski", Names(review, AccountCheck.PasswordOld));
        }

        var set = Assert.Single(harness.Settings.Current.AccountReview.CheckSets);
        Assert.Equal("Poranny przegląd helpdesku", set.Name);
        Assert.Equal([AccountCheck.Locked, AccountCheck.PasswordExpiring, AccountCheck.PasswordOld], set.Checks);
        Assert.Equal(7, set.Parameters.ExpiryWarningDays);
        Assert.Equal(new AccountReviewScope { Kind = AccountReviewScopeKind.OrganizationalUnit, Dn = "OU=Pracownicy,OU=Firma,{domain}" }, set.Scope);
        Assert.Equal(set.Scope, harness.Settings.Current.AccountReview.Scope);

        using var next = harness.Factory.CreateAccountReview();
        await next.InitializeAsync();
        Assert.Equal(7, next.PasswordExpiringDays);
        Assert.Equal(scenario.Company.EmployeesOu, next.ScopeDn);
        foreach (var option in next.CheckOptions)
        {
            option.IsEnabled = true;
        }

        next.PasswordExpiringDays = 14;
        next.ScopeKind = next.ScopeKinds[0];
        next.SelectedCheckSet = Assert.Single(next.CheckSets);
        Assert.Equal(7, next.PasswordExpiringDays);
        Assert.Equal(30, next.PasswordAgeDays);
        Assert.Equal(AccountReviewScopeKind.OrganizationalUnit, next.ScopeKind.Value);
        Assert.Equal(scenario.Company.EmployeesOu, next.ScopeDn);
        Assert.Equal(3, next.CheckOptions.Count(o => o.IsEnabled));

        harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await next.DeleteCheckSetCommand.ExecuteAsync(null);
        Assert.Empty(harness.Settings.Current.AccountReview.CheckSets);
        Assert.Empty(next.CheckSets);
    }

    [Fact]
    public async Task ExactLastLogon_ReadsEveryDomainController_AndDropsTheAccountsThatAreActiveAfterAll()
    {
        var scenario = await ReviewScenario.CreateAsync();
        var harness = scenario.Harness;
        var session = harness.Session;
        var now = harness.Time.GetUtcNow();
        DomainControllerSeed.Add(session, "dc01.contoso.test", pdc: true);
        DomainControllerSeed.Add(session, "dc02.contoso.test");
        DomainControllerSeed.Add(session, "dc03.contoso.test");
        session.SetValues(scenario.Wieslaw, Attr.LastLogon, ReviewScenario.FileTime(now.AddDays(-5)));
        var opened = new List<HookedSession>();
        harness.ConnectToServer = (current, server, _) =>
        {
            if (server == "dc03.contoso.test")
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "dc03 is down");
            }

            var other = new HookedSession(new FakeDomainControllerSession(session, server));
            other.Extra[scenario.Company.PawelLewandowski] = (Attr.LastLogon, ReviewScenario.FileTime(now.AddDays(-2)));
            opened.Add(other);
            return Task.FromResult<IDirectorySession>(other);
        };
        using var review = scenario.FactoryWithConnector().CreateAccountReview();
        await review.InitializeAsync();
        await review.RunCommand.ExecuteAsync(null);

        review.SelectedTile = review.Tiles.Single(t => t.Check == AccountCheck.Inactive);
        Assert.True(review.ShowExactLastLogon);
        Assert.True(review.ReadExactLastLogonCommand.CanExecute(null));
        await review.ReadExactLastLogonCommand.ExecuteAsync(null);

        Assert.Empty(harness.Dialogs.Errors);
        Assert.Empty(review.Items);
        Assert.Equal(0, review.Tiles.Single(t => t.Check == AccountCheck.Inactive).Count);
        var (severity, message) = harness.Notifications.Items[^1];
        Assert.Equal(NotificationSeverity.Warning, severity);
        Assert.StartsWith("Exact last logon read from 2 of 3 domain controllers; 1 accounts are active after all.", message, StringComparison.Ordinal);
        Assert.Contains("dc03.contoso.test:", message, StringComparison.Ordinal);
        Assert.All(opened, s => Assert.True(s.IsDisposed));

        // Paweł is still a finding of other checks, now with the exact value and its DC.
        var pawel = Row(review, AccountCheck.AccountExpiring, "Paweł Lewandowski");
        Assert.Equal("dc02.contoso.test", pawel.Facts.ExactLastLogonServer);

        // Never logged on: Wiesław logged on 5 days ago at the current DC (lastLogon is not replicated).
        review.SelectedTile = review.Tiles.Single(t => t.Check == AccountCheck.NeverLoggedOn);
        await review.ReadExactLastLogonCommand.ExecuteAsync(null);
        Assert.Empty(review.Items);
        Assert.Contains("Wiesław Pękala", Names(review, AccountCheck.NoMail));
    }

    [Fact]
    public async Task Stop_CancelsTheStream_AndKeepsWhatWasEvaluated()
    {
        var scenario = await ReviewScenario.CreateOnDomainControllerAsync();
        var harness = scenario.Harness;
        var hooked = new HookedSession(new FakeDomainControllerSession(harness.Session, "dc01.contoso.test")) { HoldAfter = 3 };
        await harness.ReconnectAsync(hooked);
        using var review = harness.Factory.CreateAccountReview();
        await review.InitializeAsync();

        var run = review.RunCommand.ExecuteAsync(null);
        await hooked.Held.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(review.IsRunning);
        Assert.True(review.StopCommand.CanExecute(null));
        Assert.EndsWith("…", review.Tiles[0].CountText, StringComparison.Ordinal);
        review.StopCommand.Execute(null);
        await run;

        Assert.False(review.IsRunning);
        Assert.Null(review.Error);
        Assert.StartsWith($"Stopped (as of {Noon("en-US")}) · 3 accounts read · ", review.StatusText, StringComparison.Ordinal);
        Assert.All(review.Tiles, t => Assert.False(t.IsComplete));
        Assert.True(review.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task SessionEnded_KeepsTheResults_ButRefusesRunsAndChanges()
    {
        var scenario = await ReviewScenario.CreateAsync();
        var harness = scenario.Harness;
        using var review = await scenario.RunAsync();
        review.SelectedItem = Row(review, AccountCheck.Locked, "Żaneta Szymańska");

        await harness.ReconnectAsync(new FakeDirectoryBuilder(timeProvider: harness.Time).Session);

        Assert.True(review.IsSessionEnded);
        Assert.NotNull(review.SessionEndedMessage);
        Assert.Single(review.Items);
        Assert.False(review.RunCommand.CanExecute(null));
        Assert.False(review.UnlockCommand.CanExecute(null));
        await review.UnlockCommand.ExecuteAsync(null);
        Assert.NotEqual("0", harness.Value(scenario.Company.ZanetaSzymanska, Attr.LockoutTime));
    }

    [Fact]
    public async Task SendMessage_IsTheU1Hook_HiddenUntilALauncherIsRegistered()
    {
        var scenario = await ReviewScenario.CreateAsync();
        var harness = scenario.Harness;
        using var review = await scenario.RunAsync();
        review.SelectedTile = review.Tiles.Single(t => t.Check == AccountCheck.PasswordExpiring);
        Assert.False(review.CanSendMessages);
        Assert.False(review.SendMessageCommand.CanExecute(null));

        var launcher = new RecordingMessageLauncher();
        using var withU1 = scenario.FactoryWithConnector(launcher).CreateAccountReview();
        await withU1.InitializeAsync();
        await withU1.RunCommand.ExecuteAsync(null);
        withU1.SelectedTile = withU1.Tiles.Single(t => t.Check == AccountCheck.PasswordExpiring);
        Assert.True(withU1.CanSendMessages);
        await withU1.SendMessageCommand.ExecuteAsync(null);
        var (dns, reason) = Assert.Single(launcher.Requests);
        Assert.Equal(2, dns.Count);
        Assert.Equal(AccountCheck.PasswordExpiring, reason);
    }

    [Fact]
    public async Task Polish_TilesColumnsAndRelativeDays()
    {
        var scenario = await ReviewScenario.CreateAsync(CultureInfo.GetCultureInfo("pl-PL"));
        using var review = await scenario.RunAsync();

        Assert.Equal("Przegląd kont – contoso.test", review.WindowTitle);
        var tile = review.Tiles.Single(t => t.Check == AccountCheck.DisabledInGroups);
        Assert.Equal("Wyłączone, nadal w grupach", tile.Title);
        Assert.Equal("Pozostałości wyłączonych kont", tile.AreaTitle);
        var lukasz = Row(review, AccountCheck.PasswordExpiring, "Łukasz Wiśniewski");
        Assert.Equal("za 5 dni", lukasz[AccountReviewColumns.PasswordDays]);
        Assert.Equal("Kiedy", review.Columns[3].Header);
        Assert.StartsWith("Stan na godz. 12:00 · ", review.StatusText, StringComparison.Ordinal);
        Assert.Equal(["Cała domena", "Jednostka organizacyjna (z poddrzewem)", "Zapisany widok", "Członkowie grupy (zagnieżdżeni)"],
            review.ScopeKinds.Select(k => k.Text));
    }

    private static string Noon(string culture) => new DateTime(2026, 10, 13, 12, 0, 0).ToString("t", CultureInfo.GetCultureInfo(culture));

    [Fact]
    public void DynamicKeys_ExistInBothLanguages()
    {
        var keys = Enum.GetValues<AccountCheck>().SelectMany(c => new[] { $"AccountReview.Check.{c}", $"AccountReview.Hint.{c}" })
            .Concat(Enum.GetValues<AccountCheckArea>().Select(a => $"AccountReview.Area.{a}"))
            .Concat(AccountReviewColumns.All.Select(c => $"AccountReview.Column.{c}"))
            .Concat(Enum.GetValues<AccountReviewScopeKind>().Select(k => $"AccountReview.Scope.{k}"))
            .ToList();
        foreach (var culture in new[] { "en-US", "pl-PL" })
        {
            var localizer = new ResxLocalizer(CultureInfo.GetCultureInfo(culture));
            Assert.All(keys, k => Assert.False(localizer[k].StartsWith('['), $"{culture}: {k}"));
        }
    }
}
