using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PropertySheets;
using AdMng.Presentation.Services;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Dialogs;

/// <summary>Phase 1 "Kopiowanie wartości": copy buttons on property pages and copying from the list tabs.</summary>
public class CopyFieldTests
{
    private static string Nl => Environment.NewLine;

    [Fact]
    public async Task TextFields_ThatPeopleCopy_HaveACopyButton_ConfirmedWithTheirLabel()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        var mail = Text(sheet, L.Pages.General, Attr.Mail);
        var department = Text(sheet, L.Pages.Organization, Attr.Department);
        var phone = Text(sheet, L.Pages.General, Attr.TelephoneNumber);

        Assert.True(mail.CanCopy);
        Assert.True(department.CanCopy);
        Assert.True(Text(sheet, L.Pages.General, Attr.DisplayName).CanCopy);
        Assert.True(Text(sheet, L.Pages.Organization, Attr.Title).CanCopy);
        Assert.True(Text(sheet, L.Pages.Telephones, Attr.Mobile).CanCopy);
        Assert.True(phone.CanCopy);
        Assert.False(Text(sheet, L.Pages.General, Attr.GivenName).CanCopy);

        // Empty values cannot be copied.
        Assert.False(phone.CopyCommand.CanExecute(null));
        phone.Value = "+48 22 123 45 67";
        Assert.True(phone.CopyCommand.CanExecute(null));

        mail.CopyCommand.Execute(null);
        Assert.Equal("jan.kowalski@contoso.test", harness.Clipboard.Text);
        Assert.Equal($"Copied: {mail.Label}.", Assert.Single(harness.Notifications.Items).Message);
        Assert.Equal(NotificationSeverity.Info, harness.Notifications.Items[0].Severity);

        // The pending (edited) value is what the operator sees, so that is what is copied.
        department.Value = "Dział Wdrożeń";
        department.CopyCommand.Execute(null);
        Assert.Equal("Dział Wdrożeń", harness.Clipboard.Text);
    }

    [Fact]
    public async Task SamAccountName_AlsoCopiesTheDomainQualifiedLogon_ForUsersAndGroups()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski, CultureInfo.GetCultureInfo("pl-PL"));
        var sam = Text(sheet, L.Pages.Account, Attr.SamAccountName);

        Assert.True(sam.CanCopy);
        Assert.Equal(@"CONTOSO\", sam.QualifiedPrefix);
        Assert.Equal(@"CONTOSO\jan.kowalski", sam.QualifiedValue);
        sam.CopyQualifiedCommand.Execute(null);
        Assert.Equal(@"CONTOSO\jan.kowalski", harness.Clipboard.Text);
        Assert.Equal(@"Skopiowano: DOMENA\nazwa logowania.", Assert.Single(harness.Notifications.Items).Message);

        var group = harness.Factory.CreatePropertySheet(harness.Company.GroupIt);
        await group.InitializeAsync();
        var groupSam = Text(group, L.Pages.General, Attr.SamAccountName);
        Assert.Equal(@"CONTOSO\GG_IT", groupSam.QualifiedValue);

        // Without the NetBIOS name of the domain there is nothing to qualify with.
        harness.Session.Info = harness.Session.Info with { DomainNetBiosName = null };
        var plain = harness.Factory.CreatePropertySheet(harness.Company.JanKowalski);
        await plain.InitializeAsync();
        var plainSam = Text(plain, L.Pages.Account, Attr.SamAccountName);
        Assert.Null(plainSam.QualifiedPrefix);
        Assert.False(plainSam.CopyQualifiedCommand.CanExecute(null));
    }

    [Fact]
    public async Task Upn_CopiesPrefixAtSuffix_AsItWillBeWritten()
    {
        var (sheet, harness) = await OpenAsync(c => c.JanKowalski);
        var upn = Field<UpnFieldViewModel>(sheet, L.Pages.Account, Attr.UserPrincipalName);

        upn.CopyCommand.Execute(null);
        Assert.Equal("jan.kowalski@contoso.test", harness.Clipboard.Text);

        upn.Prefix = "jkowalski";
        upn.CopyCommand.Execute(null);
        Assert.Equal("jkowalski@contoso.test", harness.Clipboard.Text);

        upn.Prefix = " ";
        Assert.False(upn.CopyCommand.CanExecute(null));
    }

    [Fact]
    public async Task Manager_CopiesTheManagersName()
    {
        var (sheet, harness) = await OpenAsync(c => c.LukaszWisniewski);
        var manager = Field<DnFieldViewModel>(sheet, L.Pages.Organization, Attr.Manager);

        Assert.True(manager.CopyCommand.CanExecute(null));
        manager.CopyCommand.Execute(null);
        Assert.Equal("Jan Kowalski", harness.Clipboard.Text);

        manager.ClearCommand.Execute(null);
        Assert.False(manager.CopyCommand.CanExecute(null));
    }

    [Fact]
    public async Task EmailAddressesTab_CopiesOneAddressWithoutItsPrefix_EvenWhenReadOnly()
    {
        var harness = await B2Harness.CreateAsync();
        harness.Session.SetValues(harness.Company.JanKowalski, Attr.ProxyAddresses, "SMTP:jan.kowalski@contoso.test", "smtp:jk@contoso.test", "SIP:jan@contoso.test");
        harness.Session.SetPermissions(harness.Company.JanKowalski, new EffectivePermissions
        {
            IsKnown = true,
            WritableAttributes = new HashSet<string>([Attr.Description], StringComparer.OrdinalIgnoreCase),
        });
        var sheet = harness.Factory.CreatePropertySheet(harness.Company.JanKowalski);
        await sheet.InitializeAsync();
        var proxies = Assert.Single(Page(sheet, L.Pages.EmailAddresses).Fields.OfType<ProxyAddressesFieldViewModel>());
        Assert.True(proxies.IsReadOnly);

        Assert.False(proxies.CopyAddressCommand.CanExecute(null));
        proxies.SelectedItem = Assert.Single(proxies.Items, i => i.Address == "jk@contoso.test");
        Assert.True(proxies.CopyAddressCommand.CanExecute(null));
        proxies.CopyAddressCommand.Execute(null);
        Assert.Equal("jk@contoso.test", harness.Clipboard.Text);

        proxies.SelectedItem = Assert.Single(proxies.Items, i => i.Type == "SIP");
        proxies.CopyAddressCommand.Execute(null);
        Assert.Equal("jan@contoso.test", harness.Clipboard.Text);
        Assert.All(harness.Notifications.Items, n => Assert.Equal($"Copied: {proxies.Label}.", n.Message));
    }

    [Fact]
    public async Task MembersTab_CopiesTheMembersAddressesForOutlook_SkippingThoseWithoutMail()
    {
        var harness = await B2Harness.CreateAsync();
        harness.Builder.AddMembers(harness.Company.GroupAccounting, harness.Company.Computer1);
        var sheet = harness.Factory.CreatePropertySheet(harness.Company.GroupAccounting);
        await sheet.InitializeAsync();
        var members = Assert.IsType<MembersPageViewModel>(sheet.Pages[1]);
        await B2Harness.WaitUntilAsync(() => members.Items.Count == 4 && !members.IsLoading);

        Assert.True(members.CanCopyAllMail);
        members.CopyAllMailCommand.Execute(null);

        Assert.Equal(
            "grazyna.dabrowska@contoso.test; malgorzata.wojcik@contoso.test; zbigniew.kazmierczak@contoso.test",
            harness.Clipboard.Text);
        var toast = Assert.Single(harness.Notifications.Items);
        Assert.Equal(NotificationSeverity.Warning, toast.Severity);
        Assert.Equal("Copied e-mail addresses: 3. Skipped objects without an e-mail address: 1.", toast.Message);

        // A member staged for removal is not a recipient any more.
        members.SelectedItem = Assert.Single(members.Items, i => i.Name == "Zbigniew Kaźmierczak");
        members.RemoveCommand.Execute(null);
        members.CopyAllMailCommand.Execute(null);
        Assert.Equal("grazyna.dabrowska@contoso.test; malgorzata.wojcik@contoso.test", harness.Clipboard.Text);

        // The Member Of page has no "all members".
        var memberOf = Assert.IsType<MemberOfPageViewModel>(sheet.Pages[2]);
        Assert.False(memberOf.CanCopyAllMail);
        Assert.False(memberOf.CopyAllMailCommand.CanExecute(null));
    }

    [Fact]
    public async Task MembershipLists_CopyNamesDnsAndMailOfTheSelection()
    {
        var harness = await B2Harness.CreateAsync();
        var sheet = harness.Factory.CreatePropertySheet(harness.Company.GroupAccounting);
        await sheet.InitializeAsync();
        var members = Assert.IsType<MembersPageViewModel>(sheet.Pages[1]);
        await B2Harness.WaitUntilAsync(() => members.Items.Count == 3 && !members.IsLoading);

        Assert.False(members.CopyNamesCommand.CanExecute(null));
        var selected = members.Items.Where(i => i.Name != "Małgorzata Wójcik").ToList();
        members.SelectedItem = selected[0];
        members.SelectedItems = selected;
        members.NotifySelectionChanged();

        members.CopyNamesCommand.Execute(null);
        Assert.Equal($"Grażyna Dąbrowska{Nl}Zbigniew Kaźmierczak", harness.Clipboard.Text);
        members.CopyDnsCommand.Execute(null);
        Assert.Equal($"{harness.Company.GrazynaDabrowska}{Nl}{harness.Company.ZbigniewKazmierczak}", harness.Clipboard.Text, ignoreCase: true);
        members.CopyMailCommand.Execute(null);
        Assert.Equal("grazyna.dabrowska@contoso.test; zbigniew.kazmierczak@contoso.test", harness.Clipboard.Text);

        // Member Of of a user: the group's name and DN.
        var user = harness.Factory.CreatePropertySheet(harness.Company.JanKowalski);
        await user.InitializeAsync();
        var memberOf = Assert.IsType<MemberOfPageViewModel>(Assert.Single(user.Pages, p => p.Key == L.Pages.MemberOf));
        await B2Harness.WaitUntilAsync(() => memberOf.Items.Any(i => i.Name == "GG_IT") && !memberOf.IsLoading);
        memberOf.SelectedItem = Assert.Single(memberOf.Items, i => i.Name == "GG_IT");
        memberOf.CopyNamesCommand.Execute(null);
        Assert.Equal("GG_IT", harness.Clipboard.Text);
        memberOf.CopyDnsCommand.Execute(null);
        Assert.Equal(harness.Company.GroupIt, harness.Clipboard.Text, ignoreCase: true);
    }

    [Fact]
    public async Task SessionLog_CopiesTheSelectedEntryAsLabelledLines()
    {
        var harness = await B2Harness.CreateAsync();
        using var log = harness.Factory.CreateSessionLog();
        await harness.Coordinator.ApplyAsync(
            new ChangeRequest([harness.Company.JanKowalski], [new SetAttributeOperation(Attr.Title, "Analityk")], "test"),
            TestContext.Current.CancellationToken);

        Assert.False(log.CopyEntryCommand.CanExecute(null));
        log.SelectedEntry = Assert.Single(log.Entries);
        log.CopyEntryCommand.Execute(null);

        var lines = harness.Clipboard.Text!.Split(Nl);
        Assert.Equal($"Time: {log.SelectedEntry.Timestamp}", lines[0]);
        Assert.Contains($"Object: Jan Kowalski ({harness.Company.JanKowalski})", lines);
        Assert.Contains("Domain controller: dc01.contoso.test", lines);
        Assert.Contains("Action: Modify", lines);
        Assert.Contains($"Attribute: {log.SelectedEntry.Attribute}", lines);
        Assert.Contains("Before: Administrator systemów", lines);
        Assert.Contains("After: Analityk", lines);
        Assert.Contains("Status: Succeeded", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("Error:", StringComparison.Ordinal));
        Assert.Equal("Copied: Session log entry.", harness.Notifications.Items[^1].Message);

        // The DN stays available (Ctrl+Shift+C).
        log.CopyDnCommand.Execute(null);
        Assert.Equal(harness.Company.JanKowalski, harness.Clipboard.Text);
    }

    [Fact]
    public async Task CopyCommands_OfOtherWindows_AreNotConfirmed_WhenTheClipboardIsBusy()
    {
        var harness = await B2Harness.CreateAsync();

        // Attribute Editor.
        var sheet = harness.Factory.CreatePropertySheet(harness.Company.JanKowalski);
        await sheet.InitializeAsync();
        var editor = Assert.IsType<AttributeEditorPageViewModel>(sheet.Pages[^1]);
        sheet.SelectedPage = editor;
        await B2Harness.WaitUntilAsync(() => editor.Rows.Count > 0);

        // Session log (Ctrl+Shift+C) and PowerShell history.
        using var log = harness.Factory.CreateSessionLog();
        using var history = harness.Factory.CreatePowerShellHistory();
        await harness.Coordinator.ApplyAsync(
            new ChangeRequest([harness.Company.JanKowalski], [new SetAttributeOperation(Attr.Title, "Analityk")], "test"),
            TestContext.Current.CancellationToken);
        log.SelectedEntry = Assert.Single(log.Entries);
        history.SelectedItem = Assert.Single(history.Items);

        // Recycle Bin.
        await harness.Session.DeleteAsync(harness.Company.SalesOu, subtree: true, TestContext.Current.CancellationToken);
        using var bin = harness.Factory.CreateRecycleBin();
        await bin.InitializeAsync();
        bin.SelectedItem = bin.Items.Single(r => r.Name == "Sprzedaż");

        // Selected last: the change above reloads the editor's rows.
        await B2Harness.WaitUntilAsync(() => editor.Rows.Any(r => r.Attribute == Attr.Title && r.ValueText == "Analityk"));
        editor.SelectedRow = Assert.Single(editor.Rows, r => r.Attribute == Attr.Department);

        void CopyAll()
        {
            editor.CopyValueCommand.Execute(null);
            log.CopyDnCommand.Execute(null);
            history.CopyCommand.Execute(null);
            bin.CopyDnCommand.Execute(null);
        }

        harness.Notifications.Clear();
        CopyAll();
        Assert.Equal(4, harness.Notifications.Items.Count);
        Assert.Equal("Copied: Distinguished name (DN).", harness.Notifications.Items[1].Message);

        harness.Notifications.Clear();
        harness.Clipboard.IsBusy = true;
        CopyAll();
        Assert.Empty(harness.Notifications.Items);
    }

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
}
