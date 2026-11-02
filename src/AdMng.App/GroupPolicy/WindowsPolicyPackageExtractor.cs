using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;

namespace AdMng.App.GroupPolicy;

/// <summary>
/// Unpacks the template packages only Windows opens (K3, "Zaktualizuj szablony"): the Windows templates MSI
/// (<c>msiexec /a</c>, an administrative installation into a folder), the Microsoft 365 Apps EXE (<c>/extract: /quiet</c>)
/// and CAB archives (<c>expand.exe</c>; Edge's CAB holds a ZIP).
/// </summary>
/// <remarks>
/// The package (at most <see cref="PolicyTemplatePackage.MaxArchiveBytes"/>) is first copied into a new private folder of a
/// temporary folder of the operator's profile and held open there read-only (nobody can change or replace it) while its
/// Authenticode signature is checked through that handle (<see cref="AuthenticodeVerifier"/>) and while the tool runs on
/// that very copy, with the private folder as its working folder (no DLL planted beside a package in the operator's folder is
/// loaded). An MSI or EXE runs only when Microsoft signed it: a valid signature whose chain ends in a Microsoft root and
/// whose certificate's organization is exactly "Microsoft Corporation". A CAB, which is only unpacked, may be unsigned (the
/// window says so); its header is read first and a CAB that would unpack more than
/// <see cref="PolicyTemplatePackage.MaxArchiveBytes"/>, belongs to a set or names a file outside its folder is refused. What
/// was unpacked is read within the limits of <see cref="PolicyTemplatePackage"/> (only the files the package keeps) and the
/// temporary folder is deleted right after (vendor files, not directory data).
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsPolicyPackageExtractor : IPolicyPackageExtractor
{
    /// <summary>How long an installer may take.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public async Task<PolicyTemplatePackage> ExtractAsync(string file, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        var source = Path.GetFullPath(file);
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not (".msi" or ".exe" or ".cab"))
        {
            throw new PolicyTemplatePackageException(PolicyTemplatePackageError.Unsupported, "The file is not an installer or a CAB archive.");
        }

        if (new FileInfo(source).Length > PolicyTemplatePackage.MaxArchiveBytes)
        {
            throw TooLarge();
        }

        var temp = System.IO.Directory.CreateTempSubdirectory("admng-admx-").FullName;
        try
        {
            // The private copy has a name of its own: nothing of the operator's file name reaches the tools' command lines.
            var folder = System.IO.Directory.CreateDirectory(Path.Combine(temp, "package")).FullName;
            var output = System.IO.Directory.CreateDirectory(Path.Combine(temp, "out")).FullName;
            var copy = Path.Combine(folder, "package" + extension);
            await CopyAsync(source, copy, cancellationToken).ConfigureAwait(false);

            PackageTrust trust;
            string? signer;
            using (var handle = File.OpenHandle(copy, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (extension == ".cab")
                {
                    CheckCabinet(copy);
                }

                var signature = await Task.Run(() => AuthenticodeVerifier.Verify(copy, handle), cancellationToken).ConfigureAwait(false);
                if (extension is ".msi" or ".exe" && !signature.Microsoft)
                {
                    throw new PolicyTemplatePackageException(PolicyTemplatePackageError.Unsigned, "The installer is not signed by Microsoft.");
                }

                // A CAB may be unsigned; one whose certificate claims Microsoft without Microsoft's chain is not taken as signed.
                (trust, signer) = signature.Valid && (signature.Microsoft || !signature.ClaimsMicrosoft)
                    ? (PackageTrust.Signed, signature.Signer)
                    : (PackageTrust.Unsigned, null);

                var (tool, arguments) = extension switch
                {
                    ".msi" => (SystemTool("msiexec.exe"), $"/a {Quote(copy)} /qn TARGETDIR={Quote(output)}"),
                    ".exe" => (copy, $"/extract:{Quote(output)} /quiet"),
                    _ => (SystemTool("expand.exe"), $"{Quote(copy)} -F:* {Quote(output)}"),
                };
                await RunAsync(tool, arguments, folder, cancellationToken).ConfigureAwait(false);
            }

            var name = Path.GetFileName(source);
            var package = await Task.Run(() => Unpacked(name, output), cancellationToken).ConfigureAwait(false);
            return package.WithTrust(trust, signer);
        }
        catch (InvalidDataException ex)
        {
            throw PolicyTemplatePackage.IsTooLarge(ex)
                ? TooLarge(ex)
                : new PolicyTemplatePackageException(PolicyTemplatePackageError.ExtractionFailed, "The unpacked package cannot be read.", ex);
        }
        finally
        {
            try
            {
                System.IO.Directory.Delete(temp, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The temporary folder of the profile is cleaned by Windows; it holds vendor templates only.
            }
        }
    }

    /// <summary>The package of what was unpacked: its templates, or the ZIP inside (Edge's CAB holds MicrosoftEdgePolicyTemplates.zip).</summary>
    private static PolicyTemplatePackage Unpacked(string name, string output)
    {
        var package = PolicyTemplatePackage.FromFolder(name, output, eager: true);
        if (package.AdmxFiles.Count > 0 || System.IO.Directory.EnumerateFiles(output, "*.zip", SearchOption.AllDirectories).FirstOrDefault() is not { } zip)
        {
            return package;
        }

        return new FileInfo(zip).Length > PolicyTemplatePackage.MaxArchiveBytes ? throw TooLarge() : PolicyTemplatePackage.FromZip(name, File.ReadAllBytes(zip));
    }

    /// <summary>Copies the package into the private folder (at most <see cref="PolicyTemplatePackage.MaxArchiveBytes"/>).</summary>
    private static async Task CopyAsync(string source, string target, CancellationToken cancellationToken)
    {
        var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (input.ConfigureAwait(false))
        {
            var copy = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous);
            await using (copy.ConfigureAwait(false))
            {
                var buffer = new byte[81_920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > PolicyTemplatePackage.MaxArchiveBytes)
                    {
                        throw TooLarge();
                    }

                    await copy.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>Reads the cabinet's header: a CAB that would unpack too much, or outside its folder, is not unpacked.</summary>
    private static void CheckCabinet(string cabinet)
    {
        using var stream = new FileStream(cabinet, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (CabinetDirectory.Read(stream).Bytes > PolicyTemplatePackage.MaxArchiveBytes)
        {
            throw TooLarge();
        }
    }

    private static string SystemTool(string name) => Path.Combine(Environment.SystemDirectory, name);

    /// <summary>A path in double quotes, as msiexec (<c>TARGETDIR="…"</c>), expand and the Office extractor expect it.</summary>
    private static string Quote(string path) => path.Contains('"', StringComparison.Ordinal)
        ? throw new ArgumentException("A path cannot contain quotation marks.", nameof(path))
        : "\"" + path + "\"";

    private static PolicyTemplatePackageException TooLarge(Exception? innerException = null) =>
        new(PolicyTemplatePackageError.TooLarge, "The package is too large.", innerException);

    private static async Task RunAsync(string tool, string arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(tool, arguments) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory };
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new PolicyTemplatePackageException(PolicyTemplatePackageError.ExtractionFailed, "The unpacking tool did not start.");
        }
        catch (Win32Exception ex)
        {
            throw new PolicyTemplatePackageException(PolicyTemplatePackageError.ExtractionFailed, "The unpacking tool did not start.", ex);
        }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw new PolicyTemplatePackageException(PolicyTemplatePackageError.ExtractionFailed, "Unpacking took too long.");
            }

            if (process.ExitCode != 0)
            {
                throw new PolicyTemplatePackageException(PolicyTemplatePackageError.ExtractionFailed, $"The unpacking tool ended with {process.ExitCode}.");
            }
        }
    }
}
