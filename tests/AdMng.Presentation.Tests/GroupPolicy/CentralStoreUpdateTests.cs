using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.GroupPolicy;

public class CentralStoreUpdateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A Chrome-like archive of the sample templates (windows/admx/…, culture folders, other files).</summary>
    private static byte[] Zip(string contosoAdmx = SampleAdministrativeTemplates.ContosoAdmx)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string text)
            {
                using var entry = zip.CreateEntry(path).Open();
                entry.Write(SampleAdministrativeTemplates.Utf8(text));
            }

            Add("windows/admx/contoso.admx", contosoAdmx);
            Add("windows/admx/en-US/contoso.adml", SampleAdministrativeTemplates.ContosoAdmlEn);
            Add("windows/admx/pl/contoso.adml", SampleAdministrativeTemplates.ContosoAdmlPl);
            Add("windows/examples/readme.txt", "ignored");
        }

        return stream.ToArray();
    }

    [Fact]
    public async Task Window_LoadsAFile_ComparesWithTheStore_BacksItUp_AndUpdatesThroughThePreview()
    {
        var harness = await GroupPolicyHarness.CreateAsync(System.Globalization.CultureInfo.GetCultureInfo("pl-PL"));
        foreach (var (path, content) in SampleAdministrativeTemplates.Files(polish: false))
        {
            harness.Sysvol.PutPolicyDefinition(path, content);
        }

        var folder = System.IO.Directory.CreateTempSubdirectory("admng-k3-ui-");
        try
        {
            var newer = SampleAdministrativeTemplates.ContosoAdmx.Replace("revision=\"1.2\"", "revision=\"1.3\"", StringComparison.Ordinal);
            var archive = Path.Combine(folder.FullName, "policy_templates.zip");
            await File.WriteAllBytesAsync(archive, Zip(newer), Ct);
            harness.B2.Files.NextOpenPath = archive;
            CentralStoreUpdateViewModel? seen = null;
            harness.Windows.CentralStoreHandler = async update =>
            {
                seen = update;
                await update.OpenFileCommand.ExecuteAsync(null);
                Assert.Null(update.Error);
                Assert.Equal(PackageTrust.NotChecked, update.Package!.Trust);
                Assert.True(update.IsUntrusted);
                Assert.Equal(["en-US", "pl"], update.Cultures.Select(c => c.Culture));
                Assert.All(update.Cultures, c => Assert.True(c.IsSelected)); // en-US always, pl for a Polish UI
                Assert.Equal([("contoso.admx", CentralStoreFileStatus.Changed), ("en-US/contoso.adml", CentralStoreFileStatus.Unchanged),
                    ("pl-PL/contoso.adml", CentralStoreFileStatus.New)], update.Rows.Select(r => (r.Path, r.Status)));
                Assert.Equal("Do zapisania: 2, bez zmian: 1, pominięte: 0", update.SummaryText);

                update.BackupFolder = folder.FullName;
                Assert.True(update.UpdateCommand.CanExecute(null));
                await update.UpdateCommand.ExecuteAsync(null);
                return update.ChangedStore;
            };

            Assert.True(await harness.Factory.UpdateTemplatesAsync());

            Assert.Equal(SampleAdministrativeTemplates.Utf8(newer), harness.Sysvol.GetPolicyDefinition("contoso.admx"));
            Assert.NotNull(harness.Sysvol.GetPolicyDefinition("pl-PL/contoso.adml"));
            var backup = Assert.Single(System.IO.Directory.GetDirectories(folder.FullName));
            Assert.StartsWith("PolicyDefinitions-", Path.GetFileName(backup), StringComparison.Ordinal);
            Assert.Equal(SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmx), await File.ReadAllBytesAsync(Path.Combine(backup, "contoso.admx"), Ct));
            Assert.Contains(Assert.Single(harness.Windows.Previews).Rows, r => r.Contains("PolicyDefinitions/contoso.admx", StringComparison.Ordinal));
            Assert.All(seen!.Rows, r => Assert.Equal(CentralStoreFileStatus.Unchanged, r.Status)); // compared again after the update
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Window_WithoutAStore_OffersTheTemplatesOfThisComputer_AndCreatesTheStore()
    {
        var harness = await GroupPolicyHarness.CreateAsync();
        var folder = System.IO.Directory.CreateTempSubdirectory("admng-k3-local-");
        try
        {
            foreach (var (path, content) in SampleAdministrativeTemplates.Files())
            {
                var file = Path.Combine([folder.FullName, .. path.Split('/')]);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllBytesAsync(file, content, Ct);
            }

            harness.Local.Folder = folder.FullName;
            var update = new CentralStoreUpdateViewModel(harness.Services, harness.Definitions, new PolicyTemplatePackageLoader(local: harness.Local));
            await update.UseLocalStoreCommand.ExecuteAsync(null);

            Assert.Equal("The domain has no central store yet: the update creates it.", update.StoreNote);
            Assert.All(update.Rows, r => Assert.Equal(CentralStoreFileStatus.New, r.Status));
            Assert.True(update.UpdateCommand.CanExecute(null)); // no backup of a store that does not exist
            await update.UpdateCommand.ExecuteAsync(null);

            Assert.Equal(["contoso.admx", "en-US/contoso.adml", "en-US/windows.adml", "windows.admx"], harness.Sysvol.PolicyDefinitionFiles());
            Assert.Contains(Assert.Single(harness.Windows.Previews).Plan.Objects.Single().Issues, i => i.Code == Core.Batch.IssueCodes.CentralStoreCreated);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Window_ReportsAnUnusablePackage()
    {
        var harness = await GroupPolicyHarness.CreateAsync();
        var folder = System.IO.Directory.CreateTempSubdirectory("admng-k3-bad-");
        try
        {
            var text = Path.Combine(folder.FullName, "notatka.txt");
            await File.WriteAllTextAsync(text, "nie szablon", Ct);
            var msi = Path.Combine(folder.FullName, "Windows.msi");
            await File.WriteAllBytesAsync(msi, [1, 2, 3], Ct);
            var update = new CentralStoreUpdateViewModel(harness.Services, harness.Definitions, new PolicyTemplatePackageLoader());

            harness.B2.Files.NextOpenPath = text;
            await update.OpenFileCommand.ExecuteAsync(null);
            Assert.Equal("This file is not a package of templates (ZIP, CAB, MSI, EXE or ADMX).", update.Error);

            harness.B2.Files.NextOpenPath = msi;
            await update.OpenFileCommand.ExecuteAsync(null);
            Assert.Equal("MSI, CAB and EXE packages are unpacked on Windows only.", update.Error);
            Assert.False(update.CanExtractInstallers);
            Assert.Null(update.Package);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Download_OfEdge_ReadsTheReleaseList_ChecksTheHash_AndMarksThePinnedAddress()
    {
        var zip = Zip();
        var hash = Convert.ToHexString(SHA256.HashData(zip));
        var handler = new FakeHandler(request => request.RequestUri!.Host switch
        {
            "edgeupdates.microsoft.com" => Json($$"""
                [ { "Product": "Policy", "Releases": [ { "ProductVersion": "131.0.2903.48", "Artifacts": [
                  { "ArtifactName": "zip", "Location": "https://msedge.sf.dl.delivery.mp.microsoft.com/files/MicrosoftEdgePolicyTemplates.zip",
                    "Hash": "{{hash}}", "HashAlgorithm": "SHA256", "SizeInBytes": {{zip.Length}} } ] } ] } ]
                """),
            "msedge.sf.dl.delivery.mp.microsoft.com" => Bytes(zip),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var loader = new PolicyTemplatePackageLoader(handler: () => handler);

        var package = await loader.DownloadAsync(TemplateVendor.MicrosoftEdge, Ct);

        Assert.Equal("Microsoft Edge 131.0.2903.48", package.Description);
        Assert.Equal(PackageTrust.PinnedDownload, package.Trust);
        Assert.Equal("msedge.sf.dl.delivery.mp.microsoft.com", package.Signer);
        Assert.Equal(["contoso.admx"], package.AdmxFiles);
        Assert.All(handler.Requests, r => Assert.Contains("ADManager", r.Headers.UserAgent.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Download_WithAWrongHash_IsDiscarded()
    {
        var zip = Zip();
        var handler = new FakeHandler(request => request.RequestUri!.Host == "api.github.com"
            ? Json("""
                { "tag_name": "v6.5", "assets": [ { "name": "policy_templates_v6.5.zip", "browser_download_url": "https://github.com/mozilla/policy-templates/releases/download/v6.5/policy_templates_v6.5.zip",
                  "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000" } ] }
                """)
            : Bytes(zip));

        var error = await Assert.ThrowsAsync<PolicyTemplatePackageException>(() => new PolicyTemplatePackageLoader(handler: () => handler).DownloadAsync(TemplateVendor.MozillaFirefox, Ct));

        Assert.Equal(PolicyTemplatePackageError.HashMismatch, error.Error);
    }

    [Fact]
    public async Task Download_RedirectedOutsideTheVendor_OrFailing_IsRefused()
    {
        var redirected = new FakeHandler(request =>
        {
            var response = Bytes(Zip());
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example.com/policy_templates.zip");
            return response;
        });
        var failing = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        Assert.Equal(PolicyTemplatePackageError.DownloadFailed,
            (await Assert.ThrowsAsync<PolicyTemplatePackageException>(() => new PolicyTemplatePackageLoader(handler: () => redirected).DownloadAsync(TemplateVendor.GoogleChrome, Ct))).Error);
        Assert.Equal(PolicyTemplatePackageError.DownloadFailed,
            (await Assert.ThrowsAsync<PolicyTemplatePackageException>(() => new PolicyTemplatePackageLoader(handler: () => failing).DownloadAsync(TemplateVendor.GoogleChrome, Ct))).Error);
    }

    [Fact]
    public async Task Download_OfChrome_TakesTheFixedAddress()
    {
        var handler = new FakeHandler(_ => Bytes(Zip()));

        var package = await new PolicyTemplatePackageLoader(handler: () => handler).DownloadAsync(TemplateVendor.GoogleChrome, Ct);

        Assert.Equal(VendorTemplateCatalog.ChromeTemplates, Assert.Single(handler.Requests).RequestUri);
        Assert.Equal("Google Chrome", package.Description);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] content) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = answer(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
