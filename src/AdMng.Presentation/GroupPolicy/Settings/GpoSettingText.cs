using AdMng.Core.GroupPolicy.Content;

namespace AdMng.Presentation.GroupPolicy.Settings;

/// <summary>Where a setting sits below its configuration in GPMC's tree: under "Zasady", under "Preferencje", or neither.</summary>
public enum GpoSettingBranch
{
    /// <summary>Directly below the configuration (GPO comments, unreadable files, keys of no known shape).</summary>
    None,

    /// <summary>"Zasady" (Policies).</summary>
    Policies,

    /// <summary>"Preferencje" (Preferences).</summary>
    Preferences,
}

/// <summary>
/// A GPO setting – or, after <see cref="GpoSettingPresenter.DescribeRows"/>, a preference item or an administrative template
/// policy made of several settings – in the operator's language (design note docs/dev/k-computers-and-gpo.md 20.1):
/// where GPMC lists it, its name, its value and the technical key behind it. Built in memory for display only.
/// </summary>
public sealed record GpoSettingText
{
    /// <summary>Separator of the segments of a group path (" › ").</summary>
    public const string Separator = " › ";

    /// <summary>The setting key (for a row made of several settings: the first one).</summary>
    public required string Key { get; init; }

    /// <summary>Every setting key the row shows (one for a single setting).</summary>
    public IReadOnlyList<string> Keys { get; init; } = [];

    public GpoScope Scope { get; init; }

    /// <summary>What the key is about (security template section, Registry.pol, preferences …).</summary>
    public GpoSettingArea Kind { get; init; }

    /// <summary>"Konfiguracja komputera", "Konfiguracja użytkownika" or "Obiekt zasad grupy".</summary>
    public required string Configuration { get; init; }

    public GpoSettingBranch Branch { get; init; }

    /// <summary>"Zasady", "Preferencje" or empty (<see cref="Branch"/> in the window's language).</summary>
    public string BranchText { get; init; } = string.Empty;

    /// <summary>The folders below the branch: "Ustawienia systemu Windows", "Ustawienia zabezpieczeń", "Zasady konta", "Zasady haseł".</summary>
    public IReadOnlyList<string> Groups { get; init; } = [];

    /// <summary>The path below the configuration: "Zasady › Ustawienia systemu Windows › … › Zasady haseł".</summary>
    public string Path => string.Join(Separator, BranchText.Length == 0 ? Groups : [BranchText, .. Groups]);

    /// <summary>GPMC's tree: "Konfiguracja komputera › Zasady › Ustawienia systemu Windows › … › Zasady haseł".</summary>
    public string GroupPath => Path.Length == 0 ? Configuration : Configuration + Separator + Path;

    /// <summary>"Maksymalny okres ważności hasła", "Debugowanie programów", "Administrators (built-in)".</summary>
    public required string Name { get; init; }

    /// <summary>The key or constant behind the setting (tool tip, CSV, search): never a value.</summary>
    public required string Technical { get; init; }

    /// <summary>"42 dni", "Administratorzy, KONTOSO\Serwisanci", "Powodzenie i niepowodzenie", "Włączone".</summary>
    public required string ValueText { get; init; }

    /// <summary>The parts of the value one per line: list elements, a policy's options, a preference item's properties.</summary>
    public IReadOnlyList<string> Elements { get; init; } = [];

    /// <summary>What a special value means, or another note on the setting (null: none).</summary>
    public string? Explanation { get; init; }

    /// <summary>"Edytuj to ustawienie": the setting or policy an editor opens at (null: none, e.g. a cut key).</summary>
    public GroupPolicySettingFocus? Focus { get; init; }

    /// <summary>A secret: the value is never shown (a policy row keeps its state and the options that hold no secret).</summary>
    public bool IsMasked { get; init; }

    /// <summary>The file the setting came from (relative path; null when unknown).</summary>
    public string? Source { get; init; }

    /// <summary>The scope and kind only: names and values are GPO content and stay out of logs.</summary>
    public override string ToString() => Scope + "/" + Kind;
}
