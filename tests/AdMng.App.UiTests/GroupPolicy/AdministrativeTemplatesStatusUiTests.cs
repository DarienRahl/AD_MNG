using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.GroupPolicy;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static AdMng.App.UiTests.GroupPolicy.GroupPolicyStatusUiSupport;

namespace AdMng.App.UiTests.GroupPolicy;

/// <summary>
/// The status line of the administrative templates editor (track V, stage V2b-3; docs/dev/v-interface-renewal.md "V2b-3 as
/// built"), through the real tab of the main window over the fake directory: one line under the header - the numbers of
/// settings and of changed ones, a Registry.pol that changed on SYSVOL under the edits as the state of the tab with
/// Discard as its action, a save whose plan cannot be made with Retry that leads to the change plan preview - and the two
/// notes under it: where the templates come from, and which Registry.pol cannot be read. A failing domain controller is an
/// exception of the fake session; every state that is asserted lasts until the test changes it. What the editor edits and
/// saves is covered by <see cref="GroupPolicyWindowTests"/> and <see cref="GroupPolicyEditingUiTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class AdministrativeTemplatesStatusUiTests
{
    private const string PreviewPane = "Turn off the preview pane";

    [Fact]
    public Task TheEditor_HasOneStatusLine_UnderItsHeader_WithItsSummary_AndTheTemplatesNoteUnderIt_AndNoOldFeedback() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, sample) = await OpenMainAsync(context, _ => PutCentralStore(context));
        var (editor, view) = await OpenEditorAsync(main, sample);
        var line = view.PageStatus;

        // One status line, named PageStatus, over the status of the document: under the header, above the templates note
        // and the tree.
        AssertTheLineOf(line, view, editor);
        Assert.True(TopOf(view.TitleText, view) < TopOf(line, view), "the status line is not under the header");
        Assert.True(TopOf(line, view) < TopOf(view.DefinitionsPanel, view), "the status line is not above the templates note");
        Assert.True(TopOf(view.DefinitionsPanel, view) < TopOf(view.CategoryTree, view), "the templates note is not above the tree");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);

        // Nothing happens: how many settings there are and how many are configured - in the line, and in the status bar nowhere.
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(editor.LoadedText, line.Message);
        Assert.Equal(AppIcons.StatusInfo, line.IconKey);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(string.Empty, main.TabStatusText.Text);
        Assert.Equal(string.Empty, main.TabSelectionText.Text);
        Assert.False(view.ChangedText.IsVisible);

        // Where the templates come from: an information note under the line, with its two buttons - not a bar, not a card.
        Assert.True(view.DefinitionsPanel.IsVisible);
        Assert.True(view.DefinitionsNote.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Info"), view.DefinitionsNote.Style);
        Assert.Equal(editor.DefinitionsNote, view.DefinitionsText.Text);
        Assert.Contains("central store", view.DefinitionsText.Text, StringComparison.Ordinal);
        Assert.False(view.DefinitionsMissingNote.IsVisible);
        Assert.False(view.FileNote.IsVisible);
        Assert.True(view.UpdateTemplatesButton.IsVisible);
        Assert.True(view.ReloadDefinitionsButton.IsVisible);
        Assert.Same(editor.ReloadDefinitionsCommand, view.ReloadDefinitionsButton.Command);
        Assert.True(LeftOf(view.DefinitionsText, view) < LeftOf(view.ReloadDefinitionsButton, view), "the buttons are not after the sentence");
        AssertNoNotes(view.DocumentNotes);
        AssertNoOldFeedback(view);

        // An edit joins the summary with the number of changed settings - the header says it as it did.
        SelectPreviewPane(editor);
        editor.Editor!.IsEnabled = true;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal("Changed settings: 1 (not saved)", editor.SelectionText);
        Assert.Equal($"{editor.LoadedText} · {editor.SelectionText}", line.Message);
        Assert.True(view.ChangedText.IsVisible);
        Assert.Equal(editor.SelectionText, view.ChangedText.Text);

        // The edits go - the operator is asked first - and the changed count goes with them. (A tab with unsaved edits
        // would make the main window ask again when the test closes it, and that question would outlive the test.)
        await DiscardAsync(editor);
        Assert.False(editor.HasChanges);
        Assert.Equal(editor.LoadedText, line.Message);
        Assert.False(view.ChangedText.IsVisible);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task WhatTheContentLacks_IsSaidInNotes_WhileTheLineKeepsTheSummary() => WpfTestHost.RunAsync(async () =>
    {
        // No central store on the domain controller and no templates on "this computer" (the runner has some), and a
        // Registry.pol of the user configuration that cannot be read.
        await using var context = await UiTestContext.CreateAsync(
            configure: services => services.Replace(ServiceDescriptor.Singleton<ILocalPolicyDefinitions>(new NoLocalPolicyDefinitions())));
        var (main, sample) = await OpenMainAsync(context, gpo => context.Sysvol.Put(gpo.SitePrinters, "User/Registry.pol", [1, 2, 3, 4, 5, 6, 7, 8, 9]));
        var (editor, view) = await OpenEditorAsync(main, sample);
        var line = view.PageStatus;

        // The line keeps the summary: that something is missing is a sentence about the content, not an event.
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(editor.LoadedText, line.Message);
        Assert.Null(editor.Status.Lasting);

        // No templates: an attention note - the information note is not there -, with the buttons that help.
        Assert.True(editor.IsCentralStoreMissing);
        Assert.True(view.DefinitionsMissingNote.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), view.DefinitionsMissingNote.Style);
        Assert.Equal(editor.DefinitionsNote, view.DefinitionsMissingText.Text);
        Assert.Contains("No ADMX templates", view.DefinitionsMissingText.Text, StringComparison.Ordinal);
        Assert.False(view.DefinitionsNote.IsVisible);
        Assert.True(view.UpdateTemplatesButton.IsVisible);
        Assert.True(view.ReloadDefinitionsButton.IsVisible);

        // The file that cannot be read: an attention note of the view, above the templates note.
        Assert.True(view.FileNote.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), view.FileNote.Style);
        Assert.Equal(editor.FileNote, view.FileText.Text);
        Assert.Contains("User/Registry.pol", view.FileText.Text, StringComparison.Ordinal);
        Assert.True(TopOf(line, view) < TopOf(view.FileNote, view), "the file note is not under the status line");
        Assert.True(TopOf(view.FileNote, view) < TopOf(view.DefinitionsPanel, view), "the file note is not above the templates note");
        Assert.True(TopOf(view.DefinitionsPanel, view) < TopOf(view.CategoryTree, view), "the templates note is not above the tree");
        AssertNoOldFeedback(view);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task AFileThatChangedUnderTheEdits_IsTheStateOfTheTab_WithDiscardAsItsAction_UntilTheEditsAreDiscarded() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, sample) = await OpenMainAsync(context, _ => PutCentralStore(context));
        var (editor, view) = await OpenEditorAsync(main, sample);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var restTop = TopOf(view.CategoryTree, view);
        var row = SelectPreviewPane(editor);
        editor.Editor!.IsEnabled = true;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // The header says how many settings are changed, which makes it a little higher than at rest.
        var treeTop = TopOf(view.CategoryTree, view);

        // Another administrator saved the file; the tab is read again (Refresh) on top of the edits.
        var other = new RegistryPolicyDocument();
        PolicyEngine.Write(row.Definition.Policy, PolicySetting.Disabled, other);
        context.Sysvol.Put(sample.SitePrinters, "Machine/Registry.pol", other.ToBytes());
        ControlHost.Press(view.RefreshButton);
        await UiWait.UntilAsync(() => !editor.IsLoading && line.Kind == PageStatusKind.Attention, "the conflict is said");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // The state of the tab: what happened, what it means for the save, what to do - with Discard, and not to be closed.
        Assert.Equal("Attention", line.Title);
        Assert.NotNull(editor.ConflictNote);
        Assert.StartsWith("The settings of this GPO changed on ", line.Message, StringComparison.Ordinal);
        Assert.Equal(editor.ConflictNote, line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Discard changes", Assert.IsType<string>(action.Content));
        Assert.Same(editor.DiscardCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.True(editor.HasChanges); // the edits are kept
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(view),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == editor.ConflictNote);
        AssertNoOldFeedback(view);
        view.UpdateLayout();
        Assert.Equal(treeTop + GrowthOf(line), TopOf(view.CategoryTree, view), 0.5);

        // Discard, pressed in the line: the operator is asked first; then the current file is shown and the summary is back.
        var drive = UiWait.DriveModalAsync<MessageDialogWindow>(dialog =>
        {
            dialog.ViewModel.ConfirmCommand.Execute(null);
            return Task.CompletedTask;
        });
        ControlHost.Press(action);
        await drive;
        await UiWait.UntilAsync(() => !editor.IsLoading && line.Kind == PageStatusKind.Idle, "the edits were discarded");
        Assert.Null(editor.ConflictNote);
        Assert.False(editor.HasChanges);
        Assert.Equal(editor.LoadedText, line.Message);
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        view.UpdateLayout();
        Assert.Equal(restTop, TopOf(view.CategoryTree, view), 0.5);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task ASaveWhosePlanCannotBeMade_SaysSoInTheLine_AndRetryLeadsToTheChangePlanPreview() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var (main, sample) = await OpenMainAsync(context, _ => PutCentralStore(context));
        var (editor, view) = await OpenEditorAsync(main, sample);
        var line = view.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var session = context.Directory.Session;
        var restTop = TopOf(view.CategoryTree, view);
        var row = SelectPreviewPane(editor);
        editor.Editor!.IsEnabled = true;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var summary = line.Message;

        // The header says how many settings are changed, which makes it a little higher than at rest.
        var treeTop = TopOf(view.CategoryTree, view);
        Assert.True(view.SaveButton.IsEnabled);

        // The domain controller does not answer the read of the GPO any more: the plan of the save cannot be made.
        var gpo = sample.SitePrinters.ToString("B");
        var fail = 1;
        session.BeforeOperation = operation =>
        {
            if (Volatile.Read(ref fail) == 1 && operation.DistinguishedName.Contains(gpo, StringComparison.OrdinalIgnoreCase))
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer");
            }
        };
        ControlHost.Press(view.SaveButton);
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Error && !editor.Status.IsWorking && !editor.IsSaving, "the plan could not be made");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Said where the save was started - no dialog -: what was not done, why, what to do, the code. The editor is as it
        // was, with its edits, so the message can be closed; Retry makes the plan of the same save again.
        Assert.Equal("Error", line.Title);
        Assert.StartsWith($"Administrative templates of {editor.GpoName} – nothing was changed: ", line.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Press Retry to try again. (ServerUnavailable)", line.Message, StringComparison.Ordinal);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Empty(Application.Current.Windows.OfType<MessageDialogWindow>());
        Assert.True(editor.HasChanges);
        Assert.True(view.SaveButton.IsEnabled);
        view.UpdateLayout();
        Assert.Equal(treeTop + GrowthOf(line), TopOf(view.CategoryTree, view), 0.5);
        AssertNoOldFeedback(view);

        // The domain controller answers again. Retry, pressed in the line: the plan is made and shown in the change plan
        // preview - while that window is open the line of the tab says its summary again - and executed there.
        Volatile.Write(ref fail, 0);
        var drive = UiWait.DriveModalAsync<ChangePlanPreviewWindow>(async preview =>
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Equal(PageStatusKind.Idle, line.Kind);
            Assert.Equal(summary, line.Message);
            await preview.ViewModel.ExecuteCommand.ExecuteAsync(null);
            await UiWait.UntilAsync(() => preview.ViewModel.Step == PlanPreviewStep.Results, "the results of the save");
            Assert.False(preview.ViewModel.ResultHasFailures);
            preview.ViewModel.CloseCommand.Execute(null);
        });
        ControlHost.Press(action);
        await drive;
        await UiWait.UntilAsync(() => !editor.IsLoading && !editor.HasChanges && line.Kind == PageStatusKind.Idle, "the save was made and the tab read again");
        session.BeforeOperation = null;
        var written = RegistryPolicyDocument.Parse(context.Sysvol.Get(sample.SitePrinters, "Machine/Registry.pol"));
        Assert.Equal(PolicyState.Enabled, PolicyEngine.Read(row.Definition.Policy, written.View).State);
        Assert.Equal(editor.LoadedText, line.Message);
        view.UpdateLayout();
        Assert.Equal(restTop, TopOf(view.CategoryTree, view), 0.5);

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    /// <summary>"This computer" has no templates: the editor of a domain without a central store lists registry values only.</summary>
    private sealed class NoLocalPolicyDefinitions : ILocalPolicyDefinitions
    {
        public string? Folder => null;

        public IPolicyDefinitionSource? Source => null;
    }

    /// <summary>"Discard changes" of the editor, answered with the confirmation the operator gives.</summary>
    private static async Task DiscardAsync(AdministrativeTemplatesEditorViewModel editor)
    {
        var drive = UiWait.DriveModalAsync<MessageDialogWindow>(dialog =>
        {
            dialog.ViewModel.ConfirmCommand.Execute(null);
            return Task.CompletedTask;
        });
        await editor.DiscardCommand.ExecuteAsync(null);
        await drive;
        await UiWait.UntilAsync(() => !editor.IsLoading && !editor.Status.IsWorking, "the edits were discarded");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
    }

    private static void PutCentralStore(UiTestContext context)
    {
        foreach (var (path, content) in SampleAdministrativeTemplates.Files())
        {
            context.Sysvol.PutPolicyDefinition(path, content);
        }
    }

    private static Task<(AdministrativeTemplatesEditorViewModel Editor, AdministrativeTemplatesEditorView View)> OpenEditorAsync(MainWindow main, GroupPolicyManagementSample sample) =>
        OpenAsync<AdministrativeTemplatesEditorViewModel, AdministrativeTemplatesEditorView>(
            main, new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.AdministrativeTemplates) { GpoId = sample.SitePrinters, Name = "Lokacja Warszawa – Drukarki" });

    /// <summary>"Turn off the preview pane" of the computer configuration, selected in the editor.</summary>
    private static PolicyRowViewModel SelectPreviewPane(AdministrativeTemplatesEditorViewModel editor)
    {
        editor.SelectedNode = editor.Roots.Single(r => r.Scope == PolicyClass.Machine).Children.Single(c => c.Name == "System");
        var row = editor.Policies.Single(p => p.Name == PreviewPane);
        editor.SelectedPolicy = row;
        return row;
    }
}
