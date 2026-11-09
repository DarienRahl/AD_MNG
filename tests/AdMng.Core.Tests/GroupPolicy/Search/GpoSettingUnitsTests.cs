using System.Text;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy.Search;

/// <summary>The setting units of 21.2: every kind, identity, occurrences, collections, values, refusals and principals.</summary>
public sealed class GpoSettingUnitsTests
{
    private const string DomainSid = "S-1-5-21-1000-2000-3000";
    private const string ItSid = DomainSid + "-1105";
    private const string AdminsSid = DomainSid + "-1200";
    private static readonly GpoUnitContext Plain = new(null, DomainSid);
    private static readonly Lazy<PolicyDefinitionSet> Definitions = new(SampleAdministrativeTemplates.LoadPolish);

    // ---- Security template ---------------------------------------------------------------------------------------------

    [Fact]
    public void SecurityValues_OneUnitPerKey_ComparedByValue()
    {
        var a = Single(Units(Template("[System Access]", "MinimumPasswordLength = 12")));
        var b = Single(Units(Template("[System Access]", "MinimumPasswordLength = 12")));
        var c = Single(Units(Template("[System Access]", "MinimumPasswordLength = 8")));

        Assert.Equal(("K|Computer/Security/System Access/MinimumPasswordLength", GpoSettingUnitKind.SecurityValue), (a.BaseId, a.Kind));
        Assert.Equal((a.BaseId, 1, GpoScope.Computer, GpoSettingArea.SystemAccess), (a.Id, a.Occurrence, a.Scope, a.Area));
        Assert.Equal((GpoUnitComparability.Comparable, (GpoUnitRefusal?)null), (a.Comparability, a.Refusal));
        Assert.True(a.CountsAsSetting);
        Assert.False(a.BlocksMerge);
        Assert.True(GpoSettingUnits.SameValue(a, b));
        Assert.False(GpoSettingUnits.SameValue(a, c));
        Assert.Equal("SecurityValue Computer", a.ToString());
    }

    [Fact]
    public void UserRight_ByNameAndBySid_CompareEqual_WithTheResolvedNames()
    {
        var byName = Template("[Privilege Rights]", @"SeRemoteInteractiveLogonRight = CONTOSO\GG_IT,*S-1-5-32-544");
        var bySid = Template("[Privilege Rights]", $"SeRemoteInteractiveLogonRight = *{ItSid},*S-1-5-32-544");
        var context = Plain with { SidsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [@"contoso\gg_it"] = ItSid } };

        var a = Single(Units(byName, context));
        var b = Single(Units(bySid, context));

        Assert.Equal((GpoSettingUnitKind.UserRight, "K|Computer/Security/Privilege Rights/SeRemoteInteractiveLogonRight"), (a.Kind, a.BaseId));
        Assert.True(GpoSettingUnits.SameValue(a, b));
        Assert.Equal([ItSid, "S-1-5-32-544"], a.PrincipalSids.Order(StringComparer.Ordinal));
        Assert.Equal(a.PrincipalSids.Order(), b.PrincipalSids.Order());
        Assert.Contains(@"CONTOSO\GG_IT", a.Principals);
        Assert.False(GpoSettingUnits.SameValue(Single(Units(byName)), Single(Units(bySid)))); // without the map the name stays a name
    }

    [Fact]
    public void OtherDomainsWellKnownName_IsNotThisDomainsRid_WhenTheResolverAnswered()
    {
        // KnownSid maps any DOMAIN\Domain Admins to this domain's -512; only the resolver knows which prefix is this domain.
        var settings = SettingsOf(Template("[Privilege Rights]", @"SeBackupPrivilege = KONTOSO\Domain Admins", @"SeRestorePrivilege = CONTOSO\Domain Admins"));
        Assert.Equal([@"CONTOSO\Domain Admins", @"KONTOSO\Domain Admins"], GpoSettingUnits.PrincipalNames(settings, DomainSid).Order(StringComparer.Ordinal));
        var context = Plain with { SidsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [@"CONTOSO\Domain Admins"] = DomainSid + "-512" } };
        var bySid = Template("[Privilege Rights]", $"SeBackupPrivilege = *{DomainSid}-512");

        var units = GpoSettingUnits.Of(settings, null, null, context);

        var other = units.Single(u => u.BaseId.EndsWith("SeBackupPrivilege", StringComparison.Ordinal));
        var own = units.Single(u => u.BaseId.EndsWith("SeRestorePrivilege", StringComparison.Ordinal));
        Assert.Empty(other.PrincipalSids);
        Assert.Equal([DomainSid + "-512"], own.PrincipalSids);
        Assert.False(GpoSettingUnits.SameValue(other, Single(Units(bySid, context))));
        Assert.Equal([DomainSid + "-512"], Single(Units(Template("[Privilege Rights]", @"SeBackupPrivilege = KONTOSO\Domain Admins"))).PrincipalSids); // no map: well-known
    }

    [Fact]
    public void RestrictedGroup_ByNameAndBySid_OneBaseId()
    {
        var context = Plain with { SidsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [@"CONTOSO\GG_Admins"] = AdminsSid } };
        var byName = Units(Template("[Group Membership]", @"CONTOSO\GG_Admins__Members = CONTOSO\jan.kowalski", @"CONTOSO\GG_Admins__Memberof = *S-1-5-32-544"), context);
        var bySid = Units(Template("[Group Membership]", $"*{AdminsSid}__Members = CONTOSO\\jan.kowalski"), context);

        Assert.Equal([$"G|{AdminsSid}|MemberOf", $"G|{AdminsSid}|Members"], byName.Select(u => u.BaseId).Order(StringComparer.Ordinal));
        Assert.Equal($"G|{AdminsSid}|Members", Single(bySid).BaseId);
        Assert.All(byName, u => Assert.Equal(GpoSettingUnitKind.RestrictedGroup, u.Kind));
        Assert.True(GpoSettingUnits.SameValue(byName.Single(u => u.BaseId.EndsWith("|Members", StringComparison.Ordinal)), Single(bySid)));
        Assert.Contains(AdminsSid, Single(bySid).PrincipalSids);
        Assert.Equal(@"G|CONTOSO\GG_ADMINS|Members", Units(Template("[Group Membership]", @"CONTOSO\GG_Admins__Members = x"))[0].BaseId);
    }

    [Fact]
    public void ServiceObjectSecurityAndSecurityOptions()
    {
        var units = Units(Template(
            "[Service General Setting]", "\"Spooler\",4,\"\"",
            "[File Security]", "\"%SystemRoot%\\System32\\narzędzie.exe\",0,\"D:PAR(A;;FA;;;BA)\"",
            "[Registry Keys]", "\"MACHINE\\SOFTWARE\\Contoso\",2,\"D:PAR(A;;KA;;;BA)\"",
            "[Registry Values]", "MACHINE\\System\\CurrentControlSet\\Control\\Lsa\\LimitBlankPasswordUse=4,1"));

        var service = units.Single(u => u.Kind == GpoSettingUnitKind.Service);
        Assert.Equal(("O|Computer/Security/Services/Spooler", 2, (GpoUnitRefusal?)null), (service.BaseId, service.Settings.Count, service.Refusal));
        var objects = units.Where(u => u.Kind == GpoSettingUnitKind.ObjectSecurity).ToList();
        Assert.Equal(["O|Computer/Security/File Security/%SystemRoot%\\System32\\narzędzie.exe", "O|Computer/Security/Registry Keys/HKLM\\SOFTWARE\\Contoso"],
            objects.Select(u => u.BaseId).Order(StringComparer.Ordinal));
        Assert.All(objects, o => Assert.Equal((2, GpoUnitRefusal.ObjectSecurity), (o.Settings.Count, o.Refusal!.Value)));
        Assert.All(objects, o => Assert.True(o.BlocksMerge));
        var option = units.Single(u => u.Kind == GpoSettingUnitKind.SecurityRegistryValue);
        Assert.Equal(@"K|Computer/Security/Registry Values/HKLM\System\CurrentControlSet\Control\Lsa!LimitBlankPasswordUse", option.BaseId);
    }

    [Fact]
    public void AuditSubcategories_ByCatalogGuid_NameOnlyRowsToo_OptionsRefused()
    {
        var units = Units(Audit(",System,Audit Logon,{0cce9215-69ae-11d9-bed3-505054503030},Success and Failure,,3", ",,Option:CrashOnAuditFail,,Enabled,,1"));
        var nameOnly = Single(Units(Audit(",System,Audit Logon,,Success and Failure,,3")));

        var logon = units.Single(u => u.Kind == GpoSettingUnitKind.AuditSubcategory);
        Assert.Equal("A|{0cce9215-69ae-11d9-bed3-505054503030}", logon.BaseId);
        Assert.Equal(logon.BaseId, nameOnly.BaseId);
        var option = units.Single(u => u.Kind == GpoSettingUnitKind.AuditOption);
        Assert.Equal((GpoUnitRefusal.AuditOption, true), (option.Refusal!.Value, option.BlocksMerge));
    }

    // ---- Registry.pol ----------------------------------------------------------------------------------------------------

    [Fact]
    public void RegistryPol_WithoutDefinitions_ValuesAndKeyActions()
    {
        var document = new RegistryPolicyDocument();
        document.Set(@"Software\Policies\Contoso\Eksplorator", "NoPreview", RegistryFormat.RegDword, BitConverter.GetBytes(1u));
        document.ClearValues(@"Software\Contoso\Lista");

        var units = Units([("Machine/Registry.pol", document.ToBytes())]);

        Assert.Equal([GpoSettingUnitKind.RegistryValue, GpoSettingUnitKind.RegistryKey], units.Select(u => u.Kind).Order());
        Assert.Equal(@"K|Computer/Registry/HKLM\Software\Policies\Contoso\Eksplorator!NoPreview", units.Single(u => u.Kind == GpoSettingUnitKind.RegistryValue).BaseId);
    }

    [Fact]
    public void RegistryPol_WithDefinitions_APolicyIsOneUnit_ComparedBySetting()
    {
        var a = new RegistryPolicyDocument();
        a.Set(@"Software\Policies\Contoso\Eksplorator", "NoPreview", RegistryFormat.RegDword, BitConverter.GetBytes(1u));
        a.Set(@"Software\Contoso\Inne", "Wartość", RegistryFormat.RegSz, Encoding.Unicode.GetBytes("ż\0"));
        var b = new RegistryPolicyDocument();
        b.Set(@"Software\Policies\Contoso\Eksplorator", "NoPreview", RegistryFormat.RegDword, BitConverter.GetBytes(1u));
        var context = new GpoUnitContext(Definitions.Value, DomainSid);

        var unitsA = GpoSettingUnits.Of(SettingsOf(("Machine/Registry.pol", a.ToBytes())), a, null, context);
        var unitsB = GpoSettingUnits.Of(SettingsOf(("Machine/Registry.pol", b.ToBytes())), b, null, context);

        var policy = unitsA.Single(u => u.Kind == GpoSettingUnitKind.AdministrativeTemplate);
        Assert.Equal(("P|Machine|" + SampleAdministrativeTemplates.HidePreview, SampleAdministrativeTemplates.HidePreview, PolicyClass.Machine),
            (policy.BaseId, policy.PolicyId, policy.PolicyClass!.Value));
        Assert.Equal(PolicyState.Enabled, policy.Policy!.State);
        Assert.Equal(GpoSettingUnitKind.RegistryValue, unitsA.Single(u => u != policy).Kind);
        Assert.True(GpoSettingUnits.SameValue(policy, Single(unitsB)));
        Assert.Equal(policy.ValueFingerprint, Single(unitsB).ValueFingerprint);

        // Without a set the same value is a raw registry value.
        Assert.Equal(GpoSettingUnitKind.RegistryValue, Single(GpoSettingUnits.Of(SettingsOf(("Machine/Registry.pol", b.ToBytes())), b, null, Plain)).Kind);
    }

    [Fact]
    public void RegistryPol_Unreadable_IsAnUnreadableFileUnit_ThatBlocksMerge()
    {
        var unit = Single(Units([("Machine/Registry.pol", Encoding.ASCII.GetBytes("PReg?"))]));

        Assert.Equal((GpoSettingUnitKind.UnreadableFile, GpoUnitComparability.Unreadable, GpoUnitRefusal.Unreadable), (unit.Kind, unit.Comparability, unit.Refusal!.Value));
        Assert.False(unit.CountsAsSetting);
        Assert.True(unit.BlocksMerge);
    }

    [Fact]
    public void TruncatedKey_IsTruncated_AndRefused()
    {
        var document = new RegistryPolicyDocument();
        document.Set(@"Software\Contoso\" + new string('ł', 600), "Wartość", RegistryFormat.RegDword, BitConverter.GetBytes(1u));

        var unit = Single(Units([("Machine/Registry.pol", document.ToBytes())]));

        Assert.True(GpoSettingKey.IsTruncated(unit.Settings[0].Key));
        Assert.Equal((GpoUnitComparability.Truncated, GpoUnitRefusal.Truncated), (unit.Comparability, unit.Refusal!.Value));
        Assert.Equal(unit.BaseId, GpoSettingUnits.BaseIdOf(unit.Id));
    }

    // ---- Preferences -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Drives_SameLetterInTwoGpos_DifferentUids_OneBaseId_SameValue()
    {
        var a = Single(Units(Drives(Drive("{AAAAAAAA-0000-0000-0000-000000000001}", "K", @"\\fs01\Księgowość", "2026-10-01 08:00:00"))));
        var b = Single(Units(Drives(Drive("{BBBBBBBB-0000-0000-0000-000000000002}", "K", @"\\fs01\Księgowość", "2026-11-02 09:30:00"))));
        var other = Single(Units(Drives(Drive("{CCCCCCCC-0000-0000-0000-000000000003}", "K", @"\\fs01\Kadry", null))));

        Assert.Equal(("I|User|Drives|Drive|K", GpoSettingUnitKind.Preference, "Drives"), (a.BaseId, a.Kind, a.PreferenceType));
        Assert.Equal(a.BaseId, b.BaseId);
        Assert.Equal("{AAAAAAAA-0000-0000-0000-000000000001}", a.ItemId);
        Assert.True(GpoSettingUnits.SameValue(a, b));
        Assert.False(GpoSettingUnits.SameValue(a, other));
        Assert.Equal((GpoUnitComparability.Comparable, (GpoUnitRefusal?)null, false), (a.Comparability, a.Refusal, a.Disabled));
    }

    [Fact]
    public void Drives_TwoItemsMappingK_Occurrences()
    {
        var units = Units(Drives(Drive("{AAAAAAAA-0000-0000-0000-000000000001}", "K", @"\\fs01\A", null), Drive("{AAAAAAAA-0000-0000-0000-000000000002}", "K", @"\\fs01\B", null)));

        Assert.Equal(["I|User|Drives|Drive|K", "I|User|Drives|Drive|K#2"], units.Select(u => u.Id));
        Assert.Equal([1, 2], units.Select(u => u.Occurrence));
        Assert.All(units, u => Assert.Equal("I|User|Drives|Drive|K", u.BaseId));
        Assert.Equal("I|User|Drives|Drive|K", GpoSettingUnits.BaseIdOf(units[1].Id));
    }

    [Fact]
    public void Collections_TargetedDisabledAndPlain()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Drives clsid="{8FDDCC1A-0C3C-43cd-A6B4-71A6DF20DA8C}">
              <Collection clsid="{53B533F5-224C-47e3-B01B-CA3B3F3FF4BF}" name="Zwykła" uid="{C0000000-0000-0000-0000-000000000001}">
                <Drive clsid="{935D1B74-9CB8-4e3c-9914-7DD559B7A417}" name="P:" uid="{D0000000-0000-0000-0000-000000000001}"><Properties action="U" path="\\fs01\Płock" letter="P"/></Drive>
              </Collection>
              <Collection clsid="{53B533F5-224C-47e3-B01B-CA3B3F3FF4BF}" name="Kierowana" uid="{C0000000-0000-0000-0000-000000000002}">
                <Filters><FilterGroup bool="AND" not="0" name="CONTOSO\GG_IT" sid="S-1-5-21-1000-2000-3000-1105" userContext="1" primaryGroup="0" localGroup="0"/></Filters>
                <Drive clsid="{935D1B74-9CB8-4e3c-9914-7DD559B7A417}" name="T:" uid="{D0000000-0000-0000-0000-000000000002}"><Properties action="U" path="\\fs01\Toruń" letter="T"/></Drive>
              </Collection>
              <Collection clsid="{53B533F5-224C-47e3-B01B-CA3B3F3FF4BF}" name="Wyłączona" uid="{C0000000-0000-0000-0000-000000000003}" disabled="1">
                <Drive clsid="{935D1B74-9CB8-4e3c-9914-7DD559B7A417}" name="W:" uid="{D0000000-0000-0000-0000-000000000003}"><Properties action="U" path="\\fs01\Wrocław" letter="W"/></Drive>
              </Collection>
              <Drive clsid="{935D1B74-9CB8-4e3c-9914-7DD559B7A417}" name="Z:" uid="{D0000000-0000-0000-0000-000000000004}" disabled="1"><Properties action="U" path="\\fs01\Zamość" letter="Z"/></Drive>
            </Drives>
            """;

        var units = Units([("User/Preferences/Drives/Drives.xml", Encoding.UTF8.GetBytes(xml))]);

        Assert.Equal(["I|User|Drives|Drive|P", "I|User|Drives|Drive|T", "I|User|Drives|Drive|W", "I|User|Drives|Drive|Z"], units.Select(u => u.BaseId)); // no collection units
        var (plain, targeted, inDisabled, disabled) = (units[0], units[1], units[2], units[3]);
        Assert.Equal((GpoUnitComparability.Comparable, false, (GpoUnitRefusal?)null), (plain.Comparability, plain.Disabled, plain.Refusal));
        Assert.Equal((GpoUnitComparability.Conditional, false, GpoUnitRefusal.ScopedCollection), (targeted.Comparability, targeted.Disabled, targeted.Refusal!.Value));
        Assert.Contains("collection:", targeted.FiltersFingerprint, StringComparison.Ordinal);
        Assert.Contains(ItSid, targeted.PrincipalSids);
        Assert.Equal((true, GpoUnitRefusal.ScopedCollection), (inDisabled.Disabled, inDisabled.Refusal!.Value));
        Assert.Equal((true, (GpoUnitRefusal?)null), (disabled.Disabled, disabled.Refusal));
        Assert.Equal(string.Empty, plain.FiltersFingerprint);
        Assert.False(GpoSettingUnits.SameValue(disabled, disabled)); // a disabled item is never "the value"
        Assert.True(GpoSettingUnits.SameValue(plain, plain));
        Assert.True(disabled.CountsAsSetting);

        // Without the XML (settings only) the collection chain is unknown: the items are taken as at the root.
        var flat = GpoSettingUnits.Of(SettingsOf(("User/Preferences/Drives/Drives.xml", Encoding.UTF8.GetBytes(xml))), null, null, Plain);
        Assert.Null(flat.Single(u => u.BaseId.EndsWith("|T", StringComparison.Ordinal)).Refusal);
    }

    [Fact]
    public void ItemLevelTargeting_IsConditional_WithItsFilters()
    {
        var groups = FakeDirectoryBuilder.Groups(FakeDirectoryBuilder.LocalGroup("Administratorzy (wbudowane)", "S-1-5-32-544", [(@"CONTOSO\GG_IT", ItSid, "ADD")], targeted: true));

        var unit = Single(Units([("Machine/Preferences/Groups/Groups.xml", Encoding.UTF8.GetBytes(groups))]));

        Assert.Equal(("I|Computer|Groups|Group|S-1-5-32-544", GpoUnitComparability.Conditional, (GpoUnitRefusal?)null), (unit.BaseId, unit.Comparability, unit.Refusal));
        Assert.StartsWith("item:FilterComputer", unit.FiltersFingerprint, StringComparison.Ordinal);
        Assert.Equal([ItSid, "S-1-5-32-544"], unit.PrincipalSids.Order(StringComparer.Ordinal));
        Assert.False(GpoSettingUnits.SameValue(unit, unit));
    }

    [Fact]
    public void Groups_ByNameWithoutSid_ResolvedMembers_AndPrincipalNames()
    {
        var groups = FakeDirectoryBuilder.Groups(FakeDirectoryBuilder.LocalGroup("Operatorzy kopii", null, [(@"CONTOSO\łukasz.wiśniewski", null, "ADD"), (@"CONTOSO\GG_IT", ItSid, "ADD")]));
        var settings = SettingsOf(("Machine/Preferences/Groups/Groups.xml", Encoding.UTF8.GetBytes(groups)));

        var names = GpoSettingUnits.PrincipalNames(settings, DomainSid);
        var unit = Single(GpoSettingUnits.Of(settings, null, null, Plain with
        {
            SidsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [@"CONTOSO\łukasz.wiśniewski"] = DomainSid + "-1300" },
        }));

        Assert.Equal([@"CONTOSO\łukasz.wiśniewski", "Operatorzy kopii"], names.Order(StringComparer.Ordinal));
        Assert.Equal("I|Computer|Groups|Group|Operatorzy kopii", unit.BaseId);
        Assert.Equal([ItSid, DomainSid + "-1300"], unit.PrincipalSids.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void CpasswordItem_IsMasked_NeverEqual_RefusedAsSecret()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Groups clsid="{3125E937-EB16-4b4c-9934-544FC6D24D26}">
              <User clsid="{DF5F1855-51E5-4d24-8B1A-D9BDE98BA1D1}" name="serwis" uid="{E0000000-0000-0000-0000-000000000001}" changed="2026-10-01 08:00:00"><Properties action="U" newName="" fullName="Konto serwisowe" cpassword="j1Uyj3Vx8TY9LtLZil2uAuZkFQA" userName="serwis"/></User>
            </Groups>
            """;

        var unit = Single(Units([("Machine/Preferences/Groups/Groups.xml", Encoding.UTF8.GetBytes(xml))]));

        Assert.Equal(("I|Computer|Groups|User|serwis", GpoUnitComparability.Masked, GpoUnitRefusal.Secret), (unit.BaseId, unit.Comparability, unit.Refusal!.Value));
        Assert.False(GpoSettingUnits.SameValue(unit, unit));
        Assert.True(unit.BlocksMerge);
        Assert.DoesNotContain("j1Uyj3", unit.ValueFingerprint, StringComparison.Ordinal);
    }

    [Fact]
    public void PreferenceIdentities_PerType_AndAmbiguousFallback()
    {
        Assert.Equal(@"I|Computer|Registry|Registry|HKEY_LOCAL_MACHINE|SOFTWARE\Contoso|Tryb", Single(Units(Pref("Registry", "Registry", "<Properties action=\"U\" hive=\"HKEY_LOCAL_MACHINE\" key=\"SOFTWARE\\Contoso\" name=\"Tryb\" type=\"REG_SZ\" value=\"ż\"/>"))).BaseId);
        Assert.Equal("I|Computer|Services|NTService|Spooler", Single(Units(Pref("Services", "NTService", "<Properties startupType=\"DISABLED\" serviceName=\"Spooler\"/>"))).BaseId);
        Assert.Equal(@"I|Computer|Files|File|C:\Dane\plik.txt", Single(Units(Pref("Files", "File", "<Properties action=\"C\" fromPath=\"\\\\fs01\\a.txt\" targetPath=\"C:\\Dane\\plik.txt\"/>"))).BaseId);
        Assert.Equal("I|Computer|EnvironmentVariables|EnvironmentVariable|ŚCIEŻKA|0", Single(Units(Pref("EnvironmentVariables", "EnvironmentVariable", "<Properties action=\"U\" name=\"ŚCIEŻKA\" value=\"x\" user=\"0\"/>"))).BaseId);
        Assert.Equal("I|Computer|ScheduledTasks|ImmediateTaskV2|Porządki", Single(Units(Pref("ScheduledTasks", "ImmediateTaskV2", "<Properties action=\"C\" name=\"Porządki\"/>"))).BaseId);
        Assert.Equal(@"I|Computer|IniFiles|Ini|C:\a.ini|Sekcja|Klucz", Single(Units(Pref("IniFiles", "Ini", "<Properties path=\"C:\\a.ini\" section=\"Sekcja\" property=\"Klucz\" value=\"1\"/>"))).BaseId);
        Assert.Equal("I|Computer|Printers|SharedPrinter|\\\\druk\\Biuro", Single(Units(Pref("Printers", "SharedPrinter", "<Properties action=\"U\" path=\"\\\\druk\\Biuro\"/>"))).BaseId);
        Assert.Equal("I|Computer|NetworkShares|NetShare|Dane", Single(Units(Pref("NetworkShares", "NetShare", "<Properties action=\"U\" name=\"Dane\" path=\"D:\\Dane\"/>"))).BaseId);

        // A type without an identity: its @name; none at all: the item id, ambiguous and refused.
        Assert.Equal("I|Computer|DataSources|DataSource|Księgi", Single(Units(Pref("DataSources", "DataSource", "<Properties dsn=\"x\"/>", name: "Księgi"))).BaseId);
        var ambiguous = Single(Units(Pref("DataSources", "DataSource", "<Properties dsn=\"x\"/>", name: null)));
        Assert.Equal(("I|Computer|DataSources|DataSource|({F0000000-0000-0000-0000-000000000001})", GpoUnitComparability.Ambiguous, GpoUnitRefusal.Unsupported),
            (ambiguous.BaseId, ambiguous.Comparability, ambiguous.Refusal!.Value));
    }

    [Fact]
    public void RemovePolicy_IsKept()
    {
        var unit = Single(Units(Pref("Registry", "Registry", "<Properties action=\"U\" hive=\"HKEY_CURRENT_USER\" key=\"Software\\A\" name=\"B\" value=\"1\"/>", extra: " removePolicy=\"1\"")));

        Assert.True(unit.RemovePolicy);
    }

    // ---- Scripts, comments, files, other ---------------------------------------------------------------------------------

    [Fact]
    public void Scripts_CommandAndParameters_OneUnit_Cumulative_Refused()
    {
        var unit = Single(Units([("Machine/Scripts/scripts.ini", FakeSysvolFileReader.Utf16("[Startup]\r\n0CmdLine=porządki.cmd\r\n0Parameters=-cicho\r\n"))]));

        Assert.Equal(("S|Computer|cmd|Startup|porządki.cmd|-cicho", GpoSettingUnitKind.Script, 2), (unit.BaseId, unit.Kind, unit.Settings.Count));
        Assert.Equal((GpoUnitComparability.Cumulative, GpoUnitRefusal.Script, true), (unit.Comparability, unit.Refusal!.Value, unit.BlocksMerge));
    }

    [Fact]
    public void CommentsAndScriptFiles_DoNotCount_AndNeverBlockAMerge()
    {
        var units = Units(
        [
            ("GPO.cmt", Encoding.UTF8.GetBytes("Uzgodnione z Łukaszem")),
            ("Machine/Scripts/Startup/porządki.cmd", Encoding.UTF8.GetBytes("echo Zażółć")),
            ("Machine/Scripts/psscripts.ini", FakeSysvolFileReader.Utf16("[ScriptsConfig]\r\nStartExecutePSFirst=true\r\n")),
        ]);

        var comment = units.Single(u => u.Kind == GpoSettingUnitKind.Comment);
        var file = units.Single(u => u.Kind == GpoSettingUnitKind.ScriptFile);
        var config = units.Single(u => u.Kind == GpoSettingUnitKind.ScriptConfiguration);
        Assert.Equal((false, false, GpoUnitRefusal.Comment), (comment.CountsAsSetting, comment.BlocksMerge, comment.Refusal!.Value));
        Assert.Equal((false, false, GpoUnitRefusal.Script), (file.CountsAsSetting, file.BlocksMerge, file.Refusal!.Value));
        Assert.Equal((true, true, GpoUnitRefusal.Script), (config.CountsAsSetting, config.BlocksMerge, config.Refusal!.Value));
        Assert.Equal(GpoScope.Gpo, comment.Scope);
    }

    [Fact]
    public void OtherKey_IsUnsupported()
    {
        var unit = Single(GpoSettingUnits.Of([new GpoSetting { Key = "Nieznane/Klucz", Value = "ł" }], null, null, Plain));

        Assert.Equal((GpoSettingUnitKind.Other, GpoUnitRefusal.Unsupported, "K|Nieznane/Klucz"), (unit.Kind, unit.Refusal!.Value, unit.BaseId));
    }

    [Fact]
    public void BaseIdThatLooksLikeAnOccurrence_GetsATerminator_RoundTrip()
    {
        var document = new RegistryPolicyDocument();
        document.Set(@"Software\Contoso", "Wersja#2", RegistryFormat.RegDword, BitConverter.GetBytes(1u));

        var unit = Single(Units([("Machine/Registry.pol", document.ToBytes())]));

        Assert.Equal(@"K|Computer/Registry/HKLM\Software\Contoso!Wersja#2|", unit.BaseId);
        Assert.Equal(unit.BaseId, unit.Id);
        Assert.Equal(unit.BaseId, GpoSettingUnits.BaseIdOf(unit.Id));
        Assert.Equal("K|a#1", GpoSettingUnits.BaseIdOf("K|a#1"));
        Assert.Equal("K|a#02", GpoSettingUnits.BaseIdOf("K|a#02"));
        Assert.Equal("K|a", GpoSettingUnits.BaseIdOf("K|a#12"));
    }

    [Fact]
    public void SameValue_NeedsTheSameBaseId()
    {
        var a = Single(Units(Template("[System Access]", "MinimumPasswordLength = 12")));
        var b = Single(Units(Template("[System Access]", "MaximumPasswordAge = 12")));

        Assert.False(GpoSettingUnits.SameValue(a, b));
    }

    [Theory]
    [InlineData("Computer/Preferences/Groups/{0D1F7F22-6D1B-4A35-9D9C-0E3D9F3F0001}@name")]
    [InlineData("Computer/Preferences/Groups/{0D1F7F22-6D1B-4A35-9D9C-0E3D9F3F0001}/Members")]
    [InlineData("User/Preferences/Drives/Drive#2/path")]
    [InlineData("User/Preferences/Drives/Drive#2@element")]
    [InlineData("User/Preferences/Drives")]
    [InlineData("Computer/Preferences/ScheduledTasks/{X}/Task/Settings/Enabled")]
    [InlineData("Computer/Security/System Access/MinimumPasswordLength")]
    [InlineData("Computer/Registry/HKLM\\Software!x")]
    [InlineData("File/Machine/Registry.pol")]
    [InlineData("")]
    [InlineData("a/preferences/b/c@d/e")]
    public void ItemKeyOf_EqualsTheOldPrivateCopies(string key)
    {
        Assert.Equal(OldPreferenceItem(key), GpoSettingUnits.ItemKeyOf(key));
    }

    [Fact]
    public void Context_AndScope_ToString_HaveNoValues()
    {
        var context = Plain with { SidsByName = new Dictionary<string, string> { [@"CONTOSO\Żaneta"] = ItSid } };
        var scope = new GpoPreferenceScope(true, false, ["FilterGroup name=CONTOSO\\Żaneta"]);

        Assert.DoesNotContain("Żaneta", context.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Żaneta", scope.ToString(), StringComparison.Ordinal);
        Assert.True(scope.Scoped);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------------

    /// <summary>The private <c>PreferenceItem(key)</c> of the K5 modeller and the K6 conflict rules before K7.</summary>
    private static string? OldPreferenceItem(string key)
    {
        var parts = key.Split('/', 5);
        if (parts.Length < 4 || !parts[1].Equals("Preferences", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var item = parts[3];
        var at = item.IndexOf('@', StringComparison.Ordinal);
        return string.Join('/', parts[0], parts[1], parts[2], at < 0 ? item : item[..at]);
    }

    private static GpoSettingUnit Single(IReadOnlyList<GpoSettingUnit> units) => Assert.Single(units);

    private static IReadOnlyList<GpoSettingUnit> Units(IEnumerable<(string Path, byte[] Bytes)> files, GpoUnitContext? context = null) =>
        GpoSettingUnits.Of(Content(files), context ?? Plain);

    private static IReadOnlyList<GpoSetting> SettingsOf(params (string Path, byte[] Bytes)[] files) => GroupPolicyReader.ReadSettings(Content(files)).Settings;

    private static GpoFolderContent Content(IEnumerable<(string Path, byte[] Bytes)> files) =>
        new([], files.ToDictionary(f => f.Path, f => f.Bytes, StringComparer.OrdinalIgnoreCase));

    private static (string, byte[])[] Template(params string[] lines) =>
        [(SecurityTemplate.RelativePath, FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template(lines)))];

    private static (string, byte[])[] Audit(params string[] rows) =>
        [("Machine/Microsoft/Windows NT/Audit/audit.csv", Encoding.UTF8.GetBytes(string.Join("\r\n",
            ["Machine Name,Policy Target,Subcategory,Subcategory GUID,Inclusion Setting,Exclusion Setting,Setting Value", .. rows]) + "\r\n"))];

    private static (string, byte[])[] Drives(params string[] items) =>
        [("User/Preferences/Drives/Drives.xml", Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?><Drives clsid=\"{8FDDCC1A-0C3C-43cd-A6B4-71A6DF20DA8C}\">" + string.Concat(items) + "</Drives>"))];

    private static string Drive(string uid, string letter, string path, string? changed) =>
        $"<Drive clsid=\"{{935D1B74-9CB8-4e3c-9914-7DD559B7A417}}\" name=\"{letter}:\" uid=\"{uid}\"" + (changed is null ? string.Empty : $" changed=\"{changed}\"")
        + $"><Properties action=\"U\" path=\"{System.Security.SecurityElement.Escape(path)}\" label=\"\" letter=\"{letter}\"/></Drive>";

    private static (string, byte[])[] Pref(string type, string element, string properties, string? name = "Element", string extra = "") =>
        [($"Machine/Preferences/{type}/{type}.xml", Encoding.UTF8.GetBytes(
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?><{type} clsid=\"{{00000000-0000-0000-0000-000000000000}}\"><{element} clsid=\"{{00000000-0000-0000-0000-000000000001}}\""
            + (name is null ? string.Empty : $" name=\"{name}\"") + $" uid=\"{{F0000000-0000-0000-0000-000000000001}}\"{extra}>{properties}</{element}></{type}>"))];
}
