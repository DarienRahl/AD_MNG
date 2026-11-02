using System.Text;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy.AdministrativeTemplates;

public class AdmxParserTests
{
    private static AdmxFile Contoso() => AdmxParser.Parse("contoso.admx", SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmx));

    [Fact]
    public void Parse_ReadsNamespacesResourcesAndCategories()
    {
        var file = Contoso();

        Assert.Equal("contoso", file.TargetPrefix);
        Assert.Equal(SampleAdministrativeTemplates.ContosoNamespace, file.TargetNamespace);
        Assert.Equal(SampleAdministrativeTemplates.BaseNamespace, file.Prefixes["windows"]);
        Assert.Equal("1.2", file.MinRequiredRevision);
        Assert.Equal(["Aktualizacje", "Pulpit"], file.Categories.Select(c => c.Name));
        Assert.Equal("windows:WindowsComponents", file.Categories[0].ParentCategory);
        Assert.Equal((SampleAdministrativeTemplates.BaseNamespace, "WindowsComponents"), file.Resolve("windows:WindowsComponents"));
        Assert.Equal((SampleAdministrativeTemplates.ContosoNamespace, "Aktualizacje"), file.Resolve("Aktualizacje"));
        Assert.Null(file.Resolve("nieznany:Cokolwiek"));
    }

    [Fact]
    public void Parse_ReadsAPolicyWithValuesAndElements()
    {
        var policy = Contoso().Policies.Single(p => p.Name == "KonfiguracjaAktualizacji");

        Assert.Equal(PolicyClass.Machine, policy.Class);
        Assert.Equal(SampleAdministrativeTemplates.UpdatesKey, policy.Key);
        Assert.Equal("NoAutoUpdate", policy.ValueName);
        Assert.Equal("Konfiguracja", policy.Presentation);
        Assert.Equal("windows:SUPPORTED_Win10", policy.SupportedOn);
        Assert.Equal(PolicyValue.Decimal(0), policy.EnabledValue);
        Assert.Equal(PolicyValue.Decimal(1), policy.DisabledValue);
        var choice = Assert.IsType<EnumElement>(policy.Elements[0]);
        Assert.True(choice.Required);
        Assert.Equal([2UL, 3UL, 4UL], choice.Items.Select(i => i.Value.Number));
        var day = Assert.IsType<DecimalElement>(policy.Elements[1]);
        Assert.Equal((0UL, 7UL), (day.MinValue, day.MaxValue));
        var other = Assert.IsType<BooleanElement>(policy.Elements[2]);
        Assert.Equal(PolicyValue.Delete, other.FalseValue);
        Assert.True(policy.IsWritable);
    }

    [Fact]
    public void Parse_ReadsListsTextsAndNumbers()
    {
        var file = Contoso();
        var server = file.Policies.Single(p => p.Name == "SerwerAktualizacji");
        var sites = file.Policies.Single(p => p.Name == "ZaufaneWitryny");
        var drives = file.Policies.Single(p => p.Name == "MapowanieDyskow");
        var message = file.Policies.Single(p => p.Name == "Komunikat");

        Assert.Null(server.ValueName);
        Assert.Equal(SampleAdministrativeTemplates.UpdatesKey, server.EnabledList!.DefaultKey);
        Assert.Equal(200, Assert.IsType<TextElement>(server.Elements[0]).MaxLength);
        Assert.Equal(TextElement.DefaultMaxLength, Assert.IsType<TextElement>(server.Elements[1]).MaxLength);

        var list = Assert.IsType<ListElement>(sites.Elements.Single());
        Assert.Equal(string.Empty, list.ValuePrefix);
        Assert.Equal(PolicyClass.Both, sites.Class);
        Assert.True(Assert.IsType<ListElement>(drives.Elements.Single()).ExplicitValue);

        var size = Assert.IsType<DecimalElement>(message.Elements[2]);
        Assert.True(size.IsLong);
        Assert.Equal(10_000_000_000UL, size.MaxValue);
        var delay = Assert.IsType<DecimalElement>(message.Elements[3]);
        Assert.True(delay.StoreAsText);
        Assert.True(delay.Soft);
        Assert.Equal(3, Assert.IsType<MultiTextElement>(message.Elements[0]).MaxStrings);
    }

    [Fact]
    public void Parse_ReadsValueListsOfCheckBoxesAndEnumItems()
    {
        var policy = Contoso().Policies.Single(p => p.Name == "TrybPracy");

        var saver = Assert.IsType<BooleanElement>(policy.Elements[0]);
        Assert.Null(saver.TrueValue);
        Assert.Equal("Dim", saver.TrueList!.Items.Single().ValueName);
        var profile = Assert.IsType<EnumElement>(policy.Elements[1]);
        Assert.Equal(PolicyValue.String("dom"), profile.Items[1].Value);
        Assert.Equal(80UL, profile.Items[1].ValueList!.Items.Single().Value.Number);
    }

    [Fact]
    public void Parse_TakesANegativeDecimalAsItsTwosComplement()
    {
        var file = AdmxParser.Parse("x.admx", Admx("""
            <policy name="P" class="Machine" displayName="$(string.P)" key="Software\Policies\X" valueName="V">
              <enabledValue><decimal value="-1" /></enabledValue>
            </policy>
            """));

        Assert.Equal(uint.MaxValue, file.Policies.Single().EnabledValue!.Number);
    }

    [Theory]
    [InlineData("""<policy name="P" class="Computer" displayName="x" key="Software\X" />""")]
    [InlineData("""<policy name="P" class="Machine" displayName="x" />""")]
    [InlineData("""<policy name="P" class="Machine" displayName="x" key="Software\X"><elements><text id="a" valueName="b" /><text id="A" valueName="c" /></elements></policy>""")]
    [InlineData("""<policy name="P" class="Machine" displayName="x" key="Software\X"><elements><text id="a" /></elements></policy>""")]
    [InlineData("""<policy name="P" class="Machine" displayName="x" key="Software\X" valueName="v"><enabledValue><decimal value="abc" /></enabledValue></policy>""")]
    [InlineData("""<policy name="P" class="Machine" displayName="x" key="Software\X"><elements><decimal id="a" valueName="b" minValue="5" maxValue="1" /></elements></policy>""")]
    [InlineData("""<policy name="P" class="Machine" displayName="x" key="Software\X"><elements><unknown id="a" valueName="b" /></elements></policy>""")]
    [InlineData("""<policy name="P" class="Machine" displayName="x" key="Software\X" clientExtension="not-a-guid" />""")]
    public void Parse_RefusesAFileWithAnInvalidPolicy(string policy) =>
        Assert.Throws<GpoParseException>(() => AdmxParser.Parse("x.admx", Admx(policy)));

    [Fact]
    public void Parse_RefusesADocumentTypeDefinition()
    {
        var content = Encoding.UTF8.GetBytes("""
            <?xml version="1.0"?>
            <!DOCTYPE lol [<!ENTITY a "aaaaaaaaaa">]>
            <policyDefinitions><policyNamespaces><target prefix="x" namespace="X" /></policyNamespaces></policyDefinitions>
            """);

        Assert.Throws<GpoParseException>(() => AdmxParser.Parse("x.admx", content));
    }

    [Fact]
    public void Parse_RefusesAnAdmlOrAFileWithoutTarget()
    {
        Assert.Throws<GpoParseException>(() => AdmxParser.Parse("x.admx", SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.BaseAdmlEn)));
        Assert.Throws<GpoParseException>(() => AdmxParser.Parse("x.admx", Encoding.UTF8.GetBytes("<policyDefinitions><policyNamespaces /></policyDefinitions>")));
    }

    [Fact]
    public void Parse_ReadsAClientExtension()
    {
        var file = AdmxParser.Parse("x.admx", Admx("""
            <policy name="P" class="User" displayName="x" key="Software\X" valueName="v" clientExtension="{25537BA6-77A8-11D2-9B6C-0000F8080861}" />
            """));

        Assert.Equal(Guid.Parse("25537BA6-77A8-11D2-9B6C-0000F8080861"), file.Policies.Single().ClientExtension);
    }

    private static byte[] Admx(string policies) => Encoding.UTF8.GetBytes($"""
        <?xml version="1.0" encoding="utf-8"?>
        <policyDefinitions revision="1.0" schemaVersion="1.0" xmlns="http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions">
          <policyNamespaces><target prefix="x" namespace="Test.X" /></policyNamespaces>
          <resources minRequiredRevision="1.0" />
          <policies>{policies}</policies>
        </policyDefinitions>
        """);
}
