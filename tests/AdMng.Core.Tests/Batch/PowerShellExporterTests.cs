using AdMng.Core.Batch;

namespace AdMng.Core.Tests.Batch;

public class PowerShellExporterTests
{
    private const string Password = "Tajne!Haslo2026";

    private readonly PowerShellExporter _exporter = new();

    private static ChangePlan SamplePlan() => new()
    {
        Title = "Porządki w dziale IT",
        CreatedAt = new DateTimeOffset(2026, 10, 13, 14, 0, 0, TimeSpan.FromHours(2)),
        Server = "dc01.contoso.test",
        Objects =
        [
            new ObjectPlan
            {
                DistinguishedName = "CN=Jan O'Brien,OU=IT,DC=contoso,DC=test",
                DisplayName = "Jan O'Brien",
                Changes =
                [
                    new PlannedChange { Action = ChangeAction.ResetPassword, Password = Password },
                    new PlannedChange
                    {
                        Action = ChangeAction.Modify,
                        Attributes =
                        [
                            new AttributeChange("department", AttributeChangeKind.Replace, ["IT"], ["Księgowość"]),
                            new AttributeChange("otherTelephone", AttributeChangeKind.Replace, ["1"], ["111", "222"]),
                            new AttributeChange("description", AttributeChangeKind.Clear, ["stary"], []),
                            new AttributeChange("info", AttributeChangeKind.Clear, ["x"], []),
                            new AttributeChange("proxyAddresses", AttributeChangeKind.AddValues, [], ["smtp:jan@firma.pl"]),
                            new AttributeChange("url", AttributeChangeKind.RemoveValues, ["http://a"], ["http://a"]),
                            new AttributeChange("userAccountControl", AttributeChangeKind.Replace, ["512"], ["514"]),
                            new AttributeChange("pwdLastSet", AttributeChangeKind.Replace, ["1"], ["0"]),
                            new AttributeChange("lockoutTime", AttributeChangeKind.Replace, ["5"], ["0"]),
                            new AttributeChange("msExchHideFromAddressLists", AttributeChangeKind.Replace, [], ["TRUE"]),
                            new AttributeChange("msDS-SupportedEncryptionTypes", AttributeChangeKind.Replace, [], ["24"]),
                        ],
                    },
                    new PlannedChange { Action = ChangeAction.AddMember, TargetDn = "CN=GG_Sprzedaz,OU=Grupy,DC=contoso,DC=test" },
                    new PlannedChange { Action = ChangeAction.RemoveMember, TargetDn = "CN=GG_IT,OU=Grupy,DC=contoso,DC=test" },
                    new PlannedChange { Action = ChangeAction.Rename, TargetDn = @"CN=O'Brien\, Jan" },
                    new PlannedChange { Action = ChangeAction.Move, TargetDn = "OU=Sprzedaż,DC=contoso,DC=test" },
                ],
                Issues = [new ValidationIssue(IssueSeverity.Warning, IssueCodes.AdminSdHolder, "Protected by AdminSDHolder.")],
            },
            new ObjectPlan
            {
                DistinguishedName = "CN=Nikt,DC=contoso,DC=test",
                DisplayName = "Nikt",
                Changes = [new PlannedChange { Action = ChangeAction.Modify, Attributes = [new AttributeChange("title", AttributeChangeKind.Replace, [], ["x"])] }],
                Issues = [new ValidationIssue(IssueSeverity.Error, IssueCodes.NotFound, "Not found.")],
            },
            new ObjectPlan
            {
                DistinguishedName = "CN=Ewa,DC=contoso,DC=test",
                DisplayName = "Ewa",
                Issues = [new ValidationIssue(IssueSeverity.Info, IssueCodes.NoChange, "Nothing to change.", "title")],
            },
            new ObjectPlan
            {
                DistinguishedName = "OU=Stare,DC=contoso,DC=test",
                DisplayName = "Stare",
                Changes = [new PlannedChange { Action = ChangeAction.Delete, DeleteSubtree = true }],
            },
            new ObjectPlan
            {
                DistinguishedName = "CN=Anna Nowak,OU=IT,DC=contoso,DC=test",
                DisplayName = "Anna Nowak",
                ObjectClasses = ["top", "person", "organizationalPerson", "user"],
                Changes =
                [
                    new PlannedChange
                    {
                        Action = ChangeAction.Create,
                        Attributes =
                        [
                            new AttributeChange("objectClass", AttributeChangeKind.Replace, [], ["user"]),
                            new AttributeChange("cn", AttributeChangeKind.Replace, [], ["Anna Nowak"]),
                            new AttributeChange("sAMAccountName", AttributeChangeKind.Replace, [], ["anna.nowak"]),
                            new AttributeChange("givenName", AttributeChangeKind.Replace, [], ["Anna"]),
                        ],
                    },
                ],
            },
        ],
    };

    [Fact]
    public void Export_ProducesTheExpectedScript()
    {
        string[] expected =
        [
            "# Generated by AD Manager - review before running.",
            "# Title: Porządki w dziale IT",
            "# Plan created: 2026-10-13 12:00:00 UTC",
            "# Domain controller: dc01.contoso.test",
            "# Objects: 5 total, 3 with changes, 1 with errors (skipped), 1 without changes",
            "# Passwords are not included; the script prompts for each new password.",
            "#Requires -Modules ActiveDirectory",
            "$ErrorActionPreference = 'Stop'",
            "$server = 'dc01.contoso.test'",
            "",
            "# [1] Jan O'Brien - CN=Jan O'Brien,OU=IT,DC=contoso,DC=test",
            "# Warning [AdminSdHolder]: Protected by AdminSDHolder.",
            "$dn = 'CN=Jan O''Brien,OU=IT,DC=contoso,DC=test'",
            "# New password for Jan O'Brien",
            "$password = Read-Host -AsSecureString -Prompt 'New password for Jan O''Brien'",
            "Set-ADAccountPassword -Identity $dn -Reset -NewPassword $password -Server $server",
            "Remove-Variable password",
            "Set-ADObject -Identity $dn -Replace @{department='Księgowość'; otherTelephone=@('111','222'); userAccountControl=514; pwdLastSet=0; "
                + "msExchHideFromAddressLists=$true; 'msDS-SupportedEncryptionTypes'=24} -Clear description,info "
                + "-Add @{proxyAddresses='smtp:jan@firma.pl'} -Remove @{url='http://a'} -Server $server",
            "Unlock-ADAccount -Identity $dn -Server $server",
            "Set-ADGroup -Identity 'CN=GG_Sprzedaz,OU=Grupy,DC=contoso,DC=test' -Add @{member=$dn} -Server $server",
            "Set-ADGroup -Identity 'CN=GG_IT,OU=Grupy,DC=contoso,DC=test' -Remove @{member=$dn} -Server $server",
            "$dn = (Rename-ADObject -Identity $dn -NewName 'O''Brien, Jan' -Server $server -PassThru).DistinguishedName",
            "$dn = (Move-ADObject -Identity $dn -TargetPath 'OU=Sprzedaż,DC=contoso,DC=test' -Server $server -PassThru).DistinguishedName",
            "",
            "# [2] Nikt - CN=Nikt,DC=contoso,DC=test",
            "# Error [NotFound]: Not found.",
            "# SKIPPED: the object has validation errors.",
            "",
            "# [3] Ewa - CN=Ewa,DC=contoso,DC=test",
            "# Info [NoChange] (title): Nothing to change.",
            "# No changes.",
            "",
            "# [4] Stare - OU=Stare,DC=contoso,DC=test",
            "$dn = 'OU=Stare,DC=contoso,DC=test'",
            "Remove-ADObject -Identity $dn -Recursive -Server $server -Confirm:$false",
            "",
            "# [5] Anna Nowak - CN=Anna Nowak,OU=IT,DC=contoso,DC=test",
            "$dn = 'CN=Anna Nowak,OU=IT,DC=contoso,DC=test'",
            "New-ADObject -Name 'Anna Nowak' -Type 'user' -Path 'OU=IT,DC=contoso,DC=test' -OtherAttributes @{sAMAccountName='anna.nowak'; givenName='Anna'} -Server $server",
        ];

        var script = _exporter.Export(SamplePlan(), null);

        Assert.Equal(string.Join("\r\n", expected) + "\r\n", script);
    }

    [Fact]
    public void Export_NeverEmbedsPasswords()
    {
        var script = _exporter.Export(SamplePlan(), null);

        Assert.DoesNotContain(Password, script, StringComparison.Ordinal);
        Assert.Contains("Read-Host -AsSecureString", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_UsesTheGivenServerOrNone()
    {
        var plan = SamplePlan() with { Server = null };

        var overridden = _exporter.Export(SamplePlan(), "dc02.contoso.test");
        var none = _exporter.Export(plan, null);

        Assert.Contains("$server = 'dc02.contoso.test'", overridden, StringComparison.Ordinal);
        Assert.Contains("# Domain controller: dc02.contoso.test", overridden, StringComparison.Ordinal);
        Assert.DoesNotContain("-Server", none, StringComparison.Ordinal);
        Assert.DoesNotContain("$server", none, StringComparison.Ordinal);
        Assert.Contains("# Domain controller: (not set)", none, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("O'Brien", "'O''Brien'")]
    [InlineData("O\u2019Brien", "'O\u2019\u2019Brien'")]
    [InlineData("\u2018x\u201A\u201B", "'\u2018\u2018x\u201A\u201A\u201B\u201B'")]
    [InlineData("$(Remove-Item C:\\)", "'$(Remove-Item C:\\)'")]
    [InlineData("", "''")]
    public void Quote_DoublesAllSingleQuoteCharacters(string value, string expected)
    {
        Assert.Equal(expected, PowerShellExporter.Quote(value));
    }

    [Fact]
    public void Export_KeepsMultiLineNamesInsideComments()
    {
        var plan = new ChangePlan
        {
            Objects =
            [
                new ObjectPlan
                {
                    DistinguishedName = "CN=a\\0Ab,DC=x",
                    DisplayName = "Linia1\r\nRemove-ADObject",
                    Issues = [new ValidationIssue(IssueSeverity.Error, IssueCodes.InvalidValue, "bad\nvalue")],
                },
            ],
        };

        var lines = _exporter.Export(plan, null).Split("\r\n");

        Assert.DoesNotContain(lines, l => l.StartsWith("Remove-ADObject", StringComparison.Ordinal));
        Assert.All(lines.Where(l => l.Contains("Linia1", StringComparison.Ordinal)), l => Assert.StartsWith("#", l, StringComparison.Ordinal));
    }
}
