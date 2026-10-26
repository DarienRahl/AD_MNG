using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Filters;
using AdMng.Core.Model;
using AdMng.Testing;

namespace AdMng.Core.Tests.Filters;

public class FilterEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);
    private static readonly FilterContext Context = new()
    {
        Now = Now,
        DefaultNamingContext = "DC=contoso,DC=test",
        Schema = FakeSchema.Create("CN=Schema,CN=Configuration,DC=contoso,DC=test"),
    };

    private static readonly DirectoryObject Grazyna = new("CN=Grażyna Dąbrowska,OU=Księgowość,DC=contoso,DC=test",
    [
        new AttributeValues(Attr.Department, ["Księgowość"]),
        new AttributeValues(Attr.Description, ["Główna księgowa – Gdańsk"]),
        new AttributeValues(Attr.UserAccountControl, ["66048"]),
        new AttributeValues(Attr.UserAccountControlComputed, ["16"]),
        new AttributeValues(Attr.LastLogonTimestamp, [FileTime(Now.AddDays(-120))]),
        new AttributeValues(Attr.WhenCreated, [AdConvert.ToGeneralizedTime(Now.AddDays(-400))]),
        new AttributeValues(Attr.MemberOf, ["CN=GG_Ksiegowosc,OU=Grupy,DC=contoso,DC=test"]),
        new AttributeValues(Attr.ObjectSid, [AdConvert.SidToBytes("S-1-5-21-1-2-3-1105")]),
    ]);

    [Theory]
    [InlineData(Attr.Department, FilterOperator.Equals, "KSIĘGOWOŚĆ", true)]
    [InlineData(Attr.Department, FilterOperator.NotEquals, "IT", true)]
    [InlineData(Attr.Description, FilterOperator.Contains, "księgowa", true)]
    [InlineData(Attr.Description, FilterOperator.StartsWith, "Główna", true)]
    [InlineData(Attr.Description, FilterOperator.EndsWith, "gdańsk", true)]
    [InlineData(Attr.Mail, FilterOperator.Present, "", false)]
    [InlineData(Attr.Mail, FilterOperator.NotPresent, "", true)]
    [InlineData(Attr.Mail, FilterOperator.NotEquals, "x@contoso.test", true)]
    [InlineData(Attr.UserAccountControlComputed, FilterOperator.BitSet, "16", true)]
    [InlineData(Attr.UserAccountControl, FilterOperator.BitSet, "0x10000", true)]
    [InlineData(Attr.UserAccountControl, FilterOperator.BitNotSet, "2", true)]
    [InlineData(Attr.UserAccountControl, FilterOperator.GreaterOrEqual, "66048", true)]
    [InlineData(Attr.UserAccountControl, FilterOperator.LessOrEqual, "512", false)]
    [InlineData(Attr.LastLogonTimestamp, FilterOperator.OlderThanDays, "90", true)]
    [InlineData(Attr.LastLogonTimestamp, FilterOperator.NewerThanDays, "90", false)]
    [InlineData(Attr.WhenCreated, FilterOperator.OlderThanDays, "365", true)]
    [InlineData(Attr.WhenCreated, FilterOperator.GreaterOrEqual, "2026-01-01", false)]
    [InlineData(Attr.PwdLastSet, FilterOperator.OlderThanDays, "1", false)]
    [InlineData(Attr.ObjectSid, FilterOperator.Equals, "S-1-5-21-1-2-3-1105", true)]
    [InlineData("", FilterOperator.MemberOf, "CN=GG_Ksiegowosc,OU=Grupy,{domain}", true)]
    [InlineData("", FilterOperator.NotMemberOf, "CN=GG_Ksiegowosc,OU=Grupy,{domain}", false)]
    public void Condition_FollowsLdapSemantics(string attribute, FilterOperator op, string value, bool expected) =>
        Assert.Equal(expected, FilterEvaluator.Matches(new FilterCondition(attribute, op, value), Grazyna, Context));

    [Fact]
    public void Definition_IAndLub_NestedMembershipThroughTheCallback()
    {
        var locked = new FilterCondition(Attr.UserAccountControlComputed, FilterOperator.BitSet, "16");
        var it = new FilterCondition(Attr.Department, FilterOperator.Equals, "IT");
        var admins = new FilterCondition(string.Empty, FilterOperator.MemberOf, "CN=Domain Admins,CN=Users,{domain}");

        Assert.False(FilterEvaluator.Matches(FilterDefinition.Of(it, locked), Grazyna, Context));
        Assert.True(FilterEvaluator.Matches(new FilterDefinition { Groups = [new FilterConditionGroup { Combinator = FilterCombinator.Any, Conditions = [it, locked] }] }, Grazyna, Context));
        Assert.True(FilterEvaluator.Matches(new FilterDefinition
        {
            Combinator = FilterCombinator.Any,
            Groups = [new FilterConditionGroup { Conditions = [it] }, new FilterConditionGroup { Conditions = [locked] }],
        }, Grazyna, Context));
        Assert.False(FilterEvaluator.Matches(FilterDefinition.Of(admins), Grazyna, Context));
        Assert.True(FilterEvaluator.Matches(FilterDefinition.Of(admins), Grazyna, Context, dn => dn.StartsWith("CN=Domain Admins", StringComparison.Ordinal)));
        Assert.True(FilterEvaluator.Matches(FilterDefinition.Raw("(x=y)"), Grazyna, Context));
        Assert.True(FilterEvaluator.Matches(new FilterDefinition(), Grazyna, Context));
    }

    private static string FileTime(DateTimeOffset value) => AdConvert.ToFileTime(value).ToString(CultureInfo.InvariantCulture);
}
