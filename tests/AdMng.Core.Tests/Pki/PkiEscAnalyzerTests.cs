using System.Security.Cryptography;
using System.Text;
using AdMng.Core.Ldap;
using AdMng.Core.Pki;
using AdMng.Core.PrivilegedAccess;
using AdMng.Testing;
using AdMng.Testing.Pki;

namespace AdMng.Core.Tests.Pki;

/// <summary>
/// The LDAP-only ESC rules (docs/dev/p-certificate-services.md 2.4) on hand-built <see cref="PkiEscSnapshot"/> records: each
/// vulnerable template at its decision-4 severity and its safe look-alikes not flagged, the approval column (suppresses /
/// manager approval only / lowers / never), <c>NotPublished</c>, <c>AssumesCaEnroll</c>, <c>EkuSourcesDiffer</c>, ESC1 + ESC2
/// together on an Any-Purpose ESS template, and a default Windows forest with zero findings.
/// </summary>
public sealed class PkiEscAnalyzerTests
{
    private const string DomainSid = "S-1-5-21-3623811015-3361044348-30300820";
    private const uint EnrolleeSuppliesSubject = 0x1;
    private const uint SubjectFromAd = 0x82000000;
    private const uint PendAllRequests = 0x2;
    private const uint NoSecurityExtension = 0x80000;
    private const string AuthenticatedUsers = "S-1-5-11";
    private const string EnterpriseDomainControllers = "S-1-5-9";
    private const string SecureEmail = "1.3.6.1.5.5.7.3.4";
    private const string EncryptingFileSystem = "1.3.6.1.4.1.311.10.3.4";
    private const string CodeSigning = "1.3.6.1.5.5.7.3.3";
    private const string NameFlagAttribute = "msPKI-Certificate-Name-Flag";

    private static readonly string DomainUsers = DomainSid + "-513";
    private static readonly string DomainComputers = DomainSid + "-515";
    private static readonly string DomainAdmins = DomainSid + "-512";
    private static readonly string DomainControllers = DomainSid + "-516";
    private static readonly string EnterpriseAdmins = DomainSid + "-519";
    private static readonly string RasAndIasServers = DomainSid + "-553";
    private static readonly string GgIt = DomainSid + "-1105";
    private static readonly string GgAccounting = DomainSid + "-1107";
    private static readonly Guid IssuingCaGuid = new("2a1b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d");
    private static readonly string IssuingCaDn = "CN=" + PkiSamples.IssuingCaName + ",CN=Enrollment Services," + PkiSamples.PublicKeyServicesDn;
    private static readonly string IssuingCaComputerDn = "CN=SRV-CA02,OU=Serwery PKI,OU=Kontoso,DC=contoso,DC=test";
    private static readonly string AdministratorDn = "CN=Administrator,CN=Users,DC=contoso,DC=test";

    // ---------------------------------------------------------------- ESC1

    [Fact]
    public void Esc1_DomainUsersEnroll_IsCritical()
    {
        var template = Template("Użytkownik-VPN-Podatny", nameFlag: EnrolleeSuppliesSubject, ekus: [PkiFlags.ClientAuthentication]);
        var result = new Esc().Add(template, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12)).Analyze();

        var finding = Assert.Single(result);
        Assert.Equal(PkiFindingCodes.EnrolleeSuppliesSubjectClientAuth, finding.Code);
        Assert.Equal(PkiFindingSeverity.Critical, finding.Severity);
        Assert.Equal(PkiFindingArea.Template, finding.Area);
        Assert.Equal(template.ObjectGuid, finding.ObjectId);
        Assert.Equal(template.Dn, finding.ObjectDn);
        Assert.Equal(["Użytkownik-VPN-Podatny", "Użytkownicy domeny", "12"], finding.Arguments);
        Assert.Equal(PkiFindingNotes.AssumesCaEnroll, finding.Notes);
        Assert.Equal([IssuingCaGuid], finding.RelatedObjectIds);
        var principal = Assert.Single(finding.Principals);
        Assert.Equal((DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12, true, "Enroll"),
            (principal.Sid, principal.Name, principal.Class, principal.AccountCount, principal.IsResolved, principal.Right));
        Assert.True(result.IsEvaluated);
        Assert.Empty(result.NotEvaluated);
    }

    [Fact]
    public void Esc1_AnOtherGroup_IsMedium_AndTheCountsAddUpToTheTotal()
    {
        var template = Template("VPN-Księgowość", nameFlag: EnrolleeSuppliesSubject, ekus: [PkiFlags.SmartCardLogon]);
        var other = new Esc().Add(template, Enroll(GgIt, "GG_IT", PrincipalClass.Other, 3)).Analyze().Single();
        Assert.Equal(PkiFindingSeverity.Medium, other.Severity);

        // Two principals: the most dangerous class decides; the accounts are summed but never above the right's total.
        var both = new Esc()
            .Add(template, [Enroll(GgIt, "GG_IT", PrincipalClass.Other, 3), Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12)],
                totals: [new TemplateRightTotal(TemplateRight.Enroll, PrincipalClass.Broad, 13, 1, [])])
            .Analyze().Single();
        Assert.Equal(PkiFindingSeverity.Critical, both.Severity);
        Assert.Equal(["VPN-Księgowość", "GG_IT, Użytkownicy domeny", "13"], both.Arguments);

        // A trustee of another domain: its count is unknown.
        var foreign = new TemplateTrustee("S-1-5-21-1-2-3-1201", null, PrincipalClass.Other, TemplateRight.Enroll, -1, -1, [], false, null);
        var partly = new Esc().Add(template, [Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12), foreign]).Analyze().Single();
        Assert.Equal(["VPN-Księgowość", "Użytkownicy domeny, S-1-5-21-1-2-3-1201", "12+"], partly.Arguments);
        Assert.False(partly.Principals[1].IsResolved);
        Assert.Equal("?", new Esc().Add(template, foreign).Analyze().Single().Arguments[2]);
    }

    public static TheoryData<string> Esc1LookAlikes() => ["manager approval", "authorized signature", "Tier 0 only", "secure e-mail only", "autoenroll only", "subject from AD"];

    [Theory]
    [MemberData(nameof(Esc1LookAlikes))]
    public void Esc1_LookAlikes_AreNotFlagged(string variant)
    {
        var template = Template("Użytkownik-VPN-Bezpieczny", nameFlag: variant == "subject from AD" ? SubjectFromAd : EnrolleeSuppliesSubject,
            enrollmentFlag: variant == "manager approval" ? PendAllRequests : 0,
            ekus: [variant == "secure e-mail only" ? SecureEmail : PkiFlags.ClientAuthentication],
            raSignatures: variant == "authorized signature" ? 1 : 0);
        var trustee = variant switch
        {
            "Tier 0 only" => Enroll(DomainAdmins, "Administratorzy domeny", PrincipalClass.Tier0, 2),
            "autoenroll only" => Trustee(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, TemplateRight.AutoEnroll),
            _ => Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12),
        };

        Assert.Empty(new Esc().Add(template, trustee).Analyze());
    }

    [Fact]
    public void Esc1_FullControlAlone_Enrolls()
    {
        var template = Template("Pełna-Kontrola", nameFlag: EnrolleeSuppliesSubject, ekus: [PkiFlags.ClientAuthentication]);

        var codes = Codes(new Esc().Add(template, Trustee(GgIt, "GG_IT", PrincipalClass.Other, TemplateRight.FullControl)).Analyze());

        Assert.Contains(PkiFindingCodes.EnrolleeSuppliesSubjectClientAuth, codes);
        Assert.Contains(PkiFindingCodes.TemplateWriteable, codes);
    }

    [Fact]
    public void NotPublished_IsOneLevelLower_WithItsNote()
    {
        var template = Template("Użytkownik-VPN-Niewystawiany", nameFlag: EnrolleeSuppliesSubject, ekus: [PkiFlags.ClientAuthentication]);

        var finding = new Esc().Add(template, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12)).Unpublished(template).Analyze().Single();

        Assert.Equal(PkiFindingSeverity.High, finding.Severity);
        Assert.Equal(PkiFindingNotes.NotPublished | PkiFindingNotes.AssumesCaEnroll, finding.Notes);
        Assert.Empty(finding.RelatedObjectIds);
    }

    [Fact]
    public void EkuSourcesDiffer_WhenOnlyOneSourceAllowsClientAuthentication()
    {
        // Schema 2: the CA issues from the application policies; Certipy and Locksmith read pKIExtendedKeyUsage. Either fires.
        var policies = Template("Polityki-Klient", nameFlag: EnrolleeSuppliesSubject, ekus: [SecureEmail], policies: [PkiFlags.ClientAuthentication]);
        var legacy = Template("Rozszerzenia-Klient", nameFlag: EnrolleeSuppliesSubject, ekus: [PkiFlags.ClientAuthentication], policies: [SecureEmail]);
        var same = Template("Zgodne-Klient", nameFlag: EnrolleeSuppliesSubject, ekus: [PkiFlags.ClientAuthentication, EncryptingFileSystem], policies: [PkiFlags.ClientAuthentication]);
        var result = new Esc()
            .Add(policies, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12))
            .Add(legacy, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12))
            .Add(same, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12))
            .Analyze();

        Assert.True(Of(result, policies).Single().Notes.HasFlag(PkiFindingNotes.EkuSourcesDiffer));
        Assert.True(Of(result, legacy).Single().Notes.HasFlag(PkiFindingNotes.EkuSourcesDiffer));
        Assert.False(Of(result, same).Single().Notes.HasFlag(PkiFindingNotes.EkuSourcesDiffer));
    }

    [Fact]
    public void WithoutTheCaEnrollAssumption_TheNoteIsNotSet()
    {
        var template = Template("Użytkownik-VPN-Podatny", nameFlag: EnrolleeSuppliesSubject, ekus: [PkiFlags.ClientAuthentication]);

        var finding = new Esc { CaGrantsAuthenticatedUsersEnroll = false }.Add(template, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12)).Analyze().Single();

        Assert.Equal(PkiFindingNotes.None, finding.Notes);
    }

    // ---------------------------------------------------------------- ESC2 (and ESC1 + ESC2 together)

    [Fact]
    public void Esc2_AnyPurpose_IsAlsoAnEnrollmentAgent()
    {
        var template = Template("Dowolny-Cel-Podatny", ekus: [PkiFlags.AnyPurpose]);

        var result = new Esc().Add(template, Enroll(GgIt, "GG_IT", PrincipalClass.Other, 4)).Analyze();

        Assert.Equal([PkiFindingCodes.AnyPurposeOrNoEku, PkiFindingCodes.EnrollmentAgentTemplate], Codes(result));
        Assert.All(result, f => Assert.Equal(PkiFindingSeverity.Medium, f.Severity));
    }

    [Fact]
    public void Esc2_NoEkuAtAll_IsAnyPurpose()
    {
        var template = Template("Bez-EKU", ekus: [], policies: []);

        var finding = Of(new Esc().Add(template, Enroll(AuthenticatedUsers, "Uwierzytelnieni użytkownicy", PrincipalClass.Broad, 40)).Analyze(), template)
            .Single(f => f.Code == PkiFindingCodes.AnyPurposeOrNoEku);

        Assert.Equal(PkiFindingSeverity.Critical, finding.Severity);
    }

    [Fact]
    public void Esc2_LookAlikes_AreNotFlagged()
    {
        var subCa = Template("SubCA", schema: 1, nameFlag: EnrolleeSuppliesSubject, ekus: []);
        var approval = Template("Dowolny-Cel-Zatwierdzany", enrollmentFlag: PendAllRequests, ekus: [PkiFlags.AnyPurpose]);
        var admins = Template("Dowolny-Cel-Bezpieczny", ekus: [PkiFlags.AnyPurpose]);

        var result = new Esc()
            .Add(subCa)
            .Add(approval, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12))
            .Add(admins, Enroll(DomainAdmins, "Administratorzy domeny", PrincipalClass.Tier0, 2))
            .Analyze();

        Assert.Empty(result);
    }

    [Fact]
    public void AnAnyPurposeTemplate_WhereTheEnrolleeSuppliesTheSubject_IsEsc1Esc2Esc3AndEsc17()
    {
        var template = Template("Wszystko-Naraz", nameFlag: EnrolleeSuppliesSubject, ekus: [PkiFlags.AnyPurpose]);

        var result = new Esc().Add(template, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12)).Analyze();

        Assert.Equal(
            [PkiFindingCodes.EnrolleeSuppliesSubjectClientAuth, PkiFindingCodes.AnyPurposeOrNoEku, PkiFindingCodes.EnrollmentAgentTemplate, PkiFindingCodes.EnrolleeSuppliesSubjectServerAuth],
            Codes(result));
        Assert.Equal([PkiFindingSeverity.Critical, PkiFindingSeverity.Critical, PkiFindingSeverity.Critical, PkiFindingSeverity.High], result.Select(f => f.Severity));
        Assert.All(result, f => Assert.Equal(template.ObjectGuid, f.ObjectId));
    }

    // ---------------------------------------------------------------- ESC3

    [Fact]
    public void Esc3_AnEnrollmentAgentTemplate_NamesItsTargets()
    {
        var agent = Template("Agent-Rejestracji-Podatny", ekus: [PkiFlags.CertificateRequestAgent]);
        var target = Target("Logowanie-W-Imieniu-Podatne");
        var unpublishedTarget = Target("Logowanie-W-Imieniu-Niewystawiane");
        var user = Template("User", schema: 1, nameFlag: 0xA6000000, enrollmentFlag: 0x29, ekus: [EncryptingFileSystem, SecureEmail, PkiFlags.ClientAuthentication]);
        var adminOnly = Template("Administrator", schema: 1, ekus: [PkiFlags.ClientAuthentication]);

        var result = new Esc()
            .Add(agent, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12))
            .Add(target, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12))
            .Add(unpublishedTarget, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12)).Unpublished(unpublishedTarget)
            .Add(user, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12))
            .Add(adminOnly)
            .Analyze();

        var finding = Of(result, agent).Single();
        Assert.Equal(PkiFindingCodes.EnrollmentAgentTemplate, finding.Code);
        Assert.Equal(PkiFindingSeverity.Critical, finding.Severity);
        Assert.Equal([IssuingCaGuid, target.ObjectGuid, user.ObjectGuid], finding.RelatedObjectIds);

        // The schema-1 target is never a finding of its own (it is the default User template).
        Assert.Empty(Of(result, user));
        var targetFinding = Of(result, target).Single();
        Assert.Equal((PkiFindingCodes.EnrollmentAgentTargetTemplate, PkiFindingSeverity.High), (targetFinding.Code, targetFinding.Severity));
        Assert.Equal([IssuingCaGuid, agent.ObjectGuid], targetFinding.RelatedObjectIds);
        Assert.Equal(PkiFindingSeverity.Medium, Of(result, unpublishedTarget).Single().Severity);
    }

    [Fact]
    public void Esc3_TheTargetWeighs_AsTheAgentTemplatesOthersCanUse()
    {
        var agent = Template("Agent-Rejestracji-IT", ekus: [PkiFlags.CertificateRequestAgent]);
        var target = Target("Logowanie-W-Imieniu-Podatne");

        var withOther = new Esc().Add(agent, Enroll(GgIt, "GG_IT", PrincipalClass.Other, 4)).Add(target, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12)).Analyze();
        Assert.Equal(PkiFindingSeverity.Medium, Of(withOther, target).Single().Severity);

        var alone = new Esc().Add(target, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12)).Analyze().Single();
        Assert.Equal((PkiFindingSeverity.Low, PkiFindingNotes.TargetOnly | PkiFindingNotes.AssumesCaEnroll), (alone.Severity, alone.Notes));

        // An agent template no CA publishes cannot be used: the target stays TargetOnly.
        var unpublishedAgent = new Esc()
            .Add(agent, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12)).Unpublished(agent)
            .Add(target, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12))
            .Analyze();
        Assert.Equal(PkiFindingSeverity.High, Of(unpublishedAgent, agent).Single().Severity);
        Assert.Equal(PkiFindingSeverity.Low, Of(unpublishedAgent, target).Single().Severity);
        Assert.True(Of(unpublishedAgent, target).Single().Notes.HasFlag(PkiFindingNotes.TargetOnly));
    }

    public static TheoryData<string> Esc3LookAlikes() => ["agent with approval", "target with approval", "no signature", "signature of another policy", "schema 1 target", "target without client authentication"];

    [Theory]
    [MemberData(nameof(Esc3LookAlikes))]
    public void Esc3_LookAlikes_AreNotFlagged(string variant)
    {
        var esc = new Esc();
        var broad = Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12);
        switch (variant)
        {
            case "agent with approval":
                esc.Add(Template("Agent-Rejestracji-Bezpieczny", enrollmentFlag: PendAllRequests, ekus: [PkiFlags.CertificateRequestAgent]), broad);
                break;
            case "target with approval":
                esc.Add(Target("Logowanie-W-Imieniu-Bezpieczne", schema: 4) with { EnrollmentFlag = PendAllRequests }, broad);
                break;
            case "no signature":
                esc.Add(Target("Bez-Podpisu") with { RaSignatures = 0 }, broad);
                break;
            case "signature of another policy":
                esc.Add(Target("Inna-Polityka") with { RaApplicationPolicies = [CodeSigning] }, broad);
                break;
            case "schema 1 target":
                esc.Add(Template("Stary-Cel", schema: 1, ekus: [PkiFlags.ClientAuthentication]), broad);
                break;
            default:
                esc.Add(Target("Cel-Bez-Logowania") with { Ekus = [SecureEmail], ApplicationPolicies = [SecureEmail] }, broad);
                break;
        }

        Assert.Empty(esc.Analyze());
    }

    [Fact]
    public void Esc3_TheAuthorizedSignature_IsTheCondition_NotAProtection()
    {
        var target = Target("Logowanie-W-Imieniu-Podatne");

        Assert.True(target.RequiresAuthorizedSignature);
        Assert.Equal(PkiFindingCodes.EnrollmentAgentTargetTemplate, new Esc().Add(target, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12)).Analyze().Single().Code);
    }

    // ---------------------------------------------------------------- ESC4

    [Fact]
    public void Esc4_AuthenticatedUsersWritingTheNameFlag_IsCritical()
    {
        var template = Template("Szablon-Zapisywalny-Podatny", ekus: [PkiFlags.ClientAuthentication]);

        var finding = new Esc().Add(template, Trustee(AuthenticatedUsers, "Uwierzytelnieni użytkownicy", PrincipalClass.Broad, TemplateRight.WriteSensitiveProperty, attribute: NameFlagAttribute)).Analyze().Single();

        Assert.Equal((PkiFindingCodes.TemplateWriteable, PkiFindingSeverity.Critical), (finding.Code, finding.Severity));
        Assert.Equal(["Szablon-Zapisywalny-Podatny", "Uwierzytelnieni użytkownicy", "WriteSensitiveProperty"], finding.Arguments);
        Assert.Equal(PkiFindingNotes.None, finding.Notes);
        Assert.Equal("WriteSensitiveProperty", Assert.Single(finding.Principals).Right);
    }

    [Fact]
    public void Esc4_TheRightsAreGraded()
    {
        var dacl = Template("Zapis-DACL");
        var other = Template("Zapis-Opisu");
        var unknown = Template("Zapis-Nieznany");
        var full = Template("Pełna-Kontrola-IT");
        var owner = Template("Właściciel-IT", enrollmentFlag: PendAllRequests);
        var mixed = Template("Mieszane");
        var result = new Esc()
            .Add(dacl, Trustee(GgIt, "GG_IT", PrincipalClass.Other, TemplateRight.WriteDacl))
            .Add(other, Trustee(AuthenticatedUsers, "Uwierzytelnieni użytkownicy", PrincipalClass.Broad, TemplateRight.WriteOtherProperty, attribute: "description"))
            .Add(unknown, Trustee(GgIt, "GG_IT", PrincipalClass.Other, TemplateRight.WriteSensitiveProperty, attribute: "5f1e2d3c-4b5a-4968-8776-a5b4c3d2e1f0"))
            .Add(full, [.. FullControl(GgIt, "GG_IT", PrincipalClass.Other)])
            .Add(owner, Trustee(GgIt, "GG_IT", PrincipalClass.Other, TemplateRight.Owner))
            .Add(mixed, [Trustee(AuthenticatedUsers, "Uwierzytelnieni użytkownicy", PrincipalClass.Broad, TemplateRight.WriteOtherProperty, attribute: "description"),
                Trustee(GgIt, "GG_IT", PrincipalClass.Other, TemplateRight.WriteOwner)])
            .Analyze();

        Assert.Equal(PkiFindingSeverity.Medium, Of(result, dacl).Single().Severity);
        Assert.Equal(PkiFindingSeverity.Low, Of(result, other).Single().Severity);
        var unknownFinding = Of(result, unknown).Single();
        Assert.Equal((PkiFindingSeverity.Medium, PkiFindingNotes.UnknownAttribute), (unknownFinding.Severity, unknownFinding.Notes));
        var fullFinding = Of(result, full).Single(f => f.Code == PkiFindingCodes.TemplateWriteable);
        Assert.Equal("FullControl", fullFinding.Arguments[2]);
        Assert.Equal(["FullControl"], fullFinding.Principals.Select(p => p.Right));

        // Approval never protects a template its writer can rewrite.
        Assert.Equal(["Właściciel-IT", "GG_IT", "Owner"], Of(result, owner).Single().Arguments);
        var mixedFinding = Of(result, mixed).Single();
        Assert.Equal(PkiFindingSeverity.Medium, mixedFinding.Severity);
        Assert.Equal(["Mieszane", "GG_IT, Uwierzytelnieni użytkownicy", "WriteOwner, WriteOtherProperty"], mixedFinding.Arguments);
    }

    [Fact]
    public void Esc4_LookAlikes_AreNotFlagged_AndAnUnpublishedTemplateIsLower()
    {
        var safe = Template("Szablon-Zapisywalny-Bezpieczny");
        var enrollOnly = Template("Tylko-Rejestracja");
        var unpublished = Template("Szablon-Niewystawiany");

        var result = new Esc()
            .Add(safe, Trustee(EnterpriseAdmins, "Administratorzy przedsiębiorstwa", PrincipalClass.Tier0, TemplateRight.WriteSensitiveProperty, attribute: NameFlagAttribute))
            .Add(enrollOnly, [Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12), Trustee(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, TemplateRight.AutoEnroll)])
            .Add(unpublished, Trustee(AuthenticatedUsers, "Uwierzytelnieni użytkownicy", PrincipalClass.Broad, TemplateRight.WriteAllProperties)).Unpublished(unpublished)
            .Analyze();

        var finding = Assert.Single(result);
        Assert.Equal(unpublished.ObjectGuid, finding.ObjectId);
        Assert.Equal((PkiFindingSeverity.High, PkiFindingNotes.NotPublished), (finding.Severity, finding.Notes));
    }

    // ---------------------------------------------------------------- ESC5

    [Fact]
    public void Esc5_WritesOnPkiObjects_OutsideTier0()
    {
        var ntAuth = "CN=NTAuthCertificates," + PkiSamples.PublicKeyServicesDn;
        var esc = new Esc()
            .Grant(ntAuth, PkiAclTargetKind.NtAuth, GgIt, AclRight.WriteAllProperties, PrincipalClass.Other, "GG_IT")
            .Grant(PkiSamples.PublicKeyServicesDn, PkiAclTargetKind.Container, AuthenticatedUsers, AclRight.WriteDacl, PrincipalClass.Broad)
            .Grant(IssuingCaDn, PkiAclTargetKind.EnrollmentService, GgAccounting, AclRight.GenericAll, PrincipalClass.Other, "GG_Księgowość_Certyfikaty")
            .Grant(IssuingCaDn.ToUpperInvariant(), PkiAclTargetKind.EnrollmentService, GgAccounting, AclRight.WriteOwner, PrincipalClass.Other, "GG_Księgowość_Certyfikaty")
            .Grant(IssuingCaComputerDn, PkiAclTargetKind.CaComputer, GgIt, AclRight.AllExtendedRights, PrincipalClass.Other, "GG_IT")
            .Grant(IssuingCaComputerDn, PkiAclTargetKind.CaComputer, GgIt, AclRight.WriteAllowedToActOnBehalf, PrincipalClass.Other, "GG_IT");

        var result = esc.Analyze();

        Assert.All(result, f => Assert.Equal((PkiFindingCodes.PkiObjectWriteable, PkiFindingArea.PkiObject), (f.Code, f.Area)));
        Assert.Equal(4, result.Count);
        var container = result.Single(f => f.ObjectDn == PkiSamples.PublicKeyServicesDn);
        Assert.Equal(PkiFindingSeverity.Critical, container.Severity);
        Assert.Equal(["Public Key Services", AuthenticatedUsers, "WriteDacl"], container.Arguments);

        var nt = result.Single(f => f.ObjectDn == ntAuth);
        Assert.Equal((PkiFindingSeverity.Medium, "NTAuthCertificates", "GG_IT", "WriteAllProperties"), (nt.Severity, nt.Arguments[0], nt.Arguments[1], nt.Arguments[2]));

        var service = result.Single(f => f.ObjectId == IssuingCaGuid);
        Assert.Equal([PkiSamples.IssuingCaName, "GG_Księgowość_Certyfikaty", "GenericAll, WriteOwner"], service.Arguments);

        var computer = result.Single(f => f.ObjectDn == IssuingCaComputerDn);
        Assert.Equal([IssuingCaGuid], computer.RelatedObjectIds);
        Assert.Equal("SRV-CA02", computer.Arguments[0]);
        Assert.Equal("WriteAllowedToActOnBehalf, AllExtendedRights", computer.Arguments[2]);
        Assert.All(result, f => Assert.Equal(PkiFindingNotes.None, f.Notes));
    }

    [Fact]
    public void Esc5_LookAlikes_AreNotFlagged()
    {
        var ntAuth = "CN=NTAuthCertificates," + PkiSamples.PublicKeyServicesDn;
        var result = new Esc()
            .Grant(ntAuth, PkiAclTargetKind.NtAuth, EnterpriseAdmins, AclRight.GenericAll, PrincipalClass.Tier0)
            .Grant(ntAuth, PkiAclTargetKind.NtAuth, GgIt, AclRight.DeleteChild, PrincipalClass.Other)
            .Grant(IssuingCaDn, PkiAclTargetKind.EnrollmentService, GgIt, AclRight.AllExtendedRights, PrincipalClass.Other)
            .Grant("CN=User,CN=Certificate Templates," + PkiSamples.PublicKeyServicesDn, PkiAclTargetKind.Template, AuthenticatedUsers, AclRight.WriteTemplateProperty, PrincipalClass.Broad)
            .Grant(AdministratorDn, PkiAclTargetKind.Tier0Account, GgIt, AclRight.GenericAll, PrincipalClass.Other)
            .Analyze();

        Assert.Empty(result);
    }

    [Fact]
    public void Esc5_AnUnclassifiedTrustee_CountsAsOther_AndAnOidObjectIsNamed()
    {
        var oid = new PkiOidObject("CN=600.0F1E2D3C,CN=OID," + PkiSamples.PublicKeyServicesDn, Guid.NewGuid(), PkiSamples.ForestOid + ".600", "Polityka działu sprzedaży", 2, null);
        var esc = new Esc { Oids = [oid] };
        esc.AclGrants.Add(new PkiAclGrant(oid.Dn, PkiAclTargetKind.Oid, "S-1-5-21-9-8-7-1500", AclRight.Owner, null));

        var finding = esc.Analyze().Single();

        Assert.Equal((PkiFindingSeverity.Medium, (Guid?)oid.ObjectGuid, "Polityka działu sprzedaży"), (finding.Severity, finding.ObjectId, finding.Arguments[0]));
        var principal = Assert.Single(finding.Principals);
        Assert.Equal((PrincipalClass.Other, false, -1, "Owner"), (principal.Class, principal.IsResolved, principal.AccountCount, principal.Right));
    }

    [Fact]
    public void Esc5_OnEveryOidObjectOfALargeForest_StaysWithinATimeBudget()
    {
        // CN=OID can hold thousands of objects; a non-Tier 0 template administrator who created them all holds an explicit
        // grant on each – one ESC5 finding per object, each named from the snapshot.
        var oids = Enumerable.Range(0, 3_000)
            .Select(i => new PkiOidObject($"CN={i}.0F1E2D3C4B5A6978,CN=OID," + PkiSamples.PublicKeyServicesDn, GuidOf("OID " + i), $"{PkiSamples.ForestOid}.{i}", $"Zasada wystawiania {i:D4}", 2, null))
            .ToList();
        var esc = new Esc { Oids = oids };
        foreach (var oid in oids)
        {
            esc.Grant(oid.Dn, PkiAclTargetKind.Oid, GgIt, AclRight.GenericAll, PrincipalClass.Other, "GG_IT");
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = esc.Analyze();
        stopwatch.Stop();

        Assert.Equal(3_000, result.Count);
        Assert.Contains(result, f => f.ObjectId == oids[1_234].ObjectGuid && f.Arguments[0] == "Zasada wystawiania 1234");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"ESC5 took {stopwatch.Elapsed}.");
    }

    // ---------------------------------------------------------------- ESC9

    [Fact]
    public void Esc9_NoSecurityExtension_ByClass_AndApprovalLowersIt()
    {
        var broad = Template("Bez-Rozszerzenia-SID-Podatny", enrollmentFlag: NoSecurityExtension, ekus: [PkiFlags.ClientAuthentication]);
        var other = Template("Bez-Rozszerzenia-SID-IT", enrollmentFlag: NoSecurityExtension, ekus: [PkiFlags.PkinitClientAuthentication]);
        var approved = Template("Bez-Rozszerzenia-SID-Zatwierdzany", enrollmentFlag: NoSecurityExtension | PendAllRequests, ekus: [PkiFlags.ClientAuthentication]);
        var approvedOther = Template("Bez-Rozszerzenia-SID-Podpis", enrollmentFlag: NoSecurityExtension, ekus: [PkiFlags.ClientAuthentication], raSignatures: 1);
        var result = new Esc()
            .Add(broad, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12))
            .Add(other, Enroll(GgIt, "GG_IT", PrincipalClass.Other, 4))
            .Add(approved, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12))
            .Add(approvedOther, Enroll(GgIt, "GG_IT", PrincipalClass.Other, 4))
            .Analyze();

        Assert.Equal(PkiFindingSeverity.High, Of(result, broad).Single().Severity);
        Assert.Equal(PkiFindingSeverity.Medium, Of(result, other).Single().Severity);
        var lowered = Of(result, approved).Single();
        Assert.Equal((PkiFindingCodes.NoSecurityExtensionClientAuth, PkiFindingSeverity.Medium), (lowered.Code, lowered.Severity));
        Assert.Equal(PkiFindingNotes.ApprovalRequired | PkiFindingNotes.AssumesCaEnroll, lowered.Notes);
        Assert.Equal(PkiFindingSeverity.Low, Of(result, approvedOther).Single().Severity);
    }

    [Fact]
    public void Esc9_LookAlikes_AreNotFlagged()
    {
        var result = new Esc()
            .Add(Template("Bez-Rozszerzenia-SID-Bezpieczny", ekus: [PkiFlags.ClientAuthentication]), Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12))
            .Add(Template("Bez-Rozszerzenia-Poczta", enrollmentFlag: NoSecurityExtension, ekus: [SecureEmail]), Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12))
            .Add(Template("Bez-Rozszerzenia-Admin", enrollmentFlag: NoSecurityExtension, ekus: [PkiFlags.ClientAuthentication]), Enroll(DomainAdmins, "Administratorzy domeny", PrincipalClass.Tier0, 2))
            .Analyze();

        Assert.Empty(result);
    }

    // ---------------------------------------------------------------- ESC13

    [Fact]
    public void Esc13_APolicyLinkedToATier0Group_IsCritical_WhoeverEnrolls()
    {
        var high = Oid("500", "Polityka wysokiej pewności", "CN=GG_Administratorzy_PKI,OU=Grupy,OU=Kontoso,DC=contoso,DC=test");
        var template = Template("Wysoka-Pewność-Podatny", ekus: [PkiFlags.ClientAuthentication], issuance: [high.Oid]);
        var esc = new Esc { Oids = [high], LinkedGroups = [new PkiLinkedGroup("cn=gg_administratorzy_pki,ou=Grupy,ou=Kontoso,dc=contoso,dc=test", "GG_Administratorzy_PKI", PrivilegeLevel.Tier0)] }
            .Add(template, Enroll(GgIt, "GG_IT", PrincipalClass.Other, 4));

        var finding = esc.Analyze().Single();

        Assert.Equal((PkiFindingCodes.IssuancePolicyLinkedToGroup, PkiFindingSeverity.Critical), (finding.Code, finding.Severity));
        Assert.Equal(["Wysoka-Pewność-Podatny", "GG_IT", "4", "GG_Administratorzy_PKI"], finding.Arguments);
        Assert.Equal([IssuingCaGuid, high.ObjectGuid], finding.RelatedObjectIds);
        Assert.Equal(PkiFindingNotes.AssumesCaEnroll, finding.Notes);
    }

    [Fact]
    public void Esc13_AnotherGroup_IsHigh_AndApprovalLowersIt()
    {
        var policy = Oid("502", "Polityka działu IT", "CN=GG_Pomoc_Techniczna,OU=Grupy,OU=Kontoso,DC=contoso,DC=test");
        var plain = Template("Pewność-IT", ekus: [PkiFlags.ClientAuthentication], issuance: [policy.Oid]);
        var approved = Template("Pewność-IT-Zatwierdzany", enrollmentFlag: PendAllRequests, ekus: [PkiFlags.ClientAuthentication], issuance: [policy.Oid]);
        var esc = new Esc { Oids = [policy], LinkedGroups = [new PkiLinkedGroup(policy.GroupLinkDn!, "GG_Pomoc_Techniczna", PrivilegeLevel.Elevated)] }
            .Add(plain, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12))
            .Add(approved, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12));

        var result = esc.Analyze();

        Assert.Equal(PkiFindingSeverity.High, Of(result, plain).Single().Severity);
        var lowered = Of(result, approved).Single();
        Assert.Equal(PkiFindingSeverity.Medium, lowered.Severity);
        Assert.True(lowered.Notes.HasFlag(PkiFindingNotes.ApprovalRequired));
    }

    [Fact]
    public void Esc13_LookAlikes_AreNotFlagged()
    {
        var linked = Oid("500", "Polityka wysokiej pewności", "CN=GG_Administratorzy_PKI,OU=Grupy,OU=Kontoso,DC=contoso,DC=test");
        var unlinked = Oid("501", "Polityka średniej pewności", null);
        var unresolved = Oid("503", "Polityka obcej domeny", "CN=Obca,DC=child,DC=contoso,DC=test");
        var esc = new Esc { Oids = [linked, unlinked, unresolved], LinkedGroups = [new PkiLinkedGroup(linked.GroupLinkDn!, "GG_Administratorzy_PKI", PrivilegeLevel.Tier0)] }
            .Add(Template("Wysoka-Pewność-Bezpieczny", ekus: [PkiFlags.ClientAuthentication], issuance: [unlinked.Oid]), Enroll(GgIt, "GG_IT", PrincipalClass.Other, 4))
            .Add(Template("Obca-Pewność", ekus: [PkiFlags.ClientAuthentication], issuance: [unresolved.Oid]), Enroll(GgIt, "GG_IT", PrincipalClass.Other, 4))
            .Add(Template("Inny-OID", ekus: [PkiFlags.ClientAuthentication], issuance: [linked.Oid + ".1"]), Enroll(GgIt, "GG_IT", PrincipalClass.Other, 4))
            .Add(Template("Pewność-Poczta", ekus: [SecureEmail], issuance: [linked.Oid]), Enroll(GgIt, "GG_IT", PrincipalClass.Other, 4))
            .Add(Template("Pewność-Admin", ekus: [PkiFlags.ClientAuthentication], issuance: [linked.Oid]), Enroll(DomainAdmins, "Administratorzy domeny", PrincipalClass.Tier0, 2));

        Assert.Empty(esc.Analyze());
    }

    // ---------------------------------------------------------------- ESC14

    [Fact]
    public void Esc14_WeakMappings_OnTier0Accounts_AndOnTheOthers()
    {
        var esc = new Esc
        {
            MappedAccounts =
            [
                Account(AdministratorDn, "Administrator", true, "X509:<I>C=PL,CN=Kontoso Wystawiający CA<S>DC=test,DC=contoso,CN=Users,CN=Administrator", "X509:<RFC822>admin@contoso.test"),
                Account("CN=Konto Kopii,CN=Users,DC=contoso,DC=test", "Konto Kopii", true, "X509:<I>C=PL,CN=Kontoso Wystawiający CA<SR>0063", "Kerberos:kopia@CONTOSO.TEST"),
                Account("CN=Łukasz Wiśniewski,OU=Sprzedaż,DC=contoso,DC=test", "Łukasz Wiśniewski", false, "X509:<RFC822>lukasz.wisniewski@contoso.test"),
                Account("CN=Grażyna Dąbrowska,OU=Księgowość,DC=contoso,DC=test", "Grażyna Dąbrowska", false, "X509:<S>CN=Grażyna Dąbrowska", "X509:<RFC822>grazyna@contoso.test"),
                Account("CN=Jan Kowalski,OU=Księgowość,DC=contoso,DC=test", "Jan Kowalski", false, "X509:<I>C=PL,CN=Kontoso Wystawiający CA<SR>0063"),
                Account("CN=Paweł Lewandowski,OU=IT,DC=contoso,DC=test", "Paweł Lewandowski", false, "Kerberos:pawel.lewandowski@CONTOSO.TEST"),
            ],
        };

        var result = esc.Analyze();

        Assert.Equal(2, result.Count);
        var tier0 = result[0];
        Assert.Equal((PkiFindingCodes.WeakExplicitMapping, PkiFindingSeverity.High, PkiFindingArea.Account), (tier0.Code, tier0.Severity, tier0.Area));
        Assert.Equal(["Administrator", "X509IssuerSubject, X509Rfc822", string.Empty], tier0.Arguments);
        Assert.Equal(AdministratorDn, tier0.ObjectDn);
        Assert.Empty(tier0.Principals);
        var others = result[1];
        Assert.Equal(PkiFindingSeverity.Low, others.Severity);
        Assert.Equal(["2", "X509SubjectOnly, X509Rfc822", string.Empty], others.Arguments);
        Assert.Null(others.ObjectDn);
    }

    [Fact]
    public void Esc14_WritesOfAltSecurityIdentities_OnTier0Accounts()
    {
        var krbtgt = "CN=krbtgt,CN=Users,DC=contoso,DC=test";
        var result = new Esc { MappedAccounts = [Account(AdministratorDn, "Administrator", true, "X509:<I>C=PL,CN=Kontoso<SR>0063")] }
            .Grant(AdministratorDn, PkiAclTargetKind.Tier0Account, GgIt, AclRight.WriteAltSecurityIdentities, PrincipalClass.Other, "GG_IT")
            .Grant(krbtgt, PkiAclTargetKind.Tier0Account, AuthenticatedUsers, AclRight.WriteAllProperties, PrincipalClass.Broad)
            .Grant(krbtgt, PkiAclTargetKind.Tier0Account, DomainAdmins, AclRight.WriteAltSecurityIdentities, PrincipalClass.Tier0)
            .Grant(krbtgt, PkiAclTargetKind.Tier0Account, GgIt, AclRight.GenericAll, PrincipalClass.Other)
            .Analyze();

        Assert.Equal(2, result.Count);
        var critical = result[0];
        Assert.Equal((PkiFindingSeverity.Critical, krbtgt), (critical.Severity, critical.ObjectDn));
        Assert.Equal(["krbtgt", "WriteAllProperties", AuthenticatedUsers], critical.Arguments);
        var medium = result[1];
        Assert.Equal((PkiFindingSeverity.Medium, AdministratorDn), (medium.Severity, medium.ObjectDn));
        Assert.Equal(["Administrator", "WriteAltSecurityIdentities", "GG_IT"], medium.Arguments);
        Assert.Equal("WriteAltSecurityIdentities", Assert.Single(medium.Principals).Right);
    }

    [Fact]
    public void Esc14_OnTheMappedAccountsOfALargeDomain_StaysWithinATimeBudget()
    {
        // A smart-card forest maps every account explicitly: 100 000 mapped accounts, 50 Tier 0 accounts others can write.
        var accounts = new List<PkiAccountFact>(100_050);
        for (var i = 0; i < 100_000; i++)
        {
            accounts.Add(new PkiAccountFact($"CN=Użytkownik {i:D6},OU=Łódź,OU=Kontoso,DC=contoso,DC=test", $"Użytkownik {i:D6}", $"{DomainSid}-{10_000 + i}", false, false,
                [AltSecurityIdentity.Parse($"X509:<I>C=PL,CN=Kontoso Wystawiający CA<SR>{i & 0xFF:X2}{i >> 8 & 0xFF:X2}")]));
        }

        var esc = new Esc { MappedAccounts = accounts };
        for (var i = 0; i < 50; i++)
        {
            var dn = $"CN=Administrator {i:D2},OU=Tier0,OU=Kontoso,DC=contoso,DC=test";
            accounts.Add(new PkiAccountFact(dn, $"Administrator {i:D2}", $"{DomainSid}-{500_000 + i}", false, true, [AltSecurityIdentity.Parse("X509:<SKI>8D5E1F0A")]));
            esc.Grant(dn, PkiAclTargetKind.Tier0Account, GgIt, AclRight.WriteAltSecurityIdentities, PrincipalClass.Other, "GG_IT");
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = esc.Analyze();
        stopwatch.Stop();

        Assert.Equal(50, result.Count);
        Assert.All(result, f => Assert.Equal(PkiFindingSeverity.Medium, f.Severity));
        Assert.Contains(result, f => f.Arguments[0] == "Administrator 07");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"ESC14 took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void WithoutThePkiAclPass_Esc5AndTheMappingWritesAreNotEvaluated()
    {
        var esc = new Esc
        {
            NotEvaluated = [PkiRuleGroup.AccessControl],
            MappedAccounts = [Account(AdministratorDn, "Administrator", true, "X509:<S>CN=Administrator")],
        };
        esc.Grant(AdministratorDn, PkiAclTargetKind.Tier0Account, GgIt, AclRight.WriteAltSecurityIdentities, PrincipalClass.Other)
            .Grant("CN=NTAuthCertificates," + PkiSamples.PublicKeyServicesDn, PkiAclTargetKind.NtAuth, GgIt, AclRight.GenericAll, PrincipalClass.Other)
            .Add(Template("Szablon-Zapisywalny-Podatny"), Trustee(AuthenticatedUsers, "Uwierzytelnieni użytkownicy", PrincipalClass.Broad, TemplateRight.WriteAllProperties));

        var result = esc.Analyze();

        Assert.Equal([PkiRuleGroup.AccessControl], result.NotEvaluated);
        Assert.Equal([PkiFindingCodes.TemplateWriteable, PkiFindingCodes.WeakExplicitMapping], Codes(result));
        Assert.Equal(PkiFindingSeverity.High, result.Single(f => f.Code == PkiFindingCodes.WeakExplicitMapping).Severity);
    }

    // ---------------------------------------------------------------- ESC15

    [Fact]
    public void Esc15_ASchemaOneTemplateWithASuppliedSubject_IsMedium()
    {
        var template = Template("Stary-Szablon-V1-Podatny", schema: 1, nameFlag: EnrolleeSuppliesSubject, ekus: [SecureEmail]);
        var other = Template("Stary-Szablon-V1-IT", schema: 1, nameFlag: EnrolleeSuppliesSubject, ekus: [SecureEmail]);

        var result = new Esc()
            .Add(template, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12))
            .Add(other, Enroll(GgIt, "GG_IT", PrincipalClass.Other, 4))
            .Analyze();

        var finding = Of(result, template).Single();
        Assert.Equal((PkiFindingCodes.SchemaV1EnrolleeSuppliesSubject, PkiFindingSeverity.Medium), (finding.Code, finding.Severity));
        Assert.Equal(PkiFindingNotes.PendingCaPatch | PkiFindingNotes.AssumesCaEnroll, finding.Notes);
        Assert.Equal(PkiFindingSeverity.Medium, Of(result, other).Single().Severity);
    }

    [Fact]
    public void Esc15_LookAlikes_AreNotFlagged_AndApprovalLowersIt()
    {
        var schemaTwo = Template("Stary-Szablon-V1-Bezpieczny", schema: 2, nameFlag: EnrolleeSuppliesSubject, ekus: [SecureEmail]);
        var user = Template("User", schema: 1, nameFlag: 0xA6000000, enrollmentFlag: 0x29, ekus: [EncryptingFileSystem, SecureEmail, PkiFlags.ClientAuthentication]);
        var approved = Template("Stary-Szablon-V1-Zatwierdzany", schema: 1, nameFlag: EnrolleeSuppliesSubject, enrollmentFlag: PendAllRequests, ekus: [SecureEmail]);
        var both = Template("Stary-Szablon-V1-Niewystawiany", schema: 1, nameFlag: EnrolleeSuppliesSubject, enrollmentFlag: PendAllRequests, ekus: [SecureEmail]);

        var result = new Esc()
            .Add(schemaTwo, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12))
            .Add(user, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12))
            .Add(approved, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12))
            .Add(both, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12)).Unpublished(both)
            .Analyze();

        Assert.Empty(Of(result, schemaTwo));
        Assert.Empty(Of(result, user));
        Assert.Equal(PkiFindingSeverity.Low, Of(result, approved).Single().Severity);
        var info = Of(result, both).Single();
        Assert.Equal(PkiFindingSeverity.Info, info.Severity);
        Assert.Equal(PkiFindingNotes.ApprovalRequired | PkiFindingNotes.NotPublished | PkiFindingNotes.AssumesCaEnroll | PkiFindingNotes.PendingCaPatch, info.Notes);
    }

    // ---------------------------------------------------------------- ESC17

    [Fact]
    public void Esc17_ServerAuthenticationWithASuppliedSubject()
    {
        var computers = Template("Serwer-WWW-Podatny", nameFlag: EnrolleeSuppliesSubject, ekus: [PkiFlags.ServerAuthentication]);
        var it = Template("Serwer-WWW-IT", nameFlag: EnrolleeSuppliesSubject, ekus: [PkiFlags.ServerAuthentication]);

        var result = new Esc()
            .Add(computers, Enroll(DomainComputers, "Komputery domeny", PrincipalClass.Broad, 30))
            .Add(it, Enroll(GgIt, "GG_IT", PrincipalClass.Other, 4))
            .Analyze();

        Assert.Equal((PkiFindingCodes.EnrolleeSuppliesSubjectServerAuth, PkiFindingSeverity.High), (Of(result, computers).Single().Code, Of(result, computers).Single().Severity));
        Assert.Equal(PkiFindingSeverity.Medium, Of(result, it).Single().Severity);
    }

    [Fact]
    public void Esc17_LookAlikes_AreNotFlagged()
    {
        var result = new Esc()
            .Add(Template("Serwer-WWW-Bezpieczny", nameFlag: EnrolleeSuppliesSubject, ekus: [PkiFlags.ServerAuthentication]), Enroll(DomainAdmins, "Administratorzy domeny", PrincipalClass.Tier0, 2))
            .Add(Template("Serwer-WWW-Zatwierdzany", nameFlag: EnrolleeSuppliesSubject, enrollmentFlag: PendAllRequests, ekus: [PkiFlags.ServerAuthentication]), Enroll(DomainComputers, "Komputery domeny", PrincipalClass.Broad, 30))
            .Add(Template("Serwer-WWW-Z-AD", nameFlag: 0x08000000, ekus: [PkiFlags.ServerAuthentication]), Enroll(DomainComputers, "Komputery domeny", PrincipalClass.Broad, 30))
            .Analyze();

        Assert.Empty(result);
    }

    // ---------------------------------------------------------------- the forest, the result

    [Fact]
    public void ADefaultWindowsForest_HasNoFinding()
    {
        var esc = new Esc
        {
            MappedAccounts = [Account("CN=Jan Kowalski,OU=Księgowość,DC=contoso,DC=test", "Jan Kowalski", false, "X509:<I>C=PL,CN=Kontoso Wystawiający CA<SR>0063")],
        };
        var domainUsers = Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 120);
        var domainComputers = Enroll(DomainComputers, "Komputery domeny", PrincipalClass.Broad, 80);
        var dcs = new[] { Enroll(DomainControllers, "Kontrolery domeny", PrincipalClass.Tier0, 2), Enroll(EnterpriseDomainControllers, "ENTERPRISE DOMAIN CONTROLLERS", PrincipalClass.Tier0, 2) };
        esc.Add(Template("User", schema: 1, nameFlag: 0xA6000000, enrollmentFlag: 0x29, ekus: [EncryptingFileSystem, SecureEmail, PkiFlags.ClientAuthentication]), domainUsers)
            .Add(Template("ClientAuth", schema: 1, nameFlag: 0x82000000, enrollmentFlag: 0x20, ekus: [PkiFlags.ClientAuthentication]), domainUsers)
            .Add(Template("EFS", schema: 1, nameFlag: 0xA6000000, enrollmentFlag: 0x29, ekus: [EncryptingFileSystem]), domainUsers)
            .Add(Template("Machine", schema: 1, nameFlag: 0x18000000, ekus: [PkiFlags.ClientAuthentication, PkiFlags.ServerAuthentication]), domainComputers)
            .Add(Template("DomainController", schema: 1, nameFlag: 0x19000000, enrollmentFlag: 0x31, ekus: [PkiFlags.ClientAuthentication, PkiFlags.ServerAuthentication]), dcs)
            .Add(Template("DomainControllerAuthentication", schema: 2, nameFlag: 0x18000000, enrollmentFlag: 0x28, ekus: [PkiFlags.ClientAuthentication, PkiFlags.ServerAuthentication, PkiFlags.SmartCardLogon]), dcs)
            .Add(Template("KerberosAuthentication", schema: 2, nameFlag: 0x08400000, enrollmentFlag: 0x28, ekus: [PkiFlags.ClientAuthentication, PkiFlags.ServerAuthentication, PkiFlags.SmartCardLogon, PkiFlags.KdcAuthentication]),
                [.. dcs, Trustee(DomainControllers, "Kontrolery domeny", PrincipalClass.Tier0, TemplateRight.AutoEnroll)])
            .Add(Template("RASAndIASServer", schema: 2, nameFlag: 0x18000000, ekus: [PkiFlags.ClientAuthentication, PkiFlags.ServerAuthentication]),
                Enroll(RasAndIasServers, "Serwery RAS i IAS", PrincipalClass.Other, 0))
            .Add(Template("WebServer", schema: 1, nameFlag: EnrolleeSuppliesSubject, ekus: [PkiFlags.ServerAuthentication]))
            .Add(Template("SubCA", schema: 1, nameFlag: EnrolleeSuppliesSubject, ekus: []))
            .Add(Template("CA", schema: 1, nameFlag: EnrolleeSuppliesSubject, ekus: []))
            .Add(Template("Administrator", schema: 1, nameFlag: 0xA6000000, enrollmentFlag: 0x29, ekus: [EncryptingFileSystem, SecureEmail, PkiFlags.ClientAuthentication, CodeSigning]))
            .Add(Template("EnrollmentAgent", schema: 1, nameFlag: 0x82000000, ekus: [PkiFlags.CertificateRequestAgent]))
            .Add(Template("EnrollmentAgentOffline", schema: 1, nameFlag: EnrolleeSuppliesSubject, ekus: [PkiFlags.CertificateRequestAgent]))
            .Add(Template("SmartcardLogon", schema: 1, nameFlag: 0x82000000, ekus: [PkiFlags.ClientAuthentication, PkiFlags.SmartCardLogon]))
            .Add(Template("CEPEncryption", schema: 1, nameFlag: EnrolleeSuppliesSubject, ekus: ["1.3.6.1.4.1.311.20.2.1"]))
            .Add(Template("OCSPResponseSigning", schema: 3, nameFlag: 0x18000000, ekus: ["1.3.6.1.5.5.7.3.9"]))
            .Add(Template("Workstation", schema: 2, nameFlag: 0x18000000, ekus: [PkiFlags.ClientAuthentication]))
            .Add(Template("CodeSigning", schema: 1, nameFlag: 0xA2000000, ekus: [CodeSigning]));

        var result = esc.Analyze();

        Assert.Empty(result);
        Assert.True(result.IsEvaluated);
        Assert.Empty(result.Notices);
        Assert.Empty(result.NotEvaluated);
    }

    [Fact]
    public void TheFindings_ComeMostSevereFirst_ThenInTheOrderOfTheCatalogue()
    {
        var esc = new Esc
        {
            MappedAccounts = [Account("CN=Łukasz Wiśniewski,OU=Sprzedaż,DC=contoso,DC=test", "Łukasz Wiśniewski", false, "X509:<RFC822>lukasz.wisniewski@contoso.test")],
        };
        esc.Add(Template("B-Serwer", nameFlag: EnrolleeSuppliesSubject, ekus: [PkiFlags.ServerAuthentication]), Enroll(DomainComputers, "Komputery domeny", PrincipalClass.Broad, 30))
            .Add(Template("A-VPN", nameFlag: EnrolleeSuppliesSubject, ekus: [PkiFlags.ClientAuthentication]), Enroll(GgIt, "GG_IT", PrincipalClass.Other, 4))
            .Add(Template("C-VPN", nameFlag: EnrolleeSuppliesSubject, ekus: [PkiFlags.ClientAuthentication]), Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12))
            .Add(Template("D-Zapis"), Trustee(GgIt, "GG_IT", PrincipalClass.Other, TemplateRight.WriteDacl));

        var result = esc.Analyze();

        Assert.Equal(
            [
                (PkiFindingSeverity.Critical, PkiFindingCodes.EnrolleeSuppliesSubjectClientAuth, "C-VPN"),
                (PkiFindingSeverity.High, PkiFindingCodes.EnrolleeSuppliesSubjectServerAuth, "B-Serwer"),
                (PkiFindingSeverity.Medium, PkiFindingCodes.EnrolleeSuppliesSubjectClientAuth, "A-VPN"),
                (PkiFindingSeverity.Medium, PkiFindingCodes.TemplateWriteable, "D-Zapis"),
                (PkiFindingSeverity.Low, PkiFindingCodes.WeakExplicitMapping, "1"),
            ],
            result.Select(f => (f.Severity, f.Code, f.Arguments[0])));
        Assert.All(result, f => Assert.True(f.Severity <= PkiFindingCodes.Find(f.Code).Severity, f.Code));
        Assert.All(result, f => Assert.Equal(f.Code == PkiFindingCodes.IssuancePolicyLinkedToGroup ? 4 : 3, f.Arguments.Count));
    }

    [Fact]
    public void ATemplateWithoutReadableRights_IsNotEvaluated_NeverSafe()
    {
        var template = Template("Bez-Deskryptora", nameFlag: EnrolleeSuppliesSubject, ekus: [PkiFlags.ClientAuthentication]);
        var missing = Template("Bez-Praw", nameFlag: EnrolleeSuppliesSubject, ekus: [PkiFlags.ClientAuthentication]);
        var esc = new Esc();
        esc.Templates.Add(template);
        esc.Rights.Add(new TemplateRights(template.Cn, template.ObjectGuid, false, [Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12)], []));
        esc.Templates.Add(missing);
        esc.Notices.Add(new PkiReadNotice(PkiReadNoticeCode.SecurityDescriptorUnreadable, template.Dn, template.ObjectGuid));
        esc.Notices.Add(new PkiReadNotice(PkiReadNoticeCode.TrusteeUnresolved, "CN=Obca,DC=child,DC=contoso,DC=test", null));

        var result = esc.Analyze();

        Assert.Empty(result);
        Assert.True(result.IsEvaluated);
        Assert.Equal(
            [(PkiReadNoticeCode.SecurityDescriptorUnreadable, (Guid?)template.ObjectGuid), (PkiReadNoticeCode.TrusteeUnresolved, null), (PkiReadNoticeCode.SecurityDescriptorUnreadable, missing.ObjectGuid)],
            result.Notices.Select(n => (n.Code, n.ObjectGuid)));
    }

    [Fact]
    public void ADeniedPkiBase_IsNotEvaluated()
    {
        var esc = new Esc { MappedAccounts = [Account(AdministratorDn, "Administrator", true, "X509:<S>CN=Administrator")] };
        esc.Notices.Add(new PkiReadNotice(PkiReadNoticeCode.BaseAccessDenied, PkiSamples.PublicKeyServicesDn, null));

        var result = esc.Analyze();

        Assert.False(result.IsEvaluated);
        Assert.Equal([PkiRuleGroup.Enrollment, PkiRuleGroup.AccessControl], result.NotEvaluated);
        Assert.Equal(PkiFindingCodes.WeakExplicitMapping, Assert.Single(result).Code);
        Assert.Contains("not evaluated", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AFinding_PrintsNoValue()
    {
        var template = Template("Użytkownik-VPN-Podatny", nameFlag: EnrolleeSuppliesSubject, ekus: [PkiFlags.ClientAuthentication]);

        var finding = new Esc().Add(template, Enroll(DomainUsers, "Użytkownicy domeny", PrincipalClass.Broad, 12)).Analyze().Single();

        Assert.Equal($"EnrolleeSuppliesSubjectClientAuth Critical Template {template.ObjectGuid:B}", finding.ToString());
    }

    [Fact]
    public void Lower_NeverGoesBelowInfo()
    {
        Assert.Equal(PkiFindingSeverity.High, PkiEscAnalyzer.Lower(PkiFindingSeverity.Critical));
        Assert.Equal(PkiFindingSeverity.Info, PkiEscAnalyzer.Lower(PkiFindingSeverity.Low));
        Assert.Equal(PkiFindingSeverity.Info, PkiEscAnalyzer.Lower(PkiFindingSeverity.Info));
    }

    [Theory]
    [InlineData(PkiReadNoticeCode.BaseNotFound, true)]
    [InlineData(PkiReadNoticeCode.BaseAccessDenied, false)]
    public async Task TheReader_WithoutAPki_SkipsTheAclPass_AndCarriesTheNotice(PkiReadNoticeCode code, bool evaluated)
    {
        var builder = new FakeDirectoryBuilder();
        builder.AddSampleCompany();
        var options = new PrivilegedAccessOptions { DomainSid = builder.DomainSid, RootDomainSid = builder.DomainSid };
        var graph = new GroupGraph([]);
        var context = new PrivilegeScanContext { Session = builder.Session, Groups = graph, Options = options, Now = PkiSamples.Now };
        var snapshot = PkiSnapshot.Empty(PkiSamples.ConfigurationDn, PkiSamples.Now,
            [new PkiReadNotice(code, PkiSamples.PublicKeyServicesDn, null), new PkiReadNotice(PkiReadNoticeCode.CertificateMalformed, null, Guid.NewGuid())]);
        var mapped = Account(AdministratorDn, "Administrator", true, "X509:<RFC822>administrator@contoso.test");
        var expansion = new PkiEnrollmentExpansion(new Dictionary<string, TemplateRights>(StringComparer.OrdinalIgnoreCase), [mapped], 7, graph, options, []);
        var progress = new ListProgress<PkiEscProgress>();

        var escSnapshot = await PkiEscReader.ReadAsync(context, snapshot, expansion, new PkiEscReadOptions(CaGrantsAuthenticatedUsersEnroll: false), progress, TestContext.Current.CancellationToken);
        var result = PkiEscAnalyzer.Analyze(escSnapshot);

        Assert.Empty(escSnapshot.AclGrants);
        Assert.False(escSnapshot.CaGrantsAuthenticatedUsersEnroll);
        Assert.Same(mapped, Assert.Single(escSnapshot.MappedAccounts));

        // The overview's own notices (a malformed certificate) stay in the overview.
        Assert.Equal([code], escSnapshot.Notices.Select(n => n.Code));
        Assert.Equal(evaluated, result.IsEvaluated);
        Assert.Equal(PkiFindingCodes.WeakExplicitMapping, Assert.Single(result).Code);
        Assert.Equal([PkiEscStage.AccessControl, PkiEscStage.AccessControl, PkiEscStage.Principals, PkiEscStage.Principals, PkiEscStage.Completed], progress.Items.Select(p => p.Stage));
    }

    [Fact]
    public async Task TheReader_HonoursCancellation()
    {
        var builder = new FakeDirectoryBuilder();
        var options = new PrivilegedAccessOptions();
        var graph = new GroupGraph([]);
        var context = new PrivilegeScanContext { Session = builder.Session, Groups = graph, Options = options, Now = PkiSamples.Now };
        var expansion = new PkiEnrollmentExpansion(new Dictionary<string, TemplateRights>(), [], 0, graph, options, []);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PkiEscReader.ReadAsync(context, PkiSnapshot.Empty(PkiSamples.ConfigurationDn, PkiSamples.Now), expansion, new PkiEscReadOptions(), null, cancelled.Token));
    }

    [Fact]
    public async Task TheSample_ReadEndToEnd_FlagsTheVulnerableTemplates_AndNothingElse()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        var sample = builder.AddPublicKeySample(company);
        var session = builder.Session;
        var options = new PrivilegedAccessOptions { DomainSid = builder.DomainSid, RootDomainSid = builder.DomainSid };
        var snapshot = await PkiOverviewReader.ReadAsync(session, new PkiReadOptions(), null, ct);
        var graph = await GroupGraph.ReadAsync(session, null, 500, ct);
        var types = await AclObjectTypes.ReadAsync(session, 500, ct);
        var classifier = new PrincipalClassifier(builder.DomainSid, builder.DomainSid, options, graph,
            [.. snapshot.EnterpriseCas.Select(ca => ca.ComputerSid).OfType<string>()]);
        var expansion = await TemplateRightsEvaluator.ExpandAsync(session, snapshot.Templates, new PkiExpansionContext(graph, types, classifier, options), null, ct);
        var context = new PrivilegeScanContext { Session = session, Groups = graph, Options = options, Now = PkiSamples.Now };

        var escSnapshot = await PkiEscReader.ReadAsync(context, snapshot, expansion, new PkiEscReadOptions(), null, ct);
        var result = PkiEscAnalyzer.Analyze(escSnapshot);

        string CodeOf(string cn, string code) => cn + ":" + code;
        var byTemplate = result.Where(f => f.Area == PkiFindingArea.Template)
            .Select(f => CodeOf(snapshot.Templates.Single(t => t.ObjectGuid == f.ObjectId).Cn, f.Code) + ":" + f.Severity)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            new[]
            {
                CodeOf(PublicKeyTemplates.Esc1, PkiFindingCodes.EnrolleeSuppliesSubjectClientAuth) + ":Critical",
                CodeOf(PublicKeyTemplates.Esc1NotPublished, PkiFindingCodes.EnrolleeSuppliesSubjectClientAuth) + ":High",
                CodeOf(PublicKeyTemplates.Esc2, PkiFindingCodes.AnyPurposeOrNoEku) + ":Medium",
                CodeOf(PublicKeyTemplates.Esc2, PkiFindingCodes.EnrollmentAgentTemplate) + ":Medium",
                CodeOf(PublicKeyTemplates.Esc3, PkiFindingCodes.EnrollmentAgentTemplate) + ":Critical",
                CodeOf(PublicKeyTemplates.Esc3Target, PkiFindingCodes.EnrollmentAgentTargetTemplate) + ":High",
                CodeOf(PublicKeyTemplates.Esc4, PkiFindingCodes.TemplateWriteable) + ":Critical",
                CodeOf(PublicKeyTemplates.Esc9, PkiFindingCodes.NoSecurityExtensionClientAuth) + ":High",
                CodeOf(PublicKeyTemplates.Esc13, PkiFindingCodes.IssuancePolicyLinkedToGroup) + ":Critical",
                CodeOf(PublicKeyTemplates.Esc15, PkiFindingCodes.SchemaV1EnrolleeSuppliesSubject) + ":Medium",
                CodeOf(PublicKeyTemplates.Esc17, PkiFindingCodes.EnrolleeSuppliesSubjectServerAuth) + ":High",
            }.Order(StringComparer.Ordinal),
            byTemplate);
        var flagged = result.Where(f => f.Area == PkiFindingArea.Template).Select(f => f.ObjectId).ToHashSet();
        Assert.All(PublicKeyTemplates.Safe.Concat(PublicKeyTemplates.Defaults),
            cn => Assert.DoesNotContain(snapshot.Templates.Single(t => t.Cn == cn).ObjectGuid, flagged));

        var esc5 = result.Single(f => f.Code == PkiFindingCodes.PkiObjectWriteable);
        Assert.True(DistinguishedName.AreEqual(sample.NtAuth, esc5.ObjectDn));
        Assert.Equal(PkiFindingSeverity.Medium, esc5.Severity);
        var esc14 = result.Where(f => f.Code == PkiFindingCodes.WeakExplicitMapping).Select(f => (f.Severity, f.Arguments[0])).ToList();
        Assert.Contains((PkiFindingSeverity.High, "Administrator"), esc14);
        Assert.Contains((PkiFindingSeverity.Low, "1"), esc14);
        Assert.Contains((PkiFindingSeverity.Medium, "Administrator"), esc14);
        Assert.Contains(escSnapshot.LinkedGroups, g => g.Level == PrivilegeLevel.Tier0 && DistinguishedName.AreEqual(g.Dn, sample.LinkedGroup));
    }

    // ---------------------------------------------------------------- builders

    private static IReadOnlyList<string> Codes(IEnumerable<PkiFinding> findings) => [.. findings.Select(f => f.Code)];

    private static IReadOnlyList<PkiFinding> Of(IEnumerable<PkiFinding> findings, CertificateTemplate template) => [.. findings.Where(f => f.ObjectId == template.ObjectGuid)];

    private static Guid GuidOf(string name) => new(MD5.HashData(Encoding.UTF8.GetBytes(name)));

    private static CertificateTemplate Template(
        string cn,
        int schema = 2,
        uint nameFlag = SubjectFromAd,
        uint enrollmentFlag = 0,
        IReadOnlyList<string>? ekus = null,
        IReadOnlyList<string>? policies = null,
        int raSignatures = 0,
        IReadOnlyList<string>? raPolicies = null,
        IReadOnlyList<string>? issuance = null)
    {
        ekus ??= [PkiFlags.ClientAuthentication];
        return new CertificateTemplate
        {
            Dn = DistinguishedName.Parse("CN=Certificate Templates," + PkiSamples.PublicKeyServicesDn).Child("CN", cn).ToString(),
            Cn = cn,
            DisplayName = cn,
            ObjectGuid = GuidOf(cn),
            SchemaVersion = schema,
            NameFlag = nameFlag,
            EnrollmentFlag = enrollmentFlag,
            Ekus = ekus,
            ApplicationPolicies = policies ?? (schema >= 2 ? ekus : []),
            RaSignatures = raSignatures,
            RaApplicationPolicies = raPolicies ?? [],
            IssuancePolicies = issuance ?? [],
        };
    }

    /// <summary>An ESC3 condition-2 template: one agent signature with Certificate Request Agent, client authentication.</summary>
    private static CertificateTemplate Target(string cn, int schema = 2) =>
        Template(cn, schema: schema, ekus: [PkiFlags.ClientAuthentication, PkiFlags.SmartCardLogon], raSignatures: 1, raPolicies: [PkiFlags.CertificateRequestAgent]);

    private static PkiOidObject Oid(string suffix, string displayName, string? groupDn) =>
        new($"CN={suffix}.8D5E1F0A2B3C4D5E,CN=OID," + PkiSamples.PublicKeyServicesDn, GuidOf("OID " + suffix), PkiSamples.ForestOid + "." + suffix, displayName, 2, groupDn);

    private static TemplateTrustee Trustee(string sid, string name, PrincipalClass principalClass, TemplateRight right, int count = 5, string? attribute = null) =>
        new(sid, name, principalClass, right, count, 0, [], true, attribute);

    private static TemplateTrustee Enroll(string sid, string name, PrincipalClass principalClass, int count) => Trustee(sid, name, principalClass, TemplateRight.Enroll, count);

    /// <summary>Full control as the evaluator expands it (FullControl and every right it implies).</summary>
    private static IEnumerable<TemplateTrustee> FullControl(string sid, string name, PrincipalClass principalClass) =>
        new[] { TemplateRight.FullControl, TemplateRight.Enroll, TemplateRight.AutoEnroll, TemplateRight.WriteDacl, TemplateRight.WriteOwner, TemplateRight.WriteAllProperties }
            .Select(right => Trustee(sid, name, principalClass, right));

    private static PkiAccountFact Account(string dn, string name, bool tier0, params string[] mappings) =>
        new(dn, name, DomainSid + "-" + (GuidOf(dn).GetHashCode() & 0x7FFF), false, tier0, [.. mappings.Select(AltSecurityIdentity.Parse)]);

    /// <summary>Collects the reports synchronously (<see cref="Progress{T}"/> would post them).</summary>
    private sealed class ListProgress<T> : IProgress<T>
    {
        public List<T> Items { get; } = [];

        public void Report(T value) => Items.Add(value);
    }

    /// <summary>A hand-built <see cref="PkiEscSnapshot"/>: the templates with their trustee-level rights and the issuing CA.</summary>
    private sealed class Esc
    {
        private readonly HashSet<string> _unpublished = new(StringComparer.OrdinalIgnoreCase);

        public List<CertificateTemplate> Templates { get; } = [];

        public List<TemplateRights> Rights { get; } = [];

        public List<PkiAclGrant> AclGrants { get; } = [];

        public Dictionary<string, PrincipalClass> Classes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<PkiReadNotice> Notices { get; } = [];

        public IReadOnlyList<PkiOidObject> Oids { get; init; } = [];

        public IReadOnlyList<PkiLinkedGroup> LinkedGroups { get; init; } = [];

        public IReadOnlyList<PkiAccountFact> MappedAccounts { get; init; } = [];

        public IReadOnlyList<PkiRuleGroup> NotEvaluated { get; init; } = [];

        public bool CaGrantsAuthenticatedUsersEnroll { get; init; } = true;

        /// <summary>A template whose descriptor also holds the default trustees (Domain Admins owner and full control, Enterprise Admins full control).</summary>
        public Esc Add(CertificateTemplate template, params TemplateTrustee[] trustees) => Add(template, (IEnumerable<TemplateTrustee>)trustees);

        public Esc Add(CertificateTemplate template, IEnumerable<TemplateTrustee> trustees, IReadOnlyList<TemplateRightTotal>? totals = null)
        {
            Templates.Add(template);
            Rights.Add(new TemplateRights(template.Cn, template.ObjectGuid, true,
            [
                .. FullControl(DomainAdmins, "Administratorzy domeny", PrincipalClass.Tier0),
                .. FullControl(EnterpriseAdmins, "Administratorzy przedsiębiorstwa", PrincipalClass.Tier0),
                Trustee(DomainAdmins, "Administratorzy domeny", PrincipalClass.Tier0, TemplateRight.Owner),
                .. trustees,
            ], totals ?? []));
            return this;
        }

        public Esc Unpublished(CertificateTemplate template)
        {
            _unpublished.Add(template.Cn);
            return this;
        }

        public Esc Grant(string dn, PkiAclTargetKind kind, string sid, AclRight right, PrincipalClass principalClass, string? name = null)
        {
            AclGrants.Add(new PkiAclGrant(dn, kind, sid, right, null));
            Classes[sid] = principalClass;
            if (name is not null)
            {
                Names[sid] = name;
            }

            return this;
        }

        public PkiEscResult Analyze()
        {
            var ca = new PkiEnrollmentService(IssuingCaDn, IssuingCaGuid, PkiSamples.IssuingCaName, PkiSamples.IssuingCaName, PkiSamples.IssuingCaHost,
                IssuingCaComputerDn, DomainSid + "-1300", [], [.. Templates.Select(t => t.Cn).Where(cn => !_unpublished.Contains(cn))], [], true);
            return PkiEscAnalyzer.Analyze(new PkiEscSnapshot(Templates, Rights, [ca], Oids, LinkedGroups, AclGrants, Classes, MappedAccounts, CaGrantsAuthenticatedUsersEnroll)
            {
                Notices = Notices,
                NotEvaluated = NotEvaluated,
                TrusteeNames = Names,
            });
        }
    }
}
