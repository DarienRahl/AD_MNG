using System.Windows;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.GroupPolicy;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.ActiveDirectory;
using AdMng.Presentation.GroupPolicy;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests.GroupPolicy;

/// <summary>
/// What the status line tests of the Group Policy documents share (track V, stage V2b-3): the main window over the fake
/// directory with the Polish GPO sample, a document opened as a tab with its view, a read of the domain controller the
/// test holds and releases, and the checks that hold for every converted view.
/// </summary>
internal static class GroupPolicyStatusUiSupport
{
    /// <summary>How long a held read waits for the test at most (a failing test must not keep a pool thread for good).</summary>
    public static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);

    /// <summary>The connected main window with the GPO management sample in the directory and in SYSVOL.</summary>
    public static async Task<(MainWindow Main, GroupPolicyManagementSample Sample)> OpenMainAsync(UiTestContext context, Action<GroupPolicyManagementSample>? arrange = null)
    {
        var sample = context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        arrange?.Invoke(sample);
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        await main.Shell.InitializeAsync();
        await UiTestSupport.WaitForIdleAsync();
        return (main, sample);
    }

    /// <summary>Opens a Group Policy document as a tab, waits until it was read (or says why not) and returns it with its view.</summary>
    public static async Task<(TDocument Document, TView View)> OpenAsync<TDocument, TView>(MainWindow main, GroupPolicyDocumentRequest request)
        where TDocument : GroupPolicyDocumentBase
        where TView : UserControl
    {
        var document = Assert.IsType<TDocument>(main.Shell.OpenGroupPolicyDocument(request));
        await UiWait.UntilAsync(() => !document.IsLoading && !document.Status.IsWorking, $"the document {typeof(TDocument).Name}");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.Same(document, main.Shell.SelectedTab);
        var view = Assert.Single(UiWait.Descendants<TView>(main));
        Assert.Same(document, view.DataContext);
        return (document, view);
    }

    /// <summary>
    /// The session is on a domain controller that is not the PDC emulator (dc02.contoso.test is): the documents say
    /// where changes are made.
    /// </summary>
    public static void SeedAnotherPdcEmulator(UiTestContext context)
    {
        var session = context.Directory.Session;
        var servers = $"CN=Servers,CN=Warszawa,CN=Sites,{session.Info.ConfigurationNamingContext}";
        session.Seed(servers, new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "serversContainer" } });
        var server = $"CN=DC02,{servers}";
        session.Seed(server, new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "server" }, [Attr.DnsHostName] = "dc02.contoso.test" });
        session.Seed($"CN=NTDS Settings,{server}", new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", "nTDSDSA" } });
        session.SeedAttributes(session.Info.DefaultNamingContext, new Dictionary<string, object?> { ["fSMORoleOwner"] = $"CN=NTDS Settings,{server}" });
    }

    /// <summary>
    /// The one status line of a document view: named PageStatus, over the status of the document, of the fixed height
    /// while it says the summary, and the only one in the view.
    /// </summary>
    public static void AssertTheLineOf(StatusLine line, FrameworkElement view, GroupPolicyDocumentBase document)
    {
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(view)));
        Assert.Equal("PageStatus", line.Name);
        Assert.True(line.IsVisible);
        Assert.Same(document.Status.Current, line.Status);
        Assert.Same(document.Status.CloseCommand, line.CloseCommand);
    }

    /// <summary>
    /// The old places of feedback are gone from a converted view - and from the controls it hosts: no progress bar and
    /// no banner of the styles of before.
    /// </summary>
    public static void AssertNoOldFeedback(FrameworkElement view)
    {
        var errorBar = Application.Current.FindResource("Ad.ErrorBar");
        var warningBar = Application.Current.FindResource("Ad.WarningBar");
        Assert.Empty(UiWait.Descendants<ProgressBar>(view));
        Assert.DoesNotContain(
            UiWait.Descendants<Border>(view),
            border => ReferenceEquals(border.Style, errorBar) || ReferenceEquals(border.Style, warningBar));
        Assert.Single(UiWait.Descendants<StatusLine>(view));
    }

    /// <summary>The notes of a document are there, under its line, and say nothing: SYSVOL is available and the session is on the PDC emulator.</summary>
    public static void AssertNoNotes(GroupPolicyNotes notes)
    {
        Assert.False(notes.SysvolNote.IsVisible);
        Assert.False(notes.PdcPanel.IsVisible);
        Assert.False(notes.ChangeDcButton.IsVisible);
    }

    /// <summary>
    /// How much higher than one line the status line is right now: a result that does not fit one line wraps (to three
    /// lines at most), and what lies under the line moves down by as much - by nothing when the result fits.
    /// </summary>
    public static double GrowthOf(StatusLine line)
    {
        var growth = line.ActualHeight - (double)Application.Current.FindResource("Ad.Size.StatusLine");
        Assert.InRange(growth, -0.5, 40.5);
        return growth;
    }

    public static double TopOf(FrameworkElement element, UIElement relativeTo) =>
        element.TranslatePoint(new Point(0, 0), relativeTo).Y;

    public static double LeftOf(FrameworkElement element, UIElement relativeTo) =>
        element.TranslatePoint(new Point(0, 0), relativeTo).X;
}

/// <summary>
/// A slow domain controller: the next call of the fake session that is made on a pool thread - the reads of the
/// documents are - and matches waits there until the test lets it go (disposing does). "Working" and "Stopping…" are
/// then states that last until the test changes them. A call on the UI thread is never held: it would hold the test.
/// </summary>
internal sealed class HeldRead : IDisposable
{
    private readonly FakeDirectorySession _session;
    private readonly ManualResetEventSlim _reached = new();
    private readonly ManualResetEventSlim _release = new();
    private int _armed = 1;

    public HeldRead(FakeDirectorySession session, Func<FakeOperation, bool>? matches = null)
    {
        _session = session;
        var ui = Environment.CurrentManagedThreadId;
        session.BeforeOperation = operation =>
        {
            if (Environment.CurrentManagedThreadId != ui && (matches is null || matches(operation)) && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _reached.Set();
                _release.Wait(GroupPolicyStatusUiSupport.HoldLimit);
            }
        };
    }

    /// <summary>The call is being held.</summary>
    public bool IsReached => _reached.IsSet;

    /// <summary>Lets the held call go on; later calls are not held.</summary>
    public void Release()
    {
        Volatile.Write(ref _armed, 0);
        _release.Set();
    }

    public void Dispose()
    {
        Release();
        _session.BeforeOperation = null;
    }
}
