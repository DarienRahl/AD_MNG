using System.Globalization;
using System.Text.RegularExpressions;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Batch.Planning;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.Backup;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.Security;

namespace AdMng.Core.GroupPolicy.Management;

/// <summary>Backup, restore and import (design note 5.5).</summary>
public sealed partial class GroupPolicyPlanner
{
    /// <summary>
    /// Reads a GPO into a backup in memory (container, files, links, WMI filter): the backup itself is written by
    /// <see cref="GpmcBackupFormat.WriteAsync"/> to the folder the operator chose. Read-only for the directory.
    /// </summary>
    /// <exception cref="GpoTooLargeException">The GPO folder exceeds the in-memory limits.</exception>
    public async Task<GpoBackup> ReadBackupAsync(IDirectorySession session, ISysvolWriteConnection sysvol, Guid gpoId, string comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sysvol);
        var gpo = await RequirePolicyAsync(session, gpoId, cancellationToken).ConfigureAwait(false);
        var content = await GroupPolicyReader.ReadFolderAsync(sysvol, gpoId, cancellationToken).ConfigureAwait(false);
        var gpt = await GptVersionAsync(sysvol, gpoId, cancellationToken).ConfigureAwait(false);
        var links = await _reader.ReadLinksOfAsync(session, gpoId, cancellationToken).ConfigureAwait(false);
        var filters = gpo.WmiFilterId is null ? [] : await _reader.ReadWmiFiltersAsync(session, cancellationToken).ConfigureAwait(false);
        var head = await session.GetAsync(session.Info.DefaultNamingContext, [Attr.ObjectGuid], cancellationToken).ConfigureAwait(false);
        var trustees = SecurityDescriptorSummary.TryParse(gpo.SecurityDescriptor ?? [], out var summary) && summary is not null
            ? summary.Aces.Select(a => a.Sid).Distinct(StringComparer.OrdinalIgnoreCase).Select(sid => new BackupTrustee(sid, null, null)).ToList()
            : [];
        return new GpoBackup
        {
            BackupId = Guid.NewGuid(),
            GpoId = gpoId,
            DisplayName = gpo.DisplayName,
            Domain = session.Info.DomainDnsName,
            DomainController = session.Info.Server,
            DomainGuid = head?.ObjectGuid,
            BackupTime = _time.GetUtcNow(),
            Comment = comment ?? string.Empty,
            Options = gpo.Flags & 3,
            Version = gpt is { } sysvolVersion ? sysvolVersion : gpo.DirectoryVersion ?? GpoVersion.Zero,
            MachineExtensionNames = gpo.MachineExtensionNames,
            UserExtensionNames = gpo.UserExtensionNames,
            WmiFilterLink = gpo.WmiFilterLink,
            SecurityDescriptor = gpo.SecurityDescriptor,
            Trustees = trustees,
            Files = content.Files.Where(f => !string.Equals(f.Key, GptIni.RelativePath, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase),
            Folders = [.. content.Folders],
            Links = [.. links.Select(l => new BackupLink(l.ContainerDn, l.Order, (l.Enforced ? GroupPolicyLink.EnforcedOption : 0) | (l.Enabled ? 0 : GroupPolicyLink.DisabledOption)))],
            WmiFilter = filters.FirstOrDefault(f => string.Equals(f.Id, gpo.WmiFilterId, StringComparison.OrdinalIgnoreCase)),
            HasSidecar = true,
        };
    }

    /// <summary>
    /// Restores the GPO of a backup (<c>Restore-GPO</c>): an existing GPO gets the backup's files (written, deleted), name,
    /// status, extension names, WMI filter link (when the filter exists), explicit ACEs and the folder DACL, with both
    /// version halves raised from the current maximum; a deleted GPO is created again with the backup's GUID and content
    /// (each non-empty half at the backup's version + 1). Links are not part of a GPMC restore: with
    /// <paramref name="restoreLinks"/> the sidecar's links are added where the container exists.
    /// </summary>
    public async Task<ChangePlan> PlanRestoreAsync(IDirectorySession session, ISysvolWriteConnection sysvol, GpoBackup backup, string backupFolder, bool restoreLinks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sysvol);
        ArgumentNullException.ThrowIfNull(backup);
        var powerShell = PowerShellEquivalent.Of(PowerShellCommand.Of("Restore-GPO",
            PowerShellParameter.Literal("BackupId", backup.BackupId.ToString("D")), PowerShellParameter.Literal("Path", backupFolder ?? string.Empty),
            Domain(session), PowerShellParameter.Server));
        var issues = new List<ValidationIssue>();
        if (!string.Equals(backup.Domain, session.Info.DomainDnsName, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, IssueCodes.NotApplicable,
                "The backup comes from another domain: restore it there, or import its settings into a GPO of this domain."));
        }

        AddGppPasswordWarning(issues, backup.Files);
        var wmiLink = await ExistingWmiLinkAsync(session, backup.WmiFilterLink, issues, cancellationToken).ConfigureAwait(false);
        var existing = await _reader.ReadPolicyAsync(session, backup.GpoId, cancellationToken).ConfigureAwait(false);
        var objects = new List<ObjectPlan>();
        if (existing is null)
        {
            var nameIssues = (await ValidateNameAsync(session, backup.DisplayName, exceptId: backup.GpoId, cancellationToken).ConfigureAwait(false))
                .Select(i => i.Code == IssueCodes.Duplicate ? i with { Severity = IssueSeverity.Warning } : i);
            var files = backup.Files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).ToList();
            var version = new GpoVersion(
                files.Any(f => IsBelow(f.Key, "User")) || backup.Version.User > 0 ? Math.Min(backup.Version.User + 1, GpoVersion.MaxHalf) : 0,
                files.Any(f => IsBelow(f.Key, "Machine")) || backup.Version.Computer > 0 ? Math.Min(backup.Version.Computer + 1, GpoVersion.MaxHalf) : 0);
            objects.AddRange(NewPolicyObjects(session, backup.GpoId, backup.DisplayName, [.. issues, .. nameIssues], powerShell,
                extra: create => create with
                {
                    Attributes =
                    [
                        .. create.Attributes.Where(a => !string.Equals(a.Attribute, GroupPolicyAttributes.Flags, StringComparison.OrdinalIgnoreCase)),
                        New(GroupPolicyAttributes.Flags, backup.Options.ToString(CultureInfo.InvariantCulture)),
                        .. NewIfSet(GroupPolicyAttributes.MachineExtensionNames, backup.MachineExtensionNames),
                        .. NewIfSet(GroupPolicyAttributes.UserExtensionNames, backup.UserExtensionNames),
                        .. NewIfSet(GroupPolicyAttributes.WqlFilter, wmiLink),
                    ],
                },
                content:
                [
                    .. files.Select(f => new PlannedChange
                    {
                        Action = ChangeAction.WriteGpoFile,
                        Sysvol = new SysvolChange { GpoId = backup.GpoId, RelativePath = f.Key, NewContent = f.Value },
                    }),
                    .. version == GpoVersion.Zero
                        ? Array.Empty<PlannedChange>()
                        : [new PlannedChange { Action = ChangeAction.SetGpoVersion, GpoVersion = new GpoVersionChange(GpoVersion.Zero, GpoVersion.Zero, version) }],
                ],
                security: CopiedAces(backup.SecurityDescriptor)));
        }
        else
        {
            var content = await GroupPolicyReader.ReadFolderAsync(sysvol, backup.GpoId, cancellationToken).ConfigureAwait(false);
            var steps = await ContentStepsAsync(sysvol, existing, content, backup.Files, cancellationToken).ConfigureAwait(false);
            var gpt = await GptVersionAsync(sysvol, backup.GpoId, cancellationToken).ConfigureAwait(false);
            steps.Add(VersionChange(existing.DirectoryVersion, gpt, user: true, computer: true));
            var attributes = new List<AttributeChange>();
            AddIfChanged(attributes, Attr.DisplayName, existing.DisplayName == SysvolPaths.FolderName(existing.Id) ? null : existing.DisplayName, backup.DisplayName);
            AddIfChanged(attributes, GroupPolicyAttributes.Flags, existing.Flags.ToString(CultureInfo.InvariantCulture),
                ((existing.Flags & ~3) | backup.Options).ToString(CultureInfo.InvariantCulture));
            AddIfChanged(attributes, GroupPolicyAttributes.MachineExtensionNames, existing.MachineExtensionNames, backup.MachineExtensionNames);
            AddIfChanged(attributes, GroupPolicyAttributes.UserExtensionNames, existing.UserExtensionNames, backup.UserExtensionNames);
            if (backup.WmiFilterLink is null || wmiLink is not null)
            {
                AddIfChanged(attributes, GroupPolicyAttributes.WqlFilter, existing.WmiFilterLink, wmiLink);
            }

            steps.Add(Modify([.. attributes]) with { Issues = issues });
            if (BackupAclDelta(existing.SecurityDescriptor, backup.SecurityDescriptor) is { } delta
                && await AclPlanning.PlanAsync(session, existing.Dn, delta, cancellationToken).ConfigureAwait(false) is { } security)
            {
                steps.Add(security);
            }

            steps.Add(new PlannedChange
            {
                Action = ChangeAction.SyncGpoFolderSecurity,
                Sysvol = new SysvolChange { GpoId = backup.GpoId, OldFolderSecurity = await sysvol.GetGpoFolderSecurityAsync(backup.GpoId, cancellationToken).ConfigureAwait(false) },
            });
            objects.Add(PolicyObject(existing, steps, powerShell));
        }

        if (restoreLinks)
        {
            foreach (var link in backup.Links.OrderBy(l => l.ContainerDn, StringComparer.OrdinalIgnoreCase))
            {
                var container = await _reader.ReadContainerAsync(session, link.ContainerDn, cancellationToken).ConfigureAwait(false);
                if (container is null || container.Links.Contains(backup.GpoId))
                {
                    continue;
                }

                var after = container.Links.Insert(backup.GpoId, link.Options, link.Order);
                objects.Add(ContainerObject(container, [Modify(GpLinkChange(container, after))], PowerShellEquivalent.Of(PowerShellCommand.Of("New-GPLink",
                    GuidParameter(backup.GpoId), PowerShellParameter.Literal("Target", container.Dn),
                    PowerShellParameter.Literal("LinkEnabled", (link.Options & GroupPolicyLink.DisabledOption) == 0 ? "Yes" : "No"),
                    PowerShellParameter.Literal("Enforced", (link.Options & GroupPolicyLink.EnforcedOption) != 0 ? "Yes" : "No"),
                    PowerShellParameter.Number("Order", link.Order), Domain(session), PowerShellParameter.Server))));
            }
        }

        return Plan(session, objects, $"Restore GPO {backup.DisplayName}");
    }

    /// <summary>
    /// Imports the settings of a backup into an existing GPO (<c>Import-GPO</c>): its files, extension names and the version
    /// raise only - no name, status, WMI link or permissions. Principals and UNC paths of another domain in the settings are
    /// reported (<see cref="IssueCodes.GpoForeignReferences"/>; migration tables are not supported).
    /// </summary>
    public async Task<ChangePlan> PlanImportAsync(IDirectorySession session, ISysvolWriteConnection sysvol, Guid targetId, GpoBackup backup, string backupFolder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sysvol);
        ArgumentNullException.ThrowIfNull(backup);
        var target = await RequirePolicyAsync(session, targetId, cancellationToken).ConfigureAwait(false);
        var content = await GroupPolicyReader.ReadFolderAsync(sysvol, targetId, cancellationToken).ConfigureAwait(false);
        var files = backup.Files;
        var steps = await ContentStepsAsync(sysvol, target, content, files, cancellationToken).ConfigureAwait(false);
        var gpt = await GptVersionAsync(sysvol, targetId, cancellationToken).ConfigureAwait(false);
        steps.Add(VersionChange(target.DirectoryVersion, gpt, user: true, computer: true));
        var attributes = new List<AttributeChange>();
        AddIfChanged(attributes, GroupPolicyAttributes.MachineExtensionNames, target.MachineExtensionNames, backup.MachineExtensionNames);
        AddIfChanged(attributes, GroupPolicyAttributes.UserExtensionNames, target.UserExtensionNames, backup.UserExtensionNames);
        var issues = new List<ValidationIssue>();
        var domainSid = (await session.GetAsync(session.Info.DefaultNamingContext, [Attr.ObjectSid], cancellationToken).ConfigureAwait(false))?.ObjectSid;
        if (ForeignReferences(files, domainSid, session.Info.DomainDnsName) is { Count: > 0 } foreign)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, IssueCodes.GpoForeignReferences,
                string.Create(CultureInfo.InvariantCulture, $"{foreign.Count} references to principals or paths of another domain: {string.Join(", ", foreign.Take(5))}")));
        }

        AddGppPasswordWarning(issues, files);
        steps.Add(Modify([.. attributes]) with { Issues = issues });
        return Plan(session, [PolicyObject(target, steps, PowerShellEquivalent.Of(PowerShellCommand.Of("Import-GPO",
            PowerShellParameter.Literal("BackupId", backup.BackupId.ToString("D")), PowerShellParameter.Literal("Path", backupFolder ?? string.Empty),
            PowerShellParameter.Literal("TargetGuid", targetId.ToString("D")), Domain(session), PowerShellParameter.Server)))], $"Import settings into {target.DisplayName}");
    }

    /// <summary>
    /// SIDs of another domain (<c>S-1-5-21-a-b-c-…</c> whose domain part is not this domain's) and UNC paths naming another
    /// host than the domain, in the security template, the preferences and the scripts settings.
    /// </summary>
    public static IReadOnlyList<string> ForeignReferences(IReadOnlyDictionary<string, byte[]> files, string? domainSid, string domainDnsName)
    {
        ArgumentNullException.ThrowIfNull(files);
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, content) in files)
        {
            var role = GpoFileClassifier.Classify(path).Role;
            if (role is not (GpoFileRole.SecurityTemplate or GpoFileRole.Preferences or GpoFileRole.ScriptsIni or GpoFileRole.PsScriptsIni))
            {
                continue;
            }

            string text;
            try
            {
                text = role == GpoFileRole.Preferences ? GpoText.DecodeAnsiOrUtf8(content, allowAnsi: false) : GpoText.DecodeUnicodeText(content);
            }
            catch (GpoParseException)
            {
                continue;
            }

            foreach (Match match in DomainSidPattern().Matches(text))
            {
                var sid = match.Value;
                if (domainSid is null || !string.Equals(sid, domainSid, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(sid);
                }
            }

            foreach (Match match in UncPattern().Matches(text))
            {
                var host = match.Groups[1].Value;
                if (!host.Equals(domainDnsName, StringComparison.OrdinalIgnoreCase)
                    && !host.EndsWith("." + domainDnsName, StringComparison.OrdinalIgnoreCase)
                    && !host.Equals(domainDnsName.Split('.')[0], StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(@"\\" + host);
                }
            }
        }

        return [.. found];
    }

    /// <summary>
    /// The preference files (Groups.xml, ScheduledTasks.xml, Services.xml, DataSources.xml, Drives.xml …) that hold a non-empty
    /// <c>cpassword</c> attribute - a GPP password encrypted with the key Microsoft published (MS14-025). Only the paths are
    /// returned, never a value.
    /// </summary>
    public static IReadOnlyList<string> GppPasswordFiles(IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var found = new List<string>();
        foreach (var (path, content) in files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (GpoFileClassifier.Classify(path).Role != GpoFileRole.Preferences)
            {
                continue;
            }

            string text;
            try
            {
                text = GpoText.DecodeAnsiOrUtf8(content, allowAnsi: false);
            }
            catch (GpoParseException)
            {
                continue;
            }

            if (GppPasswordPattern().IsMatch(text))
            {
                found.Add(path);
            }
        }

        return found;
    }

    private static void AddGppPasswordWarning(List<ValidationIssue> issues, IReadOnlyDictionary<string, byte[]> files)
    {
        if (GppPasswordFiles(files) is { Count: > 0 } withPasswords)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, IssueCodes.GpoGppPassword,
                $"GPP passwords (cpassword, MS14-025) in: {string.Join(", ", withPasswords.Take(5))}"));
        }
    }

    /// <summary>Write and delete steps that make the GPO folder equal <paramref name="wanted"/> (GPT.INI excluded; leftovers deleted).</summary>
    private static async Task<List<PlannedChange>> ContentStepsAsync(ISysvolWriteConnection sysvol, GpoInfo gpo, GpoFolderContent current,
        IReadOnlyDictionary<string, byte[]> wanted, CancellationToken cancellationToken)
    {
        var steps = new List<PlannedChange>();
        foreach (var (path, content) in wanted.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(path, GptIni.RelativePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var existing = current.Files.FirstOrDefault(f => string.Equals(f.Key, path, StringComparison.OrdinalIgnoreCase));
            if (existing.Value is { } old && old.AsSpan().SequenceEqual(content))
            {
                continue;
            }

            steps.Add(new PlannedChange
            {
                Action = ChangeAction.WriteGpoFile,
                Sysvol = new SysvolChange { GpoId = gpo.Id, RelativePath = existing.Key ?? path, OldContent = existing.Value, NewContent = content },
            });
        }

        foreach (var (path, content) in current.Files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.Equals(path, GptIni.RelativePath, StringComparison.OrdinalIgnoreCase) && !wanted.ContainsKey(path)
                && !wanted.Keys.Any(k => string.Equals(k, path, StringComparison.OrdinalIgnoreCase)))
            {
                steps.Add(new PlannedChange { Action = ChangeAction.DeleteGpoFile, Sysvol = new SysvolChange { GpoId = gpo.Id, RelativePath = path, OldContent = content } });
            }
        }

        steps.AddRange(await LeftoverStepsAsync(sysvol, gpo.Id, current, cancellationToken).ConfigureAwait(false));
        return steps;
    }

    /// <summary>Deletes of the leftovers of interrupted writes (<c>*.admng-*.tmp</c>), with a <see cref="IssueCodes.SysvolLeftovers"/> note.</summary>
    private static async Task<IReadOnlyList<PlannedChange>> LeftoverStepsAsync(ISysvolWriteConnection sysvol, Guid gpoId, GpoFolderContent current, CancellationToken cancellationToken)
    {
        var steps = new List<PlannedChange>();
        foreach (var path in current.Leftovers)
        {
            var read = await sysvol.ReadGpoFileAsync(gpoId, path, GpoTransaction.MaxFileBytes, cancellationToken).ConfigureAwait(false);
            if (read.Content is { } content)
            {
                steps.Add(new PlannedChange
                {
                    Action = ChangeAction.DeleteGpoFile,
                    Sysvol = new SysvolChange { GpoId = gpoId, RelativePath = path, OldContent = content },
                    Issues = [new ValidationIssue(IssueSeverity.Info, IssueCodes.SysvolLeftovers, "A leftover of an interrupted write is deleted.")],
                });
            }
        }

        return steps;
    }

    /// <summary>The WMI link of a backup when its filter exists in this domain; otherwise a warning and null.</summary>
    private async Task<string?> ExistingWmiLinkAsync(IDirectorySession session, string? link, List<ValidationIssue> issues, CancellationToken cancellationToken)
    {
        if (WmiFilterLinks.ParseId(link) is not { } id)
        {
            return null;
        }

        var filters = await _reader.ReadWmiFiltersAsync(session, cancellationToken).ConfigureAwait(false);
        if (filters.Any(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            return WmiFilterLinks.Format(session.Info.DomainDnsName, id);
        }

        issues.Add(new ValidationIssue(IssueSeverity.Warning, IssueCodes.NotFound, "The WMI filter of the backup does not exist in this domain; the GPO is restored without it.",
            GroupPolicyAttributes.WqlFilter));
        return null;
    }

    /// <summary>The ACE delta that makes the explicit ACEs of <paramref name="current"/> equal those of the backup.</summary>
    private static AclChange? BackupAclDelta(byte[]? current, byte[]? backup)
    {
        if (current is null || backup is null || !DaclEditor.TryReadExplicit(current, out var now) || !DaclEditor.TryReadExplicit(backup, out var wanted))
        {
            return null;
        }

        var added = wanted.Where(w => AceSpec.IsValidSid(w.TrusteeSid) && w.Mask != 0 && !now.Any(n => n.SameAce(w))).ToList();
        var removed = now.Where(n => !wanted.Any(w => w.SameAce(n))).ToList();
        return added.Count == 0 && removed.Count == 0 ? null : new AclChange(added, removed);
    }

    private static void AddIfChanged(List<AttributeChange> attributes, string attribute, string? oldValue, string? newValue)
    {
        var old = string.IsNullOrEmpty(oldValue) ? null : oldValue;
        var next = string.IsNullOrEmpty(newValue) ? null : newValue;
        if (!string.Equals(old, next, StringComparison.Ordinal))
        {
            attributes.Add(Replace(attribute, old, next));
        }
    }

    [GeneratedRegex(@"S-1-5-21-\d+-\d+-\d+(?=-\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DomainSidPattern();

    [GeneratedRegex(@"\\\\([A-Za-z0-9][A-Za-z0-9.\-]{0,252})\\", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UncPattern();

    /// <summary>A <c>cpassword</c> attribute with a value (GPMC writes <c>cpassword=""</c> once the password was removed).</summary>
    [GeneratedRegex(@"\bcpassword\s*=\s*(""[^""]+""|'[^']+')", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GppPasswordPattern();
}
