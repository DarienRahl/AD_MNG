using System.Text;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Security;

namespace AdMng.Core.Tests.GroupPolicy.Security;

public class GppGroupsDocumentTests
{
    private static readonly DateTime Changed = new(2026, 11, 3, 10, 15, 0);

    private const string Sample = """
        <?xml version="1.0" encoding="utf-8"?>
        <Groups clsid="{3125E937-EB16-4b4c-9934-544FC6D24D26}">
          <User clsid="{DF5F1855-51E5-4d24-8B1A-D9BDE98BA1D1}" name="serwis" uid="{11111111-1111-1111-1111-111111111111}"><Properties action="U" userName="serwis"/></User>
          <Group clsid="{6D4A79E4-529C-4481-ABD0-F5BD7EA93BA7}" name="Administrators (built-in)" image="2" changed="2025-01-01 08:00:00" uid="{22222222-2222-2222-2222-222222222222}" bypassErrors="1">
            <Properties action="U" newName="" description="Opis" deleteAllUsers="0" deleteAllGroups="0" removeAccounts="0" groupSid="S-1-5-32-544" groupName="Administrators (built-in)">
              <Members><Member name="KONTOSO\Serwisanci" action="ADD" sid="S-1-5-21-1-2-3-1105"/></Members>
            </Properties>
            <Filters><FilterComputer bool="AND" not="0" type="NETBIOS" name="PC-KSIĘGOWOŚĆ-01"/></Filters>
          </Group>
        </Groups>
        """;

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Update_ChangesTheItem_KeepingTargetingAndOtherAttributes()
    {
        var document = GppGroupsDocument.Parse(Bytes(Sample));
        var item = Assert.Single(document.Items);

        document.Update(0, new GppLocalGroupEdit(item.GroupName!, item.GroupSid, GppItemAction.Update, DeleteAllUsers: true, DeleteAllGroups: false,
        [
            new GppGroupMember(@"KONTOSO\Serwisanci", "S-1-5-21-1-2-3-1105", GppMemberAction.Add),
            new GppGroupMember(@"KONTOSO\Łucja.Wiśniewska", "s-1-5-21-1-2-3-1201", GppMemberAction.Remove),
        ]), Changed);

        var reread = GppGroupsParser.Parse(document.ToBytes());
        var updated = Assert.Single(reread);
        Assert.True(updated.DeleteAllUsers);
        Assert.True(updated.HasItemLevelTargeting);
        Assert.Equal("{22222222-2222-2222-2222-222222222222}", updated.Uid);
        Assert.Equal([GppMemberAction.Add, GppMemberAction.Remove], updated.Members.Select(m => m.Action));
        Assert.Equal("S-1-5-21-1-2-3-1201", updated.Members[1].Sid);
        var xml = Encoding.UTF8.GetString(document.ToBytes());
        Assert.Contains("changed=\"2026-11-03 10:15:00\"", xml, StringComparison.Ordinal);
        Assert.Contains("bypassErrors=\"1\"", xml, StringComparison.Ordinal);
        Assert.Contains("description=\"Opis\"", xml, StringComparison.Ordinal);
        Assert.Contains("<User ", xml, StringComparison.Ordinal);
        Assert.Contains("PC-KSIĘGOWOŚĆ-01", xml, StringComparison.Ordinal);
        Assert.Equal([0xEF, 0xBB, 0xBF], document.ToBytes()[..3]);
    }

    [Fact]
    public void Update_WithoutChanges_KeepsTheChangedTime()
    {
        var document = GppGroupsDocument.Parse(Bytes(Sample));
        var item = document.Items[0];

        document.Update(0, new GppLocalGroupEdit(item.GroupName!, item.GroupSid, item.Action, item.DeleteAllUsers, item.DeleteAllGroups, item.Members), Changed);

        Assert.Contains("changed=\"2025-01-01 08:00:00\"", Encoding.UTF8.GetString(document.ToBytes()), StringComparison.Ordinal);
    }

    [Fact]
    public void Add_AndRemove()
    {
        var document = GppGroupsDocument.New();

        var index = document.Add(new GppLocalGroupEdit("Remote Desktop Users (built-in)", "S-1-5-32-555", GppItemAction.Replace, false, false,
            [new GppGroupMember(@"KONTOSO\Pomoc techniczna", "S-1-5-21-1-2-3-1300", GppMemberAction.Add)]), Changed);
        document.Add(new GppLocalGroupEdit("Serwisanci lokalni", null, GppItemAction.Create, false, false, []), Changed);

        Assert.Equal(0, index);
        var items = GppGroupsParser.Parse(document.ToBytes());
        Assert.Equal(["Remote Desktop Users (built-in)", "Serwisanci lokalni"], items.Select(i => i.GroupName));
        Assert.Equal(GppItemAction.Replace, items[0].Action);
        Assert.Null(items[1].GroupSid);
        Assert.Matches("^\\{[0-9A-F-]{36}\\}$", items[0].Uid!);
        Assert.Contains("image=\"1\"", Encoding.UTF8.GetString(document.ToBytes()), StringComparison.Ordinal);

        document.RemoveAt(0);

        Assert.Equal("Serwisanci lokalni", Assert.Single(GppGroupsParser.Parse(document.ToBytes())).GroupName);
    }

    [Fact]
    public void InvalidInput_IsRefused()
    {
        var document = GppGroupsDocument.New();

        Assert.Throws<GpoParseException>(() => GppGroupsDocument.Parse(Bytes("<Printers/>")));
        Assert.Throws<GpoParseException>(() => GppGroupsDocument.Parse(Bytes("<!DOCTYPE x [<!ENTITY a \"b\">]><Groups/>")));
        Assert.Throws<ArgumentException>(() => document.Add(new GppLocalGroupEdit(" ", null, GppItemAction.Update, false, false, []), Changed));
        Assert.Throws<ArgumentException>(() => document.Add(new GppLocalGroupEdit("X", "nie-SID", GppItemAction.Update, false, false, []), Changed));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.RemoveAt(0));
    }

    [Fact]
    public void Update_AndItsUndo_GiveTheBytesBack()
    {
        var bytes = GppGroupsDocument.Parse(Bytes(Sample)).ToBytes();
        var document = GppGroupsDocument.Parse(bytes);
        var item = document.Items[0];
        GppLocalGroupEdit Edit(bool deleteAllUsers) => new(item.GroupName!, item.GroupSid, item.Action, deleteAllUsers, item.DeleteAllGroups, item.Members);

        document.Update(0, Edit(true), Changed);
        Assert.NotEqual(bytes, document.ToBytes());
        document.Update(0, Edit(false), Changed.AddMinutes(1));

        Assert.Equal(bytes, document.ToBytes());
    }
}
