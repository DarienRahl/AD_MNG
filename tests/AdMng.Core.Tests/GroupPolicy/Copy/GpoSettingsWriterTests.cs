using System.Text;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy.Copy;

/// <summary>The writers and documents of the K7 planners on their own (no directory).</summary>
public class GpoSettingsWriterTests
{
    private static readonly HandUnits Units = new();

    private static GpoFolderContent Folder(params (string Path, byte[] Content)[] files) =>
        new([], files.ToDictionary(f => f.Path, f => f.Content, StringComparer.OrdinalIgnoreCase));

    private static GpoSettingsWriter Writer(GpoFolderContent target, PolicyDefinitionSet? set = null) =>
        new(target, set, p => HandUnits.KnownSid(p, null), new DateTime(2026, 11, 9, 15, 0, 0));

    private static GpoSettingUnit UnitOf(GpoFolderContent content, string keyFragment, PolicyDefinitionSet? set = null) =>
        Units.Of(content, new GpoUnitContext(set, null)).Single(u => u.Settings.Any(s => s.Key.Contains(keyFragment, StringComparison.OrdinalIgnoreCase)));

    private static byte[] Pol(params RegistryPolEntry[] entries) => RegistryPolFile.Write(entries);

    private static RegistryPolEntry Text(string key, string name, string value) => new(key, name, RegistryFormat.RegSz, RegistryPolFile.StringData(value));

    private static IReadOnlyList<string> Names(byte[] pol) => [.. RegistryPolFile.Read(pol).Select(e => e.Key + "!" + e.ValueName + (e.ValueName.StartsWith("**Delete", StringComparison.Ordinal) ? "=" + RegistryPolFile.ReadString(e.Data) : string.Empty))];

    [Fact]
    public void AValueOfASharedDeleteValuesList_IsCopiedAsItsOwnDeletion()
    {
        var source = Folder((GroupPolicyPlanner.MachineRegistryPolicyPath, Pol(new RegistryPolEntry(@"Software\Kontoso", "**DeleteValues", RegistryFormat.RegSz, RegistryPolFile.StringData("a;b")))));
        var target = Folder();
        var writer = Writer(target);
        var unit = UnitOf(source, "Kontoso!a");

        Assert.Null(writer.CheckCopy(unit, source));
        writer.Copy(unit, source, union: false);

        var (path, old, next) = Assert.Single(writer.ChangedFiles());
        Assert.Equal(GroupPolicyPlanner.MachineRegistryPolicyPath, path);
        Assert.Null(old);
        Assert.Equal([@"Software\Kontoso!**del.a"], Names(next));
    }

    [Fact]
    public void RemovingAValueOfASharedDeleteValuesList_KeepsTheOtherNames()
    {
        var target = Folder((GroupPolicyPlanner.MachineRegistryPolicyPath, Pol(new RegistryPolEntry(@"Software\Kontoso", "**DeleteValues", RegistryFormat.RegSz, RegistryPolFile.StringData("a;b")))));
        var writer = Writer(target);
        var unit = UnitOf(target, "Kontoso!a");

        Assert.Null(writer.CheckRemove(unit));
        writer.Remove(unit);

        Assert.Equal([@"Software\Kontoso!**DeleteValues=b"], Names(Assert.Single(writer.ChangedFiles()).New));
    }

    [Fact]
    public void ADeletedSubkey_IsInsertedBeforeTheValuesOfThatSubkey()
    {
        var source = Folder((GroupPolicyPlanner.MachineRegistryPolicyPath, Pol(new RegistryPolEntry(@"Software\Kontoso", "**DeleteKeys", RegistryFormat.RegSz, RegistryPolFile.StringData("Stare;Inne")))));
        var target = Folder((GroupPolicyPlanner.MachineRegistryPolicyPath, Pol(Text(@"Software\Alfa", "x", "1"), Text(@"Software\Kontoso\Stare", "y", "2"))));
        var writer = Writer(target);
        var unit = UnitOf(source, @"Kontoso\Stare");

        writer.Copy(unit, source, union: false);

        Assert.Equal([@"Software\Alfa!x", @"Software\Kontoso!**DeleteKeys=Stare", @"Software\Kontoso\Stare!y"], Names(Assert.Single(writer.ChangedFiles()).New));
    }

    [Fact]
    public void ACopiedValue_GoesAfterTheKeysDirectives_AndReplacesItsOtherForms()
    {
        var source = Folder((GroupPolicyPlanner.MachineRegistryPolicyPath, Pol(Text(@"Software\Kontoso", "a", "nowa"))));
        var target = Folder((GroupPolicyPlanner.MachineRegistryPolicyPath, Pol(
            new RegistryPolEntry(@"Software\Kontoso", "**del.a", RegistryFormat.RegSz, RegistryPolEntry.DeletionData),
            new RegistryPolEntry(@"Software\Kontoso", "**delvals.", RegistryFormat.RegSz, RegistryPolEntry.DeletionData),
            Text(@"Software\Kontoso", "b", "zostaje"))));
        var writer = Writer(target);

        writer.Copy(UnitOf(source, "Kontoso!a"), source, union: false);

        Assert.Equal([@"Software\Kontoso!**delvals.", @"Software\Kontoso!b", @"Software\Kontoso!a"], Names(Assert.Single(writer.ChangedFiles()).New));
    }

    [Fact]
    public void UnchangedDocuments_AndRefusedPreferenceFiles_AreNotWritten()
    {
        var source = Folder(("User/Preferences/Drives/Drives.xml", Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Drives clsid=\"{8FDDCC1A-0C3C-43cd-A6B4-71A6DF20DA8C}\"><Drive clsid=\"{935D1B74-9CB8-4e3c-9914-7DD559B7A417}\" name=\"K:\" uid=\"{0D1F7F22-6D1B-4A35-9D9C-0E3D9F3F0001}\"><Properties action=\"U\" path=\"\\\\fs01\\Kadry\" letter=\"K\"/></Drive></Drives>")));
        var writer = Writer(Folder());

        Assert.Null(writer.CheckCopy(UnitOf(source, "Drives"), source)); // a check opens the target document but writes nothing

        Assert.Empty(writer.ChangedFiles());
    }

    [Fact]
    public void PolicyClientExtensions_NameTheExtensionsOfConfiguredPoliciesOnly()
    {
        var set = WithClientExtension();
        var document = new RegistryPolicyDocument([new RegistryPolEntry(@"Software\Policies\Contoso\Eksplorator", "NoPreview", RegistryFormat.RegDword, RegistryPolFile.DwordData(1))]);

        Assert.Equal([ClientExtension], PolicyClientExtensions.Of(document, set, PolicyClass.Machine, PolicyOwnershipIndex.Build(set)));
        Assert.Empty(PolicyClientExtensions.Of(new RegistryPolicyDocument(), set, PolicyClass.Machine, PolicyOwnershipIndex.Build(set)));
        Assert.Empty(PolicyClientExtensions.Of(document, set, PolicyClass.User, PolicyOwnershipIndex.Build(set)));
    }

    internal static readonly Guid ClientExtension = new("D02B1F72-3407-48AE-BA88-E8213C6761F1");

    /// <summary>The sample templates with a <c>clientExtension</c> on "WylaczPodglad".</summary>
    internal static PolicyDefinitionSet WithClientExtension()
    {
        var files = SampleAdministrativeTemplates.Files();
        files["contoso.admx"] = SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmx.Replace(
            "<policy name=\"WylaczPodglad\" class=\"Machine\"", "<policy name=\"WylaczPodglad\" class=\"Machine\" clientExtension=\"{" + ClientExtension + "}\"", StringComparison.Ordinal));
        var (set, _) = PolicyDefinitionLoader.LoadAsync(new InMemoryPolicyDefinitionSource("Magazyn testowy", files), ["pl-PL", "en-US"], CancellationToken.None).GetAwaiter().GetResult();
        return set;
    }
}

/// <summary>The generic preferences document.</summary>
public class GppPreferenceDocumentTests
{
    private const string Xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><Drives clsid=\"{8FDDCC1A-0C3C-43cd-A6B4-71A6DF20DA8C}\">"
        + "<Drive clsid=\"{935D1B74-9CB8-4e3c-9914-7DD559B7A417}\" name=\"H:\"><Properties action=\"U\" path=\"\\\\fs01\\dom\" letter=\"H\"/></Drive>"
        + "<Collection clsid=\"{53B533F5-224C-47e3-B01B-CA3B3F3FF4BF}\" name=\"Działy\" uid=\"{AAAAAAAA-0000-0000-0000-000000000001}\">"
        + "<Drive clsid=\"{935D1B74-9CB8-4e3c-9914-7DD559B7A417}\" name=\"K:\"><Properties action=\"U\" path=\"\\\\fs01\\Księgowość\" letter=\"K\"/></Drive>"
        + "</Collection>"
        + "<Drive clsid=\"{935D1B74-9CB8-4e3c-9914-7DD559B7A417}\" name=\"P:\" uid=\"{BBBBBBBB-0000-0000-0000-000000000001}\"><Properties action=\"U\" path=\"\\\\fs01\\Projekty\" letter=\"P\"/></Drive>"
        + "</Drives>";

    [Fact]
    public void Items_HaveTheIdsOfTheParser_WithTheirCollections()
    {
        var bytes = Encoding.UTF8.GetBytes(Xml);
        var document = GppPreferenceDocument.Parse(bytes);
        var sink = new GpoSettingSink(GroupPolicyPlannerPaths.Drives);
        GppXmlParser.Parse(bytes, GpoScope.User, "Drives", sink);
        var parserIds = sink.Settings.Select(s => GpoSettingKey.Parse(s.Key).ItemId).Distinct().ToList();

        Assert.Equal(parserIds, document.Items.Select(i => i.ItemId));
        Assert.Equal(["Drive#1", "{AAAAAAAA-0000-0000-0000-000000000001}", "Drive#3", "{BBBBBBBB-0000-0000-0000-000000000001}"], document.Items.Select(i => i.ItemId));
        Assert.Equal("Działy", (string?)Assert.Single(document.Items[2].Collections).Attribute("name"));
        Assert.Empty(document.Items[0].Collections);
    }

    [Fact]
    public void Remove_UsesTheIdsAsRead_AfterAnEarlierRemoval()
    {
        var document = GppPreferenceDocument.Parse(Encoding.UTF8.GetBytes(Xml));

        Assert.True(document.Remove("Drive#1"));
        Assert.True(document.Remove("Drive#3"));
        Assert.False(document.Remove("Drive#3"));

        var root = GpoXml.Load(document.ToBytes()).Root!;
        Assert.Empty(Assert.Single(root.Elements(), e => e.Name.LocalName == "Collection").Elements()); // the emptied collection stays
        Assert.Equal("P:", (string?)Assert.Single(root.Elements(), e => e.Name.LocalName == "Drive").Attribute("name"));
    }

    [Fact]
    public void Append_ClonesWithANewUid_AndTheChangedTime_InUtf8WithBom()
    {
        var source = GppPreferenceDocument.Parse(Encoding.UTF8.GetBytes(Xml));
        var target = GppPreferenceDocument.New(source.RootName, source.RootClassId);

        var uid = target.Append(source.Find("Drive#1")!.Element, new DateTime(2026, 11, 9, 7, 5, 3));

        var bytes = target.ToBytes();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var drive = Assert.Single(GpoXml.Load(bytes).Root!.Elements());
        Assert.Equal(uid, (string?)drive.Attribute("uid"));
        Assert.Equal("2026-11-09 07:05:03", (string?)drive.Attribute("changed"));
        Assert.NotNull(source.Find("Drive#1")); // the source is untouched
        Assert.Equal(uid, Assert.Single(target.Items).ItemId);
    }
}

/// <summary>Paths used by the document tests.</summary>
internal static class GroupPolicyPlannerPaths
{
    public const string Drives = "User/Preferences/Drives/Drives.xml";
}
