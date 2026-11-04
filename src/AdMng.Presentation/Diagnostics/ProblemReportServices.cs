using AdMng.Core.Correspondence.Outlook;
using AdMng.Presentation.Readiness;

namespace AdMng.Presentation.Diagnostics;

/// <summary>Whether an optional component of this computer can be used, as the problem report lists it.</summary>
public enum OptionalFeatureState
{
    /// <summary>Not checked (the host has no probe for it, e.g. the tests).</summary>
    Unknown,

    Available,

    NotAvailable,

    /// <summary>Present but switched off on purpose (e.g. WebView2 by <c>ADMNG_UI_NO_WEBVIEW2</c>).</summary>
    Disabled,
}

/// <summary>
/// The WebView2 runtime of the formatted previews (U1c, U5), as far as the problem report needs it. Implemented by the App
/// (WPF-free contract); checked without starting a browser.
/// </summary>
public interface IWebView2Runtime
{
    /// <summary>Installed and usable, missing, or switched off.</summary>
    OptionalFeatureState Availability { get; }
}

/// <summary>
/// What the problem report of one connection window reads besides <see cref="Dialogs.DialogServices"/> (parameter object,
/// scoped like the readiness results it carries): the application's diagnostic buffer, the readiness results of this window
/// and the probes of the optional components (null where the host has none).
/// </summary>
public sealed class ProblemReportServices(
    DiagnosticLogBuffer log,
    IReadinessResults readiness,
    IOutlookClient? outlook = null,
    IWebView2Runtime? webView2 = null)
{
    public DiagnosticLogBuffer Log { get; } = log ?? throw new ArgumentNullException(nameof(log));

    public IReadinessResults Readiness { get; } = readiness ?? throw new ArgumentNullException(nameof(readiness));

    /// <summary>Classic Outlook (Korespondencja, U5); only its registration is read, Outlook is never started.</summary>
    public IOutlookClient? Outlook { get; } = outlook;

    public IWebView2Runtime? WebView2 { get; } = webView2;

    /// <summary>Whether classic Outlook is installed, without starting or attaching to it.</summary>
    public OptionalFeatureState OutlookFeature => Outlook?.Availability switch
    {
        null => OptionalFeatureState.Unknown,
        OutlookAvailability.Installed => OptionalFeatureState.Available,
        _ => OptionalFeatureState.NotAvailable,
    };

    /// <summary>Whether the WebView2 runtime can be used.</summary>
    public OptionalFeatureState WebView2Feature => WebView2?.Availability ?? OptionalFeatureState.Unknown;
}

/// <summary>
/// The default <see cref="IReadinessResults"/> (no readiness check registered, or not run yet in this window): no results.
/// </summary>
internal sealed class EmptyReadinessResults : IReadinessResults
{
    public IReadOnlyList<ReadinessResultCode> Last => [];
}
