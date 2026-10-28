using AdMng.Core.Model;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Shell;

/// <summary>
/// Korespondencja (U5, design note docs/dev/u5-outlook-correspondence.md 6.3): "Narzędzia → Korespondencja (Outlook)…"
/// (one window per connection window, no recipients), "Wyślij korespondencję…" for the selected users, contacts and groups
/// (their members) and "Zapytaj właścicieli skrzynek…" with the selected user objects as the scope. The commands exist only
/// when the host registered <see cref="ICorrespondenceLauncher"/>.
/// </summary>
public sealed partial class ShellViewModel
{
    private IRelayCommand[]? _correspondenceStateCommands;

    /// <summary>Korespondencja is available (the host registered its launcher).</summary>
    public bool CanUseCorrespondence => _correspondence is not null;

    private IRelayCommand[] CorrespondenceStateCommands => _correspondenceStateCommands ??=
    [
        ShowCorrespondenceCommand, SendCorrespondenceCommand, AskMailboxOwnersCommand,
    ];

    private IEnumerable<CommandDescriptor> CorrespondenceCommandDescriptors()
    {
        if (_correspondence is null)
        {
            return [];
        }

        const string obj = L.CommandCategories.Object;
        const string tools = L.CommandCategories.Tools;
        return
        [
            new("tools.correspondence", L.Correspondence.Command, tools, string.Empty, ShowCorrespondenceCommand),
            new("object.correspondence", L.Correspondence.ObjectCommand, obj, string.Empty, SendCorrespondenceCommand),
            new("object.mailboxOwners", L.Correspondence.MailboxOwnersCommand, obj, string.Empty, AskMailboxOwnersCommand),
        ];
    }

    /// <summary>"Korespondencja (Outlook)…": the window of this connection window without recipients.</summary>
    [RelayCommand(CanExecute = nameof(CanShowCorrespondence))]
    private Task ShowCorrespondenceAsync() =>
        _correspondence?.ShowCorrespondenceAsync(new CorrespondenceRequest([])) ?? Task.CompletedTask;

    private bool CanShowCorrespondence() => _correspondence is not null && IsConnected;

    /// <summary>"Wyślij korespondencję…": a new window with the selected users, contacts and groups.</summary>
    [RelayCommand(CanExecute = nameof(CanSendCorrespondence))]
    private Task SendCorrespondenceAsync()
    {
        var dns = CurrentSelection.Where(r => IsCorrespondenceSource(r.Kind)).Select(r => r.Dn).ToList();
        return _correspondence is null || dns.Count == 0
            ? Task.CompletedTask
            : _correspondence.ShowCorrespondenceAsync(new CorrespondenceRequest(dns));
    }

    private bool CanSendCorrespondence() => _correspondence is not null && IsConnected && CurrentSelection.Any(r => IsCorrespondenceSource(r.Kind));

    /// <summary>"Zapytaj właścicieli skrzynek…": a new window on the "Skrzynki" tab with the selected user objects as the scope.</summary>
    [RelayCommand(CanExecute = nameof(CanAskMailboxOwners))]
    private Task AskMailboxOwnersAsync()
    {
        var dns = CurrentSelection.Where(r => IsUserObject(r.Kind)).Select(r => r.Dn).ToList();
        return _correspondence is null || dns.Count == 0
            ? Task.CompletedTask
            : _correspondence.ShowCorrespondenceAsync(new CorrespondenceRequest(dns, CorrespondenceStart.MailboxOwners));
    }

    private bool CanAskMailboxOwners() => _correspondence is not null && IsConnected && CurrentSelection.Any(r => IsUserObject(r.Kind));

    private static bool IsUserObject(ObjectKind kind) => kind is ObjectKind.User or ObjectKind.InetOrgPerson;

    private static bool IsCorrespondenceSource(ObjectKind kind) => IsUserObject(kind) || kind is ObjectKind.Contact or ObjectKind.Group;
}
