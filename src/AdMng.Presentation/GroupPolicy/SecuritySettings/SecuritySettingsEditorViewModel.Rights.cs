using AdMng.Core.GroupPolicy.Security;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy.SecuritySettings;

/// <summary>"Prawa użytkownika": [Privilege Rights] of GptTmpl.inf.</summary>
public sealed partial class SecuritySettingsEditorViewModel
{
    private readonly Dictionary<string, UserRightRowViewModel> _rightRows = new(StringComparer.OrdinalIgnoreCase);
    private List<UserRightRowViewModel> _catalogRights = [];

    /// <summary>Every right of the catalog in GPMC's order, then the rights of the file the catalog does not know.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<UserRightRowViewModel> UserRights { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddRightHolderCommand), nameof(AddRightHolderByNameCommand), nameof(RemoveRightHolderCommand))]
    public partial UserRightRowViewModel? SelectedRight { get; set; }

    /// <summary>The holder typed (or chosen from <see cref="PrincipalChoices"/>) for "Dodaj".</summary>
    [ObservableProperty]
    public partial string NewRightHolderText { get; set; } = string.Empty;

    private void BuildRightRows()
    {
        _rightRows.Clear();
        _catalogRights = [.. SecuritySettingsCatalog.UserRights.Select(r => new UserRightRowViewModel(this, r, Localizer[SecuritySettingsText.RightKey(r)], isKnown: true))];
        foreach (var row in _catalogRights)
        {
            _rightRows[row.Constant] = row;
        }

        UserRights = [];
        SelectedRight = null;
    }

    private void RebuildRights()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> current = _template?.Document.GetPrivilegeRights() ?? new Dictionary<string, IReadOnlyList<string>>();
        IReadOnlyDictionary<string, IReadOnlyList<string>> original = _template?.OriginalDocument.GetPrivilegeRights() ?? new Dictionary<string, IReadOnlyList<string>>();
        var rows = new List<UserRightRowViewModel>(_catalogRights);
        foreach (var constant in original.Keys.Concat(current.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Where(k => !_catalogRights.Any(r => r.Constant.Equals(k, StringComparison.OrdinalIgnoreCase))))
        {
            if (!_rightRows.TryGetValue(constant, out var unknown))
            {
                unknown = new UserRightRowViewModel(this, constant, constant, isKnown: false);
                _rightRows[constant] = unknown;
            }

            rows.Add(unknown);
        }

        foreach (var row in rows)
        {
            var holders = current.TryGetValue(row.Constant, out var list) ? list : null;
            row.Update(() =>
            {
                row.IsDefined = holders is not null;
                row.Holders = holders is null ? [] : [.. holders.Select(Principal)];
                row.HoldersText = holders is null ? Localizer[L.SecuritySettings.NotDefined]
                    : holders.Count == 0 ? Localizer[L.SecuritySettings.Nobody]
                    : string.Join(", ", row.Holders.Select(h => h.DisplayName));
            });
            row.IsChanged = !SamePrincipals(original.TryGetValue(row.Constant, out var before) ? before : null, holders);
        }

        if (_template is null)
        {
            rows = [];
        }

        if (!rows.SequenceEqual(UserRights))
        {
            var selected = SelectedRight;
            UserRights = rows;
            SelectedRight = selected is not null && rows.Contains(selected) ? selected : null;
        }
    }

    internal void OnRightDefinedChanged(UserRightRowViewModel row)
    {
        if (CanEditTemplate && _template is { } template)
        {
            var current = template.Document.GetPrivilegeRights().TryGetValue(row.Constant, out var holders) ? holders : [];
            template.Document.SetPrivilegeRight(row.Constant, row.IsDefined ? current : null);
        }

        AfterEdit();
    }

    /// <summary>"Dodaj użytkownika lub grupę…": the object picker (the SID is written).</summary>
    [RelayCommand(CanExecute = nameof(CanEditRight))]
    private async Task AddRightHolderAsync()
    {
        if (SelectedRight is { } row && await PickAsync(groupsOnly: false).ConfigureAwait(true) is { } picked)
        {
            ChangeRight(row, holders => Add(holders, picked.Sid));
        }
    }

    /// <summary>"Dodaj" of the typed box: a listed principal (its SID) or the name as typed.</summary>
    [RelayCommand(CanExecute = nameof(CanEditRight))]
    private void AddRightHolderByName()
    {
        if (SelectedRight is { } row && ResolveTyped(NewRightHolderText, localGroup: false, out _) is { } value && ChangeRight(row, holders => Add(holders, value)))
        {
            NewRightHolderText = string.Empty;
        }
    }

    /// <summary>Removes a holder of the selected right (the right stays defined, also with nobody).</summary>
    [RelayCommand(CanExecute = nameof(CanEditRight))]
    private void RemoveRightHolder(SecurityPrincipalViewModel? principal)
    {
        if (SelectedRight is { } row && principal is not null)
        {
            ChangeRight(row, holders => holders.RemoveAll(h => h.Equals(principal.Value, StringComparison.OrdinalIgnoreCase)) > 0);
        }
    }

    private bool CanEditRight() => CanEditTemplate && SelectedRight is not null;

    /// <summary>Changes the holders of a right (defining it when it was not); false when nothing changed or the value was refused.</summary>
    private bool ChangeRight(UserRightRowViewModel row, Func<List<string>, bool> change)
    {
        if (!CanEditTemplate || _template is not { } template)
        {
            return false;
        }

        var holders = template.Document.GetPrivilegeRights().TryGetValue(row.Constant, out var list) ? list.ToList() : [];
        var wasDefined = list is not null;
        if (!change(holders) && wasDefined)
        {
            return false;
        }

        try
        {
            template.Document.SetPrivilegeRight(row.Constant, holders);
        }
        catch (ArgumentException)
        {
            ShowRefused();
            return false;
        }

        AfterEdit();
        return true;
    }

    /// <summary>Adds a principal unless it is listed (case ignored); true when added.</summary>
    private static bool Add(List<string> principals, string value)
    {
        if (principals.Any(p => p.Equals(value, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        principals.Add(value);
        return true;
    }
}
