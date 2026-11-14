using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy.Search;

/// <summary>
/// The K7 read helpers (21.1): <see cref="GroupPolicyReader.ReadSettings"/> with the sink overflow, the directory-side
/// settings of all GPOs in one search, and <see cref="GppXmlParser.Items"/> with the golden keys of <see cref="GppXmlParser.Parse"/>.
/// </summary>
public sealed class GpoContentReadTests : GroupPolicyTestBed
{
    /// <summary>
    /// uid-less items, a collection pseudo-item with a uid (disabled), an item below it, a nested collection without a uid
    /// (not an item) and a targeted item below both.
    /// </summary>
    internal const string CollectionsXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <Drives clsid="{8FDDCC1A-0C3C-43cd-A6B4-71A6DF20DA8C}">
          <Drive clsid="{935D1B74-9CB8-4e3c-9914-7DD559B7A417}" name="K:" uid="{11111111-1111-1111-1111-111111111111}" changed="2026-10-01 08:00:00"><Properties action="U" path="\\fs01\Kadry" letter="K"/></Drive>
          <Drive clsid="{935D1B74-9CB8-4e3c-9914-7DD559B7A417}" name="L:"><Properties action="C" path="\\fs01\Łódź" letter="L"/></Drive>
          <Collection clsid="{53B533F5-224C-47e3-B01B-CA3B3F3FF4BF}" name="Oddziały" uid="{22222222-2222-2222-2222-222222222222}" disabled="1">
            <Drive clsid="{935D1B74-9CB8-4e3c-9914-7DD559B7A417}" name="M:"><Properties action="U" path="\\fs01\Mazury" letter="M"/></Drive>
            <Collection clsid="{53B533F5-224C-47e3-B01B-CA3B3F3FF4BF}" name="Wewnętrzna">
              <Filters><FilterGroup bool="AND" not="0" name="CONTOSO\GG_Kadry" sid="S-1-5-21-1-2-3-1105" userContext="1" primaryGroup="0" localGroup="0"/></Filters>
              <Drive clsid="{935D1B74-9CB8-4e3c-9914-7DD559B7A417}" name="N:" uid="{33333333-3333-3333-3333-333333333333}"><Properties action="U" path="\\fs01\Nysa" letter="N"/><Filters><FilterComputer bool="AND" not="0" type="NETBIOS" name="WAW-PC001"/></Filters></Drive>
            </Collection>
          </Collection>
        </Drives>
        """;

    [Fact]
    public void GppParse_GoldenKeys_UnchangedByTheItemsRefactor()
    {
        var sink = new GpoSettingSink("User/Preferences/Drives/Drives.xml");

        GppXmlParser.Parse(Encoding.UTF8.GetBytes(CollectionsXml), GpoScope.User, "Drives", sink);

        // Pinned before the K7 refactor (Parse iterating Items): the Watchdog persists these keys.
        const string P = "User/Preferences/Drives/";
        string[] expected =
        [
            P + "{11111111-1111-1111-1111-111111111111}@element", P + "{11111111-1111-1111-1111-111111111111}@name",
            P + "{11111111-1111-1111-1111-111111111111}/action", P + "{11111111-1111-1111-1111-111111111111}/path",
            P + "{11111111-1111-1111-1111-111111111111}/letter",
            P + "Drive#2@element", P + "Drive#2@name", P + "Drive#2/action", P + "Drive#2/path", P + "Drive#2/letter",
            P + "{22222222-2222-2222-2222-222222222222}@element", P + "{22222222-2222-2222-2222-222222222222}@name",
            P + "{22222222-2222-2222-2222-222222222222}@disabled",
            P + "Drive#4@element", P + "Drive#4@name", P + "Drive#4/action", P + "Drive#4/path", P + "Drive#4/letter",
            P + "{33333333-3333-3333-3333-333333333333}@element", P + "{33333333-3333-3333-3333-333333333333}@name",
            P + "{33333333-3333-3333-3333-333333333333}/action", P + "{33333333-3333-3333-3333-333333333333}/path",
            P + "{33333333-3333-3333-3333-333333333333}/letter", P + "{33333333-3333-3333-3333-333333333333}/Filters",
        ];
        Assert.Equal(expected, sink.Settings.Select(s => s.Key));
        Assert.Equal(@"\\fs01\Łódź", sink.Settings.Single(s => s.Key == P + "Drive#2/path").Value);
        Assert.Equal("1", sink.Settings.Single(s => s.Key == P + "{22222222-2222-2222-2222-222222222222}@disabled").Value);
        Assert.Equal("FilterComputer bool=AND name=WAW-PC001 not=0 type=NETBIOS", sink.Settings.Single(s => s.Key.EndsWith("/Filters", StringComparison.Ordinal)).Value);
    }

    [Fact]
    public void GppItems_IdsAndCollectionChains()
    {
        var items = GppXmlParser.Items(GpoXml.Load(Encoding.UTF8.GetBytes(CollectionsXml))).ToList();

        Assert.Equal(["{11111111-1111-1111-1111-111111111111}", "Drive#2", "{22222222-2222-2222-2222-222222222222}", "Drive#4", "{33333333-3333-3333-3333-333333333333}"],
            items.Select(i => i.ItemId));
        Assert.Equal(["Drive", "Drive", "Collection", "Drive", "Drive"], items.Select(i => i.Element.Name.LocalName));
        Assert.Equal([0, 0, 0, 1, 2], items.Select(i => i.Collections.Count));
        Assert.Equal(["Oddziały", "Wewnętrzna"], items[4].Collections.Select(c => GpoXml.Attribute(c, "name")));
        Assert.Equal("Drive (depth 2)", items[4].ToString());
    }

    [Fact]
    public void ReadSettings_OverflowedRegistryPol_IsReported_AndSettingsUnchanged()
    {
        var entries = Enumerable.Range(0, GpoSettingSink.DefaultCapacity + 1)
            .Select(i => new RegistryPolEntry(@"Software\Policies\Contoso\Wiele", "Wartość" + i, RegistryFormat.RegDword, BitConverter.GetBytes(1u)));

        var content = new GpoFolderContent([], new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Machine/Registry.pol"] = RegistryPolFile.Write(entries),
            [SecurityTemplate.RelativePath] = FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template("[System Access]", "MinimumPasswordLength = 12")),
        });

        var read = GroupPolicyReader.ReadSettings(content);

        Assert.Equal(["Machine/Registry.pol"], read.OverflowedFiles);
        Assert.False(read.Complete);
        Assert.Equal(GpoSettingSink.DefaultCapacity + 1, read.Settings.Count); // 10 000 values + the password length
        Assert.Equal(read.Settings.Select(s => s.Key), GroupPolicyReader.Settings(content).Select(s => s.Key));
        Assert.Equal("10001 settings, 1 overflowed", read.ToString());
    }

    [Fact]
    public void ReadSettings_OfACompleteFolder_IsComplete()
    {
        var content = new GpoFolderContent([], new Dictionary<string, byte[]>
        {
            [SecurityTemplate.RelativePath] = FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template("[System Access]", "MinimumPasswordLength = 12")),
        });

        var read = GroupPolicyReader.ReadSettings(content);

        Assert.True(read.Complete);
        Assert.Equal(["Computer/Security/System Access/MinimumPasswordLength"], read.Settings.Select(s => s.Key));
    }

    [Fact]
    public async Task CountDirectorySettings_AllGposInOneSearch_SameAsPerGpo()
    {
        var gpo = Sample.WorkstationPasswords;
        var machine = $"CN=Machine,{GpoDn(gpo)}";
        Session.Seed($"CN=Class Store,{machine}", new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "classStore" } });
        Session.Seed($"CN=Packages,CN=Class Store,{machine}", new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "container" } });
        Session.Seed($"CN=Sieć bezprzewodowa,CN=User,{GpoDn(Sample.DriveMappings)}", new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "msieee80211-Policy" } });
        Session.Seed($"CN=Inny,CN=User,{GpoDn(Sample.DriveMappings)}", new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "container" } });
        Session.ClearOperations();

        var counts = await Reader.CountDirectorySettingsAsync(Session, Ct);

        Assert.Single(Session.Operations, o => o.Kind == FakeOperationKind.Search);
        Assert.Equal(new Dictionary<Guid, int> { [gpo] = 1, [Sample.DriveMappings] = 2 }, counts);
        foreach (var id in new[] { gpo, Sample.DriveMappings, Sample.ServerSecurity })
        {
            Assert.Equal(await Reader.CountDirectorySettingsAsync(Session, id, Ct), counts.GetValueOrDefault(id));
        }
    }

    [Fact]
    public async Task CountDirectorySettings_NoPoliciesContainer_IsEmpty()
    {
        var builder = new FakeDirectoryBuilder();

        var counts = await new GroupPolicyReader().CountDirectorySettingsAsync(builder.Session, Ct);

        Assert.Empty(counts);
    }
}
