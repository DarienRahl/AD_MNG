namespace AdMng.Presentation.Shell;

/// <summary>Status bar indicator of the virtual (VLV) list mode of very large containers.</summary>
public sealed partial class ShellViewModel
{
    /// <summary>
    /// The selected tab shows a very large container as a server sorted virtual list (VLV): the status bar shows a
    /// badge explaining that rows are read while scrolling and that sorting and filtering run on the server.
    /// </summary>
    public bool IsVirtualListShown => SelectedDocument?.IsVirtual == true;
}
