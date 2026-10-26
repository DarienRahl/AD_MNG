using System.Reflection;

namespace AdMng.Presentation.Shell;

/// <summary>
/// The version of AD Manager for the status bar corner and the About box: <c>VersionPrefix</c> of
/// <c>Directory.Build.props</c> and the commit the build came from (the informational version is "0.4.0+&lt;sha&gt;").
/// </summary>
public static class AppVersion
{
    private const int ShortCommitLength = 7;

    private static readonly (string Version, string? Commit) Current =
        Parse(typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            typeof(AppVersion).Assembly.GetName().Version);

    /// <summary>"0.4.0".</summary>
    public static string Version => Current.Version;

    /// <summary>The short commit id ("30d210e"), or null for a build without one.</summary>
    public static string? Commit => Current.Commit;

    /// <summary>"0.4.0 (30d210e)", or just the version without a commit.</summary>
    public static string Display => Commit is null ? Version : $"{Version} ({Commit})";

    /// <summary>Splits an informational version "0.4.0+30d210ef8de9…" into the version and a short commit id.</summary>
    public static (string Version, string? Commit) Parse(string? informationalVersion, Version? assemblyVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return (assemblyVersion is null ? "0.0.0" : assemblyVersion.ToString(3), null);
        }

        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return (informationalVersion.Trim(), null);
        }

        var commit = informationalVersion[(plus + 1)..].Trim();
        return (informationalVersion[..plus].Trim(),
            commit.Length == 0 ? null : commit[..Math.Min(ShortCommitLength, commit.Length)]);
    }
}
