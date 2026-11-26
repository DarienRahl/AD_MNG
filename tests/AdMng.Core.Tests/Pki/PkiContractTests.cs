using System.Reflection;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Core.Pki;
using AdMng.Core.PrivilegedAccess;
using AdMng.Core.Schema;
using AdMng.Core.Security;
using AdMng.Testing;
using AdMng.Testing.Pki;

namespace AdMng.Core.Tests.Pki;

/// <summary>
/// The P1 contract commit (docs/dev/p-certificate-services.md 2.11): every record of <c>AdMng.Core.Pki</c> prints no directory
/// value (2.7), the finding catalogue, the PKI names and the done items of the shared Core files.
/// </summary>
public sealed class PkiContractTests
{
    public static TheoryData<string> RecordNames()
    {
        var data = new TheoryData<string>();
        foreach (var type in PkiRecordSamples.Records(typeof(PkiSnapshot).Assembly, "AdMng.Core.Pki"))
        {
            data.Add(type.FullName!);
        }

        return data;
    }

    [Fact]
    public void TheContractRecords_AreFound()
    {
        var records = PkiRecordSamples.Records(typeof(PkiSnapshot).Assembly, "AdMng.Core.Pki").Select(t => t.Name).ToList();

        Assert.Contains(nameof(PkiSnapshot), records);
        Assert.Contains(nameof(CertificateTemplate), records);
        Assert.Contains(nameof(PkiFinding), records);
        Assert.Contains(nameof(PkiFlagEntry), records);
        Assert.Contains(nameof(Core.Pki.Tls.TlsProbeResult), records);
        Assert.True(records.Count >= 45, $"{records.Count} records");
    }

    [Theory]
    [MemberData(nameof(RecordNames))]
    public void EveryRecord_DeclaresAValueFreeToString(string typeName)
    {
        var type = typeof(PkiSnapshot).Assembly.GetType(typeName, throwOnError: true)!;

        Assert.True(PkiRecordSamples.DeclaresOwnToString(type), $"{type.Name} uses the compiler-generated ToString (it prints every member).");
        Assert.Equal([], PkiRecordSamples.LeakedValues(type));
    }

    [Fact]
    public void TheChecks_CatchACompilerToString_AndALeakedValue()
    {
        Assert.True(PkiRecordSamples.IsRecord(typeof(GeneratedToString)));
        Assert.False(PkiRecordSamples.DeclaresOwnToString(typeof(GeneratedToString)));
        Assert.True(PkiRecordSamples.DeclaresOwnToString(typeof(LeakingToString)));
        Assert.NotEmpty(PkiRecordSamples.LeakedValues(typeof(LeakingToString)));
        Assert.NotEmpty(PkiRecordSamples.LeakedValues(typeof(GeneratedToString)));
        Assert.False(PkiRecordSamples.IsRecord(typeof(PrincipalClassifier)));
    }

    [Fact]
    public void TlsProbeResult_PrintsPortAndOutcome()
    {
        var result = new Core.Pki.Tls.TlsProbeResult("dc01.contoso.test", 636, Core.Pki.Tls.TlsProbeOutcome.HandshakeFailed, null, [], null);

        Assert.Equal("636: HandshakeFailed", result.ToString());
    }

    [Fact]
    public void FindingCodes_AreTheElevenLdapOnlyCodes()
    {
        var codes = typeof(PkiFindingCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (Name: f.Name, Value: (string)f.GetRawConstantValue()!))
            .ToList();

        Assert.Equal(11, PkiFindingCodes.All.Count);
        Assert.All(codes, c => Assert.Equal(c.Name, c.Value));
        Assert.Equal(codes.Select(c => c.Value).Order(StringComparer.Ordinal), PkiFindingCodes.All.Select(d => d.Code).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["ESC1", "ESC2", "ESC3", "ESC3", "ESC4", "ESC5", "ESC9", "ESC13", "ESC14", "ESC15", "ESC17"],
            PkiFindingCodes.All.Select(d => d.Esc));
        Assert.All(PkiFindingCodes.All, d => Assert.Null(d.Mdi));
        Assert.Null(PkiFindingCodes.Find(PkiFindingCodes.WeakExplicitMapping).Locksmith);
        Assert.Equal("ESC15/EKUwu", PkiFindingCodes.Find(PkiFindingCodes.SchemaV1EnrolleeSuppliesSubject).Locksmith);
        Assert.Equal(PkiApprovalEffect.Lowers, PkiFindingCodes.Find(PkiFindingCodes.NoSecurityExtensionClientAuth).Approval);
        Assert.Equal(PkiApprovalEffect.Never, PkiFindingCodes.Find(PkiFindingCodes.TemplateWriteable).Approval);
        Assert.Equal(PkiApprovalEffect.Suppresses, PkiFindingCodes.Find(PkiFindingCodes.EnrollmentAgentTemplate).Approval);

        // ESC3 condition 2: the authorized signature is the condition itself, only manager approval suppresses it (2.4).
        Assert.Equal(PkiApprovalEffect.ManagerApprovalSuppresses, PkiFindingCodes.Find(PkiFindingCodes.EnrollmentAgentTargetTemplate).Approval);
        Assert.Equal([PkiFindingCodes.EnrollmentAgentTargetTemplate], PkiFindingCodes.All.Where(d => d.Approval == PkiApprovalEffect.ManagerApprovalSuppresses).Select(d => d.Code));
        Assert.Throws<KeyNotFoundException>(() => PkiFindingCodes.Find("ESC8"));
    }

    [Fact]
    public void PkiNames_AreTheSevenClassesAndTheNineSensitiveAttributes()
    {
        Assert.Equal(7, PkiClasses.All.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            [
                "msPKI-Certificate-Name-Flag", "msPKI-Enrollment-Flag", "pKIExtendedKeyUsage", "msPKI-Certificate-Application-Policy",
                "msPKI-RA-Signature", "msPKI-RA-Application-Policies", "msPKI-RA-Policies", "msPKI-Certificate-Policy",
                "msPKI-Template-Schema-Version",
            ],
            PkiAttributes.SensitiveTemplateAttributes);
        Assert.Subset(PkiAttributes.OverviewAttributes.ToHashSet(StringComparer.OrdinalIgnoreCase), PkiAttributes.TemplateAttributes.ToHashSet(StringComparer.OrdinalIgnoreCase));
        Assert.Contains(Attr.NtSecurityDescriptor, PkiAttributes.TemplateAttributes);
        Assert.Contains(PkiAttributes.AltSecurityIdentities, PkiAttributes.AccountAttributes);
    }

    [Fact]
    public void TheTemplateExtendedRights_AreKnownWithoutTheConfiguration()
    {
        var types = new AclObjectTypes(null, null);

        Assert.True(types.IsRight(ExtendedRightIds.CertificateEnrollment, AclObjectTypes.CertificateEnrollment));
        Assert.True(types.IsRight(ExtendedRightIds.CertificateAutoEnrollment, AclObjectTypes.CertificateAutoEnrollment));
        Assert.Equal(FakeDirectoryBuilder.AclGuids.CertificateEnrollment, ExtendedRightIds.CertificateEnrollment);
        Assert.Equal(FakeDirectoryBuilder.AclGuids.CertificateAutoEnrollment, ExtendedRightIds.CertificateAutoEnrollment);
        Assert.Equal("altSecurityIdentities", AclObjectTypes.AltSecurityIdentities);
        Assert.True(Enum.IsDefined(AclRight.WriteTemplateProperty));
        Assert.True(Enum.IsDefined(AclRight.WriteAltSecurityIdentities));
    }

    [Fact]
    public void CompleteMemberOf_IsTheSharedStaticHelper()
    {
        var method = typeof(PrivilegedAccessScanner).GetMethod("CompleteMemberOfAsync", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.True(method.IsAssembly);
        Assert.Equal([typeof(IDirectoryReader), typeof(DirectoryObject), typeof(CancellationToken)], method.GetParameters().Select(p => p.ParameterType));
    }

    [Theory]
    [InlineData("userPKCS12")]
    [InlineData("userPKCS12;binary")]
    [InlineData("msPKIAccountCredentials")]
    [InlineData("msPKIDPAPIMasterKeys")]
    [InlineData("msFVE-KeyPackage")]
    [InlineData("msDS-ManagedPassword")]
    [InlineData("supplementalCredentials")]
    [InlineData("msLAPS-EncryptedPassword")]
    [InlineData("msLAPS-EncryptedDSRMPasswordHistory")]
    public void KeyMaterial_IsSecretAndNeverShown(string attribute)
    {
        Assert.True(SecretAttributes.IsSecret(attribute));
        Assert.True(SecretAttributes.IsNeverShown(attribute, null));
        Assert.True(SecretAttributes.IsNeverShown(attribute, FakeSchema.Create()));
    }

    [Theory]
    [InlineData("ms-Mcs-AdmPwd")]
    [InlineData("msLAPS-Password")]
    [InlineData("msFVE-RecoveryPassword")]
    [InlineData("description")]
    [InlineData("userCertificate")]
    public void OperatorReadableSecretsAndOrdinaryAttributes_AreShown(string attribute) =>
        Assert.False(SecretAttributes.IsNeverShown(attribute, FakeSchema.Create()));

    [Fact]
    public void BinarySecrets_OfTheSchema_AreNeverShown()
    {
        var schema = new SchemaCatalog(
        [
            new AttributeDefinition { LdapName = "contoso-KeyBlob", Syntax = AttributeSyntax.OctetString, IsConfidential = true },
            new AttributeDefinition { LdapName = "contoso-Pin", Syntax = AttributeSyntax.UnicodeString, IsConfidential = true },
        ], []);

        Assert.True(SecretAttributes.IsNeverShown("contoso-KeyBlob", schema));
        Assert.False(SecretAttributes.IsNeverShown("contoso-Pin", schema));
        Assert.True(SecretAttributes.IsNeverShown("unicodePwd", FakeSchema.Create()));
    }

    [Fact]
    public async Task CertificateTemplateOf_FillsTheIdentityMembers()
    {
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        var sample = builder.AddPublicKeySample(company);
        var dn = sample.Template(PublicKeyTemplates.Esc1);
        var obj = await builder.Session.GetAsync(dn, PkiAttributes.TemplateAttributes, CancellationToken.None);

        var template = CertificateTemplate.Of(obj!);

        Assert.Equal(dn, template.Dn);
        Assert.Equal(PublicKeyTemplates.Esc1, template.Cn);
        Assert.Equal(PublicKeyTemplates.Esc1, template.DisplayName);
        Assert.Equal(builder.Session.Snapshot(dn)!.ObjectGuid, template.ObjectGuid);
        Assert.NotNull(template.SecurityDescriptor);
        Assert.True(SecurityDescriptorSummary.TryParse(template.SecurityDescriptor, out _));
        Assert.DoesNotContain(PublicKeyTemplates.Esc1, template.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheEmptySnapshot_CarriesItsNotices()
    {
        var snapshot = PkiSnapshot.Empty(PkiSamples.ConfigurationDn, PkiSamples.Now, [new PkiReadNotice(PkiReadNoticeCode.BaseNotFound, PkiSamples.PublicKeyServicesDn, null)]);

        Assert.Empty(snapshot.Templates);
        Assert.Same(PkiTrustMaterial.Empty, snapshot.TrustMaterial);
        Assert.Equal(PkiReadNoticeCode.BaseNotFound, Assert.Single(snapshot.Notices).Code);
        Assert.Equal("BaseNotFound", snapshot.Notices[0].ToString());
    }

    /// <summary>A record with the synthesized ToString (prints every member).</summary>
    private sealed record GeneratedToString(string Name, IReadOnlyList<string> Hosts);

    /// <summary>A record whose own ToString prints a value.</summary>
    private sealed record LeakingToString(string Name)
    {
        public override string ToString() => "Leaking " + Name;
    }
}
