using AdMng.Core.PowerShell;
using AdMng.Presentation.PowerShell;

namespace AdMng.Presentation.Tests.PowerShell;

/// <summary>The state of the line editor (track R, decision D5): Enter, Shift+Enter, Up / Down, Tab, Ctrl+R and the highlight.</summary>
public sealed class PowerShellInputModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (PowerShellInputModel Input, FakePowerShellSession Session, PowerShellInputHistory History) Create()
    {
        var session = new FakePowerShellSession();
        var history = new PowerShellInputHistory(session.ShouldRemember);
        return (new PowerShellInputModel(() => session, history), session, history);
    }

    private static PowerShellCompletionMatch Match(string text, PowerShellCompletionKind kind = PowerShellCompletionKind.Command) => new(text, text, kind, null);

    // ---- Highlighting off the UI thread ---------------------------------------------------------------------------

    [Fact]
    public async Task ALongText_IsHighlightedOnAPoolThread_AndNextChangeStopsThat()
    {
        var (input, session, _) = Create();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.TokenizeHook = text =>
        {
            // Only the long text is held, on its pool thread; the short one that supersedes it is tokenized at once.
            if (text.Length > PowerShellInputModel.SynchronousHighlightLength)
            {
                started.TrySetResult();
                hold.Task.GetAwaiter().GetResult();
            }
        };
        var longText = "Get-AdmUser " + new string('x', PowerShellInputModel.SynchronousHighlightLength);

        input.SetText(longText, longText.Length);
        var first = input.HighlightTask;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Empty(input.Spans);
        Assert.False(first.IsCompleted);

        // The next keystroke supersedes it: the first result, when it arrives, is dropped.
        input.SetText("Get-AdmUser", 11);
        hold.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal("Get-AdmUser", input.Text);
        Assert.NotEmpty(input.Spans);
        Assert.All(input.Spans, span => Assert.True(span.Start + span.Length <= input.Text.Length));
    }

    [Fact]
    public async Task ALongText_GetsItsSpans_WhenNothingChanged()
    {
        var (input, _, _) = Create();
        var longText = "Get-AdmUser -Name " + new string('x', PowerShellInputModel.SynchronousHighlightLength);

        input.SetText(longText, longText.Length);
        await input.HighlightTask.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(PowerShellSyntaxKind.Command, input.Spans[0].Kind);
        Assert.Equal(longText, input.Text);
    }

    [Fact]
    public void ACommandLine_IsHighlightedAtOnce_AndATextPastTheLimitNotAtAll()
    {
        var (input, _, _) = Create();

        input.SetText("Get-AdmUser", 3);
        Assert.True(input.HighlightTask.IsCompleted);
        Assert.NotEmpty(input.Spans);

        var huge = new string('a', PowerShellInputModel.MaxHighlightedLength + 1);
        input.SetText(huge, 0);
        Assert.Empty(input.Spans);
        Assert.True(input.HighlightTask.IsCompleted);
    }

    [Fact]
    public async Task AClickOnAMatch_TakesThatMatch()
    {
        var (input, session, _) = Create();
        session.Completion = (_, _) => new PowerShellCompletionResult(0, 3, [Match("Get-AdmUser"), Match("Get-AdmGroup"), Match("Get-AdmComputer")]);
        input.SetText("Get", 3);
        await input.CompleteAsync(Ct);
        Assert.True(input.IsCompletionOpen);

        input.AcceptCompletionAt(5);
        Assert.True(input.IsCompletionOpen);
        input.AcceptCompletionAt(2);

        Assert.False(input.IsCompletionOpen);
        Assert.Equal("Get-AdmComputer", input.Text);
        Assert.Equal(15, input.CaretIndex);
        input.AcceptCompletionAt(0);
        Assert.Equal("Get-AdmComputer", input.Text);
    }

    // ---- Text -------------------------------------------------------------------------------------------------------

    [Fact]
    public void SetText_TakesTheTextAndTheCaret_AndHighlightsIt()
    {
        var (input, _, _) = Create();

        input.SetText("Get-AdmUser -Name jan", 8);

        Assert.Equal("Get-AdmUser -Name jan", input.Text);
        Assert.Equal(8, input.CaretIndex);
        Assert.Equal(
            [PowerShellSyntaxKind.Command, PowerShellSyntaxKind.Parameter, PowerShellSyntaxKind.Other],
            input.Spans.Select(span => span.Kind));
        Assert.False(input.IsMultiLine);
        Assert.False(input.IsBlank);
    }

    [Fact]
    public void TheCaret_StaysInsideTheText()
    {
        var (input, _, _) = Create();

        input.SetText("abc", 99);
        Assert.Equal(3, input.CaretIndex);
        input.SetText("abc", -5);
        Assert.Equal(0, input.CaretIndex);
    }

    [Fact]
    public void InsertAndShiftEnter_PutTextAtTheCaret()
    {
        var (input, _, _) = Create();
        input.SetText("ac", 1);

        input.Insert("b");
        Assert.Equal("abc", input.Text);
        Assert.Equal(2, input.CaretIndex);

        input.InsertNewLine();
        Assert.Equal("ab\nc", input.Text);
        Assert.Equal(3, input.CaretIndex);
        Assert.True(input.IsMultiLine);
    }

    [Fact]
    public void ATextPastTheLimit_IsNotHighlighted()
    {
        var (input, _, _) = Create();

        input.SetText(new string('a', PowerShellInputModel.MaxHighlightedLength + 1), 0);

        Assert.Empty(input.Spans);
    }

    [Fact]
    public void Clear_EmptiesTheEditor_AndForgetsWhereTheHistoryWas()
    {
        var (input, _, history) = Create();
        history.Add("one");
        input.MoveUp();
        input.BeginSearch();

        input.Clear();

        Assert.Equal(string.Empty, input.Text);
        Assert.Empty(input.Spans);
        Assert.False(history.IsNavigating);
        Assert.False(input.IsSearching);
    }

    // ---- Enter ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Get-Date", PowerShellEnterAction.Run)]
    [InlineData("Get-Date | Where-Object { $_ }", PowerShellEnterAction.Run)]
    [InlineData("Get-Date |", PowerShellEnterAction.NewLine)]
    [InlineData("if ($x) {", PowerShellEnterAction.NewLine)]
    [InlineData("'an open quote", PowerShellEnterAction.NewLine)]
    [InlineData("", PowerShellEnterAction.Run)]
    public void Enter_RunsACompleteInput_AndInsertsALineForAnIncompleteOne(string text, PowerShellEnterAction expected)
    {
        var (input, _, _) = Create();
        input.SetText(text, text.Length);

        var action = input.PressEnter();

        Assert.Equal(expected, action);
        Assert.Equal(expected == PowerShellEnterAction.NewLine ? text + "\n" : text, input.Text);
    }

    [Fact]
    public void Enter_InTheMiddleOfAnIncompleteInput_InsertsTheLineAtTheCaret()
    {
        var (input, _, _) = Create();
        input.SetText("if ($x) {  }", 11);
        input.SetText("if ($x) {", 9);

        input.PressEnter();

        Assert.Equal("if ($x) {\n", input.Text);
        Assert.Equal(10, input.CaretIndex);
    }

    [Fact]
    public void WhileTheEditorTakesAnAnswer_EnterAlwaysSendsIt_EvenIfItLooksIncomplete()
    {
        var (input, _, _) = Create();
        input.SetMode(PowerShellInputMode.Answer);
        input.SetText("it's", 4);

        Assert.Equal(PowerShellEnterAction.Run, input.PressEnter());
        Assert.Equal("it's", input.Text);
    }

    [Fact]
    public async Task AnAnswer_IsNotHighlighted_NotCompleted_NotSearched_AndUpAndDownDoNothing()
    {
        var (input, session, history) = Create();
        history.Add("older");
        session.Completion = (_, _) => new PowerShellCompletionResult(0, 0, [Match("x")]);
        input.SetMode(PowerShellInputMode.Answer);

        input.SetText("Get-Date", 8);
        await input.CompleteAsync(Ct);
        input.BeginSearch();

        Assert.False(input.IsSecret);
        Assert.True(input.IsAnswering);
        Assert.Empty(input.Spans);
        Assert.Empty(session.CompletionRequests);
        Assert.False(input.IsSearching);
        Assert.False(input.MoveUp());
        Assert.False(input.MoveDown());
        Assert.Equal("Get-Date", input.Text);

        input.SetMode(PowerShellInputMode.Command);
        Assert.Equal(string.Empty, input.Text);
        Assert.False(input.IsAnswering);
    }

    /// <summary>Audit W-08: the secret went through the model's text, a string per key; the model takes no text in the secret mode.</summary>
    [Fact]
    public void ASecret_IsNeverTextOfTheModel()
    {
        var (input, _, _) = Create();
        var changes = new List<string?>();
        input.SetMode(PowerShellInputMode.Secret);
        input.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        input.SetText("S3cret", 6);
        input.Insert("x");

        Assert.True(input.IsSecret);
        Assert.Equal(string.Empty, input.Text);
        Assert.DoesNotContain(nameof(PowerShellInputModel.Text), changes);
    }

    /// <summary>Audit W-09: a question of the running command took away what the operator was typing as the next command.</summary>
    [Theory]
    [InlineData(PowerShellInputMode.Answer)]
    [InlineData(PowerShellInputMode.Secret)]
    public void WhatWasTypedAheadOfAQuestion_ComesBackWhenTheQuestionIsOver(PowerShellInputMode question)
    {
        var (input, _, _) = Create();
        input.SetText("Get-AdmUser jan", 11);

        input.SetMode(question);
        Assert.Equal(string.Empty, input.Text);
        Assert.Equal("Get-AdmUser jan", input.CommandDraft);
        input.SetText("answer", 6);

        input.SetMode(PowerShellInputMode.Command);
        Assert.Equal("Get-AdmUser jan", input.Text);
        Assert.Equal(11, input.CaretIndex);
        Assert.Equal(string.Empty, input.CommandDraft);
    }

    // ---- Up and Down -----------------------------------------------------------------------------------------------

    [Fact]
    public void UpAndDown_WalkTheHistory_AndDownComesBackToWhatWasTyped()
    {
        var (input, _, history) = Create();
        history.Add("one");
        history.Add("two");
        input.SetText("draft", 5);

        Assert.True(input.MoveUp());
        Assert.Equal("two", input.Text);
        Assert.Equal(3, input.CaretIndex);
        Assert.True(input.MoveUp());
        Assert.Equal("one", input.Text);
        Assert.False(input.MoveUp());
        Assert.True(input.MoveDown());
        Assert.Equal("two", input.Text);
        Assert.True(input.MoveDown());
        Assert.Equal("draft", input.Text);
        Assert.False(input.MoveDown());
    }

    [Fact]
    public void InAMultiLineInput_UpAndDownMoveTheCaret_UntilTheFirstAndTheLastLine()
    {
        var (input, _, history) = Create();
        history.Add("older");
        input.SetText("first line\nsecond\nthird line here", "first line\nsecond\nthird line here".Length);

        Assert.True(input.MoveUp());
        Assert.Equal("first line\nsecond".Length, input.CaretIndex);
        Assert.Equal("first line\nsecond\nthird line here", input.Text);
        Assert.True(input.MoveUp());
        Assert.Equal(6, input.CaretIndex);

        // The first line: the next Up is the history.
        Assert.True(input.MoveUp());
        Assert.Equal("older", input.Text);

        history.ResetNavigation();
        input.SetText("a\nbb\nccc", 0);
        Assert.True(input.MoveDown());
        Assert.Equal(2, input.CaretIndex);
        Assert.True(input.MoveDown());
        Assert.Equal(5, input.CaretIndex);
        Assert.False(input.MoveDown());
    }

    [Fact]
    public void TheColumn_IsKeptWhenTheCaretMovesToAShorterLine()
    {
        var (input, _, _) = Create();
        input.SetText("ab\nlonger line", 9);

        input.MoveUp();

        Assert.Equal(2, input.CaretIndex);
    }

    // ---- Completion ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OneMatch_IsPutInAtOnce_AtTheSpanTheEngineGave()
    {
        var (input, session, _) = Create();
        session.Completion = (_, _) => new PowerShellCompletionResult(0, 7, [Match("Get-AdmUser")]);
        input.SetText("Get-Adm -Name x", 7);

        await input.CompleteAsync(Ct);

        Assert.Equal("Get-AdmUser -Name x", input.Text);
        Assert.Equal("Get-AdmUser".Length, input.CaretIndex);
        Assert.False(input.IsCompletionOpen);
        Assert.Equal([("Get-Adm -Name x", 7)], session.CompletionRequests);
    }

    [Fact]
    public async Task SeveralMatches_OpenTheList_TabAndTheArrowsMoveInIt_EnterTakesTheSelected()
    {
        var (input, session, _) = Create();
        session.Completion = (_, _) => new PowerShellCompletionResult(0, 8, [Match("Get-AdmUser"), Match("Get-AdmGroup"), Match("Get-AdmComputer")]);
        input.SetText("Get-Adm", 7);

        await input.CompleteAsync(Ct);
        Assert.True(input.IsCompletionOpen);
        Assert.Equal(0, input.SelectedCompletionIndex);
        Assert.Equal(3, input.Completions.Count);
        Assert.Equal("Get-Adm", input.Text);

        await input.CompleteAsync(Ct);
        Assert.Equal(1, input.SelectedCompletionIndex);
        input.CompletePrevious();
        input.CompletePrevious();
        Assert.Equal(2, input.SelectedCompletionIndex);
        await input.CompleteAsync(Ct);
        Assert.Equal(0, input.SelectedCompletionIndex);
        Assert.True(input.MoveDown());
        Assert.Equal(1, input.SelectedCompletionIndex);
        Assert.True(input.MoveUp());
        Assert.Equal(0, input.SelectedCompletionIndex);
        input.MoveDown();

        Assert.Equal(PowerShellEnterAction.AcceptedCompletion, input.PressEnter());
        Assert.Equal("Get-AdmGroup", input.Text);
        Assert.Equal("Get-AdmGroup".Length, input.CaretIndex);
        Assert.False(input.IsCompletionOpen);
        Assert.Empty(input.Completions);
    }

    [Fact]
    public async Task ACompletionInTheMiddle_ReplacesOnlyItsSpan()
    {
        var (input, session, _) = Create();
        session.Completion = (_, _) => new PowerShellCompletionResult(16, 5, [Match("-Properties", PowerShellCompletionKind.Parameter)]);
        input.SetText("Get-AdmUser jan -Prop | Select-Object Name", 21);

        await input.CompleteAsync(Ct);

        Assert.Equal("Get-AdmUser jan -Properties | Select-Object Name", input.Text);
    }

    [Fact]
    public async Task NoMatch_DoesNothing()
    {
        var (input, _, _) = Create();
        input.SetText("zzz", 3);

        await input.CompleteAsync(Ct);

        Assert.False(input.IsCompletionOpen);
        Assert.Equal("zzz", input.Text);
    }

    [Fact]
    public async Task EscapeAndTyping_CloseTheList_WithoutChangingTheText()
    {
        var (input, session, _) = Create();
        session.Completion = (_, _) => new PowerShellCompletionResult(0, 3, [Match("Get-AdmUser"), Match("Get-AdmGroup")]);
        input.SetText("Get", 3);
        await input.CompleteAsync(Ct);
        Assert.True(input.IsCompletionOpen);

        input.CloseCompletion();
        Assert.False(input.IsCompletionOpen);
        Assert.Equal("Get", input.Text);

        await input.CompleteAsync(Ct);
        input.SetText("Get-", 4);
        Assert.False(input.IsCompletionOpen);
        Assert.Equal("Get-", input.Text);
    }

    [Fact]
    public async Task AResultForATextThatChangedMeanwhile_IsDropped()
    {
        var (input, session, _) = Create();
        var gate = new TaskCompletionSource();
        session.BeforeCompletion = () => gate.Task;
        session.Completion = (_, _) => new PowerShellCompletionResult(0, 3, [Match("Get-AdmUser")]);
        input.SetText("Get", 3);

        var completing = input.CompleteAsync(Ct);
        input.SetText("Get-Date", 8);
        gate.SetResult();
        await completing;

        Assert.Equal("Get-Date", input.Text);
        Assert.False(input.IsCompletionOpen);
    }

    [Fact]
    public async Task ASecondTab_SupersedesTheFirstRequest()
    {
        var (input, session, _) = Create();
        var gate = new TaskCompletionSource();
        var calls = 0;
        session.BeforeCompletion = () => calls++ == 0 ? gate.Task : Task.CompletedTask;
        session.Completion = (_, _) => new PowerShellCompletionResult(0, 3, [Match("Get-AdmUser")]);
        input.SetText("Get", 3);

        var first = input.CompleteAsync(Ct);
        var second = input.CompleteAsync(Ct);
        await second;
        gate.SetResult();
        await first;

        Assert.Equal("Get-AdmUser", input.Text);
    }

    [Fact]
    public async Task WithoutASession_NothingIsCompleted_AndNothingIsHighlighted()
    {
        var input = new PowerShellInputModel(() => null, new PowerShellInputHistory(_ => true));
        input.SetText("Get-Date", 8);

        await input.CompleteAsync(Ct);

        Assert.Empty(input.Spans);
        Assert.Equal(PowerShellEnterAction.Run, input.PressEnter());
        Assert.Equal("Get-Date", input.Text);
    }

    // ---- The search (Ctrl+R) ----------------------------------------------------------------------------------------

    [Fact]
    public void CtrlR_FindsTheNewestLineThatContainsWhatIsTyped_AndAgainTheNextOlderOne()
    {
        var (input, _, history) = Create();
        history.Add("Get-AdmUser anna");
        history.Add("Get-Date");
        history.Add("Get-AdmUser jan");
        input.SetText("draft", 5);

        input.BeginSearch();
        Assert.True(input.IsSearching);
        input.SetSearchQuery("user");
        Assert.Equal("Get-AdmUser jan", input.SearchMatch);
        Assert.False(input.SearchFailed);

        input.BeginSearch();
        Assert.Equal("Get-AdmUser anna", input.SearchMatch);
        input.SearchOlder();
        Assert.Equal("Get-AdmUser anna", input.SearchMatch);

        // The text is not touched until the match is taken.
        Assert.Equal("draft", input.Text);
        Assert.Equal(PowerShellEnterAction.AcceptedSearch, input.PressEnter());
        Assert.Equal("Get-AdmUser anna", input.Text);
        Assert.Equal("Get-AdmUser anna".Length, input.CaretIndex);
        Assert.False(input.IsSearching);
    }

    [Fact]
    public void ASearchWithoutAMatch_Fails_AndEscapeLeavesTheTextAlone()
    {
        var (input, _, history) = Create();
        history.Add("Get-Date");
        input.SetText("draft", 5);
        input.BeginSearch();

        input.SetSearchQuery("zzz");
        Assert.True(input.SearchFailed);
        Assert.Null(input.SearchMatch);

        input.CancelSearch();
        Assert.False(input.IsSearching);
        Assert.Equal("draft", input.Text);

        input.BeginSearch();
        input.SetSearchQuery("zzz");
        input.AcceptSearch();
        Assert.Equal("draft", input.Text);
    }

    [Fact]
    public void UpAndDown_StepTheSearch_AndTheSearchNeverChangesTheHistory()
    {
        var (input, _, history) = Create();
        history.Add("a1");
        history.Add("b");
        history.Add("a2");
        input.BeginSearch();
        input.SetSearchQuery("a");
        Assert.Equal("a2", input.SearchMatch);

        Assert.True(input.MoveUp());

        Assert.Equal("a1", input.SearchMatch);
        Assert.Equal(["a1", "b", "a2"], history.Entries);
    }
}
