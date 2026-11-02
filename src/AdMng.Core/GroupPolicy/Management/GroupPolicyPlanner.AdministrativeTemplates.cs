using System.Buffers.Binary;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;

namespace AdMng.Core.GroupPolicy.Management;

/// <summary>One half (computer or user) of a GPO's administrative templates as the editor saves it (K3).</summary>
public sealed record RegistryPolicySave
{
    /// <summary>The Registry.pol content as the editor read it (null: there was none) – the compare-and-swap of the write.</summary>
    public byte[]? Original { get; init; }

    /// <summary>The new Registry.pol content.</summary>
    public required byte[] Content { get; init; }

    /// <summary>The client-side extensions the configured policies name (ADMX <c>clientExtension</c>) besides the Registry one.</summary>
    public IReadOnlyCollection<Guid> ClientExtensions { get; init; } = [];

    /// <summary>The content differs from the original (a missing or empty file and one without entries are the same).</summary>
    public bool Changed => Original is null || Original.Length == 0
        ? RegistryPolFile.Read(Content).Count > 0
        : !Original.AsSpan().SequenceEqual(Content);

    /// <summary>Never content.</summary>
    public override string ToString() => $"Registry.pol ({Content.Length} B)";
}

/// <summary>Administrative templates (design note 6, K3).</summary>
public sealed partial class GroupPolicyPlanner
{
    /// <summary>The computer configuration's Registry.pol below the GPO folder.</summary>
    public const string MachineRegistryPolicyPath = "Machine/Registry.pol";

    /// <summary>The user configuration's Registry.pol below the GPO folder.</summary>
    public const string UserRegistryPolicyPath = "User/Registry.pol";

    /// <summary>
    /// Saves the administrative templates of a GPO as GPMC's editor does: the extension names gain the Registry extension
    /// with the administrative templates tool of the half (and the extensions the configured policies name), the changed
    /// Registry.pol files are written (compare-and-swap with what the editor read) and the version of each changed half goes
    /// up by one. PowerShell: <c>Set-GPRegistryValue</c> / <c>Remove-GPRegistryValue</c> per changed value. A plan without
    /// objects when nothing changed.
    /// </summary>
    public async Task<ChangePlan> PlanAdministrativeTemplatesAsync(IDirectorySession session, ISysvolConnection sysvol, Guid gpoId,
        RegistryPolicySave? machine, RegistryPolicySave? user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sysvol);
        var gpo = await RequirePolicyAsync(session, gpoId, cancellationToken).ConfigureAwait(false);
        var machineChanged = machine?.Changed == true;
        var userChanged = user?.Changed == true;
        if (!machineChanged && !userChanged)
        {
            return Plan(session, [], $"Administrative templates of {gpo.DisplayName}");
        }

        var steps = new List<PlannedChange>();
        var attributes = new List<AttributeChange>();
        if (machineChanged)
        {
            AddIfChanged(attributes, GroupPolicyAttributes.MachineExtensionNames, gpo.MachineExtensionNames,
                ExtensionNames(gpo.MachineExtensionNames, machine!, GpoExtensionNames.AdministrativeTemplatesMachineTool));
        }

        if (userChanged)
        {
            AddIfChanged(attributes, GroupPolicyAttributes.UserExtensionNames, gpo.UserExtensionNames,
                ExtensionNames(gpo.UserExtensionNames, user!, GpoExtensionNames.AdministrativeTemplatesUserTool));
        }

        // The extensions first: a client that sees them before the new files reads the old ones, never the other way round.
        if (attributes.Count > 0)
        {
            steps.Add(Modify([.. attributes]));
        }

        var issues = new List<ValidationIssue>();
        if (machineChanged)
        {
            steps.Add(new PlannedChange
            {
                Action = ChangeAction.WriteGpoFile,
                Sysvol = new SysvolChange { GpoId = gpoId, RelativePath = MachineRegistryPolicyPath, OldContent = machine!.Original, NewContent = machine.Content },
            });
            if ((gpo.Flags & GroupPolicyObjectInfo.ComputerSettingsDisabledFlag) != 0)
            {
                issues.Add(new ValidationIssue(IssueSeverity.Warning, IssueCodes.GpoComputerPartDisabled,
                    "The computer configuration of this GPO is disabled: clients apply the change only after it is enabled."));
            }
        }

        if (userChanged)
        {
            steps.Add(new PlannedChange
            {
                Action = ChangeAction.WriteGpoFile,
                Sysvol = new SysvolChange { GpoId = gpoId, RelativePath = UserRegistryPolicyPath, OldContent = user!.Original, NewContent = user.Content },
            });
            if ((gpo.Flags & UserSettingsDisabledFlag) != 0)
            {
                issues.Add(new ValidationIssue(IssueSeverity.Warning, IssueCodes.GpoUserPartDisabled,
                    "The user configuration of this GPO is disabled: clients apply the change only after it is enabled."));
            }
        }

        var gpt = await GptVersionAsync(sysvol, gpoId, cancellationToken).ConfigureAwait(false);
        var version = VersionChange(gpo.DirectoryVersion, gpt, user: userChanged, computer: machineChanged);
        steps.Add(version with { Issues = [.. version.Issues, .. issues] });

        var commands = new List<PowerShellCommand>();
        if (machineChanged)
        {
            commands.AddRange(RegistryCommands(session, gpoId, "HKLM", machine!));
        }

        if (userChanged)
        {
            commands.AddRange(RegistryCommands(session, gpoId, "HKCU", user!));
        }

        return Plan(session, [PolicyObject(gpo, steps, new PowerShellEquivalent(commands))], $"Administrative templates of {gpo.DisplayName}");
    }

    /// <summary><c>flags</c> bit: the user configuration of the GPO is disabled.</summary>
    private const int UserSettingsDisabledFlag = 1;

    private static string ExtensionNames(string? current, RegistryPolicySave save, Guid tool)
    {
        var value = GpoExtensionNames.With(current, GpoExtensionNames.Registry, tool);
        foreach (var extension in save.ClientExtensions.Where(e => e != Guid.Empty && e != GpoExtensionNames.Registry).Distinct().OrderBy(e => e))
        {
            value = GpoExtensionNames.With(value, extension, tool);
        }

        return value;
    }

    /// <summary>
    /// <c>Set-GPRegistryValue</c> for every value the new file sets or deletes differently, <c>Remove-GPRegistryValue</c> for
    /// every value it no longer names (a whole key when nothing of it is left).
    /// </summary>
    private static List<PowerShellCommand> RegistryCommands(IDirectorySession session, Guid gpoId, string hive, RegistryPolicySave save)
    {
        static string Id(RegistryPolEntry e) => RegistryPolicyDocument.NormalizeKey(e.Key).ToUpperInvariant() + "\n" + e.ValueName.ToUpperInvariant();
        var old = (save.Original is { Length: > 0 } original ? RegistryPolFile.Read(original) : []).GroupBy(Id).ToDictionary(g => g.Key, g => g.Last());
        var next = RegistryPolFile.Read(save.Content);
        var nextIds = next.Select(Id).ToHashSet();
        var nextKeys = next.Select(e => RegistryPolicyDocument.NormalizeKey(e.Key)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var commands = new List<PowerShellCommand>();
        PowerShellCommand Command(string cmdlet, string key, params PowerShellParameter[] parameters) => new(cmdlet,
        [
            PowerShellParameter.Literal("Guid", gpoId.ToString("D")),
            PowerShellParameter.Literal("Domain", session.Info.DomainDnsName),
            PowerShellParameter.Server,
            PowerShellParameter.Literal("Key", hive + "\\" + RegistryPolicyDocument.NormalizeKey(key)),
            .. parameters,
        ]);

        foreach (var entry in next.GroupBy(Id).Select(g => g.Last()))
        {
            if (old.TryGetValue(Id(entry), out var before) && before.SameData(entry))
            {
                continue;
            }

            var name = entry.ValueName;
            if (name.Equals(RegistryPolicyDocument.DeleteAllValues, StringComparison.OrdinalIgnoreCase))
            {
                commands.Add(Command("Set-GPRegistryValue", entry.Key, PowerShellParameter.Switch("Disable")));
            }
            else if (name.StartsWith(RegistryPolicyDocument.DeletePrefix, StringComparison.OrdinalIgnoreCase))
            {
                commands.Add(Command("Set-GPRegistryValue", entry.Key, PowerShellParameter.Literal("ValueName", name[RegistryPolicyDocument.DeletePrefix.Length..]),
                    PowerShellParameter.Switch("Disable")));
            }
            else
            {
                var soft = name.StartsWith(RegistryPolicyDocument.SoftPrefix, StringComparison.OrdinalIgnoreCase);
                var valueName = soft ? name[RegistryPolicyDocument.SoftPrefix.Length..] : name;
                if (Typed(entry) is not { } typed)
                {
                    continue; // a type Set-GPRegistryValue cannot write (binary, …): the plan's SYSVOL step covers it
                }

                commands.Add(Command("Set-GPRegistryValue", entry.Key, [PowerShellParameter.Literal("ValueName", valueName), .. typed]) with
                {
                    Comment = soft ? "Written as **soft. (kept when the client has the value); Set-GPRegistryValue writes a plain value." : null,
                });
            }
        }

        var removedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, entry) in old)
        {
            if (nextIds.Contains(id))
            {
                continue;
            }

            var key = RegistryPolicyDocument.NormalizeKey(entry.Key);
            if (!nextKeys.Contains(key))
            {
                if (removedKeys.Add(key))
                {
                    commands.Add(Command("Remove-GPRegistryValue", key));
                }

                continue;
            }

            var name = entry.ValueName;
            if (name.Equals(RegistryPolicyDocument.DeleteAllValues, StringComparison.OrdinalIgnoreCase) || name.StartsWith("**Delete", StringComparison.OrdinalIgnoreCase))
            {
                continue; // no cmdlet removes a directive alone; the plan's SYSVOL step does
            }

            var valueName = name.StartsWith(RegistryPolicyDocument.DeletePrefix, StringComparison.OrdinalIgnoreCase) ? name[RegistryPolicyDocument.DeletePrefix.Length..]
                : name.StartsWith(RegistryPolicyDocument.SoftPrefix, StringComparison.OrdinalIgnoreCase) ? name[RegistryPolicyDocument.SoftPrefix.Length..]
                : name;
            commands.Add(Command("Remove-GPRegistryValue", key, PowerShellParameter.Literal("ValueName", valueName)));
        }

        return commands;
    }

    /// <summary><c>-Type … -Value …</c> of a value; null for a type the cmdlet does not take.</summary>
    private static PowerShellParameter[]? Typed(RegistryPolEntry entry) => entry.Type switch
    {
        RegistryFormat.RegSz => [PowerShellParameter.Literal("Type", "String"), PowerShellParameter.Literal("Value", RegistryPolFile.ReadString(entry.Data))],
        RegistryFormat.RegExpandSz => [PowerShellParameter.Literal("Type", "ExpandString"), PowerShellParameter.Literal("Value", RegistryPolFile.ReadString(entry.Data))],
        RegistryFormat.RegDword when entry.Data.Length == 4 =>
            [PowerShellParameter.Literal("Type", "DWord"), PowerShellParameter.Number("Value", BinaryPrimitives.ReadUInt32LittleEndian(entry.Data))],
        RegistryFormat.RegQword when entry.Data.Length == 8 && BinaryPrimitives.ReadUInt64LittleEndian(entry.Data) <= long.MaxValue =>
            [PowerShellParameter.Literal("Type", "QWord"), PowerShellParameter.Number("Value", (long)BinaryPrimitives.ReadUInt64LittleEndian(entry.Data))],
        RegistryFormat.RegMultiSz => [PowerShellParameter.Literal("Type", "MultiString"), PowerShellParameter.Literals("Value", RegistryPolFile.ReadMultiString(entry.Data))],
        _ => null,
    };
}
