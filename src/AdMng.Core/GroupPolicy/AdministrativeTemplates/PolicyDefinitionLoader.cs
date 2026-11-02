using System.Globalization;

namespace AdMng.Core.GroupPolicy.AdministrativeTemplates;

/// <summary>A file of a policy definition store: <c>name.admx</c> or <c>culture/name.adml</c>.</summary>
/// <param name="RelativePath">'/'-separated path below the store's root.</param>
/// <param name="Length">Size in bytes.</param>
public sealed record PolicyDefinitionFile(string RelativePath, long Length);

/// <summary>
/// The files of a policy definition store: the central store (<c>SYSVOL\&lt;domain&gt;\Policies\PolicyDefinitions</c> of the
/// session's DC), the local store of the operator's computer (<c>%WINDIR%\PolicyDefinitions</c>), or the folder of a vendor
/// package. Read-only.
/// </summary>
public interface IPolicyDefinitionSource
{
    /// <summary>"Magazyn centralny (dc1.contoso.test)", "Ten komputer" … (for the editor's header).</summary>
    string Description { get; }

    /// <summary>The ADMX files of the root and the ADML files of its culture folders; null when the store does not exist.</summary>
    Task<IReadOnlyList<PolicyDefinitionFile>?> ListAsync(CancellationToken cancellationToken);

    /// <summary>One file into memory; null when it is missing, unreadable or larger than <paramref name="maxBytes"/>.</summary>
    Task<byte[]?> ReadAsync(string relativePath, long maxBytes, CancellationToken cancellationToken);
}

/// <summary>
/// Reads a store into a <see cref="PolicyDefinitionSet"/>: every <c>*.admx</c> of the root, each with the ADML of the first
/// requested culture that has it, then the file's fallback culture, then <c>en-US</c>, then any culture. Files are parsed
/// one by one (a broken file is an issue, never fatal) within <see cref="MaxFiles"/> and <see cref="MaxFileBytes"/>.
/// </summary>
public static class PolicyDefinitionLoader
{
    /// <summary>Most ADMX files read from one store.</summary>
    public const int MaxFiles = 4_096;

    /// <summary>Largest ADMX or ADML file read (the largest Windows ADML is about 2 MB).</summary>
    public const long MaxFileBytes = 32L * 1024 * 1024;

    /// <summary>The culture tried when neither the requested ones nor the file's fallback have its ADML.</summary>
    public const string DefaultCulture = "en-US";

    /// <summary>The cultures to try for a UI culture: itself, then en-US ("pl-PL" → pl-PL, en-US).</summary>
    public static IReadOnlyList<string> CulturesFor(CultureInfo uiCulture)
    {
        ArgumentNullException.ThrowIfNull(uiCulture);
        var result = new List<string>();
        if (!string.IsNullOrEmpty(uiCulture.Name))
        {
            result.Add(uiCulture.Name);
        }

        if (!result.Contains(DefaultCulture, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(DefaultCulture);
        }

        return result;
    }

    /// <summary>Reads and parses the store; <see cref="PolicyDefinitionSet.Empty"/> with no files when it does not exist.</summary>
    /// <returns>The set and whether the store exists.</returns>
    public static async Task<(PolicyDefinitionSet Set, bool Exists)> LoadAsync(IPolicyDefinitionSource source, IReadOnlyList<string> cultures,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(cultures);
        var listing = await source.ListAsync(cancellationToken).ConfigureAwait(false);
        if (listing is null)
        {
            return (PolicyDefinitionSet.Empty, false);
        }

        var issues = new List<PolicyDefinitionIssue>();
        var admxFiles = listing.Where(f => IsAdmx(f.RelativePath)).OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        if (admxFiles.Count > MaxFiles)
        {
            issues.Add(new PolicyDefinitionIssue(PolicyDefinitionIssueKind.TooManyFiles, string.Empty, admxFiles.Count.ToString(CultureInfo.InvariantCulture)));
            admxFiles = admxFiles.Take(MaxFiles).ToList();
        }

        // culture folder (as listed) by name, and the ADML files of each.
        var admlByCulture = listing
            .Select(f => (File: f, Parts: f.RelativePath.Split('/')))
            .Where(f => f.Parts.Length == 2 && f.Parts[1].EndsWith(".adml", StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => f.Parts[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToDictionary(f => f.Parts[1], f => f.File, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        var parsed = new List<(AdmxFile, AdmlFile?)>();
        foreach (var file in admxFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AdmxFile admx;
            try
            {
                admx = await ReadAdmxAsync(source, file, cancellationToken).ConfigureAwait(false)
                    ?? throw new GpoParseException("The file could not be read.");
            }
            catch (GpoParseException ex)
            {
                issues.Add(new PolicyDefinitionIssue(PolicyDefinitionIssueKind.FileUnreadable, file.RelativePath, ex.Message));
                continue;
            }

            var admlName = Path.ChangeExtension(file.RelativePath, ".adml");
            var tried = new List<string>(cultures);
            if (admx.FallbackCulture is { Length: > 0 } fallback)
            {
                tried.Add(fallback);
            }

            tried.Add(DefaultCulture);
            tried.AddRange(admlByCulture.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
            AdmlFile? adml = null;
            var found = false;
            foreach (var culture in tried.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!admlByCulture.TryGetValue(culture, out var inCulture) || !inCulture.TryGetValue(admlName, out var admlFile))
                {
                    continue;
                }

                found = true;
                try
                {
                    var content = await source.ReadAsync(admlFile.RelativePath, MaxFileBytes, cancellationToken).ConfigureAwait(false)
                        ?? throw new GpoParseException("The file could not be read.");
                    adml = AdmlParser.Parse(admlName, culture, content);
                    if (IsOlder(adml.Revision, admx.MinRequiredRevision))
                    {
                        issues.Add(new PolicyDefinitionIssue(PolicyDefinitionIssueKind.LanguageOutdated, admlFile.RelativePath, admx.MinRequiredRevision));
                    }

                    break;
                }
                catch (GpoParseException ex)
                {
                    issues.Add(new PolicyDefinitionIssue(PolicyDefinitionIssueKind.LanguageUnreadable, admlFile.RelativePath, ex.Message));
                }
            }

            if (!found)
            {
                issues.Add(new PolicyDefinitionIssue(PolicyDefinitionIssueKind.LanguageMissing, file.RelativePath));
            }

            parsed.Add((admx, adml));
        }

        var textCulture = cultures.Count > 0 && TryCulture(cultures[0]) is { } first ? first : CultureInfo.InvariantCulture;
        return (PolicyDefinitionSet.Build(parsed, issues, textCulture), true);
    }

    /// <summary>A root file <c>name.admx</c> (no folder).</summary>
    public static bool IsAdmx(string relativePath) =>
        !relativePath.Contains('/', StringComparison.Ordinal) && relativePath.EndsWith(".admx", StringComparison.OrdinalIgnoreCase);

    private static async Task<AdmxFile?> ReadAdmxAsync(IPolicyDefinitionSource source, PolicyDefinitionFile file, CancellationToken cancellationToken)
    {
        if (file.Length > MaxFileBytes)
        {
            throw new GpoParseException("The file is too large.");
        }

        var content = await source.ReadAsync(file.RelativePath, MaxFileBytes, cancellationToken).ConfigureAwait(false);
        return content is null ? null : AdmxParser.Parse(file.RelativePath, content);
    }

    /// <summary>Revision <paramref name="revision"/> is below <paramref name="required"/> ("1.0" &lt; "1.2"); unknown texts are never older.</summary>
    internal static bool IsOlder(string? revision, string? required) =>
        Version.TryParse(Normalize(revision), out var have) && Version.TryParse(Normalize(required), out var need) && have < need;

    private static string? Normalize(string? revision) =>
        revision is null ? null : revision.Trim().Contains('.', StringComparison.Ordinal) ? revision.Trim() : revision.Trim() + ".0";

    private static CultureInfo? TryCulture(string name)
    {
        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }
}

/// <summary>A store held in memory (a vendor package being imported, tests).</summary>
public sealed class InMemoryPolicyDefinitionSource : IPolicyDefinitionSource
{
    private readonly Dictionary<string, byte[]> _files;

    /// <param name="description">The store's description.</param>
    /// <param name="files">'/'-separated relative path → content.</param>
    public InMemoryPolicyDefinitionSource(string description, IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        Description = description ?? string.Empty;
        _files = new Dictionary<string, byte[]>(files, StringComparer.OrdinalIgnoreCase);
    }

    public string Description { get; }

    public IReadOnlyDictionary<string, byte[]> Files => _files;

    public Task<IReadOnlyList<PolicyDefinitionFile>?> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PolicyDefinitionFile>?>([.. _files.Select(f => new PolicyDefinitionFile(f.Key, f.Value.LongLength))]);

    public Task<byte[]?> ReadAsync(string relativePath, long maxBytes, CancellationToken cancellationToken) =>
        Task.FromResult(_files.TryGetValue(relativePath, out var content) && content.LongLength <= maxBytes ? content : null);
}

/// <summary>
/// A store in a folder of the local file system: the operator's <c>%WINDIR%\PolicyDefinitions</c> (the Microsoft editor's
/// fallback without a central store) or a folder the operator chose. Read-only; nothing is written.
/// </summary>
public sealed class FolderPolicyDefinitionSource(string description, string folder) : IPolicyDefinitionSource
{
    public string Description { get; } = description ?? string.Empty;

    public string Folder { get; } = folder ?? throw new ArgumentNullException(nameof(folder));

    public Task<IReadOnlyList<PolicyDefinitionFile>?> ListAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<PolicyDefinitionFile>?>(() =>
    {
        var root = new DirectoryInfo(Folder);
        if (!root.Exists)
        {
            return null;
        }

        var files = new List<PolicyDefinitionFile>();
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, ReturnSpecialDirectories = false };
        foreach (var file in root.EnumerateFiles("*.admx", options))
        {
            files.Add(new PolicyDefinitionFile(file.Name, file.Length));
        }

        foreach (var culture in root.EnumerateDirectories("*", options))
        {
            foreach (var file in culture.EnumerateFiles("*.adml", options))
            {
                files.Add(new PolicyDefinitionFile(culture.Name + "/" + file.Name, file.Length));
            }
        }

        return files;
    }, cancellationToken);

    public async Task<byte[]?> ReadAsync(string relativePath, long maxBytes, CancellationToken cancellationToken)
    {
        if (!SysvolPaths.TryNormalize(relativePath, out var normalized))
        {
            return null;
        }

        var path = Path.Combine([Folder, .. normalized.Split('/')]);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > maxBytes)
            {
                return null;
            }

            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
