using System.Net.Http.Headers;
using System.Security.Cryptography;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;

namespace AdMng.Presentation.GroupPolicy.AdministrativeTemplates;

/// <summary>Why a package of templates cannot be used.</summary>
public enum PolicyTemplatePackageError
{
    /// <summary>The file or folder does not exist.</summary>
    NotFound,

    /// <summary>The file type is not a template package (ZIP, CAB, MSI, EXE, ADMX or a folder).</summary>
    Unsupported,

    /// <summary>The package holds no ADMX file.</summary>
    NoTemplates,

    /// <summary>MSI, CAB and EXE packages are unpacked on Windows only.</summary>
    WindowsOnly,

    /// <summary>An installer or self-extracting archive without a valid signature of its vendor is not run.</summary>
    Unsigned,

    /// <summary>The package could not be unpacked.</summary>
    ExtractionFailed,

    /// <summary>The download failed (network, HTTP status, a redirect outside the vendor's hosts).</summary>
    DownloadFailed,

    /// <summary>The downloaded archive does not match the hash or size the vendor publishes.</summary>
    HashMismatch,

    /// <summary>The package is too large.</summary>
    TooLarge,
}

/// <summary>A package of templates cannot be used (<see cref="Error"/>); the message is English and value-free.</summary>
public sealed class PolicyTemplatePackageException : Exception
{
    public PolicyTemplatePackageException()
    {
    }

    public PolicyTemplatePackageException(string message)
        : base(message)
    {
    }

    public PolicyTemplatePackageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PolicyTemplatePackageException(PolicyTemplatePackageError error, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    public PolicyTemplatePackageError Error { get; } = PolicyTemplatePackageError.ExtractionFailed;
}

/// <summary>
/// Unpacks the installers and archives only Windows can open (Windows templates MSI, Microsoft 365 Apps EXE, Edge CAB) and
/// checks their Authenticode signature (implemented by the App; none elsewhere).
/// </summary>
public interface IPolicyPackageExtractor
{
    /// <summary>The package of an MSI, EXE or CAB file (signature checked first; a temporary folder deleted right after).</summary>
    /// <exception cref="PolicyTemplatePackageException">Unsigned, not unpacked, or no templates.</exception>
    Task<PolicyTemplatePackage> ExtractAsync(string file, CancellationToken cancellationToken);
}

/// <summary>
/// Loads packages of ADMX templates for "Zaktualizuj szablony" (K3): a folder, a ZIP archive, an ADMX file (its folder),
/// the installers and archives of <see cref="IPolicyPackageExtractor"/>, the templates of this computer, and the downloads
/// of the vendors with stable addresses (<see cref="VendorTemplateCatalog"/>: Edge, Chrome, Firefox – HTTPS only on the
/// vendors' hosts, also after redirects, with the published SHA-256 checked when there is one). Archives are held in
/// memory; nothing is written except the temporary CAB of an Edge download that only offers a CAB.
/// </summary>
public sealed class PolicyTemplatePackageLoader(IPolicyPackageExtractor? extractor = null, ILocalPolicyDefinitions? local = null,
    Func<HttpMessageHandler>? handler = null)
{
    private readonly Func<HttpMessageHandler> _handler = handler ?? (() => new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 });

    /// <summary>The file types offered in the open dialog.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".zip", ".cab", ".msi", ".exe", ".admx"];

    /// <summary>Unpacking MSI, EXE and CAB packages is possible here.</summary>
    public bool CanExtractInstallers => extractor is not null;

    /// <summary>The templates of this computer as a package (null: none).</summary>
    public PolicyTemplatePackage? LocalStore() => local?.Folder is { } folder && Directory.Exists(folder)
        ? PolicyTemplatePackage.FromFolder(folder, folder)
        : null;

    /// <summary>A folder of templates (an extracted package, the operator's own ADMX).</summary>
    public Task<PolicyTemplatePackage> LoadFolderAsync(string folder, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        return Task.Run(() => Checked(Directory.Exists(folder)
            ? PolicyTemplatePackage.FromFolder(Path.GetFileName(folder.TrimEnd('\\', '/')), folder)
            : throw new PolicyTemplatePackageException(PolicyTemplatePackageError.NotFound, "The folder does not exist.")), cancellationToken);
    }

    /// <summary>A package file: ZIP, CAB, MSI, EXE, or an ADMX file (then its folder with the culture folders beside it).</summary>
    public async Task<PolicyTemplatePackage> LoadFileAsync(string file, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        if (!File.Exists(file))
        {
            throw new PolicyTemplatePackageException(PolicyTemplatePackageError.NotFound, "The file does not exist.");
        }

        var extension = Path.GetExtension(file).ToLowerInvariant();
        switch (extension)
        {
            case ".zip":
                var length = new FileInfo(file).Length;
                if (length > PolicyTemplatePackage.MaxArchiveBytes)
                {
                    throw new PolicyTemplatePackageException(PolicyTemplatePackageError.TooLarge, "The archive is too large.");
                }

                var bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
                return Checked(FromZip(Path.GetFileName(file), bytes));
            case ".admx":
                return await LoadFolderAsync(Path.GetDirectoryName(Path.GetFullPath(file))!, cancellationToken).ConfigureAwait(false);
            case ".cab" or ".msi" or ".exe":
                if (extractor is null)
                {
                    throw new PolicyTemplatePackageException(PolicyTemplatePackageError.WindowsOnly, "Installers and CAB archives are unpacked on Windows only.");
                }

                return Checked(await extractor.ExtractAsync(file, cancellationToken).ConfigureAwait(false));
            default:
                throw new PolicyTemplatePackageException(PolicyTemplatePackageError.Unsupported, "The file is not a template package.");
        }
    }

    /// <summary>Downloads the current templates of a vendor (HTTPS on the vendor's hosts only).</summary>
    public async Task<PolicyTemplatePackage> DownloadAsync(TemplateVendor vendor, CancellationToken cancellationToken)
    {
        using var client = new HttpClient(_handler(), disposeHandler: true) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ADManager", "1.0"));
        var download = vendor switch
        {
            TemplateVendor.GoogleChrome => VendorTemplateCatalog.Chrome(),
            TemplateVendor.MicrosoftEdge => VendorTemplateCatalog.ParseEdgeProducts(await TextAsync(client, vendor, VendorTemplateCatalog.EdgeProducts, cancellationToken).ConfigureAwait(false)),
            TemplateVendor.MozillaFirefox => VendorTemplateCatalog.ParseFirefoxRelease(await TextAsync(client, vendor, VendorTemplateCatalog.FirefoxLatestRelease, cancellationToken).ConfigureAwait(false)),
            _ => null,
        } ?? throw new PolicyTemplatePackageException(PolicyTemplatePackageError.DownloadFailed, "The vendor publishes no template package.");

        var archive = await BytesAsync(client, vendor, download.Url, cancellationToken).ConfigureAwait(false);
        if ((download.Size is { } size && size != archive.LongLength)
            || (download.Sha256 is { } sha256 && !string.Equals(Convert.ToHexStringLower(SHA256.HashData(archive)), sha256, StringComparison.Ordinal)))
        {
            throw new PolicyTemplatePackageException(PolicyTemplatePackageError.HashMismatch, "The download does not match the vendor's published hash or size.");
        }

        var name = Name(vendor, download);
        PolicyTemplatePackage package;
        if (download.IsCab)
        {
            if (extractor is null)
            {
                throw new PolicyTemplatePackageException(PolicyTemplatePackageError.WindowsOnly, "The vendor offers a CAB archive, unpacked on Windows only.");
            }

            var temp = Path.Combine(Path.GetTempPath(), "admng-" + Guid.NewGuid().ToString("N") + ".cab");
            try
            {
                await File.WriteAllBytesAsync(temp, archive, cancellationToken).ConfigureAwait(false);
                package = await extractor.ExtractAsync(temp, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                File.Delete(temp);
            }
        }
        else
        {
            package = FromZip(name, archive);
        }

        return Checked(package).WithTrust(PackageTrust.PinnedDownload, download.Url.Host);
    }

    private static string Name(TemplateVendor vendor, VendorDownload download)
    {
        var product = vendor switch
        {
            TemplateVendor.MicrosoftEdge => "Microsoft Edge",
            TemplateVendor.GoogleChrome => "Google Chrome",
            _ => "Mozilla Firefox",
        };
        return download.Version.Length > 0 ? product + " " + download.Version : product;
    }

    private static PolicyTemplatePackage FromZip(string name, byte[] archive)
    {
        try
        {
            return PolicyTemplatePackage.FromZip(name, archive);
        }
        catch (InvalidDataException ex)
        {
            throw new PolicyTemplatePackageException(PolicyTemplatePackageError.ExtractionFailed, "The archive cannot be read.", ex);
        }
    }

    private static PolicyTemplatePackage Checked(PolicyTemplatePackage package) => package.AdmxFiles.Count > 0
        ? package
        : throw new PolicyTemplatePackageException(PolicyTemplatePackageError.NoTemplates, "The package holds no ADMX file.");

    private static async Task<string> TextAsync(HttpClient client, TemplateVendor vendor, Uri url, CancellationToken cancellationToken)
    {
        var bytes = await GetAsync(client, vendor, url, VendorTemplateCatalog.MaxDescriptionBytes, cancellationToken).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static Task<byte[]> BytesAsync(HttpClient client, TemplateVendor vendor, Uri url, CancellationToken cancellationToken) =>
        GetAsync(client, vendor, url, VendorTemplateCatalog.MaxDownloadBytes, cancellationToken);

    /// <summary>A GET on a pinned host whose final address (after redirects) is pinned too, within <paramref name="limit"/>.</summary>
    private static async Task<byte[]> GetAsync(HttpClient client, TemplateVendor vendor, Uri url, long limit, CancellationToken cancellationToken)
    {
        if (!VendorTemplateCatalog.IsPinnedHost(vendor, url))
        {
            throw new PolicyTemplatePackageException(PolicyTemplatePackageError.DownloadFailed, "The address is not one of the vendor's.");
        }

        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } final && !VendorTemplateCatalog.IsPinnedHost(vendor, final))
            {
                throw new PolicyTemplatePackageException(PolicyTemplatePackageError.DownloadFailed, "The download was redirected outside the vendor's hosts.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new PolicyTemplatePackageException(PolicyTemplatePackageError.DownloadFailed, $"The vendor answered {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength is { } declared && declared > limit)
            {
                throw new PolicyTemplatePackageException(PolicyTemplatePackageError.TooLarge, "The download is too large.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    throw new PolicyTemplatePackageException(PolicyTemplatePackageError.TooLarge, "The download is too large.");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        catch (HttpRequestException ex)
        {
            throw new PolicyTemplatePackageException(PolicyTemplatePackageError.DownloadFailed, "The vendor could not be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PolicyTemplatePackageException(PolicyTemplatePackageError.DownloadFailed, "The download timed out.", ex);
        }
    }
}
