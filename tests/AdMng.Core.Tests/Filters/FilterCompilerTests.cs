using AdMng.Core.ActiveDirectory;
using AdMng.Core.Filters;
using AdMng.Core.Ldap;
using AdMng.Testing;

namespace AdMng.Core.Tests.Filters;

public class FilterCompilerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);
    private static readonly FilterContext Context = new()
    {
        Now = Now,
        DefaultNamingContext = "DC=contoso,DC=test",
        Schema = FakeSchema.Create("CN=Schema,CN=Configuration,DC=contoso,DC=test"),
    };

    [Theory]
    [InlineData("department", FilterOperator.Equals, "Księgowość", "(department=Księgowość)")]
    [InlineData("department", FilterOperator.NotEquals, "IT", "(!(department=IT))")]
    [InlineData("description", FilterOperator.Contains, "admin*(Łódź)", @"(description=*admin\2a\28Łódź\29*)")]
    [InlineData("sAMAccountName", FilterOperator.StartsWith, "adm_", "(sAMAccountName=adm_*)")]
    [InlineData("mail", FilterOperator.EndsWith, @"@żółw.pl\", @"(mail=*@żółw.pl\5c)")]
    [InlineData("manager", FilterOperator.Present, "", "(manager=*)")]
    [InlineData("mail", FilterOperator.NotPresent, "", "(!(mail=*))")]
    [InlineData("adminCount", FilterOperator.Equals, " 1 ", "(adminCount=1)")]
    [InlineData("logonCount", FilterOperator.GreaterOrEqual, "10", "(logonCount>=10)")]
    [InlineData("userAccountControl", FilterOperator.BitSet, "2", "(userAccountControl:1.2.840.113556.1.4.803:=2)")]
    [InlineData("userAccountControl", FilterOperator.BitNotSet, "0x10000", "(!(userAccountControl:1.2.840.113556.1.4.803:=65536))")]
    [InlineData("ignored", FilterOperator.MemberOf, "CN=GG_Księgowość (Kraków),OU=Grupy,{domain}",
        @"(memberOf:1.2.840.113556.1.4.1941:=CN=GG_Księgowość \28Kraków\29,OU=Grupy,DC=contoso,DC=test)")]
    [InlineData("", FilterOperator.NotMemberOf, "CN=Domain Admins,CN=Users,DC=contoso,DC=test",
        "(!(memberOf:1.2.840.113556.1.4.1941:=CN=Domain Admins,CN=Users,DC=contoso,DC=test))")]
    public void Condition_CompilesThroughLdapFilter_EveryValueEscaped(string attribute, FilterOperator op, string value, string expected)
    {
        var filter = FilterCompiler.Compile(new FilterCondition(attribute, op, value), Context);

        Assert.Equal(expected, filter);
        Assert.Null(LdapFilter.Validate(filter));
    }

    [Fact]
    public void Dates_OlderAndNewer_ForFileTimeAndGeneralizedTime()
    {
        var older = FilterCompiler.Compile(new FilterCondition(Attr.LastLogonTimestamp, FilterOperator.OlderThanDays, "90"), Context);
        Assert.Equal($"(&(lastLogonTimestamp>=1)(lastLogonTimestamp<={AdConvert.ToFileTime(Now.AddDays(-90))}))", older);

        var newer = FilterCompiler.Compile(new FilterCondition(Attr.PwdLastSet, FilterOperator.NewerThanDays, "7"), Context);
        Assert.Equal($"(&(pwdLastSet>={AdConvert.ToFileTime(Now.AddDays(-7))})(!(pwdLastSet=9223372036854775807)))", newer);

        var created = FilterCompiler.Compile(new FilterCondition(Attr.WhenCreated, FilterOperator.NewerThanDays, "30"), Context);
        Assert.Equal($"(whenCreated>={AdConvert.ToGeneralizedTime(Now.AddDays(-30))})", created);

        var since = FilterCompiler.Compile(new FilterCondition(Attr.WhenCreated, FilterOperator.LessOrEqual, "2026-01-31"), Context);
        Assert.Equal("(whenCreated<=20260131000000.0Z)", since);
        var expires = FilterCompiler.Compile(new FilterCondition(Attr.AccountExpires, FilterOperator.LessOrEqual, "2027-01-01"), Context);
        Assert.Equal($"(accountExpires<={AdConvert.ToFileTime(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero))})", expires);
    }

    [Fact]
    public void Groups_CombineWithIAndLub()
    {
        var definition = new FilterDefinition
        {
            Combinator = FilterCombinator.Any,
            Groups =
            [
                new FilterConditionGroup
                {
                    Conditions =
                    [
                        new FilterCondition("department", FilterOperator.Equals, "IT"),
                        new FilterCondition("title", FilterOperator.Contains, "administrator"),
                    ],
                },
                new FilterConditionGroup
                {
                    Combinator = FilterCombinator.Any,
                    Conditions =
                    [
                        new FilterCondition("employeeType", FilterOperator.Equals, "IT-Admin"),
                        new FilterCondition("description", FilterOperator.StartsWith, "Konto admin"),
                    ],
                },
            ],
        };

        Assert.Equal(
            "(|(&(department=IT)(title=*administrator*))(|(employeeType=IT-Admin)(description=Konto admin*)))",
            FilterCompiler.ToLdap(definition, Context));
        var plan = FilterCompiler.Plan(definition, Context);
        Assert.Equal(FilterCompiler.ToLdap(definition, Context), plan.ServerFilter);
        Assert.False(plan.NeedsClientCheck);
        Assert.Empty(plan.Attributes);
        Assert.Equal("(department=IT)", FilterCompiler.ToLdap(FilterDefinition.Of(new FilterCondition("department", FilterOperator.Equals, "IT")), Context));
    }

    [Fact]
    public void ConstructedAttributes_AreCheckedOnTheClient_TheServerGetsWhatItCanEvaluate()
    {
        var locked = new FilterCondition(Attr.UserAccountControlComputed, FilterOperator.BitSet, "16");
        var it = new FilterCondition("department", FilterOperator.Equals, "IT");

        // I: the server narrows down with the department, the client checks the lock.
        var all = FilterCompiler.Plan(FilterDefinition.Of(it, locked), Context);
        Assert.Equal("(department=IT)", all.ServerFilter);
        Assert.True(all.NeedsClientCheck);
        Assert.Equal(["department", Attr.UserAccountControlComputed], all.Attributes);

        // LUB: the server cannot narrow down at all.
        var any = FilterCompiler.Plan(new FilterDefinition { Groups = [new FilterConditionGroup { Combinator = FilterCombinator.Any, Conditions = [it, locked] }] }, Context);
        Assert.Null(any.ServerFilter);
        Assert.True(any.NeedsClientCheck);

        // The preview still shows the whole filter.
        Assert.Contains("msDS-User-Account-Control-Computed", FilterCompiler.ToLdap(FilterDefinition.Of(it, locked), Context), StringComparison.Ordinal);

        // Without a schema the known constructed attributes are recognised by name.
        var noSchema = Context with { Schema = null };
        Assert.True(FilterCompiler.IsConstructed(Attr.PasswordExpiryComputed, null));
        Assert.True(FilterCompiler.Plan(FilterDefinition.Of(locked), noSchema).NeedsClientCheck);
        Assert.Null(FilterCompiler.Plan(FilterDefinition.Of(locked), noSchema).ServerFilter);
    }

    [Fact]
    public void RawLdap_IsValidated_AndGoesToTheServerAsTyped()
    {
        var raw = FilterDefinition.Raw("objectClass=user");
        Assert.True(raw.IsRaw);
        Assert.Equal("(objectClass=user)", FilterCompiler.ToLdap(raw, Context));
        Assert.Equal(new FilterPlan("(objectClass=user)", false, []), FilterCompiler.Plan(raw, Context));
        Assert.Equal([new FilterIssue(FilterIssueCode.InvalidRawFilter)], FilterCompiler.Validate(FilterDefinition.Raw("(&(a=1)"), Context));
        Assert.Throws<FilterDefinitionException>(() => FilterCompiler.ToLdap(FilterDefinition.Raw("(&(a=1)"), Context));
    }

    [Fact]
    public void Validate_ReportsEveryBrokenCondition_WithItsPosition()
    {
        var definition = new FilterDefinition
        {
            Groups =
            [
                new FilterConditionGroup
                {
                    Conditions =
                    [
                        new FilterCondition("", FilterOperator.Equals, "x"),
                        new FilterCondition("dział (stary)", FilterOperator.Equals, "x"),
                        new FilterCondition("department", FilterOperator.Equals, "  "),
                        new FilterCondition("adminCount", FilterOperator.Equals, "tak"),
                    ],
                },
                new FilterConditionGroup
                {
                    Conditions =
                    [
                        new FilterCondition("lastLogonTimestamp", FilterOperator.OlderThanDays, "-3"),
                        new FilterCondition("department", FilterOperator.OlderThanDays, "3"),
                        new FilterCondition("userAccountControl", FilterOperator.BitSet, "0"),
                        new FilterCondition("x", FilterOperator.MemberOf, "nie DN"),
                        new FilterCondition("whenCreated", FilterOperator.GreaterOrEqual, "wczoraj"),
                        new FilterCondition("logonCount", FilterOperator.LessOrEqual, "dużo"),
                    ],
                },
            ],
        };

        Assert.Equal(
        [
            new FilterIssue(FilterIssueCode.MissingAttribute, 0, 0),
            new FilterIssue(FilterIssueCode.InvalidAttribute, 0, 1),
            new FilterIssue(FilterIssueCode.MissingValue, 0, 2),
            new FilterIssue(FilterIssueCode.NotANumber, 0, 3),
            new FilterIssue(FilterIssueCode.InvalidDays, 1, 0),
            new FilterIssue(FilterIssueCode.NotADate, 1, 1),
            new FilterIssue(FilterIssueCode.InvalidMask, 1, 2),
            new FilterIssue(FilterIssueCode.InvalidGroup, 1, 3),
            new FilterIssue(FilterIssueCode.NotADate, 1, 4),
            new FilterIssue(FilterIssueCode.NotANumber, 1, 5),
        ], FilterCompiler.Validate(definition, Context));
        Assert.Equal([new FilterIssue(FilterIssueCode.Empty)], FilterCompiler.Validate(new FilterDefinition(), Context));
        Assert.True(new FilterDefinition().IsEmpty);
    }

    [Fact]
    public void ValueKinds_FromTheSchema_OrKnownNames()
    {
        var schema = Context.Schema;
        Assert.Equal(FilterValueKind.FileTime, FilterCompiler.ValueKind(Attr.PwdLastSet, schema));
        Assert.Equal(FilterValueKind.GeneralizedTime, FilterCompiler.ValueKind(Attr.WhenCreated, schema));
        Assert.Equal(FilterValueKind.Integer, FilterCompiler.ValueKind(Attr.UserAccountControl, schema));
        Assert.Equal(FilterValueKind.DistinguishedName, FilterCompiler.ValueKind(Attr.Manager, schema));
        Assert.Equal(FilterValueKind.Text, FilterCompiler.ValueKind(Attr.Department, schema));
        Assert.Equal(FilterValueKind.FileTime, FilterCompiler.ValueKind(Attr.LastLogonTimestamp, null));
        Assert.Equal(FilterValueKind.GeneralizedTime, FilterCompiler.ValueKind(Attr.WhenChanged, null));
        Assert.Equal(FilterValueKind.Integer, FilterCompiler.ValueKind(Attr.AdminCount, null));
        Assert.Equal(65536L, FilterCompiler.ParseMask("0x10000"));
        Assert.Null(FilterCompiler.ParseMask("-2"));
        Assert.Equal("CN=X,DC=contoso,DC=test", FilterCompiler.ResolveDn("CN=X,{domain}", Context));
        Assert.Equal("CN=X,{domain}", FilterCompiler.ResolveDn("CN=X,{domain}", Context with { DefaultNamingContext = null }));
    }
}
