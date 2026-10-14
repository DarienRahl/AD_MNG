using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Model;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.PropertySheets;
using AdMng.Presentation.Services;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Testing;
using ManualTimeProvider = AdMng.Testing.ManualTimeProvider;

namespace AdMng.Presentation.Tests.Dialogs;

public class FieldTests
{
    private static async Task<(FieldContext Context, B2Harness Harness)> ContextAsync()
    {
        var harness = await B2Harness.CreateAsync();
        return (new FieldContext(harness.Services, harness.Session), harness);
    }

    private static DirectoryObject Object(params (string Attribute, string[] Values)[] attributes) =>
        new("CN=Test,DC=contoso,DC=test", attributes.Select(a => new AttributeValues(a.Attribute, a.Values)));

    private static SetValuesOperation Set(IEnumerable<BatchOperation> operations, string attribute) =>
        Assert.Single(operations.OfType<SetValuesOperation>(), o => o.Attribute == attribute);

    [Fact]
    public async Task HomeFolder_SwitchesBetweenLocalPathAndNetworkDrive()
    {
        var (context, _) = await ContextAsync();
        var field = new HomeFolderFieldViewModel(context);
        field.Load(Object((Attr.HomeDirectory, [@"\\srv01\home$\jan"]), (Attr.HomeDrive, ["h:"])));

        Assert.True(field.UseDrive);
        Assert.Equal("H:", field.Drive);
        Assert.Equal(@"\\srv01\home$\jan", field.NetworkPath);
        Assert.False(field.IsDirty);

        field.NetworkPath = @"C:\home";
        Assert.Equal(@"Enter a network path such as \\server\share\user.", field.Error);

        field.UseLocalPath = true;
        field.LocalPath = @"D:\Profiles\jan";
        Assert.Null(field.Error);
        var operations = field.GetOperations().ToList();
        Assert.Empty(Set(operations, Attr.HomeDrive).Values);
        Assert.Equal([@"D:\Profiles\jan"], Set(operations, Attr.HomeDirectory).Values);
    }

    [Fact]
    public async Task Workstations_AreStoredAsOneCommaSeparatedValue()
    {
        var (context, _) = await ContextAsync();
        var field = new WorkstationsFieldViewModel(context);
        field.Load(Object((Attr.UserWorkstations, ["WAW-PC001,WAW-PC002"])));
        Assert.False(field.AllComputers);
        Assert.Equal(["WAW-PC001", "WAW-PC002"], field.Computers);

        field.NewComputer = "krk-lt001$";
        field.AddCommand.Execute(null);
        Assert.Equal(["WAW-PC001,WAW-PC002,krk-lt001"], Set(field.GetOperations(), Attr.UserWorkstations).Values);

        field.NewComputer = "zła nazwa";
        field.AddCommand.Execute(null);
        Assert.NotNull(field.NewComputerError);

        field.AllComputers = true;
        Assert.Empty(Set(field.GetOperations(), Attr.UserWorkstations).Values);
    }

    [Fact]
    public async Task Expiry_UsesTheLocalTimeZoneLikeAduc()
    {
        var harness = await B2Harness.CreateAsync();
        var warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
        var services = new Presentation.Dialogs.DialogServices(
            harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes,
            new ManualTimeProvider(localTimeZone: warsaw), harness.Dialogs, harness.Files, harness.Clipboard, harness.Notifications,
            harness.Coordinator, harness.Activities, harness.Lists, harness.Regional);
        var field = new ExpiryFieldViewModel(new FieldContext(services, harness.Session));

        // 2026-07-16 00:00 in Warsaw (CEST, UTC+2) is the end of 2026-07-15.
        var stored = AdConvert.ToFileTime(new DateTimeOffset(2026, 7, 15, 22, 0, 0, TimeSpan.Zero));
        field.Load(Object((Attr.AccountExpires, [stored.ToString(System.Globalization.CultureInfo.InvariantCulture)])));
        Assert.False(field.IsNever);
        Assert.Equal(new DateTime(2026, 7, 15), field.EndOfDate);

        field.EndOfDate = new DateTime(2026, 12, 31);
        var operation = Assert.IsType<SetAccountExpirationOperation>(Assert.Single(field.GetOperations()));
        Assert.Equal(new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero), operation.ExpiresAt!.Value.ToUniversalTime());

        field.IsNever = true;
        Assert.Null(Assert.IsType<SetAccountExpirationOperation>(Assert.Single(field.GetOperations())).ExpiresAt);
    }

    [Fact]
    public async Task Country_KeepsUnknownCodesSelectable_AndClearsAllThreeAttributes()
    {
        var (context, _) = await ContextAsync();
        var field = new CountryFieldViewModel(context);
        var standard = field.Options.Count;
        field.Load(Object((Attr.Country, ["XK"]), (Attr.CountryName, ["Kosovo"]), (Attr.CountryCode, ["0"])));
        field.Load(Object((Attr.Country, ["XK"]), (Attr.CountryName, ["Kosovo"]), (Attr.CountryCode, ["0"])));

        Assert.Equal("Kosovo", field.SelectedOption?.Text);
        Assert.False(field.IsDirty);

        // Reloading the sheet must not add the hand-edited entry again.
        Assert.Equal(standard + 1, field.Options.Count);
        Assert.Single(field.Options, o => o.Country?.Code == "XK");

        field.SelectedOption = field.Options[0];
        var operations = field.GetOperations().ToList();
        Assert.Empty(Set(operations, Attr.Country).Values);
        Assert.Empty(Set(operations, Attr.CountryName).Values);
        Assert.Equal(["0"], Set(operations, Attr.CountryCode).Values);
    }

    [Fact]
    public async Task ProxyAddresses_KeepExactlyOnePrimaryPerType()
    {
        var (context, _) = await ContextAsync();
        var field = new ProxyAddressesFieldViewModel(context);
        field.Load(Object((Attr.ProxyAddresses, ["SMTP:a@contoso.test", "smtp:b@contoso.test", "X500:/o=Org/cn=a"])));
        string? primary = null;
        field.PrimarySmtpChanged += (_, address) => primary = address;

        field.SelectedItem = field.Items[0];
        field.RemoveCommand.Execute(null);

        Assert.Equal("SMTP:b@contoso.test", field.Items[0].Value);
        Assert.Equal("b@contoso.test", primary);
        Assert.Null(field.Error);

        field.NewType = "SMTP";
        field.NewAddress = "bez-malpy";
        field.AddCommand.Execute(null);
        Assert.Equal("Enter a valid address (e-mail addresses must contain @).", field.NewAddressError);
        Assert.Equal(["SMTP:b@contoso.test", "X500:/o=Org/cn=a"], Set(field.GetOperations(), Attr.ProxyAddresses).Values);
    }

    [Fact]
    public void GroupScopeConversions_FollowAduc()
    {
        Assert.True(GroupTypeFieldViewModel.CanConvert(GroupScope.Global, GroupScope.Universal));
        Assert.True(GroupTypeFieldViewModel.CanConvert(GroupScope.DomainLocal, GroupScope.Universal));
        Assert.True(GroupTypeFieldViewModel.CanConvert(GroupScope.Universal, GroupScope.Global));
        Assert.True(GroupTypeFieldViewModel.CanConvert(GroupScope.Universal, GroupScope.DomainLocal));
        Assert.False(GroupTypeFieldViewModel.CanConvert(GroupScope.Global, GroupScope.DomainLocal));
        Assert.False(GroupTypeFieldViewModel.CanConvert(GroupScope.DomainLocal, GroupScope.Global));
        Assert.False(GroupTypeFieldViewModel.CanConvert(GroupScope.BuiltinLocal, GroupScope.Universal));
    }

    [Fact]
    public async Task UpnSuffixes_IncludeTheForestsAdditionalSuffixes()
    {
        var (context, harness) = await ContextAsync();
        harness.Session.Seed($"CN=Configuration,{harness.Builder.DomainDn}", new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "container" } });
        harness.Session.Seed($"CN=Partitions,CN=Configuration,{harness.Builder.DomainDn}", new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "container" },
            [UpnFieldViewModel.UpnSuffixesAttribute] = new[] { "firma.pl", "contoso.com" },
        });
        var field = new UpnFieldViewModel(context);
        field.Load(Object((Attr.UserPrincipalName, ["jan@firma.pl"])));

        await field.LoadDetailsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["contoso.test", "firma.pl", "contoso.com"], field.Suffixes);
        Assert.Equal("jan", field.Prefix);
        Assert.Equal("firma.pl", field.Suffix);
        field.Prefix = "jan kowalski";
        Assert.Equal("Enter a valid user logon name (no spaces or @) and choose a suffix.", field.Error);
    }

    [Fact]
    public async Task EncryptionTypes_KeepOtherBits()
    {
        var (context, _) = await ContextAsync();
        var field = new EncryptionTypesFieldViewModel(context);
        field.Load(Object((Attr.SupportedEncryptionTypes, ["4"])));

        field.Aes256 = true;

        Assert.Equal(["20"], Set(field.GetOperations(), Attr.SupportedEncryptionTypes).Values);
    }

    [Fact]
    public async Task DirectoryLookup_ReadsManyObjectsInBatchesOf100()
    {
        var harness = await B2Harness.CreateAsync();
        var dns = Enumerable.Range(1, 150).Select(i => harness.Builder.AddUser("Test", $"Użytkownik{i:000}", harness.Company.SalesOu)).ToList();
        harness.Session.ClearOperations();

        var found = await DirectoryLookup.ReadManyAsync(harness.Session, [.. dns, .. dns.Take(5), "CN=Nie Ma,DC=contoso,DC=test"], [Attr.Name], TestContext.Current.CancellationToken);

        Assert.Equal(150, found.Count);
        Assert.Equal(2, harness.Session.Operations.Count(o => o.Kind == FakeOperationKind.Search));
    }

    [Fact]
    public void CsvFiles_DetectsUtf8AndAnsiPolish()
    {
        var polish = System.Globalization.CultureInfo.GetCultureInfo("pl-PL");
        const string text = "Imię;Nazwisko\r\nŁukasz;Żółć\r\n";

        Assert.Equal(text, CsvFiles.Decode([.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes(text)], 1250));
        Assert.Equal(text, CsvFiles.Decode(Encoding.UTF8.GetBytes(text), 1250));
        Assert.Equal(text, CsvFiles.Decode(Encoding.GetEncoding(1250).GetBytes(text), 1250));
        Assert.Equal(text, CsvFiles.Decode(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray(), 1250));
        Assert.Equal(';', CsvFiles.ListSeparator(polish));
    }

    [Fact]
    public void RegionalSettings_AreIndependentOfTheUiLanguage()
    {
        // Polish region, English UI: Excel's ANSI files are Windows-1250 and its delimiter is ';'.
        var english = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        var regional = new SystemRegionalSettings(System.Globalization.CultureInfo.GetCultureInfo("pl-PL"), 1250);
        // The code page provider directly: Encoding.GetEncoding(1250) depends on whether CsvFiles registered it already.
        var bytes = CodePagesEncodingProvider.Instance.GetEncoding(1250)!.GetBytes("Łukasz Żółć;Księgowość");

        Assert.Equal("Łukasz Żółć;Księgowość", CsvFiles.Decode(bytes, regional.AnsiCodePage));
        Assert.NotEqual("Łukasz Żółć;Księgowość", CsvFiles.Decode(bytes, english.TextInfo.ANSICodePage));
        Assert.Equal(';', CsvFiles.ListSeparator(regional.Culture));
        if (!OperatingSystem.IsWindows())
        {
            // No system ANSI code page outside Windows (GetACP): the regional culture's one.
            Assert.Equal(1250, new SystemRegionalSettings(System.Globalization.CultureInfo.GetCultureInfo("pl-PL")).AnsiCodePage);
        }
    }

    [Fact]
    public async Task AttributeOptions_AreWritableNonBinaryAttributesOfTheKinds()
    {
        var harness = await B2Harness.CreateAsync();

        var users = AttributeOption.ForKinds(harness.Session.Schema, harness.Localizer, [ObjectKind.User]);
        var groups = AttributeOption.ForKinds(harness.Session.Schema, harness.Localizer, [ObjectKind.Group]);

        Assert.Contains(users, o => o.LdapName == Attr.Department && o.Text == "Department");
        Assert.Contains(users, o => o.LdapName == Attr.SamAccountName && o.Text == "Logon Name (pre-Windows 2000) [sAMAccountName]");
        Assert.DoesNotContain(users, o => o.LdapName is Attr.ObjectGuid or Attr.Cn or Attr.MemberOf or Attr.UnicodePwd or Attr.ThumbnailPhoto);
        Assert.Contains(groups, o => o.LdapName == Attr.Member);
        Assert.DoesNotContain(groups, o => o.LdapName == Attr.Department);
    }

    [Fact]
    public async Task Shell_ImportCsvOpensTheBatchEditorInCsvMode()
    {
        var harness = await TestHarness.ConnectedAsync();
        using var shell = harness.CreateShell();
        await shell.InitializeAsync();

        await shell.ImportCsvCommand.ExecuteAsync(null);

        Assert.Empty(Assert.Single(harness.Dialogs.BatchEditorRequests).TargetDns);
        Assert.Contains(shell.CommandDescriptors, d => d.Id == "tools.importCsv");
    }
}
