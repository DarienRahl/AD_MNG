using System.Windows;
using AdMng.Presentation.Computers;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.Services;

/// <summary>The computer windows of a connection window (K1): the review (one) and the remote actions (one per computer).</summary>
public sealed partial class ToolWindows
{
    /// <summary>Singleton key of the computer review window (one per connection window).</summary>
    public const string ComputerReviewKey = "tools:computer-review";

    private ComputerViewModelFactory ComputerFactory => _services.GetRequiredService<ComputerViewModelFactory>();

    /// <summary>"Przegląd komputerów" (modeless; an open one is activated).</summary>
    public Window ShowComputerReview() => ShowSingleton(ComputerReviewKey, () => ComputerFactory.CreateReview());

    /// <summary>"Zdalne akcje" of one computer (modeless; the open window of the same computer is activated).</summary>
    public Window ShowRemoteComputer(string distinguishedName) =>
        ShowSingleton(RemoteComputerViewModel.WindowKey(distinguishedName), () => ComputerFactory.CreateRemote(distinguishedName));
}
