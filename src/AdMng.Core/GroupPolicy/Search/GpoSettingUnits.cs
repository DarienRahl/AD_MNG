using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;

namespace AdMng.Core.GroupPolicy.Search;

/// <summary>
/// Builds the setting units of one GPO (docs/dev/k-computers-and-gpo.md 21.2): pure, in memory. K7 contract –
/// implementation: W1.
/// </summary>
public static class GpoSettingUnits
{
    /// <summary>The units of one GPO from its normalized settings and its two parsed Registry.pol documents.</summary>
    public static IReadOnlyList<GpoSettingUnit> Of(IReadOnlyList<GpoSetting> settings, RegistryPolicyDocument? machineRegistry,
        RegistryPolicyDocument? userRegistry, GpoUnitContext context) =>
        throw new NotImplementedException("K7 W1: GpoSettingUnits.Of");

    /// <summary>The units of a folder read in full (planners: <see cref="GroupPolicyReader.ReadSettings"/> + both Registry.pol).</summary>
    public static IReadOnlyList<GpoSettingUnit> Of(GpoFolderContent content, GpoUnitContext context) =>
        throw new NotImplementedException("K7 W1: GpoSettingUnits.Of(content)");

    /// <summary>
    /// The principal names in <paramref name="settings"/> that <see cref="GpoPrincipalResolver.KnownSid"/> cannot map
    /// (distinct, as written): the input of one <see cref="GpoPrincipalResolver.ResolveAsync"/> batch before <c>Of</c>.
    /// </summary>
    public static IReadOnlyCollection<string> PrincipalNames(IReadOnlyList<GpoSetting> settings, string? domainSid) =>
        throw new NotImplementedException("K7 W1: GpoSettingUnits.PrincipalNames");

    /// <summary>
    /// Same setting, same value: false unless both are <see cref="GpoUnitComparability.Comparable"/> and neither is
    /// <see cref="GpoSettingUnit.Disabled"/>; policies compare by <see cref="PolicySetting"/> equality, the others by
    /// <see cref="GpoSettingUnit.ValueFingerprint"/>. Masked values are never equal.
    /// </summary>
    public static bool SameValue(GpoSettingUnit a, GpoSettingUnit b) =>
        throw new NotImplementedException("K7 W1: GpoSettingUnits.SameValue");

    /// <summary>The <see cref="GpoSettingUnit.BaseId"/> of a unit id (strips <c>#n</c>).</summary>
    public static string BaseIdOf(string unitId) =>
        throw new NotImplementedException("K7 W1: GpoSettingUnits.BaseIdOf");

    /// <summary>
    /// The per-GPO preference item prefix of a setting key (<c>&lt;Scope&gt;/Preferences/&lt;Type&gt;/&lt;id&gt;</c>), null
    /// for other keys – exactly what the old private <c>PreferenceItem(key)</c> copies of the K5 modeller and the K6
    /// conflict rules return.
    /// </summary>
    public static string? ItemKeyOf(string settingKey) =>
        throw new NotImplementedException("K7 W1: GpoSettingUnits.ItemKeyOf");
}
