using System.Collections.ObjectModel;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Correspondence;

/// <summary>The state of the window's Outlook connection.</summary>
public enum OutlookState
{
    Unknown,
    Connecting,
    Connected,
    NotInstalled,
    Failed,
    NotSupported,
}

/// <summary>
/// The Outlook connection of the Korespondencja window (design note 6.4): state, profile accounts, the chosen account and
/// "w imieniu"; the session is created on demand (<see cref="EnsureConnectedAsync"/>) and kept for the window's life;
/// "Sprawdź Outlooka" shows the diagnostics (with the journal, Downloads and signatures folders). Failures show the
/// localized code text (<c>Correspondence.Outlook.{code}</c>), never a modal error.
/// </summary>
public sealed partial class OutlookConnectionViewModel : ObservableObject, IAsyncDisposable
{
    public OutlookConnectionViewModel(DialogServices services, CorrespondenceServices correspondence)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Correspondence = correspondence ?? throw new ArgumentNullException(nameof(correspondence));
    }

    [ObservableProperty]
    public partial OutlookState State { get; private set; } = OutlookState.Unknown;

    /// <summary>"Outlook: połączono (2 konta)" …</summary>
    [ObservableProperty]
    public partial string StateText { get; private set; } = string.Empty;

    /// <summary>The accounts of the profile.</summary>
    public ObservableCollection<OutlookAccount> Accounts { get; } = [];

    /// <summary>The account to send from (null: the profile's default account).</summary>
    [ObservableProperty]
    public partial OutlookAccount? SelectedAccount { get; set; }

    /// <summary>"W imieniu" (SentOnBehalfOfName), or empty.</summary>
    [ObservableProperty]
    public partial string OnBehalfOf { get; set; } = string.Empty;

    /// <summary>The connected session, or null.</summary>
    internal IOutlookSession? Session { get; private set; }

    private DialogServices Services { get; }

    private CorrespondenceServices Correspondence { get; }

    /// <summary>The connected session: connects when there is none (state and text updated); null when Outlook cannot be used.</summary>
    public Task<IOutlookSession?> EnsureConnectedAsync(CancellationToken cancellationToken = default) => Task.FromResult(Session);

    /// <summary>Takes the account and "w imieniu" of the settings (the account is applied after connect).</summary>
    public void LoadFrom(CorrespondenceSettings settings)
    {
    }

    /// <summary>Writes the account and "w imieniu" into the settings.</summary>
    public void SaveTo(CorrespondenceSettings settings)
    {
    }

    /// <summary>Ends the session (the window closed).</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [RelayCommand]
    private Task ConnectAsync() => Task.CompletedTask;

    /// <summary>"Sprawdź Outlooka": the diagnostics report.</summary>
    [RelayCommand]
    private Task CheckAsync() => Task.CompletedTask;
}
