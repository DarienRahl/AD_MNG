using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Tests.Dialogs;

namespace AdMng.Presentation.Tests.Shell;

/// <summary>Phase 1 "Kopiowanie wartości": Copy as…, Ctrl+C on the object list and the details pane copy buttons.</summary>
public class CopyTests
{
    private static string Nl => Environment.NewLine;

    // ---- Copy as… ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task CopyAsMail_ManyObjects_JoinsForOutlookInListOrder_AndCountsTheSkipped()
    {
        var harness = await B2Harness.CreateAsync();
        harness.Session.SetValues(harness.Company.BartlomiejKozlowski, Attr.Mail);
        var shell = await OpenAsync(harness, harness.Company.SalesOu);

        // Selected in reverse list order: the clipboard follows the list.
        var rows = SelectReversed(shell);
        await shell.CopyAsCommand.ExecuteAsync(CopyFormat.Mail);

        var expected = rows
            .Where(r => r.Name != "Bartłomiej Kozłowski")
            .Select(r => harness.Value(r.Dn, Attr.Mail));
        Assert.Equal(string.Join("; ", expected), harness.Clipboard.Text);
        var toast = Assert.Single(harness.Notifications.Items);
        Assert.Equal(NotificationSeverity.Warning, toast.Severity);
        Assert.Equal("Copied e-mail addresses: 2. Skipped objects without an e-mail address: 1.", toast.Message);
    }

    [Fact]
    public async Task CopyAsMail_RemovesDuplicateAddresses()
    {
        var harness = await B2Harness.CreateAsync();
        harness.Session.SetValues(harness.Company.ZanetaSzymanska, Attr.Mail, "sprzedaz@contoso.test");
        harness.Session.SetValues(harness.Company.PawelLewandowski, Attr.Mail, "SPRZEDAZ@contoso.test");
        var shell = await OpenAsync(harness, harness.Company.SalesOu);
        SelectByName(shell, "Paweł Lewandowski", "Żaneta Szymańska");

        await shell.CopyAsCommand.ExecuteAsync(CopyFormat.Mail);

        Assert.Single(harness.Clipboard.Text!.Split("; "));
        Assert.Equal("Copied: E-mail address.", Assert.Single(harness.Notifications.Items).Message);
    }

    [Fact]
    public async Task CopyAs_EveryFormat_ForOneUser()
    {
        var harness = await B2Harness.CreateAsync();
        var shell = await OpenAsync(harness, harness.Company.ItOu);
        SelectByName(shell, "Jan Kowalski");
        var jan = (await harness.Session.GetAsync(harness.Company.JanKowalski, [Attr.ObjectGuid, Attr.ObjectSid], TestContext.Current.CancellationToken))!;

        var expected = new Dictionary<CopyFormat, string>
        {
            [CopyFormat.Mail] = "jan.kowalski@contoso.test",
            [CopyFormat.UserPrincipalName] = "jan.kowalski@contoso.test",
            [CopyFormat.SamAccountName] = "jan.kowalski",
            [CopyFormat.DomainLogin] = @"CONTOSO\jan.kowalski",
            [CopyFormat.Name] = "Jan Kowalski",
            [CopyFormat.DisplayName] = "Jan Kowalski",
            [CopyFormat.DistinguishedName] = harness.Company.JanKowalski,
            [CopyFormat.CanonicalPath] = "contoso.test/Firma/Pracownicy/IT/Jan Kowalski",
            [CopyFormat.ObjectGuid] = jan.ObjectGuid!.Value.ToString("D"),
            [CopyFormat.ObjectSid] = jan.ObjectSid!,
        };

        Assert.Equal(Enum.GetValues<CopyFormat>().Order(), expected.Keys.Order());
        foreach (var (format, value) in expected)
        {
            await shell.CopyAsCommand.ExecuteAsync(format);
            Assert.Equal(value, harness.Clipboard.Text);
            Assert.False(harness.Clipboard.IsSecret);
        }

        // One object: "Copied: <what>." without counts.
        Assert.Equal(
            expected.Keys.Select(f => $"Copied: {harness.Localizer[ObjectCopyFormats.LabelKey(f)]}."),
            harness.Notifications.Items.Select(n => n.Message));
        Assert.All(harness.Notifications.Items, n => Assert.Equal(NotificationSeverity.Info, n.Severity));
    }

    [Fact]
    public async Task CopyAs_ManyObjects_OneValuePerLine_AndReadsTheDirectoryLive()
    {
        var harness = await B2Harness.CreateAsync();
        var shell = await OpenAsync(harness, harness.Company.ItOu);
        var rows = SelectReversed(shell);

        // Changed by somebody else after the list was loaded: the copy reads the DC, not the rows.
        harness.Session.SetValues(harness.Company.JanKowalski, Attr.UserPrincipalName, "jkowalski@firma.pl");
        await shell.CopyAsCommand.ExecuteAsync(CopyFormat.UserPrincipalName);

        Assert.Equal(
            string.Join(Nl, rows.Select(r => r.Name == "Jan Kowalski" ? "jkowalski@firma.pl" : harness.Value(r.Dn, Attr.UserPrincipalName))),
            harness.Clipboard.Text);
        Assert.Equal("Copied 2 values: User principal name (UPN).", Assert.Single(harness.Notifications.Items).Message);
    }

    [Fact]
    public async Task CopyAs_NoObjectHasTheValue_WarnsAndLeavesTheClipboard()
    {
        var harness = await B2Harness.CreateAsync();
        var shell = await OpenAsync(harness, harness.Company.ComputersOu);
        SelectByName(shell, "WAW-PC001", "WAW-PC002");

        await shell.CopyAsCommand.ExecuteAsync(CopyFormat.Mail);
        await shell.CopyAsCommand.ExecuteAsync(CopyFormat.UserPrincipalName);

        Assert.Null(harness.Clipboard.Text);
        Assert.Equal(
            ["None of the objects has an e-mail address.", "None of the objects has a value: User principal name (UPN)."],
            harness.Notifications.Items.Select(n => n.Message));
        Assert.All(harness.Notifications.Items, n => Assert.Equal(NotificationSeverity.Warning, n.Severity));
    }

    [Fact]
    public async Task CopyAs_NeedsASelection_AndTheDomainLoginTheNetBiosName()
    {
        var harness = await B2Harness.CreateAsync();
        var shell = await OpenAsync(harness, harness.Company.ItOu);

        Assert.False(shell.CopyAsCommand.CanExecute(CopyFormat.Mail));
        Assert.False(shell.CopyRowsCommand.CanExecute(null));
        SelectByName(shell, "Jan Kowalski");
        Assert.True(shell.CopyAsCommand.CanExecute(CopyFormat.Mail));
        Assert.True(shell.CopyAsCommand.CanExecute(CopyFormat.DomainLogin));
        Assert.True(shell.CopyRowsCommand.CanExecute(null));

        harness.Session.Info = harness.Session.Info with { DomainNetBiosName = null };
        Assert.False(shell.CopyAsCommand.CanExecute(CopyFormat.DomainLogin));
        Assert.True(shell.CopyAsCommand.CanExecute(CopyFormat.SamAccountName));
    }

    [Fact]
    public async Task CopyAs_BusyClipboard_DoesNotConfirm()
    {
        var harness = await B2Harness.CreateAsync();
        var shell = await OpenAsync(harness, harness.Company.ItOu);
        SelectByName(shell, "Jan Kowalski");
        harness.Clipboard.IsBusy = true;

        await shell.CopyAsCommand.ExecuteAsync(CopyFormat.Mail);
        await shell.CopyRowsCommand.ExecuteAsync(null);

        Assert.Null(harness.Clipboard.Text);
        Assert.Empty(harness.Notifications.Items);
    }

    [Fact]
    public async Task CopyCommands_AreInThePalette_WithTheirShortcutsAndCategory()
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo("pl-PL"));
        var shell = await OpenAsync(harness, harness.Company.ItOu);
        var palette = shell.CommandPalette;

        var mail = palette.Find("copyAs.mail")!;
        Assert.Equal("Ctrl+Shift+M", mail.Shortcut);
        Assert.Equal(L.Copy.Category, mail.CategoryKey);
        Assert.Equal(CopyFormat.Mail, mail.Parameter);
        Assert.Same(shell.CopyAsCommand, mail.Command);
        Assert.Equal("Ctrl+C", palette.Find("object.copyRows")!.Shortcut);
        Assert.Equal(string.Empty, palette.Find("object.copyRowsWithHeaders")!.Shortcut);
        var dn = palette.Find("object.copyDn")!;
        Assert.Equal(L.Copy.Category, dn.CategoryKey);
        Assert.Equal("Ctrl+Shift+C", dn.Shortcut);
        foreach (var format in Enum.GetValues<CopyFormat>().Where(f => f != CopyFormat.DistinguishedName))
        {
            Assert.Single(shell.CommandDescriptors, d => Equals(d.Parameter, format) && ReferenceEquals(d.Command, shell.CopyAsCommand));
        }

        // Without a selection the palette does not offer them; with one, "kopiuj mail" finds the e-mail address.
        palette.OpenCommand.Execute(null);
        palette.FilterText = "kopiuj mail";
        Assert.DoesNotContain(palette.Results, r => r.Descriptor.Id == "copyAs.mail");
        SelectByName(shell, "Jan Kowalski");
        palette.Refresh();
        Assert.Equal("copyAs.mail", palette.Results.First().Descriptor.Id);
        Assert.Equal("Adres e-mail", palette.Results.First().Title);
        Assert.Equal("Kopiuj jako", palette.Results.First().Category);
    }

    // ---- Ctrl+C on the list ------------------------------------------------------------------------------------

    [Fact]
    public async Task CopyRows_VisibleColumnsAsTabSeparatedText_InListOrder()
    {
        var harness = await B2Harness.CreateAsync();
        var shell = await OpenAsync(harness, harness.Company.ItOu);
        var document = shell.SelectedDocument!;
        var rows = SelectReversed(shell);
        var columns = document.VisibleColumns.ToList();

        await shell.CopyRowsCommand.ExecuteAsync(null);

        var expected = rows.Select(r => string.Join('\t', columns.Select(c => r[c.Attribute])));
        Assert.Equal(string.Join(Nl, expected), harness.Clipboard.Text);
        Assert.Contains("Jan Kowalski\tUser", harness.Clipboard.Text, StringComparison.Ordinal);
        Assert.Equal("Copied rows: 2.", Assert.Single(harness.Notifications.Items).Message);

        await shell.CopyRowsWithHeadersCommand.ExecuteAsync(null);

        var lines = harness.Clipboard.Text!.Split(Nl);
        Assert.Equal(string.Join('\t', columns.Select(c => c.Header)), lines[0]);
        Assert.Equal("Name\tType\tDescription", lines[0]);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public async Task CopyRows_LeavesConfidentialColumnsOut_AndSaysWhich()
    {
        var harness = await B2Harness.CreateAsync();
        harness.Session.SetValues(harness.Company.Computer1, "msLAPS-Password", "Tajne!Hasło-2026");
        var shell = await OpenAsync(harness, harness.Company.ComputersOu);
        var document = shell.SelectedDocument!;
        await document.ApplyColumnsAsync([Attr.Name, "msLAPS-Password"]);
        SelectByName(shell, "WAW-PC001");
        Assert.Equal("Tajne!Hasło-2026", document.Selection[0]["msLAPS-Password"]);

        await shell.CopyRowsWithHeadersCommand.ExecuteAsync(null);

        Assert.Equal($"Name{Nl}WAW-PC001", harness.Clipboard.Text);
        Assert.False(harness.Clipboard.IsSecret);
        var toast = Assert.Single(harness.Notifications.Items);
        Assert.Equal(NotificationSeverity.Warning, toast.Severity);
        Assert.Equal("Copied rows: 1. Columns with confidential values were not copied: msLAPS-Password.", toast.Message);

        // Only confidential columns: nothing is copied.
        await document.ApplyColumnsAsync(["msLAPS-Password"]);
        SelectByName(shell, "WAW-PC001");
        await shell.CopyRowsCommand.ExecuteAsync(null);
        Assert.Equal($"Name{Nl}WAW-PC001", harness.Clipboard.Text);
        Assert.Equal("Columns with confidential values were not copied: msLAPS-Password.", harness.Notifications.Items[^1].Message);
    }

    [Theory]
    [InlineData("Jan Kowalski", "Jan Kowalski")]
    [InlineData("=HYPERLINK(\"http://x\")", "'=HYPERLINK(\"http://x\")")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("-cmd|' /C calc'!A0", "'-cmd|' /C calc'!A0")]
    [InlineData("+48 22 123 45 67", "+48 22 123 45 67")]
    [InlineData("-5", "-5")]
    [InlineData("wiersz 1\r\nwiersz 2\tkoniec", "wiersz 1  wiersz 2 koniec")]
    [InlineData("", "")]
    public void ListClipboardText_Cell_ProtectsFormulas_ButNotNumbers(string value, string expected) =>
        Assert.Equal(expected, ListClipboardText.Cell(value));

    // ---- Details pane ------------------------------------------------------------------------------------------

    [Fact]
    public async Task DetailsSummary_User_HasCopyableValues_DomainLogonMobileEmployeeIdAndManager()
    {
        var harness = await B2Harness.CreateAsync();
        var lukasz = harness.Company.LukaszWisniewski;
        harness.Session.SetValues(lukasz, Attr.Mobile, "+48 600 100 200");
        harness.Session.SetValues(lukasz, Attr.EmployeeId, "E-00042");
        var summary = await SummaryAsync(harness, lukasz, "CONTOSO");

        Assert.Equal(@"CONTOSO\lukasz.wisniewski", Field(summary, "Domain logon name").CopyText);
        Assert.Equal("+48 600 100 200", Field(summary, "Mobile").CopyText);
        Assert.Equal("E-00042", Field(summary, "Employee ID").CopyText);
        Assert.Equal("Jan Kowalski", Field(summary, "Manager").Value);
        Assert.Equal("Jan Kowalski", Field(summary, "Manager").CopyText);
        Assert.All(summary.Fields.Where(f => f.CanCopy), f => Assert.Equal(f.Value, f.CopyText));
        Assert.Contains(summary.Fields, f => f.Value == "lukasz.wisniewski@contoso.test" && f.CanCopy);
        Assert.False(Field(summary, "Status").CanCopy);
        Assert.False(Field(summary, "Password expires").CanCopy);

        Assert.Equal("Łukasz Wiśniewski", summary.NameField.CopyText);
        Assert.Equal("contoso.test/Firma/Pracownicy/IT/Łukasz Wiśniewski", summary.PathField.CopyText);
        Assert.Equal(lukasz, summary.DnField.CopyText);

        // Without a NetBIOS name there is no domain logon line.
        var plain = await SummaryAsync(harness, lukasz, null);
        Assert.DoesNotContain(plain.Fields, f => f.Label == "Domain logon name");

        var many = ObjectSummaryViewModel.ForMany([(ObjectKind.User, false), (ObjectKind.Computer, true)], harness.Localizer);
        Assert.False(many.NameField.CanCopy);
        Assert.False(many.DnField.CanCopy);
        Assert.DoesNotContain(many.Fields, f => f.CanCopy);
    }

    [Fact]
    public async Task DetailsPane_CopyButton_CopiesTheValue_AndConfirmsWithItsLabel()
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo("pl-PL"));
        var shell = await OpenAsync(harness, harness.Company.ItOu);
        SelectByName(shell, "Jan Kowalski");
        await B2Harness.WaitUntilAsync(() => shell.SelectedObject is { Name: "Jan Kowalski" });
        var summary = shell.SelectedObject!;
        var login = Assert.Single(summary.Fields, f => f.Label == "Nazwa logowania w domenie");

        Assert.True(shell.CopyDetailsValueCommand.CanExecute(login));
        shell.CopyDetailsValueCommand.Execute(login);
        Assert.Equal(@"CONTOSO\jan.kowalski", harness.Clipboard.Text);
        var toast = Assert.Single(harness.Notifications.Items);
        Assert.Equal(NotificationSeverity.Info, toast.Severity);
        Assert.Equal("Skopiowano: Nazwa logowania w domenie.", toast.Message);

        shell.CopyDetailsValueCommand.Execute(summary.DnField);
        Assert.Equal(harness.Company.JanKowalski, harness.Clipboard.Text);

        // Status lines and null parameters copy nothing.
        var state = Assert.Single(summary.Fields, f => f.Label == "Stan");
        Assert.False(shell.CopyDetailsValueCommand.CanExecute(state));
        Assert.False(shell.CopyDetailsValueCommand.CanExecute(null));
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------

    private static async Task<ShellViewModel> OpenAsync(B2Harness harness, string container)
    {
        var shell = harness.CreateShell();
        await shell.InitializeAsync();
        await shell.NavigateAsync(ListLocation.ForContainer(container));
        harness.Notifications.Clear();
        return shell;
    }

    private static void SelectByName(ShellViewModel shell, params string[] names)
    {
        var document = shell.SelectedDocument!;
        var rows = names.Select(n => document.Items.Single(r => r.Name == n)).ToList();
        document.SelectedItem = rows[0];
        document.SelectedItems = rows;
    }

    /// <summary>Selects every row, last one first (the copy must follow the list order); returns the rows in list order.</summary>
    private static List<ObjectRowViewModel> SelectReversed(ShellViewModel shell)
    {
        var document = shell.SelectedDocument!;
        var rows = document.Items.ToList();
        Assert.True(rows.Count > 1);
        document.SelectedItem = rows[^1];
        document.SelectedItems = Enumerable.Reverse(rows).ToList();
        return rows;
    }

    private static async Task<ObjectSummaryViewModel> SummaryAsync(B2Harness harness, string dn, string? netBios)
    {
        var obj = await harness.Session.GetAsync(dn, ObjectSummaryViewModel.Attributes, TestContext.Current.CancellationToken);
        var policy = await harness.Sessions.GetDomainPolicyAsync(TestContext.Current.CancellationToken);
        return ObjectSummaryViewModel.Create(obj!, harness.Localizer, harness.Sessions.Formatter, harness.Session.Schema, policy, harness.Time.GetUtcNow(), netBios);
    }

    private static SummaryField Field(ObjectSummaryViewModel summary, string label) => Assert.Single(summary.Fields, f => f.Label == label);
}
