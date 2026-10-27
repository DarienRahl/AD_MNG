using System.Globalization;
using System.Security.Cryptography;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;

namespace AdMng.Core.GroupPolicy.Comparison;

/// <summary>Part of a GPO comparison.</summary>
public enum GpoComparisonSection
{
    /// <summary>Status, versions, WMI filter, extension names.</summary>
    Core,

    /// <summary>Security filtering and delegation (trustee → level).</summary>
    Permissions,

    /// <summary>Links (container → order, enforced, enabled).</summary>
    Links,

    /// <summary>Normalized settings of the files (the Watchdog's keys).</summary>
    Settings,

    /// <summary>The files of the GPO folders.</summary>
    Files,
}

/// <summary>How a row of the comparison differs.</summary>
public enum GpoComparisonState
{
    Same,
    Different,
    OnlyInA,
    OnlyInB,
}

/// <summary>One row of a GPO comparison.</summary>
/// <param name="Section">Part of the GPO.</param>
/// <param name="Key">Language-neutral key: a core key (<see cref="GroupPolicyComparer.StatusKey"/> …), a trustee SID, a container DN, a setting key or a file path.</param>
/// <param name="ValueA">Value in GPO A (null: absent).</param>
/// <param name="ValueB">Value in GPO B (null: absent).</param>
/// <param name="State">Same, different, only in A, only in B.</param>
/// <param name="Masked">A secret setting: only its presence is compared and shown, never a value.</param>
public sealed record GpoComparisonRow(GpoComparisonSection Section, string Key, string? ValueA, string? ValueB, GpoComparisonState State, bool Masked = false);

/// <summary>What one side of a comparison holds (read on the session's DC, in memory).</summary>
/// <param name="Gpo">The container (with the GPT.INI version when read).</param>
/// <param name="Content">The folder's files; null when SYSVOL could not be read (settings and files are then not compared).</param>
/// <param name="Links">The GPO's links.</param>
public sealed record GpoComparisonSide(GpoInfo Gpo, GpoFolderContent? Content, IReadOnlyList<GpoLinkInfo> Links);

/// <summary>
/// Compares two GPOs (design note docs/dev/k-computers-and-gpo.md 5.6): core settings (status, versions, WMI filter,
/// extension names), security filtering and delegation (GPMC levels per trustee), links, the normalized settings of both
/// folders (the shared parsers of <see cref="GpoContentParser"/>; masked values are compared by presence only and never
/// shown) and the file list (identical / different / only in A / only in B). Pure and in memory.
/// </summary>
public static class GroupPolicyComparer
{
    public const string StatusKey = "core:status";
    public const string UserVersionKey = "core:userVersion";
    public const string ComputerVersionKey = "core:computerVersion";
    public const string WmiFilterKey = "core:wmiFilter";
    public const string MachineExtensionsKey = "core:machineExtensions";
    public const string UserExtensionsKey = "core:userExtensions";

    /// <summary>The value of a masked setting (presence only).</summary>
    public const string MaskedValue = "***";

    public static IReadOnlyList<GpoComparisonRow> Compare(GpoComparisonSide a, GpoComparisonSide b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var rows = new List<GpoComparisonRow>
        {
            Row(GpoComparisonSection.Core, StatusKey, a.Gpo.Status.ToString(), b.Gpo.Status.ToString()),
            Row(GpoComparisonSection.Core, UserVersionKey, Number(a.Gpo.DirectoryVersion?.User), Number(b.Gpo.DirectoryVersion?.User)),
            Row(GpoComparisonSection.Core, ComputerVersionKey, Number(a.Gpo.DirectoryVersion?.Computer), Number(b.Gpo.DirectoryVersion?.Computer)),
            Row(GpoComparisonSection.Core, WmiFilterKey, a.Gpo.WmiFilterId, b.Gpo.WmiFilterId),
            Row(GpoComparisonSection.Core, MachineExtensionsKey, Blank(a.Gpo.MachineExtensionNames), Blank(b.Gpo.MachineExtensionNames)),
            Row(GpoComparisonSection.Core, UserExtensionsKey, Blank(a.Gpo.UserExtensionNames), Blank(b.Gpo.UserExtensionNames)),
        };

        rows.AddRange(Join(GpoComparisonSection.Permissions,
            GpoPermissionLevels.Read(a.Gpo.SecurityDescriptor).ToDictionary(p => p.Sid, p => p.Level.ToString(), StringComparer.OrdinalIgnoreCase),
            GpoPermissionLevels.Read(b.Gpo.SecurityDescriptor).ToDictionary(p => p.Sid, p => p.Level.ToString(), StringComparer.OrdinalIgnoreCase)));
        rows.AddRange(Join(GpoComparisonSection.Links, Links(a.Links), Links(b.Links)));
        if (a.Content is { } contentA && b.Content is { } contentB)
        {
            var settingsA = GroupPolicyReader.Settings(contentA).ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
            var settingsB = GroupPolicyReader.Settings(contentB).ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var key in settingsA.Keys.Union(settingsB.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                var sa = settingsA.GetValueOrDefault(key);
                var sb = settingsB.GetValueOrDefault(key);
                var masked = sa?.Kind == GpoSettingKind.Masked || sb?.Kind == GpoSettingKind.Masked;
                rows.Add(Row(GpoComparisonSection.Settings, key, SettingValue(sa), SettingValue(sb)) with { Masked = masked });
            }

            rows.AddRange(Join(GpoComparisonSection.Files, Files(contentA), Files(contentB)));
        }

        return rows;
    }

    private static string? SettingValue(GpoSetting? setting) => setting switch
    {
        null => null,
        { Kind: GpoSettingKind.Masked } => MaskedValue,
        { Kind: GpoSettingKind.Unreadable } => GpoSettingsUnreadable,
        { IsList: true } => string.Join(", ", setting.Elements()),
        _ => setting.Value,
    };

    /// <summary>The value shown for a file that could not be parsed.</summary>
    public const string GpoSettingsUnreadable = "(unreadable)";

    private static Dictionary<string, string> Links(IReadOnlyList<GpoLinkInfo> links) =>
        links.GroupBy(l => l.ContainerDn, StringComparer.OrdinalIgnoreCase).ToDictionary(
            g => g.Key,
            g => g.First() is var l
                ? string.Create(CultureInfo.InvariantCulture, $"order {l.Order}{(l.Enforced ? ", enforced" : string.Empty)}{(l.Enabled ? string.Empty : ", disabled")}")
                : string.Empty,
            StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> Files(GpoFolderContent content) =>
        content.Files.Where(f => !string.Equals(f.Key, GptIni.RelativePath, StringComparison.OrdinalIgnoreCase)).ToDictionary(
            f => f.Key,
            f => string.Create(CultureInfo.InvariantCulture, $"{f.Value.Length} B, sha256:{Convert.ToHexStringLower(SHA256.HashData(f.Value))[..12]}"),
            StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<GpoComparisonRow> Join(GpoComparisonSection section, Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Select(key => Row(section, key, a.GetValueOrDefault(key), b.GetValueOrDefault(key)));

    private static GpoComparisonRow Row(GpoComparisonSection section, string key, string? a, string? b) => new(section, key, a, b, (a, b) switch
    {
        (null, null) => GpoComparisonState.Same,
        (null, _) => GpoComparisonState.OnlyInB,
        (_, null) => GpoComparisonState.OnlyInA,
        _ => string.Equals(a, b, StringComparison.Ordinal) ? GpoComparisonState.Same : GpoComparisonState.Different,
    });

    private static string? Number(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
