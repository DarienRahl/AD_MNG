using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;

namespace AdMng.App.GroupPolicy;

/// <summary>
/// Unpacks the template packages only Windows opens (K3, "Zaktualizuj szablony"): the Windows templates MSI
/// (<c>msiexec /a</c>, an administrative installation into a folder), the Microsoft 365 Apps EXE (<c>/extract: /quiet</c>)
/// and CAB archives (<c>expand.exe</c>; Edge's CAB holds a ZIP). The Authenticode signature is checked first
/// (<c>WinVerifyTrust</c>): an MSI or EXE is run only when Microsoft signed it; a CAB, which is only unpacked, may be
/// unsigned (the window says so). Everything is unpacked into a temporary folder of the operator's profile, read into
/// memory and deleted right after (vendor files, not directory data).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPolicyPackageExtractor : IPolicyPackageExtractor
{
    /// <summary>How long an installer may take.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public async Task<PolicyTemplatePackage> ExtractAsync(string file, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        var path = Path.GetFullPath(file);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var (signed, signer) = await Task.Run(() => Signature(path), cancellationToken).ConfigureAwait(false);
        var microsoft = signed && signer is not null && signer.Contains("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase);
        if (extension is ".msi" or ".exe" && !microsoft)
        {
            throw new PolicyTemplatePackageException(PolicyTemplatePackageError.Unsigned, "The installer is not signed by Microsoft.");
        }

        var temp = System.IO.Directory.CreateTempSubdirectory("admng-admx-").FullName;
        try
        {
            var (tool, arguments) = extension switch
            {
                ".msi" => (Path.Combine(Environment.SystemDirectory, "msiexec.exe"), (IReadOnlyList<string>)["/a", path, "/qn", "TARGETDIR=" + temp]),
                ".exe" => (path, ["/extract:" + temp, "/quiet"]),
                ".cab" => (Path.Combine(Environment.SystemDirectory, "expand.exe"), [path, "-F:*", temp]),
                _ => throw new PolicyTemplatePackageException(PolicyTemplatePackageError.Unsupported, "The file is not an installer or a CAB archive."),
            };
            await RunAsync(tool, arguments, cancellationToken).ConfigureAwait(false);

            var name = Path.GetFileName(path);
            var package = PolicyTemplatePackage.FromFolder(name, temp, eager: true);
            if (package.AdmxFiles.Count == 0 && System.IO.Directory.EnumerateFiles(temp, "*.zip", SearchOption.AllDirectories).FirstOrDefault() is { } zip)
            {
                // Edge's CAB holds MicrosoftEdgePolicyTemplates.zip.
                package = PolicyTemplatePackage.FromZip(name, await File.ReadAllBytesAsync(zip, cancellationToken).ConfigureAwait(false));
            }

            return package.WithTrust(signed ? PackageTrust.Signed : PackageTrust.Unsigned, SignerName(signer));
        }
        catch (InvalidDataException ex)
        {
            throw new PolicyTemplatePackageException(PolicyTemplatePackageError.ExtractionFailed, "The unpacked archive cannot be read.", ex);
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

    /// <summary>The signature of a file: valid (chain trusted, WinVerifyTrust) and the subject of its signer.</summary>
    internal static (bool Valid, string? Subject) Signature(string path)
    {
        var valid = WinVerifyTrust(path) == 0;
        string? subject = null;
        try
        {
#pragma warning disable SYSLIB0057 // No loader replaces reading the signer of an Authenticode-signed file.
            subject = X509Certificate.CreateFromSignedFile(path).Subject;
#pragma warning restore SYSLIB0057
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Not signed.
        }

        return (valid && subject is not null, subject);
    }

    /// <summary>"Microsoft Corporation" from "CN=Microsoft Corporation, O=Microsoft Corporation, …".</summary>
    private static string? SignerName(string? subject)
    {
        if (subject is null)
        {
            return null;
        }

        foreach (var part in subject.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("O=", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[2..].Trim('"');
            }
        }

        return subject;
    }

    private static async Task RunAsync(string tool, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

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

    // ---- WinVerifyTrust ----------------------------------------------------------------------------------------------

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeWholeChain = 1;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdCacheOnlyUrlRetrieval = 0x1000;

    /// <summary>WINTRUST_ACTION_GENERIC_VERIFY_V2.</summary>
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    /// <summary>0 when the file's Authenticode signature is valid and its chain trusted.</summary>
    private static int WinVerifyTrust(string path)
    {
        var pathPointer = Marshal.StringToCoTaskMemUni(path);
        var fileInfo = new WinTrustFileInfo { StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = pathPointer };
        var filePointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, fDeleteOld: false);
            var data = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeWholeChain,
                UnionChoice = WtdChoiceFile,
                File = filePointer,
                StateAction = WtdStateActionVerify,
                ProviderFlags = WtdCacheOnlyUrlRetrieval,
            };
            var action = GenericVerifyV2;
            var result = WinVerifyTrustNative(IntPtr.Zero, ref action, ref data);
            data.StateAction = WtdStateActionClose;
            _ = WinVerifyTrustNative(IntPtr.Zero, ref action, ref data);
            return result;
        }
        finally
        {
            Marshal.FreeCoTaskMem(filePointer);
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    [DllImport("wintrust.dll", EntryPoint = "WinVerifyTrust", ExactSpelling = true)]
    private static extern int WinVerifyTrustNative(IntPtr window, ref Guid action, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr File;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}
