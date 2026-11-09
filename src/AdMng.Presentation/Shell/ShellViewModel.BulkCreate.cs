using AdMng.Core.Model;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Shell;

/// <summary>
/// The entry point of the bulk creation wizard <em>Zakładanie wielu kont</em> (docs/dev/phase3-bulk-create.md 2.7):
/// <c>new.bulkCreate</c> (<em>Wiele kont…</em>, group <em>Nowy</em>; decision 1 explains the id) opens the batch editor in
/// create mode with the users of the current container – or of the container the tree's or the list's <em>Nowy</em>
/// submenu passes. The wizard reports <c>CannotCreateChild</c> itself for the chosen kind, so the command does not read
/// the create permissions.
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>
    /// The wizard is usable: false in the contract commit (its steps are stubs until wave 2), so the palette does not offer
    /// <c>new.bulkCreate</c> and the menus show it disabled. B4 sets it to true with the create mode of the batch editor.
    /// </summary>
    internal static bool IsBulkCreateAvailable => false;

    /// <summary>The commands whose CanExecute depends on the session (refreshed with the other state commands).</summary>
    private IRelayCommand[] BulkCreateStateCommands => [BulkCreateCommand];

    /// <summary>The descriptor of <c>new.bulkCreate</c> (category <em>Nowy</em>, with the other <c>new.*</c> commands).</summary>
    private IEnumerable<CommandDescriptor> BulkCreateCommandDescriptors() =>
    [
        new("new.bulkCreate", L.BulkCreate.Command, L.CommandCategories.New, string.Empty, BulkCreateCommand),
    ];

    /// <summary><em>Wiele kont…</em>: the batch editor in create mode for users in <paramref name="parentDn"/> (default: the current container).</summary>
    [RelayCommand(CanExecute = nameof(CanBulkCreate))]
    private Task BulkCreateAsync(string? parentDn) =>
        _dialogs.ShowBatchEditorAsync(new BatchEditorRequest([])
        {
            Create = new BulkCreateRequest(ObjectKind.User, CreateTarget(ObjectKind.User, parentDn)),
        });

    private bool CanBulkCreate(string? parentDn) => IsBulkCreateAvailable && IsConnected;
}
