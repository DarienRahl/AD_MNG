namespace AdMng.Presentation.Feedback;

/// <summary>
/// The execution of a change plan that a window follows in its status line (the owner's decision D-A; design note
/// docs/dev/v-interface-renewal.md "D-A repair as built"): the activity runs in the connection, the window only shows it.
/// Closing such a window may therefore let the run go on to its end without the window - "Close and continue in the
/// background" - and the connection then says how it ended (<see cref="RunningWork"/>). Set on the operation of the
/// window (<see cref="OperationScope.Run"/>) by <c>Changes.ExecutionStatus</c>; no WPF type.
/// </summary>
/// <remarks>Members are UI-thread members, like those of the view model whose window closes.</remarks>
public interface IWindowRun
{
    /// <summary>
    /// Why this run must stay with its window - the localized sentence of the close question ("the generated passwords
    /// exist only in this window") - or null when it may continue in the background. It may change while the run goes on
    /// (the bulk creation wizard: not before the first account exists).
    /// </summary>
    string? MustStayReason { get; }

    /// <summary>
    /// True while the run may leave its window now: its activity is followed by the window and has not handed its result
    /// back to the window's code yet. False before the activity started, after the result came back (the window is
    /// finishing - a re-read, the results) and once it left.
    /// </summary>
    bool CanLeave { get; }

    /// <summary>True once the run left its window: the window's code must not touch the result any more.</summary>
    bool LeftWindow { get; }

    /// <summary>
    /// True when the run ended and its result came back to the window, whose code has not said it yet (it reads the list
    /// again first, the Recycle Bin): nothing can be stopped or left any more - the window is finishing.
    /// </summary>
    bool HasReturned { get; }

    /// <summary>True once Stop was asked for the run (in its line, in the list of background operations or here).</summary>
    bool IsStopping { get; }

    /// <summary>
    /// The window closes and lets the run go on: the window stops following it (its line shows the summary again, the
    /// operation ends for the holder) and the connection says how the run ended. False - and nothing changes - when the
    /// result already came back to the window (<see cref="CanLeave"/>). <see cref="MustStayReason"/> is not looked at: a
    /// forced close (the owner window, the end of the program) hands every run over this way.
    /// </summary>
    bool LeaveWindow();

    /// <summary>
    /// A close of the window asks about this run or waits for it: should the window close before its line said how the run
    /// ended - the result came back while the question was open - the connection says it (<see cref="WindowClosed"/>).
    /// </summary>
    void WindowMayClose();

    /// <summary>
    /// The window will close right after the run ended (Stop and close, a Stop that came first, a result that came back under
    /// the question): its line goes with the window, so the connection says how the run ended once the window closed
    /// (<see cref="WindowClosed"/>). The window's code leaves out what only an open window needs - its own notification, a
    /// re-read of its list. Nothing is reported when the window says the outcome itself in a notification.
    /// </summary>
    void ReportByConnection();

    /// <summary>The close did not happen: the window says the result in its line itself, and nothing is kept for the connection.</summary>
    void WindowStays();

    /// <summary>
    /// The window closed (its view model was disposed). When the close came before its line said how the run ended
    /// (<see cref="WindowMayClose"/>, <see cref="ReportByConnection"/>), the connection says it now - in a later turn of the UI
    /// thread, after the window is gone, so that a dialog of the connection is never owned by the window that closes. Only once;
    /// nothing for a run that left the window (the connection said it already) or whose window said it in a notification.
    /// </summary>
    void WindowClosed();

    /// <summary>
    /// Stops the run (the executor finishes the object in progress and starts no other), also when its operation lost the
    /// line of the window to another one.
    /// </summary>
    void Stop();
}

/// <summary>
/// Work of a connection that no window follows (the owner's decision D-A): a run left in the background by its window, a
/// change of a simple action (<c>ChangeCoordinator.ApplyAsync</c>). The connection window lists it in its close question and
/// stops it before it closes - closing that window ends the connection.
/// </summary>
public interface IBackgroundWork
{
    /// <summary>One line for the close question: "In the background: “Disable 120 accounts”, 12 / 120".</summary>
    string Line { get; }

    /// <summary>True once Stop was asked for it.</summary>
    bool IsStopping { get; }

    /// <summary>Completes when the work has ended; never fails.</summary>
    Task Ended { get; }

    /// <summary>
    /// Stops the work because the connection closes: nothing new is started, the object in progress is finished, and its
    /// outcome is not reported in a notification or a dialog (the window and its activity list go with the connection).
    /// </summary>
    void StopBecauseTheConnectionCloses();
}

/// <summary>The work of a connection that no window follows (<see cref="IBackgroundWork"/>).</summary>
public interface IConnectionWork
{
    /// <summary>What runs in the connection without a window, newest first. UI thread.</summary>
    IReadOnlyList<IBackgroundWork> RunningInBackground { get; }
}
