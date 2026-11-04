using AdMng.Presentation.Services;
using AdMng.Presentation.UserMessages;

namespace AdMng.Presentation.Correspondence;

/// <summary>What the Korespondencja window starts with.</summary>
public enum CorrespondenceStart
{
    /// <summary>The recipients tab (the DNs become recipients from AD).</summary>
    Recipients,

    /// <summary>The "Skrzynki" tab (the DNs are the scope of the mailbox owner query).</summary>
    MailboxOwners,
}

/// <summary>Opens the Korespondencja window (design note docs/dev/u5-outlook-correspondence.md 6.2, 6.3).</summary>
/// <param name="DistinguishedNames">The selected objects (empty: a new window without recipients).</param>
/// <param name="Start">What the window starts with.</param>
public sealed record CorrespondenceRequest(IReadOnlyList<string> DistinguishedNames, CorrespondenceStart Start = CorrespondenceStart.Recipients)
{
    public override string ToString() => $"{nameof(CorrespondenceRequest)} {Start} ({DistinguishedNames.Count})";
}

/// <summary>
/// Protects the journal key (App: DPAPI CurrentUser with the entropy "AdMng.CorrespondenceJournal"; tests: an XOR fake).
/// Application singleton.
/// </summary>
public interface ICorrespondenceKeyProtector
{
    byte[] Protect(byte[] data);

    byte[] Unprotect(byte[] data);
}

/// <summary>Facts of the computer the window runs on (App singleton).</summary>
public interface ICorrespondencePlatform
{
    /// <summary>The user's Downloads folder (SHGetKnownFolderPath), or null.</summary>
    string? DownloadsFolder { get; }

    /// <summary>The ANSI code page of legacy text files (<see cref="IRegionalSettings.AnsiCodePage"/>).</summary>
    int AnsiCodePage { get; }
}

/// <summary>"Otwórz w przeglądarce": the preview through a one-shot loopback server (design note 9.3; nothing on disk).</summary>
public interface IBrowserPreview
{
    Task OpenAsync(HtmlPreviewContent content, string title);
}

/// <summary>The dialogs of the Korespondencja window (App: the connection window's <c>DialogService</c>, scoped).</summary>
public interface ICorrespondenceDialogs
{
    /// <summary>The system open dialog with multi-select; empty when cancelled.</summary>
    Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<FileFilter> filters);

    /// <summary>A text prompt (one line or multiline); null when cancelled.</summary>
    Task<string?> PromptTextAsync(string title, string message, string initial, bool multiline);

    /// <summary>A read-only text report with a copy button.</summary>
    Task ShowReportAsync(string title, string text);

    /// <summary>The system folder picker; null when cancelled.</summary>
    Task<string?> PickFolderAsync(string title);
}

/// <summary>Opens the Korespondencja window of the connection window (App: <c>DialogService</c>, scoped).</summary>
public interface ICorrespondenceLauncher
{
    Task ShowCorrespondenceAsync(CorrespondenceRequest request);
}
