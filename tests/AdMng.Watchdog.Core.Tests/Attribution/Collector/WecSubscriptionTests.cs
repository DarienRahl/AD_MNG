using AdMng.Watchdog.Core.Attribution;

namespace AdMng.Watchdog.Core.Tests.Attribution.Collector;

/// <summary>The subscription <c>admng-dc-security</c> of stage B design 7.3, generated from the catalog.</summary>
public sealed class WecSubscriptionTests
{
    // The example of stage B design 7.3, verbatim.
    private const string Design = """"
uuid = "4f7c2e1a-9b3d-4c55-8e21-6a0d9c7b3e10"
name = "admng-dc-security"
query = """
<QueryList>
  <Query Id="0" Path="Security">
    <Select Path="Security">*[System[(EventID=4720 or EventID=4722 or EventID=4723 or EventID=4724 or EventID=4725 or EventID=4726 or EventID=4738 or EventID=4740 or EventID=4767 or EventID=4781 or EventID=4765 or EventID=4766 or EventID=4794 or EventID=4741 or EventID=4742 or EventID=4743 or EventID=4719)]]</Select>
    <Select Path="Security">*[System[(EventID=4727 or EventID=4728 or EventID=4729 or EventID=4730 or EventID=4731 or EventID=4732 or EventID=4733 or EventID=4734 or EventID=4735 or EventID=4737 or EventID=4754 or EventID=4755 or EventID=4756 or EventID=4757 or EventID=4758 or EventID=4764)]]</Select>
    <Select Path="Security">*[System[(EventID=4744 or EventID=4745 or EventID=4746 or EventID=4747 or EventID=4748 or EventID=4749 or EventID=4750 or EventID=4751 or EventID=4752 or EventID=4753 or EventID=4759 or EventID=4760 or EventID=4761 or EventID=4762 or EventID=4763)]]</Select>
    <Select Path="Security">*[System[(EventID=5136 or EventID=5137 or EventID=5138 or EventID=5139 or EventID=5141 or EventID=1102 or EventID=1104 or EventID=1100)]]</Select>
    <Select Path="Security">*[System[(EventID=4624)]] and *[EventData[Data[@Name='LogonType']='3' or Data[@Name='LogonType']='10']]</Select>
    <Select Path="Security">*[System[(EventID=4625 or EventID=4771)]]</Select>
    <Select Path="Security">*[System[(EventID=4776)]] and *[EventData[Data[@Name='Status']!='0x0']]</Select>
    <Suppress Path="Security">*[System[(EventID=5136)]] and *[EventData[Data[@Name='AttributeLDAPDisplayName']='dSCorePropagationData']]</Suppress>
  </Query>
</QueryList>
"""

[options]
enabled = true
heartbeat_interval = 900
connection_retry_count = 5
connection_retry_interval = 60
max_time = 30
max_envelope_size = 512000
read_existing_events = false
content_format = "Raw"
ignore_channel_error = true

[filter]
operation = "Only"
type = "Client"
flags = "CaseInsensitive"
targets = ["DC01$@CONTOSO.COM", "DC02$@CONTOSO.COM"]

[[outputs]]
driver = "Files"
format = "RawJson"
config = { path = "/var/lib/openwec/out/{client}/security.jsonl" }
"""";

    [Fact]
    public void TheGeneratedSubscription_IsTheDesignsExample()
    {
        var toml = WecSubscription.Toml(Guid.Parse("4f7c2e1a-9b3d-4c55-8e21-6a0d9c7b3e10"), ["DC01$@CONTOSO.COM", "DC02$@CONTOSO.COM"],
            "/var/lib/openwec/out/{client}/security.jsonl");

        Assert.Equal(Design.ReplaceLineEndings("\n") + "\n", toml);
    }

    [Fact]
    public void TheChangeSelects_CoverTheIngestionSetExactly_AtMost20IdsEach()
    {
        var ids = WecSubscription.ChangeGroups.SelectMany(g => g).ToList();

        Assert.Equal(SecurityEventCatalog.IngestionSet.Order(), ids.Order());
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(WecSubscription.ChangeGroups, g => Assert.InRange(g.Count, 1, SecurityEventQueryBuilder.MaxEventIdsPerSelect));
        Assert.All(SecurityEventCatalog.LogonRecordCodes, code => Assert.Contains($"EventID={code}", WecSubscription.Query(), StringComparison.Ordinal));
    }

    [Fact]
    public void TheQuery_IsWellFormedXml()
    {
        var document = System.Xml.Linq.XDocument.Parse(WecSubscription.Query());

        Assert.Equal(7, document.Root!.Element("Query")!.Elements("Select").Count());
        Assert.Single(document.Root.Element("Query")!.Elements("Suppress"));
    }

    [Theory]
    [InlineData("DC01@CONTOSO.COM")]
    [InlineData("DC01$")]
    [InlineData("DC01$@CONTOSO.COM\"\nuuid = \"x")]
    [InlineData("")]
    public void ATargetThatIsNotADcPrincipal_IsRefused(string target) =>
        Assert.Throws<ArgumentException>(() => WecSubscription.Toml(Guid.NewGuid(), [target], "/var/lib/openwec/out/{client}/security.jsonl"));

    [Theory]
    [InlineData("/var/lib/openwec/out/security.jsonl")]
    [InlineData("relative/{client}/security.jsonl")]
    [InlineData("/var/lib/openwec/out/{client}/a\"b")]
    public void AnUnusableOutputPath_IsRefused(string path) =>
        Assert.Throws<ArgumentException>(() => WecSubscription.Toml(Guid.NewGuid(), ["DC01$@CONTOSO.COM"], path));

    [Fact]
    public void NoTarget_IsRefused() =>
        Assert.Throws<ArgumentException>(() => WecSubscription.Toml(Guid.NewGuid(), [], "/var/lib/openwec/out/{client}/security.jsonl"));
}
