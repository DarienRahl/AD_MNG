using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>What an (unsaved) rule would have matched and sent over the last days.</summary>
public interface IRulePreviewService
{
    Task<RulePreviewResult> PreviewAsync(RulePreviewRequest request, CancellationToken cancellationToken);
}
