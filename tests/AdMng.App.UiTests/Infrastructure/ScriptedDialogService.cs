using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Services;
using AdMng.Presentation.Views;

namespace AdMng.App.UiTests.Infrastructure;

/// <summary>Dialog service with scripted answers for UI tests that must not open modal windows.</summary>
internal sealed class ScriptedDialogService : IDialogService
{
    /// <summary>Answer of <see cref="ConfirmAsync"/> and <see cref="ConfirmTypedAsync"/>.</summary>
    public bool ConfirmAnswer { get; set; }

    /// <summary>Titles of the confirmations asked so far.</summary>
    public List<string> Confirmations { get; } = [];

    /// <summary>The texts of the confirmations asked so far, in the same order as <see cref="Confirmations"/>.</summary>
    public List<string> ConfirmationMessages { get; } = [];

    /// <summary>The texts of the notices shown so far (<see cref="ShowInfoAsync"/>).</summary>
    public List<string> InfoMessages { get; } = [];

    public Task ShowInfoAsync(string title, string message)
    {
        InfoMessages.Add(message);
        return Task.CompletedTask;
    }

    /// <summary>The texts of the error dialogs shown so far (<see cref="ShowErrorAsync"/>).</summary>
    public List<string> ErrorMessages { get; } = [];

    public Task ShowErrorAsync(string title, string message)
    {
        ErrorMessages.Add(message);
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message, bool destructive)
    {
        Confirmations.Add(title);
        ConfirmationMessages.Add(message);
        return Task.FromResult(ConfirmAnswer);
    }

    public Task<bool> ConfirmTypedAsync(string title, string message, string expectedText)
    {
        Confirmations.Add(title);
        return Task.FromResult(ConfirmAnswer);
    }

    /// <summary>Answers of <see cref="AskCloseAsync"/>, in order ("Don't close" when empty).</summary>
    public Queue<CloseAnswer> CloseAnswers { get; } = new();

    /// <summary>The close questions asked so far: the message and whether "Close and continue in the background" was offered.</summary>
    public List<(string Message, bool CanContinue)> CloseQuestions { get; } = [];

    public Task<CloseAnswer> AskCloseAsync(string title, string message, bool canContinue)
    {
        CloseQuestions.Add((message, canContinue));
        return Task.FromResult(CloseAnswers.Count > 0 ? CloseAnswers.Dequeue() : CloseAnswer.KeepOpen);
    }

    public Task<string?> PickContainerAsync(IDirectorySession session, string? startDn) => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<string>> PickObjectsAsync(IDirectorySession session, IReadOnlyList<ObjectKind> kinds, bool multiSelect) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task ShowPropertiesAsync(string distinguishedName) => Task.CompletedTask;

    public Task ShowBatchEditorAsync(BatchEditorRequest request) => Task.CompletedTask;

    public Task ShowResetPasswordAsync(IReadOnlyList<string> distinguishedNames) => Task.CompletedTask;

    public Task<string?> ShowNewObjectAsync(ObjectKind kind, string parentDn) => Task.FromResult<string?>(null);

    public Task ShowFindAsync(string? startDn) => Task.CompletedTask;

    public Task ShowSessionLogAsync() => Task.CompletedTask;

    public Task ShowPowerShellAsync() => Task.CompletedTask;

    public List<AdMng.Core.PowerShell.PowerShellGridTable> PowerShellGrids { get; } = [];

    public Task ShowPowerShellGridAsync(AdMng.Core.PowerShell.PowerShellGridTable table, Action? closed = null)
    {
        PowerShellGrids.Add(table);
        return Task.CompletedTask;
    }

    public Task ShowProblemReportAsync() => Task.CompletedTask;

    public Task ShowRecycleBinAsync(Guid? select = null) => Task.CompletedTask;

    public Task ShowAccountReviewAsync() => Task.CompletedTask;

    public Task ShowSettingsAsync() => Task.CompletedTask;

    public Task ShowConnectAsync() => Task.CompletedTask;

    public Task<bool> ShowChangeDomainControllerAsync() => Task.FromResult(false);

    public Task<bool> ShowSavedViewEditorAsync(SavedViewEditorViewModel editor) => Task.FromResult(false);

    public Task<bool> ShowFilterEditorAsync(Presentation.Filters.FilterEditorViewModel editor) => Task.FromResult(false);

    public Task ShowSavedFiltersAsync() => Task.CompletedTask;

    public Task<bool> ShowColumnChooserAsync(ColumnChooserViewModel chooser) => Task.FromResult(false);

    public Task<bool> ShowLogonHoursAsync(Presentation.Dialogs.LogonHoursViewModel editor) => Task.FromResult(false);

    public Task<bool> ShowWatchdogCertificateTrustAsync(Presentation.Watchdog.WatchdogCertificateTrustViewModel dialog) => Task.FromResult(false);
}
