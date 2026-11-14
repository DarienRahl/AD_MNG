using System.Windows;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.GroupPolicy;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.Copy;
using AdMng.Presentation.Localization;
using Microsoft.Extensions.DependencyInjection;
using static AdMng.App.UiTests.GroupPolicy.GroupPolicyStatusUiSupport;

namespace AdMng.App.UiTests.GroupPolicy;

/// <summary>
/// The feedback of the three Group Policy dialogs that start no operation (track V, stage V2b-3;
/// docs/dev/v-interface-renewal.md "V2b-3 as built"), through the real windows: the WMI filter editor, the window that
/// copies settings or merges two GPOs, and the name of a new, copied or renamed GPO collect input and hand it to their
/// opener, which plans it and says how that went in the status line of its document - so they have no status line. What
/// they say is true of their fields for as long as it is shown, and stands next to them as a note. No banner of the styles
/// of before is left. How the editor, the copy and the name work is covered by <see cref="GroupPolicyWindowTests"/> and
/// <see cref="GpoCopySettingsUiTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GroupPolicyDialogNotesUiTests
{
    [Fact]
    public Task TheWmiFilterEditor_HasNoStatusLine_AndTheQueriesThatCouldNotBeReadAreANoteAboveThem() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var localizer = context.Services.GetRequiredService<ILocalizer>();
        var gp = context.Services.GetRequiredService<GroupPolicyServices>();
        var windows = context.Services.GetRequiredService<IWindowService>();

        // A filter whose stored queries are in order: nothing to say besides what is typed.
        var fine = new WmiFilterEditorViewModel(gp.Ui, GroupPolicySamples.Filter());
        var editor = (WmiFilterEditorWindow)windows.CreateWindow(fine);
        UiTestSupport.ShowOffScreen(editor);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.IsNotAssignableFrom<IHasPageStatus>(fine);
        Assert.False(editor.UnparsedNote.IsVisible);
        Assert.True(editor.OkButton.IsEnabled);
        AssertNoStatusAndNoOldFeedback(editor);
        editor.Close();

        // A filter whose queries could not be parsed: a note over the queries - they are replaced when it is saved.
        var raw = GroupPolicySamples.Filter() with { Queries = [], RawQueries = "1;3;10;37;WQL;root\\CIMv2;SELECT * FROM Win32_OperatingSystem;" };
        var broken = new WmiFilterEditorViewModel(gp.Ui, raw);
        var window = (WmiFilterEditorWindow)windows.CreateWindow(broken);
        UiTestSupport.ShowOffScreen(window);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.True(broken.HasUnparsedQueries);
        Assert.True(window.UnparsedNote.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), window.UnparsedNote.Style);
        Assert.True(window.UnparsedNoteText.IsDescendantOf(window.UnparsedNote));
        Assert.Equal(localizer[L.GroupPolicy.WmiUnparsedNote], window.UnparsedNoteText.Text);
        Assert.True(TopOf(window.DescriptionBox, window) < TopOf(window.UnparsedNote, window), "the note is not under the description");
        Assert.True(TopOf(window.UnparsedNote, window) < TopOf(window.QueriesList, window), "the note is not above the queries");
        Assert.False(window.UnparsedNote.IsTabStop);

        // Why OK is disabled stays the validation text of the footer, next to the buttons.
        window.NameBox.Text = string.Empty;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.False(window.OkButton.IsEnabled);
        Assert.Equal(broken.ValidationError, window.ValidationText.Text);
        Assert.True(window.OkButton.IsDefault);
        Assert.True(window.CancelButton.IsCancel);
        AssertNoStatusAndNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheCopyWindow_HasNoStatusLine_TooManySourcesAndWhatBlocksTheMergeAreErrorNotes_AndWhatTheMergeChangesIsAnAttentionNote() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var gp = context.Services.GetRequiredService<GroupPolicyServices>();
        var windows = context.Services.GetRequiredService<IWindowService>();

        // Copy: settings of more source GPOs than one copy takes - why OK is disabled, a note over the list.
        var many = Enumerable.Range(0, GpoSettingsCopyRequest.MaxSources + 1)
            .Select(i => new GpoCopyItem(
                new GpoUnitSelection(Guid.NewGuid(), $"S|Computer|System Access|Setting{i}"), $"Zasady {i}", $"Ustawienie {i}", null, "1", null))
            .ToList();
        var copy = new GpoCopySettingsViewModel(gp.Ui, many, "Stacje robocze – Pulpit zdalny");
        var window = (GpoCopySettingsWindow)windows.CreateWindow(copy);
        UiTestSupport.ShowOffScreen(window);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.IsNotAssignableFrom<IHasPageStatus>(copy);
        Assert.True(copy.HasTooManySources);
        Assert.True(window.TooManySourcesBar.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Error"), window.TooManySourcesBar.Style);
        Assert.True(window.TooManySourcesText.IsDescendantOf(window.TooManySourcesBar));
        Assert.Equal(copy.TooManySourcesText, window.TooManySourcesText.Text);
        Assert.True(TopOf(window.TooManySourcesBar, window) < TopOf(window.ItemsList, window), "the note is not above the list");
        Assert.False(window.OkButton.IsEnabled);
        Assert.False(window.TooManySourcesBar.IsTabStop);
        AssertNoStatusAndNoOldFeedback(window);

        // Fewer settings checked: the note goes with what it was about, and OK works again.
        window.CheckNoneButton.Command.Execute(null);
        foreach (var item in copy.Items.Take(2))
        {
            item.IsChecked = true;
        }

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.False(window.TooManySourcesBar.IsVisible);
        Assert.True(window.OkButton.IsEnabled);
        window.Close();

        // Merge: a setting that cannot be copied blocks it (an error note), the differences to check are an attention note.
        var source = Guid.NewGuid();
        var items = new List<GpoCopyItem>
        {
            new(new GpoUnitSelection(source, "S|Computer|System Access|MinimumPasswordLength"), "Stacje robocze – Zasady haseł", "Minimalna długość hasła", null, "12 znaków", null),
            new(new GpoUnitSelection(source, "I|Computer|Groups|User|serwis_łódź", "{7A1B2C3D-4E5F-4061-8273-94A5B6C7D8E9}"), "Stacje robocze – Zasady haseł", "serwis_łódź", null,
                "(ustawione – ukryte)", GpoUnitRefusal.Secret),
        };
        var merge = new GpoMergeInfo("Stacje robocze – Zasady haseł", "Stacje robocze – Pulpit zdalny", null, true, true, ["Obiekty GPO mają różne filtry WMI."]);
        var merging = new GpoCopySettingsViewModel(gp.Ui, items, merge);
        var mergeWindow = (GpoCopySettingsWindow)windows.CreateWindow(merging);
        UiTestSupport.ShowOffScreen(mergeWindow);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.True(mergeWindow.BlockingBar.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Error"), mergeWindow.BlockingBar.Style);
        Assert.True(mergeWindow.BlockingList.IsDescendantOf(mergeWindow.BlockingBar));
        Assert.Single(mergeWindow.BlockingList.Items);
        Assert.True(mergeWindow.WarningsBar.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), mergeWindow.WarningsBar.Style);
        Assert.True(mergeWindow.WarningsList.IsDescendantOf(mergeWindow.WarningsBar));
        Assert.Single(mergeWindow.WarningsList.Items);
        Assert.True(TopOf(mergeWindow.BlockingBar, mergeWindow) < TopOf(mergeWindow.WarningsBar, mergeWindow), "the two notes are not one under the other");
        Assert.True(TopOf(mergeWindow.WarningsBar, mergeWindow) < TopOf(mergeWindow.ItemsList, mergeWindow), "the notes are not above the list");
        Assert.False(mergeWindow.OkButton.IsEnabled);
        Assert.True(mergeWindow.OkButton.IsDefault);
        Assert.True(mergeWindow.CancelButton.IsCancel);
        AssertNoStatusAndNoOldFeedback(mergeWindow);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheNameOfAGpo_HasNoStatusLine_AndKeepsItsValidationText() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var gp = context.Services.GetRequiredService<GroupPolicyServices>();
        var name = new NewGpoViewModel(gp.Ui, NewGpoMode.New, "Nowy obiekt zasad grupy", "Komputery");
        var window = (NewGpoWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(name);
        UiTestSupport.ShowOffScreen(window);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // The dialog only asks for a name; "why OK is disabled" is the validation text under the box, as it was.
        Assert.IsNotAssignableFrom<IHasPageStatus>(name);
        Assert.False(window.NameErrorText.IsVisible);
        name.Name = "  ";
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.True(window.NameErrorText.IsVisible);
        Assert.Equal(name.NameError, window.NameErrorText.Text);
        Assert.False(window.OkButton.IsEnabled);
        Assert.True(window.OkButton.IsDefault);
        Assert.True(window.CancelButton.IsCancel);
        AssertNoStatusAndNoOldFeedback(window);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>No progress bar - and no status line: the dialog has no status. (The banners of before cannot come back: XamlSourceTests.)</summary>
    private static void AssertNoStatusAndNoOldFeedback(Window window)
    {
        Assert.Empty(UiWait.Descendants<ProgressBar>(window));
        Assert.Empty(UiWait.Descendants<StatusLine>(window));
    }
}
