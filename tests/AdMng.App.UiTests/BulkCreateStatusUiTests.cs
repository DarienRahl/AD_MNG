using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.BulkCreate;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Services;
using AdMng.Testing;
using AdMng.Testing.BulkCreate;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests;

/// <summary>
/// The status of the bulk creation wizard (track V, stage V2b-4; docs/dev/v-interface-renewal.md "V2b-4 as built"), through
/// the real batch editor in create mode over the fake directory: the wizard has no line of its own - the one line of the
/// window says what the page of the step shown says: the summary of the table, the answer to a press of the rules, the
/// check with Stop - and the views of the wizard have neither a banner nor a bar: what stays in them is notes. A slow domain
/// controller is a call the test holds and releases; every state that is asserted lasts until the test changes it. How the
/// steps render is covered by <see cref="BulkCreateInputUiTests"/> and <see cref="BulkCreateWindowTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class BulkCreateStatusUiTests
{
    /// <summary>How long a held read waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    [Fact]
    public Task TheWizard_HasNoLineOfItsOwn_AndTheSummaryOfTheTableIsInTheLineOfTheWindow() => WpfTestHost.RunAsync(async () =>
    {
        var clipboard = new TableClipboard();
        await using var context = await BulkCreateInputUiTests.CreateContextAsync(clipboard);
        var sample = context.Directory.AddBulkCreateSample(context.Company);
        await context.ConnectAsync();
        var (window, editor) = await BulkCreateInputUiTests.OpenAsync(context, sample.NewEmployeesOu);
        var wizard = editor.BulkCreate!;
        var line = window.PageStatus;

        // One line in the whole window, the wizard's views included - the one of the window, over the status of the window.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        Assert.Same(editor.Status.Current, line.Status);
        Assert.Same(wizard.Status, editor.SourceStatus);
        Assert.Equal((PageStatusKind.Idle, string.Empty), (line.Kind, line.Message));
        AssertNoOldFeedback(window);

        // The summary of the table is said there - and not a second time in the step.
        clipboard.Content = new ClipboardContent(null, null, null, ExcelSamples.OperatorScenarioTsv);
        await wizard.Source.PasteCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal("Wiersze: 5, kolumny: 5 – nagłówki rozpoznane", line.Message);
        Assert.Equal(wizard.Source.Summary, line.Message);
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == line.Message);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task WhatBelongsToTheTable_IsANote_TheReadingWarningsAndTheRepeatedPasteQuestion() => WpfTestHost.RunAsync(async () =>
    {
        var clipboard = new TableClipboard();
        await using var context = await BulkCreateInputUiTests.CreateContextAsync(clipboard);
        var sample = context.Directory.AddBulkCreateSample(context.Company);
        await context.ConnectAsync();
        var (window, editor) = await BulkCreateInputUiTests.OpenAsync(context, sample.NewEmployeesOu);
        var wizard = editor.BulkCreate!;
        var line = window.PageStatus;
        var attention = Application.Current.FindResource("Ad.Note.Attention");
        var source = BulkCreateInputUiTests.Single<BulkSourceView>(window);

        // The question of a repeated paste has three answers - a status has one action - and stays next to the buttons.
        clipboard.Content = new ClipboardContent(null, null, null, ExcelSamples.OperatorScenarioTsv);
        await wizard.Source.PasteCommand.ExecuteAsync(null);
        await wizard.Source.PasteCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(wizard.Source.HasRepeatedPaste);
        Assert.True(source.RepeatedPastePanel.IsVisible);
        Assert.Same(attention, source.RepeatedPasteNote.Style);
        Assert.Equal(wizard.Source.RepeatedPasteQuestion, source.RepeatedPasteText.Text);
        Assert.True(source.AppendRepeatedButton.IsVisible && source.ReplaceWithRepeatedButton.IsVisible && source.CancelRepeatedButton.IsVisible);
        Assert.Equal((PageStatusKind.Idle, wizard.Source.Summary), (line.Kind, line.Message));
        wizard.Source.CancelRepeatedCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.False(source.RepeatedPastePanel.IsVisible);

        // The warnings of reading the sheet: true of the table for as long as it is shown.
        clipboard.Content = new ClipboardContent(null, null, null, ExcelSamples.ErrorValuesTsv);
        await wizard.Source.ReplaceWithPasteCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.NotEmpty(wizard.Source.Warnings);
        Assert.True(source.WarningsPanel.IsVisible);
        Assert.Same(attention, source.WarningsPanel.Style);
        Assert.Equal(wizard.Source.Warnings, source.WarningsList.Items.Cast<string>());
        Assert.Equal((PageStatusKind.Idle, wizard.Source.Summary), (line.Kind, line.Message));
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheRulesStep_SaysTheAnswerToAProfileCommandInTheLine_AndWhyDalejIsNotPossibleIsANote() => WpfTestHost.RunAsync(async () =>
    {
        var clipboard = new TableClipboard();
        await using var context = await BulkCreateInputUiTests.CreateContextAsync(clipboard);
        var sample = context.Directory.AddBulkCreateSample(context.Company);
        await context.ConnectAsync();
        var (window, editor) = await BulkCreateInputUiTests.OpenAsync(context, sample.NewEmployeesOu);
        var wizard = editor.BulkCreate!;
        var line = window.PageStatus;
        clipboard.Content = new ClipboardContent(null, null, null, ExcelSamples.OperatorScenarioTsv);
        await wizard.Source.PasteCommand.ExecuteAsync(null);
        await wizard.NextStepCommand.ExecuteAsync(null);
        await wizard.NextStepCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        var rules = BulkCreateInputUiTests.Single<BulkRulesView>(window);
        var model = wizard.Rules;

        // Step 3 says nothing at rest.
        Assert.Same(model.Status, wizard.Status.Front);
        Assert.Equal((PageStatusKind.Idle, string.Empty), (line.Kind, line.Message));

        // A profile that cannot be saved: what was skipped and why, in the line - and not under the button.
        await model.SaveProfileCommand.ExecuteAsync(null);
        model.NewProfileName = " ";
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var below = TopOf(rules.NamesSection, window);
        await model.ConfirmSaveProfileAsCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(PageStatusKind.Attention, line.Kind);
        Assert.Equal("Nie zapisano profilu: Wpisz nazwę profilu.", line.Message);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.DoesNotContain(UiWait.Descendants<TextBlock>(rules), text => text.Name == "ProfileStatusText");
        Assert.DoesNotContain(
            UiWait.Descendants<TextBlock>(window),
            text => text.IsVisible && !text.IsDescendantOf(line) && text.Text == "Wpisz nazwę profilu.");
        window.UpdateLayout();
        Assert.Equal(below + GrowthOf(line), TopOf(rules.NamesSection, window), 0.5);

        // Why Dalej is not possible is true of the rules below it: a note of the step - the line keeps what it said.
        model.AddRuleCommand.Execute(null);
        model.Rules[^1].Pattern = "Nowy pracownik";
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal("Wybierz atrybut reguły.", model.Error);
        Assert.True(rules.RulesErrorNote.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Error"), rules.RulesErrorNote.Style);
        Assert.Equal(model.Error, rules.RulesErrorText.Text);
        Assert.Equal(PageStatusKind.Attention, line.Kind);
        Assert.Equal("Nie zapisano profilu: Wpisz nazwę profilu.", line.Message);
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheCheck_IsSaidInTheLineWithStop_ItsButtonIsItsCancel_AndAStoppedCheckLastsWithItsAction() => WpfTestHost.RunAsync(async () =>
    {
        var clipboard = new TableClipboard();
        await using var context = await BulkCreateInputUiTests.CreateContextAsync(clipboard);
        var sample = context.Directory.AddBulkCreateSample(context.Company);
        await context.ConnectAsync();
        var (window, editor) = await BulkCreateInputUiTests.OpenAsync(context, sample.NewEmployeesOu);
        var wizard = editor.BulkCreate!;
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        var session = context.Directory.Session;
        clipboard.Content = new ClipboardContent(null, null, null, ExcelSamples.OperatorScenarioTsv);
        await wizard.Source.PasteCommand.ExecuteAsync(null);
        await wizard.NextStepCommand.ExecuteAsync(null);
        await wizard.NextStepCommand.ExecuteAsync(null);

        // The first read of the container is on its way to a slow domain controller: the check is working and waits.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hold = 1;
        session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Get && DistinguishedName.AreEqual(operation.DistinguishedName, sample.NewEmployeesOu)
                && Interlocked.Exchange(ref hold, 0) == 1)
            {
                held.Set();
                release.Wait(HoldLimit);
            }
        };
        try
        {
            var entering = wizard.NextStepCommand.ExecuteAsync(null);
            await UiWait.UntilAsync(() => held.IsSet, "the check waits for the domain controller");
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var check = BulkCreateInputUiTests.Single<BulkCheckView>(window);
            var top = TopOf(check.FilterBox, window);

            // Working, with Stop - the line of the window, and no bar and no Stop button of the step.
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Sprawdzanie wierszy…", line.Message);
            Assert.True(ring.IsActive);
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Zatrzymaj", Assert.IsType<string>(action.Content));
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            Assert.Equal(ActionButtonState.Running, check.RecheckButton.State);
            Assert.Equal(ActionButtonState.Idle, check.RenumberButton.State);
            Assert.Same(wizard.Check.RecheckCommand, check.RecheckButton.Command);
            Assert.Same(wizard.Check.Status.CancelCommand, check.RecheckButton.CancelCommand);
            Assert.Same(wizard.Check.RenumberCommand, check.RenumberButton.Command);
            Assert.DoesNotContain(
                UiWait.Descendants<Button>(window),
                other => other.IsVisible && other is not ActionButton && !other.IsDescendantOf(line)
                    && (AutomationProperties.GetName(other) == "Zatrzymaj" || other.Content as string == "Zatrzymaj"));
            AssertNoOldFeedback(window);
            window.UpdateLayout();
            Assert.Equal(top, TopOf(check.FilterBox, window));

            // Cancel where you started: "Stopping…" on the button and in the line at once - the read that is on its way
            // to the domain controller has not returned.
            ControlHost.Press(check.RecheckButton);
            await UiWait.UntilAsync(() => check.RecheckButton.State == ActionButtonState.Cancelling && wizard.Check.Status.IsCancelling, "the check is stopping");
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal("Zatrzymywanie…", line.Message);
            Assert.Equal(Visibility.Collapsed, action.Visibility);
            Assert.Equal(ActionButtonState.Idle, check.RenumberButton.State);
            release.Set();
            await entering.WaitAsync(HoldLimit);
            session.BeforeOperation = null;
            await UiTestSupport.WaitForIdleAsync(rounds: 2);

            // Stopped: a state of the page that lasts - what may be wrong and the way out - with the action of the button.
            Assert.Equal("Attention", line.Title);
            Assert.StartsWith("Sprawdzanie wierszy zatrzymano: ", line.Message, StringComparison.Ordinal);
            Assert.False(ring.IsActive);
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Sprawdź ponownie", Assert.IsType<string>(action.Content));
            Assert.Same(wizard.Check.RecheckCommand, action.Command);
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            Assert.Equal(ActionButtonState.Idle, check.RecheckButton.State);
            window.UpdateLayout();
            Assert.Equal(top + GrowthOf(line), TopOf(check.FilterBox, window), 0.5);

            // The action of the line checks again: the summary, the rows, nothing that lasts.
            ControlHost.Press(action);
            await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Idle && wizard.Check.Computation is not null, "the check was repeated");
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Equal(wizard.Check.Summary, line.Message);
            Assert.Equal(5, check.CheckGrid.Items.Count);
            Assert.Null(wizard.Check.Status.Lasting);
            window.UpdateLayout();
            Assert.Equal(top, TopOf(check.FilterBox, window), 0.5);
            AssertNoOldFeedback(window);
        }
        finally
        {
            release.Set();
            session.BeforeOperation = null;
        }

        context.AssertHealthy();
    });

    [Fact]
    public Task TheNotesOfTheCheckAndOfTheResults_HaveTheLookOfANote() => WpfTestHost.RunAsync(async () =>
    {
        var dialogs = new ScriptedDialogService { ConfirmAnswer = true };
        var clipboard = new RecordingClipboard();
        await using var context = await UiTestContext.CreateAsync("pl", services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IDialogService>(dialogs));
            services.Replace(ServiceDescriptor.Singleton<IClipboardService>(clipboard));
        });
        var sample = context.Directory.AddBulkCreateSample(context.Company);
        await context.ConnectAsync();
        var editor = context.Services.GetRequiredService<IDialogViewModelFactory>().CreateBatchEditor(new BatchEditorRequest([])
        {
            Create = new BulkCreateRequest(ObjectKind.User, sample.NewEmployeesOu),
        });
        var window = (BatchEditorWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(editor);
        UiTestSupport.ShowOffScreen(window);
        await window.Initialization;
        await UiTestSupport.WaitForIdleAsync();
        var bulk = editor.BulkCreate!;
        var attention = Application.Current.FindResource("Ad.Note.Attention");

        // The problems that concern every row are a note above the grid.
        bulk.ShowSample(BulkCreateSamples.Computation(sample.NewEmployeesOu, sample.GroupNew), BulkCreateSamples.Plan());
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var check = BulkCreateInputUiTests.Single<BulkCheckView>(window);
        Assert.Same(attention, check.RunIssuesBar.Style);
        Assert.Equal((PageStatusKind.Idle, bulk.Check.Summary), (window.PageStatus.Kind, window.PageStatus.Message));

        // The notes of the results - replication, accounts left disabled... - are notes above the accounts, one each.
        await editor.PreviewCommand.ExecuteAsync(null);
        await editor.ExecuteCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => editor.IsResultsStep, "the results of the creation");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var results = Assert.Single(BatchEditorWindowTests.Descendants<BulkResultsView>(window));
        Assert.NotEmpty(bulk.ResultNotes);
        var notes = UiWait.Descendants<ContentControl>(results.ResultNotesList).Where(c => ReferenceEquals(c.Style, attention)).ToList();
        Assert.Equal(bulk.ResultNotes.Count, notes.Count);
        Assert.Equal(
            bulk.ResultNotes,
            notes.Select(note => Assert.IsType<TextBlock>(note.Content).Text));
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The old places of feedback are gone from the whole window - the views of the wizard included: no progress bar, no
    /// banner of the styles of before, one status line.
    /// </summary>
    private static void AssertNoOldFeedback(BatchEditorWindow window)
    {
        var errorBar = Application.Current.FindResource("Ad.ErrorBar");
        var warningBar = Application.Current.FindResource("Ad.WarningBar");
        Assert.Empty(UiWait.Descendants<ProgressBar>(window));
        Assert.DoesNotContain(
            UiWait.Descendants<Border>(window),
            border => ReferenceEquals(border.Style, errorBar) || ReferenceEquals(border.Style, warningBar));
        Assert.Same(window.PageStatus, Assert.Single(UiWait.Descendants<StatusLine>(window)));
    }

    /// <summary>
    /// How much higher than one line the status line is right now: a result that does not fit one line wraps (to three
    /// lines at most), and what lies under the line moves down by as much - by nothing when the result fits.
    /// </summary>
    private static double GrowthOf(StatusLine line)
    {
        var growth = line.ActualHeight - (double)Application.Current.FindResource("Ad.Size.StatusLine");
        Assert.InRange(growth, -0.5, 40.5);
        return growth;
    }

    private static double TopOf(FrameworkElement element, UIElement relativeTo) =>
        element.TranslatePoint(new Point(0, 0), relativeTo).Y;

    /// <summary>Counts the copies (the protected path for passwords) and keeps nothing.</summary>
    private sealed class RecordingClipboard : IClipboardService
    {
        public bool SetText(string text) => true;

        public bool SetSecret(string secret) => true;
    }
}
