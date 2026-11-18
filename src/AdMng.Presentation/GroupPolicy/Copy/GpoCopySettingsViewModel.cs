using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy.Copy;

/// <summary>The two modes of the "Kopiuj ustawienia" window.</summary>
public enum GpoCopyWindowMode
{
    /// <summary>Copy the checked settings into the target GPO.</summary>
    Copy,

    /// <summary>Merge A into B: every setting of A, then A's links replaced by B and A disabled.</summary>
    Merge,
}

/// <summary>A conflict mode of the window (null <see cref="Mode"/>: derived from the link order, merge only).</summary>
public sealed record GpoConflictOption(GpoCopyConflictMode? Mode, string Text, string Hint);

/// <summary>
/// What the merge window shows besides the settings (21.5): the two GPOs, the conflict mode the link order gives (null
/// with <see cref="OrderMixed"/>: the operator chooses) and the differences in scope (names only, never values).
/// </summary>
/// <param name="SourceName">A (merged and then disabled).</param>
/// <param name="TargetName">B (receives A's settings and links).</param>
/// <param name="Derived">The conflict mode of the link order (null: mixed).</param>
/// <param name="SharedContainers">At least one container links both GPOs.</param>
/// <param name="OrderMixed">A precedes B in some shared containers and follows it in others.</param>
/// <param name="Warnings">The differences of security filtering, WMI filter and links, in the window's language.</param>
public sealed record GpoMergeInfo(string SourceName, string TargetName, GpoCopyConflictMode? Derived, bool SharedContainers, bool OrderMixed,
    IReadOnlyList<string> Warnings)
{
    /// <summary>For diagnostics: the derived mode and counts only (names are directory values).</summary>
    public override string ToString() => $"{Derived?.ToString() ?? "mixed"}, {Warnings.Count} warnings";
}

/// <summary>One setting in the window: its GPMC texts, its source GPO and, when it cannot be copied, why (shown disabled).</summary>
public sealed partial class GpoCopyItemViewModel : ObservableObject
{
    private readonly Action _changed;
    private readonly bool _ready;

    internal GpoCopyItemViewModel(GpoCopyItem item, string? refusalText, Action changed)
    {
        Item = item;
        RefusalText = refusalText;
        _changed = changed;
        IsChecked = CanCopy;
        _ready = true;
    }

    public GpoCopyItem Item { get; }

    public string Name => Item.Name;

    public string GroupPath => Item.GroupPath ?? string.Empty;

    public string ValueText => Item.ValueText ?? string.Empty;

    public string SourceGpoName => Item.SourceGpoName;

    /// <summary>Why the setting cannot be copied (null: it can).</summary>
    public string? RefusalText { get; }

    public bool CanCopy => RefusalText is null;

    /// <summary>The setting is copied (always false for a refused one).</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value)
    {
        if (value && !CanCopy)
        {
            IsChecked = false;
            return;
        }

        if (_ready)
        {
            _changed();
        }
    }

    /// <summary>For diagnostics: the item's GUID and refusal only.</summary>
    public override string ToString() => Item.ToString();
}

/// <summary>
/// "Kopiuj ustawienia" (K7, docs/dev/k-computers-and-gpo.md 21.5, modal): in copy mode the settings offered (those that
/// cannot be copied disabled with the reason – the MS14-025 refusal included), the target GPO, the conflict mode
/// ("Zastąp", "Pomiń", "Połącz listy") and the limit of <see cref="GpoSettingsCopyRequest.MaxSources"/> source GPOs; in
/// merge mode A and B, the conflict mode the link order gives (or the operator's choice when it is mixed), the settings
/// that block the merge and the differences of filtering, WMI filter and links. Nothing is read or written here: the
/// actions plan after OK and show the change plan preview (rule 5).
/// </summary>
public sealed partial class GpoCopySettingsViewModel : WindowViewModelBase
{
    private readonly string _targetName;

    /// <summary>The copy window: <paramref name="items"/> into <paramref name="targetName"/>.</summary>
    public GpoCopySettingsViewModel(DialogServices services, IReadOnlyList<GpoCopyItem> items, string targetName)
        : this(services, GpoCopyWindowMode.Copy, items, targetName, null)
    {
    }

    /// <summary>The merge window: every setting of A (<paramref name="items"/>) into B.</summary>
    public GpoCopySettingsViewModel(DialogServices services, IReadOnlyList<GpoCopyItem> items, GpoMergeInfo merge)
        : this(services, GpoCopyWindowMode.Merge, items, (merge ?? throw new ArgumentNullException(nameof(merge))).TargetName, merge)
    {
    }

    private GpoCopySettingsViewModel(DialogServices services, GpoCopyWindowMode mode, IReadOnlyList<GpoCopyItem> items, string targetName, GpoMergeInfo? merge)
        : base(services)
    {
        ArgumentNullException.ThrowIfNull(items);
        Mode = mode;
        Merge = merge;
        _targetName = targetName ?? string.Empty;
        Items = [.. items.Select(i => new GpoCopyItemViewModel(i, i.Refusal is { } refusal ? GpoUnitRefusalText.Text(Localizer, refusal) : null, OnItemChanged))];
        BuildTexts(keepSelection: false);
    }

    public GpoCopyWindowMode Mode { get; }

    public bool IsMerge => Mode == GpoCopyWindowMode.Merge;

    public bool IsCopy => Mode == GpoCopyWindowMode.Copy;

    /// <summary>The merge's GPOs, derived mode and warnings (null in copy mode).</summary>
    public GpoMergeInfo? Merge { get; }

    public override string WindowTitle => IsMerge
        ? Localizer.Format(L.GpoCopy.MergeWindowTitle, Merge!.SourceName, Merge.TargetName)
        : Localizer.Format(L.GpoCopy.WindowTitle, _targetName);

    /// <summary>The settings offered (copy) or every setting of A (merge).</summary>
    public IReadOnlyList<GpoCopyItemViewModel> Items { get; }

    /// <summary>"Docelowy obiekt GPO: …" (copy) or what the merge does (merge).</summary>
    [ObservableProperty]
    public partial string IntroText { get; private set; } = string.Empty;

    /// <summary>"Zaznaczono 3 z 4" (copy) / "Ustawienia „A” (4)" (merge).</summary>
    [ObservableProperty]
    public partial string CountText { get; private set; } = string.Empty;

    /// <summary>"2 ustawień nie można skopiować – powód przy każdym z nich." (null: every setting can be copied).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRefused))]
    public partial string? RefusedText { get; private set; }

    public bool HasRefused => RefusedText is not null;

    /// <summary>More than <see cref="GpoSettingsCopyRequest.MaxSources"/> source GPOs are checked (OK is disabled).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTooManySources))]
    public partial string? TooManySourcesText { get; private set; }

    public bool HasTooManySources => TooManySourcesText is not null;

    /// <summary>The conflict modes offered.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<GpoConflictOption> ConflictOptions { get; private set; } = [];

    /// <summary>The chosen conflict mode (merge with a mixed order: none until the operator chooses).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConflictHint))]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    public partial GpoConflictOption? SelectedConflict { get; set; }

    public string ConflictHint => SelectedConflict?.Hint ?? string.Empty;

    /// <summary>Merge: what the link order says ("„A” ma pierwszeństwo przed „B” …").</summary>
    [ObservableProperty]
    public partial string OrderText { get; private set; } = string.Empty;

    /// <summary>Merge: the order is mixed and no mode is chosen yet.</summary>
    public bool NeedsConflictChoice => IsMerge && Merge!.OrderMixed && SelectedConflict is null;

    /// <summary>Merge: the settings of A that cannot be copied (they block the merge).</summary>
    public IReadOnlyList<GpoCopyItemViewModel> BlockingItems => IsMerge ? [.. Items.Where(i => !i.CanCopy)] : [];

    public bool HasBlocking => BlockingItems.Count > 0;

    /// <summary>Merge: the differences of filtering, WMI filter and links.</summary>
    public IReadOnlyList<string> Warnings => Merge?.Warnings ?? [];

    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>"Kopiuj" / "Scal".</summary>
    public string OkText => Localizer[IsMerge ? L.GpoCopy.MergeButton : L.GpoCopy.CopyButton];

    /// <summary>The checked settings (set by OK).</summary>
    public IReadOnlyList<GpoCopyItem> SelectedItems { get; private set; } = [];

    /// <summary>The chosen conflict mode (set by OK; null in a merge: derived from the link order).</summary>
    public GpoCopyConflictMode? Conflict { get; private set; }

    /// <summary>The window was confirmed.</summary>
    public bool Confirmed { get; private set; }

    /// <summary>The source GPOs of the checked settings.</summary>
    public int SourceCount => Items.Where(i => i.IsChecked).Select(i => i.Item.Unit.GpoId).Distinct().Count();

    protected override bool UsesSession => false;

    [RelayCommand(CanExecute = nameof(CanOk))]
    private void Ok()
    {
        if (!CanOk())
        {
            return;
        }

        SelectedItems = IsMerge ? [.. Items.Select(i => i.Item)] : [.. Items.Where(i => i.IsChecked).Select(i => i.Item)];
        Conflict = SelectedConflict?.Mode;
        Confirmed = true;
        RequestClose(true);
    }

    private bool CanOk() => SelectedConflict is not null && (IsMerge
        ? !HasBlocking
        : Items.Any(i => i.IsChecked) && SourceCount <= GpoSettingsCopyRequest.MaxSources);

    [RelayCommand]
    private void Cancel() => RequestClose(false);

    /// <summary>Checks every setting that can be copied.</summary>
    [RelayCommand]
    private void CheckAll()
    {
        foreach (var item in Items.Where(i => i.CanCopy))
        {
            item.IsChecked = true;
        }
    }

    /// <summary>Unchecks every setting.</summary>
    [RelayCommand]
    private void CheckNone()
    {
        foreach (var item in Items)
        {
            item.IsChecked = false;
        }
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        BuildTexts(keepSelection: true);
        OnPropertyChanged(nameof(OkText));
    }

    partial void OnSelectedConflictChanged(GpoConflictOption? value) => OnPropertyChanged(nameof(NeedsConflictChoice));

    private void OnItemChanged()
    {
        UpdateCounts();
        OkCommand.NotifyCanExecuteChanged();
    }

    private void BuildTexts(bool keepSelection)
    {
        var selected = keepSelection ? SelectedConflict?.Mode : null;
        var hadSelection = keepSelection && SelectedConflict is not null;
        var replace = new GpoConflictOption(GpoCopyConflictMode.Replace, Localizer[L.GpoCopy.ConflictReplace], Localizer[L.GpoCopy.ConflictReplaceHint]);
        var skip = new GpoConflictOption(GpoCopyConflictMode.Skip, Localizer[L.GpoCopy.ConflictSkip], Localizer[L.GpoCopy.ConflictSkipHint]);
        var union = new GpoConflictOption(GpoCopyConflictMode.Union, Localizer[L.GpoCopy.ConflictUnion], Localizer[L.GpoCopy.ConflictUnionHint]);
        if (IsMerge)
        {
            var merge = Merge!;
            IntroText = Localizer.Format(L.GpoCopy.MergeIntro, merge.SourceName, merge.TargetName);
            OrderText = Localizer.Format(merge.OrderMixed ? L.GpoCopy.MergeOrderMixed
                : !merge.SharedContainers ? L.GpoCopy.MergeOrderNoShared
                : merge.Derived == GpoCopyConflictMode.Skip ? L.GpoCopy.MergeOrderTargetFirst
                : L.GpoCopy.MergeOrderSourceFirst, merge.SourceName, merge.TargetName);
            var options = new List<GpoConflictOption>();
            if (!merge.OrderMixed)
            {
                var derived = merge.Derived == GpoCopyConflictMode.Skip ? skip : replace;
                options.Add(new GpoConflictOption(null, Localizer.Format(L.GpoCopy.ConflictDerived, derived.Text), Localizer[L.GpoCopy.ConflictDerivedHint]));
            }

            options.AddRange([replace, skip, union]);
            ConflictOptions = options;
            SelectedConflict = hadSelection ? options.FirstOrDefault(o => o.Mode == selected) : merge.OrderMixed ? null : options[0];
        }
        else
        {
            IntroText = Localizer.Format(L.GpoCopy.TargetText, _targetName);
            OrderText = string.Empty;
            ConflictOptions = [replace, skip, union];
            SelectedConflict = ConflictOptions.FirstOrDefault(o => o.Mode == (hadSelection ? selected : GpoCopyConflictMode.Replace)) ?? replace;
        }

        UpdateCounts();
    }

    private void UpdateCounts()
    {
        var refused = Items.Count(i => !i.CanCopy);
        RefusedText = refused == 0 ? null : Localizer.Format(L.GpoCopy.RefusedNote, refused);
        if (IsMerge)
        {
            CountText = Localizer.Format(L.GpoCopy.MergeSettingsHeading, Items.Count);
            TooManySourcesText = null;
            return;
        }

        CountText = Localizer.Format(L.GpoCopy.CheckedCount, Items.Count(i => i.IsChecked), Items.Count);
        var sources = SourceCount;
        TooManySourcesText = sources > GpoSettingsCopyRequest.MaxSources
            ? Localizer.Format(L.GpoCopy.TooManySources, sources, GpoSettingsCopyRequest.MaxSources)
            : null;
    }
}
