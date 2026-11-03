using System.Globalization;
using System.Resources;
using System.Text;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.Settings;
using AdMng.Presentation.Localization;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.GroupPolicy.Settings;

/// <summary>
/// <see cref="GpoSettingPresenter"/> (K6, design note 20.1): every area of the GPO content parsers in GPMC's Polish words –
/// tree paths, names, units with Polish plural forms, special values, principals by name, preferences, scripts – and that
/// masked values stay masked and no key or value of an unknown shape throws.
/// </summary>
public sealed class GpoSettingPresenterTests
{
    private const string ComputerPolicies = "Konfiguracja komputera › Zasady";
    private const string SecuritySettings = ComputerPolicies + " › Ustawienia systemu Windows › Ustawienia zabezpieczeń";
    private const string DomainSid = "S-1-5-21-1004336348-1177238915-682003330";
    private const string Serwisanci = DomainSid + "-1105";

    private static readonly ILocalizer Polish = new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"));
    private static readonly ILocalizer English = new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"));

    private static GpoSettingPresenter Presenter(ILocalizer? localizer = null) =>
        new(localizer ?? Polish, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [Serwisanci] = "KONTOSO\\Serwisanci" });

    private static GpoSetting Text(string key, string value) => new() { Key = key, Value = value };

    private static GpoSetting List(string key, params string[] elements) => new() { Key = key, Value = string.Join(GpoSetting.ListSeparator, elements), IsList = true };

    private static string Pl(long number) => number.ToString("N0", CultureInfo.GetCultureInfo("pl-PL"));

    [Fact]
    public void AccountPolicies_SpeakGpmcsPolish_WithUnits()
    {
        var text = Presenter().Describe(Text("Computer/Security/System Access/MaximumPasswordAge", "42"));

        Assert.Equal(SecuritySettings + " › Zasady konta › Zasady haseł", text.GroupPath);
        Assert.Equal("Konfiguracja komputera", text.Configuration);
        Assert.Equal("Maksymalny okres ważności hasła", text.Name);
        Assert.Equal("42 dni", text.ValueText);
        Assert.Equal("Computer/Security/System Access/MaximumPasswordAge", text.Technical);
        Assert.Equal(GroupPolicyDocumentKind.SecuritySettings, text.Focus?.Editor);

        Assert.Equal("12 znaków", Presenter().Describe(Text("Computer/Security/System Access/MinimumPasswordLength", "12")).ValueText);
        Assert.Equal("24 zapamiętane hasła", Presenter().Describe(Text("Computer/Security/System Access/PasswordHistorySize", "24")).ValueText);
        Assert.Equal("Włączone", Presenter().Describe(Text("Computer/Security/System Access/PasswordComplexity", "1")).ValueText);
        Assert.Equal("Wyłączone", Presenter().Describe(Text("Computer/Security/System Access/ClearTextPassword", "0")).ValueText);
        var reset = Presenter().Describe(Text("Computer/Security/System Access/ResetLockoutCount", "30"));
        Assert.Equal("30 minut", reset.ValueText);
        Assert.Equal(SecuritySettings + " › Zasady konta › Zasady blokady konta", reset.GroupPath);
    }

    [Theory]
    [InlineData(1, "1 nieudana próba logowania")]
    [InlineData(3, "3 nieudane próby logowania")]
    [InlineData(5, "5 nieudanych prób logowania")]
    [InlineData(12, "12 nieudanych prób logowania")]
    [InlineData(22, "22 nieudane próby logowania")]
    [InlineData(25, "25 nieudanych prób logowania")]
    public void LockoutThreshold_UsesPolishPluralForms(int value, string expected) =>
        Assert.Equal(expected, Presenter().Describe(Text("Computer/Security/System Access/LockoutBadCount", value.ToString(CultureInfo.InvariantCulture))).ValueText);

    [Theory]
    [InlineData("MaximumPasswordAge", "0", "Hasło nie wygasa")]
    [InlineData("MaximumPasswordAge", "-1", "Hasło nie wygasa")]
    [InlineData("MinimumPasswordAge", "0", "Hasło można zmienić od razu")]
    [InlineData("MinimumPasswordLength", "0", "Hasło nie jest wymagane")]
    [InlineData("PasswordHistorySize", "0", "Historia haseł nie jest przechowywana")]
    [InlineData("LockoutBadCount", "0", "Konto nie jest blokowane")]
    [InlineData("LockoutDuration", "-1", "Do odblokowania przez administratora")]
    [InlineData("LockoutDuration", "0", "Do odblokowania przez administratora")]
    [InlineData("MinimumPasswordAge", "1", "1 dzień")]
    [InlineData("LockoutDuration", "15", "15 minut")]
    public void SpecialValues_AreExplained(string key, string value, string expected) =>
        Assert.Equal(expected, Presenter().Describe(Text("Computer/Security/System Access/" + key, value)).ValueText);

    [Fact]
    public void Kerberos_HasItsFolder_HoursAndTheClockTolerance()
    {
        var ticket = Presenter().Describe(Text("Computer/Security/Kerberos Policy/MaxTicketAge", "10"));
        var skew = Presenter().Describe(Text("Computer/Security/Kerberos Policy/MaxClockSkew", "5"));

        Assert.Equal(SecuritySettings + " › Zasady konta › Zasady protokołu Kerberos", ticket.GroupPath);
        Assert.Equal("10 godzin", ticket.ValueText);
        Assert.Equal("Maksymalna tolerancja synchronizacji zegara komputera", skew.Name);
        Assert.Equal("5 minut", skew.ValueText);
        Assert.Contains("zegarem kontrolera domeny", skew.Explanation, StringComparison.Ordinal);
        Assert.Equal("4 dni", Presenter().Describe(Text("Computer/Security/Kerberos Policy/MaxRenewAge", "4")).ValueText);
        Assert.Equal("600 minut", Presenter().Describe(Text("Computer/Security/Kerberos Policy/MaxServiceAge", "600")).ValueText);
    }

    [Fact]
    public void Audit_LegacyCategories_AndAdvancedSubcategories()
    {
        var legacy = Presenter().Describe(Text("Computer/Security/Event Audit/AuditLogonEvents", "Success and Failure"));
        var advanced = Presenter().Describe(Text("Computer/Audit/{0cce9215-69ae-11d9-bed3-505054503030}", "Success; exclusion: Failure"));
        var option = Presenter().Describe(Text("Computer/Audit/Option/CrashOnAuditFail", "1"));

        Assert.Equal(SecuritySettings + " › Zasady lokalne › Zasady inspekcji", legacy.GroupPath);
        Assert.Equal("Przeprowadź inspekcję zdarzeń logowania", legacy.Name);
        Assert.Equal("Powodzenie i niepowodzenie", legacy.ValueText);
        Assert.Equal("Bez inspekcji", Presenter().Describe(Text("Computer/Security/Event Audit/AuditSystemEvents", "No Auditing")).ValueText);
        Assert.Equal(SecuritySettings + " › Konfiguracja zaawansowanych zasad inspekcji › Zasady inspekcji › Logowanie/wylogowywanie", advanced.GroupPath);
        Assert.Equal("Inspekcja logowania", advanced.Name);
        Assert.StartsWith("Powodzenie", advanced.ValueText, StringComparison.Ordinal);
        Assert.Equal(["Wykluczenie: Niepowodzenie"], advanced.Elements);
        Assert.Equal("Włączone", option.ValueText);
        Assert.StartsWith("Inspekcja: Wyłącz system natychmiast", option.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void UserRights_NamePrincipals_LocalGroupsWithoutTheDirectory()
    {
        var right = List("Computer/Security/Privilege Rights/SeDebugPrivilege", Serwisanci, "S-1-5-32-544");

        var text = Presenter().Describe(right);

        Assert.Equal(SecuritySettings + " › Zasady lokalne › Przypisywanie praw użytkownika", text.GroupPath);
        Assert.Equal("Debugowanie programów", text.Name);
        Assert.Equal("Administratorzy, KONTOSO\\Serwisanci", text.ValueText);
        Assert.Equal(["Administratorzy", "KONTOSO\\Serwisanci"], text.Elements);
        Assert.Equal("Nikt (zdefiniowano pustą listę)", Presenter().Describe(List("Computer/Security/Privilege Rights/SeTcbPrivilege")).ValueText);
        Assert.Equal("USŁUGA LOKALNA, Wszyscy", Presenter().Describe(List("Computer/Security/Privilege Rights/SeChangeNotifyPrivilege", "S-1-1-0", "S-1-5-19")).ValueText);
        // A SID nobody resolved stays a SID; a principal written as a name stays the name.
        Assert.Equal("KONTOSO\\Ktoś, S-1-5-21-1-2-3-4", Presenter().Describe(List("Computer/Security/Privilege Rights/SeBackupPrivilege", "S-1-5-21-1-2-3-4", "KONTOSO\\Ktoś")).ValueText);
    }

    [Fact]
    public void Sids_AreOnlyThoseTheDirectoryMustName()
    {
        GpoSetting[] settings =
        [
            List("Computer/Security/Privilege Rights/SeDebugPrivilege", Serwisanci, "S-1-5-32-544", "S-1-1-0"),
            List($"Computer/Security/Group Membership/*{DomainSid}-1200/Members", DomainSid + "-1201"),
            List("Computer/Preferences/Groups/{1}/Properties/Members", $"Member action=ADD name=KONTOSO\\Księgowość sid={DomainSid}-1300"),
            Text("Computer/Preferences/Groups/{1}/groupSid", "S-1-5-32-544"),
            Text("Computer/Security/System Access/MinimumPasswordLength", "12"),
        ];

        var sids = GpoSettingPresenter.Sids(settings);

        Assert.Equal([Serwisanci, DomainSid + "-1200", DomainSid + "-1201", DomainSid + "-1300"], sids.Order(StringComparer.Ordinal));
        Assert.Equal([Serwisanci], GpoSettingPresenter.Sids("Computer/Security/Privilege Rights/SeDebugPrivilege", [Serwisanci, "S-1-5-18"]));
    }

    [Fact]
    public void RestrictedGroups_MembersAndMemberOf()
    {
        var members = Presenter().Describe(List("Computer/Security/Group Membership/S-1-5-32-544/Members"));
        var memberOf = Presenter().Describe(List($"Computer/Security/Group Membership/{Serwisanci}/MemberOf", "S-1-5-32-555"));

        Assert.Equal(SecuritySettings + " › Grupy z ograniczeniami", members.GroupPath);
        Assert.Equal("Administratorzy – członkowie", members.Name);
        Assert.Equal("Brak członków (zdefiniowano pustą listę)", members.ValueText);
        Assert.Contains("wszyscy członkowie są usuwani", members.Explanation, StringComparison.Ordinal);
        Assert.Equal("KONTOSO\\Serwisanci – jest członkiem grup", memberOf.Name);
        Assert.Equal("Użytkownicy pulpitu zdalnego", memberOf.ValueText);
    }

    [Fact]
    public void SecurityOptions_ByTheCatalog_AndUnknownRegistryValuesByTheirPath()
    {
        var lm = Presenter().Describe(Text(@"Computer/Security/Registry Values/HKLM\System\CurrentControlSet\Control\Lsa!LmCompatibilityLevel", "REG_DWORD:5"));
        var uac = Presenter().Describe(Text(@"Computer/Security/Registry Values/HKLM\Software\Microsoft\Windows\CurrentVersion\Policies\System!EnableLUA", "REG_DWORD:0"));
        var kerberos = Presenter().Describe(Text(@"Computer/Security/Registry Values/HKLM\Software\Microsoft\Windows\CurrentVersion\Policies\System\Kerberos\Parameters!SupportedEncryptionTypes", "REG_DWORD:24"));
        var notice = Presenter().Describe(Text(@"Computer/Security/Registry Values/HKLM\Software\Microsoft\Windows\CurrentVersion\Policies\System!LegalNoticeText", "REG_MULTI_SZ:\"Zażółć\",\"gęślą jaźń\""));
        var unknown = Presenter().Describe(Text(@"Computer/Security/Registry Values/HKLM\Software\Firma!Tryb", "REG_SZ:pełny"));

        Assert.Equal(SecuritySettings + " › Zasady lokalne › Opcje zabezpieczeń › Zabezpieczenia sieci", lm.GroupPath);
        Assert.Equal("Zabezpieczenia sieci: Poziom uwierzytelniania LAN Manager", lm.Name);
        Assert.Equal("Wyślij tylko odpowiedź NTLMv2. Odmów LM i NTLM", lm.ValueText);
        Assert.Equal("Wyłączone", uac.ValueText);
        Assert.Equal("AES128_HMAC_SHA1, AES256_HMAC_SHA1", kerberos.ValueText);
        Assert.Equal(["Zażółć", "gęślą jaźń"], notice.Elements);
        Assert.Equal(SecuritySettings + " › Zasady lokalne › Opcje zabezpieczeń › Inne", unknown.GroupPath);
        Assert.Equal(@"HKLM\Software\Firma\Tryb", unknown.Name);
        Assert.Equal("pełny (REG_SZ)", unknown.ValueText);
    }

    [Fact]
    public void Services_ObjectSecurity_AndEventLogs()
    {
        var spooler = Presenter().Describe(Text("Computer/Security/Services/Spooler/StartupMode", "Disabled"));
        var size = Presenter().Describe(Text("Computer/Security/Security Log/MaximumLogSize", "20480"));
        var retention = Presenter().Describe(Text("Computer/Security/System Log/AuditLogRetentionPeriod", "2"));
        var folder = Presenter().Describe(Text(@"Computer/Security/File Security/%SystemRoot%\Temp/Mode", "Replace"));

        Assert.Equal(SecuritySettings + " › Usługi systemowe", spooler.GroupPath);
        Assert.Equal("Spooler", spooler.Name);
        Assert.Equal("Wyłączony", spooler.ValueText);
        Assert.Equal("Maksymalny rozmiar dziennika zabezpieczeń", size.Name);
        Assert.Equal(Pl(20480) + " KB", size.ValueText);
        Assert.Equal(SecuritySettings + " › Dziennik zdarzeń", size.GroupPath);
        Assert.Equal("Metoda przechowywania dziennika systemu", retention.Name);
        Assert.Equal("Nie zastępuj zdarzeń (czyść dziennik ręcznie)", retention.ValueText);
        Assert.Equal(SecuritySettings + " › System plików", folder.GroupPath);
        Assert.StartsWith("Zastąp istniejące uprawnienia", folder.ValueText, StringComparison.Ordinal);

        // A view of rows: a service's start mode and permissions are one row.
        var rows = Presenter().DescribeRows(
        [
            Text("Computer/Security/Services/Spooler/StartupMode", "Disabled"),
            Text("Computer/Security/Services/Spooler/Security", "D:AR(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)"),
        ]);
        var row = Assert.Single(rows);
        Assert.Equal("Spooler", row.Name);
        Assert.Equal("Wyłączony; Zdefiniowano uprawnienia", row.ValueText);
        Assert.Contains("Tryb uruchamiania: Wyłączony", row.Elements);
        Assert.Contains("D:AR(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)", row.Elements);
        Assert.Equal(2, row.Keys.Count);
    }

    [Fact]
    public void RegistryPol_WithoutTemplates_ShowsThePathTypedDataAndActions()
    {
        const string Key = @"Computer/Registry/HKLM\Software\Policies\Contoso\Aktualizacje\AU";

        var dword = Presenter().Describe(Text(Key + "!NoAutoUpdate", "REG_DWORD:1"));
        var big = Presenter().Describe(Text(Key + "!Limit", "REG_DWORD:4096"));

        Assert.Equal(ComputerPolicies + " › Szablony administracyjne › Dodatkowe ustawienia rejestru", dword.GroupPath);
        Assert.Equal(@"HKLM\Software\Policies\Contoso\Aktualizacje\AU\NoAutoUpdate", dword.Name);
        Assert.Equal("1 (REG_DWORD)", dword.ValueText);
        Assert.Equal(GroupPolicyDocumentKind.AdministrativeTemplates, dword.Focus?.Editor);
        Assert.Equal("4096 (0x1000) (REG_DWORD)", big.ValueText);
        Assert.Equal("Usuń wartość", Presenter().Describe(Text(Key + "!AUOptions", "DELETE")).ValueText);
        var all = Presenter().Describe(Text(Key + "!**delvals.", "DELETE_ALL_VALUES"));
        Assert.Equal(@"HKLM\Software\Policies\Contoso\Aktualizacje\AU", all.Name);
        Assert.Equal("Usuń wszystkie wartości klucza", all.ValueText);
        Assert.Equal("Utwórz klucz", Presenter().Describe(Text(Key, "CREATE_KEY")).ValueText);
        Assert.Equal("Usuń klucz", Presenter().Describe(Text(Key + @"\Stary", "DELETE_KEY")).ValueText);
        Assert.Equal(@"HKLM\Software\Policies\Contoso\Aktualizacje\AU\(Domyślna)", Presenter().Describe(Text(Key + "!(Default)", "REG_SZ:x")).Name);
        var soft = Presenter().Describe(Text(Key + "!Serwer", "SOFT:REG_SZ:https://wsus.contoso.test"));
        Assert.Equal("https://wsus.contoso.test (REG_SZ) – tylko gdy wartość jeszcze nie istnieje", soft.ValueText);
        var lines = Presenter().Describe(Text(@"User/Registry/HKCU\Software\Firma!Linie", "REG_MULTI_SZ:\"Łódź\",\"Gdańsk\""));
        Assert.Equal("Konfiguracja użytkownika › Zasady › Szablony administracyjne › Dodatkowe ustawienia rejestru", lines.GroupPath);
        Assert.Equal(["Łódź", "Gdańsk"], lines.Elements);
        Assert.Equal("Łódź; Gdańsk (REG_MULTI_SZ)", lines.ValueText);
    }

    [Fact]
    public void RegistryPol_WithTemplates_CollapsesAPolicysValuesIntoOnePolicyRow()
    {
        var set = SampleAdministrativeTemplates.LoadPolish();
        var document = new RegistryPolicyDocument();
        var updates = set.Find(SampleAdministrativeTemplates.Updates)!;
        PolicyEngine.Write(updates.Policy, PolicySetting.Enabled(new Dictionary<string, PolicyElementValue>
        {
            ["Tryb"] = new EnumElementValue(2),
            ["Dzien"] = new NumberElementValue(3),
            ["InneProdukty"] = new BooleanElementValue(true),
        }), document);
        document.Set(@"Software\Firma\Inne", "Wartość", RegistryFormat.RegDword, RegistryPolFile.DwordData(7));
        var content = new GpoFolderContent([], new Dictionary<string, byte[]> { ["Machine/Registry.pol"] = document.ToBytes() });
        var settings = GroupPolicyReader.Settings(content);
        var (computer, user) = GpoSettingPresenter.Policies(content, set);
        var presenter = new GpoSettingPresenter(Polish, null, set) { ComputerPolicies = computer, UserPolicies = user };

        var rows = presenter.DescribeRows(settings);

        Assert.Equal(2, rows.Count);
        var policy = Assert.Single(rows, r => r.Name == updates.DisplayName);
        Assert.Equal(ComputerPolicies + " › Szablony administracyjne › " + string.Join(" › ", updates.Category!.PathNames()), policy.GroupPath);
        Assert.StartsWith("Włączone", policy.ValueText, StringComparison.Ordinal);
        Assert.Contains("Konfigurowanie aktualizacji automatycznych: 4 – Pobierz automatycznie i zaplanuj instalację", policy.Elements);
        Assert.Equal(GroupPolicySettingFocus.ForPolicy(PolicyClass.Machine, SampleAdministrativeTemplates.Updates), policy.Focus);
        Assert.Equal(4, policy.Keys.Count);
        Assert.Contains("Określa, czy ten komputer", policy.Explanation, StringComparison.Ordinal);
        var extra = Assert.Single(rows, r => r.Name == @"HKLM\Software\Firma\Inne\Wartość");
        Assert.EndsWith("Dodatkowe ustawienia rejestru", extra.GroupPath, StringComparison.Ordinal);
        Assert.Equal("7 (REG_DWORD)", extra.ValueText);
    }

    [Fact]
    public void Preferences_OneRowPerItem_WithActionMembersAndTargeting()
    {
        var groups = FakeDirectoryBuilder.Groups(FakeDirectoryBuilder.LocalGroup("Administrators (built-in)", "S-1-5-32-544",
            [("KONTOSO\\Serwisanci", Serwisanci, "ADD"), ("KONTOSO\\Goście firmowi", null, "REMOVE")], deleteAllUsers: true, targeted: true));
        var settings = Read("Machine/Preferences/Groups/Groups.xml", groups);

        var row = Assert.Single(Presenter().DescribeRows(settings));

        Assert.Equal("Konfiguracja komputera › Preferencje › Ustawienia panelu sterowania › Lokalni użytkownicy i grupy", row.GroupPath);
        Assert.Equal("Administrators (built-in)", row.Name);
        Assert.Equal("Aktualizuj: + KONTOSO\\Serwisanci, − KONTOSO\\Goście firmowi", row.ValueText);
        Assert.Contains("Typ elementu: Grupa lokalna", row.Elements);
        Assert.Contains("Akcja: Aktualizuj", row.Elements);
        Assert.Contains("Grupa (SID): Administratorzy", row.Elements);
        Assert.Contains("Usuń wszystkich użytkowników członkowskich: Tak", row.Elements);
        Assert.Contains("Kierowanie na poziomie elementu: komputer WAW-PC001", row.Elements);
        Assert.Equal(GroupPolicyDocumentKind.SecuritySettings, row.Focus?.Editor);
        Assert.Equal(settings.Count, row.Keys.Count);
    }

    [Fact]
    public void ALocalUserOfGroupsXml_OffersNoEditor_ItsLocalGroupsDo()
    {
        const string User = "<User clsid=\"{DF5F1855-51E5-4d24-8B1A-D9BDE98BA1D1}\" name=\"Administrator (built-in)\" image=\"2\" changed=\"2026-10-20 10:00:00\""
            + " uid=\"{8A2B5C1D-0E3F-4A6B-9C7D-1E2F3A4B5C6D}\"><Properties action=\"U\" newName=\"Admin-Łódź\" fullName=\"\" description=\"\""
            + " changeLogon=\"0\" noChange=\"0\" neverExpires=\"0\" acctDisabled=\"0\" userName=\"Administrator (built-in)\"/></User>";
        var settings = Read("Machine/Preferences/Groups/Groups.xml",
            FakeDirectoryBuilder.Groups(User, FakeDirectoryBuilder.LocalGroup("Users (built-in)", "S-1-5-32-545", [])));

        var rows = Presenter().DescribeRows(settings);

        Assert.Null(rows.Single(r => r.Name == "Administrator (built-in)").Focus);
        Assert.Equal(GroupPolicyDocumentKind.SecuritySettings, rows.Single(r => r.Name == "Users (built-in)").Focus?.Editor);
    }

    [Fact]
    public void Preferences_DriveMaps_AndAPropertyOnItsOwn()
    {
        const string Drives = "<?xml version=\"1.0\" encoding=\"utf-8\"?><Drives clsid=\"{8FDDCC1A-0C3C-43cd-A6B4-71A6DF20DA8C}\"><Drive clsid=\"{935D1B74-9CB8-4e3c-9914-7DD559B7A417}\" name=\"K:\" uid=\"{0D1F7F22-6D1B-4A35-9D9C-0E3D9F3F0001}\"><Properties action=\"U\" path=\"\\\\fs01.contoso.test\\Księgowość\" label=\"Księgowość\" letter=\"K\" useLetter=\"1\"/></Drive></Drives>";
        var settings = Read("User/Preferences/Drives/Drives.xml", Drives);

        var row = Assert.Single(Presenter().DescribeRows(settings));
        var path = settings.Single(s => s.Key.EndsWith("/path", StringComparison.Ordinal));
        var alone = Presenter().Describe(path);
        var named = new GpoSettingPresenter(Polish) { ItemNames = GpoSettingPresenter.ItemNamesOf(settings) }.Describe(path);

        Assert.Equal("Konfiguracja użytkownika › Preferencje › Ustawienia systemu Windows › Mapowania dysków", row.GroupPath);
        Assert.Equal("K:", row.Name);
        Assert.Equal("Aktualizuj: K: → \\\\fs01.contoso.test\\Księgowość", row.ValueText);
        Assert.Contains("Etykieta: Księgowość", row.Elements);
        Assert.Equal("Element {0D1F7F22-6D1B-4A35-9D9C-0E3D9F3F0001} – Ścieżka", alone.Name);
        Assert.Equal("\\\\fs01.contoso.test\\Księgowość", alone.ValueText);
        Assert.Equal("K: – Ścieżka", named.Name);
    }

    [Fact]
    public void Preferences_OtherTypes_AreNamedByGpmcsFolders()
    {
        const string Variables = "<?xml version=\"1.0\" encoding=\"utf-8\"?><EnvironmentVariables clsid=\"{BF141A63-327B-438a-B9BF-2C188F13B7AD}\"><EnvironmentVariable clsid=\"{78570023-8373-4a19-BA80-2F150738EA19}\" name=\"FIRMA\" uid=\"{A1}\" disabled=\"1\" removePolicy=\"1\"><Properties action=\"C\" name=\"FIRMA\" value=\"Zażółć\" user=\"0\"/></EnvironmentVariable></EnvironmentVariables>";
        const string Services = "<?xml version=\"1.0\" encoding=\"utf-8\"?><NTServices clsid=\"{2CFB484A-4E96-4b5d-A0B6-093D2F91E6AE}\"><NTService clsid=\"{AB6F0B67-341F-4e51-92F9-005FBFBA1A43}\" name=\"Spooler\" uid=\"{B2}\"><Properties startupType=\"DISABLED\" serviceName=\"Spooler\" serviceAction=\"STOP\"/></NTService></NTServices>";

        var variable = Assert.Single(Presenter().DescribeRows(Read("Machine/Preferences/EnvironmentVariables/EnvironmentVariables.xml", Variables)));
        var service = Assert.Single(Presenter().DescribeRows(Read("Machine/Preferences/Services/Services.xml", Services)));

        Assert.Equal("Konfiguracja komputera › Preferencje › Ustawienia systemu Windows › Środowisko", variable.GroupPath);
        Assert.Equal("Utwórz: FIRMA = Zażółć", variable.ValueText);
        Assert.Contains("Typ elementu: Zmienna środowiskowa", variable.Elements);
        Assert.Contains("Wyłączony – klienci go pomijają", variable.Elements);
        Assert.Contains("Usuń ten element, gdy przestanie być stosowany", variable.Elements);
        Assert.Null(variable.Focus?.Editor);
        Assert.Equal("Konfiguracja komputera › Preferencje › Ustawienia panelu sterowania › Usługi", service.GroupPath);
        Assert.Equal("Spooler, Wyłączony, Zatrzymaj usługę", service.ValueText);
    }

    [Fact]
    public void Scripts_PhasesCommandAndParameters_ScriptFiles()
    {
        var rows = Presenter().DescribeRows(
        [
            Text("Computer/Scripts/Startup/0", "start.cmd"),
            Text("Computer/Scripts/Startup/0/Parameters", "/cichy"),
            Text("User/Scripts/PowerShell/Logon/1", "logowanie.ps1"),
            Text("Computer/Scripts/PowerShell/Config/StartExecutePSFirst", "true"),
            new GpoSetting { Key = "Computer/Scripts/Files/Startup/start.cmd", Value = "sha256:00ff size:2048", Kind = GpoSettingKind.Hash },
        ]);

        var startup = rows[0];
        Assert.Equal(ComputerPolicies + " › Ustawienia systemu Windows › Skrypty (uruchamianie/zamykanie) › Uruchamianie", startup.GroupPath);
        Assert.Equal("Skrypt 1", startup.Name);
        Assert.Equal("start.cmd /cichy", startup.ValueText);
        Assert.Equal(["Skrypt: start.cmd", "Parametry: /cichy"], startup.Elements);
        Assert.Equal("Konfiguracja użytkownika › Zasady › Ustawienia systemu Windows › Skrypty (logowanie/wylogowanie) › Logowanie", rows[1].GroupPath);
        Assert.Equal("Skrypt programu PowerShell 2", rows[1].Name);
        Assert.Equal("Uruchamianie: najpierw uruchamiaj skrypty programu Windows PowerShell", rows[2].Name);
        Assert.Equal("Tak", rows[2].ValueText);
        Assert.Equal("start.cmd", rows[3].Name.Split('/')[^1]);
        Assert.Equal("2 KB", rows[3].ValueText);
        Assert.Equal(["SHA-256: 00ff"], rows[3].Elements);
    }

    [Fact]
    public void Comments_UnreadableFiles_MaskedAndLongValues()
    {
        var comment = Presenter().Describe(Text("Gpo/Comment", "Uzgodnione z Łukaszem Wiśniewskim"));
        var unreadable = Presenter().Describe(new GpoSetting { Key = "File/Machine/Registry.pol", Kind = GpoSettingKind.Unreadable });
        var masked = Presenter().Describe(new GpoSetting { Key = "Computer/Preferences/Groups/{1}/cpassword", Kind = GpoSettingKind.Masked });
        var hashed = Presenter().Describe(new GpoSetting { Key = "Computer/Security/System Access/NewAdministratorName", Value = "sha256:abcd", Kind = GpoSettingKind.Hash });
        var redacted = Presenter().Describe(@"Computer/Security/Registry Values/HKLM\System\CurrentControlSet\Services\SNMP\Parameters\ValidCommunities!*", ["REG_DWORD:4"], masked: true);

        Assert.Equal("Obiekt zasad grupy", comment.GroupPath);
        Assert.Equal("Komentarz do obiektu GPO", comment.Name);
        Assert.Equal("Uzgodnione z Łukaszem Wiśniewskim", comment.ValueText);
        Assert.Equal("Konfiguracja komputera › Pliki, których nie udało się odczytać", unreadable.GroupPath);
        Assert.Equal("Machine/Registry.pol", unreadable.Name);
        Assert.Equal("(nie można odczytać pliku)", unreadable.ValueText);
        Assert.Null(unreadable.Focus);
        Assert.Equal("(ustawione – ukryte)", masked.ValueText);
        Assert.True(masked.IsMasked);
        Assert.Equal("Element {1} – Hasło", masked.Name);
        Assert.Equal("(długa wartość – zachowano tylko jej skrót)", hashed.ValueText);
        Assert.Equal("(ustawione – ukryte)", redacted.ValueText);
        Assert.DoesNotContain(redacted.Elements, e => e.Contains("REG_DWORD", StringComparison.Ordinal));
        Assert.EndsWith(@"\(nazwy wartości ukryte)", redacted.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncatedKeys_AreShownButNotLinked()
    {
        var head = @"Computer/Registry/HKLM\Software\" + new string('x', GpoValues.MaxKeyLength);
        var key = GpoValues.Key(head + "!Wartość");
        Assert.True(GpoSettingKey.IsTruncated(key));

        var text = Presenter().Describe(Text(key, "REG_DWORD:1"));

        Assert.Null(text.Focus);
        Assert.EndsWith("…", text.Name, StringComparison.Ordinal);
        Assert.Contains("za długi", text.Explanation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Computer")]
    [InlineData("Computer/")]
    [InlineData("User/Coś/Innego")]
    [InlineData("Foo/Bar/Baz")]
    [InlineData("Computer/Security/System Access/MaximumPasswordAge")]
    [InlineData("Computer/Security/Group Membership")]
    [InlineData("Computer/Security/Services/")]
    [InlineData("Computer/Audit/Option/")]
    [InlineData("Computer/Audit/File System/Global")]
    [InlineData("Computer/Registry/HKLM")]
    [InlineData("Computer/Registry/!")]
    [InlineData("Computer/Preferences/Groups")]
    [InlineData("Computer/Preferences/Groups/{")]
    [InlineData("User/Preferences/ScheduledTasks/{X}/Task/Actions/Exec[2]/Command")]
    [InlineData("Computer/Scripts/Startup/x")]
    [InlineData("Computer/Scripts/Files/")]
    [InlineData("User/Comment/")]
    [InlineData("File/")]
    public void UnknownShapes_NeverThrow(string key)
    {
        string[][] values = [[], [""], ["abc"], ["REG_DWORD:x"], ["sha256:"], ["Member action"], ["a", "b"], ["SOFT:"]];
        foreach (var value in values)
        {
            var text = Presenter().Describe(key, value, masked: false);
            Assert.NotNull(text.Name);
            Assert.NotNull(text.ValueText);
            Assert.NotNull(text.GroupPath);
            _ = Presenter().DescribeElement(key, value.FirstOrDefault() ?? string.Empty);
        }

        Assert.Single(Presenter().DescribeRows([Text(key, "x")]));
        _ = GpoSettingPresenter.Sids(key, ["x"]);
    }

    [Fact]
    public void English_UsesGpmcsEnglishTree()
    {
        var age = Presenter(English).Describe(Text("Computer/Security/System Access/MaximumPasswordAge", "42"));

        Assert.Equal("Computer Configuration › Policies › Windows Settings › Security Settings › Account Policies › Password Policy", age.GroupPath);
        Assert.Equal("Maximum password age", age.Name);
        Assert.Equal("42 days", age.ValueText);
        Assert.Equal("1 invalid logon attempt", Presenter(English).Describe(Text("Computer/Security/System Access/LockoutBadCount", "1")).ValueText);
        Assert.Equal("Password never expires", Presenter(English).Describe(Text("Computer/Security/System Access/MaximumPasswordAge", "0")).ValueText);
        Assert.Equal("Administrators, KONTOSO\\Serwisanci", Presenter(English).Describe(List("Computer/Security/Privilege Rights/SeDebugPrivilege", "S-1-5-32-544", Serwisanci)).ValueText);
    }

    [Fact]
    public void Elements_OfChangeRows_AreReadable()
    {
        Assert.Equal("Administratorzy", Presenter().DescribeElement("Computer/Security/Privilege Rights/SeDebugPrivilege", "S-1-5-32-544"));
        Assert.Equal("− KONTOSO\\Serwisanci", Presenter().DescribeElement("Computer/Preferences/Groups/{1}/Properties/Members", $"Member action=REMOVE name=X sid={Serwisanci}"));
        Assert.Equal("+ KONTOSO\\Domain Admins", Presenter().DescribeElement("Computer/Preferences/Groups/{1}/Properties/Members", "Member action=ADD name=KONTOSO\\Domain Admins sid="));
        Assert.Equal("nie komputer WAW-PC001", Presenter().DescribeElement("Computer/Preferences/Groups/{1}/Filters", "FilterComputer bool=AND name=WAW-PC001 not=1 type=NETBIOS"));
    }

    [Fact]
    public void Ordered_FollowsGpmc_ComputerUserGpo_PoliciesBeforePreferences()
    {
        var rows = GpoSettingPresenter.Ordered(Presenter().DescribeRows(
        [
            Text("Gpo/Comment", "x"),
            Text("User/Scripts/Logon/0", "a.cmd"),
            Read("Machine/Preferences/Groups/Groups.xml", FakeDirectoryBuilder.Groups(FakeDirectoryBuilder.LocalGroup("Users (built-in)", "S-1-5-32-545", []))).First(),
            Text("Computer/Security/System Access/MinimumPasswordLength", "12"),
        ]), Polish);

        Assert.Equal(["Konfiguracja komputera", "Konfiguracja komputera", "Konfiguracja użytkownika", "Obiekt zasad grupy"], rows.Select(r => r.Configuration));
        Assert.Equal(GpoSettingBranch.Policies, rows[0].Branch);
        Assert.Equal(GpoSettingBranch.Preferences, rows[1].Branch);
    }

    [Fact]
    public void DynamicKeys_ExistInBothLanguages()
    {
        var resources = new ResourceManager(ResxLocalizer.ResourceBaseName, typeof(ResxLocalizer).Assembly);
        var en = resources.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true)!;
        var pl = resources.GetResourceSet(CultureInfo.GetCultureInfo("pl"), createIfNotExists: true, tryParents: false)!;
        var keys = L.GpoSettings.DynamicKeys().ToList();

        Assert.True(keys.Count > 100);
        Assert.Equal([], keys.Where(k => en.GetString(k) is null));
        Assert.Equal([], keys.Where(k => pl.GetString(k) is null));
    }

    [Fact]
    public void ToString_CarriesNoValues()
    {
        var text = Presenter().Describe(List("Computer/Security/Privilege Rights/SeDebugPrivilege", Serwisanci));

        Assert.Equal("Computer/UserRight", text.ToString());
        Assert.DoesNotContain("Serwisanci", text.ToString(), StringComparison.Ordinal);
    }

    private static IReadOnlyList<GpoSetting> Read(string path, string xml) =>
        GpoFileDiff.ReadSettings(GpoFileClassifier.Classify(path), Encoding.UTF8.GetBytes(xml)) ?? throw new InvalidOperationException("not parsed");
}
