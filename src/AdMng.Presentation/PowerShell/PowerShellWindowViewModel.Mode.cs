using AdMng.Core.PowerShell;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.PowerShell;

// The language mode (track R, decision D3): ConstrainedLanguage until the operator turns the trusted mode on, for this window only.
public sealed partial class PowerShellWindowViewModel
{
    /// <summary>The language mode of the window's runspace: <see cref="PowerShellLanguageMode.Constrained"/> until the operator turns the trusted mode on. Never remembered.</summary>
    public PowerShellLanguageMode LanguageMode => _session?.LanguageMode ?? PowerShellLanguageMode.Constrained;

    /// <summary>FullLanguage: any .NET code. Shown in the title and in a note that lasts.</summary>
    public bool IsTrustedMode => LanguageMode == PowerShellLanguageMode.Full;

    /// <summary>The switch can be pressed: the engine is ready, nothing runs, and a lockdown does not forbid the trusted mode. Turning it off is always possible.</summary>
    public bool CanToggleTrustedMode => IsReady && !IsRunning && !IsStarting && (IsTrustedMode || !IsLockdownEnforced(_session!));

    /// <summary>Why the switch is disabled (a lockdown, a command running, the engine starting), or null while it can be pressed.</summary>
    public string? TrustedModeDisabledReason
    {
        get
        {
            if (_session is not { } session || IsStarting)
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
    /// The switch of the trusted mode. Turning it on asks first - what it allows, in the words of the domain's window - and is
    /// refused under a lockdown; turning it off does not ask. The mode is of this window only.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanToggleTrustedMode))]
    private async Task ToggleTrustedModeAsync()
    {
        if (_session is null)
        {
            return;
        }

        if (IsTrustedMode)
        {
            await SwitchAsync(PowerShellLanguageMode.Constrained).ConfigureAwait(true);
            return;
        }

        var confirmed = await Services.Dialogs.ConfirmAsync(
            Localizer[L.PowerShell.TrustedConfirmTitle],
            Localizer.Format(L.PowerShell.TrustedConfirmMessage, ConfirmationDomain),
            destructive: true).ConfigureAwait(true);
        if (confirmed)
        {
            await SwitchAsync(PowerShellLanguageMode.Full).ConfigureAwait(true);
        }
    }

    private async Task SwitchAsync(PowerShellLanguageMode mode)
    {
        if (_session is not { } session || IsDisposed)
        {
            return;
        }

        try
        {
            await session.SetLanguageModeAsync(mode, _lifetime.Token).ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            // A command started while the question was open, or a lockdown came into force: the mode stays as it was. The scrollback
            // always says so; the line only when no command runs - a result would end the operation that owns it.
            var text = PageStatusText.Failure(Localizer[L.PowerShellStatus.ModeNotChanged], ex.Message);
            Output.AddLine(PowerShellLineKind.Information, text);
            if (!IsRunning)
            {
                Status.Show(PageStatus.Attention(text, new PageStatusLog(L.PowerShellStatus.ModeNotChanged)));
            }

            NotifyMode();
            return;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }

        Output.AddLine(PowerShellLineKind.Information, Localizer[session.LanguageMode == PowerShellLanguageMode.Full ? L.PowerShell.TrustedOn : L.PowerShell.TrustedOff]);
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
