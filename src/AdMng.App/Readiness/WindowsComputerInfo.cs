using System.Globalization;
using System.IO;
using AdMng.App.Services;
using AdMng.Presentation.Readiness;
using Microsoft.Win32;

namespace AdMng.App.Readiness;

/// <summary>
/// "Ten komputer" of the readiness check on Windows: the product name, build and update build revision from
/// <c>HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion</c> (read only; "Windows 10" in the product name of a Windows 11
/// build – a known quirk of that key – is shown as Windows 11) and the WebView2 runtime through
/// <see cref="WebView2Host.ProbeRuntimeVersion"/> (no environment is created, no browser started). Application-wide.
/// </summary>
public sealed class WindowsComputerInfo(WebView2Host webView2) : ILocalComputerInfo
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    private readonly Lazy<(string Product, int? Build, int? Ubr)> _version = new(Read);
    private readonly Lazy<string?> _webView2 = new(WebView2Host.ProbeRuntimeVersion);

    public bool IsWindows => System.OperatingSystem.IsWindows();

    public string OperatingSystem => _version.Value.Product;

    public int? Build => _version.Value.Build;

    public int? UpdateBuildRevision => _version.Value.Ubr;

    public bool CanProbeWebView2 => IsWindows;

    public bool IsWebView2Disabled => webView2?.IsDisabled == true;

    public string? WebView2Version => IsWebView2Disabled ? null : _webView2.Value;

    private static (string Product, int? Build, int? Ubr) Read()
    {
        var build = Environment.OSVersion.Version.Build;
        if (!System.OperatingSystem.IsWindows())
        {
            return (System.Runtime.InteropServices.RuntimeInformation.OSDescription, null, null);
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CurrentVersionKey);
            var product = key?.GetValue("ProductName") as string ?? "Windows";
            if (int.TryParse(key?.GetValue("CurrentBuildNumber") as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var registryBuild))
            {
                build = registryBuild;
            }

            int? ubr = key?.GetValue("UBR") is int revision ? revision : null;
            if (build >= Core.Computers.WindowsReleaseCatalog.FirstWindows11Build && product.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase))
            {
                product = "Windows 11" + product["Windows 10".Length..];
            }

            return (product, build, ubr);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return ("Windows", build, null);
        }
    }
}
