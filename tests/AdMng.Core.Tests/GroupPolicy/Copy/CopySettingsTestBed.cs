using System.Globalization;
using System.Text;
using AdMng.Core.Batch;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy.Copy;

/// <summary>The GPO test bed with the K7 planners on hand-built units, and builders for GPO content.</summary>
public abstract class CopySettingsTestBed : GroupPolicyTestBed
{
    protected const string SecurityPair = "[{827D319E-6EAC-11D2-A4EA-00C04F79F83A}{803E14A0-B4FB-11D0-A0D0-00A0C90F574B}]";
    protected const string AuditPair = "[{F3CCC681-B74C-4060-9F26-CD84525DCA2A}{0F3F3735-573D-9804-99E4-AB2A69BA5FD4}]";
    protected const string MachineRegistryPair = "[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{0F6B957D-509E-11D1-A7CC-0000F87571E3}]";
    protected const string UserRegistryPair = "[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{0F6B957E-509E-11D1-A7CC-0000F87571E3}]";

    /// <summary>Drive Maps as GPMC lists them in gPCUserExtensionNames (core entry + CSE, both with the Drive Maps tool).</summary>
    protected const string DrivesPairs = "[{00000000-0000-0000-0000-000000000000}{2EA1A81B-48E5-45E9-8BB7-A6E3AC170006}][{5794DAFD-BE60-433F-88A2-1A31939AC01F}{2EA1A81B-48E5-45E9-8BB7-A6E3AC170006}]";

    protected const string DrivesClassId = "{8FDDCC1A-0C3C-43cd-A6B4-71A6DF20DA8C}";
    protected const string DrivesPath = "User/Preferences/Drives/Drives.xml";

    protected CopySettingsTestBed()
    {
        Time.SetUtcNow(new DateTimeOffset(2026, 11, 9, 14, 30, 5, TimeSpan.Zero));
        Planner.UnitSource = Units.Source;
    }

    protected HandUnits Units { get; } = new();

    protected PolicyDefinitionSet Definitions { get; } = SampleAdministrativeTemplates.LoadPolish();

    /// <summary>A new managed GPO (versions 0/0 unless given) with optional extension names.</summary>
    protected Guid AddGpo(string name, int user = 0, int computer = 0, string? machineNames = null, string? userNames = null, int flags = 0)
    {
        var attributes = new Dictionary<string, object?> { ["flags"] = flags };
        if (machineNames is not null)
        {
            attributes["gPCMachineExtensionNames"] = machineNames;
        }

        if (userNames is not null)
        {
            attributes["gPCUserExtensionNames"] = userNames;
        }

        return Builder.AddManagedGroupPolicy(Sysvol, name, user, computer, attributes: attributes);
    }

    protected void PutTemplate(Guid gpo, params string[] lines) =>
        Sysvol.Put(gpo, SecurityTemplate.RelativePath, FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template(lines)));

    protected void PutRegistry(Guid gpo, bool user, params RegistryPolEntry[] entries) =>
        Sysvol.Put(gpo, user ? GroupPolicyPlanner.UserRegistryPolicyPath : GroupPolicyPlanner.MachineRegistryPolicyPath, RegistryPolFile.Write(entries));

    protected void PutDrives(Guid gpo, params string[] items) =>
        Sysvol.Put(gpo, DrivesPath, Encoding.UTF8.GetBytes($"<?xml version=\"1.0\" encoding=\"utf-8\"?><Drives clsid=\"{DrivesClassId}\">{string.Concat(items)}</Drives>"));

    /// <summary>A GPP drive map item (K: unless given).</summary>
    protected static string Drive(string path, string letter = "K", string? uid = null, string extra = "", string filters = "", string itemAttributes = "") =>
        $"<Drive clsid=\"{{935D1B74-9CB8-4e3c-9914-7DD559B7A417}}\" name=\"{letter}:\" status=\"{letter}:\" image=\"2\" changed=\"2026-10-01 08:00:00\" uid=\"{uid ?? Guid.NewGuid().ToString("B").ToUpperInvariant()}\"{itemAttributes}>"
        + $"<Properties action=\"U\" thisDrive=\"NOCHANGE\" allDrives=\"NOCHANGE\" userName=\"\" path=\"{System.Security.SecurityElement.Escape(path)}\" label=\"Dział\" persistent=\"1\" useLetter=\"1\" letter=\"{letter}\"{extra}/>"
        + filters + "</Drive>";

    protected static RegistryPolEntry Dword(string key, string name, uint value) => new(key, name, RegistryFormat.RegDword, RegistryPolFile.DwordData(value));

    protected static RegistryPolEntry Text(string key, string name, string value) => new(key, name, RegistryFormat.RegSz, RegistryPolFile.StringData(value));

    protected static RegistryPolEntry DeleteAll(string key) => new(key, RegistryPolicyDocument.DeleteAllValues, RegistryFormat.RegSz, RegistryPolEntry.DeletionData);

    /// <summary>The units of a GPO as the planner sees them now (hand-built).</summary>
    protected async Task<IReadOnlyList<GpoSettingUnit>> UnitsOfAsync(Guid gpo, PolicyDefinitionSet? definitions = null)
    {
        await using var sysvol = await OpenSysvolAsync();
        var content = await GroupPolicyReader.ReadFolderAsync(sysvol, gpo, Ct);
        return Units.Of(content, new GpoUnitContext(definitions, Builder.DomainSid) { SidsByName = new Dictionary<string, string>(Units.Accounts, StringComparer.OrdinalIgnoreCase) });
    }

    /// <summary>The selection of the single unit of <paramref name="gpo"/> that matches.</summary>
    protected async Task<GpoUnitSelection> SelectAsync(Guid gpo, Func<GpoSettingUnit, bool> match, PolicyDefinitionSet? definitions = null)
    {
        var unit = Assert.Single(await UnitsOfAsync(gpo, definitions), u => match(u));
        return new GpoUnitSelection(gpo, unit.Id, unit.ItemId, unit.ValueFingerprint);
    }

    protected static Func<GpoSettingUnit, bool> Key(string fragment) =>
        u => u.Settings.Any(s => s.Key.Contains(fragment, StringComparison.OrdinalIgnoreCase)) || u.BaseId.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    protected async Task<GpoSettingsPlan> CopyAsync(Guid target, IReadOnlyList<GpoUnitSelection> units, GpoCopyConflictMode conflict = GpoCopyConflictMode.Replace,
        PolicyDefinitionSet? definitions = null)
    {
        await using var sysvol = await OpenSysvolAsync();
        return await Planner.PlanCopySettingsAsync(Session, sysvol, new GpoSettingsCopyRequest
        {
            TargetId = target,
            Units = units,
            Conflict = conflict,
            Definitions = definitions,
        }, Ct);
    }

    protected async Task<GpoSettingsPlan> RemoveAsync(Guid gpo, IReadOnlyList<GpoUnitSelection> units, PolicyDefinitionSet? definitions = null)
    {
        await using var sysvol = await OpenSysvolAsync();
        return await Planner.PlanRemoveSettingsAsync(Session, sysvol, gpo, units, definitions, Ct);
    }

    protected async Task<GpoSettingsPlan> MergeAsync(Guid a, Guid b, GpoCopyConflictMode? conflict = null, PolicyDefinitionSet? definitions = null)
    {
        await using var sysvol = await OpenSysvolAsync();
        return await Planner.PlanMergeAsync(Session, sysvol, new GpoMergeRequest { SourceId = a, TargetId = b, Conflict = conflict, Definitions = definitions }, Ct);
    }

    /// <summary>Executes the plan, then its rollback; returns the result of the plan.</summary>
    protected async Task<BatchResult> ExecuteAndUndoAsync(ChangePlan plan, Action afterExecute)
    {
        var result = await ExecuteOkAsync(plan);
        afterExecute();
        await ExecuteOkAsync(new RollbackPlanner().CreateRollbackPlan(result));
        return result;
    }

    /// <summary>
    /// What a GPO looks like for undo checks: its files (GPT.INI excluded), extension names and flags. The version is left out:
    /// an undo raises it again so that clients reprocess the GPO.
    /// </summary>
    protected string Snapshot(Guid gpo) =>
        string.Join("\n", Sysvol.Files(gpo).Where(f => !f.Equals(GptIni.RelativePath, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase)
            .Select(f => f + "=" + Convert.ToHexString(Sysvol.Get(gpo, f)!)))
        + "\nmachine=" + Value(GpoDn(gpo), "gPCMachineExtensionNames")
        + "\nuser=" + Value(GpoDn(gpo), "gPCUserExtensionNames")
        + "\nflags=" + Value(GpoDn(gpo), "flags");

    protected GpoVersion DirectoryVersion(Guid gpo) => GpoVersion.FromInt32(int.Parse(Value(GpoDn(gpo), "versionNumber")!, CultureInfo.InvariantCulture));

    protected GpoVersion GptVersion(Guid gpo) => GpoVersion.FromInt32(GptIni.ReadVersion(Sysvol.Get(gpo, GptIni.RelativePath)!)!.Value);

    protected static IReadOnlyList<ValidationIssue> IssuesOf(ChangePlan plan) =>
        [.. plan.Objects.SelectMany(o => o.Issues.Concat(o.Changes.SelectMany(c => c.Issues)))];

    protected static ObjectPlan Target(GpoSettingsPlan plan) => plan.Plan.Objects[0];
}
