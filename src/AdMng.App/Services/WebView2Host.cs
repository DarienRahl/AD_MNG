using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace AdMng.App.Services;

/// <summary>
/// The one WebView2 environment of the process for the rendered preview of messages (U1c, design note u1c-u1d section 10),
/// created lazily on first use: the Evergreen runtime (part of Windows 11 and current Windows 10; possibly missing on
/// Windows Server jump hosts – then <see cref="GetEnvironmentAsync"/> answers null and the views show the HTML source), a user
/// data folder <c>%TEMP%\AdMng-WebView2-{pid}-{random}</c> deleted when the application exits (stale ones of ended processes
/// at the next start) and a browser without background networking, component updates, sync, pings or crash reporting.
/// <c>ADMNG_UI_NO_WEBVIEW2=1</c> forces the fallback (UI tests). Application-wide (singleton).
/// </summary>
public sealed class WebView2Host(ILogger<WebView2Host> logger) : IDisposable
{
    /// <summary>The origin of the preview (a reserved name that never resolves); everything is answered from memory.</summary>
    public const string Origin = "https://preview.admng.invalid";

    /// <summary>The document of the preview.</summary>
    public const string DocumentUrl = Origin + "/message";

    /// <summary>The prefix of an inline image (<c>{CidPrefix}{contentId}</c>).</summary>
    public const string CidPrefix = Origin + "/cid/";

    /// <summary>Set to 1: no WebView2, the views show the source (UI tests).</summary>
    public const string DisableVariable = "ADMNG_UI_NO_WEBVIEW2";

    /// <summary>Where the runtime is offered (opened in the default browser only when the operator clicks the link).</summary>
    public const string DownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    private const string FolderPrefix = "AdMng-WebView2-";
    private const string BrowserArguments =
        "--disable-background-networking --disable-component-update --disable-sync --no-pings --disable-breakpad --disable-crash-reporter";

    private readonly Lock _gate = new();
    private Task<CoreWebView2Environment?>? _environment;
    private string? _userDataFolder;

    /// <summary>The fallback is forced by <see cref="DisableVariable"/>.</summary>
    public static bool IsDisabledByEnvironment => Environment.GetEnvironmentVariable(DisableVariable) == "1";

    /// <summary>No WebView2 at all: the views show the HTML source (default: <see cref="IsDisabledByEnvironment"/>; the UI
    /// tests set it for the fallback test).</summary>
    public bool IsDisabled { get; init; } = IsDisabledByEnvironment;

    /// <summary>The user data folder of this process while WebView2 runs (UI tests check that it is deleted).</summary>
    public string? UserDataFolder => _userDataFolder;

    /// <summary>The environment, or null when WebView2 cannot be used (runtime missing, forced off, creation failed).
    /// Call on the UI thread.</summary>
    public Task<CoreWebView2Environment?> GetEnvironmentAsync()
    {
        lock (_gate)
        {
            return _environment ??= CreateAsync();
        }
    }

    private async Task<CoreWebView2Environment?> CreateAsync()
    {
        if (IsDisabled)
        {
            return null;
        }

        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            logger.LogInformation("The WebView2 runtime is not available; the message preview shows the HTML source.");
            return null;
        }

        DeleteStaleFolders();
        var folder = Path.Combine(Path.GetTempPath(), $"{FolderPrefix}{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, folder, new CoreWebView2EnvironmentOptions(BrowserArguments)).ConfigureAwait(true);
            _userDataFolder = folder;
            return environment;
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            logger.LogWarning("WebView2 could not be started ({Error}); the message preview shows the HTML source.", ex.GetType().Name);
            TryDelete(folder);
            return null;
        }
    }

    /// <summary>Deletes the user data folders of processes that ended (a crash dump of a renderer could hold a previewed
    /// message there).</summary>
    private static void DeleteStaleFolders()
    {
        try
        {
            foreach (var folder in System.IO.Directory.EnumerateDirectories(Path.GetTempPath(), FolderPrefix + "*"))
            {
                var name = Path.GetFileName(folder)[FolderPrefix.Length..];
                var dash = name.IndexOf('-', StringComparison.Ordinal);
                if (dash > 0 && int.TryParse(name[..dash], out var processId) && processId != Environment.ProcessId && !IsRunning(processId))
                {
                    TryDelete(folder);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another start tries again.
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (System.IO.Directory.Exists(folder))
            {
                System.IO.Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still in use by the browser process: the next start deletes it.
        }
    }

    /// <summary>Deletes the user data folder. The browser processes end shortly after the last preview control was closed
    /// (with its window), so a locked folder is tried again for up to a second; what stays is deleted at the next start.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _userDataFolder, null) is not { } folder)
        {
            return;
        }

        for (var attempt = 0; attempt < 5 && System.IO.Directory.Exists(folder); attempt++)
        {
            if (attempt > 0)
            {
                Thread.Sleep(200);
            }

            TryDelete(folder);
        }
    }
}
