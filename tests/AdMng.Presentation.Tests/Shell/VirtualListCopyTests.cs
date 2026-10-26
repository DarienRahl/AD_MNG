using AdMng.Presentation.Lists;
using AdMng.Presentation.Services;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Tests.TestDoubles;

namespace AdMng.Presentation.Tests.Shell;

/// <summary>
/// Copying from a very large container shown as a virtual list (VLV): the copy follows the server's sort order like in a
/// loaded list, and "Loading…" placeholders are never copied (phase 1 copy + navigation tracks together).
/// </summary>
public class VirtualListCopyTests
{
    private const string Masowe = "OU=Masowe,DC=contoso,DC=test";

    private static string UserDn(int i) => $"CN=Użytkownik {i:0000},{Masowe}";

    [Fact]
    public async Task CopyDn_SelectedInReverse_FollowsTheVirtualListOrder()
    {
        var (harness, shell, rows) = await OpenVirtualAsync();
        var document = shell.SelectedDocument!;
        List<ObjectRowViewModel> selected = [rows[7], rows[2], rows[5]];
        document.SelectedItem = selected[0];
        document.SelectedItems = selected;

        shell.CopyDnCommand.Execute(null);

        Assert.Equal(string.Join(Environment.NewLine, UserDn(2), UserDn(5), UserDn(7)), harness.Clipboard.Text);
    }

    [Fact]
    public async Task CopyAsName_SelectedInReverse_FollowsTheVirtualListOrder()
    {
        var (harness, shell, rows) = await OpenVirtualAsync();
        var document = shell.SelectedDocument!;
        List<ObjectRowViewModel> selected = [rows[9], rows[0], rows[4]];
        document.SelectedItem = selected[0];
        document.SelectedItems = selected;

        await shell.CopyAsCommand.ExecuteAsync(CopyFormat.Name);

        Assert.Equal(string.Join(Environment.NewLine, "Użytkownik 0000", "Użytkownik 0004", "Użytkownik 0009"), harness.Clipboard.Text);
    }

    [Fact]
    public async Task Placeholder_AloneSelected_IsNotCopied()
    {
        var (harness, shell, rows) = await OpenVirtualAsync();
        var document = shell.SelectedDocument!;
        harness.Directory.HoldVirtualList = _ => new TaskCompletionSource().Task;
        var placeholder = rows[95];
        Assert.True(placeholder.IsPlaceholder);

        document.SelectedItems = null;
        document.SelectedItem = placeholder;

        Assert.False(shell.CopyDnCommand.CanExecute(null));
        Assert.False(shell.CopyRowsCommand.CanExecute(null));
        Assert.False(shell.CopyAsCommand.CanExecute(CopyFormat.Name));
        Assert.Null(harness.Clipboard.Text);
    }

    [Fact]
    public async Task Placeholders_SelectedInTheView_DoNotEnableTheCopyCommands()
    {
        var (harness, shell, rows) = await OpenVirtualAsync();
        var document = shell.SelectedDocument!;
        harness.Directory.HoldVirtualList = _ => new TaskCompletionSource().Task;
        var placeholder = rows[95];
        Assert.True(placeholder.IsPlaceholder);

        // The view's live selection holds what the list control selected - also a "Loading..." row (End, PageDown).
        document.SelectedItems = new List<ObjectRowViewModel> { placeholder };
        document.SelectedItem = placeholder;

        Assert.False(shell.CopyDnCommand.CanExecute(null));
        Assert.False(shell.CopyRowsCommand.CanExecute(null));
        Assert.False(shell.CopyRowsWithHeadersCommand.CanExecute(null));
        Assert.False(shell.CopyAsCommand.CanExecute(CopyFormat.Name));

        // A range of rows that are all still loading (Shift+End) neither.
        document.SelectedItems = new List<ObjectRowViewModel> { placeholder, rows[96], rows[97] };
        Assert.False(shell.CopyDnCommand.CanExecute(null));
        Assert.False(shell.CopyRowsCommand.CanExecute(null));
        Assert.False(shell.CopyAsCommand.CanExecute(CopyFormat.Mail));
        Assert.Null(harness.Clipboard.Text);
    }

    [Fact]
    public async Task SelectionWithRowsStillLoading_CopiesTheLoadedRows_AndSaysHowManyWereLeftOut()
    {
        var (harness, shell, rows) = await OpenVirtualAsync();
        var document = shell.SelectedDocument!;
        harness.Directory.HoldVirtualList = _ => new TaskCompletionSource().Task;
        List<ObjectRowViewModel> selected = [rows[1], rows[95], rows[3], rows[96]];
        Assert.Equal([false, true, false, true], selected.Select(r => r.IsPlaceholder));
        document.SelectedItem = selected[0];
        document.SelectedItems = selected;

        await shell.CopyRowsCommand.ExecuteAsync(null);
        var lines = harness.Clipboard.Text!.Split(Environment.NewLine);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("Użytkownik 0001\t", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("Użytkownik 0003\t", lines[1], StringComparison.Ordinal);
        Assert.Equal((NotificationSeverity.Warning, "Copied rows: 2. Rows still loading were not copied: 2."), harness.Notifications.Items[^1]);

        shell.CopyDnCommand.Execute(null);
        Assert.Equal(string.Join(Environment.NewLine, UserDn(1), UserDn(3)), harness.Clipboard.Text);
        Assert.Equal(NotificationSeverity.Warning, harness.Notifications.Items[^1].Severity);
        Assert.EndsWith("Rows still loading were not copied: 2.", harness.Notifications.Items[^1].Message, StringComparison.Ordinal);

        await shell.CopyAsCommand.ExecuteAsync(CopyFormat.Name);
        Assert.Equal(string.Join(Environment.NewLine, "Użytkownik 0001", "Użytkownik 0003"), harness.Clipboard.Text);
        Assert.Equal(NotificationSeverity.Warning, harness.Notifications.Items[^1].Severity);
        Assert.EndsWith("Rows still loading were not copied: 2.", harness.Notifications.Items[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectedRows_OfPagesEvictedFromTheCache_KeepTheirPlaceAndIdentity()
    {
        var (harness, shell, rows) = await OpenVirtualAsync(maxCachedPages: 2);
        var document = shell.SelectedDocument!;
        var early = rows.GetLoaded(3)!;

        // Scrolled to the end: two more pages are read, the first page is evicted from the cache.
        await LoadAsync(rows, 55);
        await LoadAsync(rows, 115);
        Assert.Null(rows.GetLoaded(3));
        var late = rows.GetLoaded(115)!;

        // Clicked in the reverse order: the copy still follows the list.
        List<ObjectRowViewModel> selected = [late, early];
        document.SelectedItem = late;
        document.SelectedItems = selected;
        shell.CopyDnCommand.Execute(null);
        Assert.Equal(string.Join(Environment.NewLine, UserDn(3), UserDn(115)), harness.Clipboard.Text);

        // The language changes meanwhile: the row out of the cache is relocalized with the shown ones.
        Assert.Equal("User", early.TypeText);
        harness.Localizer.SetCulture(System.Globalization.CultureInfo.GetCultureInfo("pl-PL"));

        // Scrolled back: the page is read again and the selected row keeps its identity (the view shows it selected).
        await LoadAsync(rows, 3);
        Assert.Same(early, rows.GetLoaded(3));
        Assert.Equal(3, rows.IndexOf(early));
        Assert.Equal("Użytkownik", early.TypeText);
        await shell.CopyAsCommand.ExecuteAsync(CopyFormat.Name);
        Assert.Equal(string.Join(Environment.NewLine, "Użytkownik 0003", "Użytkownik 0115"), harness.Clipboard.Text);
    }

    private static async Task LoadAsync(VirtualRowList rows, int index)
    {
        _ = rows[index];
        await Lists.TestWait.UntilAsync(() => rows.GetLoaded(index) is not null);
        await rows.Idle;
    }

    private static async Task<(TestHarness Harness, ShellViewModel Shell, VirtualRowList Rows)> OpenVirtualAsync(int maxCachedPages = VirtualRowList.DefaultMaxCachedPages)
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: 120);
        var shell = harness.CreateShell();
        await shell.InitializeAsync();
        var document = shell.SelectedDocument!;
        document.VirtualThreshold = 50;
        document.VirtualPageSize = 10;
        document.VirtualMaxCachedPages = maxCachedPages;
        await shell.NavigateAsync(ListLocation.ForContainer(Masowe));
        Assert.True(document.IsVirtual);
        var rows = document.VirtualRows!;
        Assert.All(Enumerable.Range(0, 10), i => Assert.NotNull(rows.GetLoaded(i)));
        return (harness, shell, rows);
    }
}
