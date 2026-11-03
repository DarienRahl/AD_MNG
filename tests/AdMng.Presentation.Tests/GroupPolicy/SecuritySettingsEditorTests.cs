using System.Globalization;
using System.Text;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Security;
using AdMng.Presentation.Changes;
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
    public async Task ASaveSeenLanding_NeverRebasesLaterEdits_OverARestoredFile()
    {
        var (harness, editor, gpo) = await OpenSampleAsync();
        Right(editor, "SeBackupPrivilege").IsDefined = true;
        await SaveAsync(editor); // S1 written, and the reload has seen it
        var saved = harness.Sysvol.Get(gpo, GptTmpl)!;
        Assert.False(editor.HasChanges);

        harness.Sysvol.Put(gpo, GptTmpl, FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template("[Privilege Rights]", "SeShutdownPrivilege = *S-1-5-32-544")));
        await editor.LoadAsync(); // F5: another administrator's file
        Right(editor, "SeDebugPrivilege").IsDefined = true;
        harness.Sysvol.Put(gpo, GptTmpl, saved); // S1 restored (a backup, an undo)
        await editor.LoadAsync();

        Assert.NotNull(editor.ConflictNote);
        Assert.True(editor.HasChanges);
        Assert.True(Right(editor, "SeDebugPrivilege").IsDefined);
        Assert.True(Right(editor, "SeShutdownPrivilege").IsDefined); // still the edits on the other administrator's file
    }

    [Fact]
    public async Task ACancelledSave_IsForgotten()
    {
        var (harness, editor, gpo) = await OpenSampleAsync();
        Right(editor, "SeBackupPrivilege").IsDefined = true;
        harness.Windows.ExecutePreviews = false;
        await editor.SaveCommand.ExecuteAsync(null);
        harness.Windows.ExecutePreviews = true;

        Assert.True(editor.HasChanges);
        Assert.False(editor.IsSaving);
        Assert.Single(harness.Windows.Previews);
        await SaveAsync(editor);
        Assert.Contains("SeBackupPrivilege = \r\n", Text(harness, gpo, GptTmpl), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhileLoading_TheEditorIsReadOnly_AndEditsAreRefused()
    {
        var (harness, editor, _) = await OpenSampleAsync();
        using var entered = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var armed = 1;
        harness.Sysvol.BeforeCall = (operation, _, path) =>
        {
            if (operation == "Read" && path?.EndsWith("GptTmpl.inf", StringComparison.Ordinal) == true && Interlocked.Exchange(ref armed, 0) == 1)
            {
                entered.Release();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        };

        var load = editor.LoadAsync();
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(30), Ct));
        try
        {
            Assert.True(editor.IsLoading);
            Assert.False(editor.CanEditTemplate);
            Assert.False(editor.CanEditGroups);
            Assert.False(Right(editor, "SeBackupPrivilege").CanEdit);
            Assert.False(editor.GppGroups[0].CanEdit);
            Assert.False(editor.AddServiceCommand.CanExecute(null));
            Assert.False(editor.AddGppGroupCommand.CanExecute(null));

            Right(editor, "SeBackupPrivilege").IsDefined = true; // a click that slipped through: refused
            editor.GppGroups[0].DeleteAllUsers = true;
            Assert.False(Right(editor, "SeBackupPrivilege").IsDefined);
            Assert.False(editor.GppGroups[0].DeleteAllUsers);
            Assert.False(editor.HasChanges);
        }
        finally
        {
            release.Set();
            await load;
            harness.Sysvol.BeforeCall = null;
        }

        Assert.True(editor.CanEditTemplate);
        Assert.True(Right(editor, "SeBackupPrivilege").CanEdit);
        Assert.True(editor.AddServiceCommand.CanExecute(null));
    }

    [Fact]
    public async Task GppChanges_AreCountedByItem_AndAnUndoneEditLeavesNoChange()
    {
        var harness = await GroupPolicyHarness.CreateAsync();
        var gpo = harness.Sample.SitePrinters;
        harness.Sysvol.PutText(gpo, GroupsXml, FakeDirectoryBuilder.Groups(
            FakeDirectoryBuilder.LocalGroup("Administrators (built-in)", "S-1-5-32-544", [("Łukasz", null, "ADD")], targeted: true),
            FakeDirectoryBuilder.LocalGroup("Serwisanci", null, []),
            FakeDirectoryBuilder.LocalGroup("Księgowość", null, [])));
        var editor = await harness.SecurityAsync(gpo);
        var first = editor.GppGroups[0];

        first.DeleteAllUsers = true;
        Assert.True(editor.HasChanges);
        Assert.Equal(1, editor.ChangedCount);
        first.DeleteAllUsers = false; // undone: the item as read again, no new "changed" time

        Assert.False(editor.HasChanges);
        Assert.Equal(0, editor.ChangedCount);
        Assert.False(first.IsChanged);
        Assert.True(await editor.CanCloseAsync());
        Assert.DoesNotContain(harness.B2.Dialogs.Shown, s => s.StartsWith("confirm:", StringComparison.Ordinal));

        editor.SelectedGppGroup = first;
        editor.RemoveGppGroupCommand.Execute(null); // [A, B, C] → [B, C]: one change
        Assert.Equal(1, editor.ChangedCount);
        Assert.Equal(["Serwisanci", "Księgowość"], editor.GppGroups.Select(g => g.GroupName));
        Assert.All(editor.GppGroups, g => Assert.False(g.IsChanged));
    }

    [Fact]
    public async Task AComboBoxClearingItsSelection_IsNotAnEdit()
    {
        var (_, editor, _) = await OpenSampleAsync();
        var level = Option(editor, "LmCompatibilityLevel");
        var anonymous = Option(editor, "LSAAnonymousNameLookup");
        var gpp = editor.GppGroups[0];

        // What the shared editor's combo box does while it switches from one row to the next.
        level.SelectedChoice = null;
        anonymous.SelectedChoice = null;
        gpp.SelectedAction = null;

        Assert.Equal(4, level.SelectedChoice!.Value);
        Assert.Equal(0, anonymous.SelectedChoice!.Value);
        Assert.Equal(GppItemAction.Update, gpp.SelectedAction!.Action);
        Assert.Null(level.Problem);
        Assert.False(editor.HasProblems);
        Assert.False(editor.HasChanges);
    }

    [Fact]
    public async Task TheTypedRawValue_SurvivesARefresh_AndIsResetForAnotherEntry()
    {
        var (_, editor, _) = await OpenSampleAsync();
        var entry = editor.RawEntries.Single(e => e.Key == "NieznanyKlucz");
        editor.SelectedRawEntry = entry;
        editor.RawValueText = "9";

        Right(editor, "SeBackupPrivilege").IsDefined = true; // any edit refreshes every page

        Assert.Same(entry, editor.SelectedRawEntry);
        Assert.Equal("9", editor.RawValueText);
        editor.ReplaceRawValueCommand.Execute(null);
        Assert.Equal("9", editor.SelectedRawEntry!.Value);
        Assert.Equal("9", editor.RawValueText);

        editor.SelectedRawEntry = editor.RawEntries.Single(e => e.Section == "Registry Values");
        Assert.Equal("1,\"zażółć\"", editor.RawValueText);
    }

    [Fact]
    public async Task APasswordLengthAbove14_WarnsUnlessTheLimitsAreRelaxed_WithoutBlockingTheSave()
    {
        var (_, editor, _) = await OpenSampleAsync(CultureInfo.GetCultureInfo("pl-PL"));
        var length = Option(editor, "MinimumPasswordLength");
        Assert.Null(length.Warning); // 12

        length.Text = "20";
        Assert.Contains("Złagodź limity minimalnej długości hasła", length.Warning, StringComparison.Ordinal);
        Assert.Null(length.Problem);
        Assert.True(editor.SaveCommand.CanExecute(null));

        var relax = Option(editor, "RelaxMinimumPasswordLengthLimits");
        Assert.Equal("Złagodź limity minimalnej długości hasła", relax.Name);
        relax.IsDefined = true;
        relax.SelectedChoice = relax.Choices.Single(c => c.Value == 1);
        Assert.Null(length.Warning);
    }

    [Fact]
    public async Task ATypedNameWithASemicolon_IsRefused()
    {
        var (harness, editor, _) = await OpenSampleAsync();
        editor.SelectedRight = Right(editor, "SeBackupPrivilege");
        editor.NewRightHolderText = @"KONTOSO\Serwis;anci";

        editor.AddRightHolderByNameCommand.Execute(null);

        Assert.False(Right(editor, "SeBackupPrivilege").IsDefined);
        Assert.False(editor.HasChanges);
        Assert.Contains(harness.B2.Notifications.Items, n => n.Severity == NotificationSeverity.Warning && n.Message.Contains("semicolon", StringComparison.Ordinal));
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

    // ---- K6: editing comfort (docs/dev/k-computers-and-gpo.md 20.3) ----------------------------------------------------

    [Fact]
    public async Task SearchAndFilters_NarrowEveryPage()
    {
        var (_, editor, _) = await OpenSampleAsync(CultureInfo.GetCultureInfo("pl-PL"));
        Assert.False(editor.IsFiltering);
        Assert.Equal(editor.UserRights, editor.VisibleUserRights);
        Assert.Equal(editor.SelectedOptionGroup!.Rows, editor.Options);
        Assert.Equal(2, editor.VisibleServices.Count);

        editor.SearchText = "DEBUGOWANIE";
        Assert.Equal(["SeDebugPrivilege"], editor.VisibleUserRights.Select(r => r.Constant));
        Assert.Empty(editor.VisibleServices);
        Assert.Empty(editor.VisibleRestrictedGroups);

        editor.SearchText = "serwisanci"; // a holder of a right and a GPP local group
        Assert.Equal(["SeRemoteInteractiveLogonRight"], editor.VisibleUserRights.Select(r => r.Constant));
        Assert.Equal(["Serwisanci"], editor.VisibleGppGroups.Select(g => g.GroupName));

        editor.SearchText = "poziom uwierzytelniania lan manager"; // the options list spans the groups
        Assert.True(editor.ShowsOptionGroupColumn);
        var option = Assert.Single(editor.Options);
        Assert.Equal("LmCompatibilityLevel", option.Definition.Id);
        Assert.Equal("Zabezpieczenia sieci", option.GroupName);

        editor.SearchText = "spooler";
        Assert.Equal(["Spooler"], editor.VisibleServices.Select(s => s.Name));

        editor.SearchText = "inspekcja logowania";
        var category = Assert.Single(editor.VisibleAuditCategories);
        Assert.Contains(category.Rows, r => r.Subcategory?.Id == "Logon");
        Assert.All(category.Rows, r => Assert.Contains("logowania", r.Name, StringComparison.OrdinalIgnoreCase));

        editor.ClearSearchCommand.Execute(null);
        Assert.False(editor.IsFiltering);
        Assert.Equal(editor.UserRights, editor.VisibleUserRights);
        Assert.False(editor.ShowsOptionGroupColumn);
    }

    [Fact]
    public async Task OnlyDefinedAndOnlyChanged_KeepARowWhileItIsEdited_UntilTheFilterChanges()
    {
        var (_, editor, _) = await OpenSampleAsync();

        editor.OnlyDefined = true;
        Assert.Equal(["SeDebugPrivilege", "SeFutureRight", "SeRemoteInteractiveLogonRight"], editor.VisibleUserRights.Select(r => r.Constant).Order(StringComparer.Ordinal));
        Assert.All(editor.Options, o => Assert.True(o.IsDefined));
        Assert.Contains(editor.Options, o => o.Definition.Id == "MinimumPasswordLength");
        Assert.Equal(["AuditLogonEvents"], editor.VisibleLegacyAudit.Select(r => r.Id));
        Assert.Equal("Logon", Assert.Single(Assert.Single(editor.VisibleAuditCategories).Rows).Subcategory!.Id);

        var debug = Right(editor, "SeDebugPrivilege");
        debug.IsDefined = false; // edited under the filter: the row stays listed…
        Assert.Contains(debug, editor.VisibleUserRights);
        editor.OnlyDefined = false;
        editor.OnlyDefined = true; // …until the filter changes
        Assert.DoesNotContain(debug, editor.VisibleUserRights);

        editor.OnlyDefined = false;
        editor.OnlyChanged = true;
        Assert.Equal([debug], editor.VisibleUserRights);
        Assert.Empty(editor.VisibleServices);
        var spooler = editor.Services.Single(s => s.Name == "Spooler");
        spooler.SelectedMode = editor.ServiceModes.Single(m => m.Value == 3); // changed while listing only the changed: it appears
        Assert.Equal([spooler], editor.VisibleServices);
    }

    [Fact]
    public async Task FocusSetting_OpensThePageAndTheRowOfTheSetting()
    {
        var (harness, editor, _) = await OpenSampleAsync();
        editor.SearchText = "nie ma takiego";

        Assert.True(editor.FocusSetting(GroupPolicySettingFocus.ForSetting("Computer/Security/Privilege Rights/SeDebugPrivilege")));
        Assert.Equal(string.Empty, editor.SearchText);
        Assert.Equal(SecuritySettingsPage.Rights, editor.SelectedPage);
        Assert.Equal(1, editor.SelectedPageIndex);
        Assert.Equal("SeDebugPrivilege", editor.SelectedRight!.Constant);

        Assert.True(editor.FocusSetting(GroupPolicySettingFocus.ForSetting("Computer/Security/System Access/MinimumPasswordLength")));
        Assert.Equal(SecuritySettingsPage.Options, editor.SelectedPage);
        Assert.Equal("MinimumPasswordLength", editor.SelectedOption!.Definition.Id);
        Assert.Contains(editor.SelectedOption, editor.SelectedOptionGroup!.Rows);

        Assert.True(editor.FocusSetting(GroupPolicySettingFocus.ForSetting(@"Computer/Security/Registry Values/HKLM\System\CurrentControlSet\Control\Lsa!LmCompatibilityLevel")));
        Assert.Equal("LmCompatibilityLevel", editor.SelectedOption!.Definition.Id);

        Assert.True(editor.FocusSetting(GroupPolicySettingFocus.ForSetting("Computer/Security/System Access/NieznanyKlucz")));
        Assert.True(editor.IsRawGroupSelected);
        Assert.Equal("NieznanyKlucz", editor.SelectedRawEntry!.Key);

        Assert.True(editor.FocusSetting(GroupPolicySettingFocus.ForSetting("Computer/Security/Event Audit/AuditLogonEvents")));
        Assert.Equal(SecuritySettingsPage.Audit, editor.SelectedPage);
        Assert.Equal(["AuditLogonEvents"], editor.LegacyAudit.Where(r => r.IsFocused).Select(r => r.Id));

        Assert.True(editor.FocusSetting(GroupPolicySettingFocus.ForSetting("Computer/Audit/{0cce9215-69ae-11d9-bed3-505054503030}")));
        Assert.DoesNotContain(editor.LegacyAudit, r => r.IsFocused);
        Assert.Equal("Logon", editor.AdvancedAuditCategories.SelectMany(c => c.Rows).Single(r => r.IsFocused).Subcategory!.Id);

        Assert.True(editor.FocusSetting(GroupPolicySettingFocus.ForSetting("Computer/Security/Group Membership/S-1-5-32-544/Members")));
        Assert.Equal(SecuritySettingsPage.Groups, editor.SelectedPage);
        Assert.Equal("S-1-5-32-544", editor.SelectedRestrictedGroup!.GroupValue);

        Assert.True(editor.FocusSetting(GroupPolicySettingFocus.ForSetting("Computer/Security/Services/Netlogon/StartupMode")));
        Assert.Equal(SecuritySettingsPage.Services, editor.SelectedPage);
        Assert.Equal("Netlogon", editor.SelectedService!.Name);

        var uid = GppGroupsDocument.Parse(harness.Sysvol.Get(harness.Sample.SitePrinters, GroupsXml)).Items[1].Uid;
        Assert.True(editor.FocusSetting(GroupPolicySettingFocus.ForSetting($"Computer/Preferences/Groups/{uid}/Properties@action")));
        Assert.Equal(SecuritySettingsPage.Groups, editor.SelectedPage);
        Assert.Same(editor.GppGroups[1], editor.SelectedGppGroup);

        Assert.False(editor.FocusSetting(GroupPolicySettingFocus.ForSetting("Computer/Security/Privilege Rights/SeNieznanePrawo")));
        Assert.Contains(harness.B2.Notifications.Items, n => n.Severity == NotificationSeverity.Info && n.Message.Contains("SeNieznanePrawo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Saving_ShowsTheSettingChanges_OfEveryPage()
    {
        var (harness, editor, _) = await OpenSampleAsync(CultureInfo.GetCultureInfo("pl-PL"));
        var shutdown = Right(editor, "SeShutdownPrivilege");
        editor.SelectedRight = shutdown;
        editor.NewRightHolderText = "Administratorzy";
        editor.AddRightHolderByNameCommand.Execute(null);
        Option(editor, "MinimumPasswordLength").Text = "14";
        var logon = editor.LegacyAudit.Single(r => r.Id == "AuditLogonEvents");
        logon.SelectedState = logon.States.Single(s => s.Value == 1);
        var advanced = editor.AdvancedAuditCategories.SelectMany(c => c.Rows).Single(r => r.Subcategory!.Id == "Logon");
        advanced.SelectedState = advanced.States.Single(s => s.Value == 1);
        editor.Services.Single(s => s.Name == "Spooler").SelectedMode = editor.ServiceModes.Single(m => m.Value == 3);
        editor.SelectedRawEntry = editor.RawEntries.Single(e => e.Key == "NieznanyKlucz");
        editor.RemoveRawEntryCommand.Execute(null);
        editor.SelectedRestrictedGroup = editor.RestrictedGroups.Single();
        editor.RemoveRestrictedMemberCommand.Execute(editor.RestrictedGroups.Single().Members.Single());
        var gpp = editor.GppGroups[0];
        gpp.SelectedAction = gpp.Actions.Single(a => a.Action == GppItemAction.Replace);
        IReadOnlyList<GroupPolicySettingChange> shown = [];
        harness.Windows.BeforePreview = preview =>
        {
            shown = preview.SettingChanges;
            return Task.CompletedTask;
        };

        await SaveAsync(editor);

        Assert.Equal(8, shown.Count);
        Assert.Contains(new GroupPolicySettingChange("Zamykanie systemu", "Nie zdefiniowano", "Administratorzy", "Prawa użytkownika"), shown);
        Assert.Contains(new GroupPolicySettingChange("Minimalna długość hasła", "12", "14", "Zasady haseł"), shown);
        Assert.Contains(new GroupPolicySettingChange("Przeprowadź inspekcję zdarzeń logowania", "Powodzenie i niepowodzenie", "Powodzenie",
            "Zasady inspekcji (kategorie, [Event Audit])"), shown);
        Assert.Contains(new GroupPolicySettingChange("Inspekcja logowania", "Powodzenie i niepowodzenie", "Powodzenie", "Logowanie/wylogowywanie"), shown);
        Assert.Contains(new GroupPolicySettingChange("Spooler", "Wyłączony", "Ręczny", "Usługi"), shown);
        Assert.Contains(shown, c => c.Setting == "[System Access] NieznanyKlucz" && c.Before == "7" && c.After == "Nie zdefiniowano");
        Assert.Contains(new GroupPolicySettingChange("Członkowie grupy Administratorzy", @"CONTOSO\GG_IT", "Brak członków (zdefiniowano pustą listę)",
            "Grupy z ograniczeniami (GptTmpl.inf)"), shown);
        Assert.Contains(new GroupPolicySettingChange("Administratorzy", @"Aktualizuj; członkowie: + CONTOSO\GG_IT, − Łukasz",
            @"Zamień; członkowie: + CONTOSO\GG_IT, − Łukasz", "Preferencje – grupy lokalne (Groups.xml)"), shown);
    }

    [Fact]
    public async Task ASecretRegistryValueOutsideTheCatalog_IsMaskedInTheSummary()
    {
        var harness = await GroupPolicyHarness.CreateAsync();
        var gpo = harness.Sample.SitePrinters;
        harness.Sysvol.Put(gpo, GptTmpl, FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template(
            "[Registry Values]", @"MACHINE\Software\Microsoft\Windows NT\CurrentVersion\Winlogon\DefaultPassword=1,""Hasło-Łódź1""")));
        var editor = await harness.SecurityAsync(gpo);
        editor.SelectedOptionGroup = editor.OptionGroups.Single(g => g.IsRaw);
        editor.SelectedRawEntry = Assert.Single(editor.RawEntries);
        editor.RemoveRawEntryCommand.Execute(null);
        IReadOnlyList<GroupPolicySettingChange> shown = [];
        harness.Windows.BeforePreview = preview =>
        {
            shown = preview.SettingChanges;
            return Task.CompletedTask;
        };

        await SaveAsync(editor);

        var change = Assert.Single(shown);
        Assert.Equal("••••••", change.Before);
        Assert.DoesNotContain("Hasło", string.Join("|", shown.Select(c => c.Setting + c.Before + c.After)), StringComparison.Ordinal);
    }
}
