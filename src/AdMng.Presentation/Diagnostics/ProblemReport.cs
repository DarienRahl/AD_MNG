using System.Globalization;
using System.Text;
using AdMng.Core.Directory;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Readiness;

namespace AdMng.Presentation.Diagnostics;

/// <summary>
/// How the connection of a window logged on and protects its channel, for the problem report: the logon type and the
/// transport flags only – never the server, the domain or the account (CLAUDE.md rule 1).
/// </summary>
/// <param name="Authentication">Windows logon, typed credentials or simple bind.</param>
/// <param name="Security">Signing and sealing, LDAPS, StartTLS or none.</param>
/// <param name="SecurityStrengthFactor">The SASL security strength factor where it is reported (Kerberos off Windows), else null.</param>
/// <param name="IsGlobalCatalog">The session reads the global catalog.</param>
public sealed record ProblemReportConnection(AuthenticationMode Authentication, TransportSecurity Security, int? SecurityStrengthFactor, bool IsGlobalCatalog)
{
    /// <summary>The flags of a bound session (nothing else of <paramref name="info"/> is taken).</summary>
    public static ProblemReportConnection Of(SessionInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new ProblemReportConnection(info.Authentication, info.Security, info.SecurityStrengthFactor, info.IsGlobalCatalog);
    }

    /// <summary>The channel is signed (Negotiate signing and sealing).</summary>
    public bool Signing => Security == TransportSecurity.SignAndSeal;

    /// <summary>The channel is sealed by Negotiate (a bind without sealing is refused for <see cref="TransportSecurity.SignAndSeal"/>).</summary>
    public bool Sealing => Security == TransportSecurity.SignAndSeal;

    public bool Ldaps => Security == TransportSecurity.Tls;

    public bool StartTls => Security == TransportSecurity.StartTls;
}

/// <summary>
/// Everything a problem report shows (design note s-pilot-readiness.md 3) – facts about the program, this computer and the
/// window's connection, codes and statuses, and log templates; no directory value can be represented here.
/// </summary>
public sealed record ProblemReportContent
{
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>"0.12.0 (9def9bf)".</summary>
    public required string Version { get; init; }

    /// <summary>The operating system as the runtime describes it (name, version, build).</summary>
    public required string OperatingSystem { get; init; }

    public required string OperatingSystemArchitecture { get; init; }

    /// <summary>The .NET runtime (".NET 10.0.1").</summary>
    public required string Runtime { get; init; }

    public required string ProcessArchitecture { get; init; }

    /// <summary>The UI language (culture name).</summary>
    public required string UiCulture { get; init; }

    /// <summary>The culture of dates and numbers (culture name).</summary>
    public required string FormatCulture { get; init; }

    /// <summary>The window's connection, or null while it is not connected.</summary>
    public ProblemReportConnection? Connection { get; init; }

    public OptionalFeatureState Outlook { get; init; }

    public OptionalFeatureState WebView2 { get; init; }

    /// <summary>A Watchdog service address is set (the address itself is not shown: it names a host).</summary>
    public bool WatchdogConfigured { get; init; }

    public IReadOnlyList<ReadinessResultCode> Readiness { get; init; } = [];

    /// <summary>The diagnostic buffer, oldest first.</summary>
    public IReadOnlyList<DiagnosticLogEntry> Log { get; init; } = [];

    /// <summary>The entries logged since the start (more than <see cref="Log"/> once the buffer is full).</summary>
    public long LogTotal { get; init; }
}

/// <summary>
/// Writes a <see cref="ProblemReportContent"/> as the text the operator copies or saves: labels in the UI language, values
/// (versions, enum names, codes, times, templates) invariant so the reader of any report finds the same words.
/// </summary>
public static class ProblemReportBuilder
{
    private const string Indent = "  ";

    public static string Build(ProblemReportContent content, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(localizer);
        var text = new StringBuilder();
        text.AppendLine(localizer[L.ProblemReport.Heading]);
        text.AppendLine(localizer.Format(L.ProblemReport.Created, Time(content.CreatedAt)));
        text.AppendLine(localizer[L.ProblemReport.ValueFree]);

        Section(text, localizer[L.ProblemReport.SectionEnvironment]);
        Line(text, localizer.Format(L.ProblemReport.Version, content.Version));
        Line(text, localizer.Format(L.ProblemReport.OperatingSystem, content.OperatingSystem, content.OperatingSystemArchitecture));
        Line(text, localizer.Format(L.ProblemReport.Runtime, content.Runtime, content.ProcessArchitecture));
        Line(text, localizer.Format(L.ProblemReport.Culture, content.UiCulture, content.FormatCulture));

        Section(text, localizer[L.ProblemReport.SectionConnection]);
        if (content.Connection is not { } connection)
        {
            Line(text, localizer[L.ProblemReport.NotConnected]);
        }
        else
        {
            Line(text, localizer.Format(L.ProblemReport.Logon, localizer[DisplayNames.AuthenticationKey(connection.Authentication)], connection.Authentication));
            Line(text, localizer.Format(L.ProblemReport.Mechanism, localizer[connection.Authentication == AuthenticationMode.SimpleBind
                ? L.ProblemReport.MechanismSimpleBind
                : L.ProblemReport.MechanismNegotiate]));
            Line(text, localizer.Format(L.ProblemReport.Protection, localizer[DisplayNames.SecurityKey(connection.Security)], connection.Security));
            Line(text, localizer.Format(L.ProblemReport.Flags, YesNo(localizer, connection.Signing), YesNo(localizer, connection.Sealing),
                YesNo(localizer, connection.Ldaps), YesNo(localizer, connection.StartTls)));
            Line(text, localizer.Format(L.ProblemReport.GlobalCatalog, YesNo(localizer, connection.IsGlobalCatalog)));
            if (connection.SecurityStrengthFactor is { } ssf)
            {
                Line(text, localizer.Format(L.ProblemReport.SecurityStrength, ssf.ToString(CultureInfo.InvariantCulture)));
            }
        }

        Section(text, localizer[L.ProblemReport.SectionFeatures]);
        Line(text, localizer.Format(L.ProblemReport.Outlook, Feature(localizer, content.Outlook)));
        Line(text, localizer.Format(L.ProblemReport.WebView2, Feature(localizer, content.WebView2)));
        Line(text, localizer.Format(L.ProblemReport.Watchdog, YesNo(localizer, content.WatchdogConfigured)));

        Section(text, localizer[L.ProblemReport.SectionReadiness]);
        if (content.Readiness.Count == 0)
        {
            Line(text, localizer[L.ProblemReport.ReadinessNone]);
        }

        foreach (var result in content.Readiness)
        {
            Line(text, $"{result.Code}: {result.Status}");
        }

        Section(text, localizer.Format(L.ProblemReport.SectionLog,
            content.Log.Count.ToString(CultureInfo.InvariantCulture), content.LogTotal.ToString(CultureInfo.InvariantCulture)));
        if (content.Log.Count == 0)
        {
            Line(text, localizer[L.ProblemReport.LogEmpty]);
        }

        foreach (var entry in content.Log)
        {
            Entry(text, entry);
        }

        return text.ToString();
    }

    /// <summary>One entry: time, level, category, event id (and name), template; then one line per exception.</summary>
    private static void Entry(StringBuilder text, DiagnosticLogEntry entry)
    {
        var eventId = entry.EventName is null
            ? entry.EventId.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{entry.EventId} {entry.EventName}");
        var template = entry.Template is null ? "-" : entry.Template.ReplaceLineEndings(" ");
        Line(text, $"{Time(entry.Time)} {entry.Level} {entry.Category} [{eventId}] {template}");
        foreach (var exception in entry.Exceptions)
        {
            Line(text, string.Create(CultureInfo.InvariantCulture, $"{Indent}{exception.Type} (HResult 0x{exception.HResult:X8})"));
        }
    }

    private static void Section(StringBuilder text, string title)
    {
        text.AppendLine();
        text.Append("== ").Append(title).AppendLine(" ==");
    }

    private static void Line(StringBuilder text, string line) => text.Append(Indent).AppendLine(line);

    private static string Time(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string YesNo(ILocalizer localizer, bool value) => localizer[value ? L.Common.Yes : L.Common.No];

    private static string Feature(ILocalizer localizer, OptionalFeatureState state) => state switch
    {
        OptionalFeatureState.Available => localizer[L.Common.Yes],
        OptionalFeatureState.NotAvailable => localizer[L.Common.No],
        OptionalFeatureState.Disabled => localizer[L.ProblemReport.FeatureDisabled],
        _ => localizer[L.ProblemReport.FeatureUnknown],
    };
}
