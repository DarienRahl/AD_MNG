using System.Globalization;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Rules.Validation;

/// <summary>One validation pass: the (normalized) document, its environment and the collected issues.</summary>
internal sealed class ValidationRun(
    WatchdogConfiguration configuration,
    ConfigValidationContext context,
    DateTimeOffset now,
    DateTimeOffset? baselineCompletedAt)
{
    private readonly List<ConfigIssue> _issues = [];

    public WatchdogConfiguration Configuration { get; } = configuration;

    public ConfigValidationContext Context { get; } = context;

    public DateTimeOffset Now { get; } = now;

    /// <summary>When the detection baseline completed (null = unknown or not yet).</summary>
    public DateTimeOffset? BaselineCompletedAt { get; } = baselineCompletedAt;

    public IReadOnlyList<ConfigIssue> Issues => _issues;

    public void Error(string path, string code, string message, params string[] args) =>
        Add(path, code, IssueSeverity.Error, message, args);

    public void Warning(string path, string code, string message, params string[] args) =>
        Add(path, code, IssueSeverity.Warning, message, args);

    public ConfigValidationResult ToResult() => new() { Issues = [.. _issues] };

    /// <summary><c>prefix[index]</c>.</summary>
    public static string Item(string prefix, int index) => string.Create(CultureInfo.InvariantCulture, $"{prefix}[{index}]");

    /// <summary>Invariant text of a number or time span for <see cref="ConfigIssue.Args"/>.</summary>
    public static string Arg(IFormattable value) => value.ToString(null, CultureInfo.InvariantCulture);

    private void Add(string path, string code, IssueSeverity severity, string message, string[] args) =>
        _issues.Add(new ConfigIssue { Path = path, Code = code, Severity = severity, Message = message, Args = args });
}
