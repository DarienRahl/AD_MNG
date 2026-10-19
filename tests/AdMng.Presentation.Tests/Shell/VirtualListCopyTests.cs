using AdMng.Presentation.Lists;
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

    private static async Task<(TestHarness Harness, ShellViewModel Shell, VirtualRowList Rows)> OpenVirtualAsync()
    {
        var harness = await TestHarness.ConnectedAsync(bulkUsers: 120);
        var shell = harness.CreateShell();
        await shell.InitializeAsync();
        var document = shell.SelectedDocument!;
        document.VirtualThreshold = 50;
        document.VirtualPageSize = 10;
        await shell.NavigateAsync(ListLocation.ForContainer(Masowe));
        Assert.True(document.IsVirtual);
        var rows = document.VirtualRows!;
        Assert.All(Enumerable.Range(0, 10), i => Assert.NotNull(rows.GetLoaded(i)));
        return (harness, shell, rows);
    }
}
