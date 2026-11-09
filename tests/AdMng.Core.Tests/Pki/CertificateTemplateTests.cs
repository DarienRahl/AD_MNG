using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;
using AdMng.Core.Pki;
using AdMng.Testing;
using AdMng.Testing.Pki;

namespace AdMng.Core.Tests.Pki;

/// <summary>
/// <see cref="CertificateTemplate.Of"/> (docs/dev/p-certificate-services.md 2.2, 2.10) on the templates of
/// <see cref="FakeDirectoryBuilder.AddPublicKeySample"/> (schema 1 without a schema-version attribute, schema 2, the
/// schema-4 backquote <c>msPKI-RA-Application-Policies</c>) and on hand-built objects (negative masks, both RA formats,
/// provider priorities, malformed values).
/// </summary>
public sealed class CertificateTemplateTests
{
    private const string TemplatesDn = "CN=Certificate Templates,CN=Public Key Services,CN=Services,CN=Configuration,DC=contoso,DC=test";

    private static readonly Lazy<Sample> Shared = new(Sample.Create);

    [Fact]
    public async Task TheDefaultUserTemplate_IsSchema1_WithoutSchemaVersionOrRaSignature()
    {
        var (template, obj) = await ReadAsync(PublicKeyTemplates.User);

        Assert.False(obj.Has(PkiAttributes.TemplateSchemaVersion));
        Assert.False(obj.Has(PkiAttributes.RaSignature));
        Assert.Equal(1, template.SchemaVersion);
        Assert.Equal(0, template.RaSignatures);
        Assert.False(template.RequiresAuthorizedSignature);
        Assert.Equal("Użytkownik", template.DisplayName);
        Assert.Equal(PublicKeyTemplates.User, template.Cn);
        Assert.Equal(4, template.Revision);
        Assert.Equal(0, template.MinorRevision);
        Assert.Equal(PkiSamples.ForestOid + ".1.1", template.Oid);

        Assert.Equal(0xA6000000u, template.NameFlag);
        Assert.Equal(0x29u, template.EnrollmentFlag);
        Assert.Equal(0x10u, template.PrivateKeyFlag);
        Assert.Equal(0x1023Au, template.GeneralFlag);
        Assert.False(template.EnrolleeSuppliesSubject);
        Assert.False(template.RequiresManagerApproval);
        Assert.False(template.NoSecurityExtension);
        Assert.Equal(0, template.MinimumCaVersion);
        Assert.Equal(0, template.MinimumClientVersion);

        Assert.Equal([PkiSamples.EncryptingFileSystem, PkiSamples.SecureEmail, PkiSamples.ClientAuthentication], template.Ekus);
        Assert.Empty(template.ApplicationPolicies);
        Assert.Same(template.Ekus, template.EffectiveEku);
        Assert.True(PkiFlags.IsClientAuth(template.EffectiveEku));
        Assert.Empty(template.RaApplicationPolicies);
        Assert.Empty(template.CryptoSettings);
        Assert.Empty(template.IssuancePolicies);
        Assert.Empty(template.RaIssuancePolicies);
        Assert.Empty(template.SupersededTemplates);

        Assert.Equal(TimeSpan.FromDays(365), template.Validity!.Value.Duration);
        Assert.Equal(PkiPeriodUnit.Years, template.Validity.Value.Unit);
        Assert.Equal(TimeSpan.FromDays(42), template.RenewalOverlap!.Value.Duration);
        Assert.Equal(PkiPeriodUnit.Weeks, template.RenewalOverlap.Value.Unit);
        Assert.Equal(2048, template.MinimalKeySize);
        Assert.Equal(["Microsoft Enhanced Cryptographic Provider v1.0"], template.CryptoProviders);
        Assert.Equal(new byte[] { 0xA0, 0x00 }, template.KeyUsage);
        Assert.NotNull(template.SecurityDescriptor);
    }

    [Fact]
    public async Task TheDefaultUserTemplate_DecodesItsFlags()
    {
        var (template, _) = await ReadAsync(PublicKeyTemplates.User);

        Assert.Equal(
            ["CT_FLAG_SUBJECT_ALT_REQUIRE_UPN", "CT_FLAG_SUBJECT_ALT_REQUIRE_EMAIL", "CT_FLAG_SUBJECT_REQUIRE_EMAIL", "CT_FLAG_SUBJECT_REQUIRE_DIRECTORY_PATH"],
            template.DecodeFlags(PkiFlagAttribute.NameFlag).Set.Select(e => e.Name));
        Assert.Equal(
            ["CT_FLAG_INCLUDE_SYMMETRIC_ALGORITHMS", "CT_FLAG_PUBLISH_TO_DS", "CT_FLAG_AUTO_ENROLLMENT"],
            template.DecodeFlags(PkiFlagAttribute.EnrollmentFlag).Set.Select(e => e.Name));
        Assert.Equal(["CT_FLAG_EXPORTABLE_KEY"], template.DecodeFlags(PkiFlagAttribute.PrivateKeyFlag).Set.Select(e => e.Name));
        Assert.Equal(
            ["CT_FLAG_ADD_EMAIL", "CT_FLAG_PUBLISH_TO_DS", "CT_FLAG_EXPORTABLE_KEY", "CT_FLAG_AUTO_ENROLLMENT", "CT_FLAG_ADD_TEMPLATE_NAME", "CT_FLAG_IS_DEFAULT"],
            template.DecodeFlags(PkiFlagAttribute.GeneralFlag).Set.Select(e => e.Name));
        Assert.All(Enum.GetValues<PkiFlagAttribute>(), a => Assert.Equal(0, template.DecodeFlags(a).UnknownBits));
        Assert.Equal(template.PrivateKeyFlag, template.Mask(PkiFlagAttribute.PrivateKeyFlag));
    }

    [Fact]
    public async Task ASchema2Template_ReadsApplicationPolicies_RevisionsAndTheVersionFields()
    {
        var (template, _) = await ReadAsync(PublicKeyTemplates.Esc1);

        Assert.Equal(2, template.SchemaVersion);
        Assert.Equal(100, template.Revision);
        Assert.Equal(3, template.MinorRevision);
        Assert.True(template.EnrolleeSuppliesSubject);
        Assert.False(template.RequiresManagerApproval);
        Assert.Equal(0, template.RaSignatures);
        Assert.Equal([PkiSamples.ClientAuthentication], template.Ekus);
        Assert.Equal([PkiSamples.ClientAuthentication], template.ApplicationPolicies);
        Assert.Same(template.ApplicationPolicies, template.EffectiveEku);
        Assert.Equal(0x01010000u, template.PrivateKeyFlag);
        Assert.Equal(1, template.MinimumCaVersion);
        Assert.Equal(1, template.MinimumClientVersion);
        Assert.Equal(new[] { (PkiFlags.MinimumCaVersion, 1), (PkiFlags.MinimumClientVersion, 1) },
            template.DecodeFlags(PkiFlagAttribute.PrivateKeyFlag).Fields.Select(f => (f.Field, f.Value)));
        Assert.Empty(template.CryptoProviders);
        Assert.Equal(0x20220u, template.GeneralFlag);
    }

    [Fact]
    public async Task TheSchema4Template_SplitsTheBackquoteRaApplicationPolicies()
    {
        var (template, obj) = await ReadAsync(PublicKeyTemplates.Esc3TargetSafe);

        Assert.Contains("`PZPWSTR`", obj.GetString(PkiAttributes.RaApplicationPolicies)!, StringComparison.Ordinal);
        Assert.Equal(4, template.SchemaVersion);
        Assert.Equal(1, template.RaSignatures);
        Assert.True(template.RequiresAuthorizedSignature);
        Assert.True(template.RequiresManagerApproval);
        Assert.Equal([PkiSamples.CertificateRequestAgent], template.RaApplicationPolicies);
        Assert.Equal(5, template.CryptoSettings.Count);
        Assert.Equal("RSA", template.CryptoSettings["msPKI-Asymmetric-Algorithm"]);
        Assert.Equal("SHA256", template.CryptoSettings["msPKI-Hash-Algorithm"]);
        Assert.Equal("16777215", template.CryptoSettings["msPKI-Key-Usage"]);
        Assert.Equal("3DES", template.CryptoSettings["msPKI-Symmetric-Algorithm"]);
        Assert.Equal("168", template.CryptoSettings["mspki-symmetric-key-length"]);
        Assert.DoesNotContain(PkiAttributes.RaApplicationPolicies, template.CryptoSettings.Keys);
    }

    [Fact]
    public async Task TheSchema2EnrollmentAgentTarget_HoldsAPlainOid()
    {
        var (template, _) = await ReadAsync(PublicKeyTemplates.Esc3Target);

        Assert.Equal(2, template.SchemaVersion);
        Assert.Equal(1, template.RaSignatures);
        Assert.Equal([PkiSamples.CertificateRequestAgent], template.RaApplicationPolicies);
        Assert.Empty(template.CryptoSettings);
        Assert.True(PkiFlags.IsClientAuth(template.EffectiveEku));
    }

    [Theory]
    [InlineData(PublicKeyTemplates.Esc1Safe, true, false)]
    [InlineData(PublicKeyTemplates.Esc9, false, true)]
    [InlineData(PublicKeyTemplates.Esc9Safe, false, false)]
    public async Task TheDerivedMembers_FollowTheEnrollmentFlag(string cn, bool approval, bool noSecurityExtension)
    {
        var (template, _) = await ReadAsync(cn);

        Assert.Equal(approval, template.RequiresManagerApproval);
        Assert.Equal(noSecurityExtension, template.NoSecurityExtension);
    }

    [Fact]
    public async Task IssuancePolicies_AndTheSchema1Esc15Template()
    {
        var (esc13, _) = await ReadAsync(PublicKeyTemplates.Esc13);
        Assert.Equal([PkiSamples.ForestOid + ".500"], esc13.IssuancePolicies);

        var (esc15, _) = await ReadAsync(PublicKeyTemplates.Esc15);
        Assert.Equal(1, esc15.SchemaVersion);
        Assert.True(esc15.EnrolleeSuppliesSubject);
        Assert.Equal([PkiSamples.SecureEmail], esc15.EffectiveEku);
        Assert.False(PkiFlags.IsClientAuth(esc15.EffectiveEku));

        var (subCa, _) = await ReadAsync(PublicKeyTemplates.SubCa);
        Assert.Empty(subCa.Ekus);
        Assert.True(PkiFlags.IsAnyPurpose(subCa.EffectiveEku));
        Assert.Equal("Podrzędny urząd certyfikacji", subCa.DisplayName);
    }

    [Fact]
    public async Task EveryTemplateOfTheSample_Reads_AndPrintsOnlyItsGuid()
    {
        var sample = Shared.Value;
        foreach (var cn in PublicKeyTemplates.All)
        {
            var (template, obj) = await ReadAsync(cn);

            Assert.Equal(cn, template.Cn);
            Assert.InRange(template.SchemaVersion, 1, 4);
            Assert.NotNull(template.Validity);
            Assert.Equal(sample.Builder.Session.Snapshot(sample.PublicKey.Template(cn))!.ObjectGuid, template.ObjectGuid);
            Assert.Equal($"CertificateTemplate {template.ObjectGuid:B}", template.ToString());
            Assert.DoesNotContain(cn, template.ToString(), StringComparison.Ordinal);
            Assert.Equal(obj.Has(PkiAttributes.TemplateSchemaVersion), template.SchemaVersion > 1);
        }
    }

    [Fact]
    public void NegativeMasks_AsTheDcReturnsThem_ParseAsUnsigned()
    {
        var template = CertificateTemplate.Of(Template(
            (PkiAttributes.CertificateNameFlag, ["-2113929216"]),
            (PkiAttributes.EnrollmentFlag, ["-2147483648"]),
            (PkiAttributes.PrivateKeyFlag, ["-1"]),
            (PkiAttributes.Flags, ["4294967295"])));

        Assert.Equal(0x82000000u, template.NameFlag);
        Assert.Equal(0x80000000u, template.EnrollmentFlag);
        Assert.Equal(0xFFFFFFFFu, template.PrivateKeyFlag);
        Assert.Equal(0xFFFFFFFFu, template.GeneralFlag);
        Assert.Equal(["CT_FLAG_SUBJECT_ALT_REQUIRE_UPN", "CT_FLAG_SUBJECT_REQUIRE_DIRECTORY_PATH"], template.DecodeFlags(PkiFlagAttribute.NameFlag).Set.Select(e => e.Name));
        Assert.Equal(15, template.MinimumCaVersion);
        Assert.Equal(15, template.MinimumClientVersion);
    }

    [Fact]
    public void AnEmptyObject_ReadsAsTheDefaults()
    {
        var template = CertificateTemplate.Of(new DirectoryObject("CN=Pusty-Szablon-Ąę," + TemplatesDn, []));

        Assert.Equal("Pusty-Szablon-Ąę", template.Cn);
        Assert.Null(template.DisplayName);
        Assert.Equal(Guid.Empty, template.ObjectGuid);
        Assert.Null(template.SecurityDescriptor);
        Assert.Equal(1, template.SchemaVersion);
        Assert.Equal(0, template.Revision);
        Assert.Null(template.Oid);
        Assert.Equal(0u, template.NameFlag | template.EnrollmentFlag | template.PrivateKeyFlag | template.GeneralFlag);
        Assert.Equal(0, template.RaSignatures);
        Assert.Null(template.Validity);
        Assert.Null(template.RenewalOverlap);
        Assert.Null(template.MinimalKeySize);
        Assert.Null(template.KeyUsage);
        Assert.Empty(template.Ekus);
        Assert.True(PkiFlags.IsAnyPurpose(template.EffectiveEku));
    }

    [Fact]
    public void MalformedValues_ReadAsAbsent()
    {
        var template = CertificateTemplate.Of(Template(
            (PkiAttributes.TemplateSchemaVersion, ["0"]),
            (PkiAttributes.RaSignature, ["jeden"]),
            (PkiAttributes.CertificateNameFlag, ["0x1"]),
            (PkiAttributes.MinimalKeySize, ["dużo"]),
            (PkiAttributes.ExpirationPeriod, [new byte[] { 0x00, 0x40, 0x39, 0x87, 0x2E, 0xE1, 0xFE }]),
            (PkiAttributes.OverlapPeriod, [BitConverter.GetBytes(TimeSpan.FromDays(42).Ticks)])));

        Assert.Equal(1, template.SchemaVersion);
        Assert.Equal(0, template.RaSignatures);
        Assert.Equal(0u, template.NameFlag);
        Assert.Null(template.MinimalKeySize);
        Assert.Null(template.Validity);
        Assert.Null(template.RenewalOverlap);
    }

    [Fact]
    public void MultiValuedStrings_AreTrimmed_AndRepeatsDropped_InServerOrder()
    {
        var template = CertificateTemplate.Of(Template(
            (PkiAttributes.TemplateSchemaVersion, ["3"]),
            (PkiAttributes.ExtendedKeyUsage, [" 1.3.6.1.5.5.7.3.2", "1.3.6.1.5.5.7.3.4", "1.3.6.1.5.5.7.3.2", ""]),
            (PkiAttributes.ApplicationPolicy, ["1.3.6.1.5.5.7.3.4"]),
            (PkiAttributes.RaPolicies, [PkiSamples.ForestOid + ".501"]),
            (PkiAttributes.CertificatePolicy, [PkiSamples.ForestOid + ".500", PkiSamples.ForestOid + ".501"]),
            (PkiAttributes.SupersedeTemplates, ["Użytkownik-Stary", "Poczta-Ółówek"]),
            (PkiAttributes.TemplateOid, [PkiSamples.ForestOid + ".1.77 "])));

        Assert.Equal(3, template.SchemaVersion);
        Assert.Equal([PkiFlags.ClientAuthentication, "1.3.6.1.5.5.7.3.4"], template.Ekus);
        Assert.Equal(["1.3.6.1.5.5.7.3.4"], template.EffectiveEku);
        Assert.Equal([PkiSamples.ForestOid + ".501"], template.RaIssuancePolicies);
        Assert.Equal([PkiSamples.ForestOid + ".500", PkiSamples.ForestOid + ".501"], template.IssuancePolicies);
        Assert.Equal(["Użytkownik-Stary", "Poczta-Ółówek"], template.SupersededTemplates);
        Assert.Equal(PkiSamples.ForestOid + ".1.77", template.Oid);
    }

    [Fact]
    public void CryptoProviders_AreOrderedByTheirPriority_WithoutThePrefix()
    {
        var template = CertificateTemplate.Of(Template((PkiAttributes.DefaultCsps,
        [
            "2,Microsoft Base Cryptographic Provider v1.0",
            "Microsoft Software Key Storage Provider",
            "1,Microsoft Enhanced Cryptographic Provider v1.0",
            "10,Microsoft Strong Cryptographic Provider",
        ])));

        Assert.Equal(
        [
            "Microsoft Enhanced Cryptographic Provider v1.0", "Microsoft Base Cryptographic Provider v1.0",
            "Microsoft Strong Cryptographic Provider", "Microsoft Software Key Storage Provider",
        ], template.CryptoProviders);
    }

    [Fact]
    public void RaApplicationPolicies_BothFormats_SeveralValues_AndIncompleteTriples()
    {
        var (policies, settings) = CertificateTemplate.ParseRaApplicationPolicies(
        [
            // Schema 4 with two policy triples (one listing two OIDs), no trailing backquote, a repeated setting.
            "msPKI-RA-Application-Policies`PZPWSTR`1.3.6.1.4.1.311.20.2.1`msPKI-Hash-Algorithm`PZPWSTR`SHA384"
                + "`msPKI-RA-Application-Policies`PZPWSTR`1.3.6.1.5.5.7.3.2, 1.3.6.1.4.1.311.20.2.1`msPKI-Hash-Algorithm`PZPWSTR`SHA1`msPKI-Key-Usage`DWORD",
            // An older plain OID next to it.
            " 1.3.6.1.4.1.311.10.3.1 ",
            string.Empty,
        ]);

        Assert.Equal(["1.3.6.1.4.1.311.20.2.1", PkiFlags.ClientAuthentication, "1.3.6.1.4.1.311.10.3.1"], policies);
        Assert.Equal("SHA384", Assert.Single(settings).Value);
    }

    [Fact]
    public void RaApplicationPolicies_ASchema4ValueWithoutPolicies_HasOnlyCryptoSettings()
    {
        var template = CertificateTemplate.Of(Template(
            (PkiAttributes.TemplateSchemaVersion, ["4"]),
            (PkiAttributes.RaApplicationPolicies,
                ["msPKI-Asymmetric-Algorithm`PZPWSTR`ECDH_P256`msPKI-Hash-Algorithm`PZPWSTR`SHA256`msPKI-Key-Usage`DWORD`16777215`msPKI-Symmetric-Algorithm`PZPWSTR`AES`msPKI-Symmetric-Key-Length`DWORD`256`"])));

        Assert.Empty(template.RaApplicationPolicies);
        Assert.Equal(["msPKI-Asymmetric-Algorithm", "msPKI-Hash-Algorithm", "msPKI-Key-Usage", "msPKI-Symmetric-Algorithm", "msPKI-Symmetric-Key-Length"],
            template.CryptoSettings.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("ECDH_P256", template.CryptoSettings["msPKI-Asymmetric-Algorithm"]);
        Assert.Equal("256", template.CryptoSettings["msPKI-Symmetric-Key-Length"]);
        Assert.Throws<ArgumentNullException>(() => CertificateTemplate.ParseRaApplicationPolicies(null!));
    }

    [Fact]
    public void Of_RefusesNull() => Assert.Throws<ArgumentNullException>(() => CertificateTemplate.Of(null!));

    [Fact(Skip = "P1 wave 1 merge")]
    public async Task TheOverviewReader_FillsEveryTemplateMember()
    {
        // End to end with C1's reader: the overview search's attributes carry everything Of reads.
        var sample = Sample.Create();

        var snapshot = await PkiOverviewReader.ReadAsync(sample.Builder.Session, new PkiReadOptions(), null, CancellationToken.None);

        Assert.Equal(PublicKeyTemplates.All.Order(StringComparer.Ordinal), snapshot.Templates.Select(t => t.Cn).Order(StringComparer.Ordinal));
        var user = Assert.Single(snapshot.Templates, t => t.Cn == PublicKeyTemplates.User);
        Assert.Equal(1, user.SchemaVersion);
        Assert.Equal(0xA6000000u, user.NameFlag);
        Assert.Equal(0x1023Au, user.GeneralFlag);
        Assert.Equal(PkiPeriodUnit.Years, user.Validity?.Unit);
        Assert.Equal(["Microsoft Enhanced Cryptographic Provider v1.0"], user.CryptoProviders);
        var schema4 = Assert.Single(snapshot.Templates, t => t.Cn == PublicKeyTemplates.Esc3TargetSafe);
        Assert.Equal(4, schema4.SchemaVersion);
        Assert.Equal([PkiSamples.CertificateRequestAgent], schema4.RaApplicationPolicies);
        Assert.Equal("SHA256", schema4.CryptoSettings["msPKI-Hash-Algorithm"]);
        var esc13 = Assert.Single(snapshot.Templates, t => t.Cn == PublicKeyTemplates.Esc13);
        Assert.Equal([PkiSamples.ForestOid + ".500"], esc13.IssuancePolicies);
        Assert.All(snapshot.Templates, t => Assert.NotNull(t.SecurityDescriptor));
    }

    private static async Task<(CertificateTemplate Template, DirectoryObject Object)> ReadAsync(string cn)
    {
        var sample = Shared.Value;
        var obj = await sample.Builder.Session.GetAsync(sample.PublicKey.Template(cn), PkiAttributes.TemplateAttributes, CancellationToken.None);
        Assert.NotNull(obj);
        return (CertificateTemplate.Of(obj), obj);
    }

    private static DirectoryObject Template(params (string Name, object[] Values)[] attributes) =>
        new("CN=Szablon-Próbny," + TemplatesDn,
        [
            new AttributeValues(Attr.Cn, ["Szablon-Próbny"]),
            new AttributeValues(Attr.ObjectGuid, [new Guid("4b0c9e1d-2f3a-4c5b-8d6e-7f8091a2b3c4").ToByteArray()]),
            .. attributes.Select(a => new AttributeValues(a.Name, a.Values)),
        ]);

    private sealed record Sample(FakeDirectoryBuilder Builder, PublicKeySample PublicKey)
    {
        public static Sample Create()
        {
            var builder = new FakeDirectoryBuilder();
            var company = builder.AddSampleCompany();
            return new Sample(builder, builder.AddPublicKeySample(company));
        }

        public override string ToString() => nameof(Sample);
    }
}
