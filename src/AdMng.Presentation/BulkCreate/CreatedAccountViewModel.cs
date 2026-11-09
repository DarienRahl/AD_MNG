using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.BulkCreate;

/// <summary>
/// One created account of the results (docs/dev/phase3-bulk-create.md 2.7, owner B4; modelled on
/// <c>ResetPasswordViewModel.PasswordResultViewModel</c>): status, the Excel row, name, login, UPN, e-mail, the initial
/// password – masked, revealed per row, copied through <c>IClipboardService.SetSecret</c> – and the message. The password
/// lives only here and in the plans and results (rule 2); <see cref="ToString"/> never prints it. Machine passwords of
/// computers are never listed.
/// </summary>
/// <remarks>Contract stub: B4 fills it from the accumulated results and writes the commands.</remarks>
public sealed partial class CreatedAccountViewModel : ObservableObject
{
    /// <summary>What a hidden password shows.</summary>
    public const string Mask = "••••••••";

    /// <param name="row">The wizard's row number.</param>
    /// <param name="password">The initial password (null: none, or an account without a password).</param>
    internal CreatedAccountViewModel(int row, string? password)
    {
        Row = row;
        Password = password;
    }

    /// <summary>The wizard's row number.</summary>
    public int Row { get; }

    /// <summary>The row of the Excel sheet (<em>Nr</em>).</summary>
    [ObservableProperty]
    public partial int ExcelRow { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Login { get; set; }

    [ObservableProperty]
    public partial string? Upn { get; set; }

    [ObservableProperty]
    public partial string? Mail { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    /// <summary>The account has an initial password this window knows.</summary>
    public bool HasPassword => Password is not null;

    /// <summary><em>Pokaż</em>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordText))]
    public partial bool IsPasswordVisible { get; set; }

    /// <summary>The password or <see cref="Mask"/>.</summary>
    public string PasswordText => HasPassword ? (IsPasswordVisible ? Password! : Mask) : string.Empty;

    /// <summary>The password was copied, exported or shown and confirmed as handed out (clears the close guard).</summary>
    [ObservableProperty]
    public partial bool WasHandedOut { get; set; }

    /// <summary><em>Usunięte (cofnięte)</em>: the undo deleted the account.</summary>
    [ObservableProperty]
    public partial bool IsUndone { get; set; }

    /// <summary>The initial password (internal: never bound except through <see cref="PasswordText"/>, never logged).</summary>
    internal string? Password { get; private set; }

    /// <summary><em>Kopiuj</em>: the password through <c>SetSecret</c>.</summary>
    [RelayCommand]
    private void CopyPassword() => throw new NotImplementedException("F3 B4: CreatedAccountViewModel.CopyPassword (2.7).");

    /// <summary>Forgets the password (window closed, undone).</summary>
    internal void ClearPassword()
    {
        Password = null;
        IsPasswordVisible = false;
        OnPropertyChanged(nameof(HasPassword));
        OnPropertyChanged(nameof(PasswordText));
    }

    /// <summary>The row number and the flags – never the password or a name.</summary>
    public override string ToString() => $"Created account row {Row} (password={HasPassword}, handed out={WasHandedOut}, undone={IsUndone})";
}
