using System.Globalization;
using System.Text;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Security;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.SecuritySettings;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Tests.Lists;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.GroupPolicy;

public class SecuritySettingsEditorTests
{
    private const string AuditHeader = "Machine Name,Policy Target,Subcategory,Subcategory GUID,Inclusion Setting,Exclusion Setting,Setting Value";
    private const string GptTmpl = "Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf";
    private const string AuditCsv = "Machine/Microsoft/Windows NT/Audit/audit.csv";
    private const string GroupsXml = "Machine/Preferences/Groups/Groups.xml";
    private const string Lmc = @"MACHINE\System\CurrentControlSet\Control\Lsa\LmCompatibilityLevel";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>"Lokacja Warszawa – Drukarki" with a template, an audit policy and two GPP items (the second disabled).</summary>
    private static async Task<(GroupPolicyHarness Harness, SecuritySettingsEditorViewModel Editor, Guid Gpo)> OpenSampleAsync(CultureInfo? culture = null)
    {
        var harness = await GroupPolicyHarness.CreateAsync(culture);
        var gpo = harness.Sample.SitePrinters;
        var it = harness.B2.Builder.SidOf(harness.Company.GroupIt);
        harness.Sysvol.Put(gpo, GptTmpl, FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template(
            "[System Access]", "MinimumPasswordLength = 12", "NewAdministratorName = \"Admin-Wrocław\"", "LSAAnonymousNameLookup = 0", "NieznanyKlucz = 7",
            "[Event Audit]", "AuditLogonEvents = 3",
            "[Privilege Rights]", "SeDebugPrivilege = *S-1-5-32-544", @"SeRemoteInteractiveLogonRight = *S-1-5-32-555,KONTOSO\Serwisanci", "SeFutureRight = *S-1-5-32-544",
            "[Group Membership]", $"*S-1-5-32-544__Members = *{it}",
            "[Registry Values]", Lmc + "=4,4", @"MACHINE\Software\Contoso\Łódź=1,""zażółć""",
            "[Service General Setting]", "\"Spooler\",4,\"\"", "\"Netlogon\",2,\"D:AR(A;;CCLC;;;SY)\"",
            "[File Security]", "\"%SystemRoot%\\Temp\",0,\"D:PAR(A;OICI;FA;;;BA)\"")));
        harness.Sysvol.PutText(gpo, AuditCsv, AuditHeader + "\r\n,System,Audit Logon,{0cce9215-69ae-11d9-bed3-505054503030},Success and Failure,,3\r\n");
        harness.Sysvol.PutText(gpo, GroupsXml, FakeDirectoryBuilder.Groups(
            FakeDirectoryBuilder.LocalGroup("Administrators (built-in)", "S-1-5-32-544", [(@"CONTOSO\GG_IT", it, "ADD"), ("Łukasz", null, "REMOVE")], targeted: true),
            FakeDirectoryBuilder.LocalGroup("Serwisanci", null, [], action: "C", disabled: true)));
        var editor = await harness.SecurityAsync(gpo);
        return (harness, editor, gpo);
    }

    private static UserRightRowViewModel Right(SecuritySettingsEditorViewModel editor, string constant) => editor.UserRights.Single(r => r.Constant == constant);

    private static SecurityOptionRowViewModel Option(SecuritySettingsEditorViewModel editor, string id) =>
        editor.OptionGroups.SelectMany(g => g.Rows).Single(r => r.Definition.Id == id);

    private static string Text(GroupPolicyHarness harness, Guid gpo, string path) =>
        GpoText.DecodeUnicodeText(harness.Sysvol.Get(gpo, path) ?? throw new InvalidOperationException("No " + path));

    private static async Task SaveAsync(SecuritySettingsEditorViewModel editor)
    {
        Assert.True(editor.SaveCommand.CanExecute(null));
        await editor.SaveCommand.ExecuteAsync(null);
        await editor.LoadAsync();
    }

    [Fact]
    public async Task OpensASampleGpo_WithEveryPagePopulated_InPolish()
    {
        var (_, editor, _) = await OpenSampleAsync(CultureInfo.GetCultureInfo("pl-PL"));

        Assert.Null(editor.Error);
        Assert.Null(editor.FileNote);
        Assert.Equal("Ustawienia zabezpieczeń – Lokacja Warszawa – Drukarki", editor.Title);
        Assert.True(editor.CanEditTemplate && editor.CanEditAudit && editor.CanEditGroups);
        Assert.Equal(0, editor.ChangedCount);

        // Grupy lokalne
        var restricted = Assert.Single(editor.RestrictedGroups);
        Assert.Equal("Administratorzy", restricted.Group!.DisplayName);
        Assert.True(restricted.MembersDefined);
        Assert.Equal(@"CONTOSO\GG_IT", Assert.Single(restricted.Members).DisplayName);
        Assert.Equal(2, editor.GppGroups.Count);
        var gpp = editor.GppGroups[0];
        Assert.Equal("Administratorzy", gpp.GroupName);
        Assert.Equal(GppItemAction.Update, gpp.SelectedAction!.Action);
        Assert.True(gpp.HasTargeting);
        Assert.Equal(["+ CONTOSO\\GG_IT", "− Łukasz"], gpp.MembersText.Split(", "));
        Assert.True(gpp.Members[1].Principal.IsTypedName);
        Assert.True(editor.GppGroups[1].IsDisabled);
        Assert.Equal("Utwórz", editor.GppGroups[1].SelectedAction!.Label);

        // Prawa użytkownika: the catalog in GPMC's order, then the file's own rights.
        Assert.Equal(SecuritySettingsCatalog.UserRights.Count + 1, editor.UserRights.Count);
        Assert.Equal("Debugowanie programów", Right(editor, "SeDebugPrivilege").Name);
        Assert.Equal("Administratorzy", Right(editor, "SeDebugPrivilege").HoldersText);
        var remote = Right(editor, "SeRemoteInteractiveLogonRight");
        Assert.Equal("Zezwalaj na logowanie za pomocą usług pulpitu zdalnego", remote.Name);
        Assert.Equal(["Użytkownicy pulpitu zdalnego", @"KONTOSO\Serwisanci"], remote.Holders.Select(h => h.DisplayName));
        Assert.True(remote.Holders[1].IsTypedName);
        Assert.Equal("Nie zdefiniowano", Right(editor, "SeDenyNetworkLogonRight").HoldersText);
        Assert.Equal("Odmowa dostępu do tego komputera z sieci", Right(editor, "SeDenyNetworkLogonRight").Name);
        var unknown = editor.UserRights[^1];
        Assert.Equal("SeFutureRight", unknown.Name);
        Assert.False(unknown.IsKnown);

        // Zasady kont i opcje zabezpieczeń
        Assert.Equal("Zasady haseł", editor.OptionGroups[0].Name);
        Assert.NotNull(editor.OptionGroups[0].Note);
        var length = Option(editor, "MinimumPasswordLength");
        Assert.Equal("Minimalna długość hasła", length.Name);
        Assert.Equal("12", length.ValueText);
        Assert.Equal("od 0 do 128", length.RangeText);
        Assert.Equal("Nie zdefiniowano", Option(editor, "LockoutBadCount").ValueText);
        Assert.Equal("Próg blokady konta", Option(editor, "LockoutBadCount").Name);
        Assert.Equal("Admin-Wrocław", Option(editor, "NewAdministratorName").Text);
        Assert.Equal("Wyłączone", Option(editor, "LSAAnonymousNameLookup").ValueText);
        Assert.Equal("Wyślij tylko odpowiedź NTLMv2. Odmów LM", Option(editor, "LmCompatibilityLevel").ValueText);
        Assert.Equal([("System Access", "NieznanyKlucz", "7"), ("Registry Values", @"MACHINE\Software\Contoso\Łódź", "1,\"zażółć\"")],
            editor.RawEntries.Select(e => (e.Section, e.Key, e.Value)));

        // Inspekcja: both configured, subcategories not forced – the note.
        var logon = editor.LegacyAudit.Single(r => r.Id == "AuditLogonEvents");
        Assert.Equal("Przeprowadź inspekcję zdarzeń logowania", logon.Name);
        Assert.Equal("Powodzenie i niepowodzenie", logon.SelectedState!.Label);
        var category = editor.AdvancedAuditCategories.Single(c => c.Category == "LogonLogoff");
        var advanced = category.Rows.Single(r => r.Id == "Logon");
        Assert.Equal("Inspekcja logowania", advanced.Name);
        Assert.Equal(3, advanced.SelectedState!.Value);
        Assert.Equal("Nie skonfigurowano", category.Rows.Single(r => r.Id == "Logoff").SelectedState!.Label);
        Assert.NotNull(editor.AuditNote);

        // Usługi
        Assert.Equal(["Spooler", "Netlogon"], editor.Services.Select(s => s.Name));
        Assert.Equal("Wyłączony", editor.Services[0].ModeText);
        Assert.Equal("brak", editor.Services[0].SecurityText);
        Assert.Equal("Automatyczny", editor.Services[1].ModeText);
        Assert.Equal("D:AR(A;;CCLC;;;SY)", editor.Services[1].SecurityText);
    }

    [Fact]
    public async Task ARightsHolders_AreEdited_AndTheSaveWritesGptTmplThroughThePreview()
    {
        var harness = await GroupPolicyHarness.CreateAsync();
        var gpo = harness.Sample.SitePrinters;
        var editor = await harness.SecurityAsync(gpo);
        Assert.False(editor.SaveCommand.CanExecute(null));

        editor.SelectedRight = Right(editor, "SeDebugPrivilege");
        editor.NewRightHolderText = "Administrators"; // from the list: the SID is written
        editor.AddRightHolderByNameCommand.Execute(null);
        editor.NewRightHolderText = @"KONTOSO\Serwisanci"; // typed: written as typed
        editor.AddRightHolderByNameCommand.Execute(null);
        harness.B2.Dialogs.PickedObjects.Enqueue([harness.Company.GroupIt]);
        await editor.AddRightHolderCommand.ExecuteAsync(null);

        var row = Right(editor, "SeDebugPrivilege");
        Assert.True(row.IsDefined);
        Assert.True(row.IsChanged);
        Assert.Equal(["Administrators", @"KONTOSO\Serwisanci", @"CONTOSO\GG_IT"], row.Holders.Select(h => h.DisplayName));
        Assert.Equal(string.Empty, editor.NewRightHolderText);
        Assert.Equal(1, editor.ChangedCount);
        Assert.Equal("Changed settings: 1 (not saved)", editor.SelectionText);

        editor.RemoveRightHolderCommand.Execute(row.Holders[1]);
        Right(editor, "SeDenyNetworkLogonRight").IsDefined = true; // defined with nobody
        Assert.Equal("Nobody (defined empty)", Right(editor, "SeDenyNetworkLogonRight").HoldersText);
        Assert.Equal(2, editor.ChangedCount);

        await SaveAsync(editor);

        var preview = Assert.Single(harness.Windows.Previews);
        Assert.Contains("Get-GPO", preview.PowerShell, StringComparison.Ordinal);
        var bytes = harness.Sysvol.Get(gpo, GptTmpl)!;
        Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes[..2]);
        var text = Text(harness, gpo, GptTmpl);
        Assert.Contains($"SeDebugPrivilege = *S-1-5-32-544,*{harness.B2.Builder.SidOf(harness.Company.GroupIt)}\r\n", text, StringComparison.Ordinal);
        Assert.Contains("SeDenyNetworkLogonRight = \r\n", text, StringComparison.Ordinal);
        Assert.Contains("{827D319E-6EAC-11D2-A4EA-00C04F79F83A}", harness.Value(harness.GpoDn(gpo), "gPCMachineExtensionNames"), StringComparison.Ordinal);
        Assert.False(editor.HasChanges);
        Assert.Equal(0, editor.ChangedCount);
        Assert.False(Right(editor, "SeDebugPrivilege").IsChanged);
        Assert.Null(harness.Sysvol.Get(gpo, AuditCsv)); // untouched files are not written
        Assert.Null(harness.Sysvol.Get(gpo, GroupsXml));
    }

    [Fact]
    public async Task GppItems_AreUpdatedAndAdded_AndKeepTheirTargeting()
    {
        var (harness, editor, gpo) = await OpenSampleAsync();
        var first = editor.GppGroups[0];
        editor.SelectedGppGroup = first;

        first.DeleteAllUsers = true;
        editor.NewGppMemberText = "Remote Desktop Users";
        editor.AddGppMemberByNameCommand.Execute(null);
        editor.NewGppMemberText = @"KONTOSO\Żaneta";
        editor.NewGppMemberRemoves = true;
        editor.AddGppMemberByNameCommand.Execute(null);
        editor.RemoveGppMemberCommand.Execute(first.Members.Single(m => m.Principal.DisplayName == "Łukasz"));

        editor.NewGppGroupText = "Remote Desktop Users (built-in)";
        editor.AddGppGroupCommand.Execute(null);
        Assert.Equal(3, editor.GppGroups.Count);
        Assert.Same(editor.GppGroups[2], editor.SelectedGppGroup);
        editor.SelectedGppGroup!.SelectedAction = editor.SelectedGppGroup.Actions.Single(a => a.Action == GppItemAction.Replace);
        Assert.True(editor.GppGroups[0].IsChanged);
        Assert.False(editor.GppGroups[1].IsChanged);
        Assert.Equal(2, editor.ChangedCount);

        await SaveAsync(editor);

        var xml = Encoding.UTF8.GetString(harness.Sysvol.Get(gpo, GroupsXml)!);
        Assert.Contains("<Filters><FilterComputer", xml, StringComparison.Ordinal);
        var items = GppGroupsParser.Parse(harness.Sysvol.Get(gpo, GroupsXml)!);
        Assert.Equal(3, items.Count);
        Assert.True(items[0].HasItemLevelTargeting);
        Assert.True(items[0].DeleteAllUsers);
        Assert.Equal([("S-1-5-32-555", GppMemberAction.Add), (null, GppMemberAction.Remove)], items[0].Members.Skip(1).Select(m => (m.Sid, m.Action)));
        Assert.Equal(@"KONTOSO\Żaneta", items[0].Members[2].Name);
        Assert.Contains("changed=\"2026-10-13 12:00:00\"", xml, StringComparison.Ordinal);
        Assert.True(items[1].Disabled);
        Assert.Equal(GppItemAction.Create, items[1].Action);
        Assert.Equal(("Remote Desktop Users (built-in)", "S-1-5-32-555", GppItemAction.Replace), (items[2].GroupName, items[2].GroupSid, items[2].Action));
        Assert.Contains("{17D89FEC-5C44-4972-B12D-241CAEF74509}", harness.Value(harness.GpoDn(gpo), "gPCMachineExtensionNames"), StringComparison.Ordinal);
        Assert.False(editor.HasChanges);
    }

    [Fact]
    public async Task RestrictedGroups_AreAdded_WithMembersAndMemberOf()
    {
        var harness = await GroupPolicyHarness.CreateAsync();
        var gpo = harness.Sample.SitePrinters;
        var editor = await harness.SecurityAsync(gpo);

        editor.NewRestrictedGroupText = "Administratorzy"; // a Polish name of a built-in group: its SID
        editor.AddRestrictedGroupCommand.Execute(null);
        var row = Assert.Single(editor.RestrictedGroups);
        Assert.Same(row, editor.SelectedRestrictedGroup);
        Assert.Equal("Administrators", row.Group!.DisplayName);
        Assert.False(row.MembersDefined);
        Assert.Equal("Not defined", row.MembersText);

        row.MembersDefined = true;
        Assert.Equal("No members (defined empty)", row.MembersText);
        harness.B2.Dialogs.PickedObjects.Enqueue([harness.Company.GroupIt]);
        await editor.AddRestrictedMemberCommand.ExecuteAsync(null);
        editor.NewRestrictedMemberOfText = "Remote Desktop Users";
        editor.AddRestrictedMemberOfByNameCommand.Execute(null);
        editor.NewRestrictedGroupText = "Serwisanci-Łódź"; // a group named only
        editor.AddRestrictedGroupCommand.Execute(null);
        Assert.True(editor.RestrictedGroups[1].Group!.IsTypedName);

        await SaveAsync(editor);

        var it = harness.B2.Builder.SidOf(harness.Company.GroupIt);
        var text = Text(harness, gpo, GptTmpl);
        Assert.Contains($"*S-1-5-32-544__Members = *{it}\r\n", text, StringComparison.Ordinal);
        Assert.Contains("*S-1-5-32-544__Memberof = *S-1-5-32-555\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Serwisanci-Łódź__Memberof = \r\n", text, StringComparison.Ordinal);
        Assert.Equal([@"CONTOSO\GG_IT"], editor.RestrictedGroups[0].Members.Select(m => m.DisplayName));
    }

    [Fact]
    public async Task OptionValues_AreValidated_AndWrittenAsSeceditWritesThem()
    {
        var (harness, editor, gpo) = await OpenSampleAsync();
        var lockout = Option(editor, "LockoutBadCount");
        editor.SelectedOptionGroup = editor.OptionGroups.Single(g => g.Group == SecurityOptionGroup.AccountLockout);
        Assert.Contains(lockout, editor.Options);

        lockout.IsDefined = true;
        Assert.Equal("0", lockout.Text);
        lockout.Text = "1000";
        Assert.Equal("Enter a number from 0 to 999.", lockout.Problem);
        Assert.True(editor.HasProblems);
        Assert.False(editor.SaveCommand.CanExecute(null));
        lockout.Text = "abc";
        Assert.Equal("Not a whole number.", lockout.Problem);
        lockout.Text = "5";
        Assert.Null(lockout.Problem);
        Assert.True(editor.SaveCommand.CanExecute(null));

        var admin = Option(editor, "NewAdministratorName");
        admin.Text = "Admin \"x\"";
        Assert.NotNull(admin.Problem);
        admin.Text = "Zbigniew-Admin";
        Option(editor, "LmCompatibilityLevel").SelectedChoice = Option(editor, "LmCompatibilityLevel").Choices.Single(c => c.Value == 5);
        var legal = Option(editor, "LegalNoticeText");
        legal.IsDefined = true;
        legal.Text = "Dostęp tylko dla pracowników," + Environment.NewLine + "wszystko jest rejestrowane";
        Option(editor, "LSAAnonymousNameLookup").IsDefined = false;

        await SaveAsync(editor);

        var text = Text(harness, gpo, GptTmpl);
        Assert.Contains("LockoutBadCount = 5\r\n", text, StringComparison.Ordinal);
        Assert.Contains("NewAdministratorName = \"Zbigniew-Admin\"\r\n", text, StringComparison.Ordinal);
        Assert.Contains(Lmc + " = 4,5\r\n", text, StringComparison.Ordinal);
        Assert.Contains(@"MACHINE\Software\Microsoft\Windows\CurrentVersion\Policies\System\LegalNoticeText = 7,""Dostęp tylko dla pracowników,"",wszystko jest rejestrowane", text, StringComparison.Ordinal);
        Assert.DoesNotContain("LSAAnonymousNameLookup", text, StringComparison.Ordinal);
        Assert.Equal("Dostęp tylko dla pracowników, / wszystko jest rejestrowane", Option(editor, "LegalNoticeText").ValueText);
    }

    [Fact]
    public async Task RawEntriesAndUnknownSections_AreKept_AndRawValuesCanBeReplacedOrRemoved()
    {
        var (harness, editor, gpo) = await OpenSampleAsync();
        editor.SelectedOptionGroup = editor.OptionGroups[^1];
        Assert.True(editor.IsRawGroupSelected);

        editor.SelectedRawEntry = editor.RawEntries.Single(e => e.Key == "NieznanyKlucz");
        Assert.Equal("7", editor.RawValueText);
        editor.RawValueText = "8";
        editor.ReplaceRawValueCommand.Execute(null);
        Assert.True(editor.RawEntries.Single(e => e.Key == "NieznanyKlucz").IsChanged);
        editor.SelectedRawEntry = editor.RawEntries.Single(e => e.Key.EndsWith("Łódź", StringComparison.Ordinal));
        editor.RemoveRawEntryCommand.Execute(null);
        Assert.Single(editor.RawEntries);

        await SaveAsync(editor);

        var text = Text(harness, gpo, GptTmpl);
        Assert.Contains("NieznanyKlucz = 8\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Łódź", text, StringComparison.Ordinal);
        Assert.Contains("[File Security]\r\n\"%SystemRoot%\\Temp\",0,\"D:PAR(A;OICI;FA;;;BA)\"\r\n", text, StringComparison.Ordinal);
        Assert.Contains("SeFutureRight = *S-1-5-32-544\r\n", text, StringComparison.Ordinal);
        Assert.Contains("\"Netlogon\",2,\"D:AR(A;;CCLC;;;SY)\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAdvancedSubcategory_IsWrittenToAuditCsv_AndALegacyCategoryToTheTemplate()
    {
        var harness = await GroupPolicyHarness.CreateAsync();
        var gpo = harness.Sample.SitePrinters;
        var editor = await harness.SecurityAsync(gpo);
        Assert.Null(editor.AuditNote);

        var logon = editor.AdvancedAuditCategories.SelectMany(c => c.Rows).Single(r => r.Id == "Logon");
        logon.SelectedState = logon.States.Single(s => s.Value == 1);
        var validation = editor.AdvancedAuditCategories.SelectMany(c => c.Rows).Single(r => r.Id == "CredentialValidation");
        validation.SelectedState = validation.States.Single(s => s.Value == 3);
        var legacy = editor.LegacyAudit.Single(r => r.Id == "AuditSystemEvents");
        legacy.SelectedState = legacy.States.Single(s => s.Value == 2);
        Assert.NotNull(editor.AuditNote);
        var force = Option(editor, "SCENoApplyLegacyAuditPolicy");
        force.IsDefined = true;
        force.SelectedChoice = force.Choices.Single(c => c.Value == 1);
        Assert.Null(editor.AuditNote);
        Assert.Equal(4, editor.ChangedCount);

        await SaveAsync(editor);

        var csv = Encoding.UTF8.GetString(harness.Sysvol.Get(gpo, AuditCsv)!);
        Assert.Equal(AuditHeader + "\r\n"
            + ",System,Audit Logon,{0cce9215-69ae-11d9-bed3-505054503030},Success,,1\r\n"
            + ",System,Audit Credential Validation,{0cce923f-69ae-11d9-bed3-505054503030},Success and Failure,,3\r\n", csv);
        var text = Text(harness, gpo, GptTmpl);
        Assert.Contains("[Event Audit]\r\nAuditSystemEvents = 2\r\n", text, StringComparison.Ordinal);
        Assert.Contains(@"SCENoApplyLegacyAuditPolicy = 4,1", text, StringComparison.Ordinal);
        Assert.Contains("{F3CCC681-B74C-4060-9F26-CD84525DCA2A}", harness.Value(harness.GpoDn(gpo), "gPCMachineExtensionNames"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Services_ChangeTheirStartMode_AndAreAddedAndRemoved()
    {
        var (harness, editor, gpo) = await OpenSampleAsync();
        var netlogon = editor.Services.Single(s => s.Name == "Netlogon");
        netlogon.SelectedMode = editor.ServiceModes.Single(m => m.Value == 3);
        Assert.True(netlogon.IsChanged);

        editor.NewServiceName = "Usługa \"zła\"";
        editor.AddServiceCommand.Execute(null);
        Assert.Contains(harness.B2.Notifications.Items, n => n.Severity == NotificationSeverity.Warning);
        editor.NewServiceName = "RemoteRegistry";
        editor.AddServiceCommand.Execute(null);
        Assert.Equal("Disabled", editor.SelectedService!.ModeText);
        editor.SelectedService = editor.Services.Single(s => s.Name == "Spooler");
        editor.RemoveServiceCommand.Execute(null);

        await SaveAsync(editor);

        var text = Text(harness, gpo, GptTmpl);
        Assert.Contains("\"Netlogon\",3,\"D:AR(A;;CCLC;;;SY)\"\r\n\"RemoteRegistry\",4,\"\"\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Spooler", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileChangedBehindTheEdits_KeepsThem_WithAConflictNote_AndDiscardShowsTheNewFile()
    {
        var (harness, editor, gpo) = await OpenSampleAsync();
        Right(editor, "SeBackupPrivilege").IsDefined = true;
        harness.Sysvol.Put(gpo, GptTmpl, FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template("[Privilege Rights]", "SeShutdownPrivilege = *S-1-5-32-544")));

        await editor.LoadAsync();

        Assert.NotNull(editor.ConflictNote);
        Assert.True(editor.HasChanges);
        Assert.True(Right(editor, "SeBackupPrivilege").IsDefined);

        await editor.SaveCommand.ExecuteAsync(null); // the compare-and-swap refuses it
        Assert.Equal("SeShutdownPrivilege = *S-1-5-32-544", Text(harness, gpo, GptTmpl).Split("\r\n")[^2]);

        harness.B2.Dialogs.ConfirmAnswers.Enqueue(true);
        await editor.DiscardCommand.ExecuteAsync(null);

        Assert.Null(editor.ConflictNote);
        Assert.False(editor.HasChanges);
        Assert.False(Right(editor, "SeBackupPrivilege").IsDefined);
        Assert.True(Right(editor, "SeShutdownPrivilege").IsDefined);
        Assert.Empty(editor.Services);
    }

    [Fact]
    public async Task EditsAfterASave_WhoseReloadCouldNotReadTheFile_StayWithoutAFalseConflict()
    {
        var (harness, editor, gpo) = await OpenSampleAsync();
        Right(editor, "SeBackupPrivilege").IsDefined = true;
        harness.Windows.AfterPreview = _ => harness.Sysvol.Fail("Read", SysvolWriteError.Failed, "GptTmpl.inf");
        await editor.SaveCommand.ExecuteAsync(null);
        await ObjectListViewModelTests.WaitUntilAsync(() => !editor.IsLoading);
        harness.Windows.AfterPreview = null;
        Assert.True(editor.HasChanges);

        Right(editor, "SeShutdownPrivilege").IsDefined = true;
        await editor.LoadAsync(); // the file now holds what this tab saved – not someone else's change

        Assert.Null(editor.ConflictNote);
        Assert.False(Right(editor, "SeBackupPrivilege").IsChanged);
        Assert.True(Right(editor, "SeShutdownPrivilege").IsChanged);
        Assert.Equal(1, editor.ChangedCount);

        await SaveAsync(editor);
        var text = Text(harness, gpo, GptTmpl);
        Assert.Contains("SeBackupPrivilege = \r\n", text, StringComparison.Ordinal);
        Assert.Contains("SeShutdownPrivilege = \r\n", text, StringComparison.Ordinal);
        Assert.False(editor.HasChanges);
    }

    [Fact]
    public async Task AFileThatCannotBeRead_MakesOnlyItsPagesReadOnly()
    {
        var harness = await GroupPolicyHarness.CreateAsync();
        var gpo = harness.Sample.SitePrinters;
        harness.Sysvol.PutText(gpo, GroupsXml, "<Groups><niedomknięty>");
        harness.Sysvol.Fail("Read", SysvolWriteError.AccessDenied, "audit.csv");

        var editor = await harness.SecurityAsync(gpo);

        Assert.Contains(GroupsXml, editor.FileNote, StringComparison.Ordinal);
        Assert.Contains(AuditCsv, editor.FileNote, StringComparison.Ordinal);
        Assert.Contains("access denied", editor.FileNote, StringComparison.Ordinal);
        Assert.False(editor.CanEditGroups);
        Assert.False(editor.CanEditAudit);
        Assert.True(editor.CanEditTemplate);
        Assert.False(editor.AddGppGroupCommand.CanExecute(null));
        Assert.True(editor.AddRestrictedGroupCommand.CanExecute(null));
        Assert.All(editor.AdvancedAuditCategories.SelectMany(c => c.Rows), r => Assert.False(r.CanEdit));
        Assert.All(editor.LegacyAudit, r => Assert.True(r.CanEdit));

        var logon = editor.AdvancedAuditCategories.SelectMany(c => c.Rows).Single(r => r.Id == "Logon");
        logon.SelectedState = logon.States[2]; // a click that slipped through: refused, the row shows the file again
        Assert.Null(logon.SelectedState!.Value);
        Assert.False(editor.HasChanges);
    }

    [Fact]
    public async Task WithoutAWritableSysvol_NothingCanBeEdited()
    {
        var harness = await GroupPolicyHarness.CreateAsync(null, withWriter: false);
        var editor = await harness.SecurityAsync(harness.Sample.SitePrinters);

        Assert.NotNull(editor.SysvolWarning);
        Assert.False(editor.CanEdit);
        Assert.False(editor.CanEditTemplate);
        Assert.Empty(editor.UserRights);
        Assert.False(editor.AddServiceCommand.CanExecute(null));
    }

    [Fact]
    public async Task WhileSaving_TheEditorIsReadOnly()
    {
        var (harness, editor, _) = await OpenSampleAsync();
        Right(editor, "SeBackupPrivilege").IsDefined = true;
        var seen = new List<(bool Saving, bool Template, bool Row, bool Save)>();
        harness.Windows.BeforePreview = _ =>
        {
            seen.Add((editor.IsSaving, editor.CanEditTemplate, Right(editor, "SeBackupPrivilege").CanEdit, editor.SaveCommand.CanExecute(null)));
            return Task.CompletedTask;
        };

        await SaveAsync(editor);

        Assert.Equal([(true, false, false, false)], seen);
        Assert.True(editor.CanEditTemplate);
        Assert.True(Right(editor, "SeBackupPrivilege").CanEdit);
    }

    [Fact]
    public async Task ClosingTheTab_WithUnsavedChanges_AsksFirst()
    {
        var harness = await GroupPolicyHarness.CreateAsync();
        using var shell = harness.CreateShell();
        var editor = Assert.IsType<SecuritySettingsEditorViewModel>(shell.OpenGroupPolicyDocument(
            new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.SecuritySettings) { GpoId = harness.Sample.SitePrinters }));
        await editor.LoadAsync();
        Assert.True(await editor.CanCloseAsync());

        Right(editor, "SeShutdownPrivilege").IsDefined = true;
        Assert.True(editor.HasUnsavedChanges);
        Assert.True(shell.HasUnsavedDocuments);

        harness.B2.Dialogs.ConfirmAnswers.Enqueue(false);
        await shell.CloseTabCommand.ExecuteAsync(editor);
        Assert.Contains(editor, shell.Documents);
        Assert.Contains("confirm:Setting changes not saved yet: 1. Close the tab and discard them?", harness.B2.Dialogs.Shown);

        harness.B2.Dialogs.ConfirmAnswers.Enqueue(true);
        await shell.CloseTabCommand.ExecuteAsync(editor);
        Assert.DoesNotContain(editor, shell.Documents);
    }

    [Fact]
    public async Task TheGpoDocumentAndTheGpoList_OpenTheEditor_InTheShell()
    {
        var harness = await GroupPolicyHarness.CreateAsync();
        using var shell = harness.CreateShell();
        var gpo = await harness.GpoAsync(harness.Sample.WorkstationPasswords);

        gpo.EditSecuritySettingsCommand.Execute(null);
        for (var i = 0; i < 200 && !shell.Documents.OfType<SecuritySettingsEditorViewModel>().Any(); i++)
        {
            await Task.Delay(10, Ct);
        }

        var editor = Assert.Single(shell.Documents.OfType<SecuritySettingsEditorViewModel>());
        Assert.Equal(harness.Sample.WorkstationPasswords, editor.GpoId);
        Assert.Same(editor, shell.SelectedTab);
        Assert.True(GroupPolicyViewModelFactory.Matches(editor,
            new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.SecuritySettings) { GpoId = harness.Sample.WorkstationPasswords }));
        Assert.False(GroupPolicyViewModelFactory.Matches(editor,
            new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.AdministrativeTemplates) { GpoId = harness.Sample.WorkstationPasswords }));

        var list = await harness.ObjectsAsync();
        Assert.False(list.EditSecuritySettingsCommand.CanExecute(null));
    }

    [Fact]
    public void EveryCatalogName_IsLocalized_InBothLanguages()
    {
        var english = new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"));
        var polish = new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"));
        var keys = SecuritySettingsText.AllKeys().ToList();

        Assert.Equal([], keys.Where(k => !english.Contains(k)));
        Assert.Equal([], keys.Where(k => polish[k].StartsWith('[')));
        Assert.Equal("Debug programs", english[SecuritySettingsText.RightKey("SeDebugPrivilege")]);
        Assert.Equal("Minimalna długość hasła", polish[SecuritySettingsText.OptionKey("MinimumPasswordLength")]);
        Assert.Equal("Przeprowadź inspekcję zdarzeń logowania", polish[SecuritySettingsText.EventAuditKey("AuditLogonEvents")]);
        Assert.Equal("Inspekcja logowania", polish[SecuritySettingsText.AuditSubcategoryKey("Logon")]);
        Assert.Equal("Goście", polish[SecuritySettingsText.LocalGroupNameKey("Guests")]);
        Assert.Equal("Administratorzy", polish[SecuritySettingsText.LocalGroupNameKey("Administrators")]);
    }
}
