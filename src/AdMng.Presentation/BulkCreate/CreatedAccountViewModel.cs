using AdMng.Core.Batch;
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

    /// <summary>The outcome of the object over every execution (the status glyph).</summary>
    [ObservableProperty]
    public partial ObjectResultStatus Outcome { get; set; }

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

    /// <summary>The created object's DN (tool tip of the name).</summary>
    [ObservableProperty]
    public partial string? DistinguishedName { get; set; }

    /// <summary>The account has an initial password this window knows.</summary>
    public bool HasPassword => Password is not null;

    /// <summary><em>Pokaż</em> (revealing a password counts as handing it out, as in <em>Resetuj hasło</em>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordText))]
    public partial bool IsPasswordVisible { get; set; }

    /// <summary>The password or <see cref="Mask"/>.</summary>
    public string PasswordText => HasPassword ? (IsPasswordVisible ? Password! : Mask) : string.Empty;

    /// <summary>The password was copied, exported or shown (clears the close guard).</summary>
    [ObservableProperty]
    public partial bool WasHandedOut { get; set; }

    /// <summary><em>Usunięte (cofnięte)</em>: the undo deleted the account.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyPasswordCommand))]
    public partial bool IsUndone { get; set; }

    /// <summary>The initial password (internal: never bound except through <see cref="PasswordText"/>, never logged).</summary>
    internal string? Password { get; private set; }

    /// <summary>The password was generated (the close guard watches it; a shared password was typed by the operator).</summary>
    internal bool IsGeneratedPassword { get; set; }

    /// <summary>Copies a password through <c>SetSecret</c>; true when it reached the clipboard (set by the wizard).</summary>
    internal Func<CreatedAccountViewModel, bool>? CopyRequested { get; set; }

    /// <summary><em>Kopiuj</em>: the password through <c>SetSecret</c>; a copy that reached the clipboard hands it out.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyPassword))]
    private void CopyPassword()
    {
        if (HasPassword && CopyRequested?.Invoke(this) == true)
        {
            WasHandedOut = true;
        }
    }

    private bool CanCopyPassword() => HasPassword && !IsUndone;

    /// <summary>A later execution set a new password (a retry after a failed reset): it replaces the one shown.</summary>
    internal void SetPassword(string? password)
    {
        if (string.Equals(password, Password, StringComparison.Ordinal))
        {
            return;
        }

        Password = password;
        WasHandedOut = false;
        OnPropertyChanged(nameof(HasPassword));
        OnPropertyChanged(nameof(PasswordText));
        CopyPasswordCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Forgets the password (window closed, undone).</summary>
    internal void ClearPassword()
    {
        Password = null;
        IsPasswordVisible = false;
        OnPropertyChanged(nameof(HasPassword));
        OnPropertyChanged(nameof(PasswordText));
        CopyPasswordCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsPasswordVisibleChanged(bool value) => WasHandedOut |= value && HasPassword;

    /// <summary>The row number and the flags – never the password or a name.</summary>
    public override string ToString() => $"Created account row {Row} (password={HasPassword}, handed out={WasHandedOut}, undone={IsUndone})";
}

/// <summary>A column of the pasted sheet the operator adds to the copied table of created accounts (2.7).</summary>
public sealed partial class SheetColumnOption : ObservableObject
{
    /// <param name="index">The column's index in the table.</param>
    /// <param name="header">The header or the letter.</param>
    internal SheetColumnOption(int index, string header)
    {
        Index = index;
        Header = header ?? throw new ArgumentNullException(nameof(header));
    }

    public int Index { get; }

    /// <summary>The header (or the letter of a table without one).</summary>
    public string Header { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>The index and the flag – never the header (it may name a column of the sheet).</summary>
    public override string ToString() => $"Sheet column {Index} (selected={IsSelected})";
}
