using System.Text;
using AdMng.Core.GroupPolicy;

namespace AdMng.Core.Tests.GroupPolicy;

/// <summary>GPP Groups.xml (Local Users and Groups) as the privileged access review (U4c) reads it.</summary>
public sealed class GppGroupsParserTests
{
    private const string Xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <Groups clsid="{3125E937-EB16-4b4c-9934-544FC6D24D26}">
          <User clsid="{DF5F1855-51E5-4d24-8B1A-D9BDE98BA1D1}" name="admin.lokalny" uid="{11111111-1111-1111-1111-111111111111}">
            <Properties action="U" newName="" fullName="Administrator lokalny" cpassword="" userName="admin.lokalny"/>
          </User>
          <Group clsid="{6D4A79E4-529C-4481-ABD0-F5BD7EA93BA7}" name="Administratorzy (wbudowane)" uid="{22222222-2222-2222-2222-222222222222}">
            <Properties action="U" newName="" description="" deleteAllUsers="1" deleteAllGroups="0" removeAccounts="0"
                        groupSid="s-1-5-32-544" groupName="Administratorzy (wbudowane)">
              <Members>
                <Member name="CONTOSO\GG_Wsparcie_Stacji" action="ADD" sid="S-1-5-21-1-2-3-1105"/>
                <Member name="CONTOSO\łucja.żółkiewska" action="REMOVE" sid=""/>
                <Member name="" action="ADD" sid=""/>
              </Members>
            </Properties>
          </Group>
          <Collection clsid="{53B533F5-224C-47e3-B01B-CA3B3F3FF4BF}" name="Serwis">
            <Filters><FilterGroup bool="AND" not="0" name="CONTOSO\Stacje_Serwisowe" sid="S-1-5-21-1-2-3-1300"/></Filters>
            <Group clsid="{6D4A79E4-529C-4481-ABD0-F5BD7EA93BA7}" name="Użytkownicy pulpitu zdalnego" uid="{33333333-3333-3333-3333-333333333333}">
              <Properties action="R" groupName="Użytkownicy pulpitu zdalnego" deleteAllGroups="1">
                <Members><Member name="CONTOSO\GG_Serwis_RDP" action="ADD"/></Members>
              </Properties>
            </Group>
          </Collection>
          <Collection clsid="{53B533F5-224C-47e3-B01B-CA3B3F3FF4BF}" name="Stare" disabled="1">
            <Group clsid="{6D4A79E4-529C-4481-ABD0-F5BD7EA93BA7}" name="Operatorzy kopii zapasowych" uid="{44444444-4444-4444-4444-444444444444}">
              <Properties action="D" groupSid="S-1-5-32-551" groupName="Operatorzy kopii zapasowych"/>
            </Group>
          </Collection>
          <Group clsid="{6D4A79E4-529C-4481-ABD0-F5BD7EA93BA7}" name="Lokalna grupa SQL" uid="{55555555-5555-5555-5555-555555555555}">
            <Properties action="C" groupName="SQL Admins (lokalni)"><Members/></Properties>
            <Filters/>
          </Group>
        </Groups>
        """;

    [Fact]
    public void LocalGroupItems_InDocumentOrder_WithActionsTargetingAndMembers()
    {
        var groups = GppGroupsParser.Parse(Utf8Bom(Xml));

        Assert.Equal(["Administratorzy (wbudowane)", "Użytkownicy pulpitu zdalnego", "Operatorzy kopii zapasowych", "SQL Admins (lokalni)"],
            groups.Select(g => g.GroupName));

        var administrators = groups[0];
        Assert.Equal("{22222222-2222-2222-2222-222222222222}", administrators.Uid);
        Assert.Equal("S-1-5-32-544", administrators.GroupSid);
        Assert.Equal(GppItemAction.Update, administrators.Action);
        Assert.True(administrators.DeleteAllUsers);
        Assert.False(administrators.DeleteAllGroups);
        Assert.False(administrators.HasItemLevelTargeting);
        Assert.False(administrators.Disabled);
        Assert.Equal(
            [
                new GppGroupMember(@"CONTOSO\GG_Wsparcie_Stacji", "S-1-5-21-1-2-3-1105", GppMemberAction.Add),
                new GppGroupMember(@"CONTOSO\łucja.żółkiewska", null, GppMemberAction.Remove),
            ],
            administrators.Members);

        // In a collection with a filter: item-level targeting is inherited; no groupSid - the name decides.
        var remoteDesktop = groups[1];
        Assert.Null(remoteDesktop.GroupSid);
        Assert.Equal(GppItemAction.Replace, remoteDesktop.Action);
        Assert.True(remoteDesktop.DeleteAllGroups);
        Assert.True(remoteDesktop.HasItemLevelTargeting);
        Assert.Equal(new GppGroupMember(@"CONTOSO\GG_Serwis_RDP", null, GppMemberAction.Add), Assert.Single(remoteDesktop.Members));

        // A disabled collection disables its items; an empty Filters element is no targeting.
        Assert.True(groups[2].Disabled);
        Assert.Equal(GppItemAction.Delete, groups[2].Action);
        Assert.Equal(GppItemAction.Create, groups[3].Action);
        Assert.False(groups[3].HasItemLevelTargeting);
        Assert.Empty(groups[3].Members);
    }

    [Fact]
    public void HostileXml_FailsClosed()
    {
        const string Laughs = """
            <?xml version="1.0"?>
            <!DOCTYPE lolz [<!ENTITY lol "lol"><!ENTITY lol2 "&lol;&lol;&lol;&lol;">]>
            <Groups><Group><Properties groupName="&lol2;"/></Group></Groups>
            """;
        Assert.Throws<GpoParseException>(() => GppGroupsParser.Parse(Encoding.UTF8.GetBytes(Laughs)));
        Assert.Throws<GpoParseException>(() => GppGroupsParser.Parse("<Groups><Group>"u8));
        var deep = string.Concat(Enumerable.Repeat("<Collection>", GpoXml.MaxDepth + 2)) + string.Concat(Enumerable.Repeat("</Collection>", GpoXml.MaxDepth + 2));
        Assert.Throws<GpoParseException>(() => GppGroupsParser.Parse(Encoding.UTF8.GetBytes("<Groups>" + deep + "</Groups>")));
    }

    private static byte[] Utf8Bom(string text) => GpoTextTests.Utf8Bom(text);
}
