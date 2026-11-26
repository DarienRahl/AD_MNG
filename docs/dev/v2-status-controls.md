# V2 feedback controls - how to write the next view

For whoever writes or converts a view after the sweep of stage V2: what the pieces that say "what this page is doing" are, which
message goes where, the recipe for a page, the tests to copy and what is still open. It is a reader's guide, not the design: the
reasons, the alternatives and the as-built record of every window are in
[v-interface-renewal.md](v-interface-renewal.md) (sections *6.7 Feedback, one place each* and *V2 - Feedback*, blocks *V2a as built*,
*V2b-1 as built* with its **Foundations**, *V2b-2 as built* with its **Dialogs**, *V2b-3 as built* with its **Group Policy documents**,
*V2b-4 as built* with its **Areas** and *V2b-5b as built*, the owner's decisions of the end of the stage). Where this page and the
as-built blocks disagree, the blocks win.

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
| `OperationWrites` | `Presentation/Feedback`, `OperationScope.Writes`, the `writes:` argument of `Begin` | What an operation changes: `None` (a read - the default), `Changes` (the execution of a plan), `Messages` (a send), `Other` (a restore, a store update, a backup, a save, a remote command). `ExecutionStatus` sets `Changes` itself. (V2b-5b) | The operations a window may not be closed on silently: say it where the operation begins, never guess it from a name. |
| `RunningWork` | `Presentation/Feedback` (no WPF type) | The one question of a window closed while it works: over the holders of its view models (`IHasPageStatus`, `IHasPageStatusParts`, `Front`), only for operations that write, with labelled answers (`IDialogService.AskCloseAsync`). The execution of a plan that may go on without the window (`OperationScope.Run`, an `IWindowRun` set by `ExecutionStatus`) gets three - *Stop and close*, *Close and continue in the background*, *Don't close* (D-A); a run that must stay (`StaysWithWindow`: generated passwords, a waiting caller, the wizard's first account) and every other write get *Stop and close* / *Don't close* and the reason; Stop and close stops through the holder's own Stop and the window closes after the end, Continue lets the run leave (`LeaveWindow`) and the coordinator reports it, a write that cannot be stopped keeps the window open and says so, a read closes without a question. The window closes as answered also when a run came back under the question or is finishing, and without a question after a Stop that came first; a run whose line goes with the window is handed on (`ReportByConnection`) and the connection says how it ended once the window closed (`reportOnClose` → `IWindowRun.WindowClosed` from `WindowViewModelBase.Dispose`; `WindowStays` when the window stays). The connection window adds the work no window follows (`IConnectionWork`, the activity centre), never offers the background, hands nothing on and gives only its own reason. Asked by `WindowViewModelBase.CanCloseAsync` (a second close joins the pending one), `ShellViewModel.CloseTabAsync` and the main window. (V2b-5b, D-A) | Every window; a view model has nothing to do except `writes:` where it begins a write, and `IHasPageStatusParts` when it holds pages that are not always in front. |
| `TaskbarProgressTracker` / `TaskbarProgress` | `Presentation/Feedback` (the tracker, tested on Linux) and `App/Behaviors` (one class handler for every `Window`, registered by `AppHost.InitializeAsync`) | The progress of the taskbar button of a window: it follows the holder in front - the line - after the same delay: a part done is green, an unknown part the marquee, an operation that was on the button and ended with a state that lasts yellow or red until the page says anything else; everything else nothing. The main window follows its selected tab (`IHasFrontStatus`). (V2b-5b) | Nothing to wire: a window that has a holder gets it. A window without one is never touched. |
| `PageStatusText` | `Presentation/Feedback` | Text helpers: `Failure(tried, reason, next, code)`, `Progress(localizer, done, total)` ("12 of 300"), `Objects` / `Count` (the plural forms). | One shape of an error: "what we tried: why it failed. What to do next. (code)". |
| `c:StatusLine` | `App/Controls`, style `Ad.StatusLine` | Shows a `PageStatus` in a line of a fixed height (the summary and a running operation: one line; a result: up to three lines, then the tool tip): the picture of the severity (a ring while it works), the fixed title, the text, the action as a link, a close button where `CanClose`. A live region: polite for the summary, working and done, assertive for attention and error. Two things of time (V2b-5b): a running operation is **shown 150 ms after the page says it** (`Motion.WorkingDelay`; the line keeps showing what it showed, so a quick operation never flashes, and the result it ends as enters and is announced even when it is the message already shown - the same error after Retry), and a summary whose text changes is **announced once, 800 ms after the last change** (`Motion.SummaryThrottle`). The band of 28 px never collapses. | The one place. |
| `c:ActionButton` | `App/Controls`, styles `Ad.ActionButton`, `Ad.AccentActionButton`, `Ad.DialogActionButton`, `Ad.AccentDialogActionButton` | A button with three contents in one cell: its own at rest, ring + "Cancel" while the operation runs, ring + "Stopping..." after the press. `Command` starts, `CancelCommand` stops; Enter starts and never cancels; it keeps the keyboard focus by staying enabled. | A command that starts an operation that can be stopped. Replaces the old pair "start button + Stop button". |
| `c:ProgressRing` | `App/Controls` | A 14 or 16 px ring, indeterminate or showing a part done; costs nothing while hidden. | "Busy" where there is no sentence to say (the quick search of the main window, the images of a template being read). |
| Notes | `Ad.Note.Info`, `Ad.Note.Attention`, `Ad.Note.Error` on a `ContentControl` whose content is a wrapping `TextBlock` | A glyph of the severity and a sentence, no fill, no border. The glyph may be another of the four status icons: `c:AdNote.Icon="{x:Static c:AppIcons.StatusInfo}"` (V2b-5b; the colour stays the style's). | A sentence that belongs to one field or one section and is true for as long as that content is shown. |
| `c:StatusPill` | `App/Controls`, style `Ad.StatusPill`; kinds in `Presentation/Feedback/StatusPillKind` | A small rounded label: the picture of its `Kind` (`Ok`, `Info`, `Attention`, `Error`, `Unknown`) at 16 px and a short `Text`, which is also its automation name. The surface only tints the kind; a 1 px stroke outlines it in High Contrast. Not interactive. (V2b-5b) | The state of **one row or one item**: the result of a check, a health state, a channel state. |
| `c:EmptyState` | `App/Controls`, style `Ad.EmptyState`; the state in `Presentation/Feedback/EmptyListState` | A 48 px picture (`Icon`, an `ObjectIcon`), one sentence (`Message`) and at most one action (`ActionText` + `ActionCommand`, the link button of the line), centred in its host and collapsed while `Message` is empty. A group for a screen reader, named by its sentence. (V2b-5b) | A list that was **read** and has no rows. Never for "could not be read" (the line's error) and never for the first read (a skeleton). |
| `c:Skeleton` | `App/Controls`, style `Ad.Skeleton` | Placeholder rows (`RowCount`, at most 12, of `RowHeight`): a picture and three grey bars per row (style `Ad.Skeleton.Bar`: the fill `ControlStrongFillColorDisabledBrush`, about 2.2:1 on a list in light and dark, and a 1 px outline of `ControlStrokeColorDefaultBrush`, which draws the bar in High Contrast). `IsActive` shows it, the bars are drawn after `Motion.WorkingDelay`; still without motion, a gentle opacity pulse (`Motion.Pulse`, over `Motion.Shimmer`) with it and only while visible. Decorative (no automation peer, no focus, no pointer). (V2b-5b) | The **first read** of a list, in place of blank space. Never for a reload of rows that were shown. |

## Pill, empty state or skeleton? (V2b-5b)

Three small controls say something about *an item* or *a list*, and none of them is a status of a page:

| You have | Use | Not |
|---|---|---|
| The state of one row or one card - "OK", "Warning", "Not checked" - that belongs to that item and is true while it is shown | `c:StatusPill` in the row: `Kind` from a computed property of the row view model (`PillKind`), `Text` the word you already have (`StatusText`) | A coloured glyph with the word run into the title, or a glyph alone with the word in a tool tip: the picture and the word have to be there together |
| A list that was read **and** has no rows | `c:EmptyState` over the list: one sentence that says why, and the next step as the one action | A text in the middle of the list that the list invented (replace it - the sentence is said once), or an empty state for a failed read (that is the line's error with Retry) |
| A list that is being read for the **first** time | `c:Skeleton` over the body of the list, `IsActive` bound to the first-read state of the view model | Placeholder rows for a reload, a stopped read or a failed one - the rows come back at once, or the line says why they do not |

How each is wired (the first uses are the model):

- **Pill.** The view model gives the row a `StatusPillKind` (`ReadinessRowViewModel.PillKind`, `WatchdogHealthCheckRowViewModel.PillKind`: a
  pure mapping, tested on Linux) and the word; the view binds `Kind` and `Text` one way. Put pills of a column in one `Grid.IsSharedSizeScope`
  with `SharedSizeGroup` on their column, so the titles line up whatever the width of the words (the readiness report, the health checks). Do
  not put a pill where its column is narrow (the channel and domain controller rows of the Watchdog keep their glyph with a tool tip).
- **Empty state.** The view model has one computed property, `EmptyState` (`EmptyListState`: the sentence, the `ObjectIcon`, at most one
  `PageStatusAction`), that is `EmptyListState.None` unless the list **was read well** (a flag of the load that ended without a failure or a
  stop), is not loading, has no error and has no rows. Raise `PropertyChanged` for it where those change. The action is *Clear the filter* or
  *Clear the search* when a filter hides everything (a command that puts the filter out - and reads once, at once, if a filter of the
  server was on: a chip, or the quick filter text of a very large container), otherwise *Refresh*. A text filter of loaded rows hides only
  rows that were read: over an empty list it is no such filter, and the sentence is the empty one with *Refresh*. The view binds the four properties one way (`Message`, `Icon`, `ActionText`, `ActionCommand`) and needs no visibility
  of its own. The list's own text for "nothing matches" is removed, not kept beside it.
- **Skeleton.** The view model has `ShowsSkeleton` (loading, the first read of a location that was not shown, no error, no rows - a reload of
  the same source that was shown, `ReplaceLocationAsync`, a stop and a failure never are; a read that failed or was stopped before its first
  row showed nothing, so the next read of it is still the first); the view puts the control in the grid cell of the list with a top
  margin of the column header (29 px) and binds `IsActive`. The 150 ms before the bars are the control's own (`IStatusScheduler`, as the
  status line's), so a test replaces `Skeleton.Scheduler` with a `ManualStatusScheduler` and never waits.

The sentence of an empty state and the word of a pill are strings of both languages like every other (`List.EmptyFiltered`, `PkiTpl.EmptyNone`).
`XamlSourceTests` fails a pill without `Kind` and `Text`, an empty state without `Message` or with a label and no command (or the other way
round), and a skeleton that is never `IsActive`; and the three styles may not write a colour of their own.

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
   operations that can overlap live in two holders, or the second is refused by its command while the first runs. A command that
   starts another through `ExecuteAsync` goes past that command's `CanExecute`, so its own `CanExecute` carries the same guard (the
   holder sweep of 0.16.1 found the transport bar of the message window: its "send directly" ran `SendCommand` while a new preview was
   read, and the send's `Begin` replaced the read). An operation that
   *contains* a step that is an operation elsewhere does not begin a second scope for it. An operation that cannot be stopped is
   begun with `canCancel: false` (no Stop is offered). **An operation that writes says so** (V2b-5b): `writes: OperationWrites.Other`
   (or `Messages` for a send; `ExecutionStatus` says `Changes` itself). The window then asks before it is closed while it runs - *Stop
   and close* stops it and the window closes after its end, an operation with `canCancel: false` keeps the window open and says it is
   still working - and a read says nothing. A window that executes a plan through `ExecutionStatus` gets the three answers of D-A by
   itself: it only returns on `execution.LeftWindow` after its await (the run left with the window), and calls
   `execution.StaysWithWindow(L.CloseWork.NoBackground…)` when its result must stay with the window (generated passwords, a caller
   that reads the result) - `WindowNotifies = true` when a toast of its own is the whole report (a success; then the connection does not
   say it again when the window closes right after the run), and `execution.NotifyWhileOpen(...)` for a toast meant for an open window
   only (the batch editor's counts: held while a close looks at the run, said if the window stays, left to the connection if it closes). Its progress is on the taskbar button of the window like every other (a part done through
   `PageProgress.Of(done, total)` in `Report`), with nothing to wire.
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
   (Retry); a change never is. **Stop finishes the object in progress** (V2b-5b): the executor starts no new object after it, but an
   object whose first change has succeeded gets all its changes, so a stopped result is "12 of 300 objects done, the one that ran
   was finished" and never "half done" - `ExecutionStatus` says it, and `BatchResult.FinishedAfterStopCount` is the number.
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
    can follow one another their own keys (open question, below). The one exception is time (rule 11): a result that ends an
    operation the line never showed is a new message whatever its key.
11. **Time is the line's business, not the page's** (V2b-5b). Say "Working" the moment the work begins - never wait 150 ms yourself and
    never hold the status back to avoid a flash: the line delays what it *shows* (an operation that ends within 150 ms never shows the
    working look, and its result - not the summary - enters and is announced even when it is the same message as before, so a Retry
    that fails the same way is seen and heard; a page whose summary is empty shows its empty band meanwhile), the model and your tests
    of it stay immediate. Say the
    summary as it is - a count that changes with every key is fine: the line tells a screen reader once, after the last change. A page
    that wants its count announced politely needs nothing; a page that does *not* want a text announced puts it in a note or a field,
    not in the summary.

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
  place whatever the window is doing; it is hidden by a converter on its text, not by the status. **A note may choose its glyph**
  (V2b-5b): `c:AdNote.Icon="{x:Static c:AppIcons.StatusInfo}"` - one of `StatusInfo`, `StatusOk`, `StatusWarning`, `StatusError`, nothing
  else (`XamlSourceTests` fails another key) - for a sentence whose severity is not the one of the style's glyph (the replication note of
  the bulk creation results is information among cautions). The style still decides the colour; choose the style for how serious the
  sentence is and the icon for what it is. Without the property the glyph is the style's.
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
- **The time of the line:** `StatusFlowTests` (no WPF type, runs on Linux through a scratch project that links the file and the tests)
  and `StatusLineTimingUiTests`; both drive the line with a `ManualStatusScheduler` (`StatusLine.Scheduler`, `MayAnnounceSummary`,
  `Announced`, `Announcements` are the internal seams) so the delay and the quiet time are asserted at the millisecond without a wait.
  A test of a page does not need them: it waits for what it looks at.
- **UI tests** (Windows CI only, written without a screen; properties, resources and view model state, never pixels): one file per
  window in the style of `PropertySheetStatusUiTests`, through the real window over the fake directory, with `UiTestContext`, `UiWait`,
  `ControlHost`, `LanguageScope` and `MotionScope`. `GroupPolicy/GroupPolicyStatusUiSupport.cs` has `HeldRead`, `GrowthOf` (the line
  grows by 20 px per extra line and what lies under it moves by as much), `AssertTheLineOf`, `AssertNoNotes`. The lessons that cost a
  Windows run each:
  - **Transient states.** Assert only a state that lasts until the test changes it; never look for a transient state after an idle wait.
  - **The working look is 150 ms late** (V2b-5b). A held call lasts, but the line shows "Working" only after the delay: a test that
    asserts the working look (the title, `line.IsWorking`, the ring, the Stop link, "Stopping…" of a line that never showed
    "Working") waits for it with `UiWait.UntilAsync(() => held.IsSet && line.Kind == PageStatusKind.Working, ...)` - never with
    `WaitForIdleAsync` (rounds of 40 ms) and never with a wait for the model alone (`IsLoading`, `Status.IsWorking`, `IsCancelling`).
    The button of an operation (`ActionButton`) is not delayed. A control test of the line itself sets `Scheduler` to a
    `ManualStatusScheduler` and advances it by `Motion.TimeOf(Motion.WorkingDelayKey)` after a status that is Working.
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
- **The controls of V2b-5b:** `StatusPillUiTests`, `EmptyStateUiTests`, `SkeletonUiTests` (the template parts, what each property does, the
  automation name and peer, the delay of the skeleton with a `ManualStatusScheduler`, the pulse only with `MotionScope.On()`, the contrast of
  the bars against the surfaces of a list in the light, dark and High Contrast dictionaries of the theme, loaded into the test window); the first uses
  through the real views: `ObjectListEmptyStateUiTests` (a held read for the placeholder rows - `FakeDirectorySession.BeforeOperationAsync`
  holds a search without blocking a thread - and a reload that shows none), the GPO list in `GroupPolicyEditingUiTests`, the PKI templates in
  `PkiOverviewUiTests`, the pills in `ReadinessStatusUiTests` and `WatchdogViewTests`; on Linux `ObjectListEmptyStateTests`,
  `StatusPillKindTests` and the empty states in `GroupPolicyEditingComfortTests` and `PkiTemplatesTests`.
- **The taskbar and the close question** (V2b-5b): `TaskbarProgressTrackerTests` and `RunningWorkTests` (Linux, a fake clock and a scripted
  dialog service - copy them for a new rule), `ShellRunningWorkTests`; in a window's own tests the close question goes through the real
  close path - `TestWindowHost` (the title bar: `CanCloseAsync`, then `Dispose` on true; `Dispose` on `CloseRequested`) or the window's own
  Close command, never a direct `Dispose` (the one exception is a test named for a forced close by the owner window): begin the write
  held, close, answer *Don't close* (`ScriptedDialogService.CloseAnswers`; the window stays, nothing stopped), answer *Stop and close* (the
  line says "Stopping…", the window waits), release, and assert that the object in progress is finished and the window's own question
  comes after the end; for an execution also *Close and continue in the background* (the window closes at once, the activity runs on,
  the connection's toast or dialog says how it ended - record with `RecordingNotificationService.OnShow` / `ScriptedDialogService.OnError`
  that the window was closed by then), *Stop* pressed before the close (no question, the connection says how it ended), a result that comes
  back under the question, and the reason when it must stay (`BatchEditorStatusTests`, `ResetPasswordStatusTests`,
  `RecycleBinStatusTests`, `ChangePlanPreviewStatusTests`, `BulkCreateStatusTests`, `UserMessageComposerStatusTests`). Windows CI:
  `TaskbarProgressUiTests` (`TaskbarClock` moves the delay; assert `Window.TaskbarItemInfo`), `CloseWhileWorkingUiTests` (the real
  lifecycle: `window.Close()`, `ScriptedDialogService.CloseAnswers`, `CloseQuestions` and `InfoMessages`; wait for the model to say
  "Stopping…" before releasing the held write; one test keeps the real `DialogService` and drives its failure dialog with
  `UiWait.DriveModalAsync` to see that it comes after the window closed and is not owned by it) and `DialogWindowTests` (the
  three-button dialog).
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
and notes for the rest. And, by the owner's decisions of V2b-5b: Stop finishes the object in progress; a delay of about 150 ms before
"Working" is shown; a changed summary is announced politely and throttled; the status line keeps its band of 28 px when it has nothing to
say; a note may choose its glyph from the closed set of four; the taskbar button of a window shows what its line says; a window closed
while an operation writes asks one question.

**Built in V2b-5b** (the owner decided them and the units built them; *V2b-5b as built* in the design note): in unit 1 the Stop that finishes
the object in progress; in unit 2 the delay before "Working", the throttled announcement of a summary and the glyph of a note; in unit 3
`StatusPill`, `EmptyState` (the answer for lists without rows; the 28 px band of a line with nothing to say **stays** - owner's decision of
V2b-5b) and `Skeleton`, each with its first use; in unit 4 the progress of the taskbar button of every window that has one (18 of the 41 windows; a dialog without a button says it in its line only) and the one close question of a
window that writes (`OperationWrites`, `RunningWork`). Unit 5 recounted the stage: no banner, 45 views with a line, 29 converted views
without one (*Counts* in *V2b-5b as built*).

**Still open.** One thing is decided and not built: a mark in the tab header of a page that failed while it was not shown (6.7). The rest are
the owner's questions, listed in *What is still open after V2* in the design note - from V2b-5b among them whether a sequential plan should
finish as a whole, whether Apply and "New Object" now offer Stop in the line, whether the selection ("· 1 selected") belongs in the summary
that is announced, whether the other lists with a note become empty states, whether a write on a page that is not in front belongs on the
taskbar button, and whether the wait after Yes in the close question gets a limit; from V2b-3 and V2b-4 the loudness of the caution glyph of
the notes, where the press-time answers of the editors belong, and whether *Refresh* is the Cancel of a read the operator did not start. Do
not decide them in a view; if a view needs one answered, ask.
