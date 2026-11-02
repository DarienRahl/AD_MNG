using System.Globalization;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.Model;

namespace AdMng.Core.GroupPolicy.Management;

/// <summary>What happens to one file of the central store.</summary>
public enum CentralStoreFileStatus
{
    /// <summary>The store does not have it: written.</summary>
    New,

    /// <summary>The store has another version: replaced.</summary>
    Changed,

    /// <summary>The same bytes: nothing to do.</summary>
    Unchanged,

    /// <summary>
    /// Another ADMX file of the store declares the same namespace: the Microsoft editor would refuse one of them. Skipped,
    /// unless the old file is replaced (<see cref="CentralStoreUpdateOptions.ReplaceCollidingFiles"/>).
    /// </summary>
    NamespaceCollision,

    /// <summary>The package's file is not a valid ADMX/ADML file: skipped.</summary>
    Unreadable,

    /// <summary>An old file of a namespace collision, deleted with its language files (with the option).</summary>
    Removed,

    /// <summary>A leftover of an interrupted update (<c>*.admng-*.tmp</c>): deleted.</summary>
    Leftover,
}

/// <summary>One file of a central store update.</summary>
/// <param name="RelativePath">The path below the central store (<c>msedge.admx</c>, <c>pl-PL/msedge.adml</c>).</param>
/// <param name="Status">What happens to it.</param>
/// <param name="OldContent">The store's file (null: none).</param>
/// <param name="NewContent">The package's file (null: none or skipped).</param>
public sealed record CentralStoreFileChange(string RelativePath, CentralStoreFileStatus Status, byte[]? OldContent, byte[]? NewContent)
{
    /// <summary>Policies the new ADMX adds (ADMX files only).</summary>
    public int PoliciesAdded { get; init; }

    /// <summary>Policies the new ADMX no longer has (ADMX files only).</summary>
    public int PoliciesRemoved { get; init; }

    /// <summary>The target namespace of an ADMX file.</summary>
    public string? Namespace { get; init; }

    /// <summary>The store file a <see cref="CentralStoreFileStatus.NamespaceCollision"/> collides with.</summary>
    public string? CollidesWith { get; init; }

    /// <summary>Written or deleted by the update.</summary>
    public bool IsWritten => Status is CentralStoreFileStatus.New or CentralStoreFileStatus.Changed;

    /// <summary>Never content.</summary>
    public override string ToString() => $"{RelativePath} {Status}";
}

/// <summary>What the operator chose for a central store update.</summary>
public sealed record CentralStoreUpdateOptions
{
    /// <summary>The languages of the package to copy (culture folders of the package).</summary>
    public required IReadOnlyCollection<string> Cultures { get; init; }

    /// <summary>Delete the old ADMX (and its language files) that declares the namespace of a new one, and write the new one.</summary>
    public bool ReplaceCollidingFiles { get; init; }
}

/// <summary>The files of a central store update and the state of the store it was compared with.</summary>
/// <param name="StoreExists">The central store exists (otherwise the update creates it).</param>
/// <param name="Files">Every file of the package (and the files the update deletes), sorted by path.</param>
/// <param name="StoreFiles">The store's files as read (path → content) – the backup made before the update.</param>
public sealed record CentralStoreComparison(bool StoreExists, IReadOnlyList<CentralStoreFileChange> Files, IReadOnlyDictionary<string, byte[]> StoreFiles)
{
    public int WrittenCount => Files.Count(f => f.IsWritten);

    public bool HasChanges => Files.Any(f => f.IsWritten || f.Status is CentralStoreFileStatus.Removed or CentralStoreFileStatus.Leftover);
}

/// <summary>"Zaktualizuj szablony" (design note 6, K3).</summary>
public sealed partial class GroupPolicyPlanner
{
    /// <summary>Most files of the central store read for the comparison (and the backup).</summary>
    public const int MaxCentralStoreFiles = 20_000;

    /// <summary>
    /// Compares a package with the central store of the session's DC: every ADMX of the package and the ADML of the chosen
    /// cultures (a neutral package culture such as <c>pl</c> goes to the store's <c>pl-PL</c> when that exists) as new,
    /// changed, unchanged, unreadable or colliding with another ADMX of the same namespace (the Microsoft editor refuses
    /// both); the policies a changed ADMX adds or drops; leftovers of an interrupted update. Read-only; the store's files are
    /// kept in memory for the backup.
    /// </summary>
    public static async Task<CentralStoreComparison> CompareCentralStoreAsync(ISysvolConnection sysvol, PolicyTemplatePackage package,
        CentralStoreUpdateOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sysvol);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(options);

        // The store as it is.
        var listing = await sysvol.ListPolicyDefinitionsAsync(MaxCentralStoreFiles, cancellationToken).ConfigureAwait(false);
        var store = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in listing?.Where(e => !e.IsFolder) ?? [])
        {
            var read = await sysvol.ReadPolicyDefinitionsFileAsync(entry.RelativePath, GpoTransaction.MaxFileBytes, cancellationToken).ConfigureAwait(false);
            if (read.Content is { } content)
            {
                store[entry.RelativePath] = content;
            }
        }

        var storeCultures = store.Keys.Where(k => k.Contains('/', StringComparison.Ordinal)).Select(k => k[..k.IndexOf('/', StringComparison.Ordinal)])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var storeNamespaces = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var storeAdmx = new Dictionary<string, AdmxFile?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, content) in store.Where(f => PolicyDefinitionLoader.IsAdmx(f.Key)))
        {
            AdmxFile? parsed = null;
            try
            {
                parsed = AdmxParser.Parse(path, content);
                storeNamespaces.TryAdd(parsed.TargetNamespace, path);
            }
            catch (GpoParseException)
            {
                // An unreadable store file cannot collide; it is replaced like any other when the package has it.
            }

            storeAdmx[path] = parsed;
        }

        var files = new List<CentralStoreFileChange>();
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var admxName in package.AdmxFiles)
        {
            var content = await package.ReadAsync(admxName, PolicyTemplatePackage.MaxEntryBytes, cancellationToken).ConfigureAwait(false);
            AdmxFile? parsed = null;
            try
            {
                parsed = content is null ? null : AdmxParser.Parse(admxName, content);
            }
            catch (GpoParseException)
            {
            }

            if (parsed is null)
            {
                files.Add(new CentralStoreFileChange(admxName, CentralStoreFileStatus.Unreadable, store.GetValueOrDefault(admxName), content));
                continue;
            }

            if (storeNamespaces.TryGetValue(parsed.TargetNamespace, out var owner) && !string.Equals(owner, admxName, StringComparison.OrdinalIgnoreCase))
            {
                if (!options.ReplaceCollidingFiles)
                {
                    files.Add(new CentralStoreFileChange(admxName, CentralStoreFileStatus.NamespaceCollision, store.GetValueOrDefault(admxName), content)
                    {
                        Namespace = parsed.TargetNamespace,
                        CollidesWith = owner,
                    });
                    continue;
                }

                // The old file and its language files go, the new one comes.
                var ownerBase = Path.GetFileNameWithoutExtension(owner);
                foreach (var (path, old) in store.Where(f => string.Equals(f.Key, owner, StringComparison.OrdinalIgnoreCase)
                    || (f.Key.Contains('/', StringComparison.Ordinal) && string.Equals(f.Key[(f.Key.IndexOf('/', StringComparison.Ordinal) + 1)..], ownerBase + ".adml", StringComparison.OrdinalIgnoreCase))))
                {
                    files.Add(new CentralStoreFileChange(path, CentralStoreFileStatus.Removed, old, null) { CollidesWith = admxName });
                }
            }

            var existing = store.GetValueOrDefault(admxName);
            var status = existing is null ? CentralStoreFileStatus.New
                : existing.AsSpan().SequenceEqual(content) ? CentralStoreFileStatus.Unchanged
                : CentralStoreFileStatus.Changed;
            var oldNames = storeAdmx.GetValueOrDefault(admxName)?.Policies.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            var newNames = parsed.Policies.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            files.Add(new CentralStoreFileChange(admxName, status, existing, content)
            {
                Namespace = parsed.TargetNamespace,
                PoliciesAdded = newNames.Count(n => !oldNames.Contains(n)),
                PoliciesRemoved = existing is null ? 0 : oldNames.Count(n => !newNames.Contains(n)),
            });
            written.Add(Path.GetFileNameWithoutExtension(admxName));
        }

        // The language files of the chosen cultures, of the templates that are written.
        foreach (var culture in options.Cultures.Where(c => package.Cultures.Contains(c, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
        {
            var target = TargetCulture(culture, storeCultures);
            foreach (var baseName in written.Order(StringComparer.OrdinalIgnoreCase))
            {
                var content = await package.ReadAsync(culture + "/" + baseName + ".adml", PolicyTemplatePackage.MaxEntryBytes, cancellationToken).ConfigureAwait(false);
                if (content is null)
                {
                    continue;
                }

                var path = target + "/" + baseName + ".adml";
                var valid = true;
                try
                {
                    AdmlParser.Parse(baseName + ".adml", target, content);
                }
                catch (GpoParseException)
                {
                    valid = false;
                }

                var existing = store.GetValueOrDefault(path);
                files.Add(new CentralStoreFileChange(path, !valid ? CentralStoreFileStatus.Unreadable
                    : existing is null ? CentralStoreFileStatus.New
                    : existing.AsSpan().SequenceEqual(content) ? CentralStoreFileStatus.Unchanged
                    : CentralStoreFileStatus.Changed, existing, content));
            }
        }

        // Leftovers of an interrupted update.
        foreach (var (path, content) in store.Where(f => GpoTransaction.IsTempName(f.Key)))
        {
            files.Add(new CentralStoreFileChange(path, CentralStoreFileStatus.Leftover, content, null));
        }

        var unique = files.GroupBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        return new CentralStoreComparison(listing is not null, [.. unique], store);
    }

    /// <summary>
    /// The update as a plan: one object (the <c>CN=Policies</c> container of the domain stands for the central store
    /// <c>Policies\PolicyDefinitions</c> on SYSVOL) with a write per new or changed file, a delete per removed file and
    /// leftover – one transaction, undone from the old contents. Nothing when the comparison has no changes.
    /// </summary>
    public ChangePlan PlanCentralStoreUpdate(IDirectorySession session, CentralStoreComparison comparison, string title)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(comparison);
        var steps = new List<PlannedChange>();
        foreach (var file in comparison.Files)
        {
            if (file.IsWritten)
            {
                steps.Add(new PlannedChange
                {
                    Action = ChangeAction.WritePolicyDefinitionsFile,
                    Sysvol = new SysvolChange { GpoId = Guid.Empty, RelativePath = file.RelativePath, OldContent = file.OldContent, NewContent = file.NewContent },
                });
            }
            else if (file.Status is CentralStoreFileStatus.Removed or CentralStoreFileStatus.Leftover && file.OldContent is not null)
            {
                steps.Add(new PlannedChange
                {
                    Action = ChangeAction.DeletePolicyDefinitionsFile,
                    Sysvol = new SysvolChange { GpoId = Guid.Empty, RelativePath = file.RelativePath, OldContent = file.OldContent },
                    Issues = file.Status == CentralStoreFileStatus.Leftover
                        ? [new ValidationIssue(IssueSeverity.Info, IssueCodes.SysvolLeftovers, "A leftover of an interrupted write is deleted.")]
                        : [],
                });
            }
        }

        if (steps.Count == 0)
        {
            return Plan(session, [], title);
        }

        var policies = "CN=Policies,CN=System," + session.Info.DefaultNamingContext;
        var target = string.Create(CultureInfo.InvariantCulture, $@"\\{session.Info.Server}\SYSVOL\{session.Info.DomainDnsName}\Policies\{SysvolPaths.PolicyDefinitionsFolder}");
        return Plan(session,
        [
            new ObjectPlan
            {
                DistinguishedName = policies,
                DisplayName = target,
                Kind = ObjectKind.Container,
                ObjectClasses = ["top", "container"],
                Changes = steps,
                Issues = comparison.StoreExists
                    ? []
                    : [new ValidationIssue(IssueSeverity.Info, IssueCodes.CentralStoreCreated, "The central store does not exist yet: the update creates it.")],
            },
        ], title);
    }

    /// <summary>The store's folder for a package culture: the same name, else the store's specific culture of a neutral one (<c>pl</c> → <c>pl-PL</c>).</summary>
    public static string TargetCulture(string packageCulture, IReadOnlyCollection<string> storeCultures)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageCulture);
        ArgumentNullException.ThrowIfNull(storeCultures);
        if (storeCultures.FirstOrDefault(c => string.Equals(c, packageCulture, StringComparison.OrdinalIgnoreCase)) is { } same)
        {
            return same;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(packageCulture);
            if (culture.IsNeutralCulture)
            {
                var specific = storeCultures.FirstOrDefault(c => IsChildOf(c, culture.Name)) ?? CultureInfo.CreateSpecificCulture(culture.Name).Name;
                return string.IsNullOrEmpty(specific) ? packageCulture : specific;
            }
        }
        catch (CultureNotFoundException)
        {
        }

        return packageCulture;
    }

    private static bool IsChildOf(string culture, string neutral)
    {
        try
        {
            return string.Equals(CultureInfo.GetCultureInfo(culture).Parent.Name, neutral, StringComparison.OrdinalIgnoreCase);
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }
}

/// <summary>
/// The backup of the central store before an update (K3): its files as read for the comparison, written below a folder
/// the operator chose (an explicit export of vendor templates – not directory data, CLAUDE.md rule 1) as
/// <c>PolicyDefinitions-&lt;DC&gt;-&lt;yyyyMMdd-HHmmss&gt;</c>.
/// </summary>
public static class CentralStoreBackup
{
    /// <summary>Writes the files; returns the folder written.</summary>
    public static async Task<string> WriteAsync(string folder, string server, IReadOnlyDictionary<string, byte[]> files, DateTimeOffset time,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(files);
        var name = SysvolPaths.PolicyDefinitionsFolder + "-" + (SysvolPaths.TryNormalizeHost(server, out var host) ? host : "dc") + "-"
            + time.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = Path.Combine(folder, name);
        System.IO.Directory.CreateDirectory(target);
        foreach (var (path, content) in files)
        {
            if (!SysvolPaths.TryNormalize(path, out var normalized))
            {
                continue;
            }

            var file = Path.Combine([target, .. normalized.Split('/')]);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllBytesAsync(file, content, cancellationToken).ConfigureAwait(false);
        }

        return target;
    }
}
