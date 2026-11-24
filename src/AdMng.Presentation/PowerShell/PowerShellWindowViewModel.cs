using AdMng.Core.Directory;
using AdMng.Core.PowerShell;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Shell;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.PowerShell;

/// <summary>
/// The PowerShell window of one connection window (track R, stage R1; docs/dev/r1-powershell-window.md 6): the scrollback, the line
/// editor, the history, the status line with Stop, the language mode and the notes about the workstation's policies. Everything of
/// the window that is not drawing - tested on any system against a fake <see cref="IPowerShellSession"/>; the engine is reached
/// only through <see cref="IPowerShellSessionFactory"/>.
/// </summary>
/// <remarks>
/// <para><b>Nothing on disk</b> (CLAUDE.md rule 1): the scrollback, the history and the results are in this object and nowhere else;
/// they are gone when the window closes. <b>Nothing writes</b> (rule 5, decision D9): the window knows no command that changes the
/// directory; the place for the pending plan of stage R2 is <see cref="PendingPlan"/>, empty.</para>
/// <para><b>The status line</b> (docs/dev/v2-status-controls.md): a command is an operation that can be stopped
/// (<see cref="PageStatusHolder.Begin"/>, <see cref="OperationWrites.Other"/> - the window asks before it is closed while it runs:
/// a command the operator typed can be anything); what it did is the result - done, attention for errors and a stop, error for
/// a command that failed. What lasts is the note about the policies that record, the trusted mode, an engine that could not
/// start and the end of the session, in that order of importance after the session.</para>
/// </remarks>
public sealed partial class PowerShellWindowViewModel : WindowViewModelBase, IHasPageStatus
{
    /// <summary>The prompt the echo of a command starts with, and the one of its further lines.</summary>
    public const string PromptText = "PS> ";

    /// <summary>The continuation prompt of the further lines of a command.</summary>
    public const string ContinuationText = ">> ";

    /// <summary>The narrowest and the widest the engine's formatting is asked for.</summary>
    public const int MinOutputWidth = 40;

    public const int MaxOutputWidth = 400;

    private readonly PowerShellServices _powerShell;
    private readonly SessionInfo? _info;
    private readonly CancellationTokenSource _lifetime = new();
    private IPowerShellSession? _session;
    private PowerShellPolicies _policies = PowerShellPolicies.None;
    private PowerShellPolicyNote? _policyNote;
    private bool _policiesUnreadable;
    /// <summary>Why the policies could not be read; translated whenever the line is built, so it follows the language (audit W-01).</summary>
    private Exception? _policiesFailure;

    /// <summary>The build has no engine (no session factory): the window says so and what helps, not "open it again".</summary>
    private bool _noEngine;

    /// <summary>
    /// Why the window has no session although it was started: the factory failed, or the session lost its runspace (the new one after
    /// the trusted mode or after a left command could not be made). The cause is kept, not its text: the texts of the lasting state and
    /// of the switch are made from it when they are read, in the language of then (audit W-10).
    /// </summary>
    private Exception? _startFailure;
    private bool _started;
    private int _outputWidth = 120;

    public PowerShellWindowViewModel(DialogServices services, PowerShellServices powerShell)
        : base(services)
    {
        _powerShell = powerShell ?? throw new ArgumentNullException(nameof(powerShell));
        _info = Session?.Info;
        Status = new PageStatusHolder(Localizer, services.Dispatcher, services.Lists.StatusLog);
        History = new PowerShellInputHistory(line => _session?.ShouldRemember(line) ?? false);
        Input = new PowerShellInputModel(() => _session, History, canComplete: () => !IsSwitchingMode);
        Input.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PowerShellInputModel.Text))
            {
                RunCommand.NotifyCanExecuteChanged();
            }
        };
        Output.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PowerShellOutputDocument.DroppedLines))
            {
                OnPropertyChanged(nameof(DroppedNote));
            }
        };
        UpdateSummary();
        UpdateLasting();
    }

    /// <summary>The status line of the window (<see cref="IHasPageStatus"/>).</summary>
    public PageStatusHolder Status { get; }

    /// <summary>The scrollback: what the commands wrote, in the memory of the window.</summary>
    public PowerShellOutputDocument Output { get; } = new();

    /// <summary>The line editor.</summary>
    public PowerShellInputModel Input { get; }

    /// <summary>The commands that were run, in the memory of the window; never written anywhere.</summary>
    public PowerShellInputHistory History { get; }

    /// <summary>The place of the pending plan of stage R2: collapsed and empty in R1.</summary>
    public PowerShellPendingPlanState PendingPlan { get; } = new();

    /// <summary>"domain – DC – account – PowerShell", with "trusted mode" while it is on.</summary>
    public override string WindowTitle => _info is { } info
        ? Localizer.Format(IsTrustedMode ? L.PowerShell.WindowTitleTrusted : L.PowerShell.WindowTitle, info.DomainDnsName, info.Server, info.BoundIdentity)
        : Localizer[L.PowerShell.Name];

    /// <summary>The engine session exists and is not changing its runspace: commands can be run.</summary>
    public bool IsReady => _session is not null && !IsSessionEnded && !IsSwitchingMode;

    /// <summary>The engine is being started (the first runspace of a process takes about half a second).</summary>
    [ObservableProperty]
    public partial bool IsStarting { get; private set; }

    /// <summary>A command is running (also while it waits for an answer). The action button of the window is bound to this and to <see cref="Status"/>.</summary>
    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    /// <summary>The question the running command waits for, or null.</summary>
    [ObservableProperty]
    public partial PowerShellPromptState? PendingPrompt { get; private set; }

    /// <summary>What the editor shows before the text: the prompt of the question, or "PS&gt; ".</summary>
    public string InputPrompt => PendingPrompt is { Kind: not PowerShellPromptKind.Choice, Label: { Length: > 0 } label } ? label : PromptText;

    /// <summary>The note that the scrollback was cut ("the oldest 3,000 lines were removed…"), or null while nothing was.</summary>
    public string? DroppedNote => Output.DroppedLines > 0
        ? Localizer.Format(L.PowerShell.DroppedLines, Output.DroppedLines.ToString("N0", Localizer.Culture), PowerShellOutputDocument.MaxLines.ToString("N0", Localizer.Culture))
        : null;

    /// <summary>The policies of the workstation as they were read when the window opened.</summary>
    public PowerShellPolicies Policies => _policies;

    /// <summary>The lasting note about what the policies record, or null when none does.</summary>
    public string? PolicyNoteText => _policyNote?.Message;

    /// <summary>The width, in characters, of the engine's formatting.</summary>
    public int OutputWidth => _outputWidth;

    /// <summary>The window was given the width it can show (the editor's size, in characters); the next command is formatted for it.</summary>
    public void SetOutputWidth(int characters)
    {
        _outputWidth = Math.Clamp(characters, MinOutputWidth, MaxOutputWidth);
        if (_session is { IsRunning: false } session)
        {
            session.OutputWidth = _outputWidth;
        }
    }

    // ---- Opening -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts the engine and reads the policies, both off the UI thread. The window shows "Starting PowerShell…" and cannot run
    /// a command before it is done; what lasts - the note about the policies, a failure to start - is on the status line
    /// before the first command. Never throws.
    /// </summary>
    public override async Task InitializeAsync()
    {
        if (_started || IsDisposed)
        {
            return;
        }

        _started = true;
        try
        {
            await StartAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // StartAsync reports what it knows; this is the net under it ("never throws").
            _startFailure ??= ex;
            IsStarting = false;
            UpdateLasting();
            NotifyAvailability();
        }
    }

    private async Task StartAsync()
    {
        var directory = Session;
        if (directory is null)
        {
            // Opened while disconnected, or the session already ended: the lasting state says so.
            return;
        }

        IsStarting = true;
        NotifyAvailability();
        using var starting = Status.Begin(Localizer[L.PowerShellStatus.Starting], new PageStatusLog(L.PowerShellStatus.Starting), canCancel: false);
        try
        {
            await StartCoreAsync(directory).ConfigureAwait(true);
        }
        finally
        {
            // The state that lasts is set while the operation still has the line, so it goes from "starting" straight to the note.
            IsStarting = false;
            UpdateLasting();
            NotifyAvailability();
        }
    }

    private async Task StartCoreAsync(IDirectorySession directory)
    {
        var policies = ReadPoliciesAsync();
        if (_powerShell.Sessions is not { } factory)
        {
            _noEngine = true;
            await policies.ConfigureAwait(true);
            return;
        }

        IPowerShellSession? session = null;
        try
        {
            var request = new PowerShellSessionRequest(directory, AppVersion.Version, Localizer.Culture, _outputWidth, _powerShell.Actions);
            session = await factory.CreateAsync(request, _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _startFailure = ex;
        }

        await policies.ConfigureAwait(true);
        if (IsDisposed)
        {
            DisposeSession(session);
            return;
        }

        _session = session;
        if (session is not null)
        {
            Welcome(session);
        }

        OnPropertyChanged(nameof(Policies));
        OnPropertyChanged(nameof(PolicyNoteText));
        OnPropertyChanged(nameof(IsTrustedMode));
        OnPropertyChanged(nameof(WindowTitle));
    }

    /// <summary>Reads the policies; a failure is remembered (the lasting note then says it could not tell), never thrown.</summary>
    private async Task ReadPoliciesAsync()
    {
        if (_powerShell.Policies is not { } reader)
        {
            return;
        }

        try
        {
            _policies = await Task.Run(() => reader.ReadAsync(_lifetime.Token), _lifetime.Token).ConfigureAwait(true);
            _policyNote = PowerShellPolicyNotes.For(_policies, Localizer);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The window was closed.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _policiesUnreadable = true;
            _policiesFailure = ex;
        }
    }

    /// <summary>The first lines of the scrollback: the mode, how to get help, a lockdown, and the note about the policies.</summary>
    private void Welcome(IPowerShellSession session)
    {
        Output.AddLine(PowerShellLineKind.Information, Localizer[L.PowerShell.WelcomeMode]);
        Output.AddLine(PowerShellLineKind.Information, Localizer[L.PowerShell.WelcomeHelp]);
        if (IsLockdownEnforced(session))
        {
            Output.AddLine(PowerShellLineKind.Information, Localizer[L.PowerShell.WelcomeLockdown]);
        }

        if (_policyNote is { } note)
        {
            Output.AddLine(PowerShellLineKind.PolicyNote, note.Message);
        }
    }

    // ---- What lasts on the status line -------------------------------------------------------------------------

    /// <summary>
    /// The summary of the line: "domain · DC · account · mode". It is what the window says whenever nothing else is said - a
    /// note that lasts or a command that ran takes its place.
    /// </summary>
    private void UpdateSummary() =>
        Status.SetIdle(_info is { } info
            ? string.Join(" · ", info.DomainDnsName, info.Server, info.BoundIdentity, IsTrustedMode ? "FullLanguage" : "ConstrainedLanguage")
            : null);

    /// <summary>
    /// The one state that lasts, the most important of the window's conditions: the end of the session, a build without the engine, an
    /// engine that could not start, then the notes - what the policies record, that the policies could not be read, the trusted mode -
    /// one after the other when more than one is true. Every text is made here from the state, so it follows the language.
    /// </summary>
    private void UpdateLasting() => Status.SetLasting(LastingState());

    private PageStatus? LastingState()
    {
        if (SessionEndedStatus is { } ended)
        {
            return ended;
        }

        if (_info is null)
        {
            return PageStatus.Error(NoSessionMessage, new PageStatusLog(L.Errors.NotConnected), canClose: false);
        }

        if (_noEngine)
        {
            return PageStatus.Error(
                PageStatusText.Failure(Localizer[L.PowerShellStatus.NotAvailable], null, Localizer[L.PowerShellStatus.NotAvailableNext]),
                new PageStatusLog(L.PowerShellStatus.NotAvailable),
                canClose: false);
        }

        if (_startFailure is not null)
        {
            return PageStatus.Error(StartFailureText(), new PageStatusLog(L.PowerShellStatus.StartFailed), canClose: false);
        }

        var notes = new List<(string Text, PageStatusLog Log)>();
        if (_policyNote is { } policy)
        {
            notes.Add((policy.Message, policy.Log));
        }

        if (_policiesUnreadable)
        {
            notes.Add((
                PageStatusText.Failure(Localizer[L.PowerShellStatus.PolicyUnreadable], _policiesFailure is null ? null : Errors.Translate(_policiesFailure), Localizer[L.PowerShellStatus.PolicyUnreadableNext]),
                new PageStatusLog(L.PowerShellStatus.PolicyUnreadable)));
        }

        if (IsTrustedMode)
        {
            notes.Add((Localizer[L.PowerShellStatus.TrustedNote], new PageStatusLog(L.PowerShellStatus.TrustedNote)));
        }

        return notes.Count switch
        {
            0 => null,
            1 => PageStatus.Attention(notes[0].Text, notes[0].Log),
            _ => PageStatus.Attention(string.Join(' ', notes.Select(note => note.Text)), new PageStatusLog(L.PowerShellStatus.Notes, notes.Count)),
        };
    }

    /// <summary>"PowerShell could not be started: why. Close the window and open it again." in the language of now.</summary>
    private string StartFailureText()
    {
        var cause = _startFailure is PowerShellSessionUnavailableException { InnerException: { } inner } ? inner : _startFailure!;
        return PageStatusText.Failure(Localizer[L.PowerShellStatus.StartFailed], Errors.Translate(cause), Localizer[L.PowerShellStatus.StartFailedNext]);
    }

    /// <summary>
    /// The session has no runspace any more (<see cref="PowerShellSessionUnavailableException"/>): the window goes to the lasting state
    /// "could not be started", says so in the scrollback, and closes the session - it would refuse every command.
    /// </summary>
    private void LoseSession(IPowerShellSession session, Exception failure)
    {
        _startFailure = failure;
        if (ReferenceEquals(_session, session))
        {
            _session = null;
        }

        DisposeSession(session);
        Output.AddLine(PowerShellLineKind.Information, StartFailureText());
        UpdateLasting();
        NotifyMode();
    }

    // ---- The window ends ----------------------------------------------------------------------------------------

    /// <summary>The session of the window ended: no command runs against a connection that is gone.</summary>
    protected override void OnSessionEnded()
    {
        _scope?.Cancel();
        PendingPrompt?.Item.Cancel();
        UpdateLasting();
        NotifyAvailability();
        OnPropertyChanged(nameof(WindowTitle));
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        if (_policies.IsRecording)
        {
            _policyNote = PowerShellPolicyNotes.For(_policies, Localizer);
        }

        UpdateSummary();
        UpdateLasting();
        OnPropertyChanged(nameof(DroppedNote));
        OnPropertyChanged(nameof(PolicyNoteText));
        OnPropertyChanged(nameof(TrustedModeDisabledReason));
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        _lifetime.Cancel();
        PendingPrompt?.Item.Cancel();
        _scope?.Cancel();
        Input.CloseCompletion();
        var session = _session;
        _session = null;
        DisposeSession(session);
    }

    /// <summary>Closes the runspace without waiting for it: a pipeline that does not stop is given a few seconds, and the window is gone meanwhile.</summary>
    private static void DisposeSession(IPowerShellSession? session)
    {
        if (session is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The window is gone; there is nobody to tell and nothing of the directory in the failure.
            }
        });
    }

    private void NotifyAvailability()
    {
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(CanToggleTrustedMode));
        OnPropertyChanged(nameof(TrustedModeDisabledReason));
        RunCommand.NotifyCanExecuteChanged();
        ToggleTrustedModeCommand.NotifyCanExecuteChanged();
    }
}
