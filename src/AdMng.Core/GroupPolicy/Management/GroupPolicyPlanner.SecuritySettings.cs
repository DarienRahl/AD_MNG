using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.Security;

namespace AdMng.Core.GroupPolicy.Management;

/// <summary>One file of a GPO as an editor saves it: what it read (the compare-and-swap of the write) and the new content.</summary>
public sealed record GpoFileSave
{
    /// <summary>The content as the editor read it (null: there was none).</summary>
    public byte[]? Original { get; init; }

    /// <summary>The new content.</summary>
    public required byte[] Content { get; init; }

    /// <summary>The content differs from the original.</summary>
    public bool Changed => Original is null || !Original.AsSpan().SequenceEqual(Content);

    /// <summary>Never content.</summary>
    public override string ToString() => $"{Content.Length} B";
}

/// <summary>The security settings of a GPO's computer configuration as the K4 editor saves them; null parts are unchanged.</summary>
public sealed record SecuritySettingsSave
{
    /// <summary><c>Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf</c>.</summary>
    public GpoFileSave? Template { get; init; }

    /// <summary><c>Machine/Microsoft/Windows NT/Audit/audit.csv</c>.</summary>
    public GpoFileSave? Audit { get; init; }

    /// <summary><c>Machine/Preferences/Groups/Groups.xml</c>.</summary>
    public GpoFileSave? Groups { get; init; }

    public bool Changed => Template?.Changed == true || Audit?.Changed == true || Groups?.Changed == true;
}

/// <summary>Security settings (design note 19, K4).</summary>
public sealed partial class GroupPolicyPlanner
{
    /// <summary>
    /// Saves the security settings of a GPO as GPMC's editors do: the computer extension names gain the pair of each written
    /// file (Security + its editor for <c>GptTmpl.inf</c>, Audit Policy Configuration + its editor for <c>audit.csv</c>,
    /// Local Users and Groups + its editor and the preference core entry for <c>Groups.xml</c>), the changed files are
    /// written (compare-and-swap with what the editor read) and the computer version goes up by one. The GroupPolicy module
    /// has no cmdlet for these files: the script names the GPO and says so. A plan without objects when nothing changed.
    /// </summary>
    public async Task<ChangePlan> PlanSecuritySettingsAsync(IDirectorySession session, ISysvolConnection sysvol, Guid gpoId, SecuritySettingsSave save,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sysvol);
        ArgumentNullException.ThrowIfNull(save);
        var gpo = await RequirePolicyAsync(session, gpoId, cancellationToken).ConfigureAwait(false);
        if (!save.Changed)
        {
            return Plan(session, [], $"Security settings of {gpo.DisplayName}");
        }

        var names = gpo.MachineExtensionNames;
        var files = new List<(string Path, GpoFileSave File)>();
        if (save.Template is { Changed: true } template)
        {
            names = GpoExtensionNames.With(names, GpoExtensionNames.Security, GpoExtensionNames.SecurityTool);
            files.Add((SecurityTemplate.RelativePath, template));
        }

        if (save.Audit is { Changed: true } audit)
        {
            names = GpoExtensionNames.With(names, GpoExtensionNames.AuditPolicy, GpoExtensionNames.AuditPolicyTool);
            files.Add((AuditPolicyDocument.RelativePath, audit));
        }

        if (save.Groups is { Changed: true } groups)
        {
            names = GpoExtensionNames.With(names, GpoExtensionNames.PreferencesCore, GpoExtensionNames.PreferencesLocalUsersAndGroupsTool);
            names = GpoExtensionNames.With(names, GpoExtensionNames.PreferencesLocalUsersAndGroups, GpoExtensionNames.PreferencesLocalUsersAndGroupsTool);
            files.Add((GppGroupsDocument.RelativePath, groups));
        }

        var steps = new List<PlannedChange>();
        var attributes = new List<AttributeChange>();
        AddIfChanged(attributes, GroupPolicyAttributes.MachineExtensionNames, gpo.MachineExtensionNames, names);

        // The extensions first: a client that sees them before the new files reads the old ones, never the other way round.
        if (attributes.Count > 0)
        {
            steps.Add(Modify([.. attributes]));
        }

        foreach (var (path, file) in files)
        {
            steps.Add(new PlannedChange
            {
                Action = ChangeAction.WriteGpoFile,
                Sysvol = new SysvolChange { GpoId = gpoId, RelativePath = path, OldContent = file.Original, NewContent = file.Content },
            });
        }

        var issues = new List<ValidationIssue>();
        if ((gpo.Flags & GroupPolicyObjectInfo.ComputerSettingsDisabledFlag) != 0)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, IssueCodes.GpoComputerPartDisabled,
                "The computer configuration of this GPO is disabled: clients apply the change only after it is enabled."));
        }

        var gpt = await GptVersionAsync(sysvol, gpoId, cancellationToken).ConfigureAwait(false);
        var version = VersionChange(gpo.DirectoryVersion, gpt, user: false, computer: true);
        steps.Add(version with { Issues = [.. version.Issues, .. issues] });

        var command = new PowerShellCommand("Get-GPO", [GuidParameter(gpoId), Domain(session), PowerShellParameter.Server])
        {
            Comment = "Security settings (GptTmpl.inf, audit.csv, Groups.xml) have no GroupPolicy cmdlet: this plan writes the files on SYSVOL.",
        };
        return Plan(session, [PolicyObject(gpo, steps, PowerShellEquivalent.Of(command))], $"Security settings of {gpo.DisplayName}");
    }
}
