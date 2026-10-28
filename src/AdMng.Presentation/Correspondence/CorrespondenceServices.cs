using AdMng.Core.Audit;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Core.Csv;
using AdMng.Core.Messaging;
using AdMng.Presentation.UserMessages;

namespace AdMng.Presentation.Correspondence;

/// <summary>
/// The services of the Korespondencja windows of one connection window (scoped parameter object, design note 6.2): the
/// Outlook client, the signature source, the journal, the templates, the image store, the window's dialogs, the platform,
/// the browser preview, the CSV codec, the window's audit log, the clock, the delay between messages and the U1 field
/// engine. The App provides the Outlook client, the signatures, the dialogs, the platform and the browser preview; a host
/// without them (tests, Linux) gets null and the window disables what needs them.
/// </summary>
public sealed class CorrespondenceServices
{
    public CorrespondenceServices(
        CorrespondenceTemplateLibrary templates,
        CorrespondenceJournal journal,
        ICsvCodec csv,
        IAuditLog auditLog,
        TimeProvider time,
        IOutlookClient? outlook = null,
        IOutlookSignatureSource? signatures = null,
        MessageImageStore? images = null,
        ICorrespondenceDialogs? dialogs = null,
        ICorrespondencePlatform? platform = null,
        IBrowserPreview? browserPreview = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        Templates = templates ?? throw new ArgumentNullException(nameof(templates));
        Journal = journal ?? throw new ArgumentNullException(nameof(journal));
        Csv = csv ?? throw new ArgumentNullException(nameof(csv));
        AuditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        Time = time ?? throw new ArgumentNullException(nameof(time));
        Outlook = outlook;
        Signatures = signatures;
        Images = images;
        Dialogs = dialogs;
        Platform = platform;
        BrowserPreview = browserPreview;
        Delay = delay ?? ((span, token) => Task.Delay(span, time, token));
    }

    public CorrespondenceTemplateLibrary Templates { get; }

    /// <summary>"Już wysłane" (keyed hashes, application-wide).</summary>
    public CorrespondenceJournal Journal { get; }

    public ICsvCodec Csv { get; }

    /// <summary>The session log of the connection window (one entry per run, no addresses or subjects).</summary>
    public IAuditLog AuditLog { get; }

    public TimeProvider Time { get; }

    /// <summary>Classic Outlook (null: the host has none – sending is disabled).</summary>
    public IOutlookClient? Outlook { get; }

    /// <summary>Outlook signatures (null: no signatures offered).</summary>
    public IOutlookSignatureSource? Signatures { get; }

    /// <summary>The image files of saved templates (null: templates are saved without images).</summary>
    public MessageImageStore? Images { get; }

    /// <summary>Multi-file open, text prompt, report and folder picker (null: those actions are disabled).</summary>
    public ICorrespondenceDialogs? Dialogs { get; }

    public ICorrespondencePlatform? Platform { get; }

    public IBrowserPreview? BrowserPreview { get; }

    /// <summary>The delay between messages (tests advance a manual clock instead of waiting).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; }

    /// <summary>The U1 template expressions of AD fields (decision D2).</summary>
    public UserMessageFields Fields { get; } = new();

    public override string ToString() => nameof(CorrespondenceServices);
}
