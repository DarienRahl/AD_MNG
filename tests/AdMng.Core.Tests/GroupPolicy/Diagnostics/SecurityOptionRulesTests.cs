using AdMng.Core.GroupPolicy.Diagnostics;
using static AdMng.Core.Tests.GroupPolicy.Diagnostics.DiagnosticsSnapshotBuilder;

namespace AdMng.Core.Tests.GroupPolicy.Diagnostics;

/// <summary>Weak security options of the security template (Full).</summary>
public sealed class SecurityOptionRulesTests
{
    private const string Lsa = @"MACHINE\System\CurrentControlSet\Control\Lsa\";

    private static GpoDiagnosticsResult Analyze(string containerDn, params string[] lines)
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var gpo = builder.Gpo("Zabezpieczenia – Bazowe", computer: 2);
        builder.Link(containerDn, gpo).Folder(gpo, Template(lines));
        return builder.Analyze();
    }

    [Theory]
    [InlineData(GpoFindingCodes.LmCompatibilityWeak, Lsa + "LmCompatibilityLevel=4,2", Lsa + "LmCompatibilityLevel=4,5", @"HKLM\System\CurrentControlSet\Control\Lsa!LmCompatibilityLevel")]
    [InlineData(GpoFindingCodes.LmHashStored, Lsa + "NoLMHash=4,0", Lsa + "NoLMHash=4,1", @"HKLM\System\CurrentControlSet\Control\Lsa!NoLMHash")]
    [InlineData(GpoFindingCodes.AnonymousAccessAllowed, Lsa + "RestrictAnonymousSAM=4,0", Lsa + "RestrictAnonymousSAM=4,1", @"HKLM\System\CurrentControlSet\Control\Lsa!RestrictAnonymousSAM")]
    [InlineData(GpoFindingCodes.AnonymousAccessAllowed, Lsa + "EveryoneIncludesAnonymous=4,1", Lsa + "EveryoneIncludesAnonymous=4,0", @"HKLM\System\CurrentControlSet\Control\Lsa!EveryoneIncludesAnonymous")]
    [InlineData(GpoFindingCodes.UacDisabled, @"MACHINE\Software\Microsoft\Windows\CurrentVersion\Policies\System\EnableLUA=4,0", @"MACHINE\Software\Microsoft\Windows\CurrentVersion\Policies\System\EnableLUA=4,1", @"HKLM\Software\Microsoft\Windows\CurrentVersion\Policies\System!EnableLUA")]
    [InlineData(GpoFindingCodes.PlainTextSmbPassword, @"MACHINE\System\CurrentControlSet\Services\LanmanWorkstation\Parameters\EnablePlainTextPassword=4,1", @"MACHINE\System\CurrentControlSet\Services\LanmanWorkstation\Parameters\EnablePlainTextPassword=4,0", @"HKLM\System\CurrentControlSet\Services\LanmanWorkstation\Parameters!EnablePlainTextPassword")]
    public void RegistryValueOptions_WeakValueIsAFinding_StrongIsNot(string code, string weak, string strong, string registryKey)
    {
        var finding = Findings.Single(Analyze(Ou("Stacje"), "[Registry Values]", weak), code);
        Assert.Equal("Computer/Security/Registry Values/" + registryKey, finding.SettingKey);
        Assert.Equal(GpoFindingArea.Security, finding.Area);

        Findings.None(Analyze(Ou("Stacje"), "[Registry Values]", strong), code);
    }

    [Theory]
    [InlineData(GpoFindingCodes.ReversibleEncryption, "ClearTextPassword", "1", "0", GpoFindingSeverity.High)]
    [InlineData(GpoFindingCodes.GuestEnabled, "EnableGuestAccount", "1", "0", GpoFindingSeverity.Medium)]
    [InlineData(GpoFindingCodes.AnonymousAccessAllowed, "LSAAnonymousNameLookup", "1", "0", GpoFindingSeverity.Medium)]
    public void SystemAccessOptions_WeakValueIsAFinding_StrongIsNot(string code, string key, string weak, string strong, GpoFindingSeverity severity)
    {
        var finding = Findings.Single(Analyze(Ou("Stacje"), "[System Access]", $"{key} = {weak}"), code);
        Assert.Equal(("Computer/Security/System Access/" + key, severity), (finding.SettingKey!, finding.Severity));

        Findings.None(Analyze(Ou("Stacje"), "[System Access]", $"{key} = {strong}"), code);
    }

    [Fact]
    public void LmCompatibilityWeak_NamesTheValue()
    {
        Assert.Equal(["1"], Findings.Single(Analyze(Ou("Stacje"), "[Registry Values]", Lsa + "LmCompatibilityLevel=4,1"), GpoFindingCodes.LmCompatibilityWeak).Arguments);
    }

    [Fact]
    public void SmbAndLdapSigning_OnlyInAGpoThatReachesTheDomainControllers()
    {
        string[] lines =
        [
            "[Registry Values]",
            @"MACHINE\System\CurrentControlSet\Services\LanManServer\Parameters\RequireSecuritySignature=4,0",
            @"MACHINE\System\CurrentControlSet\Services\NTDS\Parameters\LDAPServerIntegrity=4,1",
        ];

        var onDcs = Analyze(DomainControllersOu, lines);
        Findings.Single(onDcs, GpoFindingCodes.SmbSigningNotRequired);
        Assert.Equal(["1"], Findings.Single(onDcs, GpoFindingCodes.LdapSigningNotRequired).Arguments);

        var onDomain = Analyze(DomainDn, lines);
        Findings.Single(onDomain, GpoFindingCodes.SmbSigningNotRequired);
        Findings.Single(onDomain, GpoFindingCodes.LdapSigningNotRequired);

        var onWorkstations = Analyze(Ou("Stacje"), lines);
        Findings.None(onWorkstations, GpoFindingCodes.SmbSigningNotRequired);
        Findings.None(onWorkstations, GpoFindingCodes.LdapSigningNotRequired);
    }

    [Fact]
    public void SmbSigning_ADomainLinkBlockedByTheDomainControllersOu_DoesNotReachThem_UnlessEnforced()
    {
        string[] lines = ["[Registry Values]", @"MACHINE\System\CurrentControlSet\Services\LanManServer\Parameters\RequireSecuritySignature=4,0"];
        foreach (var enforced in new[] { false, true })
        {
            var builder = new DiagnosticsSnapshotBuilder();
            var gpo = builder.Gpo("Domena – SMB", computer: 2);
            builder.Link(DomainDn, gpo, enforced: enforced).Container(DomainControllersOu, blocksInheritance: true).Folder(gpo, Template(lines));

            var findings = Findings.All(builder.Analyze(), GpoFindingCodes.SmbSigningNotRequired);

            Assert.Equal(enforced, findings.Count == 1);
        }
    }

    [Fact]
    public void WeakDomainPasswordPolicy_OnTheDomainHeadOnly_OneFindingPerWeakValue()
    {
        string[] lines = ["[System Access]", "MinimumPasswordLength = 7", "PasswordComplexity = 0", "LockoutBadCount = 0"];

        var findings = Findings.All(Analyze(DomainDn, lines), GpoFindingCodes.WeakDomainPasswordPolicy);

        Assert.Equal(
            [("LockoutBadCount", "0"), ("MinimumPasswordLength", "7"), ("PasswordComplexity", "0")],
            findings.Select(f => (f.Arguments[0], f.Arguments[1])).OrderBy(f => f.Item1, StringComparer.Ordinal));
        Findings.None(Analyze(DomainDn, "[System Access]", "MinimumPasswordLength = 14", "PasswordComplexity = 1", "LockoutBadCount = 5"),
            GpoFindingCodes.WeakDomainPasswordPolicy);
        Findings.None(Analyze(Ou("Stacje"), lines), GpoFindingCodes.WeakDomainPasswordPolicy);
    }
}
