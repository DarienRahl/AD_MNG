using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>What <see cref="NewGpoViewModel"/> asks for.</summary>
public enum NewGpoMode
{
    /// <summary>"Nowy obiekt zasad grupy" (optionally linked to a container).</summary>
    New,

    /// <summary>"Kopiuj obiekt zasad grupy": the name of the copy and its permissions.</summary>
    Copy,

    /// <summary>"Zmień nazwę".</summary>
    Rename,
}

/// <summary>
/// The name of a new, copied or renamed GPO (design note 5.2 <c>NewGpoWindow</c>): the name (1–255 characters), the
/// container the new GPO will be linked to, and for a copy the permissions ("domyślne" or "zachowaj istniejące"). The
/// caller plans and previews the operation with <see cref="Result"/>.
/// </summary>
public sealed partial class NewGpoViewModel : WindowViewModelBase
{
    public NewGpoViewModel(DialogServices services, NewGpoMode mode, string name, string? linkTarget = null, string? sourceName = null)
        : base(services)
    {
        Mode = mode;
        LinkTarget = linkTarget;
        SourceName = sourceName;
        Name = name ?? string.Empty;
    }

    public NewGpoMode Mode { get; }

    public override string WindowTitle => Localizer[Mode switch
    {
        NewGpoMode.Copy => L.GroupPolicy.CopyWindowTitle,
        NewGpoMode.Rename => L.GroupPolicy.RenameWindowTitle,
        _ => L.GroupPolicy.NewWindowTitle,
    }];

    /// <summary>The container the new GPO is linked to (display text), null for an unlinked GPO.</summary>
    public string? LinkTarget { get; }

    public bool HasLinkTarget => LinkTarget is not null;

    /// <summary>"Połącz z: Komputery".</summary>
    public string LinkTargetText => LinkTarget is null ? string.Empty : Localizer.Format(L.GroupPolicy.LinkTargetText, LinkTarget);

    /// <summary>The source of a copy or the current name of a rename.</summary>
    public string? SourceName { get; }

    /// <summary>"Źródło: Stacje robocze – Zasady haseł".</summary>
    public string SourceText => SourceName is null ? string.Empty : Localizer.Format(L.GroupPolicy.SourceText, SourceName);

    public bool IsCopy => Mode == NewGpoMode.Copy;

    public bool HasSource => SourceName is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameError), nameof(HasNameError))]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>Copy: keep the source's explicit permissions (<c>-CopyAcl</c>) instead of the default ones.</summary>
    [ObservableProperty]
    public partial bool CopyPermissions { get; set; }

    /// <summary>Why the name is not accepted (null when it is).</summary>
    public string? NameError
    {
        get
        {
            var trimmed = (Name ?? string.Empty).Trim();
            return trimmed.Length == 0 ? Localizer[L.GroupPolicy.NameRequired]
                : trimmed.Length > GroupPolicyPlanner.MaxNameLength ? Localizer.Format(L.GroupPolicy.NameTooLong, GroupPolicyPlanner.MaxNameLength)
                : null;
        }
    }

    public bool HasNameError => NameError is not null;

    /// <summary>The accepted name (set by OK).</summary>
    public string? Result { get; private set; }

    [RelayCommand(CanExecute = nameof(CanOk))]
    private void Ok()
    {
        Result = Name.Trim();
        RequestClose(true);
    }

    private bool CanOk() => NameError is null;

    [RelayCommand]
    private void Cancel() => RequestClose(false);

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(LinkTargetText));
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(NameError));
    }
}
