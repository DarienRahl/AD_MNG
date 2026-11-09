using AdMng.Core.Batch;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Testing;
using AdMng.Testing.BulkCreate;

namespace AdMng.Core.Tests.Batch.Creation;

/// <summary>Value rules of created objects (docs/dev/phase3-bulk-create.md 2.2 table, 2.11).</summary>
public sealed class CreationValuesTests
{
    private static readonly ISchemaCatalog Schema = FakeSchema.Create(bulkCreate: true);

    private static readonly CreationFinishOptions Defaults = new(SanitizeIdentifiers: true, TruncateNames: true, PhoneFormat.AsIs, IsCounterValue: false);

    private static FinishedValue Finish(ObjectKind kind, string attribute, string raw, CreationFinishOptions? options = null) =>
        CreationValues.Finish(kind, attribute, raw, options ?? Defaults);

    private static IReadOnlyList<ValidationIssue> Validate(ObjectKind kind, string attribute, params string[] values) =>
        CreationValues.Validate(kind, attribute, values, Schema);

    private static IReadOnlyList<string> Codes(IReadOnlyList<ValidationIssue> issues) => [.. issues.Select(i => i.Code)];

    [Fact]
    public void Finish_Cn_CollapsesWhiteSpace_AndCutsTo64()
    {
        var name = "Anna Maria " + new string('ż', 70);

        Assert.Equal(new FinishedValue("Łucja Żak", false, false), Finish(ObjectKind.User, "cn", "  Łucja \t Żak "));
        var cut = Finish(ObjectKind.User, "cn", name);
        Assert.Equal(64, cut.Value.Length);
        Assert.True(cut.Truncated);
        Assert.Equal(name, Finish(ObjectKind.User, "cn", name, Defaults with { IsCounterValue = true }).Value);
        Assert.Equal(name, Finish(ObjectKind.User, "cn", name, Defaults with { TruncateNames = false }).Value);
        Assert.Equal("PC-KSIEGOWOSC01", Finish(ObjectKind.Computer, "cn", "pc-ksiegowosc01").Value);
    }

    [Fact]
    public void Finish_UserLogonName_SanitizesAndCutsTo20()
    {
        Assert.Equal(new FinishedValue("lucja.zak", true, false), Finish(ObjectKind.User, "sAMAccountName", "łucja.żak"));
        Assert.Equal(new FinishedValue("kd201", false, false), Finish(ObjectKind.User, "sAMAccountName", "kd201"));
        Assert.Equal(new FinishedValue("akonstantynopolitanc", true, true), Finish(ObjectKind.User, "sAMAccountName", "akonstantynopolitańczykowa"));
        Assert.Equal("abcdefghijklmnopqrs", Finish(ObjectKind.User, "sAMAccountName", "abcdefghijklmnopqrs.tuvw").Value);
        Assert.Equal("akonstantynopolitanczykowa", Finish(ObjectKind.User, "sAMAccountName", "akonstantynopolitańczykowa", Defaults with { IsCounterValue = true }).Value);
        Assert.Equal("łucja żak", Finish(ObjectKind.User, "sAMAccountName", "łucja żak", Defaults with { SanitizeIdentifiers = false, TruncateNames = false }).Value);
    }

    [Fact]
    public void Finish_GroupLogonName_RemovesOnlyForbiddenCharacters()
    {
        Assert.Equal(new FinishedValue("Księgowość Kraków", true, false), Finish(ObjectKind.Group, "sAMAccountName", "Księgowość / Kraków"));
        Assert.Equal("KsięgowośćKraków", Finish(ObjectKind.Group, "sAMAccountName", "Księgowość/Kraków").Value);
        Assert.Equal(new string('g', 256), Finish(ObjectKind.Group, "sAMAccountName", new string('g', 300)).Value);
    }

    [Theory]
    [InlineData("pc01", "PC01$")]
    [InlineData("PC01$", "PC01$")]
    [InlineData("pc01$$", "PC01$$")]
    [InlineData("komputer-ksiegowosc", "KOMPUTER-KSIEGO$")]
    [InlineData("komputer-księgowość$", "KOMPUTER-KSIEGO$")]
    [InlineData("", "")]
    public void Finish_ComputerLogonName_UpperCaseOneDollarAndCut(string raw, string expected) =>
        Assert.Equal(expected, Finish(ObjectKind.Computer, "sAMAccountName", raw).Value);

    [Fact]
    public void Finish_Computer_RulesApplyWithoutSanitizing_AndACounterPartGetsNoDollar()
    {
        var columnValue = Defaults with { SanitizeIdentifiers = false, TruncateNames = false };

        Assert.Equal("PC01$", Finish(ObjectKind.Computer, "sAMAccountName", "pc01", columnValue).Value);
        Assert.Equal("PC", Finish(ObjectKind.Computer, "sAMAccountName", "pc", Defaults with { IsCounterValue = true }).Value);
        Assert.Equal("KOMPUTER-KSIEGOWOSC-0001$", Finish(ObjectKind.Computer, "sAMAccountName", "komputer-ksiegowosc-0001$", Defaults with { IsCounterValue = true }).Value);
    }

    [Fact]
    public void Finish_Addresses_SanitizeTheLocalPart_LowerTheDomain_NeverCut()
    {
        var mail = Finish(ObjectKind.User, "mail", "grzegorz.brzęczyszczykiewicz@Firma.PL");
        var upn = Finish(ObjectKind.User, "userPrincipalName", "Łucja.Żak@Contoso.COM");
        var prefixOnly = Finish(ObjectKind.User, "userPrincipalName", "łucja.żak");
        var longLocal = Finish(ObjectKind.User, "mail", "anna.konstantynopolitańczykowa-brzęczyszczykiewicz-wiśniewska-ślęzak@firma.pl");

        Assert.Equal(new FinishedValue("grzegorz.brzeczyszczykiewicz@firma.pl", true, false), mail);
        Assert.Equal("Lucja.Zak@contoso.com", upn.Value);
        Assert.Equal("lucja.zak", prefixOnly.Value);
        Assert.Equal("anna.konstantynopolitanczykowa-brzeczyszczykiewicz-wisniewska-slezak@firma.pl", longLocal.Value);
        Assert.False(longLocal.Truncated);
        Assert.Equal("kd201@contoso.com", Finish(ObjectKind.User, "mail", "kd201@contoso.com").Value);
        Assert.False(Finish(ObjectKind.User, "mail", "kd201@contoso.com").Sanitized);
    }

    [Fact]
    public void Finish_Addresses_ASanitizedValuePassesValidate()
    {
        var mail = Finish(ObjectKind.User, "mail", "Kowalski (Jan)@Contoso.com").Value;
        var upn = Finish(ObjectKind.User, "userPrincipalName", "kowalski(jan)@contoso.com").Value;

        Assert.Equal("KowalskiJan@contoso.com", mail);
        Assert.Empty(Validate(ObjectKind.User, "mail", mail));
        Assert.Equal("kowalskijan@contoso.com", upn);
        Assert.Empty(Validate(ObjectKind.User, "userPrincipalName", upn));
    }

    /// <summary>
    /// The parts around the counter are finished separately and joined with the number (2.4, as the planner does): the end of
    /// a part where the number goes keeps its space or dot (review of B1 – <c>Praktykant 01</c> became <c>Praktykant01</c>,
    /// <c>kd.201</c> became <c>kd201</c>).
    /// </summary>
    [Theory]
    [InlineData(ObjectKind.User, "cn", "Praktykant ", "", "Praktykant 01")]
    [InlineData(ObjectKind.User, "cn", "Sala  ", "  WAW", "Sala 01 WAW")]
    [InlineData(ObjectKind.User, "displayName", "Praktykant ", "", "Praktykant 01")]
    [InlineData(ObjectKind.User, "description", "Konto nr ", " (staż)", "Konto nr 01 (staż)")]
    [InlineData(ObjectKind.User, "sAMAccountName", "kd.", "", "kd.01")]
    [InlineData(ObjectKind.User, "sAMAccountName", "praktykant.", ".waw", "praktykant.01.waw")]
    [InlineData(ObjectKind.User, "sAMAccountName", "Praktykant ", "", "Praktykant01")]
    [InlineData(ObjectKind.Group, "sAMAccountName", "Projekt ", " Kraków", "Projekt 01 Kraków")]
    [InlineData(ObjectKind.User, "mail", "jan.", "@Contoso.COM", "jan.01@contoso.com")]
    [InlineData(ObjectKind.User, "mail", "info@sklep", ".pl", "info@sklep01.pl")]
    [InlineData(ObjectKind.User, "userPrincipalName", "praktykant.", "@contoso.com", "praktykant.01@contoso.com")]
    public void Finish_CounterParts_KeepTheJunctionWithTheNumber(ObjectKind kind, string attribute, string prefix, string suffix, string expected)
    {
        var counter = Defaults with { IsCounterValue = true };

        var joined = Finish(kind, attribute, prefix, counter).Value + "01" + Finish(kind, attribute, suffix, counter).Value;

        Assert.Equal(expected, joined);
    }

    [Fact]
    public void Validate_WhiteSpaceAtTheEnds_OfASingleLineValue_IsAnError()
    {
        // A counter rule with a stray space (" Sala {$nr}") keeps it, since counter parts keep their ends.
        var counter = Defaults with { IsCounterValue = true };
        var cn = Finish(ObjectKind.User, "cn", " Sala ", counter).Value + "01";

        Assert.Equal(" Sala 01", cn);
        Assert.Equal([IssueCodes.InvalidValue], Codes(Validate(ObjectKind.User, "cn", cn)));
        Assert.Equal([IssueCodes.InvalidValue], Codes(Validate(ObjectKind.User, "displayName", "Praktykant 01 ")));
        Assert.Empty(Validate(ObjectKind.User, "cn", "Sala 01"));
        Assert.Empty(Validate(ObjectKind.User, "streetAddress", "ul. Kwiatowa 5\r\n00-950 Warszawa"));
    }

    [Fact]
    public void Finish_CounterParts_OfAPhone_AreNotFormattedOnTheirOwn()
    {
        var counter = Defaults with { Phones = PhoneFormat.International, IsCounterValue = true };

        // ipPhone "+48 22 555 01 {$nr:10}": formatting the prefix alone regrouped it to "+4822555001".
        var joined = Finish(ObjectKind.User, "ipPhone", "+48 22 555 01 ", counter).Value + "10";

        Assert.Equal("+48 22 555 01 10", joined);
        Assert.Empty(CreationValues.Validate(ObjectKind.User, "ipPhone", [joined], Schema, PhoneFormat.International, columnCase: null));
    }

    [Fact]
    public void Finish_PhonesHomeDriveAndOtherAttributes()
    {
        var international = Defaults with { Phones = PhoneFormat.International };

        Assert.Equal("+48 600 100 200", Finish(ObjectKind.User, "mobile", "600 100 200", international).Value);
        Assert.Equal("600 100 200", Finish(ObjectKind.User, "mobile", "600 100 200").Value);
        Assert.Equal("+48 22 555 01 01", Finish(ObjectKind.Contact, "telephoneNumber", "22 555 01 01", international).Value);
        Assert.Equal("H:", Finish(ObjectKind.User, "homeDrive", "h:").Value);
        Assert.Equal(@"\\fs01\home$\kd201", Finish(ObjectKind.User, "homeDirectory", @"\\fs01\home$\kd201").Value);
        Assert.Equal("Konto utworzone 2026-10-13 – zlecenie", Finish(ObjectKind.User, "description", "Konto utworzone  2026-10-13 –  zlecenie ").Value);
        Assert.Equal("ul. Kwiatowa 5\r\n00-950 Warszawa", Finish(ObjectKind.User, "streetAddress", " ul. Kwiatowa 5\r\n00-950 Warszawa ").Value);
        Assert.Equal("Łucja Żak", Finish(ObjectKind.User, "displayName", "Łucja  Żak").Value);
    }

    [Fact]
    public void Validate_Cn()
    {
        Assert.Empty(Validate(ObjectKind.User, "cn", "Łucja Żak"));
        Assert.Equal([IssueCodes.InvalidValue], Codes(Validate(ObjectKind.User, "cn", " ")));
        Assert.Equal([IssueCodes.TooLong], Codes(Validate(ObjectKind.User, "cn", new string('a', 65))));
        Assert.Empty(Validate(ObjectKind.Computer, "cn", "PC01"));
        Assert.Equal([IssueCodes.InvalidValue], Codes(Validate(ObjectKind.Computer, "cn", "PC.01")));
        Assert.Equal([IssueCodes.InvalidValue], Codes(Validate(ObjectKind.Computer, "cn", "KOMPUTER-KSIEGOWOSC")));
        var underscore = Assert.Single(Validate(ObjectKind.Computer, "cn", "PC_01"));
        Assert.Equal((BulkCreateIssueCodes.ComputerNameNotDnsSafe, IssueSeverity.Warning), (underscore.Code, underscore.Severity));
    }

    [Theory]
    [InlineData(ObjectKind.User, "kd201")]
    [InlineData(ObjectKind.User, "lucja.zak")]
    [InlineData(ObjectKind.Computer, "PC01$")]
    [InlineData(ObjectKind.Group, "Księgowość Kraków")]
    [InlineData(ObjectKind.Contact, "anything")]
    public void Validate_LogonName_Valid(ObjectKind kind, string value) => Assert.Empty(Validate(kind, "sAMAccountName", value));

    [Theory]
    [InlineData(ObjectKind.User, "kd201$", BulkCreateIssueCodes.TrailingDollar)]
    [InlineData(ObjectKind.Computer, "PC01", BulkCreateIssueCodes.TrailingDollar)]
    [InlineData(ObjectKind.Computer, "PC01$$", BulkCreateIssueCodes.TrailingDollar)]
    [InlineData(ObjectKind.Computer, "PC$01$", BulkCreateIssueCodes.TrailingDollar)]
    [InlineData(ObjectKind.User, "akonstantynopolitancz", IssueCodes.TooLong)]
    [InlineData(ObjectKind.Computer, "KOMPUTER-KSIEGOW$", IssueCodes.TooLong)]
    [InlineData(ObjectKind.User, "jan@kowalski", IssueCodes.InvalidValue)]
    [InlineData(ObjectKind.User, "...", IssueCodes.InvalidValue)]
    [InlineData(ObjectKind.User, "jan.", IssueCodes.InvalidValue)]
    [InlineData(ObjectKind.Computer, "PC.$", IssueCodes.InvalidValue)]
    [InlineData(ObjectKind.User, "", IssueCodes.InvalidValue)]
    public void Validate_LogonName_Errors(ObjectKind kind, string value, string code)
    {
        var issue = Assert.Single(Validate(kind, "sAMAccountName", value), i => i.Code == code);

        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Equal("sAMAccountName", issue.Attribute);
    }

    [Fact]
    public void Validate_GroupLogonName_UpTo256() =>
        Assert.Equal([IssueCodes.TooLong], Codes(Validate(ObjectKind.Group, "sAMAccountName", new string('g', 257))));

    [Theory]
    [InlineData("kd201@contoso.com", null)]
    [InlineData("łucja.żak@contoso.com", null)]
    [InlineData("jan.kowalski@firma.pl", null)]
    [InlineData("kd201", IssueCodes.InvalidValue)]
    [InlineData("@contoso.com", IssueCodes.InvalidValue)]
    [InlineData("kd201@", IssueCodes.InvalidValue)]
    [InlineData("jan kowalski@contoso.com", IssueCodes.InvalidValue)]
    [InlineData("a@b@contoso.com", IssueCodes.InvalidValue)]
    [InlineData("kd201@contoso..com", IssueCodes.InvalidValue)]
    [InlineData("kd201@contoso com", IssueCodes.InvalidValue)]
    public void Validate_Upn(string value, string? code) => Assert.Equal(code is null ? [] : [code], Codes(Validate(ObjectKind.User, "userPrincipalName", value)));

    [Fact]
    public void Validate_Upn_TooLong_IsAnErrorNotACut() =>
        Assert.Equal([IssueCodes.TooLong], Codes(Validate(ObjectKind.User, "userPrincipalName", new string('a', 1020) + "@contoso.com")));

    [Fact]
    public void Validate_Mail()
    {
        Assert.Empty(Validate(ObjectKind.User, "mail", "grzegorz.brzeczyszczykiewicz@firma.pl"));
        Assert.Equal([IssueCodes.InvalidValue], Codes(Validate(ObjectKind.User, "mail", "Łucja Żak <lucja.zak@contoso.com>")));
        Assert.Equal([IssueCodes.InvalidValue], Codes(Validate(ObjectKind.User, "mail", "lucja.zak")));
        Assert.Equal([IssueCodes.TooLong], Codes(Validate(ObjectKind.User, "mail", new string('a', 65) + "@firma.pl")));
        Assert.Empty(Validate(ObjectKind.User, "mail", new string('a', 64) + "@firma.pl"));
        Assert.Equal([IssueCodes.TooLong], Codes(Validate(ObjectKind.Contact, "mail", "a@" + new string('b', 260) + ".pl")));
    }

    [Fact]
    public void Validate_Description_IsSingleValuedForEveryKind()
    {
        Assert.True(Schema.FindAttribute("description")!.IsSingleValued is false);
        foreach (var kind in new[] { ObjectKind.User, ObjectKind.Group, ObjectKind.Computer, ObjectKind.Contact })
        {
            Assert.Equal([IssueCodes.SingleValued], Codes(Validate(kind, "description", "Konto testowe", "drugi opis")));
        }

        Assert.Empty(Validate(ObjectKind.User, "description", "Konto testowe"));
    }

    [Fact]
    public void Validate_ValueCountsFromTheSchema()
    {
        Assert.Equal([IssueCodes.SingleValued], Codes(Validate(ObjectKind.User, "mobile", "+48 600 100 201", "+48 600 100 202")));
        Assert.Empty(Validate(ObjectKind.User, "otherMobile", "+48 600 100 201", "+48 600 100 202"));
        Assert.Empty(CreationValues.Validate(ObjectKind.User, "otherMobile", ["+48 600 100 201", "+48 600 100 202"], SchemaCatalog.Empty));
        Assert.Equal([IssueCodes.SingleValued], Codes(CreationValues.Validate(ObjectKind.User, "title", ["a", "b"], SchemaCatalog.Empty)));
    }

    [Fact]
    public void Validate_LineBreaks_OnlyInMultiLineAttributes()
    {
        var altEnter = Paste("Imię\tNazwisko\r\n\"Ewa\nKatarzyna\"\tWiśniewska\r\n").Cell(0, 0);

        Assert.Equal("Ewa\r\nKatarzyna", altEnter);
        var issue = Assert.Single(Validate(ObjectKind.User, "givenName", altEnter));
        Assert.Equal((BulkCreateIssueCodes.LineBreakInValue, IssueSeverity.Error), (issue.Code, issue.Severity));
        Assert.Equal([BulkCreateIssueCodes.LineBreakInValue], Codes(Validate(ObjectKind.User, "description", "a\tb")));
        Assert.Empty(Validate(ObjectKind.User, "streetAddress", "ul. Kwiatowa 5\r\n00-950 Warszawa"));
        Assert.Empty(Validate(ObjectKind.User, "info", "praktyki\r\nod 1 grudnia"));
        Assert.Equal([IssueCodes.InvalidValue], Codes(Validate(ObjectKind.User, "info", "a\u0001b")));
    }

    [Fact]
    public void Validate_Phones()
    {
        var scientific = Assert.Single(Validate(ObjectKind.User, "mobile", "4,86E+11"));
        var notFormatted = CreationValues.Validate(ObjectKind.User, "mobile", ["600 100 200 wew. 12"], Schema, PhoneFormat.International, columnCase: null);

        Assert.Equal((BulkCreateIssueCodes.ScientificNotation, IssueSeverity.Warning), (scientific.Code, scientific.Severity));
        Assert.Equal([BulkCreateIssueCodes.InvalidPhone], Codes(notFormatted));
        Assert.Equal(IssueSeverity.Warning, notFormatted[0].Severity);
        Assert.Empty(Validate(ObjectKind.User, "mobile", "600 100 200 wew. 12"));
        Assert.Empty(CreationValues.Validate(ObjectKind.User, "mobile", ["+48 600 100 200"], Schema, PhoneFormat.International, columnCase: null));
        Assert.Equal([BulkCreateIssueCodes.ScientificNotation],
            Codes(CreationValues.Validate(ObjectKind.User, "telephoneNumber", ["4,86E+11"], Schema, PhoneFormat.International, columnCase: null)));
    }

    [Theory]
    [InlineData("homeDrive", "H:", false)]
    [InlineData("homeDrive", "h:", false)]
    [InlineData("homeDrive", "H", true)]
    [InlineData("homeDrive", "HH:", true)]
    [InlineData("homeDirectory", @"\\fs01\home$\kd201", false)]
    [InlineData("homeDirectory", @"H:\home\kd201", false)]
    [InlineData("homeDirectory", @"\\fs01", true)]
    [InlineData("homeDirectory", "home/kd201", true)]
    public void Validate_HomeFolder_WarnsOnly(string attribute, string value, bool warned)
    {
        var issues = Validate(ObjectKind.User, attribute, value);

        Assert.Equal(warned, issues.Any(i => i is { Code: IssueCodes.InvalidValue, Severity: IssueSeverity.Warning }));
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Validate_AllCapsNames_OnlyFromAColumnAsIs()
    {
        Assert.Equal([BulkCreateIssueCodes.AllCapsName], Codes(CreationValues.Validate(ObjectKind.User, "sn", ["KOWALSKI"], Schema, PhoneFormat.AsIs, TextCase.AsIs)));
        Assert.Equal([BulkCreateIssueCodes.AllCapsName], Codes(CreationValues.Validate(ObjectKind.Contact, "displayName", ["JAN KOWALSKI"], Schema, PhoneFormat.AsIs, TextCase.AsIs)));
        Assert.Empty(CreationValues.Validate(ObjectKind.User, "sn", ["KOWALSKI"], Schema, PhoneFormat.AsIs, TextCase.Title));
        Assert.Empty(CreationValues.Validate(ObjectKind.User, "sn", ["KOWALSKI"], Schema, PhoneFormat.AsIs, columnCase: null));
        Assert.Empty(CreationValues.Validate(ObjectKind.User, "department", ["IT"], Schema, PhoneFormat.AsIs, TextCase.AsIs));
    }

    [Fact]
    public void Validate_SchemaRangeAndSyntax_WithoutTheValueInTheMessage()
    {
        var initials = Assert.Single(Validate(ObjectKind.User, "initials", "ŁŻĆŚŹĘĄ"));
        var badge = Assert.Single(Validate(ObjectKind.User, FakeSchema.CustomBadgeAttribute, new string('7', 33)));

        Assert.Equal(IssueCodes.TooLong, initials.Code);
        Assert.Equal(IssueCodes.TooLong, badge.Code);
        Assert.DoesNotContain("ŁŻĆ", initials.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("7777", badge.Message, StringComparison.Ordinal);
        Assert.Empty(Validate(ObjectKind.User, "employeeID", "E-1001"));
        Assert.Equal([IssueCodes.TooLong], Codes(Validate(ObjectKind.User, "employeeID", "E-1001-2002-3003-4004")));
    }

    [Fact]
    public void Validate_Messages_NeverHoldAValue()
    {
        var sentinel = BulkCreateRecordSamples.Sentinels;
        var issues = new[] { "cn", "sAMAccountName", "userPrincipalName", "mail", "description", "givenName", "mobile", "homeDrive", "homeDirectory" }
            .SelectMany(a => CreationValues.Validate(ObjectKind.User, a, [.. sentinel.Select(s => s + "\r\n$@@")], Schema, PhoneFormat.International, TextCase.AsIs))
            .ToList();

        Assert.NotEmpty(issues);
        Assert.All(issues, i => Assert.DoesNotContain(sentinel, s => i.Message.Contains(s, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Validate_ReportsEachCodeOncePerAttribute() =>
        Assert.Single(Validate(ObjectKind.User, "givenName", "a\r\nb", "c\r\nd"), i => i.Code == BulkCreateIssueCodes.LineBreakInValue);

    [Theory]
    [InlineData("proxyAddresses")]
    [InlineData("msExchMailboxGuid")]
    [InlineData("lockoutTime")]
    [InlineData("c")]
    [InlineData("co")]
    [InlineData("countryCode")]
    [InlineData("manager")]
    [InlineData("msDS-PSOApplied")]
    [InlineData("whenCreated")]
    [InlineData("canonicalName")]
    [InlineData("directReports")]
    [InlineData("thumbnailPhoto")]
    [InlineData("objectSid")]
    [InlineData("operatingSystem")]
    [InlineData("nieistniejacyAtrybut")]
    [InlineData("")]
    public void Blocked_ForUsers(string attribute) => Assert.True(CreationValues.IsBlockedAttribute(attribute, ObjectKind.User, Schema), attribute);

    [Theory]
    [InlineData("cn")]
    [InlineData("givenName")]
    [InlineData("sn")]
    [InlineData("mail")]
    [InlineData("mobile")]
    [InlineData("userPrincipalName")]
    [InlineData("sAMAccountName")]
    [InlineData("extensionAttribute5")]
    [InlineData(FakeSchema.CustomBadgeAttribute)]
    [InlineData("homeDirectory")]
    [InlineData("description")]
    public void Allowed_ForUsers(string attribute) => Assert.False(CreationValues.IsBlockedAttribute(attribute, ObjectKind.User, Schema), attribute);

    [Fact]
    public void Blocked_PerKind()
    {
        Assert.False(CreationValues.IsBlockedAttribute("operatingSystem", ObjectKind.Computer, Schema));
        Assert.False(CreationValues.IsBlockedAttribute("location", ObjectKind.Computer, Schema));
        Assert.True(CreationValues.IsBlockedAttribute(FakeSchema.CustomBadgeAttribute, ObjectKind.Contact, Schema));
        Assert.True(CreationValues.IsBlockedAttribute("sAMAccountName", ObjectKind.Contact, Schema));
        Assert.False(CreationValues.IsBlockedAttribute("extensionAttribute5", ObjectKind.Group, Schema));
        Assert.True(CreationValues.IsBlockedAttribute("groupType", ObjectKind.Group, Schema));
        Assert.True(CreationValues.IsBlockedAttribute("managedBy", ObjectKind.Group, Schema));
        Assert.True(CreationValues.IsBlockedAttribute("member", ObjectKind.Group, Schema));
    }

    [Fact]
    public void Blocked_SecretAttributes()
    {
        var schema = new SchemaCatalog(
            [
                new AttributeDefinition { LdapName = "cn", Syntax = AttributeSyntax.UnicodeString, IsSingleValued = true },
                new AttributeDefinition { LdapName = "ms-Mcs-AdmPwd", Syntax = AttributeSyntax.UnicodeString, IsSingleValued = true },
                new AttributeDefinition { LdapName = "contosoSecret", Syntax = AttributeSyntax.UnicodeString, IsConfidential = true },
                new AttributeDefinition { LdapName = "contosoPlain", Syntax = AttributeSyntax.UnicodeString },
            ],
            [new ClassDefinition { LdapName = "computer", MayContain = ["cn", "ms-Mcs-AdmPwd", "contosoSecret", "contosoPlain"] }]);

        Assert.True(CreationValues.IsBlockedAttribute("ms-Mcs-AdmPwd", ObjectKind.Computer, schema));
        Assert.True(CreationValues.IsBlockedAttribute("contosoSecret", ObjectKind.Computer, schema));
        Assert.False(CreationValues.IsBlockedAttribute("contosoPlain", ObjectKind.Computer, schema));
    }

    [Fact]
    public void WithoutSchema_OnlyTheKnownAttributes()
    {
        var none = SchemaCatalog.Empty;

        Assert.False(CreationValues.IsBlockedAttribute("mobile", ObjectKind.User, none));
        Assert.False(CreationValues.IsBlockedAttribute("employeeNumber", ObjectKind.User, none));
        Assert.False(CreationValues.IsBlockedAttribute("pager", ObjectKind.User, none));
        Assert.True(CreationValues.IsBlockedAttribute("extensionAttribute5", ObjectKind.User, none));
        Assert.True(CreationValues.IsBlockedAttribute("sAMAccountName", ObjectKind.Contact, none));
        Assert.True(CreationValues.IsBlockedAttribute("proxyAddresses", ObjectKind.User, none));
        Assert.False(CreationValues.IsBlockedAttribute("cn", ObjectKind.Group, none));
        Assert.True(CreationValues.IsBlockedAttribute("givenName", ObjectKind.Group, none));
    }

    private static InputTable Paste(string text) => TableTextReader.ReadClipboard(text, null, null, HeaderMode.Auto);
}
