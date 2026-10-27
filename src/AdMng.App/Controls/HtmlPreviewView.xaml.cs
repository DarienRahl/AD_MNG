using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using AdMng.App.Services;
using AdMng.Presentation.UserMessages;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AdMng.App.Controls;

/// <summary>
/// Renders <see cref="Preview"/> (an HTML body and its inline images, from memory) with WebView2 (U1c, design note u1c-u1d
/// section 10). Hardening: in-private controller, scripts, web messages, host objects, dev tools, context menus, dialogs,
/// status bar, browser keys, autofill, the error page and swipe navigation off; the control navigates to
/// <see cref="WebView2Host.DocumentUrl"/> and a request filter on <c>*</c> answers that document and
/// <c>…/cid/{contentId}</c> images from memory and every other request with 403 – remote images, fonts and tracking pixels
/// never load, matching what is sent. The preview copy (never the sent HTML) has its <c>cid:</c> references rewritten and a
/// Content-Security-Policy header. Any other navigation, new windows, downloads and permissions are refused. Without
/// WebView2 (or with <see cref="IsRenderingAllowed"/> off) the HTML source is shown with a one-line note.
/// </summary>
public partial class HtmlPreviewView : UserControl
{
    public static readonly DependencyProperty PreviewProperty = DependencyProperty.Register(
        nameof(Preview), typeof(HtmlPreviewContent), typeof(HtmlPreviewView), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty IsRenderingAllowedProperty = DependencyProperty.Register(
        nameof(IsRenderingAllowed), typeof(bool), typeof(HtmlPreviewView), new PropertyMetadata(true, OnChanged));

    /// <summary>The Content-Security-Policy of the preview document.</summary>
    internal const string ContentSecurityPolicy = "default-src 'none'; img-src " + WebView2Host.Origin + "; style-src 'unsafe-inline'";

    private WebView2? _browser;
    private Task? _browserReady;
    private HtmlPreviewContent _shown = HtmlPreviewContent.Empty;
    private int _version;
    private bool _closedHooked;
    private bool _closed;

    public HtmlPreviewView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            HookWindowClosed();
            Refresh();
        };

        // A hidden view (the formatting preview while the source is edited, a tab not selected) starts no browser.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Refresh();
            }
        };
    }

    /// <summary>What is shown: the HTML and its images by content id.</summary>
    public HtmlPreviewContent? Preview
    {
        get => (HtmlPreviewContent?)GetValue(PreviewProperty);
        set => SetValue(PreviewProperty, value);
    }

    /// <summary>The settings allow the rendered preview (<c>UserMessages.RenderedPreview</c>).</summary>
    public bool IsRenderingAllowed
    {
        get => (bool)GetValue(IsRenderingAllowedProperty);
        set => SetValue(IsRenderingAllowedProperty, value);
    }

    /// <summary>The WebView2 environment of the application (null: the source view only).</summary>
    public WebView2Host? Host { get; set; }

    /// <summary>The HTML source is shown instead of the rendered preview.</summary>
    public bool IsShowingSource => FallbackPanel.Visibility == Visibility.Visible;

    /// <summary>WebView2 finished rendering the current preview (UI tests).</summary>
    public bool IsRendered { get; private set; }

    /// <summary>Raised when WebView2 finished a navigation (UI tests).</summary>
    public event EventHandler? Rendered;

    /// <summary>The document WebView2 is given: <c>cid:</c> image sources point to the preview origin.</summary>
    internal static string PreviewDocument(string html) => CidSource().Replace(html, m => $"src={m.Groups[1].Value}{WebView2Host.CidPrefix}{m.Groups[2].Value}{m.Groups[1].Value}");

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((HtmlPreviewView)d).Refresh();

    private async void Refresh()
    {
        var content = Preview ?? HtmlPreviewContent.Empty;
        SourceBox.Text = content.Html;
        EmptyText.Visibility = content.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        if (_closed || !IsLoaded || !IsVisible)
        {
            return;
        }

        if (content.IsEmpty)
        {
            BrowserHost.Visibility = Visibility.Collapsed;
            FallbackPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (!IsRenderingAllowed || Host is null)
        {
            ShowSource(disabled: !IsRenderingAllowed);
            return;
        }

        try
        {
            if (await Host.GetEnvironmentAsync().ConfigureAwait(true) is not { } environment)
            {
                ShowSource(disabled: false);
                return;
            }

            // The host must be visible while the control is created: WebView2 waits for its window handle.
            BrowserHost.Visibility = Visibility.Visible;
            FallbackPanel.Visibility = Visibility.Collapsed;
            _browserReady ??= CreateBrowserAsync(environment);
            await _browserReady.ConfigureAwait(true);
            if (_closed)
            {
                return;
            }

            if (_browser?.CoreWebView2 is not { } core)
            {
                ShowSource(disabled: false);
                return;
            }

            var latest = Preview ?? HtmlPreviewContent.Empty;
            if (IsRendered && BrowserHost.Visibility == Visibility.Visible && SameContent(_shown, latest))
            {
                return;
            }

            _shown = latest;
            IsRendered = false;
            BrowserHost.Visibility = Visibility.Visible;
            FallbackPanel.Visibility = Visibility.Collapsed;
            core.Navigate($"{WebView2Host.DocumentUrl}?v={++_version}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or WebView2RuntimeNotFoundException or ObjectDisposedException)
        {
            ShowSource(disabled: false);
        }
    }

    private static bool SameContent(HtmlPreviewContent shown, HtmlPreviewContent latest) =>
        string.Equals(shown.Html, latest.Html, StringComparison.Ordinal)
        && shown.Images.Count == latest.Images.Count
        && shown.Images.All(i => latest.Images.TryGetValue(i.Key, out var image) && ReferenceEquals(image, i.Value));

    private async Task CreateBrowserAsync(CoreWebView2Environment environment)
    {
        // Assigned before the first await, so a window closed meanwhile disposes it (HookWindowClosed).
        var browser = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.White };
        _browser = browser;
        BrowserHost.Child = browser;
        var options = environment.CreateCoreWebView2ControllerOptions();
        options.IsInPrivateModeEnabled = true;
        await browser.EnsureCoreWebView2Async(environment, options).ConfigureAwait(true);
        if (_closed)
        {
            return;
        }

        var core = browser.CoreWebView2;
        var settings = core.Settings;
        settings.IsScriptEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsZoomControlEnabled = true;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => Answer(environment, e);
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(WebView2Host.DocumentUrl, StringComparison.Ordinal))
            {
                e.Cancel = true;
            }
        };
        core.NavigationCompleted += (_, e) =>
        {
            IsRendered = e.IsSuccess;
            Rendered?.Invoke(this, EventArgs.Empty);
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) =>
        {
            e.Cancel = true;
            e.Handled = true;
        };
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
    }

    /// <summary>Every request of the preview: the document and its images from memory, everything else 403.</summary>
    private void Answer(CoreWebView2Environment environment, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = e.Request.Uri;
        var path = uri.Split('?', '#')[0];
        if (string.Equals(path, WebView2Host.DocumentUrl, StringComparison.Ordinal))
        {
            var document = new MemoryStream(Encoding.UTF8.GetBytes(PreviewDocument(_shown.Html)));
            e.Response = environment.CreateWebResourceResponse(
                document,
                200,
                "OK",
                $"Content-Type: text/html; charset=utf-8\r\nContent-Security-Policy: {ContentSecurityPolicy}\r\nCache-Control: no-store");
            return;
        }

        if (path.StartsWith(WebView2Host.CidPrefix, StringComparison.Ordinal)
            && _shown.Images.TryGetValue(Uri.UnescapeDataString(path[WebView2Host.CidPrefix.Length..]), out var image))
        {
            e.Response = environment.CreateWebResourceResponse(new MemoryStream(image.Data), 200, "OK", $"Content-Type: {image.MediaType}\r\nCache-Control: no-store");
            return;
        }

        e.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "Cache-Control: no-store");
    }

    private void ShowSource(bool disabled)
    {
        BrowserHost.Visibility = Visibility.Collapsed;
        FallbackPanel.Visibility = Visibility.Visible;
        DisabledText.Visibility = disabled ? Visibility.Visible : Visibility.Collapsed;
        UnavailableText.Visibility = disabled ? Visibility.Collapsed : Visibility.Visible;
        DownloadLinkText.Visibility = disabled ? Visibility.Collapsed : Visibility.Visible;
    }

    private void HookWindowClosed()
    {
        if (_closedHooked || Window.GetWindow(this) is not { } window)
        {
            return;
        }

        _closedHooked = true;
        window.Closed += (_, _) =>
        {
            _closed = true;
            _browser?.Dispose();
            _browser = null;
        };
    }

    /// <summary>The download page opens in the default browser only when the operator clicks the link.</summary>
    private void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(WebView2Host.DownloadUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No default browser: the address stays visible in the note.
        }
    }

    [GeneratedRegex("""src\s*=\s*(["'])cid:([^"']+)\1""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CidSource();
}
