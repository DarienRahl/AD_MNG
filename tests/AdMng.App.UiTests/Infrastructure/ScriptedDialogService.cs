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

    public Task ShowInfoAsync(string title, string message) => Task.CompletedTask;

    public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

    public Task<bool> ConfirmAsync(string title, string message, bool destructive)
    {
        Confirmations.Add(title);
        return Task.FromResult(ConfirmAnswer);
    }

    public Task<bool> ConfirmTypedAsync(string title, string message, string expectedText)
    {
        Confirmations.Add(title);
        return Task.FromResult(ConfirmAnswer);
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

    public Task ShowRecycleBinAsync(Guid? select = null) => Task.CompletedTask;

    public Task ShowSettingsAsync() => Task.CompletedTask;

    public Task ShowConnectAsync() => Task.CompletedTask;

    public Task<bool> ShowChangeDomainControllerAsync() => Task.FromResult(false);

    public Task<bool> ShowSavedViewEditorAsync(SavedViewEditorViewModel editor) => Task.FromResult(false);

    public Task<bool> ShowColumnChooserAsync(ColumnChooserViewModel chooser) => Task.FromResult(false);
}
