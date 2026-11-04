using System.Runtime.InteropServices;

namespace AdMng.Presentation.Readiness;

/// <summary>
/// Facts about the operator's computer for "Ten komputer" of the readiness check: the Windows release and whether the
/// WebView2 runtime of the message previews is there. The WPF app reads the registry and the WebView2 loader; other hosts
/// (tests, Linux) use <see cref="RuntimeComputerInfo"/>. Nothing about the directory; nothing is written.
/// </summary>
public interface ILocalComputerInfo
{
    /// <summary>The computer runs Windows.</summary>
    bool IsWindows { get; }

    /// <summary>The product name as AD writes it into <c>operatingSystem</c> ("Windows 11 Pro"), or the platform's description off Windows.</summary>
    string OperatingSystem { get; }

    /// <summary>The OS build (26100), null when unknown.</summary>
    int? Build { get; }

    /// <summary>The update build revision (UBR, e.g. 4652), null when unknown.</summary>
    int? UpdateBuildRevision { get; }

    /// <summary>The host can tell whether the WebView2 runtime is installed (false: the check is not run).</summary>
    bool CanProbeWebView2 { get; }

    /// <summary>WebView2 is switched off for this process (<c>ADMNG_UI_NO_WEBVIEW2=1</c>).</summary>
    bool IsWebView2Disabled { get; }

    /// <summary>The version of the installed WebView2 runtime, null when there is none (or it cannot be probed).</summary>
    string? WebView2Version { get; }
}

/// <summary>The facts the .NET runtime knows (no registry, no WebView2 probe): the default of hosts without the WPF app.</summary>
public sealed class RuntimeComputerInfo : ILocalComputerInfo
{
    public bool IsWindows => System.OperatingSystem.IsWindows();

    public string OperatingSystem => IsWindows ? "Windows" : RuntimeInformation.OSDescription;

    public int? Build => IsWindows ? Environment.OSVersion.Version.Build : null;

    public int? UpdateBuildRevision => null;

    public bool CanProbeWebView2 => false;

    public bool IsWebView2Disabled => false;

    public string? WebView2Version => null;
}
