using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.Tests.Lists;
using AdMng.Presentation.Tests.Watchdog;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.GroupPolicy;

public class CentralStoreUpdateTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Writes files ('/'-separated paths) below <paramref name="folder"/>.</summary>
    private static async Task WriteAsync(string folder, IReadOnlyDictionary<string, byte[]> files)
    {
        foreach (var (path, content) in files)
        {
            var file = Path.Combine([folder, .. path.Split('/')]);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllBytesAsync(file, content, Ct);
        }
    }

    /// <summary>A harness whose central store has the sample templates without the Polish files.</summary>
    private static async Task<GroupPolicyHarness> StoreAsync()
    {
        var harness = await GroupPolicyHarness.CreateAsync(Polish);
        foreach (var (path, content) in SampleAdministrativeTemplates.Files(polish: false))
        {
            harness.Sysvol.PutPolicyDefinition(path, content);
        }

        return harness;
    }

    /// <summary>
    /// The archive with the compression method of <paramref name="entry"/> set to 14 (LZMA, as 7-Zip may write): the
    /// archive lists the entry, but it cannot be decompressed.
    /// </summary>
    private static byte[] WithUnsupportedMethod(byte[] zip, string entry)
    {
        var bytes = (byte[])zip.Clone();
        var name = Encoding.UTF8.GetBytes(entry);
        bool NameAt(int offset, int length) => length == name.Length && offset + length <= bytes.Length && bytes.AsSpan(offset, length).SequenceEqual(name);
        for (var i = 0; i + 46 < bytes.Length; i++)
        {
            var signature = BitConverter.ToUInt32(bytes, i);
            if (signature == 0x04034b50 && NameAt(i + 30, BitConverter.ToUInt16(bytes, i + 26)))
            {
                bytes[i + 8] = 14; // local file header: compression method
                bytes[i + 9] = 0;
            }
            else if (signature == 0x02014b50 && NameAt(i + 46, BitConverter.ToUInt16(bytes, i + 28)))
            {
                bytes[i + 10] = 14; // central directory header: compression method
                bytes[i + 11] = 0;
            }
        }

        return bytes;
    }

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
            Assert.NotEmpty(seen!.Rows);
            Assert.All(seen.Rows, r => Assert.Equal(CentralStoreFileStatus.Unchanged, r.Status)); // compared again after the update
            Assert.Null(seen.Error);
            Assert.False(seen.IsWorking);
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

    [Fact]
    public async Task OneAdmxFile_IsThatTemplate_WithTheLanguageFilesOfTheCultureFoldersBesideIt()
    {
        var folder = System.IO.Directory.CreateTempSubdirectory("admng-k3-admx-");
        try
        {
            var newer = SampleAdministrativeTemplates.ContosoAdmx.Replace("revision=\"1.2\"", "revision=\"1.3\"", StringComparison.Ordinal);
            await WriteAsync(folder.FullName, new Dictionary<string, byte[]>
            {
                ["contoso.admx"] = SampleAdministrativeTemplates.Utf8(newer),
                ["windows.admx"] = SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.BaseAdmx),
                ["en-US/contoso.adml"] = SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmlEn),
                ["en-US/windows.adml"] = SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.BaseAdmlEn),
                ["pl-PL/Contoso.adml"] = SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmlPl), // another case of the name
                ["Archiwum/contoso.admx"] = SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmx), // an older copy below
                ["Archiwum/en-US/contoso.adml"] = SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmlEn),
                ["Szablony/pl-PL/contoso.adml"] = SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmlPl), // not beside it
            });

            var package = await new PolicyTemplatePackageLoader().LoadFileAsync(Path.Combine(folder.FullName, "contoso.admx"), Ct);

            Assert.Equal("contoso.admx", package.Description);
            Assert.Equal(["contoso.admx"], package.AdmxFiles);
            Assert.Equal(["en-US", "pl-PL"], package.Cultures);
            Assert.Empty(package.Issues);
            Assert.Equal(["contoso.admx", "en-US/contoso.adml", "pl-PL/contoso.adml"],
                (await package.ListAsync(Ct))!.Select(f => f.RelativePath).Order(StringComparer.Ordinal));
            Assert.Equal(SampleAdministrativeTemplates.Utf8(newer), await package.ReadAsync("contoso.admx", PolicyTemplatePackage.MaxEntryBytes, Ct));
            Assert.Equal(SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmlPl),
                await package.ReadAsync("pl-PL/contoso.adml", PolicyTemplatePackage.MaxEntryBytes, Ct));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AFolderWithTwoTemplatesOfOneName_ShowsTheFileThatIsNotUsed()
    {
        var harness = await GroupPolicyHarness.CreateAsync();
        var folder = System.IO.Directory.CreateTempSubdirectory("admng-k3-twice-");
        try
        {
            await WriteAsync(folder.FullName, new Dictionary<string, byte[]>
            {
                ["contoso.admx"] = SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmx),
                ["en-US/contoso.adml"] = SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmlEn),
                ["Archiwum/contoso.admx"] = SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmx.Replace("revision=\"1.2\"", "revision=\"1.0\"", StringComparison.Ordinal)),
                ["Archiwum/en-US/contoso.adml"] = SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmlEn),
            });
            harness.Windows.Folder = folder.FullName;
            using var update = new CentralStoreUpdateViewModel(harness.Services, harness.Definitions, new PolicyTemplatePackageLoader());

            await update.OpenFolderCommand.ExecuteAsync(null);

            Assert.Null(update.Error);
            Assert.True(update.HasPackageIssues);
            Assert.Equal("Files of the package that are not used (1)", update.PackageIssuesTitle);
            Assert.Equal(["contoso.admx – a template of this name was found earlier in the package – that file is used"], update.PackageIssues);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Comparisons_AreSerialized_OnlyTheLatestIsShown_AndUpdateWaitsForIt()
    {
        var harness = await StoreAsync();
        var folder = System.IO.Directory.CreateTempSubdirectory("admng-k3-serial-");
        try
        {
            var archive = Path.Combine(folder.FullName, "policy_templates.zip");
            await File.WriteAllBytesAsync(archive, Zip(), Ct);
            harness.B2.Files.NextOpenPath = archive;
            using var update = new CentralStoreUpdateViewModel(harness.Services, harness.Definitions, new PolicyTemplatePackageLoader())
            {
                BackupFolder = folder.FullName,
            };
            await update.OpenFileCommand.ExecuteAsync(null);
            Assert.Contains(update.Rows, r => r.Path == "pl-PL/contoso.adml");

            // The next comparison is held while it reads the store (a slow SMB read).
            using var entered = new SemaphoreSlim(0);
            using var release = new ManualResetEventSlim();
            var lists = 0;
            harness.Sysvol.BeforeCall = (operation, _, _) =>
            {
                if (operation == "ListDefinitions" && Interlocked.Increment(ref lists) == 1)
                {
                    entered.Release();
                    release.Wait(TimeSpan.FromSeconds(30));
                }
            };

            var polish = update.Cultures.Single(c => c.Culture == "pl");
            polish.IsSelected = false; // comparison A, held
            Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(30), Ct));
            Assert.True(update.IsComparing);
            Assert.True(update.IsWorking);
            Assert.Empty(update.Rows); // what the previous options showed is gone at once
            Assert.False(update.UpdateCommand.CanExecute(null)); // "Zaktualizuj…" waits for the comparison
            Assert.Equal("Porównywanie z magazynem centralnym…", update.ProgressText);

            polish.IsSelected = true; // comparison B supersedes A
            await ObjectListViewModelTests.WaitUntilAsync(() => !update.IsComparing && update.Rows.Count > 0);
            release.Set(); // A ends after B: never shown
            await ObjectListViewModelTests.WaitUntilAsync(() => harness.Sysvol.OpenConnections == 0);
            harness.Sysvol.BeforeCall = null;

            Assert.False(update.IsComparing);
            Assert.Equal(["contoso.admx", "en-US/contoso.adml", "pl-PL/contoso.adml"], update.Rows.Select(r => r.Path));
            Assert.True(update.UpdateCommand.CanExecute(null));
            await update.UpdateCommand.ExecuteAsync(null);
            Assert.Equal(SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmlPl), harness.Sysvol.GetPolicyDefinition("pl-PL/contoso.adml"));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AFailedComparison_LeavesNothingToUpdate_AndTheNextSuccessfulOneClearsTheError()
    {
        var harness = await StoreAsync();
        var folder = System.IO.Directory.CreateTempSubdirectory("admng-k3-failed-");
        try
        {
            var archive = Path.Combine(folder.FullName, "policy_templates.zip");
            await File.WriteAllBytesAsync(archive, Zip(), Ct);
            harness.B2.Files.NextOpenPath = archive;
            using var update = new CentralStoreUpdateViewModel(harness.Services, harness.Definitions, new PolicyTemplatePackageLoader())
            {
                BackupFolder = folder.FullName,
            };
            await update.OpenFileCommand.ExecuteAsync(null);
            Assert.True(update.UpdateCommand.CanExecute(null));

            harness.Sysvol.Unavailable = SysvolUnavailableReason.Unreachable; // the next package cannot be compared
            await update.OpenFileCommand.ExecuteAsync(null);

            Assert.Equal(harness.Services.SysvolText(SysvolUnavailableReason.Unreachable, harness.Session.Info.Server), update.Error);
            Assert.Empty(update.Rows);
            Assert.Equal(string.Empty, update.SummaryText);
            Assert.False(update.UpdateCommand.CanExecute(null)); // nothing of the previous package can be written

            harness.Sysvol.Unavailable = null;
            update.ReplaceCollidingFiles = true; // any option compares again
            await ObjectListViewModelTests.WaitUntilAsync(() => !update.IsComparing && update.Rows.Count > 0);

            Assert.Null(update.Error);
            Assert.Equal("Do zapisania: 1, bez zmian: 2, pominięte: 0", update.SummaryText);
            Assert.True(update.UpdateCommand.CanExecute(null));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TheBackup_RunsOffTheUiThread_WhileTheWindowIsBusy_AndTheWindowCannotBeClosed()
    {
        var harness = await StoreAsync();
        var folder = System.IO.Directory.CreateTempSubdirectory("admng-k3-busy-");
        using var ui = new TestUiThread();
        try
        {
            var archive = Path.Combine(folder.FullName, "policy_templates.zip");
            await File.WriteAllBytesAsync(archive, Zip(), Ct);
            harness.B2.Files.NextOpenPath = archive;
            var onUiThread = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var update = new CentralStoreUpdateViewModel(harness.Services, harness.Definitions, new PolicyTemplatePackageLoader())
            {
                BackupFolder = folder.FullName,
                WriteBackup = async (target, server, files, time, token) =>
                {
                    onUiThread.TrySetResult(ui.CheckAccess());
                    await release.Task.WaitAsync(token);
                    return await CentralStoreBackup.WriteAsync(target, server, files, time, token);
                },
            };
            await ui.RunAsync(() => update.OpenFileCommand.ExecuteAsync(null));

            var running = ui.RunAsync(() => update.UpdateCommand.ExecuteAsync(null));
            Assert.False(await onUiThread.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct)); // never on the UI thread (rule 6)
            var state = await ui.RunAsync(async () => (update.IsBusy, update.ProgressText, await update.CanCloseAsync(), update.CloseCommand.CanExecute(null),
                update.UpdateCommand.CanExecute(null), update.OpenFileCommand.CanExecute(null)));
            Assert.Equal((true, "Tworzenie kopii zapasowej magazynu centralnego…", false, false, false, false), state);

            release.SetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(30), Ct);

            Assert.Single(harness.Windows.Previews);
            Assert.True(update.ChangedStore);
            Assert.False(update.IsBusy);
            Assert.True(await update.CanCloseAsync());
            Assert.Single(System.IO.Directory.GetDirectories(folder.FullName, "PolicyDefinitions-*"));
            Assert.Empty(ui.Errors);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ClosingTheWindowDuringTheBackup_PlansNothing()
    {
        var harness = await StoreAsync();
        var folder = System.IO.Directory.CreateTempSubdirectory("admng-k3-closed-");
        try
        {
            var archive = Path.Combine(folder.FullName, "policy_templates.zip");
            await File.WriteAllBytesAsync(archive, Zip(), Ct);
            harness.B2.Files.NextOpenPath = archive;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var update = new CentralStoreUpdateViewModel(harness.Services, harness.Definitions, new PolicyTemplatePackageLoader())
            {
                BackupFolder = folder.FullName,
                WriteBackup = async (target, _, _, _, _) =>
                {
                    entered.TrySetResult();
                    await release.Task; // a share that cannot be interrupted
                    return target;
                },
            };
            await update.OpenFileCommand.ExecuteAsync(null);

            var running = update.UpdateCommand.ExecuteAsync(null);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            update.Dispose(); // the window closed (its host disposes the view model)
            release.SetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(30), Ct);

            Assert.Empty(harness.Windows.Previews);
            Assert.False(update.ChangedStore);
            Assert.Null(harness.Sysvol.GetPolicyDefinition("pl-PL/contoso.adml"));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AnArchiveEntryThatCannotBeDecompressed_IsReported_NeverThrown()
    {
        var harness = await StoreAsync();
        var folder = System.IO.Directory.CreateTempSubdirectory("admng-k3-lzma-");
        try
        {
            var archive = Path.Combine(folder.FullName, "policy_templates.zip");
            await File.WriteAllBytesAsync(archive, WithUnsupportedMethod(Zip(), "windows/admx/contoso.admx"), Ct);
            harness.B2.Files.NextOpenPath = archive;
            using var update = new CentralStoreUpdateViewModel(harness.Services, harness.Definitions, new PolicyTemplatePackageLoader());

            await update.OpenFileCommand.ExecuteAsync(null); // the entries are read only for the comparison

            bool Reported() => update.Error == "Nie udało się rozpakować ani odczytać paczki."
                || update.Rows.Any(r => r is { Path: "contoso.admx", Status: CentralStoreFileStatus.Unreadable });
            Assert.NotNull(update.Package);
            Assert.True(Reported());
            Assert.False(update.IsBusy);

            update.ReplaceCollidingFiles = true; // a comparison started by an option reports it the same way
            await ObjectListViewModelTests.WaitUntilAsync(() => !update.IsComparing);
            Assert.True(Reported());
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Download_WhoseReleaseDescriptionIsNotJson_IsAFailedDownload_InTheWindowToo()
    {
        static HttpResponseMessage BlockPage() => new(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>Strona zablokowana przez serwer pośredniczący</body></html>", Encoding.UTF8, "text/html"),
        };

        var loader = new PolicyTemplatePackageLoader(handler: () => new FakeHandler(_ => BlockPage()));

        Assert.Equal(PolicyTemplatePackageError.DownloadFailed,
            (await Assert.ThrowsAsync<PolicyTemplatePackageException>(() => loader.DownloadAsync(TemplateVendor.MozillaFirefox, Ct))).Error);
        Assert.Equal(PolicyTemplatePackageError.DownloadFailed,
            (await Assert.ThrowsAsync<PolicyTemplatePackageException>(() => loader.DownloadAsync(TemplateVendor.MicrosoftEdge, Ct))).Error);

        var harness = await GroupPolicyHarness.CreateAsync();
        using var update = new CentralStoreUpdateViewModel(harness.Services, harness.Definitions, loader);
        await update.DownloadFirefoxCommand.ExecuteAsync(null);

        Assert.StartsWith("The download failed", update.Error, StringComparison.Ordinal);
        Assert.False(update.IsBusy);
        Assert.Null(update.Package);
    }

    [Fact]
    public async Task Download_InterruptedOrStalled_IsAFailedDownload()
    {
        static Func<HttpMessageHandler> Body(bool fail) =>
            () => new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream(fail)) });

        var interrupted = new PolicyTemplatePackageLoader(handler: Body(fail: true));
        Assert.Equal(PolicyTemplatePackageError.DownloadFailed,
            (await Assert.ThrowsAsync<PolicyTemplatePackageException>(() => interrupted.DownloadAsync(TemplateVendor.GoogleChrome, Ct))).Error);

        // A connection that stalls after the headers ends within the timeout, which covers the body too.
        var stalled = new PolicyTemplatePackageLoader(handler: Body(fail: false)) { DownloadTimeout = TimeSpan.FromMilliseconds(200) };
        var error = await Assert.ThrowsAsync<PolicyTemplatePackageException>(
            () => stalled.DownloadAsync(TemplateVendor.GoogleChrome, Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct));
        Assert.Equal(PolicyTemplatePackageError.DownloadFailed, error.Error);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
    }

    [Fact]
    public void Downloads_GoThroughTheSystemProxy_WithTheOperatorsCredentials()
    {
        using var handler = PolicyTemplatePackageLoader.DefaultHandler();

        Assert.True(handler.UseProxy);
        Assert.Same(CredentialCache.DefaultCredentials, handler.DefaultProxyCredentials);
        Assert.Null(handler.Credentials); // never sent to the vendors' hosts
        Assert.True(handler.AllowAutoRedirect);
        Assert.Equal(5, handler.MaxAutomaticRedirections);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] content) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };

    /// <summary>A response body that delivers a few bytes, then breaks (an IOException) or stalls until it is cancelled.</summary>
    private sealed class BrokenStream(bool fail) : Stream
    {
        private int _reads;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_reads++ == 0)
            {
                buffer.Span[..16].Fill((byte)'P');
                return 16;
            }

            if (fail)
            {
                throw new IOException("The connection was reset (test).");
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

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
