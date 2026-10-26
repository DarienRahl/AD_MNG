using System.Diagnostics;
using System.Text;
using AdMng.Core.GroupPolicy;

namespace AdMng.Core.Tests.GroupPolicy;

/// <summary>GptTmpl.inf as the privileged access review (U4c) reads it: Restricted Groups and User Rights Assignment.</summary>
public sealed class SecurityTemplateTests
{
    private const string Template = """
        [Unicode]
        Unicode=yes
        [System Access]
        MinimumPasswordAge = 1
        [Group Membership]
        *S-1-5-32-544__Memberof =
        *S-1-5-32-544__Members = *S-1-5-21-1-2-3-1105,CONTOSO\GG_Księgowość_Admini , *s-1-5-21-1-2-3-512
        *S-1-5-21-1-2-3-1201__Memberof = *S-1-5-32-555,*S-1-5-32-580
        Użytkownicy zaawansowani__Members = CONTOSO\łucja.żółkiewska
        *S-1-5-32-551__Members =
        bez_separatora = x
        *S-1-5-32-573__Unknown = *S-1-5-21-1-2-3-9
        [Privilege Rights]
        SeDebugPrivilege = *S-1-5-32-544
        SeRemoteInteractiveLogonRight = *S-1-5-32-544,*S-1-5-32-555,CONTOSO\Grażyna
        SeDebugPrivilege = *S-1-5-32-544,*S-1-5-21-1-2-3-1301
        SeDenyNetworkLogonRight =
        [Registry Values]
        MACHINE\System\CurrentControlSet\Control\Lsa\NoLMHash=4,1
        """;

    [Fact]
    public void Utf16WithBom_GroupMembershipBothForms_AndPrivilegeRights_LaterDuplicateWins()
    {
        var template = SecurityTemplate.Parse(GpoTextTests.Utf16(Template));

        Assert.Equal(
        [
            ("S-1-5-32-544", RestrictedGroupRelation.MemberOf, ""),
            ("S-1-5-32-544", RestrictedGroupRelation.Members, @"S-1-5-21-1-2-3-1105|CONTOSO\GG_Księgowość_Admini|S-1-5-21-1-2-3-512"),
            ("S-1-5-21-1-2-3-1201", RestrictedGroupRelation.MemberOf, "S-1-5-32-555|S-1-5-32-580"),
            ("Użytkownicy zaawansowani", RestrictedGroupRelation.Members, @"CONTOSO\łucja.żółkiewska"),
            ("S-1-5-32-551", RestrictedGroupRelation.Members, ""),
        ],
        template.GroupMembership.Select(g => (g.Group, g.Relation, string.Join('|', g.Principals))));

        Assert.Equal(
        [
            ("SeRemoteInteractiveLogonRight", @"S-1-5-32-544|S-1-5-32-555|CONTOSO\Grażyna"),
            ("SeDebugPrivilege", "S-1-5-32-544|S-1-5-21-1-2-3-1301"),
            ("SeDenyNetworkLogonRight", ""),
        ],
        template.PrivilegeRights.Select(r => (r.Right, string.Join('|', r.Principals))));
    }

    [Fact]
    public void LargeTemplate_ManyKeysAndDuplicates_IsParsedInLinearTime_LaterDuplicateStillWins()
    {
        // GptTmpl.inf is untrusted and may fill the 4 MiB read limit: a quadratic parse would stall the review for minutes.
        const int count = 100_000;
        var lines = new List<string> { "[Unicode]", "Unicode=yes", "[Privilege Rights]" };
        lines.AddRange(Enumerable.Range(0, count).Select(i => $"SeŻółćPrivilege{i} = *S-1-5-21-1-2-3-{i}"));
        lines.Add("[Group Membership]");
        lines.AddRange(Enumerable.Range(0, count).Select(i => $"*S-1-5-21-1-2-3-{i}__Memberof = *S-1-5-32-544"));
        lines.Add("[Privilege Rights]");
        lines.Add("seżółćprivilege0 = *S-1-5-32-551");
        var bytes = GpoTextTests.Utf16(string.Join("\r\n", lines));

        var stopwatch = Stopwatch.StartNew();
        var template = SecurityTemplate.Parse(bytes);
        stopwatch.Stop();

        Assert.Equal(count, template.PrivilegeRights.Count);
        Assert.Equal(count, template.GroupMembership.Count);
        Assert.Equal(("seżółćprivilege0", "S-1-5-32-551"), (template.PrivilegeRights[^1].Right, template.PrivilegeRights[^1].Principals.Single()));
        Assert.Equal("SeŻółćPrivilege1", template.PrivilegeRights[0].Right);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Parsing took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void Utf8AndUtf16WithoutBom_AreAccepted_TheSameWay()
    {
        var expected = SecurityTemplate.Parse(GpoTextTests.Utf16(Template));

        foreach (var bytes in new[] { GpoTextTests.Utf8Bom(Template), Encoding.Unicode.GetBytes(Template) })
        {
            var parsed = SecurityTemplate.Parse(bytes);
            Assert.Equal(expected.GroupMembership.Select(g => g.Group), parsed.GroupMembership.Select(g => g.Group));
            Assert.Equal(expected.PrivilegeRights.Select(r => r.Right), parsed.PrivilegeRights.Select(r => r.Right));
        }
    }

    [Fact]
    public void HostileOrBrokenFiles_FailClosed()
    {
        Assert.Throws<GpoParseException>(() => SecurityTemplate.Parse([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(Template)]));
        Assert.Throws<GpoParseException>(() => SecurityTemplate.Parse(Encoding.Latin1.GetBytes("[Privilege Rights]\r\nSeDebugPrivilege = Zażółć")));
        Assert.Throws<GpoParseException>(() => SecurityTemplate.Parse(GpoTextTests.Utf16("SeDebugPrivilege = *S-1-5-32-544")));
        Assert.Empty(SecurityTemplate.Parse(GpoTextTests.Utf16("\r\n")).PrivilegeRights);
    }

    [Theory]
    [InlineData("*S-1-5-32-544", "S-1-5-32-544")]
    [InlineData(" *s-1-5-21-1-2-3-500 ", "S-1-5-21-1-2-3-500")]
    [InlineData(@"CONTOSO\Ślusarczyk", @"CONTOSO\Ślusarczyk")]
    [InlineData("Administratorzy", "Administratorzy")]
    [InlineData("*", "")]
    public void Principal_SidsUpperCased_NamesKept(string value, string expected)
    {
        Assert.Equal(expected, SecurityTemplate.Principal(value));
    }

    [Fact]
    public void GroupMembershipKey_SplitsAtTheLastDoubleUnderscore()
    {
        Assert.True(SecurityTemplate.TrySplitGroupMembershipKey("*S-1-5-32-544__Members", out var group, out var relation, out _));
        Assert.Equal(("S-1-5-32-544", RestrictedGroupRelation.Members), (group, relation!.Value));
        Assert.True(SecurityTemplate.TrySplitGroupMembershipKey("GG__Księgowa__MEMBEROF", out group, out relation, out var text));
        Assert.Equal(("GG__Księgowa", RestrictedGroupRelation.MemberOf, "MEMBEROF"), (group, relation!.Value, text));
        Assert.True(SecurityTemplate.TrySplitGroupMembershipKey("*S-1-5-32-544__Inne", out _, out relation, out text));
        Assert.Null(relation);
        Assert.Equal("Inne", text);
        Assert.False(SecurityTemplate.TrySplitGroupMembershipKey("__Members", out _, out _, out _));
        Assert.False(SecurityTemplate.TrySplitGroupMembershipKey("Members", out _, out _, out _));
    }
}
