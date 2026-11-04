using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using Microsoft.Win32.SafeHandles;

namespace AdMng.App.GroupPolicy;

/// <summary>The verdict on the Authenticode signature of a template package (K3).</summary>
/// <param name="Valid"><c>WinVerifyTrust</c> accepts the signature and its chain (any root this computer trusts).</param>
/// <param name="Microsoft">
/// Valid, the signer's chain ends in a Microsoft root and its certificate is one Microsoft signs its own software with
/// (<see cref="PackageSigner.IsMicrosoftPublisher"/>): only then may an installer run.
/// </param>
/// <param name="ClaimsMicrosoft">The signer's organization is exactly "Microsoft Corporation" – true or not.</param>
/// <param name="Signer">The signer as shown (<see cref="PackageSigner.DisplayName"/>); null when not valid.</param>
internal readonly record struct AuthenticodeVerdict(bool Valid, bool Microsoft, bool ClaimsMicrosoft, string? Signer);

/// <summary>
/// Checks the Authenticode signature of a file through an open handle (K3, "Zaktualizuj szablony"): <c>WinVerifyTrust</c>
/// (<c>WINTRUST_ACTION_GENERIC_VERIFY_V2</c>, revocation from the cache only) keeps its state, and before the state is closed
/// the signer's chain is taken from it (<c>WTHelperProvDataFromStateData</c>, <c>WTHelperGetProvSignerFromChain</c>): the
/// chain must pass the Microsoft root policy (<c>CertVerifyCertificateChainPolicy(CERT_CHAIN_POLICY_MICROSOFT_ROOT)</c>, the
/// product roots or the application root "Microsoft Root Certificate Authority 2011"), and the leaf certificate of that very
/// chain is the one whose organization is compared and shown. Structures as in wintrust.h and wincrypt.h of the Windows SDK.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AuthenticodeVerifier
{
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeWholeChain = 1;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdCacheOnlyUrlRetrieval = 0x1000;

    /// <summary>CERT_CHAIN_POLICY_MICROSOFT_ROOT (an LPCSTR that is a small integer).</summary>
    private const nint MicrosoftRootPolicy = 7;

    /// <summary>MICROSOFT_ROOT_CERT_CHAIN_POLICY_CHECK_APPLICATION_ROOT_FLAG: only the application root (2011), instead of the product roots.</summary>
    private const uint CheckApplicationRootFlag = 0x0002_0000;

    /// <summary>MICROSOFT_ROOT_CERT_CHAIN_POLICY_DISABLE_FLIGHT_ROOT_FLAG: never the flight (pre-release) root.</summary>
    private const uint DisableFlightRootFlag = 0x0004_0000;

    /// <summary>WINTRUST_ACTION_GENERIC_VERIFY_V2.</summary>
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    /// <summary>The bytes of CRYPT_PROVIDER_SGNR up to and including pChainContext (its cbStruct must reach that far).</summary>
    private static readonly int SignerWithChainSize = (int)Marshal.OffsetOf<CryptProviderSigner>(nameof(CryptProviderSigner.ChainContext)) + IntPtr.Size;

    /// <summary>The signature of the file behind <paramref name="file"/> (opened for reading at <paramref name="path"/>).</summary>
    public static AuthenticodeVerdict Verify(string path, SafeFileHandle file)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(file);
        var pathPointer = IntPtr.Zero;
        var fileInfoPointer = IntPtr.Zero;
        var referenced = false;
        var verifying = false;
        var action = GenericVerifyV2;
        var data = default(WinTrustData);
        try
        {
            file.DangerousAddRef(ref referenced);
            pathPointer = Marshal.StringToCoTaskMemUni(path);
            fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = pathPointer,
                File = file.DangerousGetHandle(),
            };
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            data = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeWholeChain,
                UnionChoice = WtdChoiceFile,
                File = fileInfoPointer,
                StateAction = WtdStateActionVerify,
                ProviderFlags = WtdCacheOnlyUrlRetrieval,
            };
            verifying = true;
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0 ? Inspect(data.StateData) : default;
        }
        catch (CryptographicException)
        {
            return default; // the signer's certificate cannot be read: not taken as signed
        }
        finally
        {
            if (verifying)
            {
                // The state is freed whatever the verdict was; the chain read from it is no longer used.
                data.StateAction = WtdStateActionClose;
                _ = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            }

            if (fileInfoPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(fileInfoPointer);
            }

            if (pathPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }

            if (referenced)
            {
                file.DangerousRelease();
            }
        }
    }

    /// <summary>The signer of a verified file, from the WinVerifyTrust state (still open).</summary>
    private static AuthenticodeVerdict Inspect(IntPtr stateData)
    {
        var provider = stateData == IntPtr.Zero ? IntPtr.Zero : WTHelperProvDataFromStateData(stateData);
        var signerPointer = provider == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        if (signerPointer == IntPtr.Zero || Marshal.ReadInt32(signerPointer) < SignerWithChainSize)
        {
            return default;
        }

        var signer = Marshal.PtrToStructure<CryptProviderSigner>(signerPointer);
        if (signer.ChainContext == IntPtr.Zero)
        {
            return default;
        }

        using var leaf = Leaf(signer.ChainContext);
        if (leaf is null)
        {
            return default;
        }

        // The product roots (Microsoft Root Authority, … Certificate Authority 2010), else the application root (… 2011):
        // with the application flag the policy checks that root only.
        var microsoftRoot = MicrosoftRoot(signer.ChainContext, DisableFlightRootFlag) || MicrosoftRoot(signer.ChainContext, CheckApplicationRootFlag);
        return new AuthenticodeVerdict(
            Valid: true,
            Microsoft: microsoftRoot && PackageSigner.IsMicrosoftPublisher(leaf),
            ClaimsMicrosoft: PackageSigner.ClaimsMicrosoft(leaf),
            Signer: PackageSigner.DisplayName(leaf));
    }

    /// <summary>The signer's own certificate: the first element of the first simple chain (a copy, owned by the caller).</summary>
    private static X509Certificate2? Leaf(IntPtr chainContext)
    {
        var chain = Marshal.PtrToStructure<CertChainContext>(chainContext);
        if (chain.ChainCount == 0 || chain.Chains == IntPtr.Zero)
        {
            return null;
        }

        var simplePointer = Marshal.ReadIntPtr(chain.Chains);
        if (simplePointer == IntPtr.Zero)
        {
            return null;
        }

        var simple = Marshal.PtrToStructure<CertSimpleChain>(simplePointer);
        if (simple.ElementCount == 0 || simple.Elements == IntPtr.Zero)
        {
            return null;
        }

        var elementPointer = Marshal.ReadIntPtr(simple.Elements);
        if (elementPointer == IntPtr.Zero)
        {
            return null;
        }

        var element = Marshal.PtrToStructure<CertChainElement>(elementPointer);

        // The constructor duplicates the context, so the certificate outlives the WinVerifyTrust state.
        return element.CertContext == IntPtr.Zero ? null : new X509Certificate2(element.CertContext);
    }

    private static bool MicrosoftRoot(IntPtr chainContext, uint flags)
    {
        var para = new CertChainPolicyPara { Size = (uint)Marshal.SizeOf<CertChainPolicyPara>(), Flags = flags };
        var status = new CertChainPolicyStatus { Size = (uint)Marshal.SizeOf<CertChainPolicyStatus>() };
        return CertVerifyCertificateChainPolicy(MicrosoftRootPolicy, chainContext, ref para, ref status) && status.Error == 0;
    }

    // ---- wintrust.dll, crypt32.dll -----------------------------------------------------------------------------------

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr providerData, uint signerIndex, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [DllImport("crypt32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertVerifyCertificateChainPolicy(nint policyOid, IntPtr chainContext, ref CertChainPolicyPara policyPara, ref CertChainPolicyStatus policyStatus);

    /// <summary>WINTRUST_FILE_INFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr File;
        public IntPtr KnownSubject;
    }

    /// <summary>WINTRUST_DATA (the union is the WINTRUST_FILE_INFO pointer).</summary>
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

    /// <summary>CRYPT_PROVIDER_SGNR (sftVerifyAsOf is a FILETIME: two DWORDs).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderSigner
    {
        public uint StructSize;
        public uint VerifyAsOfLow;
        public uint VerifyAsOfHigh;
        public uint CertChainCount;
        public IntPtr CertChain;
        public uint SignerType;
        public IntPtr Signer;
        public uint Error;
        public uint CounterSignerCount;
        public IntPtr CounterSigners;
        public IntPtr ChainContext;
    }

    /// <summary>The head of CERT_CHAIN_CONTEXT (cbSize, TrustStatus, cChain, rgpChain).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CertChainContext
    {
        public uint Size;
        public uint TrustErrorStatus;
        public uint TrustInfoStatus;
        public uint ChainCount;
        public IntPtr Chains;
    }

    /// <summary>The head of CERT_SIMPLE_CHAIN (cbSize, TrustStatus, cElement, rgpElement).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CertSimpleChain
    {
        public uint Size;
        public uint TrustErrorStatus;
        public uint TrustInfoStatus;
        public uint ElementCount;
        public IntPtr Elements;
    }

    /// <summary>The head of CERT_CHAIN_ELEMENT (cbSize, pCertContext).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CertChainElement
    {
        public uint Size;
        public IntPtr CertContext;
    }

    /// <summary>CERT_CHAIN_POLICY_PARA.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CertChainPolicyPara
    {
        public uint Size;
        public uint Flags;
        public IntPtr ExtraPolicyPara;
    }

    /// <summary>CERT_CHAIN_POLICY_STATUS.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CertChainPolicyStatus
    {
        public uint Size;
        public uint Error;
        public int ChainIndex;
        public int ElementIndex;
        public IntPtr ExtraPolicyStatus;
    }
}
