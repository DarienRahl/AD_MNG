# V2 feedback controls - how to write the next view

For whoever writes or converts a view after the sweep of stage V2: what the pieces that say "what this page is doing" are, which
message goes where, the recipe for a page, the tests to copy and what is still open. It is a reader's guide, not the design: the
reasons, the alternatives and the as-built record of every window are in
[v-interface-renewal.md](v-interface-renewal.md) (sections *6.7 Feedback, one place each* and *V2 - Feedback*, blocks *V2a as built*,
*V2b-1 as built* with its **Foundations**, *V2b-2 as built* with its **Dialogs**, *V2b-3 as built* with its **Group Policy documents**
and *V2b-4 as built* with its **Areas**). Where this page and the as-built blocks disagree, the blocks win.

The one rule behind all of it: **a page says what it did or is doing in one place, its status line.** Nothing of it is a banner, a
progress bar under the header, a text in the footer or a toast of the same window. There are no banner styles any more
(`Themes/Controls.xaml` lost them in V2b-5a), and `XamlSourceTests` fails a view that names one.

## The pieces

| Piece | Where | What it is | What it is for |
|---|---|---|---|
| `PageStatus` | `Presentation/Feedback` | An immutable value: a kind (`Idle` - the summary - or `Working`, `Done`, `Attention`, `Error`), the text, how far (`PageProgress`: unknown or a fraction), at most one action (`PageStatusAction`: label and command), `CanClose`, and the copy for the diagnostic log (`PageStatusLog`: a resource key, counts, a `ResultCode`). The title ("Error", "Gotowe") is not part of it: the view takes it from the kind. | What a page says, as data a test can compare. |
| `PageStatusHolder` | `Presentation/Feedback` | The observable holder a page view model owns (`IHasPageStatus.Status`); the view binds to `Status.Current`. `SetIdle` (the summary, shown whenever nothing else is), `SetLasting` (a state that lasts), `Show` (a result), `Clear`, `Begin` (an operation), `Front` (the page in front of a window of pages), `CloseCommand`, `CancelCommand`, `IsWorking`, `IsCancelling`. | The one status of one page. Created by the view model, dies with it; there is no hub and no service. |
| `OperationScope` | returned by `Begin` | One running operation: `Token`, `Report` (a new text or count, from any thread), `Cancel`, `Complete`, `CompleteDone` / `CompleteAttention` / `CompleteError`, `IsEnded`, `IsCancelling`, `Dispose`. | "Working" exists only through a scope. A scope that is disposed without a completion ends as the summary, so no path out of a method leaves a page "working". |
| `ExecutionStatus` | `Presentation/Changes` | The execution of a change plan as an operation: working from the first moment ("Executing ... 12 of 300..."), Stop that cancels the activity, results that name the domain. | Every window that executes a plan through the coordinator, so every window says the same. It only says what happens; the pipeline (rule 5) is untouched. |
| `PageStatusText` | `Presentation/Feedback` | Text helpers: `Failure(tried, reason, next, code)`, `Progress(localizer, done, total)` ("12 of 300"), `Objects` / `Count` (the plural forms). | One shape of an error: "what we tried: why it failed. What to do next. (code)". |
| `c:StatusLine` | `App/Controls`, style `Ad.StatusLine` | Shows a `PageStatus` in a line of a fixed height (the summary and a running operation: one line; a result: up to three lines, then the tool tip): the picture of the severity (a ring while it works), the fixed title, the text, the action as a link, a close button where `CanClose`. A live region: polite for the summary, working and done, assertive for attention and error. | The one place. |
| `c:ActionButton` | `App/Controls`, styles `Ad.ActionButton`, `Ad.AccentActionButton`, `Ad.DialogActionButton`, `Ad.AccentDialogActionButton` | A button with three contents in one cell: its own at rest, ring + "Cancel" while the operation runs, ring + "Stopping..." after the press. `Command` starts, `CancelCommand` stops; Enter starts and never cancels; it keeps the keyboard focus by staying enabled. | A command that starts an operation that can be stopped. Replaces the old pair "start button + Stop button". |
| `c:ProgressRing` | `App/Controls` | A 14 or 16 px ring, indeterminate or showing a part done; costs nothing while hidden. | "Busy" where there is no sentence to say (the quick search of the main window, the images of a template being read). |
| Notes | `Ad.Note.Info`, `Ad.Note.Attention`, `Ad.Note.Error` on a `ContentControl` whose content is a wrapping `TextBlock` | A glyph of the severity and a sentence, no fill, no border. | A sentence that belongs to one field or one section and is true for as long as that content is shown. |

## Sort every message first

Every text a view used to say (a banner, a progress bar, a count line, a toast of the same window, an error dialog) is one of four
things. The test for the first two: *is this the answer to something the operator just did, or to something the window is doing?*

1. **Status** - what the window did or is doing: reading, a read that failed, an operation that was stopped or finished, "the session
   of this window ended", "this was changed elsewhere". It goes to the **status line**. A count line of a page ("12 objects") is its
   summary (`SetIdle`) and leaves the page.
2. **Note** - a sentence that belongs to a field or a section and is true while it is shown: a validation text, "these mappings are
   weak", why *Save* is not possible, what two options mean together, a finding of the planner. It stays where it is, with the look of
   a note (`Ad.Note.*`). A sentence that answers a press but offers several ways out (a status has one action) is a note too.
3. **Progress of a row** - how far one item of a list is. It stays in its row (the bar of one activity in the list of background
   operations). It is the only `ProgressBar` left in the application.
4. **Busy** - a place that only shows that something is read and whose failure is said elsewhere: a `ProgressRing` in that place.
   Unless its read can fail - then it has status to say, and that goes to the line (a picker whose tree could not be read).

## The recipe: view model

1. **One holder per view model**, created with the localizer, the UI dispatcher and the status logger of the scoped services
   (`new PageStatusHolder(localizer, dispatcher, services.Lists.StatusLog)`), exposed as `IHasPageStatus.Status`. A window that is not a
   window view model (the Connect window, the two editors that work without a session) takes the dispatcher and an optional logger as
   constructor arguments. A window of tabs gives every page that says something of its own a holder and tells its own holder which one is
   shown: `Status.Front = selectedPage?.Status`, before the page is activated; the line stays bound to the window's holder.
2. **The summary is `SetIdle(text)`** - counts, "as of", the object that was read - joined with " · ". It is what the page says whenever
   nothing else is. A hosted control that is only a part of a window has none and says its status through the window.
3. **Every long operation runs in a scope with its token.** Begin it before the first await and let `using` end it:

   ```csharp
   using var reading = Status.Begin(
       Localizer.Format(L.ResetPasswordStatus.Reading, count), new PageStatusLog(L.ResetPasswordStatus.Reading, count));
   var token = reading.Token;                              // never CancellationToken.None, never the window's token alone
   try
   {
       var found = await Task.Run(() => ReadAsync(session, token), token).ConfigureAwait(true);
       if (reading.IsEnded)                                // replaced or disposed while it ran: say nothing, touch nothing
       {
           return;
       }

       Show(found);                                        // a result that is the content itself: no message, the scope just ends
   }
   catch (OperationCanceledException)
   {
       if (reading.IsCancelling)                           // only the operator's Stop says so
       {
           reading.CompleteAttention(Localizer[L.ResetPasswordStatus.ReadStopped], new PageStatusLog(L.ResetPasswordStatus.ReadStopped),
               new PageStatusAction(Localizer[L.Common.Retry], ReadAgainCommand));
       }
   }
   catch (DirectoryException ex)
   {
       var code = ResultCode.Of(ex);
       reading.CompleteError(
           PageStatusText.Failure(Localizer[L.ResetPasswordStatus.ReadFailed], Errors.Translate(ex), Localizer[L.Feedback.RetryNext], code),
           new PageStatusLog(L.ResetPasswordStatus.ReadFailed).WithCode(code),
           new PageStatusAction(Localizer[L.Common.Retry], ReadAgainCommand));
   }
   ```

   A second `Begin` on a holder **replaces** the running operation (its token is cancelled and what it says later is ignored); two
   operations that can overlap live in two holders, or the second is refused by its command while the first runs. An operation that
   *contains* a step that is an operation elsewhere does not begin a second scope for it. An operation that cannot be stopped is
   begun with `canCancel: false` (no Stop is offered).
4. **A state that lasts is computed in one method.** "Could not be read", "was deleted", "is out of date", "the session of this window ended"
   are true for as long as the page shows what it shows. A cancelled or failed read that leaves the content empty or stale is
   `SetLasting(error with Retry)` and the operation ends without a message; one that leaves the content as it was is a result
   (`CompleteError`). Keep **one** method (`UpdateLasting`) that computes the most important of the page's conditions - first of all
   `SessionEndedStatus` of `WindowViewModelBase` - and call it from every `On...Changed` hook of the conditions; setting an equal state
   again changes nothing. Only an attention or an error can last. Call it *before* you dispose the running operation when the
   session ends, so the line goes from "working" straight to "the session ended".
5. **A write goes through `ExecutionStatus`**, begun before the first step that changes anything:
   `using var execution = ExecutionStatus.Begin(Status, Localizer, title, domain, plan.Objects.Count);`, every activity the coordinator
   starts goes to `execution.Follow(activity)`, the end is `execution.Complete(result, next, action)` or `execution.Fail(reason, code)`.
   Where a step that can be stopped is followed by a write, check `scope.IsCancelling` after the step returned as well as in the
   `catch`: a planner that finished although Stop was pressed must not lead to the write. A read is repeated by a link in the line
   (Retry); a change never is.
6. **Stop returns control at once.** The operator's Stop puts "Stopping..." on the page first and then cancels the token; the page
   must be free before the work has ended. For a call that nothing can interrupt (parsing, synchronous IO) run it as
   `Task.Run(...).WaitAsync(token)`, free the page when the wait ends and drop the late answer (there must be no state to undo).
7. **Words.** Errors are `PageStatusText.Failure(tried, reason, next, code)`; "what was skipped: why" is the same helper for an
   attention; counts of objects use `PageStatusText.Objects`; an operation over many items is "Saving the accounts: 12 of 300..." with
   `PageStatusText.Progress` and `PageProgress.Of(done, total)` in the same `Report`. Strings go to the block *V interface renewal (V2)*
   of `Strings.resx` and `Strings.pl.resx`, grouped as `<Window>.Status.*` with one class of `L` per window ("Press Retry to try
   again." is the shared `Feedback.RetryNext`). Both languages are required.
8. **The log copy holds a key, counts and a code - never a value** (rule 1 of `CLAUDE.md`). `PageStatusLog` takes the resource key of the
   message and numbers; `ResultCode` can only be made from numbers and names the program produced (`ResultCode.Of(exception)`, `.Ldap`,
   `.Win32`, `.Kind`, `.Remote`, `.Watchdog`, `.Outlook`) - never a server message, a name, a path or a password. The text on screen
   may carry names; the log copy never does. A new message that is not the summary is logged once, a new count of the same message is
   not logged again.
9. **A dialog says what is not started.** What happens when a button was pressed and nothing was started (a field is missing, two
   passwords differ) is an attention built from "what was skipped: why", with the key of the reason as its log copy; a dialog that
   closes itself on success says nothing in its line (the opener's toast or dialog tells it).
10. **The same message key on two pages.** `StatusLine` decides "a new message" by kind and key: switching between two tabs that say
    messages of the same key changes the text without the entrance motion and without a live-region announcement. Give pages that
    can follow one another their own keys (open question, below).

## The recipe: view

- **One `c:StatusLine` named `PageStatus`**, bound exactly like this (the source test wants this text):

  ```xml
  <c:StatusLine x:Name="PageStatus"
                CloseCommand="{Binding Status.CloseCommand, Mode=OneWay}"
                DockPanel.Dock="Top"
                Status="{Binding Status.Current, Mode=OneWay}" />
  ```

  Windows and dialogs put it at the top, above the fields, 8 px in from the window edge (16 px in a dialog with 24 px content);
  a window with tabs puts it above the tab control, outside the scrolling. In a `DockPanel` declare the footer first, then the header,
  then the line (tab order is the order of declaration). `IsDefault` and `IsCancel` stay where they were. A window with a fixed height
  is 36 px higher than before; a dialog with `SizeToContent="Height"` grows and shrinks with a result that wraps.
- **A start button whose operation can be stopped is an `c:ActionButton`**: `Command` starts, `CancelCommand` is the holder's cancel,
  `IsRunning` and `IsCancelling` are bound to the holder of *that* operation (`Status.IsWorking`, `Status.IsCancelling`). Two or more
  action buttons over one holder each need a flag of their own (`IsConnecting`, `IsBusy`), or all of them would say "Cancel". A button
  that never had a Stop of its own and shares a holder with others stays a `Button`: its Stop is in the line. Use the accent style for
  the primary action and the dialog styles in a footer.
- **Notes** are `ContentControl` with `Ad.Note.Info`, `Ad.Note.Attention` or `Ad.Note.Error` and a wrapping `TextBlock` that keeps its name
  and `AutomationProperties.LiveSetting`. They are not told by colour alone: each severity has its own glyph. A note stays in its
  place whatever the window is doing; it is hidden by a converter on its text, not by the status.
- **A ring** for "busy" without a sentence: a `c:ProgressRing` in the place, and the old sentence as the `AutomationProperties.ItemStatus`
  of that place (the ring is outside UI Automation).
- **Register the file in the source tests.** A view with a line goes on `PagesWithAStatusLine`, a converted view with no status to say
  (only notes: a dialog that collects input, a window that only asks, a control that a converted window hosts) goes on
  `ConvertedViewsWithoutAStatusLine` (both in `tests/AdMng.App.UiTests/XamlSourceTests.cs`, sorted, never overlapping). A view with a
  line that is not on the first list fails; a view on the second list that grows a line fails.
- **A hosted flow has the line of its host.** The bulk creation wizard is the first step of the batch editor: no `StatusLine` of its own,
  its holder is `SourceStatus` of the host and is `Status.Front` exactly while the step is shown. Ask who hosts a view before you
  put a line in it.
- **New windows** are registered with `services.AddWindow<TViewModel, TWindow>()`, opened from `DialogService` and added to `SmokeTest`
  and a UI test, as `CLAUDE.md` says; a per-connection service is scoped.

## Tests to copy

Presentation tests run on Linux and are the bulk: the view models run against the real engines and the fake directory.

- **The model and the holder:** `Feedback/PageStatusHolderTests`, `PageStatusTests`, `PageStatusSourcesTests` (a state that lasts, the page
  in front, "12 of 300"), `Changes/ExecutionStatusTests`. **One window:** copy the `<Window>StatusTests` next to the view model
  (`Dialogs/FindStatusTests` is a small one with a single operation; `Dialogs/PropertySheetStatusTests` has pages in front and a state that lasts).
- **`StatusRecorder` and `Said`** (`tests/AdMng.Presentation.Tests/GroupPolicy/GroupPolicyStatusTestSupport.cs`) record every change of
  `Status.Current` from creation and let a test assert the sequence as a whole, so a state that lasts a moment ("Stopping..." of a stop
  that is answered at once) is observed and not raced for. **`HeldDirectoryCall`** holds a call in `FakeDirectorySession.BeforeOperation`
  and releases it - the way to make a slow domain controller. Never lower `Latency` while a call may still be on its way to it; the call
  finishes normally and the test is right only most of the time. A failing server is `FailNext` or an exception from
  `BeforeOperation`. A planner's first read runs on the caller's thread: start such a command with `Task.Run`.
- **Assert** the kind, the text, the log copy (`status.Log.ToString()`: the key and counts, never a name) and the action in both
  languages, Stop with a held call, a replaced operation that says nothing, the end of the session (also during an operation), closing
  the window during an operation, and that no log copy carries a value.
- **UI tests** (Windows CI only, written without a screen; properties, resources and view model state, never pixels): one file per
  window in the style of `PropertySheetStatusUiTests`, through the real window over the fake directory, with `UiTestContext`, `UiWait`,
  `ControlHost`, `LanguageScope` and `MotionScope`. `GroupPolicy/GroupPolicyStatusUiSupport.cs` has `HeldRead`, `GrowthOf` (the line
  grows by 20 px per extra line and what lies under it moves by as much), `AssertTheLineOf`, `AssertNoNotes`. The lessons that cost a
  Windows run each:
  - **Transient states.** Assert only a state that lasts until the test changes it; never look for a transient state after an idle wait.
  - **"Stopping..." lasts until the test lets the held call go.** A hold that ends when the call is cancelled makes "Stopping..." vanish
    before the test looks (the remote fake has `HoldUntilReleased`, off by default). Hold a read only on a pool thread (`HeldRead`): a
    call held on the UI thread holds the test.
  - **Unsaved edits.** A UI test of an editor must not end with unsaved edits: the main window asks before it closes, and that question
    outlives the test. Discard through the real confirmation as the last step.
  - **Hidden files.** GPMC's `manifest.xml` is written hidden and Windows will not overwrite a hidden file: a test that makes a backup
    over an earlier one clears the attribute first.
  - **Coverage for probes.** The audit probes of readiness read a DC's log only where its coverage reaches back, and the empty fake logs of
    the UI tests have none: give the fake DCs the coverage the Presentation harness gives them (`EventLogUiSupport`).
  - **The thread pool floor.** Tests that block a pool thread on purpose (a held call) starve in-process servers that run side by side;
    the directory test assembly raises the floor of pool threads (`ThreadPoolFloor`). Do not assume a freed port is still closed either.
  - **Warnings are errors.** A nullable warning in a UI test file fails the Windows build; the project compiles on Linux, so build the
    whole solution after every change of a member (`dotnet build AdMng.slnx -c Release`), not just the project you touched. A wrong
    `StaticResource` key compiles and fails only at run time on Windows; never `Freeze()` a pen or brush built from a theme brush.
- **Source rules** (`XamlSourceTests`, they run on Windows CI; prove a new one on Linux with a throwaway script that repeats its
  expression): the two lists above; `NoXamlFile_MentionsABannerOfBefore_OrHasAProgressBarThatSaysStatus` (no XAML file names a banner
  style, and the only `ProgressBar` is the row of an activity: `ProgressBarsOfARow`); every `Ad.*` key that is used is defined; the closed
  set of Fluent brush keys; no storyboard, duration or easing outside `Themes/Motion.xaml`.

## What is settled, and what is still the owner's

**Settled** (in the as-built blocks): the accent variant of the action button; a page with several operations (a state that lasts, several
holders with the page in front, operations one after another on one holder); determinate progress ("12 of 300" and the part done in the
ring); the look of the notes; the execution of a plan as an operation; "busy" as a ring; a result that does not fit the line wraps to
three lines at most (the owner's decision of 2026-11-11); how a dialog carries its line and when it has none; a flow that another window
hosts has the line of its host; a family of documents shares the holder of its base class; an "as of" text is the detail of the summary;
a quick action never ends an operation of its page; the coverage of a run is said in the line for what it could not read and stays in rows
and notes for the rest.

**Not started, waiting for the owner** (*What V2b-5 has to do* in the design note): a delay (about 150 ms) before "Working" so that an
operation that ends at once does not flash; `StatusPill`, `EmptyState` (also the answer to the empty 28 px band of a line with nothing to
say) and `Skeleton`; taskbar progress; the close question of a window closed during an operation; the screen reader (a result that only
changes the summary is not announced, a mark in the tab header of a page that failed while it was not shown); whether a note may choose
its glyph. The list *Still the owner's* in *What V2b has to do* holds the smaller questions of the units (the loudness of the caution glyph
of the notes, where the press-time answers of the editors belong, whether *Refresh* is the Cancel of a read the operator did not start,
what a stopped run keeps). Do not decide them in a view; if a view needs one answered, ask.
