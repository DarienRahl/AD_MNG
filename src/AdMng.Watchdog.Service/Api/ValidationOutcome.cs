using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Service.Api;

/// <summary>A validation result with what was read for it: stored secret bindings and resolved channel URL hosts.</summary>
public sealed record ValidationOutcome(
    ConfigValidationResult Result,
    IReadOnlyDictionary<string, string> StoredBindings,
    IReadOnlyDictionary<string, string?> UrlHosts);
