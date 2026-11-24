using AdMng.Core.PowerShell;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.PowerShell;

// The language mode (track R, decision D3): ConstrainedLanguage until the operator turns the trusted mode on, for this window only.
public sealed partial class PowerShellWindowViewModel
{
    /// <summary>The language mode of the window's runspace: <see cref="PowerShellLanguageMode.Constrained"/> until the operator turns the trusted mode on. Never remembered.</summary>
    public PowerShellLanguageMode LanguageMode => _session?.LanguageMode ?? PowerShellLanguageMode.Constrained;

    /// <summary>FullLanguage: any .NET code. Shown in the title and in a note that lasts.</summary>
    public bool IsTrustedMode => LanguageMode == PowerShellLanguageMode.Full;

    /// <summary>
    /// The trusted mode is being turned off: the session makes its new runspace (a fraction of a second). Meanwhile nothing runs, Tab
    /// completes nothing and the switch waits; the status line says so.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSwitchingMode { get; private set; }

    /// <summary>
    /// The switch can be pressed: the engine is ready, nothing runs, and a lockdown does not forbid the trusted mode. Turning it off is
    /// always possible. Exactly when <see cref="TrustedModeDisabledReason"/> is null.
    /// </summary>
    public bool CanToggleTrustedMode => TrustedModeDisabledReason is null;

    /// <summary>
    /// Why the switch is disabled - in every state it is disabled in (audit W-10): the session ended or there is none, the build has no
    /// engine, PowerShell could not be started, the trusted mode is being turned off, the engine is starting, a command runs, a lockdown
    /// forbids the trusted mode - or null while it can be pressed. Made when it is read, in the language of now.
    /// </summary>
    public string? TrustedModeDisabledReason
    {
        get
        {
            if (Session is null)
            {
                return NoSessionMessage;
            }

            if (_noEngine)
            {
                return Localizer[L.PowerShellStatus.NotAvailable];
            }

            if (_startFailure is not null)
            {
                return Localizer[L.PowerShell.TrustedBlockedFailed];
            }

            if (IsSwitchingMode)
            {
                return Localizer[L.PowerShell.TrustedBlockedSwitching];
            }

            if (_session is not { } session || IsStarting || IsSessionEnded)
            {
                return Localizer[L.PowerShell.TrustedBlockedStarting];
            }

            if (IsRunning)
            {
                return Localizer[L.PowerShell.TrustedBlockedRunning];
            }

            return !IsTrustedMode && IsLockdownEnforced(session) ? Localizer[L.PowerShell.TrustedBlockedLockdown] : null;
        }
    }

    /// <summary>A system lockdown (WDAC / AppLocker) forces ConstrainedLanguage: the session says so, and so may the policies read at the start.</summary>
    private bool IsLockdownEnforced(IPowerShellSession session) =>
        session.TrustedModeBlock == PowerShellTrustedModeBlock.SystemLockdown || _policies.Lockdown == PowerShellLockdownState.Enforce;

    /// <summary>
    /// The switch of the trusted mode. Turning it on asks first - what it allows, in the words of the domain's window, and that turning
    /// it off starts a new session - and is refused under a lockdown; turning it off does not ask. The mode is of this window only.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanToggleTrustedMode))]
    private async Task ToggleTrustedModeAsync()
    {
        if (_session is not { } session)
        {
            return;
        }

        if (IsTrustedMode)
        {
            await TurnTrustedModeOffAsync(session).ConfigureAwait(true);
            return;
        }

        var confirmed = await Services.Dialogs.ConfirmAsync(
            Localizer[L.PowerShell.TrustedConfirmTitle],
            Localizer.Format(L.PowerShell.TrustedConfirmMessage, ConfirmationDomain),
            destructive: true).ConfigureAwait(true);
        if (confirmed)
        {
            await TurnTrustedModeOnAsync().ConfigureAwait(true);
        }
    }

    /// <summary>On: the same runspace in FullLanguage, after the question (the session may have changed while it was open).</summary>
    private async Task TurnTrustedModeOnAsync()
    {
        if (_session is not { } session || IsDisposed || !IsReady)
        {
            return;
        }

        try
        {
            await session.SetLanguageModeAsync(PowerShellLanguageMode.Full, _lifetime.Token).ConfigureAwait(true);
        }
        catch (PowerShellSessionUnavailableException ex) when (!IsDisposed)
        {
            LoseSession(session, ex);
            return;
        }
        catch (InvalidOperationException ex) when (!IsDisposed)
        {
            ModeNotChanged(session, ex);
            return;
        }
        catch (Exception) when (IsDisposed || _lifetime.IsCancellationRequested)
        {
            return;
        }

        Output.AddLine(PowerShellLineKind.Information, Localizer[L.PowerShell.TrustedOn]);
        NotifyMode();
    }

    /// <summary>
    /// Off (audit PS-02): the session goes on in a new runspace in ConstrainedLanguage, so nothing the trusted mode defined runs in
    /// FullLanguage afterwards. While it is made the window is "switching" - the line says so, nothing runs, Tab completes nothing - and
    /// the completion list and a running highlight are dropped first. The history and the scrollback stay; the scrollback says that the
    /// variables, functions and aliases are gone, and whether the new runspace is under a lockdown (the session asked again). When the new
    /// runspace cannot be made the window is not left trusted: PowerShell could not be started, which lasts.
    /// </summary>
    private async Task TurnTrustedModeOffAsync(IPowerShellSession session)
    {
        Input.CancelPendingWork();
        IsSwitchingMode = true;
        NotifyAvailability();
        Exception? refused = null;
        using (Status.Begin(Localizer[L.PowerShellStatus.SwitchingOff], new PageStatusLog(L.PowerShellStatus.SwitchingOff), canCancel: false))
        {
            try
            {
                await session.SetLanguageModeAsync(PowerShellLanguageMode.Constrained, _lifetime.Token).ConfigureAwait(true);
                if (!IsDisposed)
                {
                    Output.AddLine(PowerShellLineKind.Information, Localizer[L.PowerShell.TrustedOff]);
                    if (IsLockdownEnforced(session))
                    {
                        Output.AddLine(PowerShellLineKind.Information, Localizer[L.PowerShell.WelcomeLockdown]);
                    }
                }
            }
            catch (PowerShellSessionUnavailableException ex) when (!IsDisposed)
            {
                IsSwitchingMode = false;
                LoseSession(session, ex);
            }
            catch (InvalidOperationException ex) when (!IsDisposed)
            {
                refused = ex;
            }
            catch (Exception) when (IsDisposed || _lifetime.IsCancellationRequested)
            {
                // The window closed meanwhile: the session is closed with it.
            }
            finally
            {
                // The state that lasts is set while the operation still has the line, so it goes from "switching" straight to it.
                IsSwitchingMode = false;
                if (!IsDisposed)
                {
                    NotifyMode();
                }
            }
        }

        if (refused is not null && !IsDisposed)
        {
            ModeNotChanged(session, refused);
        }
        else if (!IsDisposed)
        {
            Input.Highlight();
        }
    }

    /// <summary>
    /// The engine refused the switch - a command started while the question was open, or a lockdown came into force: the mode stays as
    /// it was. The scrollback always says so; the line only when no command runs - a result would end the operation that owns it. The
    /// reason is the window's own words for what it knows - a command runs, a lockdown - and the program's error translator for anything
    /// else; never the engine's English message alone.
    /// </summary>
    private void ModeNotChanged(IPowerShellSession session, Exception refusal)
    {
        var reason = IsRunning || session.IsRunning
            ? Localizer[L.PowerShell.TrustedBlockedRunning]
            : IsLockdownEnforced(session)
                ? Localizer[L.PowerShell.TrustedBlockedLockdown]
                : Errors.Translate(refusal);
        var text = PageStatusText.Failure(Localizer[L.PowerShellStatus.ModeNotChanged], reason);
        Output.AddLine(PowerShellLineKind.Information, text);
        if (!IsRunning)
        {
            Status.Show(PageStatus.Attention(text, new PageStatusLog(L.PowerShellStatus.ModeNotChanged)));
        }

        NotifyMode();
    }

    private void NotifyMode()
    {
        UpdateSummary();
        UpdateLasting();
        OnPropertyChanged(nameof(LanguageMode));
        OnPropertyChanged(nameof(IsTrustedMode));
        OnPropertyChanged(nameof(WindowTitle));
        NotifyAvailability();
    }
}
