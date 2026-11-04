using System.Text;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Security;

namespace AdMng.Core.Tests.GroupPolicy.Security;

public class SecurityTemplateDocumentTests
{
    internal const string Sample = """
        [Unicode]
        Unicode=yes
        [System Access]
        MinimumPasswordLength = 12
        PasswordComplexity = 1
        [Privilege Rights]
        SeDebugPrivilege = *S-1-5-32-544
        SeRemoteInteractiveLogonRight = *S-1-5-32-544,*S-1-5-32-555,KONTOSO\Serwisanci
        [Group Membership]
        *S-1-5-32-544__Memberof =
        *S-1-5-32-544__Members = *S-1-5-21-1-2-3-512,KONTOSO\Łukasz.Żółć
        [Registry Values]
        MACHINE\System\CurrentControlSet\Control\Lsa\NoLMHash=4,1
        [File Security]
        "%SystemRoot%\System32\drivers\etc\hosts",2,"D:PAR(A;;FA;;;BA)"
        [Service General Setting]
        "Spooler",4,""
        ; komentarz
        [Version]
        signature="$CHICAGO$"
        Revision=1
        """;

    internal static byte[] Utf16(string text) => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text.ReplaceLineEndings("\r\n") + "\r\n")];

    [Fact]
    public void Parse_ThenToBytes_RoundTripsTheFileUnchanged()
    {
        var bytes = Utf16(Sample);

        var document = SecurityTemplateDocument.Parse(bytes);

        Assert.Equal(bytes, document.ToBytes());
    }

    [Fact]
    public void Reads_TheTypedSections()
    {
        var document = SecurityTemplateDocument.Parse(Utf16(Sample));

        Assert.Equal("12", document.Get(SecurityTemplateDocument.SystemAccess, "minimumpasswordlength"));
        Assert.Equal(["S-1-5-32-544", "S-1-5-32-555", @"KONTOSO\Serwisanci"], document.GetPrivilegeRights()["SeRemoteInteractiveLogonRight"]);
        var members = Assert.Single(document.GetRestrictedGroups(), g => g.Relation == RestrictedGroupRelation.Members);
        Assert.Equal(["S-1-5-21-1-2-3-512", @"KONTOSO\Łukasz.Żółć"], members.Principals);
        Assert.Empty(Assert.Single(document.GetRestrictedGroups(), g => g.Relation == RestrictedGroupRelation.MemberOf).Principals);
        Assert.Equal(new ServiceStartupSetting("Spooler", 4, string.Empty), Assert.Single(document.GetServices()));
        Assert.Equal("4,1", Assert.Single(document.GetRegistryValues()).Value);
    }

    [Fact]
    public void Edits_ChangeOnlyTheirLines_AndKeepUnknownSections()
    {
        var document = SecurityTemplateDocument.Parse(Utf16(Sample));

        document.Set(SecurityTemplateDocument.SystemAccess, "MinimumPasswordLength", "14");
        document.SetPrivilegeRight("SeDebugPrivilege", null);
        document.SetPrivilegeRight("SeBackupPrivilege", ["S-1-5-32-551", "s-1-5-32-551"]);
        document.SetRestrictedGroup("*S-1-5-32-544", RestrictedGroupRelation.Members, ["S-1-5-21-1-2-3-512"]);
        document.SetService("spooler", new ServiceStartupSetting("Spooler", 3, string.Empty));
        document.SetService("W32Time", new ServiceStartupSetting("W32Time", 2, "D:(A;;GA;;;SY)"));
        document.SetRegistryValue(@"MACHINE\System\CurrentControlSet\Control\Lsa\NoLMHash", null);

        var text = document.ToText();
        Assert.Contains("MinimumPasswordLength = 14\r\nPasswordComplexity = 1\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SeDebugPrivilege", text, StringComparison.Ordinal);
        Assert.Contains("SeBackupPrivilege = *S-1-5-32-551\r\n", text, StringComparison.Ordinal);
        Assert.Contains("*S-1-5-32-544__Members = *S-1-5-21-1-2-3-512\r\n", text, StringComparison.Ordinal);
        Assert.Contains("\"Spooler\",3,\"\"\r\n\"W32Time\",2,\"D:(A;;GA;;;SY)\"\r\n; komentarz\r\n[Version]", text, StringComparison.Ordinal);
        Assert.Contains("[Registry Values]\r\n[File Security]", text, StringComparison.Ordinal); // read from the file: kept
        Assert.Contains("[File Security]\r\n\"%SystemRoot%\\System32\\drivers\\etc\\hosts\",2,\"D:PAR(A;;FA;;;BA)\"\r\n", text, StringComparison.Ordinal);
        Assert.Equal(document.ToText(), SecurityTemplateDocument.Parse(document.ToBytes()).ToText());
    }

    [Fact]
    public void Set_OfADuplicatedKey_LeavesOneLine_TheLastPosition()
    {
        var document = SecurityTemplateDocument.Parse(Utf16("[System Access]\nLockoutBadCount = 5\nMinimumPasswordAge = 1\nlockoutbadcount = 10"));

        Assert.Equal("10", document.Get(SecurityTemplateDocument.SystemAccess, "LockoutBadCount"));
        document.Set(SecurityTemplateDocument.SystemAccess, "LockoutBadCount", "3");

        Assert.Equal("[System Access]\r\nMinimumPasswordAge = 1\r\nLockoutBadCount = 3\r\n", document.ToText());
    }

    [Fact]
    public void Set_OfTheSameValue_ChangesNothing()
    {
        var bytes = Utf16(Sample);
        var document = SecurityTemplateDocument.Parse(bytes);

        document.SetPrivilegeRight("SeDebugPrivilege", ["S-1-5-32-544"]);
        document.Set(SecurityTemplateDocument.SystemAccess, "PasswordComplexity", "1");
        document.SetService("Spooler", new ServiceStartupSetting("Spooler", 4, string.Empty));

        Assert.Equal(bytes, document.ToBytes());
    }

    [Fact]
    public void New_AndEmpty_AreTheMinimalTemplate()
    {
        var created = SecurityTemplateDocument.Parse([]);
        created.Set(SecurityTemplateDocument.SystemAccess, "EnableGuestAccount", "0");

        Assert.Equal("[Unicode]\r\nUnicode = yes\r\n[Version]\r\nsignature = \"$CHICAGO$\"\r\nRevision = 1\r\n[System Access]\r\nEnableGuestAccount = 0\r\n", created.ToText());
        Assert.Equal([0xFF, 0xFE], created.ToBytes()[..2]);
        var sink = new GpoSettingSink(SecurityTemplate.RelativePath);
        GptTmplParser.Parse(created.ToBytes(), sink);
        Assert.Equal("0", Assert.Single(sink.Settings, s => s.Key == "Computer/Security/System Access/EnableGuestAccount").Value);
    }

    [Fact]
    public void InvalidInput_IsRefused()
    {
        var document = SecurityTemplateDocument.New();

        Assert.Throws<GpoParseException>(() => SecurityTemplateDocument.Parse(Utf16("tylko tekst")));
        Assert.Throws<ArgumentException>(() => document.Set(SecurityTemplateDocument.SystemAccess, "A=B", "1"));
        Assert.Throws<ArgumentException>(() => document.Set(SecurityTemplateDocument.SystemAccess, "A", "1\r\nInjected = 1"));
        Assert.Throws<ArgumentException>(() => document.SetPrivilegeRight("SeDebugPrivilege", ["a,b"]));
    }

    [Fact]
    public void RegistryValueFormats_MatchSecedit()
    {
        Assert.Equal("4,5", SecurityTemplateDocument.DwordValue(5));
        Assert.Equal("1,\"Uwaga \"\"system\"\"\"", SecurityTemplateDocument.StringValue("Uwaga \"system\""));
        Assert.Equal("7,Wiersz pierwszy,\"Drugi, z przecinkiem\"", SecurityTemplateDocument.MultiStringValue(["Wiersz pierwszy", "Drugi, z przecinkiem"]));
    }

    [Fact]
    public void Set_OfAKeyInTwoSectionsOfTheSameName_LeavesOneLine()
    {
        var document = SecurityTemplateDocument.Parse(Utf16("[System Access]\nMinimumPasswordLength = 8\n[Version]\nRevision=1\n[System Access]\nLockoutBadCount = 5"));

        document.Set(SecurityTemplateDocument.SystemAccess, "MinimumPasswordLength", "12");

        Assert.Equal("[System Access]\r\nMinimumPasswordLength = 12\r\n[Version]\r\nRevision=1\r\n[System Access]\r\nLockoutBadCount = 5\r\n", document.ToText());

        var twice = SecurityTemplateDocument.Parse(Utf16("[System Access]\nMinimumPasswordLength = 8\n[System Access]\nMinimumPasswordLength = 10"));
        twice.Set(SecurityTemplateDocument.SystemAccess, "MinimumPasswordLength", "12");
        Assert.Equal("[System Access]\r\n[System Access]\r\nMinimumPasswordLength = 12\r\n", twice.ToText());
    }

    [Fact]
    public void RemoveAndDefineAgain_KeepsTheSectionAndItsComment_AndGivesTheBytesBack()
    {
        var bytes = Utf16("[Privilege Rights]\nSeDebugPrivilege = *S-1-5-32-544\n; zarządzane przez wzorzec v2");
        var document = SecurityTemplateDocument.Parse(bytes);

        document.SetPrivilegeRight("SeDebugPrivilege", null);
        Assert.Equal("[Privilege Rights]\r\n; zarządzane przez wzorzec v2\r\n", document.ToText());
        document.SetPrivilegeRight("SeDebugPrivilege", ["S-1-5-32-544"]);

        Assert.Equal(bytes, document.ToBytes());
    }

    [Fact]
    public void ASectionCreatedByAnEdit_GoesAgainWhenEmptied()
    {
        var bytes = Utf16(Sample);
        var document = SecurityTemplateDocument.Parse(bytes);

        document.Set(SecurityTemplateDocument.KerberosPolicy, "MaxTicketAge", "10");
        document.Remove(SecurityTemplateDocument.KerberosPolicy, "MaxTicketAge");

        Assert.Equal(bytes, document.ToBytes());
    }

    [Fact]
    public void MultiString_QuotesSemicolonsAndOuterSpaces_AndPrincipalsRefuseThem()
    {
        Assert.Equal("7,\"Dostęp ograniczony; naruszenia są ścigane.\",\" wcięty\",zwykły", SecurityTemplateDocument.MultiStringValue(["Dostęp ograniczony; naruszenia są ścigane.", " wcięty", "zwykły"]));
        Assert.Throws<ArgumentException>(() => SecurityTemplateDocument.New().SetPrivilegeRight("SeDebugPrivilege", ["KONTOSO\\a;b"]));
    }
}
