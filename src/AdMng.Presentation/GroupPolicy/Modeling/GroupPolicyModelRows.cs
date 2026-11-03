using System.Globalization;
using System.Text;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Modeling;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.GroupPolicy.Settings;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.GroupPolicy.Modeling;

/// <summary>What a part of a model is computed for: an account, a container (a new account there) or nothing.</summary>
public enum ModelingTargetKind
{
    None,
    Account,
    Container,
}

/// <summary>The computer or user input of a model.</summary>
/// <param name="Kind">Account, container or none.</param>
/// <param name="Dn">The account or the container (null for none).</param>
public sealed record ModelingTargetChoice(ModelingTargetKind Kind, string? Dn)
{
    public static ModelingTargetChoice None { get; } = new(ModelingTargetKind.None, null);

    public static ModelingTargetChoice Account(string dn) => new(ModelingTargetKind.Account, dn);

    public static ModelingTargetChoice Container(string dn) => new(ModelingTargetKind.Container, dn);

    public bool IsNone => Kind == ModelingTargetKind.None || string.IsNullOrWhiteSpace(Dn);

    /// <summary>The same target (DNs compared as DNs).</summary>
    public bool SameAs(ModelingTargetChoice other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return IsNone ? other.IsNone : !other.IsNone && Kind == other.Kind && Core.Ldap.DistinguishedName.AreEqual(Dn!, other.Dn!);
    }
}

/// <summary>A site of the "Lokacja" list (null DN: unknown).</summary>
public sealed record ModelingSiteOption(string? Dn, string Name)
{
    public override string ToString() => Name;
}

/// <summary>An item of the WMI filter choice.</summary>
public sealed record WmiAssumptionOption(WmiFilterAssumption Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>An item of the loopback choice.</summary>
public sealed record LoopbackOption(LoopbackChoice Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>A GPO of a part ("Obiekty zasad grupy" of the model): applied ones by precedence, then the excluded ones.</summary>
public sealed record ModelGpoRowViewModel
{
    public required ModeledGpo Gpo { get; init; }

    public string Name => Gpo.Name;

    public bool Applies => Gpo.Applies;

    /// <summary>"1" … for applied GPOs, a dash for the others.</summary>
    public required string PrecedenceText { get; init; }

    /// <summary>The link's container ("contoso.test/Firma/Komputery", the site's name, the domain).</summary>
    public required string LocationText { get; init; }

    public required string EnforcedText { get; init; }

    public bool Enforced => Gpo.Link.Enforced;

    public string LinkOrderText => Gpo.Link.Order.ToString(CultureInfo.CurrentCulture);

    /// <summary>"Zastosowany", "Zablokowany przez blokowanie dziedziczenia w …".</summary>
    public required string OutcomeText { get; init; }
}

/// <summary>A setting of a part: an administrative template policy or a normalized setting, with its winner.</summary>
public sealed record ModelSettingRowViewModel
{
    /// <summary>"Szablony administracyjne / Składniki systemu Windows / Aktualizacje", "Zasady kont".</summary>
    public required string Area { get; init; }

    public required string Name { get; init; }

    public required string Value { get; init; }

    public required string WinnerGpo { get; init; }

    /// <summary>The other applied GPOs that configure it (overridden, or – cumulative – also applied).</summary>
    public required int OverriddenCount { get; init; }

    public string OverriddenText => OverriddenCount == 0 ? string.Empty : OverriddenCount.ToString(CultureInfo.CurrentCulture);

    public bool IsConditional { get; init; }

    public bool IsCumulative { get; init; }

    /// <summary>"zależy od komputera", "kumulatywne" (empty when neither).</summary>
    public required string MarksText { get; init; }

    /// <summary>"Dlaczego": the winner, its link and the overridden GPOs with their values.</summary>
    public required string Details { get; init; }

    /// <summary>The key or policy behind the row (tool tip); empty for a policy.</summary>
    public string Technical { get; init; } = string.Empty;

    /// <summary>"Edytuj to ustawienie": the setting or policy in the winning GPO (null: no editor of AD Manager edits it).</summary>
    public GroupPolicySettingFocus? Focus { get; init; }

    /// <summary>The GPO whose value wins (the one "Edytuj to ustawienie" opens).</summary>
    public Guid GpoId { get; init; }

    /// <summary>An editor of AD Manager edits the setting.</summary>
    public bool CanEdit => GroupPolicyActions.CanEditSetting(Focus);
}

/// <summary>"Konfiguracja komputera" or "Konfiguracja użytkownika" of the model.</summary>
public sealed partial class ModelPartViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;

    public ModelPartViewModel(ILocalizer localizer, ModelingPart part)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        Part = part;
        DetailsText = localizer[L.GroupPolicyModel.DetailsHint];
    }

    public ModelingPart Part { get; }

    /// <summary>The model has this part.</summary>
    [ObservableProperty]
    public partial bool IsAvailable { get; private set; }

    /// <summary>"Model nie obejmuje komputera." when it has not.</summary>
    [ObservableProperty]
    public partial string UnavailableText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<ModelGpoRowViewModel> Gpos { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ModelSettingRowViewModel> Settings { get; private set; } = [];

    [ObservableProperty]
    public partial ModelSettingRowViewModel? SelectedSetting { get; set; }

    /// <summary>"Dlaczego" of the selected setting (a hint when none is selected).</summary>
    [ObservableProperty]
    public partial string DetailsText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    public partial IReadOnlyList<string> Warnings { get; private set; } = [];

    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>The token: the account and its groups ("CONTOSO\GG_IT"), well-known principals by name.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> Groups { get; private set; } = [];

    /// <summary>"3 GPO, 12 ustawień".</summary>
    [ObservableProperty]
    public partial string CountText { get; private set; } = string.Empty;

    /// <summary>The GPOs that apply.</summary>
    public int AppliedCount => Gpos.Count(g => g.Applies);

    partial void OnSelectedSettingChanged(ModelSettingRowViewModel? value) =>
        DetailsText = value?.Details ?? _localizer[L.GroupPolicyModel.DetailsHint];

    internal void Show(IReadOnlyList<ModelGpoRowViewModel> gpos, IReadOnlyList<ModelSettingRowViewModel> settings, IReadOnlyList<string> warnings, IReadOnlyList<string> groups)
    {
        var selected = SelectedSetting;
        IsAvailable = true;
        UnavailableText = string.Empty;
        Gpos = gpos;
        Settings = settings;
        Warnings = warnings;
        Groups = groups;
        CountText = _localizer.Format(L.GroupPolicyModel.GpoCount, AppliedCount) + ", "
            + CorrespondenceText.Plural(_localizer, settings.Count, L.GroupPolicyModel.SettingsOne, L.GroupPolicyModel.SettingsFew, L.GroupPolicyModel.SettingsMany);
        SelectedSetting = selected is null ? null : settings.FirstOrDefault(s => s.Area == selected.Area && s.Name == selected.Name);
        OnSelectedSettingChanged(SelectedSetting);
    }

    internal void Clear(string unavailableText)
    {
        IsAvailable = false;
        UnavailableText = unavailableText;
        Gpos = [];
        Settings = [];
        Warnings = [];
        Groups = [];
        CountText = string.Empty;
        SelectedSetting = null;
        OnSelectedSettingChanged(null);
    }
}

/// <summary>The texts of a model (outcomes, values, details, warnings) in the window's language.</summary>
/// <param name="localizer">The window's language.</param>
/// <param name="domainDnsName">The domain (the location of a domain link).</param>
/// <param name="sidNames">Names of the domain SIDs the settings name (resolved once per model).</param>
internal sealed class GroupPolicyModelText(ILocalizer localizer, string domainDnsName, IReadOnlyDictionary<string, string>? sidNames = null)
{
    private readonly IReadOnlyDictionary<string, string>? _sidNames = sidNames;

    private ILocalizer L10n { get; } = localizer ?? throw new ArgumentNullException(nameof(localizer));

    /// <summary>The domain head, a site (its name) or an OU (its canonical path).</summary>
    public string Location(GroupPolicySom container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return container.Kind switch
        {
            GroupPolicySomKind.Domain => domainDnsName,
            GroupPolicySomKind.Site => Core.Ldap.DistinguishedName.TryParse(container.Dn, out var site) && !site.IsRoot ? site.Name : container.Dn,
            _ => AddressResolver.ToDisplayPath(container.Dn),
        };
    }

    public ModelGpoRowViewModel GpoRow(ModeledGpo gpo, ModelingPart part) => new()
    {
        Gpo = gpo,
        PrecedenceText = gpo.Precedence?.ToString(L10n.Culture) ?? "–",
        LocationText = Location(gpo.Container),
        EnforcedText = GroupPolicyText.YesNo(L10n, gpo.Link.Enforced),
        OutcomeText = Outcome(gpo, part),
    };

    public string Outcome(ModeledGpo gpo, ModelingPart part)
    {
        ArgumentNullException.ThrowIfNull(gpo);
        var wmi = gpo.WmiFilterName ?? L10n[L.GroupPolicyModel.WmiUnknownName];
        var text = gpo.Outcome switch
        {
            ModeledGpoOutcome.Applied => L10n[L.GroupPolicyModel.OutcomeApplied],
            ModeledGpoOutcome.AppliedIfWmiFilter => L10n.Format(L.GroupPolicyModel.OutcomeAppliedIfWmi, wmi),
            ModeledGpoOutcome.LinkDisabled => L10n[L.GroupPolicyModel.OutcomeLinkDisabled],
            ModeledGpoOutcome.Blocked => L10n.Format(L.GroupPolicyModel.OutcomeBlocked, gpo.BlockedBy is { } by ? AddressResolver.ToDisplayPath(by) : "–"),
            ModeledGpoOutcome.Duplicate => L10n[L.GroupPolicyModel.OutcomeDuplicate],
            ModeledGpoOutcome.NotFound => L10n[L.GroupPolicyModel.OutcomeNotFound],
            ModeledGpoOutcome.OtherDomain => L10n[L.GroupPolicyModel.OutcomeOtherDomain],
            ModeledGpoOutcome.PartDisabled => L10n[part == ModelingPart.Computer ? L.GroupPolicyModel.OutcomeComputerDisabled : L.GroupPolicyModel.OutcomeUserDisabled],
            ModeledGpoOutcome.Empty => L10n[L.GroupPolicyModel.OutcomeEmpty],
            ModeledGpoOutcome.SecurityDenied => L10n[L.GroupPolicyModel.OutcomeSecurityDenied],
            ModeledGpoOutcome.ComputerCannotRead => L10n[L.GroupPolicyModel.OutcomeComputerCannotRead],
            _ => L10n.Format(L.GroupPolicyModel.OutcomeWmiFalse, wmi),
        };
        return gpo.FromLoopback ? L10n.Format(L.GroupPolicyModel.OutcomeLoopback, text) : text;
    }

    public string Warning(ModelingWarning warning)
    {
        ArgumentNullException.ThrowIfNull(warning);
        var gpo = warning.GpoName ?? (warning.GpoId is { } id ? GroupPolicyText.Guid(id) : "–");
        return warning.Kind switch
        {
            ModelingWarningKind.NoSite => L10n[L.GroupPolicyModel.WarningNoSite],
            ModelingWarningKind.ContentUnreadable => L10n.Format(L.GroupPolicyModel.WarningContentUnreadable, gpo),
            ModelingWarningKind.FileUnreadable => L10n.Format(L.GroupPolicyModel.WarningFileUnreadable, gpo, warning.Detail ?? "–"),
            ModelingWarningKind.NoDefinitions => L10n[L.GroupPolicyModel.WarningNoDefinitions],
            ModelingWarningKind.AccountPolicyBelowDomain => L10n.Format(L.GroupPolicyModel.WarningAccountPolicy, gpo),
            ModelingWarningKind.TokenIncomplete => L10n[L.GroupPolicyModel.WarningTokenIncomplete],
            _ => L10n[L.GroupPolicyModel.WarningNoComputer],
        };
    }

    public string LoopbackMode(LoopbackMode mode) => L10n[mode switch
    {
        Core.GroupPolicy.Modeling.LoopbackMode.Merge => L.GroupPolicyModel.ModeMerge,
        Core.GroupPolicy.Modeling.LoopbackMode.Replace => L.GroupPolicyModel.ModeReplace,
        _ => L.GroupPolicyModel.ModeNone,
    }];

    /// <summary>"Pętla zwrotna: scalanie – ustawiona przez GPO „…”."</summary>
    public string Loopback(GroupPolicyModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.LoopbackSource switch
        {
            LoopbackSource.Settings => L10n.Format(L.GroupPolicyModel.LoopbackFromGpo, LoopbackMode(model.Loopback), model.LoopbackSetBy?.GpoName ?? "–"),
            LoopbackSource.NotConfigured => L10n[L.GroupPolicyModel.LoopbackNotConfigured],
            LoopbackSource.Operator => L10n.Format(L.GroupPolicyModel.LoopbackByOperator, LoopbackMode(model.Loopback)),
            _ => L10n[L.GroupPolicyModel.LoopbackNotApplicable],
        };
    }

    /// <summary>The policies and the other settings of a part as rows (policies first, both by area and name).</summary>
    public IReadOnlyList<ModelSettingRowViewModel> SettingRows(ModeledPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var links = part.Gpos.Where(g => g.Applies).GroupBy(g => g.GpoId).ToDictionary(g => g.Key, g => g.First());
        var rows = new List<ModelSettingRowViewModel>();
        foreach (var policy in part.Policies)
        {
            var winnerValue = PolicyValue(policy.Definition, policy.Winner.Setting);
            var details = Details(links, policy.Winner.Source, winnerValue,
                [.. policy.Overridden.Select(o => (o.Source, PolicyValue(policy.Definition, o.Setting)))], cumulative: false, conditional: false);
            rows.Add(new ModelSettingRowViewModel
            {
                Area = PolicyArea(policy.Definition),
                Name = policy.Definition.DisplayName,
                Value = winnerValue,
                WinnerGpo = policy.Winner.Source.GpoName,
                OverriddenCount = policy.Overridden.Count,
                MarksText = string.Empty,
                Details = details,
                Focus = GroupPolicySettingFocus.ForPolicy(part.Part == ModelingPart.User ? PolicyClass.User : PolicyClass.Machine, policy.Definition.Id),
                GpoId = policy.Winner.Source.GpoId,
            });
        }

        // The other settings in the presenter's words; the settings of one preference item from one GPO are one row.
        var presenter = new GpoSettingPresenter(L10n, _sidNames)
        {
            ItemNames = GpoSettingPresenter.ItemNamesOf(part.Settings.SelectMany(s => s.Overridden.Select(o => o.Setting).Prepend(s.Winner.Setting))),
        };
        foreach (var group in part.Settings.GroupBy(ItemOf))
        {
            var members = group.ToList();
            var winners = members.Select(m => m.Winner.Setting).ToList();
            var text = members.Count == 1 ? presenter.Describe(winners[0]) : presenter.DescribeRows(winners)[0];
            var others = members.SelectMany(m => m.Overridden).GroupBy(o => o.Source.GpoId).Select(g =>
            {
                var settings = g.Select(o => o.Setting).ToList();
                var value = settings.Count == 1 ? presenter.Describe(settings[0]).ValueText : presenter.DescribeRows(settings)[0].ValueText;
                return (g.First().Source, value);
            }).ToList();
            var conditional = members.Any(m => m.Winner.Conditional);
            var cumulative = members.Any(m => m.Cumulative);
            var marks = new List<string>();
            if (conditional)
            {
                marks.Add(L10n[L.GroupPolicyModel.MarkConditional]);
            }

            if (cumulative)
            {
                marks.Add(L10n[L.GroupPolicyModel.MarkCumulative]);
            }

            var winner = members[0].Winner.Source;
            rows.Add(new ModelSettingRowViewModel
            {
                Area = SettingArea(text),
                Name = text.Name,
                Value = text.ValueText,
                WinnerGpo = winner.GpoName,
                OverriddenCount = others.Count,
                IsConditional = conditional,
                IsCumulative = cumulative,
                MarksText = string.Join(", ", marks),
                Details = Details(links, winner, text.ValueText, others, cumulative, conditional),
                Technical = text.Technical,
                Focus = text.Focus,
                GpoId = winner.GpoId,
            });
        }

        var compare = StringComparer.Create(L10n.Culture, ignoreCase: true);
        return [.. rows.OrderBy(r => r.Area, compare).ThenBy(r => r.Name, compare)];
    }

    /// <summary>The settings of one preference item that one GPO wins are one row; every other setting is a row of its own.</summary>
    private static string ItemOf(ModeledSetting setting) =>
        GpoSettingKey.Parse(setting.Key) is { Area: GpoSettingArea.Preference, ItemKey: { } item, Truncated: false }
            ? "I|" + item + "|" + setting.Winner.Source.GpoId.ToString("N", CultureInfo.InvariantCulture)
            : "K|" + setting.Key;

    /// <summary>
    /// The area of a setting below its configuration (the model's part says which one): "Ustawienia systemu Windows › … › Zasady
    /// haseł" for a policy setting, "Preferencje › Ustawienia panelu sterowania › …" for a preference.
    /// </summary>
    public static string SettingArea(GpoSettingText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Branch == GpoSettingBranch.Policies ? string.Join(GpoSettingText.Separator, text.Groups) : text.Path;
    }

    /// <summary>"Szablony administracyjne › Składniki systemu Windows › Aktualizacje".</summary>
    public string PolicyArea(PolicyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return string.Join(GpoSettingText.Separator, [L10n[L.GroupPolicyModel.AreaTemplates], .. AdministrativeTemplateText.CategoryPath(definition)]);
    }

    /// <summary>"Włączone; Tryb: Pobierz i zainstaluj, Dzień: 3" – the state and, when enabled, the options as the GPO has them.</summary>
    public string PolicyValue(PolicyDefinition definition, PolicySetting setting) => AdministrativeTemplateText.Value(L10n, definition, setting);

    /// <summary>"Dlaczego": the winner with its link, then the overridden (or, cumulative, the also applied) GPOs with their values.</summary>
    private string Details(IReadOnlyDictionary<Guid, ModeledGpo> links, ModeledSource winner, string value,
        IReadOnlyList<(ModeledSource Source, string Value)> others, bool cumulative, bool conditional)
    {
        var text = new StringBuilder();
        text.AppendLine(L10n.Format(L.GroupPolicyModel.DetailsWinner, winner.GpoName, winner.Precedence.ToString(L10n.Culture)));
        if (links.TryGetValue(winner.GpoId, out var link))
        {
            text.AppendLine(L10n.Format(L.GroupPolicyModel.DetailsLink, Location(link.Container), GroupPolicyText.YesNo(L10n, link.Link.Enforced),
                link.Link.Order.ToString(L10n.Culture)));
            if (link.FromLoopback)
            {
                text.AppendLine(L10n[L.GroupPolicyModel.DetailsLoopback]);
            }
        }

        text.AppendLine(L10n.Format(L.GroupPolicyModel.DetailsValue, value));
        if (conditional)
        {
            text.AppendLine(L10n[L.GroupPolicyModel.DetailsConditional]);
        }

        text.AppendLine();
        if (others.Count == 0)
        {
            text.Append(L10n[L.GroupPolicyModel.DetailsNoneOverridden]);
            return text.ToString();
        }

        if (cumulative)
        {
            text.AppendLine(L10n[L.GroupPolicyModel.DetailsCumulative]);
        }

        text.Append(L10n[cumulative ? L.GroupPolicyModel.DetailsAlsoApplied : L.GroupPolicyModel.DetailsOverridden]);
        foreach (var (source, other) in others)
        {
            text.AppendLine();
            text.Append(L10n.Format(L.GroupPolicyModel.DetailsItem, source.GpoName, source.Precedence.ToString(L10n.Culture), other));
        }

        return text.ToString();
    }
}
