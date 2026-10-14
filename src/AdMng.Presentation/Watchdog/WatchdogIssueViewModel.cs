using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Presentation.Watchdog;

/// <summary>A validation finding of the configuration (section 8.3) with its localized text.</summary>
public sealed class WatchdogIssueViewModel(ConfigIssue issue, ILocalizer localizer)
{
    public ConfigIssue Issue { get; } = issue ?? throw new ArgumentNullException(nameof(issue));

    public bool IsError => Issue.Severity == IssueSeverity.Error;

    /// <summary>JSON path of the setting ("smtp.host", "rules[3].delivery.channelIds[0]").</summary>
    public string Path => Issue.Path;

    public string Code => Issue.Code;

    /// <summary>Localized text ("Watchdog.Validation.{code}"), else the service's English message.</summary>
    public string Text { get; } = (localizer ?? throw new ArgumentNullException(nameof(localizer))).ValidationText(issue);

    /// <summary>"smtp.host: Wymagane pole." for the issue list.</summary>
    public string DisplayText => string.IsNullOrEmpty(Path) ? Text : $"{Path}: {Text}";
}
