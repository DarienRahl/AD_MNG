using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PropertySheets;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Dialogs;

public class PropertySheetTests
{
    private static async Task<(PropertySheetViewModel Sheet, B2Harness Harness)> OpenAsync(Func<SampleCompany, string> target, CultureInfo? culture = null)
    {
        var harness = await B2Harness.CreateAsync(culture);
        var sheet = harness.Factory.CreatePropertySheet(target(harness.Company));
        await sheet.InitializeAsync();
        return (sheet, harness);
    }

    private static FieldsPageViewModel Page(PropertySheetViewModel sheet, string key) =>
        Assert.IsType<FieldsPageViewModel>(Assert.Single(sheet.Pages, p => p.Key == key));

    private static T Field<T>(PropertySheetViewModel sheet, string key, string attribute)
        where T : PropertyFieldViewModel =>
        Assert.Single(Page(sheet, key).Fields.OfType<T>(), f => f.Attribute == attribute);

    private static FieldViewModel Text(PropertySheetViewModel sheet, string key, string attribute) => Field<FieldViewModel>(sheet, key, attribute);

    [Fact]
    public async Task User_HasAducPagesInOrder_AndLiveValues()
    {
        var (sheet, _) = await OpenAsync(c => c.LukaszWisniewski);

        Assert.Equal(
            [L.Pages.General, L.Pages.Address, L.Pages.Account, L.Pages.Profile, L.Pages.Telephones, L.Pages.Organization,
             L.Pages.MemberOf, L.Pages.Object, L.Pages.AttributeEditor],
            sheet.Pages.Select(p => p.Key));
        Assert.Equal("Łukasz Wiśniewski Properties", sheet.WindowTitle);
        Assert.Equal(ObjectKind.User, sheet.Kind);
        Assert.Equal("contoso.test/Firma/Pracownicy/IT", sheet.Path);
        Assert.Equal("Łukasz", Text(sheet, L.Pages.General, Attr.GivenName).Value);
        Assert.Equal("Wiśniewski", Text(sheet, L.Pages.General, Attr.Sn).Value);
        Assert.Equal("IT", Text(sheet, L.Pages.Organization, Attr.Department).Value);
        var manager = Field<DnFieldViewModel>(sheet, L.Pages.Organization, Attr.Manager);
        Assert.Equal("Jan Kowalski", manager.DisplayName);
        var upn = Field<UpnFieldViewModel>(sheet, L.Pages.Account, Attr.UserPrincipalName);
        Assert.Equal("lukasz.wisniewski", upn.Prefix);
        Assert.Equal("contoso.test", upn.Suffix);
        Assert.False(sheet.HasChanges);
        Assert.False(sheet.IsLoading);
        Assert.Null(sheet.LoadError);
    }

    [Fact]
    public async Task Polish_UsesPolishTitlesAndAttributeNames()
    {
        var (sheet, _) = await OpenAsync(c => c.JanKowalski, CultureInfo.GetCultureInfo("pl-PL"));

        Assert.Equal("Właściwości: Jan Kowalski", sheet.WindowTitle);
        Assert.Equal("Ogólne", sheet.Pages[0].Title);
        Assert.Equal("Imię", Text(sheet, L.Pages.General, Attr.GivenName).Label);
        Assert.Equal("Opcje konta", Page(sheet, L.Pages.Account).Sections[1].Header);
    }

    [Fact]
    public async Task Apply_WritesChangedFieldsThroughThePipeline_AndReloads()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        var published = new List<DirectoryChange>();
        harness.Changes.Changed += (_, e) => published.AddRange(e.Changes);
        var changedEvents = 0;
        sheet.ObjectChanged += (_, _) => changedEvents++;

        Text(sheet, L.Pages.Organization, Attr.Title).Value = "Kierownik działu IT";
        Text(sheet, L.Pages.General, Attr.Description).Value = "Opis z polskimi znakami: zażółć gęślą jaźń";
        Text(sheet, L.Pages.General, Attr.Office).Value = "  ";
        Assert.True(sheet.HasChanges);
        Assert.True(sheet.ApplyCommand.CanExecute(null));

        var applied = await sheet.ApplyChangesAsync();

        Assert.True(applied);
        Assert.Null(sheet.ApplyError);
        Assert.Equal("Kierownik działu IT", harness.Value(harness.Company.JanKowalski, Attr.Title));
        Assert.Equal("Opis z polskimi znakami: zażółć gęślą jaźń", harness.Value(harness.Company.JanKowalski, Attr.Description));
        Assert.False(sheet.HasChanges);
        Assert.Equal("Kierownik działu IT", Text(sheet, L.Pages.Organization, Attr.Title).OriginalValue);
        Assert.Contains(published, c => c.Kind == DirectoryChangeKind.Modified && c.DistinguishedName == harness.Company.JanKowalski);
        Assert.Contains(harness.AuditLog.Entries, e => e.Attribute == Attr.Title && e.NewValue == "Kierownik działu IT" && e.Succeeded);
        Assert.Single(harness.Activities.Activities);
        Assert.Equal(1, changedEvents);
    }

    [Fact]
    public async Task LiveValidation_BlocksApply_AndRevertRestores()
    {
        var (sheet, _) = await OpenAsync(c => c.JanKowalski);
        var givenName = Text(sheet, L.Pages.General, Attr.GivenName);

        givenName.Value = new string('x', 65);

        Assert.Equal("The value is too long (maximum 64 characters).", givenName.Error);
        Assert.True(sheet.HasErrors);
        Assert.False(sheet.ApplyCommand.CanExecute(null));
        Assert.Equal(64, givenName.MaxLength);

        givenName.Revert();

        Assert.Equal("Jan", givenName.Value);
        Assert.False(sheet.HasErrors);
        Assert.False(sheet.HasChanges);
    }

    [Fact]
    public async Task PlannerFindings_AreShownOnTheField_AndNothingIsWritten()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        var sam = Text(sheet, L.Pages.Account, Attr.SamAccountName);
        harness.Session.ClearOperations();

        sam.Value = "lukasz.wisniewski";
        var applied = await sheet.ApplyChangesAsync();

        Assert.False(applied);
        Assert.NotNull(sheet.ApplyError);
        Assert.True(sam.IsIssueError);
        Assert.Equal("The value of 'Logon Name (pre-Windows 2000)' is already used by another object.", sam.Issue);
        Assert.Empty(harness.Session.Writes);
        Assert.Equal("jan.kowalski", harness.Value(harness.Company.JanKowalski, Attr.SamAccountName));

        // Editing the field clears the finding.
        sam.Value = "jan.kowalski2";
        Assert.Null(sam.Issue);
        Assert.True(await sheet.ApplyChangesAsync());
        Assert.Equal("jan.kowalski2", harness.Value(harness.Company.JanKowalski, Attr.SamAccountName));
    }

    [Fact]
    public async Task AccountOptions_ExpiryAndMustChange_UseTheDedicatedOperations()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        var neverExpires = Assert.Single(Page(sheet, L.Pages.Account).Fields.OfType<FlagFieldViewModel>(), f => f.Flag == UserAccountControl.DontExpirePassword);
        var disabled = Assert.Single(Page(sheet, L.Pages.Account).Fields.OfType<FlagFieldViewModel>(), f => f.Flag == UserAccountControl.AccountDisable);
        var expiry = Field<ExpiryFieldViewModel>(sheet, L.Pages.Account, Attr.AccountExpires);
        Assert.True(expiry.IsNever);

        neverExpires.IsChecked = true;
        disabled.IsChecked = true;
        expiry.IsNever = false;
        expiry.EndOfDate = new DateTime(2026, 12, 31);
        Assert.True(await sheet.ApplyChangesAsync());

        var uac = (UserAccountControl)int.Parse(harness.Value(harness.Company.JanKowalski, Attr.UserAccountControl)!, CultureInfo.InvariantCulture);
        Assert.True(uac.HasFlag(UserAccountControl.DontExpirePassword));
        Assert.True(uac.HasFlag(UserAccountControl.AccountDisable));
        var expires = AdConvert.FromFileTime(harness.Value(harness.Company.JanKowalski, Attr.AccountExpires));
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), expires);

        // Reloaded: "end of" 2026-12-31 again.
        expiry = Field<ExpiryFieldViewModel>(sheet, L.Pages.Account, Attr.AccountExpires);
        Assert.False(expiry.IsNever);
        Assert.Equal(new DateTime(2026, 12, 31), expiry.EndOfDate);
    }

    [Fact]
    public async Task MustChangePasswordWithNeverExpires_IsAWarningOnTheField()
    {
        var (sheet, harness) = await OpenAsync(c => c.GrazynaDabrowska);
        var mustChange = Assert.Single(Page(sheet, L.Pages.Account).Fields.OfType<CheckFieldViewModel>(), f => f.Attribute == Attr.PwdLastSet);

        mustChange.IsChecked = true;
        Assert.True(await sheet.ApplyChangesAsync());

        Assert.Equal("0", harness.Value(harness.Company.GrazynaDabrowska, Attr.PwdLastSet));
        var warning = Assert.Single(sheet.Issues);
        Assert.False(warning.IsError);
        Assert.Equal(harness.Localizer[DisplayNames.IssueKey(IssueCodes.PasswordNeverExpiresConflict)], warning.Text);
        Assert.Null(mustChange.Issue);
    }

    [Fact]
    public async Task UnlockCheckbox_IsOnlyShownForLockedAccounts_AndUnlocks()
    {
        var (locked, harness) = await OpenAsync(c => c.ZanetaSzymanska);
        var unlock = Assert.Single(Page(locked, L.Pages.Account).Fields.OfType<CheckFieldViewModel>(), f => f.Attribute == Attr.LockoutTime);
        Assert.True(unlock.IsVisible);

        unlock.IsChecked = true;
        Assert.True(await locked.ApplyChangesAsync());
        Assert.Equal("0", harness.Value(harness.Company.ZanetaSzymanska, Attr.LockoutTime));

        var notLocked = harness.Factory.CreatePropertySheet(harness.Company.JanKowalski);
        await notLocked.InitializeAsync();
        Assert.False(Assert.Single(Page(notLocked, L.Pages.Account).Fields.OfType<CheckFieldViewModel>(), f => f.Attribute == Attr.LockoutTime).IsVisible);
    }

    [Theory]
    [InlineData("en-US", "Poland")]
    [InlineData("pl-PL", "Polska")]
    public async Task CountryPicker_WritesCodeNameAndNumericCodeTogether(string culture, string expectedName)
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski, CultureInfo.GetCultureInfo(culture));
        var country = Field<CountryFieldViewModel>(sheet, L.Pages.Address, Attr.Country);
        Assert.Null(country.SelectedOption?.Country);

        country.SelectedOption = Assert.Single(country.Options, o => o.Country?.Code == "PL");
        Assert.True(await sheet.ApplyChangesAsync());

        Assert.Equal("PL", harness.Value(harness.Company.JanKowalski, Attr.Country));
        Assert.Equal(expectedName, harness.Value(harness.Company.JanKowalski, Attr.CountryName));
        Assert.Equal("616", harness.Value(harness.Company.JanKowalski, Attr.CountryCode));
        Assert.Equal("PL", Field<CountryFieldViewModel>(sheet, L.Pages.Address, Attr.Country).SelectedOption?.Country?.Code);
    }

    [Fact]
    public async Task OtherTelephones_AreEditedAsAList()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        var phone = Text(sheet, L.Pages.General, Attr.TelephoneNumber);
        var others = Assert.IsType<MultiValueFieldViewModel>(phone.Others);

        phone.Value = "+48 22 100 00 01";
        others.NewValue = "+48 22 100 00 02";
        others.AddCommand.Execute(null);
        others.NewValue = "+48 22 100 00 02";
        others.AddCommand.Execute(null);
        Assert.Equal("This value is already in the list.", others.NewValueError);
        Assert.True(await sheet.ApplyChangesAsync());

        Assert.Equal(["+48 22 100 00 02"], harness.Values(harness.Company.JanKowalski, Attr.OtherTelephone));
        Assert.Equal("+48 22 100 00 01", harness.Value(harness.Company.JanKowalski, Attr.TelephoneNumber));
    }

    [Fact]
    public async Task MemberOf_ShowsGroupsAndPrimaryGroup_AndStagesChanges()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        var page = Assert.IsType<MemberOfPageViewModel>(Assert.Single(sheet.Pages, p => p.Key == L.Pages.MemberOf));

        Assert.Equal(["Domain Users", "GG_IT"], page.Items.Select(i => i.Name));
        var primary = Assert.Single(page.Items, i => i.IsPrimary);
        Assert.Equal("Domain Users", primary.Name);
        Assert.Equal("Domain Users", page.PrimaryGroupName);
        Assert.Equal("Groups: 2", page.CountText);

        page.SelectedItem = primary;
        Assert.False(page.RemoveCommand.CanExecute(null));

        harness.Dialogs.PickedObjects.Enqueue([harness.Company.GroupSales]);
        await page.AddCommand.ExecuteAsync(null);
        page.SelectedItem = Assert.Single(page.Items, i => i.Name == "GG_IT");
        page.RemoveCommand.Execute(null);

        Assert.Equal("Will be added", Assert.Single(page.Items, i => i.Name == "GG_Sprzedaz").StatusText);
        Assert.True(Assert.Single(page.Items, i => i.Name == "GG_IT").IsPendingRemove);
        Assert.True(sheet.HasChanges);
        Assert.True(await sheet.ApplyChangesAsync());

        Assert.Contains(harness.Company.JanKowalski, harness.Values(harness.Company.GroupSales, Attr.Member), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(harness.Company.JanKowalski, harness.Values(harness.Company.GroupIt, Attr.Member), StringComparer.OrdinalIgnoreCase);
        page = Assert.IsType<MemberOfPageViewModel>(Assert.Single(sheet.Pages, p => p.Key == L.Pages.MemberOf));
        Assert.Equal(["Domain Users", "GG_Sprzedaz"], page.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task MemberOf_SetPrimaryGroup_FollowsTheSelection_AndWritesPrimaryGroupId()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        var page = Assert.IsType<MemberOfPageViewModel>(Assert.Single(sheet.Pages, p => p.Key == L.Pages.MemberOf));
        var notifications = 0;
        page.SetPrimaryGroupCommand.CanExecuteChanged += (_, _) => notifications++;

        // The button must be re-evaluated when the selection changes (WPF only asks again on CanExecuteChanged).
        page.SelectedItem = Assert.Single(page.Items, i => i.Name == "GG_IT");
        Assert.True(notifications > 0);
        Assert.True(page.SetPrimaryGroupCommand.CanExecute(null));
        page.SelectedItem = Assert.Single(page.Items, i => i.IsPrimary);
        Assert.False(page.SetPrimaryGroupCommand.CanExecute(null));

        page.SelectedItem = Assert.Single(page.Items, i => i.Name == "GG_IT");
        page.SetPrimaryGroupCommand.Execute(null);
        Assert.Equal("GG_IT", page.PrimaryGroupName);
        Assert.True(sheet.HasChanges);
        Assert.True(await sheet.ApplyChangesAsync());

        Assert.NotEqual("513", harness.Value(harness.Company.JanKowalski, Attr.PrimaryGroupId));
        page = Assert.IsType<MemberOfPageViewModel>(Assert.Single(sheet.Pages, p => p.Key == L.Pages.MemberOf));
        Assert.Equal("GG_IT", page.PrimaryGroupName);
        Assert.Equal("GG_IT", Assert.Single(page.Items, i => i.IsPrimary).Name);
    }

    [Fact]
    public async Task MemberOf_FilterAndNestedMemberships()
    {
        var (sheet, _) = await OpenAsync(c => c.JanKowalski);
        var page = Assert.IsType<MemberOfPageViewModel>(Assert.Single(sheet.Pages, p => p.Key == L.Pages.MemberOf));

        page.FilterText = "gg_";
        Assert.Equal(["GG_IT"], page.Items.Select(i => i.Name));

        await page.LoadNestedCommand.ExecuteAsync(null);

        Assert.True(page.IsNestedLoaded);
        Assert.Equal(["DL_Wszyscy", "GG_IT"], page.NestedItems.Select(i => i.Name));
    }

    [Fact]
    public async Task Group_MembersPage_LoadsResolvesAndStagesMembers()
    {
        var (sheet, harness) = await OpenAsync(c => c.GroupAccounting);
        Assert.Equal(
            [L.Pages.General, L.Pages.Members, L.Pages.MemberOf, L.Pages.ManagedBy, L.Pages.Object, L.Pages.AttributeEditor],
            sheet.Pages.Select(p => p.Key));
        var members = Assert.IsType<MembersPageViewModel>(sheet.Pages[1]);
        Assert.Equal(["Grażyna Dąbrowska", "Małgorzata Wójcik", "Zbigniew Kaźmierczak"], members.Items.Select(i => i.Name));
        Assert.Contains(harness.Session.Operations, o => o.Kind == FakeOperationKind.GetAllValues && o.DistinguishedName == harness.Company.GroupAccounting);

        harness.Dialogs.PickedObjects.Enqueue([harness.Company.PawelLewandowski]);
        await members.AddCommand.ExecuteAsync(null);
        members.SelectedItem = Assert.Single(members.Items, i => i.Name == "Zbigniew Kaźmierczak");
        members.RemoveCommand.Execute(null);
        Assert.True(await sheet.ApplyChangesAsync());

        var stored = harness.Values(harness.Company.GroupAccounting, Attr.Member);
        Assert.Contains(harness.Company.PawelLewandowski, stored, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(harness.Company.ZbigniewKazmierczak, stored, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(3, stored.Count);
    }

    [Fact]
    public async Task Group_ScopeConversionFollowsAducRules()
    {
        var (sheet, harness) = await OpenAsync(c => c.GroupIt);
        var type = Field<GroupTypeFieldViewModel>(sheet, L.Pages.General, Attr.GroupType);

        Assert.Equal(GroupScope.Global, type.Scope);
        Assert.False(Assert.Single(type.ScopeOptions, o => o.Scope == GroupScope.DomainLocal).IsEnabled);
        Assert.True(Assert.Single(type.ScopeOptions, o => o.Scope == GroupScope.Universal).IsEnabled);

        type.IsDomainLocal = true;
        Assert.NotNull(type.Error);
        type.IsUniversal = true;
        type.IsDistribution = true;
        Assert.Null(type.Error);
        Assert.True(await sheet.ApplyChangesAsync());

        Assert.Equal(GroupTypes.Compose(GroupScope.Universal, security: false).ToString(CultureInfo.InvariantCulture),
            harness.Value(harness.Company.GroupIt, Attr.GroupType));
    }

    [Fact]
    public async Task Computer_GeneralShowsRole_AndDelegationIsSummarized()
    {
        var (sheet, harness) = await OpenAsync(c => c.Computer1);

        Assert.Equal(
            [L.Pages.General, L.Pages.OperatingSystem, L.Pages.MemberOf, L.Pages.Delegation, L.Pages.Location, L.Pages.ManagedBy,
             L.Pages.Object, L.Pages.AttributeEditor],
            sheet.Pages.Select(p => p.Key));
        var general = Page(sheet, L.Pages.General);
        Assert.Contains(general.Fields.OfType<FieldViewModel>(), f => f.Value == "Workstation or server" && f.IsInformational);
        Assert.Contains(general.Fields.OfType<FieldViewModel>(), f => f.Value == "WAW-PC001");
        Assert.Equal("Warszawa, pokój 101", Text(sheet, L.Pages.Location, Attr.Location).Value);

        var dc = harness.Factory.CreatePropertySheet(harness.Builder.DomainControllerDn);
        await dc.InitializeAsync();
        Assert.Contains(Page(dc, L.Pages.General).Fields.OfType<FieldViewModel>(), f => f.Value == "Domain controller");
        Assert.Contains(Page(dc, L.Pages.Delegation).Fields.OfType<FieldViewModel>(),
            f => f.Value == "Trust this computer for delegation to any service (Kerberos only)");
    }

    [Fact]
    public async Task ManagedBy_PicksAnObject_AndShowsItsDetails()
    {
        var (sheet, harness) = await OpenAsync(c => c.ItOu);
        Assert.Equal([L.Pages.General, L.Pages.ManagedBy, L.Pages.Object, L.Pages.AttributeEditor], sheet.Pages.Select(p => p.Key));
        var managedBy = Field<DnFieldViewModel>(sheet, L.Pages.ManagedBy, Attr.ManagedBy);
        Assert.False(managedBy.HasValue);

        harness.Dialogs.PickedObjects.Enqueue([harness.Company.JanKowalski]);
        await managedBy.BrowseCommand.ExecuteAsync(null);

        Assert.Equal("Jan Kowalski", managedBy.DisplayName);
        Assert.Contains(managedBy.Details, d => d.Value == "Warszawa");
        Assert.True(await sheet.ApplyChangesAsync());
        Assert.Equal(harness.Company.JanKowalski, harness.Value(harness.Company.ItOu, Attr.ManagedBy), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ObjectPage_ShowsIdentityValues_WithCopy()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        var page = Page(sheet, L.Pages.Object);
        var canonical = Assert.Single(page.Fields.OfType<FieldViewModel>(), f => f.Attribute == Attr.CanonicalName);

        Assert.Equal("contoso.test/Firma/Pracownicy/IT/Jan Kowalski", canonical.Value);
        Assert.True(page.Fields.All(f => f.IsReadOnly));
        Assert.Contains(page.Fields.OfType<FieldViewModel>(), f => f.Attribute == Attr.ObjectSid && f.Value.StartsWith("S-1-5-21-", StringComparison.Ordinal));
        Assert.Contains(page.Fields.OfType<FieldViewModel>(), f => f.Attribute == Attr.ObjectClass && f.Value == "user");

        canonical.CopyCommand.Execute(null);
        Assert.Equal("contoso.test/Firma/Pracownicy/IT/Jan Kowalski", harness.Clipboard.Text);
    }

    [Fact]
    public async Task ReadOnlyFields_ExplainWhy()
    {
        var harness = await B2Harness.CreateAsync();
        harness.Session.SetPermissions(harness.Company.JanKowalski, new EffectivePermissions
        {
            IsKnown = true,
            WritableAttributes = new HashSet<string>([Attr.Description, Attr.TelephoneNumber], StringComparer.OrdinalIgnoreCase),
        });
        var sheet = harness.Factory.CreatePropertySheet(harness.Company.JanKowalski);
        await sheet.InitializeAsync();

        var givenName = Text(sheet, L.Pages.General, Attr.GivenName);
        Assert.True(givenName.IsReadOnly);
        Assert.Equal("You do not have permission to change this value.", givenName.ReadOnlyReason);
        Assert.False(Text(sheet, L.Pages.General, Attr.Description).IsReadOnly);
        var canonical = Assert.Single(Page(sheet, L.Pages.Object).Fields, f => f.Attribute == Attr.CanonicalName);
        Assert.True(canonical.IsReadOnly);
    }

    [Fact]
    public async Task AttributeEditor_LoadsLazily_EditsAndAppliesLast()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        var editor = Assert.IsType<AttributeEditorPageViewModel>(sheet.Pages[^1]);
        Assert.Empty(editor.Rows);

        sheet.SelectedPage = editor;
        await B2Harness.WaitUntilAsync(() => editor.Rows.Count > 0);

        var department = Assert.Single(editor.Rows, r => r.Attribute == Attr.Department);
        Assert.Equal("IT", department.ValueText);
        Assert.Equal("Unicode String", department.SyntaxText);
        Assert.True(Assert.Single(editor.Rows, r => r.Attribute == Attr.ObjectGuid).IsReadOnly);
        Assert.Contains(editor.Rows, r => r.Attribute == Attr.Info && !r.HasValue);

        editor.ShowOnlyWithValues = true;
        Assert.DoesNotContain(editor.Rows, r => r.Attribute == Attr.Info);
        editor.ShowOnlyWithValues = false;

        // Same attribute on the General tab and in the Attribute Editor: the editor wins.
        Text(sheet, L.Pages.General, Attr.Description).Value = "z karty Ogólne";
        editor.EditCommand.Execute(Assert.Single(editor.Rows, r => r.Attribute == Attr.Description));
        var valueEditor = Assert.IsType<AttributeValueEditorViewModel>(editor.Editor);
        Assert.Equal(AttributeEditorKind.MultiText, valueEditor.Kind);
        valueEditor.NewValue = "z edytora atrybutów";
        valueEditor.AddValueCommand.Execute(null);
        valueEditor.OkCommand.Execute(null);
        Assert.Null(editor.Editor);

        editor.EditCommand.Execute(Assert.Single(editor.Rows, r => r.Attribute == Attr.Info));
        editor.Editor!.Value = "Notatka";
        editor.Editor.OkCommand.Execute(null);
        Assert.Equal("Notatka", Assert.Single(editor.Rows, r => r.Attribute == Attr.Info).ValueText);
        Assert.True(sheet.HasChanges);

        Assert.True(await sheet.ApplyChangesAsync());
        Assert.Equal("Notatka", harness.Value(harness.Company.JanKowalski, Attr.Info));
        Assert.Equal(["z edytora atrybutów"], harness.Values(harness.Company.JanKowalski, Attr.Description));
        await B2Harness.WaitUntilAsync(() => editor.Rows.Any(r => r.Attribute == Attr.Info && r.HasValue && !r.IsDirty));
    }

    [Fact]
    public async Task AttributeEditor_ValidatesBySyntax()
    {
        var (sheet, _) = await OpenAsync(c => c.JanKowalski);
        var editor = Assert.IsType<AttributeEditorPageViewModel>(sheet.Pages[^1]);
        await editor.ActivateAsync();

        editor.EditCommand.Execute(Assert.Single(editor.Rows, r => r.Attribute == Attr.CountryCode));
        var valueEditor = editor.Editor!;
        Assert.Equal(AttributeEditorKind.Integer, valueEditor.Kind);
        valueEditor.Value = "abc";
        valueEditor.OkCommand.Execute(null);

        Assert.Equal("Enter a whole number.", valueEditor.Error);
        Assert.Same(valueEditor, editor.Editor);
        valueEditor.CancelCommand.Execute(null);
        Assert.Null(editor.Editor);
        Assert.False(sheet.HasChanges);

        editor.EditCommand.Execute(Assert.Single(editor.Rows, r => r.Attribute == Attr.AccountExpires));
        Assert.Equal(AttributeEditorKind.FileTime, editor.Editor!.Kind);
        Assert.Equal("(never)", editor.Editor.Value);
    }

    [Fact]
    public async Task EmailPage_EnforcesOnePrimarySmtp_AndSyncsMail()
    {
        var harness = await B2Harness.CreateAsync();
        harness.Session.SetValues(harness.Company.JanKowalski, Attr.ProxyAddresses, "SMTP:jan.kowalski@contoso.test", "smtp:jk@contoso.test");
        var sheet = harness.Factory.CreatePropertySheet(harness.Company.JanKowalski);
        await sheet.InitializeAsync();
        var page = Page(sheet, L.Pages.EmailAddresses);
        var proxies = Assert.Single(page.Fields.OfType<ProxyAddressesFieldViewModel>());
        var mail = Assert.Single(page.Fields.OfType<FieldViewModel>(), f => f.Attribute == Attr.Mail);

        proxies.NewType = "SMTP";
        proxies.NewAddress = "jan@firma.pl";
        proxies.AddCommand.Execute(null);
        var added = Assert.Single(proxies.Items, i => i.Address == "jan@firma.pl");
        Assert.Equal("smtp", added.Prefix);

        proxies.SelectedItem = added;
        proxies.SetPrimaryCommand.Execute(null);

        Assert.Equal("jan@firma.pl", proxies.PrimarySmtpAddress);
        Assert.Equal("jan@firma.pl", mail.Value);

        // One E-mail field shown on both tabs: the General tab cannot show (or write) a different address.
        Assert.Same(mail, Text(sheet, L.Pages.General, Attr.Mail));
        Assert.Single(proxies.Items, i => i.IsPrimary);
        Assert.True(await sheet.ApplyChangesAsync());
        Assert.Equal(
            ["SMTP:jan@firma.pl", "smtp:jan.kowalski@contoso.test", "smtp:jk@contoso.test"],
            harness.Values(harness.Company.JanKowalski, Attr.ProxyAddresses).Order(StringComparer.Ordinal));
        Assert.Equal("jan@firma.pl", harness.Value(harness.Company.JanKowalski, Attr.Mail));
    }

    [Fact]
    public async Task ChangeMadeElsewhere_ReloadsACleanSheet_AndFlagsADirtyOne()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        var change = new ChangeRequest([harness.Company.JanKowalski], [new SetAttributeOperation(Attr.Title, "Architekt")], "test");

        await harness.Coordinator.ApplyAsync(change, TestContext.Current.CancellationToken);
        await B2Harness.WaitUntilAsync(() => Text(sheet, L.Pages.Organization, Attr.Title).Value == "Architekt");
        Assert.False(sheet.IsOutdated);

        Text(sheet, L.Pages.General, Attr.Initials).Value = "K";
        await harness.Coordinator.ApplyAsync(change with { Operations = [new SetAttributeOperation(Attr.Title, "Kierownik")] }, TestContext.Current.CancellationToken);

        Assert.True(sheet.IsOutdated);
        Assert.Equal("K", Text(sheet, L.Pages.General, Attr.Initials).Value);
    }

    [Fact]
    public async Task ValueChangedOnTheServerAfterOpening_IsNotOverwrittenWithoutAsking()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        harness.Session.SetValues(harness.Company.JanKowalski, Attr.Title, "Zmienione przez kogoś");
        Text(sheet, L.Pages.Organization, Attr.Title).Value = "Mój tytuł";
        Text(sheet, L.Pages.General, Attr.Initials).Value = "K";

        harness.Dialogs.ConfirmAnswers.Enqueue(false);
        Assert.False(await sheet.ApplyChangesAsync());

        var question = Assert.Single(harness.Dialogs.Shown, s => s.StartsWith("confirm:", StringComparison.Ordinal));
        Assert.Contains(harness.Localizer.AttributeName(Attr.Title, harness.Session.Schema), question, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.Localizer.AttributeName(Attr.Initials, harness.Session.Schema), question, StringComparison.Ordinal);
        Assert.True(sheet.IsOutdated);
        Assert.Equal(harness.Localizer[L.PropertySheet.Outdated], sheet.ApplyError);
        Assert.Equal("Zmienione przez kogoś", harness.Value(harness.Company.JanKowalski, Attr.Title));
        Assert.Null(harness.Value(harness.Company.JanKowalski, Attr.Initials));
        Assert.True(sheet.HasChanges);

        harness.Dialogs.ConfirmAnswers.Enqueue(true);
        Assert.True(await sheet.ApplyChangesAsync());

        Assert.Equal("Mój tytuł", harness.Value(harness.Company.JanKowalski, Attr.Title));
        Assert.Equal("K", harness.Value(harness.Company.JanKowalski, Attr.Initials));
        Assert.False(sheet.IsOutdated);
    }

    [Fact]
    public async Task Cancel_AsksBeforeDiscardingChanges()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        bool? closed = null;
        sheet.CloseRequested += (_, result) => closed = result;
        Text(sheet, L.Pages.General, Attr.Initials).Value = "K";

        harness.Dialogs.ConfirmAnswers.Enqueue(false);
        await sheet.CancelCommand.ExecuteAsync(null);
        Assert.Null(closed);
        Assert.False(await sheet.CanCloseAsync());

        harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await sheet.CancelCommand.ExecuteAsync(null);
        Assert.False(closed);
        Assert.Null(harness.Value(harness.Company.JanKowalski, Attr.Initials));
    }

    [Fact]
    public async Task Ok_AppliesAndCloses()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        bool? closed = null;
        sheet.CloseRequested += (_, result) => closed = result;
        Text(sheet, L.Pages.General, Attr.Initials).Value = "K";

        await sheet.OkCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Equal("K", harness.Value(harness.Company.JanKowalski, Attr.Initials));
    }

    [Fact]
    public async Task DeletedObject_ShowsAnError()
    {
        var harness = await B2Harness.CreateAsync();
        var sheet = harness.Factory.CreatePropertySheet("CN=Nie Ma,OU=IT,OU=Pracownicy,OU=Firma,DC=contoso,DC=test");

        await sheet.InitializeAsync();

        Assert.True(sheet.IsDeleted);
        Assert.Equal("The object no longer exists.", sheet.LoadError);
        Assert.Empty(sheet.Pages);
    }

    [Fact]
    public async Task FailedWrite_KeepsTheValues_AndExplains()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        harness.Session.FailNext(FakeOperationKind.Modify, harness.Company.JanKowalski, DirectoryErrorKind.AccessDenied);
        var title = Text(sheet, L.Pages.Organization, Attr.Title);

        title.Value = "Dyrektor";
        var applied = await sheet.ApplyChangesAsync();

        Assert.False(applied);
        Assert.Contains(harness.Errors.Translate(DirectoryErrorKind.AccessDenied), sheet.ApplyError, StringComparison.Ordinal);
        Assert.Equal("Dyrektor", title.Value);
        Assert.True(sheet.HasChanges);
    }

    [Fact]
    public async Task AttributeEditor_OkOnAnUnchangedDate_StagesNothing()
    {
        var harness = await B2Harness.CreateAsync();
        var jan = harness.Company.JanKowalski;

        // Sub-second FILETIME (shown in whole seconds of local time) and 0 (shown as "never").
        var lockedAt = AdConvert.ToFileTime(new DateTimeOffset(2026, 10, 13, 7, 30, 15, TimeSpan.Zero)) + 1_234_567;
        harness.Session.SetValues(jan, Attr.LockoutTime, lockedAt.ToString(CultureInfo.InvariantCulture));
        harness.Session.SetValues(jan, Attr.AccountExpires, "0");
        var sheet = harness.Factory.CreatePropertySheet(jan);
        await sheet.InitializeAsync();
        var editor = Assert.IsType<AttributeEditorPageViewModel>(sheet.Pages[^1]);
        await editor.ActivateAsync();
        AttributeRowViewModel Row(string attribute) => Assert.Single(editor.Rows, r => r.Attribute == attribute);

        foreach (var attribute in new[] { Attr.LockoutTime, Attr.AccountExpires, Attr.PwdLastSet })
        {
            editor.EditCommand.Execute(Row(attribute));
            Assert.Equal(AttributeEditorKind.FileTime, editor.Editor!.Kind);
            editor.Editor.OkCommand.Execute(null);
            Assert.Null(editor.Editor);
            Assert.False(Row(attribute).IsDirty, attribute);
        }

        // "never" typed for a value that already means never keeps the value read from the DC (0, not long.MaxValue).
        editor.EditCommand.Execute(Row(Attr.AccountExpires));
        editor.Editor!.Value = "never";
        editor.Editor.OkCommand.Execute(null);
        Assert.False(Row(Attr.AccountExpires).IsDirty);
        Assert.False(sheet.HasChanges);

        // A staged value survives reopening the editor and confirming it unchanged.
        editor.EditCommand.Execute(Row(Attr.LockoutTime));
        editor.Editor!.Value = "0";
        editor.Editor.OkCommand.Execute(null);
        Assert.Equal(["0"], Row(Attr.LockoutTime).PendingValues);
        editor.EditCommand.Execute(Row(Attr.LockoutTime));
        editor.Editor!.OkCommand.Execute(null);
        Assert.Equal(["0"], Row(Attr.LockoutTime).PendingValues);

        Assert.True(await sheet.ApplyChangesAsync());
        Assert.Equal("0", harness.Value(jan, Attr.LockoutTime));
        Assert.Equal("0", harness.Value(jan, Attr.AccountExpires));
    }

    [Fact]
    public async Task AttributeEditor_RemovingSurroundingSpaces_IsAnEdit()
    {
        var harness = await B2Harness.CreateAsync();
        var jan = harness.Company.JanKowalski;
        harness.Session.SetValues(jan, Attr.Department, "Dział IT ");
        var sheet = harness.Factory.CreatePropertySheet(jan);
        await sheet.InitializeAsync();
        var editor = Assert.IsType<AttributeEditorPageViewModel>(sheet.Pages[^1]);
        await editor.ActivateAsync();
        var row = Assert.Single(editor.Rows, r => r.Attribute == Attr.Department);

        // Confirmed without touching the text: nothing is staged.
        editor.EditCommand.Execute(row);
        Assert.Equal(AttributeEditorKind.Text, editor.Editor!.Kind);
        Assert.Equal("Dział IT ", editor.Editor.Value);
        editor.Editor.OkCommand.Execute(null);
        Assert.False(row.IsDirty);

        // The operator removes the trailing space: that is a change, not the text that was shown.
        editor.EditCommand.Execute(row);
        editor.Editor!.Value = "Dział IT";
        editor.Editor.OkCommand.Execute(null);
        Assert.Equal(["Dział IT"], row.PendingValues);

        Assert.True(await sheet.ApplyChangesAsync());
        Assert.Equal("Dział IT", harness.Value(jan, Attr.Department));
    }

    [Fact]
    public async Task MemberOf_ReloadCancelsTheRunningNestedQuery()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        var page = Assert.IsType<MemberOfPageViewModel>(Assert.Single(sheet.Pages, p => p.Key == L.Pages.MemberOf));
        using var started = new SemaphoreSlim(0);
        using var firstGate = new ManualResetEventSlim();
        using var secondGate = new ManualResetEventSlim();
        var nestedQueries = 0;
        harness.Session.BeforeOperation = operation =>
        {
            // LDAP_MATCHING_RULE_IN_CHAIN: the "All (nested)" query.
            if (operation.Kind == FakeOperationKind.Search && operation.Search?.Filter.Contains("1.2.840.113556.1.4.1941", StringComparison.Ordinal) == true)
            {
                var gate = Interlocked.Increment(ref nestedQueries) == 1 ? firstGate : secondGate;
                started.Release();
                gate.Wait(TestContext.Current.CancellationToken);
            }
        };

        var first = page.LoadNestedCommand.ExecuteAsync(null);
        try
        {
            Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.True(page.IsNestedLoading);

            // The object is read again (Refresh, change made elsewhere): the running query belongs to the old read.
            await sheet.LoadAsync();
            Assert.False(page.IsNestedLoading);
            Assert.False(page.IsNestedLoaded);

            var second = page.LoadNestedCommand.ExecuteAsync(null);
            Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            firstGate.Set();
            await first;

            // The stale query neither shows its results nor ends the newer load.
            Assert.True(page.IsNestedLoading);
            Assert.False(page.IsNestedLoaded);
            Assert.Empty(page.NestedItems);

            secondGate.Set();
            await second;
        }
        finally
        {
            firstGate.Set();
            secondGate.Set();
        }

        Assert.False(page.IsNestedLoading);
        Assert.True(page.IsNestedLoaded);
        Assert.Equal(["DL_Wszyscy", "GG_IT"], page.NestedItems.Select(i => i.Name));
    }

    [Fact]
    public async Task ParentOuRenamedElsewhere_SheetFollowsTheObject()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        var jan = harness.Company.JanKowalski;
        var renamedOu = DistinguishedName.Parse(harness.Company.ItOu).WithLeafValue("Dział IT");
        var moved = renamedOu.Child("CN", "Jan Kowalski").ToString();

        var outcome = await harness.Coordinator.ApplyAsync(
            new ChangeRequest([harness.Company.ItOu], [new RenameOperation("Dział IT")], "test"), TestContext.Current.CancellationToken);

        Assert.Equal(ChangeOutcome.Executed, outcome);
        Assert.True(harness.Session.Exists(moved));
        await B2Harness.WaitUntilAsync(() => sheet.Shows(moved) && !sheet.IsLoading);
        Assert.False(sheet.Shows(jan));
        Assert.False(sheet.IsDeleted);
        Assert.Null(sheet.LoadError);
        Assert.EndsWith("/Dział IT", sheet.Path, StringComparison.Ordinal);

        // Apply writes to the object at its new DN.
        Text(sheet, L.Pages.Organization, Attr.Title).Value = "Kierownik";
        Assert.True(await sheet.ApplyChangesAsync());
        Assert.Equal("Kierownik", harness.Value(moved, Attr.Title));
    }

    [Fact]
    public async Task ParentOuDeletedWithItsSubtree_MarksTheSheetDeleted()
    {
        var harness = await B2Harness.CreateAsync();
        var ou = harness.Builder.AddOu("Tymczasowe", harness.Company.EmployeesOu);
        var user = harness.Builder.AddUser("Zofia", "Żółkiewska", ou);
        var sheet = harness.Factory.CreatePropertySheet(user);
        await sheet.InitializeAsync();
        Assert.True(sheet.Shows(user));

        harness.Changes.Publish([new DirectoryChange(DirectoryChangeKind.Deleted, ou, ObjectKind: ObjectKind.OrganizationalUnit)]);

        await B2Harness.WaitUntilAsync(() => sheet.IsDeleted);
        Assert.False(sheet.Shows(user));
    }
}
