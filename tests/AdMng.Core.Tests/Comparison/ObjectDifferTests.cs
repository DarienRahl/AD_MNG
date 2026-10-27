using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Comparison;
using AdMng.Core.Schema;
using static AdMng.Core.Tests.Comparison.ComparisonTestData;

namespace AdMng.Core.Tests.Comparison;

public class ObjectDifferTests
{
    [Fact]
    public void MultiValuedAttributes_AreComparedAsSets()
    {
        var a = User(Contoso, LukaszIn(ContosoNc), (Attr.OtherTelephone, ["+48 12 111 11 11", "+48 12 222 22 22"]));
        var b = User(Fabrikam, LukaszIn(FabrikamNc), (Attr.OtherTelephone, ["+48 12 222 22 22", "+48 12 111 11 11"]));

        var row = Row(ObjectDiffer.Compare(a, b), Attr.OtherTelephone);

        Assert.Equal(AttributeComparisonState.Same, row.State);
        Assert.False(row.IsDifference);
        Assert.Equal(2, row.CommonCount);
        Assert.All(row.ValuesA.Concat(row.ValuesB), v => Assert.Equal(ValueSide.Both, v.Side));
    }

    [Fact]
    public void DifferentStrings_AreDifferent_WithPerValueMarks()
    {
        var a = User(Contoso, LukaszIn(ContosoNc), (Attr.City, ["Kraków"]), (Attr.Description, ["Zespół ąęł", "Projekt Źródło"]));
        var b = User(Fabrikam, LukaszIn(FabrikamNc), (Attr.City, ["Wrocław"]), (Attr.Description, ["Projekt Źródło", "Zespół Żółw"]));

        var rows = ObjectDiffer.Compare(a, b);

        var city = Row(rows, Attr.City);
        Assert.Equal(AttributeComparisonState.Different, city.State);
        Assert.False(city.IsCaseOnly);
        Assert.Equal(ValueSide.OnlyA, Assert.Single(city.ValuesA).Side);
        Assert.Equal(ValueSide.OnlyB, Assert.Single(city.ValuesB).Side);

        var description = Row(rows, Attr.Description);
        Assert.Equal(AttributeComparisonState.Different, description.State);
        Assert.Equal((1, 1, 1), (description.CommonCount, description.OnlyACount, description.OnlyBCount));
        Assert.Equal(ValueSide.Both, description.ValuesA.Single(v => (string)v.Value == "Projekt Źródło").Side);
        Assert.Equal(ValueSide.OnlyA, description.ValuesA.Single(v => (string)v.Value == "Zespół ąęł").Side);
        Assert.Equal(ValueSide.OnlyB, description.ValuesB.Single(v => (string)v.Value == "Zespół Żółw").Side);
    }

    [Fact]
    public void ACaseOnlyDifference_IsADifference_MarkedCaseOnly()
    {
        var a = User(Contoso, LukaszIn(ContosoNc), (Attr.Title, ["Starszy Programista"]));
        var b = User(Fabrikam, LukaszIn(FabrikamNc), (Attr.Title, ["starszy programista"]));

        var row = Row(ObjectDiffer.Compare(a, b), Attr.Title);

        Assert.Equal(AttributeComparisonState.Different, row.State);
        Assert.True(row.IsCaseOnly);
        Assert.True(row.IsDifference);
    }

    [Fact]
    public void DnValues_AreComparedInTheirPortableForm_AcrossNamingContexts()
    {
        var a = User(Contoso, LukaszIn(ContosoNc), (Attr.Manager, [JanIn(ContosoNc)]), ("secretary", [JanIn(ContosoNc)]));
        var b = User(Fabrikam, LukaszIn(FabrikamNc),
            (Attr.Manager, [JanIn(FabrikamNc).ToUpperInvariant()]),
            ("secretary", [$"CN=Paweł Lewandowski,OU=Sprzedaż,OU=Pracownicy,OU=Firma,{FabrikamNc}"]));

        var rows = ObjectDiffer.Compare(a, b);

        var manager = Row(rows, Attr.Manager);
        Assert.Equal(AttributeComparisonState.Same, manager.State);
        Assert.True(manager.IsDnValued);
        Assert.Equal(AttributeComparisonState.Different, Row(rows, "secretary").State);
    }

    [Fact]
    public void DnBinaryAndDnStringValues_KeepTheirPrefix_AndNormalizeTheDn()
    {
        var dnString = new AttributeDefinition { LdapName = "msTestDnString", Syntax = AttributeSyntax.DnWithString, IsSingleValued = false };
        var schema = SchemaWith(null, dnString);
        var a = Snapshot(Contoso, schema, LukaszIn(ContosoNc), ["person", "organizationalPerson", ObjectClasses.User],
            ("otherWellKnownObjects", [$"B:32:1EB93889E40C45DF9F0C64D23BBB6237:CN=Managed Service Accounts,{ContosoNc}", $"B:32:6227F0AF1FC2410D8E3BB10615BB5B0F:CN=NTDS Quotas,{ContosoNc}"]),
            ("msTestDnString", [$"S:6:Opis:a:{JanIn(ContosoNc)}"]));
        var b = Snapshot(Fabrikam, schema, LukaszIn(FabrikamNc), ["person", "organizationalPerson", ObjectClasses.User],
            ("otherWellKnownObjects", [$"B:32:1EB93889E40C45DF9F0C64D23BBB6237:CN=Managed Service Accounts,{FabrikamNc}", $"B:32:00000000000000000000000000000000:CN=NTDS Quotas,{FabrikamNc}"]),
            ("msTestDnString", [$"S:6:Opis:a:{JanIn(FabrikamNc)}"]));

        var rows = ObjectDiffer.Compare(a, b);

        var binary = Row(rows, "otherWellKnownObjects");
        Assert.Equal(AttributeComparisonState.Different, binary.State);
        Assert.Equal((1, 1, 1), (binary.CommonCount, binary.OnlyACount, binary.OnlyBCount));
        Assert.Equal(AttributeComparisonState.Same, Row(rows, "msTestDnString").State);
    }

    [Fact]
    public void Addresses_WithoutDomain_AreTheSameInAnotherDomain_UnlessSwitchedOff()
    {
        var a = User(Contoso, LukaszIn(ContosoNc),
            (Attr.Mail, ["lukasz.wisniewski@contoso.test"]),
            (Attr.UserPrincipalName, ["lukasz.wisniewski@contoso.test"]),
            (Attr.ProxyAddresses, ["SMTP:lukasz.wisniewski@contoso.test", "smtp:l.wisniewski@contoso.test"]));
        var b = User(Fabrikam, LukaszIn(FabrikamNc),
            (Attr.Mail, ["lukasz.wisniewski@fabrikam.test"]),
            (Attr.UserPrincipalName, ["Lukasz.Wisniewski@fabrikam.test"]),
            (Attr.ProxyAddresses, ["smtp:lukasz.wisniewski@fabrikam.test", "SMTP:l.wisniewski@fabrikam.test"]));

        var rows = ObjectDiffer.Compare(a, b);

        var mail = Row(rows, Attr.Mail);
        Assert.Equal(AttributeComparisonState.SameOtherDomain, mail.State);
        Assert.False(mail.IsDifference);
        var upn = Row(rows, Attr.UserPrincipalName);
        Assert.Equal(AttributeComparisonState.Different, upn.State);
        Assert.True(upn.IsCaseOnly);

        // The primary address (SMTP:) is another one on side B: a real difference, not a matter of letter case.
        Assert.Equal(AttributeComparisonState.Different, Row(rows, Attr.ProxyAddresses).State);
        Assert.False(Row(rows, Attr.ProxyAddresses).IsCaseOnly);

        var strict = ObjectDiffer.Compare(a, b, new ComparisonOptions { CompareAddressesWithoutDomain = false });
        Assert.Equal(AttributeComparisonState.Different, Row(strict, Attr.Mail).State);
        Assert.False(Row(strict, Attr.Mail).IsCaseOnly);
    }

    [Fact]
    public void Addresses_WithoutDomain_AreCountedPerKey_SoAnExtraRoutingAddressIsADifference()
    {
        var a = User(Contoso, LukaszIn(ContosoNc),
            (Attr.ProxyAddresses, ["SMTP:jan.wójcik@contoso.com", "smtp:jan.wójcik@contoso.mail.onmicrosoft.com", "smtp:jan.wójcik@contoso.onmicrosoft.com"]));
        var b = User(Fabrikam, LukaszIn(FabrikamNc),
            (Attr.ProxyAddresses, ["SMTP:jan.wójcik@fabrikam.com", "smtp:jan.wójcik@fabrikam.onmicrosoft.com"]));

        var row = Row(ObjectDiffer.Compare(a, b), Attr.ProxyAddresses);
        var reversed = Row(ObjectDiffer.Compare(b, a), Attr.ProxyAddresses);

        Assert.Equal(AttributeComparisonState.Different, row.State);
        Assert.True(row.IsDifference);
        Assert.False(row.IsCaseOnly);
        Assert.Equal((2, 1, 0), (row.CommonCount, row.OnlyACount, row.OnlyBCount));
        Assert.Single(row.ValuesA, v => v.Side == ValueSide.OnlyA);
        Assert.All(row.ValuesB, v => Assert.Equal(ValueSide.Both, v.Side));
        Assert.Equal((2, 0, 1), (reversed.CommonCount, reversed.OnlyACount, reversed.OnlyBCount));
        Assert.Single(reversed.ValuesB, v => v.Side == ValueSide.OnlyB);

        // As many addresses per local part on both sides: the same, in another domain.
        var c = User(Fabrikam, LukaszIn(FabrikamNc),
            (Attr.ProxyAddresses, ["SMTP:jan.wójcik@fabrikam.com", "smtp:jan.wójcik@fabrikam.mail.onmicrosoft.com", "smtp:jan.wójcik@fabrikam.onmicrosoft.com"]));
        Assert.Equal(AttributeComparisonState.SameOtherDomain, Row(ObjectDiffer.Compare(a, c), Attr.ProxyAddresses).State);
    }

    [Fact]
    public void Times_AreComparedAsInstants()
    {
        var a = User(Contoso, LukaszIn(ContosoNc),
            (Attr.WhenCreated, ["20261013120000.0Z"]),
            (Attr.AccountExpires, ["0"]),
            (Attr.LockoutTime, [AdConvert.ToFileTime(new DateTimeOffset(2026, 10, 13, 7, 30, 0, TimeSpan.Zero)).ToString(CultureInfo.InvariantCulture)]));
        var b = User(Fabrikam, LukaszIn(FabrikamNc),
            (Attr.WhenCreated, ["20261013120000Z"]),
            (Attr.AccountExpires, [AdConvert.FileTimeNever.ToString(CultureInfo.InvariantCulture)]),
            (Attr.LockoutTime, [AdConvert.ToFileTime(new DateTimeOffset(2026, 10, 13, 7, 31, 0, TimeSpan.Zero)).ToString(CultureInfo.InvariantCulture)]));

        var rows = ObjectDiffer.Compare(a, b);

        Assert.Equal(AttributeComparisonState.Same, Row(rows, Attr.WhenCreated).State);
        Assert.Equal(AttributeComparisonState.Same, Row(rows, Attr.AccountExpires).State);
        Assert.Equal(AttributeComparisonState.Different, Row(rows, Attr.LockoutTime).State);
    }

    [Fact]
    public void BinaryValues_AreComparedByBytes()
    {
        var a = User(Contoso, LukaszIn(ContosoNc), (Attr.ThumbnailPhoto, [new byte[] { 1, 2, 3 }]), ("jpegPhoto", [new byte[] { 9 }]));
        var b = User(Fabrikam, LukaszIn(FabrikamNc), (Attr.ThumbnailPhoto, [new byte[] { 1, 2, 3 }]), ("jpegPhoto", [new byte[] { 8 }]));

        var rows = ObjectDiffer.Compare(a, b);

        Assert.Equal(AttributeComparisonState.Same, Row(rows, Attr.ThumbnailPhoto).State);
        Assert.Equal(AttributeComparisonState.Different, Row(rows, "jpegPhoto").State);
    }

    [Fact]
    public void ASecretOfEitherSidesSchema_IsComparedByPresenceOnly_AndMaskedInBothColumns()
    {
        // employeeNumber is confidential only in B's schema: A's column must not show it either.
        var schemaB = SchemaWith(d => d.LdapName == Attr.EmployeeNumber ? d with { IsConfidential = true } : d);
        var a = Snapshot(Contoso, TestSchema, LukaszIn(ContosoNc), ["person", "organizationalPerson", ObjectClasses.User],
            (Attr.EmployeeNumber, ["PL-0042"]), ("ms-Mcs-AdmPwd", ["Hasło-LAPS-1"]));
        var b = Snapshot(Fabrikam, schemaB, LukaszIn(FabrikamNc), ["person", "organizationalPerson", ObjectClasses.User],
            (Attr.EmployeeNumber, ["PL-9999"]));

        var rows = ObjectDiffer.Compare(a, b);

        var number = Row(rows, Attr.EmployeeNumber);
        Assert.True(number.IsSecret);
        Assert.Equal(AttributeComparisonState.Same, number.State);
        Assert.Equal(SecretAttributes.Mask, Assert.Single(number.ValuesA).Value);
        Assert.Equal(SecretAttributes.Mask, Assert.Single(number.ValuesB).Value);

        var laps = Row(rows, "ms-Mcs-AdmPwd");
        Assert.True(laps.IsSecret);
        Assert.Equal(AttributeComparisonState.OnlyA, laps.State);
        Assert.Equal(SecretAttributes.Mask, Assert.Single(laps.ValuesA).Value);
        Assert.Empty(laps.ValuesB);

        // A's own secret never even reaches the snapshot.
        Assert.Equal([SecretAttributes.Mask], a.Attributes["ms-Mcs-AdmPwd"]);
        Assert.DoesNotContain("Hasło-LAPS-1", a.Object.GetStrings("ms-Mcs-AdmPwd"));
    }

    [Fact]
    public void AnAttributeAbsentFromOneSidesSchema_IsReportedAsSuch()
    {
        var schemaB = SchemaWith(d => d.LdapName == Attr.ProxyAddresses ? null : d);
        var a = Snapshot(Contoso, TestSchema, LukaszIn(ContosoNc), ["person", "organizationalPerson", ObjectClasses.User],
            (Attr.ProxyAddresses, ["SMTP:lukasz.wisniewski@contoso.test"]), (Attr.Description, ["Tylko w A"]));
        var b = Snapshot(Fabrikam, schemaB, LukaszIn(FabrikamNc), ["person", "organizationalPerson", ObjectClasses.User],
            (Attr.Info, ["Tylko w B"]));

        var rows = ObjectDiffer.Compare(a, b);
        var reversed = ObjectDiffer.Compare(b, a);

        Assert.Equal(AttributeComparisonState.NotInSchemaB, Row(rows, Attr.ProxyAddresses).State);
        Assert.Equal(AttributeComparisonState.NotInSchemaA, Row(reversed, Attr.ProxyAddresses).State);
        Assert.Equal(AttributeComparisonState.OnlyA, Row(rows, Attr.Description).State);
        Assert.Equal(AttributeComparisonState.OnlyB, Row(rows, Attr.Info).State);
        Assert.All(new[] { Row(rows, Attr.ProxyAddresses), Row(rows, Attr.Description), Row(rows, Attr.Info) }, r => Assert.True(r.IsDifference));
    }

    [Fact]
    public void VolatileAttributes_AreFlagged_NotRemoved()
    {
        var a = User(Contoso, LukaszIn(ContosoNc), (Attr.WhenChanged, ["20261013120000.0Z"]), (Attr.Title, ["Programista"]), ("msTPM-OwnerInformationTemp2", ["x"]));
        var b = User(Fabrikam, LukaszIn(FabrikamNc), (Attr.WhenChanged, ["20261014120000.0Z"]), (Attr.Title, ["Programista"]));

        var rows = ObjectDiffer.Compare(a, b);

        Assert.True(Row(rows, Attr.WhenChanged).IsVolatile);
        Assert.False(Row(rows, Attr.Title).IsVolatile);
        Assert.True(Row(rows, "msTPM-OwnerInformationTemp2").IsVolatile);
    }

    [Fact]
    public void MembershipAttributes_AreLeftToTheMembershipTabs()
    {
        var groupA = Snapshot(Contoso, TestSchema, $"CN=GG_IT,OU=Grupy,OU=Firma,{ContosoNc}", [ObjectClasses.Group],
            (Attr.Member, [JanIn(ContosoNc)]), (Attr.MemberOf, [$"CN=DL_Wszyscy,OU=Grupy,OU=Firma,{ContosoNc}"]), (Attr.Description, ["Dział IT"]));
        var groupB = Snapshot(Fabrikam, TestSchema, $"CN=GG_IT,OU=Grupy,OU=Firma,{FabrikamNc}", [ObjectClasses.Group],
            (Attr.Member, [LukaszIn(FabrikamNc)]), (Attr.Description, ["Dział IT"]));
        var userA = User(Contoso, LukaszIn(ContosoNc), (Attr.MemberOf, [$"CN=GG_IT,OU=Grupy,OU=Firma,{ContosoNc}"]));
        var userB = User(Fabrikam, LukaszIn(FabrikamNc));

        var groupRows = ObjectDiffer.Compare(groupA, groupB);
        var userRows = ObjectDiffer.Compare(userA, userB);

        Assert.DoesNotContain(groupRows, r => r.Name is Attr.Member or Attr.MemberOf);
        Assert.DoesNotContain(userRows, r => r.Name == Attr.MemberOf);
        Assert.Contains(groupRows, r => r.Name == Attr.Description);
    }

    [Fact]
    public void LargeValueSets_AreCountedPerSide()
    {
        var common = Enumerable.Range(0, 1200).Select(i => (object)$"CN=Użytkownik {i:D4},OU=Duża grupa,{{0}}").ToList();
        var a = Snapshot(Contoso, TestSchema, $"CN=GG_Duża,{ContosoNc}", [ObjectClasses.Group],
            ("secretary", [.. common.Select(v => string.Format(CultureInfo.InvariantCulture, (string)v, ContosoNc)), .. Enumerable.Range(0, 12).Select(i => (object)$"CN=Tylko A {i},{ContosoNc}")]));
        var b = Snapshot(Fabrikam, TestSchema, $"CN=GG_Duża,{FabrikamNc}", [ObjectClasses.Group],
            ("secretary", [.. common.Select(v => string.Format(CultureInfo.InvariantCulture, (string)v, FabrikamNc)), .. Enumerable.Range(0, 3).Select(i => (object)$"CN=Tylko B {i},{FabrikamNc}")]));

        var row = Row(ObjectDiffer.Compare(a, b), "secretary");

        Assert.Equal((1200, 12, 3), (row.CommonCount, row.OnlyACount, row.OnlyBCount));
        Assert.Equal(AttributeComparisonState.Different, row.State);
    }

    [Fact]
    public void ToString_CarriesNoValues()
    {
        var a = User(Contoso, LukaszIn(ContosoNc), (Attr.Title, ["Starszy programista"]));
        var b = User(Fabrikam, LukaszIn(FabrikamNc), (Attr.Title, ["Programista"]));

        var row = Row(ObjectDiffer.Compare(a, b), Attr.Title);

        Assert.DoesNotContain("programista", a.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Łukasz", a.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("rogramista", row.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("rogramista", row.ValuesA[0].ToString(), StringComparison.Ordinal);
    }
}
