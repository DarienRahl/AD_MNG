using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using AdMng.App.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;
using AdMng.Testing;

namespace AdMng.App.UiTests.GroupPolicy;

/// <summary>
/// The packages only Windows unpacks (K3 review): the Authenticode check through WinVerifyTrust, the signer's chain and the
/// Microsoft root policy; installers Microsoft did not sign never run; a CAB is unpacked in memory through Windows' cabinet
/// library from a private copy held open, within the limits, its header checked first. Windows only (no window needed);
/// the cabinets are made by <c>makecab.exe</c>.
/// </summary>
public sealed class WindowsPolicyPackageExtractorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Verify_TheRuntimesOwnLibrary_IsSignedByMicrosoft()
    {
        SkipUnlessWindows();
        var path = typeof(object).Assembly.Location; // System.Private.CoreLib.dll: Authenticode-signed by Microsoft Corporation
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

        var verdict = AuthenticodeVerifier.Verify(path, handle);

        Assert.True(verdict.Valid, "WinVerifyTrust does not accept the signature of " + path);
        Assert.True(verdict.ClaimsMicrosoft);
        Assert.Equal(PackageSigner.MicrosoftOrganization, verdict.Signer);
        Assert.True(verdict.Microsoft, "The signer's chain does not pass CERT_CHAIN_POLICY_MICROSOFT_ROOT: " + path);
    }

    [Fact]
    public void Verify_AFileWithoutASignature_IsNotValid()
    {
        SkipUnlessWindows();
        var folder = System.IO.Directory.CreateTempSubdirectory("admng-k3-sig-");
        try
        {
            var path = Path.Combine(folder.FullName, "niepodpisany.exe");
            File.WriteAllBytes(path, [0x4D, 0x5A, 0x90, 0x00, 1, 2, 3]);
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            var verdict = AuthenticodeVerifier.Verify(path, handle);

            Assert.Equal(default, verdict);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("Szablony administracyjne Windows 11.msi")]
    [InlineData("admintemplates_x64.exe")]
    public async Task Extract_RefusesAnInstallerMicrosoftDidNotSign_WithoutRunningIt(string name)
    {
        SkipUnlessWindows();
        var folder = System.IO.Directory.CreateTempSubdirectory("admng-k3-msi-");
        try
        {
            var path = Path.Combine(folder.FullName, name);
            await File.WriteAllBytesAsync(path, [0x4D, 0x5A, 0x90, 0x00, 1, 2, 3], Ct);

            // Run, the junk would end in ExtractionFailed (the tool fails); refused, it never starts.
            var error = await Assert.ThrowsAsync<PolicyTemplatePackageException>(() => new WindowsPolicyPackageExtractor().ExtractAsync(path, Ct));

            Assert.Equal(PolicyTemplatePackageError.Unsigned, error.Error);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Extract_UnpacksAnEdgeLikeCab_AsUnsigned()
    {
        SkipUnlessWindows();
        var folder = System.IO.Directory.CreateTempSubdirectory("admng-k3-cab-");
        try
        {
            var zip = Path.Combine(folder.FullName, "MicrosoftEdgePolicyTemplates.zip");
            await File.WriteAllBytesAsync(zip, Zip(), Ct);
            var cab = Path.Combine(folder.FullName, "MicrosoftEdgePolicyTemplates.cab");
            Run(Path.Combine(Environment.SystemDirectory, "makecab.exe"), $"\"{zip}\" \"{cab}\"", folder.FullName);

            var package = await new WindowsPolicyPackageExtractor().ExtractAsync(cab, Ct);

            Assert.Equal(PackageTrust.Unsigned, package.Trust);
            Assert.Null(package.Signer);
            Assert.Equal("MicrosoftEdgePolicyTemplates.cab", package.Description);
            Assert.Equal(["contoso.admx"], package.AdmxFiles);
            Assert.Equal(SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmlPl),
                await package.ReadAsync("pl/contoso.adml", PolicyTemplatePackage.MaxEntryBytes, Ct));
            Assert.True(File.Exists(cab)); // the operator's file is only read
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Extract_UnpacksACabOfTemplates_AndSkipsOtherFiles()
    {
        SkipUnlessWindows();
        var folder = System.IO.Directory.CreateTempSubdirectory("admng-k3-cab-");
        try
        {
            var admx = Path.Combine(folder.FullName, "contoso.admx");
            await File.WriteAllTextAsync(admx, SampleAdministrativeTemplates.ContosoAdmx, Ct);
            var templates = Path.Combine(folder.FullName, "szablony.cab");
            Run(Path.Combine(Environment.SystemDirectory, "makecab.exe"), $"\"{admx}\" \"{templates}\"", folder.FullName);
            var notes = Path.Combine(folder.FullName, "informacje.txt");
            await File.WriteAllTextAsync(notes, "Szablony zasad – pliki producenta.", Ct);
            var other = Path.Combine(folder.FullName, "informacje.cab");
            Run(Path.Combine(Environment.SystemDirectory, "makecab.exe"), $"\"{notes}\" \"{other}\"", folder.FullName);

            var package = await new WindowsPolicyPackageExtractor().ExtractAsync(templates, Ct);
            var empty = await new WindowsPolicyPackageExtractor().ExtractAsync(other, Ct);

            Assert.Equal(["contoso.admx"], package.AdmxFiles);
            Assert.Equal(await File.ReadAllBytesAsync(admx, Ct), await package.ReadAsync("contoso.admx", PolicyTemplatePackage.MaxEntryBytes, Ct));
            Assert.Empty(empty.AdmxFiles); // a cabinet without templates is a package without templates, not an error
            Assert.Equal(PackageTrust.Unsigned, empty.Trust);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(@"..\..\zasady.txt", 10u, PolicyTemplatePackageError.ExtractionFailed)]
    [InlineData(@"C:\Windows\zasady.txt", 10u, PolicyTemplatePackageError.ExtractionFailed)]
    [InlineData("MicrosoftEdgePolicyTemplates.zip", uint.MaxValue, PolicyTemplatePackageError.TooLarge)]
    public async Task Extract_RefusesACab_ThatWouldUnpackOutsideItsFolder_OrTooMuch(string name, uint size, PolicyTemplatePackageError expected)
    {
        SkipUnlessWindows();
        var folder = System.IO.Directory.CreateTempSubdirectory("admng-k3-badcab-");
        try
        {
            var cab = Path.Combine(folder.FullName, "zly.cab");
            await File.WriteAllBytesAsync(cab, CabinetHeader(name, size), Ct);

            var error = await Assert.ThrowsAsync<PolicyTemplatePackageException>(() => new WindowsPolicyPackageExtractor().ExtractAsync(cab, Ct));

            Assert.Equal(expected, error.Error);
            Assert.False(File.Exists(Path.Combine(folder.FullName, "zasady.txt")));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Extract_RefusesAPackageLargerThanAnArchive_BeforeCopyingIt()
    {
        SkipUnlessWindows();
        var folder = System.IO.Directory.CreateTempSubdirectory("admng-k3-big-");
        try
        {
            var cab = Path.Combine(folder.FullName, "duzy.cab");
            using (var stream = new FileStream(cab, FileMode.Create, FileAccess.Write))
            {
                stream.SetLength(PolicyTemplatePackage.MaxArchiveBytes + 1);
            }

            var error = await Assert.ThrowsAsync<PolicyTemplatePackageException>(() => new WindowsPolicyPackageExtractor().ExtractAsync(cab, Ct));

            Assert.Equal(PolicyTemplatePackageError.TooLarge, error.Error);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    /// <summary>A Chrome-like archive of the sample templates (windows/admx/…, culture folders).</summary>
    private static byte[] Zip()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in new[]
            {
                ("windows/admx/contoso.admx", SampleAdministrativeTemplates.ContosoAdmx),
                ("windows/admx/en-US/contoso.adml", SampleAdministrativeTemplates.ContosoAdmlEn),
                ("windows/admx/pl/contoso.adml", SampleAdministrativeTemplates.ContosoAdmlPl),
            })
            {
                using var entry = zip.CreateEntry(path).Open();
                entry.Write(SampleAdministrativeTemplates.Utf8(text));
            }
        }

        return stream.ToArray();
    }

    /// <summary>The header of a cabinet holding one file (CFHEADER, one CFFOLDER, one CFFILE; no data).</summary>
    private static byte[] CabinetHeader(string name, uint size)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x4643534Du); // "MSCF"
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(36u + 8u); // the file entries follow the header and the folder
        writer.Write(0u);
        writer.Write((byte)3);
        writer.Write((byte)1);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((ushort)0);
        writer.Write((ushort)0x2026);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write(size);
        writer.Write(0u);
        writer.Write((ushort)0);
        writer.Write((ushort)0x5A21);
        writer.Write((ushort)0x6000);
        writer.Write((ushort)0x80);
        writer.Write(Encoding.UTF8.GetBytes(name));
        writer.Write((byte)0);
        writer.Flush();
        return stream.ToArray();
    }

    private static void Run(string tool, string arguments, string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo(tool, arguments) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory })!;
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(1)), tool + " did not finish.");
        Assert.Equal(0, process.ExitCode);
    }

    private static void SkipUnlessWindows() => Assert.SkipUnless(OperatingSystem.IsWindows(), "WinVerifyTrust, makecab and expand exist on Windows only.");
}
