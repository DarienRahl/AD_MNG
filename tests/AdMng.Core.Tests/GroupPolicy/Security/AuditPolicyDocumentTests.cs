using System.Text;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Security;

namespace AdMng.Core.Tests.GroupPolicy.Security;

public class AuditPolicyDocumentTests
{
    private static readonly Guid Logon = SecuritySettingsCatalog.AuditSubcategories.Single(s => s.Id == "Logon").Guid;
    private static readonly Guid CredentialValidation = SecuritySettingsCatalog.AuditSubcategories.Single(s => s.Id == "CredentialValidation").Guid;

    private const string Sample = """
        Machine Name,Policy Target,Subcategory,Subcategory GUID,Inclusion Setting,Exclusion Setting,Setting Value
        ,System,Audit Logon,{0cce9215-69ae-11d9-bed3-505054503030},Success,,1
        ,,Option:CrashOnAuditFail,,Enabled,,1
        ,File System,,,,"D:(AU;SA;FA;;;WD)",
        """;

    private static byte[] Bytes(string text, bool bom = true) =>
        [.. bom ? new byte[] { 0xEF, 0xBB, 0xBF } : [], .. Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\r\n") + "\r\n")];

    [Fact]
    public void Catalog_HasTheKnownGuids()
    {
        Assert.Equal(Guid.Parse("{0cce9215-69ae-11d9-bed3-505054503030}"), Logon);
        Assert.Equal(Guid.Parse("{0cce923f-69ae-11d9-bed3-505054503030}"), CredentialValidation);
        Assert.Equal(59, SecuritySettingsCatalog.AuditSubcategories.Select(s => s.Guid).Distinct().Count());
        Assert.All(SecuritySettingsCatalog.AuditSubcategories, s => Assert.Contains(s.Category, SecuritySettingsCatalog.AdvancedAuditCategories));
    }

    [Fact]
    public void RoundTrip_KeepsTheFile_IncludingTheBom()
    {
        var bytes = Bytes(Sample);

        Assert.Equal(bytes, AuditPolicyDocument.Parse(bytes).ToBytes());
        Assert.Equal(1, AuditPolicyDocument.Parse(bytes).GetSubcategories()[Logon]);
    }

    [Fact]
    public void SetSubcategory_ReplacesAddsAndRemoves_KeepingOptionsAndSacls()
    {
        var document = AuditPolicyDocument.Parse(Bytes(Sample, bom: false));

        document.SetSubcategory(Logon, "Audit Logon", 3);
        document.SetSubcategory(CredentialValidation, "Audit Credential Validation", 2);

        Assert.Equal("""
            Machine Name,Policy Target,Subcategory,Subcategory GUID,Inclusion Setting,Exclusion Setting,Setting Value
            ,System,Audit Logon,{0cce9215-69ae-11d9-bed3-505054503030},Success and Failure,,3
            ,,Option:CrashOnAuditFail,,Enabled,,1
            ,File System,,,,"D:(AU;SA;FA;;;WD)",
            ,System,Audit Credential Validation,{0cce923f-69ae-11d9-bed3-505054503030},Failure,,2

            """.ReplaceLineEndings("\r\n"), document.ToText());

        document.SetSubcategory(Logon, "Audit Logon", null);
        var sink = new GpoSettingSink(AuditPolicyDocument.RelativePath);
        AuditCsvParser.Parse(document.ToBytes(), sink);
        Assert.Equal(["Computer/Audit/Option/CrashOnAuditFail", "Computer/Audit/File System/", "Computer/Audit/{0cce923f-69ae-11d9-bed3-505054503030}"],
            sink.Settings.Select(s => s.Key));
        Assert.Equal([0xEF, 0xBB, 0xBF], AuditPolicyDocument.Parse(Bytes(Sample)).ToBytes()[..3]);
        Assert.NotEqual(0xEF, document.ToBytes()[0]);
    }

    [Fact]
    public void ColumnsInAnotherOrder_AreWrittenInTheFilesOrder()
    {
        var document = AuditPolicyDocument.Parse(Bytes("Subcategory GUID,Setting Value,Subcategory", bom: false));

        document.SetSubcategory(Logon, "Audit Logon", 0);

        Assert.Equal("Subcategory GUID,Setting Value,Subcategory\r\n{0cce9215-69ae-11d9-bed3-505054503030},0,Audit Logon\r\n", document.ToText());
    }

    [Fact]
    public void Empty_IsANewFile_AndAFileWithoutTheColumnsFails()
    {
        var created = AuditPolicyDocument.Parse([]);
        created.SetSubcategory(Logon, "Audit Logon", 1);

        Assert.StartsWith("Machine Name,Policy Target,Subcategory,Subcategory GUID,Inclusion Setting,Exclusion Setting,Setting Value\r\n", created.ToText(), StringComparison.Ordinal);
        Assert.Throws<GpoParseException>(() => AuditPolicyDocument.Parse(Bytes("a,b,c")));
        Assert.Throws<ArgumentOutOfRangeException>(() => created.SetSubcategory(Logon, "Audit Logon", 4));
    }
}
