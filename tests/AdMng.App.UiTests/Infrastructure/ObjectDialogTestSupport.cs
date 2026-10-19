using System.Windows;
using System.Windows.Media;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Services;
using AdMng.Presentation.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests.Infrastructure;

/// <summary>
/// Dialog service of the object dialog tests: windows are opened by the real <see cref="DialogService"/>, while
/// questions, the object picker and (optionally) property sheets are scripted so that no modal window blocks a test.
/// </summary>
internal sealed class TestDialogService(DialogService inner) : IDialogService
{
    public DialogService Inner { get; } = inner;

    /// <summary>Answer of <see cref="ConfirmAsync"/> and <see cref="ConfirmTypedAsync"/>.</summary>
    public bool ConfirmAnswer { get; set; } = true;

    public List<string> Confirmations { get; } = [];

    public List<string> Errors { get; } = [];

    /// <summary>Answers of <see cref="PickObjectsAsync"/> (nothing is picked when empty).</summary>
    public Queue<IReadOnlyList<string>> PickedObjects { get; } = new();

    /// <summary>Record <see cref="ShowPropertiesAsync"/> instead of opening the property sheet.</summary>
    public bool RecordPropertiesOnly { get; set; }

    public List<string> OpenedProperties { get; } = [];

    public Task ShowInfoAsync(string title, string message) => Task.CompletedTask;

    public Task ShowErrorAsync(string title, string message)
    {
        Errors.Add(message);
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message, bool destructive)
    {
        Confirmations.Add(message);
        return Task.FromResult(ConfirmAnswer);
    }

    public Task<bool> ConfirmTypedAsync(string title, string message, string expectedText)
    {
        Confirmations.Add(message);
        return Task.FromResult(ConfirmAnswer);
    }

    public Task<string?> PickContainerAsync(IDirectorySession session, string? startDn) => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<string>> PickObjectsAsync(IDirectorySession session, IReadOnlyList<ObjectKind> kinds, bool multiSelect) =>
        Task.FromResult(PickedObjects.Count > 0 ? PickedObjects.Dequeue() : (IReadOnlyList<string>)[]);

    public Task ShowPropertiesAsync(string distinguishedName)
    {
        OpenedProperties.Add(distinguishedName);
        return RecordPropertiesOnly ? Task.CompletedTask : Inner.ShowPropertiesAsync(distinguishedName);
    }

    public Task ShowBatchEditorAsync(BatchEditorRequest request) => Task.CompletedTask;

    public Task ShowResetPasswordAsync(IReadOnlyList<string> distinguishedNames) => Inner.ShowResetPasswordAsync(distinguishedNames);

    public Task<string?> ShowNewObjectAsync(ObjectKind kind, string parentDn) => Inner.ShowNewObjectAsync(kind, parentDn);

    public Task ShowFindAsync(string? startDn) => Inner.ShowFindAsync(startDn);

    public Task ShowSessionLogAsync() => Task.CompletedTask;

    public Task ShowRecycleBinAsync(Guid? select = null) => Inner.ShowRecycleBinAsync(select);

    public Task ShowSettingsAsync() => Task.CompletedTask;

    public Task ShowConnectAsync() => Task.CompletedTask;

    public Task<bool> ShowChangeDomainControllerAsync() => Inner.ShowChangeDomainControllerAsync();

    public Task<bool> ShowSavedViewEditorAsync(SavedViewEditorViewModel editor) => Task.FromResult(false);

    public Task<bool> ShowColumnChooserAsync(ColumnChooserViewModel chooser) => Task.FromResult(false);

    public Task<bool> ShowLogonHoursAsync(Presentation.Dialogs.LogonHoursViewModel editor) => Inner.ShowLogonHoursAsync(editor);

    /// <summary>A UI test context whose IDialogService is a <see cref="TestDialogService"/> over the real one.</summary>
    /// <param name="language">UI language ("en" / "pl").</param>
    /// <param name="configure">Further replacements of services (e.g. the sample watchdog client).</param>
    public static async Task<(UiTestContext Context, TestDialogService Dialogs)> CreateContextAsync(string language = "en", Action<IServiceCollection>? configure = null)
    {
        var context = await UiTestContext.CreateAsync(language, services =>
        {
            services.AddSingleton<DialogService>();
            services.Replace(ServiceDescriptor.Singleton<IDialogService>(sp => new TestDialogService(sp.GetRequiredService<DialogService>())));
            configure?.Invoke(services);
        });
        return (context, (TestDialogService)context.Services.GetRequiredService<IDialogService>());
    }
}

/// <summary>Waiting and visual tree helpers of the object dialog tests.</summary>
internal static class UiWait
{
    /// <summary>Lets the dispatcher run until <paramref name="condition"/> holds; fails the test after the timeout.</summary>
    public static async Task UntilAsync(Func<bool> condition, string description, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for: {description}");
            }

            await UiTestSupport.WaitForIdleAsync(rounds: 1);
        }
    }

    /// <summary>Waits for a window's view model initialization (bounded).</summary>
    public static Task InitializedAsync(Task initialization) => initialization.WaitAsync(TimeSpan.FromSeconds(30));

    /// <summary>All visual descendants of <paramref name="root"/> of type <typeparamref name="T"/>.</summary>
    public static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="act"/> on the first visible <typeparamref name="TWindow"/> once a modal call opened it (the
    /// call runs a nested dispatcher loop, which also runs this). A window still open afterwards is closed, so a failing
    /// step can never leave the shared UI thread blocked in a modal loop.
    /// </summary>
    public static async Task DriveModalAsync<TWindow>(Func<TWindow, Task> act)
        where TWindow : Window
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        TWindow? window = null;
        while (window is null && DateTime.UtcNow < deadline)
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 1);
            window = Application.Current.Windows.OfType<TWindow>().FirstOrDefault(w => w.IsVisible);
        }

        if (window is null)
        {
            return;
        }

        try
        {
            await act(window);
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
            }
        }
    }
}
