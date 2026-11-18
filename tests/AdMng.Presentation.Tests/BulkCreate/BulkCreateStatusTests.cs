using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.BulkCreate;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Tests.GroupPolicy;
using AdMng.Testing.BulkCreate;

namespace AdMng.Presentation.Tests.BulkCreate;

/// <summary>
/// The status line of the bulk creation wizard as a whole (track V, stage V2b-4; docs/dev/v-interface-renewal.md "V2b-4 as
/// built"): the wizard is the source status of the create mode of the batch editor - it has no line of its own - and the
/// page of the step that is shown is in front of it, so the one line of the window says what that page says; the steps of
/// the editor after the wizard (the preview, the execution, the results) are the window's own. The pages are covered by
/// <see cref="BulkSourceStatusTests"/>, <see cref="BulkRulesStatusTests"/> and <see cref="BulkCheckStatusTests"/>.
/// </summary>
public sealed class BulkCreateStatusTests
{
    [Fact]
    public async Task TheWizard_IsTheSourceStatusOfTheEditor_AndTheStepShownIsThePageInFront()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        var editor = wizard.Editor;
        var bulk = wizard.Wizard;

        // The editor's line, the wizard, the page of the step.
        Assert.IsAssignableFrom<IHasPageStatus>(bulk);
        Assert.Same(bulk.Status, editor.SourceStatus);
        Assert.Same(bulk.Status, editor.Status.Front);
        Assert.Same(wizard.Source.Status, bulk.Status.Front);
        Assert.Equal((PageStatusKind.Idle, string.Empty), (editor.Status.Current.Kind, editor.Status.Current.Message));

        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        var summary = wizard.Source.Summary;
        Assert.Equal(summary, editor.Status.Current.Message);

        // Columns has nothing to say: the line of the wizard is empty there, and the summary is back with step 1.
        await bulk.NextStepCommand.ExecuteAsync(null);
        Assert.Equal(BulkCreateStep.Columns, bulk.Step);
        Assert.Null(bulk.Status.Front);
        Assert.Equal((PageStatusKind.Idle, string.Empty), (editor.Status.Current.Kind, editor.Status.Current.Message));
        await bulk.NextStepCommand.ExecuteAsync(null);
        Assert.Same(wizard.Rules.Status, bulk.Status.Front);
        Assert.Equal(BulkCreateStep.Rules, bulk.Step);
        await bulk.NextStepCommand.ExecuteAsync(null);
        Assert.Equal(BulkCreateStep.Check, bulk.Step);
        Assert.Same(bulk.Check.Status, bulk.Status.Front);
        Assert.Equal(bulk.Check.Summary, editor.Status.Current.Message);
        bulk.PreviousStepCommand.Execute(null);
        bulk.PreviousStepCommand.Execute(null);
        bulk.PreviousStepCommand.Execute(null);
        Assert.Same(wizard.Source.Status, bulk.Status.Front);
        Assert.Equal(summary, editor.Status.Current.Message);
    }

    [Fact]
    public async Task AfterTheWizard_TheLineIsTheWindows_AndTheStepOfTheWizardIsBackWhenTheOperatorGoesBack()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        while (wizard.Wizard.Step < BulkCreateStep.Check)
        {
            await wizard.Wizard.NextStepCommand.ExecuteAsync(null);
        }

        Assert.True(wizard.Wizard.IsReadyToPlan, wizard.Wizard.Check.Error);
        var summary = wizard.Wizard.Check.Summary;
        Assert.Equal(summary, wizard.Editor.Status.Current.Message);

        // The preview of the editor is the window's own step: the wizard is no longer in front of the line.
        await wizard.Editor.PreviewCommand.ExecuteAsync(null);
        Assert.Equal(BatchEditorStep.Preview, wizard.Editor.Step);
        Assert.Null(wizard.Editor.Status.Front);
        Assert.Equal(wizard.Editor.PreviewSummary, wizard.Editor.Status.Current.Message);

        // Back: the wizard shows the step it was left in, with what it says.
        wizard.Editor.BackToEditCommand.Execute(null);
        Assert.Same(wizard.Wizard.Status, wizard.Editor.Status.Front);
        Assert.Same(wizard.Wizard.Check.Status, wizard.Wizard.Status.Front);
        Assert.Equal(PageStatusKind.Idle, wizard.Editor.Status.Current.Kind);
        Assert.Equal(wizard.Wizard.Check.Summary, wizard.Editor.Status.Current.Message);
    }

    [Fact]
    public async Task ClosingTheWindow_TakesTheWizardOffTheLine()
    {
        var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        var bulk = wizard.Wizard;

        wizard.Dispose();

        Assert.Null(bulk.Status.Front);
        Assert.Null(wizard.Editor.Status.Front);
    }

    [Fact]
    public async Task TheWizardsHolder_SaysNothingOfItsOwn()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();

        // Whatever the pages do, the holder of the wizard keeps its own state empty: it is the page in front, nothing more.
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        await wizard.Wizard.NextStepCommand.ExecuteAsync(null);

        Assert.Equal(PageStatusKind.Idle, wizard.Wizard.Status.Own.Kind);
        Assert.False(wizard.Wizard.Status.IsWorking);
        Assert.Null(wizard.Wizard.Status.Lasting);
    }

    [Fact]
    public async Task TheCreation_IsTheExecutionOfTheWindow_AndTheCanaryAndTheRestAddUp()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        await wizard.Editor.PreviewCommand.ExecuteAsync(null);
        Assert.True(wizard.Editor.IsPreviewStep, wizard.Editor.Error);
        var seen = new StatusRecorder(wizard.Editor.Status);
        wizard.Harness.Dialogs.TypedConfirmAnswers.Enqueue(true);

        await wizard.Editor.ExecuteCommand.ExecuteAsync(null);

        // The execution of the window from the first moment: the canary object and the rest are two activities of one
        // operation, and the part done only grows from 0 of 5 to 5 of 5.
        var working = seen.Seen.Where(s => s.Kind == PageStatusKind.Working).ToList();
        Assert.StartsWith("Wykonywanie „Utworzenie kont (5) w jednostce „Nowi pracownicy”” w domenie contoso.test: 0 z 5…", working[0].Message, StringComparison.Ordinal);
        Assert.EndsWith(": 5 z 5…", working[^1].Message, StringComparison.Ordinal);
        Assert.Equal(1d, working[^1].Fraction);
        var fractions = working.Select(s => s.Fraction ?? 0d).ToList();
        Assert.Equal(fractions.Order().ToList(), fractions);

        // The result names the domain and where the report is; the wizard is no longer in front of the line.
        var done = wizard.Editor.Status.Current;
        Assert.Equal(PageStatusKind.Done, done.Kind);
        Assert.Equal(
            "Wykonano „Utworzenie kont (5) w jednostce „Nowi pracownicy”” w domenie contoso.test: zmieniono 5 obiektów. Raport jest na liście operacji w tle.",
            done.Message);
        Assert.Equal("Changes.Status.Executed (5)", done.Log.ToString());
        Assert.Null(wizard.Editor.Status.Front);
        Assert.True(wizard.Editor.IsResultsStep);
    }
}
