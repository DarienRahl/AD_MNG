using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>The <c>outbox.payload</c> blob: the rendered message (set to NULL after a successful send, spec 16.3).</summary>
internal sealed record StoredOutboxPayload
{
    public RenderedEmail? Email { get; init; }

    public RenderedHttpMessage? Http { get; init; }
}
