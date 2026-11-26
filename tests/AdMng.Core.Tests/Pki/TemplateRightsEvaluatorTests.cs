using System.Security.Cryptography;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Pki;
using AdMng.Core.PrivilegedAccess;
using AdMng.Testing;

namespace AdMng.Core.Tests.Pki;

/// <summary>
/// Decision 5 / 2.3 of docs/dev/p-certificate-services.md: the template DACL decoded into atomic rights and decided with
/// AccessCheck semantics (first applicable ACE per right, deny honoured, inherit-only and foreign inherited object types
/// skipped, GENERIC_* normalized), the fixed GUIDs of the base schema, the trustee level and AclObjectTypes.ReadAsync.
/// </summary>
public class TemplateRightsEvaluatorTests
{
    private const string Domain = FakeDirectorySession.DefaultDomainSid;
    private const string DomainUsers = Domain + "-513";
    private const string DomainAdmins = Domain + "-512";
    private const string EnterpriseAdmins = Domain + "-519";
    private const string Jan = Domain + "-1105";
    private const string Lukasz = Domain + "-1106";
    private const string GroupIt = Domain + "-1120";
    private const string Foreign = "S-1-5-21-1111111111-2222222222-3333333333-1107";

    /// <summary>Only the fixed GUIDs (no schema, no CN=Extended-Rights): what a forest without readable schema gives.</summary>
    private static readonly AclObjectTypes Fixed = new(null, null);

    private static readonly Guid Enroll = FakeDirectoryBuilder.AclGuids.CertificateEnrollment;
    private static readonly Guid AutoEnroll = FakeDirectoryBuilder.AclGuids.CertificateAutoEnrollment;
    private static readonly Guid TemplateClass = FakeDirectoryBuilder.AclGuids.PkiCertificateTemplate;
    private static readonly Guid NameFlag = new("ea1dddc4-60ff-416e-8cc0-17cee534bce7");
    private static readonly Guid EnrollmentFlag = new("d15ef7d8-f226-46db-ae79-b34e560bd12c");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("ea1dddc4-60ff-416e-8cc0-17cee534bce7", "msPKI-Certificate-Name-Flag")]
    [InlineData("d15ef7d8-f226-46db-ae79-b34e560bd12c", "msPKI-Enrollment-Flag")]
    [InlineData("18976af6-3b9e-11d2-90cc-00c04fd91ab1", "pKIExtendedKeyUsage")]
    [InlineData("dbd90548-aa37-4202-9966-8c537ba5ce32", "msPKI-Certificate-Application-Policy")]
    [InlineData("fe17e04b-937d-4f7e-8e0e-9292c8d5683e", "msPKI-RA-Signature")]
    [InlineData("3c91fbbf-4773-4ccd-a87b-85d53e7bcf6a", "msPKI-RA-Application-Policies")]
    [InlineData("d546ae22-0951-4d47-817e-1c9f96faad46", "msPKI-RA-Policies")]
    [InlineData("38942346-cc5b-424b-a7d8-6ffd12029c5f", "msPKI-Certificate-Policy")]
    [InlineData("0c15e9f5-491d-4594-918f-32813a091da9", "msPKI-Template-Schema-Version")]
    [InlineData("00fbf30c-91fe-11d1-aebc-0000f80367c1", "altSecurityIdentities")]
    public void TheFixedGuids_NameThePublishedSchemaIdGuids(string guid, string attribute)
    {
        // MS-ADA1/MS-ADA2 schemaIDGUIDs (checked against the Windows Server schema LDF).
        Assert.Equal(attribute, Fixed.AttributeName(new Guid(guid)));
        Assert.True(Fixed.Covers(new Guid(guid), attribute));
    }

    [Fact]
    public void TheFixedGuids_KnowTheTemplateClass_TheEnrollmentRights_AndPublicInformation()
    {
        Assert.True(Fixed.IsClass(TemplateClass, PkiClasses.CertificateTemplate));
        Assert.True(Fixed.IsRight(Enroll, AclObjectTypes.CertificateEnrollment));
        Assert.True(Fixed.IsRight(AutoEnroll, AclObjectTypes.CertificateAutoEnrollment));

        // altSecurityIdentities is in the Public-Information property set (attributeSecurityGUID e48d0154-…).
        Assert.True(Fixed.Covers(new Guid("e48d0154-bcf8-11d1-8702-00c04fb96050"), AclObjectTypes.AltSecurityIdentities));
        Assert.Equal("Public-Information", Fixed.Describe(new Guid("e48d0154-bcf8-11d1-8702-00c04fb96050")));
        Assert.Equal(9, PkiAttributes.SensitiveTemplateAttributes.Count);
    }

    [Fact]
    public void Enroll_IsTheEnrollmentExtendedRight_AutoEnrollApart()
    {
        var acl = Acl(DomainAdmins, Object(Allow, 0x100, DomainUsers, Enroll), Object(Allow, 0x100, GroupIt, AutoEnroll));

        Assert.True(Decide(acl, TemplateRight.Enroll, DomainUsers));
        Assert.False(Decide(acl, TemplateRight.AutoEnroll, DomainUsers));
        Assert.True(Decide(acl, TemplateRight.AutoEnroll, GroupIt));
        Assert.False(Decide(acl, TemplateRight.Enroll, GroupIt));
        Assert.Equal([TemplateRight.Enroll, TemplateRight.AutoEnroll], acl.Aces.Select(a => a.Right));
    }

    [Theory]
    [InlineData(0x10000000)] // GENERIC_ALL
    [InlineData(0xF01FF)]    // full control as ADUC writes it
    public void FullControl_AndGenericAll_GiveEveryRight(int mask)
    {
        var acl = Acl(DomainAdmins, new FakeAce(Allow, 0, mask, GroupIt));

        foreach (var right in new[] { TemplateRight.FullControl, TemplateRight.Enroll, TemplateRight.AutoEnroll, TemplateRight.WriteDacl, TemplateRight.WriteOwner, TemplateRight.WriteAllProperties })
        {
            Assert.True(Decide(acl, right, GroupIt), right.ToString());
        }

        // The attribute rights name attributes; a write of every property is WriteAllProperties.
        Assert.False(Decide(acl, TemplateRight.WriteSensitiveProperty, GroupIt));
        Assert.False(Decide(acl, TemplateRight.Owner, GroupIt));
    }

    [Fact]
    public void GenericWrite_IsWriteAllProperties_ButNoEnroll()
    {
        var acl = Acl(DomainAdmins, new FakeAce(Allow, 0, 0x40000000, GroupIt));

        Assert.True(Decide(acl, TemplateRight.WriteAllProperties, GroupIt));
        Assert.False(Decide(acl, TemplateRight.Enroll, GroupIt));
        Assert.False(Decide(acl, TemplateRight.FullControl, GroupIt));
        Assert.False(Decide(acl, TemplateRight.WriteDacl, GroupIt));
    }

    [Fact]
    public void ADenyFirst_Wins_AnAllowFirst_Wins()
    {
        // Jan is denied Enroll before Domain Users are allowed; Łukasz is allowed before he is denied.
        var acl = Acl(DomainAdmins,
            Object(Deny, 0x100, Jan, Enroll),
            Object(Allow, 0x100, DomainUsers, Enroll),
            Object(Deny, 0x100, Lukasz, Enroll));

        Assert.False(Decide(acl, TemplateRight.Enroll, Jan, DomainUsers));
        Assert.True(Decide(acl, TemplateRight.Enroll, Lukasz, DomainUsers));
        Assert.False(Decide(acl, TemplateRight.Enroll, Lukasz));
        Assert.True(Decide(acl, TemplateRight.Enroll, DomainUsers));
    }

    [Fact]
    public void EachRight_IsDecidedOnItsOwn_InDaclOrder()
    {
        // A deny of WRITE_DAC before full control: the token keeps everything else, but no longer full control.
        var acl = Acl(DomainAdmins, new FakeAce(FakeAce.AccessDenied, 0, 0x40000, GroupIt), new FakeAce(Allow, 0, 0xF01FF, GroupIt));

        Assert.False(Decide(acl, TemplateRight.WriteDacl, GroupIt));
        Assert.False(Decide(acl, TemplateRight.FullControl, GroupIt));
        Assert.True(Decide(acl, TemplateRight.WriteOwner, GroupIt));
        Assert.True(Decide(acl, TemplateRight.Enroll, GroupIt));
        Assert.True(Decide(acl, TemplateRight.WriteAllProperties, GroupIt));

        // The same deny after full control changes nothing (the bits were granted first).
        var after = Acl(DomainAdmins, new FakeAce(Allow, 0, 0xF01FF, GroupIt), new FakeAce(FakeAce.AccessDenied, 0, 0x40000, GroupIt));
        Assert.True(Decide(after, TemplateRight.WriteDacl, GroupIt));
        Assert.True(Decide(after, TemplateRight.FullControl, GroupIt));
    }

    [Fact]
    public void InheritOnlyAces_AndAcesInheritedForAnotherClass_AreSkipped()
    {
        var user = FakeDirectoryBuilder.AclGuids.User;
        var unknown = new Guid("6a1f9e47-2c3d-4b5a-8e9f-0a1b2c3d4e5f");
        var acl = Acl(DomainAdmins,
            new FakeAce(Allow, 0x0A, 0xF01FF, Jan),                                   // inherit-only: for children only
            new FakeAce(FakeAce.AccessAllowedObject, 0x12, 0xF01FF, Lukasz, null, user), // inherited for users
            new FakeAce(FakeAce.AccessAllowedObject, 0x12, 0x100, DomainUsers, Enroll, TemplateClass),
            new FakeAce(FakeAce.AccessAllowedObject, 0x12, 0x40000, GroupIt, null, unknown)); // a class nothing names: kept

        Assert.False(Decide(acl, TemplateRight.FullControl, Jan));
        Assert.False(Decide(acl, TemplateRight.FullControl, Lukasz));
        Assert.True(Decide(acl, TemplateRight.Enroll, DomainUsers));
        Assert.True(Decide(acl, TemplateRight.WriteDacl, GroupIt));
        Assert.DoesNotContain(acl.Aces, a => a.Sid == Jan || a.Sid == Lukasz);
    }

    [Fact]
    public void WritesOfSensitiveAttributes_OtherAttributes_AndUnknownGuids_AreGraded()
    {
        var types = new AclObjectTypes(FakeSchema.Create(), null);
        var displayName = FakeSchema.Create().FindAttribute(Attr.DisplayName)!.SchemaIdGuid;
        var unknown = new Guid("9b6c1d2e-3f40-4a5b-8c6d-7e8f90a1b2c3");
        var acl = Acl(types, DomainAdmins,
            Object(Allow, 0x20, Jan, NameFlag),
            Object(Allow, 0x20, Lukasz, displayName),
            Object(Allow, 0x20, GroupIt, unknown),
            Object(Allow, 0x20, DomainUsers, Enroll)); // write property with an extended-right GUID reaches no property

        Assert.Equal(new TemplateAce(Jan, TemplateRight.WriteSensitiveProperty, false, "msPKI-Certificate-Name-Flag"), acl.Aces[0]);
        Assert.Equal(new TemplateAce(Lukasz, TemplateRight.WriteOtherProperty, false, Attr.DisplayName), acl.Aces[1]);
        Assert.Equal(new TemplateAce(GroupIt, TemplateRight.WriteSensitiveProperty, false, unknown.ToString("D")), acl.Aces[2]);
        Assert.Equal(3, acl.Aces.Count);
        Assert.True(Decide(acl, TemplateRight.WriteSensitiveProperty, Jan));
        Assert.True(Decide(acl, TemplateRight.WriteOtherProperty, Lukasz));
        Assert.False(Decide(acl, TemplateRight.WriteSensitiveProperty, Lukasz));
        Assert.True(Decide(acl, TemplateRight.WriteSensitiveProperty, GroupIt));
    }

    [Fact]
    public void AttributeWrites_AreDecidedPerAttribute()
    {
        // Jan is denied the name flag; Domain Users may write the name flag and the enrollment flag.
        var acl = Acl(DomainAdmins,
            Object(Deny, 0x20, Jan, NameFlag),
            Object(Allow, 0x20, DomainUsers, NameFlag),
            Object(Allow, 0x20, DomainUsers, EnrollmentFlag));
        var nameOnly = Acl(DomainAdmins, Object(Deny, 0x20, Jan, NameFlag), Object(Allow, 0x20, DomainUsers, NameFlag));
        var denyAll = Acl(DomainAdmins, new FakeAce(FakeAce.AccessDenied, 0, 0x20, Jan), Object(Allow, 0x20, DomainUsers, NameFlag));

        Assert.True(Decide(acl, TemplateRight.WriteSensitiveProperty, Jan, DomainUsers));
        Assert.False(Decide(nameOnly, TemplateRight.WriteSensitiveProperty, Jan, DomainUsers));
        Assert.True(Decide(nameOnly, TemplateRight.WriteSensitiveProperty, DomainUsers));
        Assert.False(Decide(denyAll, TemplateRight.WriteSensitiveProperty, Jan, DomainUsers));
        Assert.False(Decide(denyAll, TemplateRight.WriteAllProperties, Jan));
    }

    [Fact]
    public void TheTemplateClassAsObjectType_CoversTheWholeObject()
    {
        var acl = Acl(DomainAdmins, Object(Allow, 0x120, GroupIt, TemplateClass));

        Assert.True(Decide(acl, TemplateRight.WriteAllProperties, GroupIt));
        Assert.True(Decide(acl, TemplateRight.Enroll, GroupIt));
        Assert.True(Decide(acl, TemplateRight.AutoEnroll, GroupIt));
    }

    [Fact]
    public void AnObjectTypedDeny_DoesNotRemoveWriteDacl_AnObjectTypedAllowGrantsIt()
    {
        // WRITE_DAC is a right of the object: an object-typed deny does not take it away (conservative), an object-typed
        // allow with the bit is counted (conservative, as U4b).
        var acl = Acl(DomainAdmins, Object(Deny, 0x40000, Jan, NameFlag), new FakeAce(Allow, 0, 0x40000, Jan), Object(Allow, 0x80000, Lukasz, NameFlag));

        Assert.True(Decide(acl, TemplateRight.WriteDacl, Jan));
        Assert.True(Decide(acl, TemplateRight.WriteOwner, Lukasz));
    }

    [Fact]
    public void TheOwner_HoldsOwner_UnlessOwnerRightsReplaceIt()
    {
        var plain = Acl(GroupIt, new FakeAce(Allow, 0, 0x20094, "S-1-5-11"));
        Assert.True(Decide(plain, TemplateRight.Owner, GroupIt));
        Assert.False(Decide(plain, TemplateRight.Owner, Jan));
        Assert.Equal(GroupIt, plain.Owner);
        Assert.False(plain.HasOwnerRights);

        // OWNER RIGHTS with READ_CONTROL only: the owner loses its implicit WRITE_DAC.
        var readOnly = Acl(GroupIt, new FakeAce(Allow, 0, 0x20000, "S-1-3-4"));
        Assert.True(readOnly.HasOwnerRights);
        Assert.False(Decide(readOnly, TemplateRight.Owner, GroupIt));
        Assert.False(Decide(readOnly, TemplateRight.WriteDacl, GroupIt));

        // OWNER RIGHTS granting WRITE_DAC: the owner (and only the owner) holds it.
        var writeDacl = Acl(GroupIt, new FakeAce(Allow, 0, 0x40000, "S-1-3-4"));
        Assert.True(Decide(writeDacl, TemplateRight.WriteDacl, GroupIt));
        Assert.False(Decide(writeDacl, TemplateRight.WriteDacl, Jan));
    }

    [Fact]
    public void ACallbackDeny_IsNotHonoured_ItsConditionCannotBeEvaluated()
    {
        var acl = Acl(DomainAdmins, new FakeAce(0x0A, 0, 0x100, Jan), new FakeAce(Allow, 0, 0x100, Jan));

        Assert.True(Decide(acl, TemplateRight.Enroll, Jan));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 0, 4, 0x80, 1, 2, 3 })]
    [InlineData(new byte[] { 1, 0, 4, 0x80, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x14, 0, 0, 0, 0, 0, 0, 0 })]
    public void AMissingOrUnparseableDescriptor_IsNotEvaluated(byte[] descriptor)
    {
        var acl = TemplateRightsEvaluator.Evaluate(descriptor, Fixed);

        Assert.False(acl.IsEvaluated);
        Assert.Empty(acl.Aces);
        Assert.All(Enum.GetValues<TemplateRight>(), r => Assert.False(TemplateRightsEvaluator.Decide(Token(DomainUsers), acl, r)));
    }

    [Fact]
    public void ADescriptorWithoutDacl_IsNotEvaluated_NeverNobody()
    {
        // Owner only (the DACL was not returned, or a NULL DACL that grants everyone everything): nieoceniony.
        var descriptor = FakeSecurityDescriptor.Build(DomainAdmins, DomainAdmins);
        descriptor[2] = 0x00;
        descriptor[3] = 0x80;

        Assert.False(TemplateRightsEvaluator.Evaluate(descriptor, Fixed).IsEvaluated);
    }

    [Fact]
    public void TheTrusteeLevel_ListsWhoHoldsEachRight_HonouringDenies()
    {
        var template = Template("Użytkownik-Księgowość", Sd(GroupIt,
            FakeAce.FullControl(DomainAdmins),
            FakeAce.FullControl(EnterpriseAdmins),
            Object(Deny, 0x100, Jan, Enroll),
            Object(Allow, 0x100, Jan, Enroll),
            Object(Allow, 0x100, DomainUsers, Enroll),
            Object(Allow, 0x100, Foreign, Enroll),
            Object(Allow, 0x20, GroupIt, NameFlag),
            new FakeAce(Allow, 0, 0x40000, "S-1-3-4"),
            new FakeAce(Allow, 0x02, 0xF01FF, "S-1-3-0")));
        var unreadable = Template("Bez-Deskryptora", null);
        var classifier = new PrincipalClassifier(Domain, Domain, new PrivilegedAccessOptions { DomainSid = Domain, RootDomainSid = Domain }, null, []);

        var rights = TemplateRightsEvaluator.TrusteeLevel([template, unreadable], Fixed, classifier);

        var evaluated = rights[0];
        Assert.True(evaluated.IsEvaluated);
        Assert.Equal(template.ObjectGuid, evaluated.TemplateGuid);
        Assert.Empty(evaluated.Totals);
        Assert.All(evaluated.Trustees, t => Assert.Equal((-1, -1), (t.AccountCount, t.DisabledCount)));
        string[] enrollers = [.. evaluated.Trustees.Where(t => t.Right == TemplateRight.Enroll).Select(t => t.Sid)];
        Assert.Equal([DomainAdmins, EnterpriseAdmins, DomainUsers, Foreign], enrollers);

        var users = evaluated.Trustees.Single(t => t.Sid == DomainUsers);
        Assert.Equal(PrincipalClass.Broad, users.Class);
        Assert.True(users.IsResolved);
        var foreign = evaluated.Trustees.Single(t => t.Sid == Foreign);
        Assert.Equal(PrincipalClass.Other, foreign.Class);
        Assert.False(foreign.IsResolved);
        var writer = Assert.Single(evaluated.Trustees, t => t.Right == TemplateRight.WriteSensitiveProperty);
        Assert.Equal((GroupIt, "msPKI-Certificate-Name-Flag", PrincipalClass.Other), (writer.Sid, writer.Attribute, writer.Class));

        // OWNER RIGHTS goes to the owner GG_IT (WRITE_DAC through it, no implicit Owner right); CREATOR OWNER is never a trustee.
        Assert.DoesNotContain(evaluated.Trustees, t => t.Sid is "S-1-3-4" or "S-1-3-0");
        Assert.DoesNotContain(evaluated.Trustees, t => t.Right == TemplateRight.Owner);
        Assert.Contains(evaluated.Trustees, t => t.Sid == GroupIt && t.Right == TemplateRight.WriteDacl);
        Assert.DoesNotContain(evaluated.Trustees, t => t.Sid == GroupIt && t.Right == TemplateRight.WriteOwner);

        // A template without its descriptor is "nieoceniony", never "nobody can enroll".
        Assert.Equal(("Bez-Deskryptora", unreadable.ObjectGuid, false), (rights[1].TemplateCn, rights[1].TemplateGuid, rights[1].IsEvaluated));
        Assert.Empty(rights[1].Trustees);
    }

    [Fact]
    public async Task ReadAsync_ReadsTheSchema_AndTheExtendedRights_InOneSearch()
    {
        var builder = new FakeDirectoryBuilder();
        builder.AddAclDefinitions();
        builder.Session.ClearOperations();

        var types = await AclObjectTypes.ReadAsync(builder.Session, 250, Ct);

        var search = Assert.Single(builder.Session.Operations).Search!;
        Assert.Equal(SearchScope.OneLevel, search.Scope);
        Assert.Equal(250, search.PageSize);
        Assert.StartsWith("CN=Extended-Rights,", search.BaseDn, StringComparison.Ordinal);
        Assert.True(types.IsRight(Enroll, AclObjectTypes.CertificateEnrollment));
        var schemaGuid = builder.Session.Schema.FindAttribute(PkiAttributes.EnrollmentFlag)!.SchemaIdGuid;
        Assert.Equal(PkiAttributes.EnrollmentFlag, types.AttributeName(schemaGuid));
        Assert.Equal(PkiAttributes.EnrollmentFlag, types.AttributeName(EnrollmentFlag));
    }

    [Fact]
    public async Task ReadAsync_WithoutExtendedRights_FallsBackToTheFixedGuids()
    {
        // No configuration partition beyond the defaults: CN=Extended-Rights does not exist.
        var builder = new FakeDirectoryBuilder();

        var types = await AclObjectTypes.ReadAsync(builder.Session, 500, Ct);

        Assert.True(types.IsRight(Enroll, AclObjectTypes.CertificateEnrollment));
        Assert.True(types.IsClass(TemplateClass, PkiClasses.CertificateTemplate));
    }

    private const byte Allow = FakeAce.AccessAllowed;
    private const byte Deny = FakeAce.AccessDeniedObject;

    private static FakeAce Object(byte type, int mask, string sid, Guid objectType) =>
        new(type == Allow ? FakeAce.AccessAllowedObject : type, 0, mask, sid, objectType);

    private static byte[] Sd(string owner, params FakeAce[] aces) => FakeSecurityDescriptor.Build(owner, owner, aces);

    private static TemplateAcl Acl(string owner, params FakeAce[] aces) => Acl(Fixed, owner, aces);

    private static TemplateAcl Acl(AclObjectTypes types, string owner, params FakeAce[] aces)
    {
        var acl = TemplateRightsEvaluator.Evaluate(Sd(owner, aces), types);
        Assert.True(acl.IsEvaluated);
        return acl;
    }

    private static IReadOnlySet<string> Token(params string[] sids) => new HashSet<string>(sids, StringComparer.OrdinalIgnoreCase);

    private static bool Decide(TemplateAcl acl, TemplateRight right, params string[] sids) => TemplateRightsEvaluator.Decide(Token(sids), acl, right);

    private static CertificateTemplate Template(string cn, byte[]? descriptor) => new()
    {
        Dn = $"CN={cn},CN=Certificate Templates,CN=Public Key Services,CN=Services,CN=Configuration,DC=contoso,DC=test",
        Cn = cn,
        ObjectGuid = new Guid(MD5.HashData(Encoding.UTF8.GetBytes("szablon:" + cn))),
        SecurityDescriptor = descriptor,
    };
}
