using System.Globalization;
using System.Resources;
using System.Text;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Presentation.GroupPolicy.Search;
using AdMng.Presentation.Localization;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.GroupPolicy.Search;

/// <summary>The GPMC texts of an index view (21.3): one row per unit of every kind, principal names, the K7 index texts.</summary>
public sealed class GpoIndexTextsTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly ResourceManager Resources = new(ResxLocalizer.ResourceBaseName, typeof(ResxLocalizer).Assembly);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryUnitKind_IsOneRow()
    {
        var harness = await GroupPolicyHarness.CreateAsync(Polish);
        harness.AddCentralStore();
        var id = harness.B2.Builder.AddManagedGroupPolicy(harness.Sysvol, "Wszystkie rodzaje – Kraków", 1, 1);
        PutEveryKind(harness.Sysvol, id);
        using var service = new GpoSettingsIndexService(harness.Services);

        var view = await service.GetAsync(GpoIndexRefresh.IfMissing, Ct);
        var texts = GpoIndexTexts.For(harness.B2.Localizer, view);
        var entry = view.Index.Find(id)!;

        Assert.Same(texts, GpoIndexTexts.For(harness.B2.Localizer, view));
        var kinds = entry.Units.Select(u => u.Kind).ToHashSet();
        Assert.Superset(new HashSet<GpoSettingUnitKind>
        {
            GpoSettingUnitKind.AdministrativeTemplate, GpoSettingUnitKind.RegistryValue, GpoSettingUnitKind.RegistryKey, GpoSettingUnitKind.SecurityValue,
            GpoSettingUnitKind.UserRight, GpoSettingUnitKind.RestrictedGroup, GpoSettingUnitKind.SecurityRegistryValue, GpoSettingUnitKind.Service,
            GpoSettingUnitKind.ObjectSecurity, GpoSettingUnitKind.AuditSubcategory, GpoSettingUnitKind.AuditOption, GpoSettingUnitKind.Preference,
            GpoSettingUnitKind.Script, GpoSettingUnitKind.ScriptConfiguration, GpoSettingUnitKind.Comment, GpoSettingUnitKind.UnreadableFile,
        }, kinds);
        foreach (var unit in view.Index.Entries.SelectMany(e => e.Units.Select(u => (Entry: e, Unit: u))))
        {
            Assert.Single(texts.PresenterOf(unit.Entry).DescribeRows(unit.Unit.Settings));
            var row = texts.Describe(unit.Entry, unit.Unit);
            Assert.Same(row, texts.Describe(unit.Entry, unit.Unit)); // cached
            Assert.False(string.IsNullOrWhiteSpace(row.Name));
        }

        var policy = entry.Units.Single(u => u.Kind == GpoSettingUnitKind.AdministrativeTemplate);
        Assert.Equal("Zasady", GpoSettingBranchText(texts, entry, policy));
        Assert.Equal(GpoScope.Computer, texts.Describe(entry, policy).Scope);
        var drive = entry.Units.Single(u => u.Kind == GpoSettingUnitKind.Preference);
        Assert.Contains("K:", texts.Describe(entry, drive).Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrincipalName_NamesDomainSidsAndWellKnownPrincipals()
    {
        var harness = await GroupPolicyHarness.CreateAsync(Polish);
        using var service = new GpoSettingsIndexService(harness.Services);
        var view = await service.GetAsync(GpoIndexRefresh.IfMissing, Ct);
        var texts = new GpoIndexTexts(harness.B2.Localizer, view);

        Assert.Equal(@"CONTOSO\GG_IT", texts.PrincipalName(harness.B2.Builder.SidOf(harness.Company.GroupIt)));
        Assert.Equal(@"CONTOSO\GG_IT", texts.PrincipalName("*" + harness.B2.Builder.SidOf(harness.Company.GroupIt)));
        Assert.Equal(@"CONTOSO\łukasz.wiśniewski", texts.PrincipalName(@"CONTOSO\łukasz.wiśniewski"));
        Assert.NotEqual("S-1-5-32-544", texts.PrincipalName("S-1-5-32-544")); // the localized local group name
        Assert.DoesNotContain("GG_IT", texts.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void IndexTexts_ExistInBothLanguages()
    {
        var en = Resources.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var pl = Resources.GetResourceSet(CultureInfo.GetCultureInfo("pl"), true, false)!;
        var keys = L.GpoIndex.AllKeys().ToList();

        Assert.Equal(9 + Enum.GetValues<GpoIndexStage>().Length + Enum.GetValues<GpoIndexStatus>().Length, keys.Count);
        Assert.Equal([], keys.Where(k => en.GetString(k) is null));
        Assert.Equal([], keys.Where(k => pl.GetString(k) is null));
        Assert.Equal("Odczyt folderów GPO: 12 z 480", string.Format(Polish, pl.GetString(L.GpoIndex.Progress)!, pl.GetString(L.GpoIndex.StageKey(GpoIndexStage.Folders)), 12, 480));
    }

    private static string GpoSettingBranchText(GpoIndexTexts texts, GpoIndexEntry entry, GpoSettingUnit unit) => texts.Describe(entry, unit).BranchText;

    /// <summary>One GPO with a unit of every kind the parsers produce.</summary>
    private static void PutEveryKind(FakeSysvol sysvol, Guid id)
    {
        sysvol.Put(id, SecurityTemplate.RelativePath, FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template(
            "[System Access]", "MinimumPasswordLength = 12",
            "[Privilege Rights]", @"SeRemoteInteractiveLogonRight = CONTOSO\GG_IT,*S-1-5-32-544",
            "[Group Membership]", "*S-1-5-32-544__Members = CONTOSO\\jan.kowalski",
            "[Registry Values]", "MACHINE\\System\\CurrentControlSet\\Control\\Lsa\\LimitBlankPasswordUse=4,1",
            "[Service General Setting]", "\"Spooler\",4,\"\"",
            "[File Security]", "\"%SystemRoot%\\System32\\narzędzie.exe\",0,\"D:PAR(A;;FA;;;BA)\"")));
        sysvol.PutText(id, "Machine/Microsoft/Windows NT/Audit/audit.csv",
            "Machine Name,Policy Target,Subcategory,Subcategory GUID,Inclusion Setting,Exclusion Setting,Setting Value\r\n"
            + ",System,Audit Logon,{0cce9215-69ae-11d9-bed3-505054503030},Success and Failure,,3\r\n,,Option:CrashOnAuditFail,,Enabled,,1\r\n");
        var registry = new RegistryPolicyDocument();
        registry.Set(@"Software\Policies\Contoso\Eksplorator", "NoPreview", RegistryFormat.RegDword, BitConverter.GetBytes(1u));
        registry.Set(@"Software\Contoso\Inne", "Wartość", RegistryFormat.RegSz, Encoding.Unicode.GetBytes("żółw\0"));
        registry.ClearValues(@"Software\Contoso\Lista");
        sysvol.Put(id, "Machine/Registry.pol", registry.ToBytes());
        sysvol.Put(id, "User/Registry.pol", Encoding.ASCII.GetBytes("PReg?")); // unreadable
        sysvol.PutText(id, "User/Preferences/Drives/Drives.xml",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Drives clsid=\"{8FDDCC1A-0C3C-43cd-A6B4-71A6DF20DA8C}\"><Drive clsid=\"{935D1B74-9CB8-4e3c-9914-7DD559B7A417}\" name=\"K:\" uid=\"{0D1F7F22-6D1B-4A35-9D9C-0E3D9F3F0077}\"><Properties action=\"U\" path=\"\\\\fs01\\Księgowość\" label=\"Księgowość\" letter=\"K\"/></Drive></Drives>");
        sysvol.Put(id, "Machine/Scripts/scripts.ini", FakeSysvolFileReader.Utf16("[Startup]\r\n0CmdLine=porządki.cmd\r\n0Parameters=-cicho\r\n"));
        sysvol.Put(id, "Machine/Scripts/psscripts.ini", FakeSysvolFileReader.Utf16("[ScriptsConfig]\r\nStartExecutePSFirst=true\r\n"));
        sysvol.PutText(id, "GPO.cmt", "Uzgodnione z Grażyną Dąbrowską");
    }
}
