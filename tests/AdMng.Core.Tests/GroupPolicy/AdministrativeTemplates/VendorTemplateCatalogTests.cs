using AdMng.Core.GroupPolicy.AdministrativeTemplates;

namespace AdMng.Core.Tests.GroupPolicy.AdministrativeTemplates;

public class VendorTemplateCatalogTests
{
    private const string EdgeProducts = """
        [
          { "Product": "Stable", "Releases": [ { "ProductVersion": "131.0.2903.48", "Artifacts": [ { "ArtifactName": "msi", "Location": "https://msedge.sf.dl.delivery.mp.microsoft.com/a/MicrosoftEdgeEnterpriseX64.msi" } ] } ] },
          { "Product": "Policy", "Releases": [
            { "ProductVersion": "130.0.2849.80", "Artifacts": [
              { "ArtifactName": "zip", "Location": "https://msedge.sf.dl.delivery.mp.microsoft.com/old/MicrosoftEdgePolicyTemplates.zip", "Hash": "00", "HashAlgorithm": "SHA256", "SizeInBytes": 10 } ] },
            { "ProductVersion": "131.0.2903.48", "Artifacts": [
              { "ArtifactName": "cab", "Location": "https://msedge.sf.dl.delivery.mp.microsoft.com/new/MicrosoftEdgePolicyTemplates.cab", "Hash": "AA", "HashAlgorithm": "SHA256", "SizeInBytes": 11 },
              { "ArtifactName": "zip", "Location": "https://msedge.sf.dl.delivery.mp.microsoft.com/new/MicrosoftEdgePolicyTemplates.zip",
                "Hash": "9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08", "HashAlgorithm": "SHA256", "SizeInBytes": 15234567 } ] },
            { "ProductVersion": "132.0.1.1", "Artifacts": [
              { "ArtifactName": "zip", "Location": "https://evil.example.com/MicrosoftEdgePolicyTemplates.zip", "Hash": "00", "HashAlgorithm": "SHA256" } ] }
          ] }
        ]
        """;

    private const string FirefoxRelease = """
        {
          "tag_name": "v6.5",
          "assets": [
            { "name": "policy_templates_v6.5.zip", "size": 512000, "digest": "sha256:9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
              "browser_download_url": "https://github.com/mozilla/policy-templates/releases/download/v6.5/policy_templates_v6.5.zip" },
            { "name": "README.md", "browser_download_url": "https://github.com/mozilla/policy-templates/releases/download/v6.5/README.md" }
          ]
        }
        """;

    [Fact]
    public void Edge_TakesTheNewestPolicyRelease_OnAMicrosoftHost_PreferringTheZip()
    {
        var download = VendorTemplateCatalog.ParseEdgeProducts(EdgeProducts)!;

        Assert.Equal("131.0.2903.48", download.Version);
        Assert.EndsWith("/new/MicrosoftEdgePolicyTemplates.zip", download.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", download.Sha256);
        Assert.Equal(15_234_567, download.Size);
        Assert.False(download.IsCab);
        Assert.Null(VendorTemplateCatalog.ParseEdgeProducts("[]"));
        Assert.Null(VendorTemplateCatalog.ParseEdgeProducts("{}"));
    }

    [Fact]
    public void Firefox_TakesThePolicyTemplatesAsset()
    {
        var download = VendorTemplateCatalog.ParseFirefoxRelease(FirefoxRelease)!;

        Assert.Equal("v6.5", download.Version);
        Assert.EndsWith("policy_templates_v6.5.zip", download.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", download.Sha256);
        Assert.Null(VendorTemplateCatalog.ParseFirefoxRelease("""{ "assets": [] }"""));
    }

    [Theory]
    [InlineData(TemplateVendor.GoogleChrome, "https://dl.google.com/dl/edgedl/chrome/policy/policy_templates.zip", true)]
    [InlineData(TemplateVendor.GoogleChrome, "http://dl.google.com/dl/edgedl/chrome/policy/policy_templates.zip", false)]
    [InlineData(TemplateVendor.GoogleChrome, "https://dl.google.com.evil.example/x.zip", false)]
    [InlineData(TemplateVendor.MozillaFirefox, "https://objects.githubusercontent.com/x", true)]
    [InlineData(TemplateVendor.MozillaFirefox, "https://example.com/x", false)]
    [InlineData(TemplateVendor.MicrosoftEdge, "https://msedge.sf.dl.delivery.mp.microsoft.com/x.zip", true)]
    [InlineData(TemplateVendor.MicrosoftEdge, "https://microsoft.com.evil.example/x.zip", false)]
    public void PinnedHosts_AreHttpsOnTheVendorsHosts(TemplateVendor vendor, string url, bool pinned) =>
        Assert.Equal(pinned, VendorTemplateCatalog.IsPinnedHost(vendor, new Uri(url)));

    [Fact]
    public void Hashes_AreReadAsHexOrBase64()
    {
        Assert.Equal("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", VendorTemplateCatalog.Hex("n4bQgYhMfWWaL+qgxVrQFaO/TxsrC4Is0V1sFbDwCgg="));
        Assert.Null(VendorTemplateCatalog.Hex("abc"));
        Assert.Equal(VendorTemplateCatalog.ChromeTemplates, VendorTemplateCatalog.Chrome().Url);
    }
}
