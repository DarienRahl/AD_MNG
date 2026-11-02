using System.Globalization;
using System.Text.Json;

namespace AdMng.Core.GroupPolicy.AdministrativeTemplates;

/// <summary>The vendors whose templates AD Manager downloads itself (stable addresses; the others come from a file).</summary>
public enum TemplateVendor
{
    MicrosoftEdge,
    GoogleChrome,
    MozillaFirefox,
}

/// <summary>A package to download: where from, its version and how to check it.</summary>
/// <param name="Vendor">The vendor.</param>
/// <param name="Url">The HTTPS address of the archive.</param>
/// <param name="Version">The version the vendor states ("131.0.2903.48", "v6.4"); empty when unknown.</param>
/// <param name="Sha256">The SHA-256 the vendor publishes (lower-case hex), or null.</param>
/// <param name="Size">The size the vendor publishes, or null.</param>
public sealed record VendorDownload(TemplateVendor Vendor, Uri Url, string Version, string? Sha256, long? Size)
{
    /// <summary>The archive is a CAB (Edge offers both; the ZIP is preferred).</summary>
    public bool IsCab => Url.AbsolutePath.EndsWith(".cab", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The stable sources of vendor templates (K3, design note 6 as decided with the operator: Edge, Chrome and Firefox are
/// downloaded, Windows and Microsoft 365 Apps come from a file): the addresses, the parsing of the vendors' release
/// descriptions and the hosts a download may come from. Pure: the HTTP calls are the host's.
/// </summary>
public static class VendorTemplateCatalog
{
    /// <summary>Microsoft's release list of Edge (the "Policy" product carries the templates).</summary>
    public static readonly Uri EdgeProducts = new("https://edgeupdates.microsoft.com/api/products?view=enterprise");

    /// <summary>Google's templates of Chrome (always the current version).</summary>
    public static readonly Uri ChromeTemplates = new("https://dl.google.com/dl/edgedl/chrome/policy/policy_templates.zip");

    /// <summary>Mozilla's latest release of the Firefox policy templates on GitHub.</summary>
    public static readonly Uri FirefoxLatestRelease = new("https://api.github.com/repos/mozilla/policy-templates/releases/latest");

    /// <summary>Largest archive downloaded.</summary>
    public const long MaxDownloadBytes = 256L * 1024 * 1024;

    /// <summary>Largest release description read.</summary>
    public const int MaxDescriptionBytes = 8 * 1024 * 1024;

    /// <summary>The download address may be used: HTTPS on a host of the vendor (also after redirects).</summary>
    public static bool IsPinnedHost(TemplateVendor vendor, Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = url.IdnHost.ToLowerInvariant();
        return vendor switch
        {
            TemplateVendor.MicrosoftEdge => host == "edgeupdates.microsoft.com" || host.EndsWith(".dl.delivery.mp.microsoft.com", StringComparison.Ordinal)
                || host == "msedge.sf.dl.delivery.mp.microsoft.com" || host.EndsWith(".download.microsoft.com", StringComparison.Ordinal),
            TemplateVendor.GoogleChrome => host == "dl.google.com",
            TemplateVendor.MozillaFirefox => host is "api.github.com" or "github.com" or "objects.githubusercontent.com" or "release-assets.githubusercontent.com",
            _ => false,
        };
    }

    /// <summary>The Chrome archive (no version or hash is published: the archive is checked by its address only).</summary>
    public static VendorDownload Chrome() => new(TemplateVendor.GoogleChrome, ChromeTemplates, string.Empty, null, null);

    /// <summary>
    /// The newest templates of the Edge release list: the "Policy" product, its release with the highest version, the ZIP
    /// artifact (else the CAB) with the published SHA-256. Null when the list has none.
    /// </summary>
    /// <exception cref="JsonException">The list is not valid JSON.</exception>
    public static VendorDownload? ParseEdgeProducts(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        VendorDownload? best = null;
        Version? bestVersion = null;
        foreach (var product in document.RootElement.EnumerateArray())
        {
            if (!string.Equals(Text(product, "Product"), "Policy", StringComparison.OrdinalIgnoreCase) || !Property(product, "Releases", out var releases)
                || releases.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var release in releases.EnumerateArray())
            {
                var versionText = Text(release, "ProductVersion") ?? string.Empty;
                if (!Version.TryParse(versionText, out var version) || (bestVersion is not null && version <= bestVersion)
                    || !Property(release, "Artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var candidates = artifacts.EnumerateArray()
                    .Select(a => (Name: Text(a, "ArtifactName") ?? string.Empty, Location: Text(a, "Location"), Hash: Text(a, "Hash"),
                        Algorithm: Text(a, "HashAlgorithm"), Size: Number(a, "SizeInBytes")))
                    .Where(a => a.Location is not null && Uri.TryCreate(a.Location, UriKind.Absolute, out var uri) && IsPinnedHost(TemplateVendor.MicrosoftEdge, uri))
                    .ToList();
                var chosen = candidates.FirstOrDefault(a => a.Name.Equals("zip", StringComparison.OrdinalIgnoreCase));
                if (chosen.Location is null)
                {
                    chosen = candidates.FirstOrDefault(a => a.Name.Equals("cab", StringComparison.OrdinalIgnoreCase));
                }

                if (chosen.Location is null)
                {
                    continue;
                }

                best = new VendorDownload(TemplateVendor.MicrosoftEdge, new Uri(chosen.Location), versionText,
                    string.Equals(chosen.Algorithm, "SHA256", StringComparison.OrdinalIgnoreCase) ? Hex(chosen.Hash) : null, chosen.Size);
                bestVersion = version;
            }
        }

        return best;
    }

    /// <summary>The policy templates archive of a Firefox release (<c>policy_templates_v….zip</c>); null when the release has none.</summary>
    /// <exception cref="JsonException">The release is not valid JSON.</exception>
    public static VendorDownload? ParseFirefoxRelease(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !Property(root, "assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = Text(asset, "name") ?? string.Empty;
            var url = Text(asset, "browser_download_url");
            if (name.StartsWith("policy_templates", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsPinnedHost(TemplateVendor.MozillaFirefox, uri))
            {
                var digest = Text(asset, "digest");
                return new VendorDownload(TemplateVendor.MozillaFirefox, uri, Text(root, "tag_name") ?? string.Empty,
                    digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? Hex(digest[7..]) : null, Number(asset, "size"));
            }
        }

        return null;
    }

    /// <summary>Lower-case hex of a hash given as hex or Base64; null when it is neither 32 bytes.</summary>
    internal static string? Hex(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return null;
        }

        var text = hash.Trim();
        if (text.Length == 64 && text.All(char.IsAsciiHexDigit))
        {
            return text.ToLowerInvariant();
        }

        try
        {
            var bytes = Convert.FromBase64String(text);
            return bytes.Length == 32 ? Convert.ToHexStringLower(bytes) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool Property(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    private static string? Text(JsonElement element, string name) =>
        Property(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement element, string name) =>
        Property(element, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number
        : Property(element, name, out var text) && text.ValueKind == JsonValueKind.String && long.TryParse(text.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed
        : null;
}
