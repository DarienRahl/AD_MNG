using AdMng.App.Services;
using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Computers;
using AdMng.Core.Computers.Remote;
using AdMng.Core.Directory;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Computers;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Views;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests.Computers;

/// <summary>Helpers of the computer UI tests (K1).</summary>
internal static class ComputerUiSupport
{
    /// <summary>
    /// A computer view model factory of the connection <paramref name="services"/> whose dialogs are the real
    /// <see cref="DialogService"/> (it implements <see cref="IComputerDialogs"/>), for contexts whose IDialogService is a
    /// scripted wrapper without the computer windows (the screenshot tests).
    /// </summary>
    public static ComputerViewModelFactory FactoryWithComputerDialogs(IServiceProvider services)
    {
        var scripted = services.GetRequiredService<DialogServices>();
        var dialogs = new DialogServices(
            scripted.Sessions, scripted.Localizer, scripted.Dispatcher, scripted.Settings, scripted.Errors, scripted.Changes, scripted.Time,
            services.GetRequiredService<DialogService>(), scripted.Files, scripted.Clipboard, scripted.Notifications, scripted.Coordinator,
            scripted.Activities, scripted.Lists, scripted.Regional, scripted.Connection);
        var resolver = services.GetService<IHostAddressResolver>();
        var ping = services.GetService<IPingService>();
        var actions = new ComputerActions(dialogs, services.GetRequiredService<IChangePlanner>(), services.GetService<ILocalToolLauncher>(), ping, resolver);
        return new ComputerViewModelFactory(
            dialogs,
            services.GetRequiredService<BatchEngines>(),
            services.GetRequiredService<ViewLibrary>(),
            services.GetRequiredService<WindowsReleaseCatalog>(),
            actions,
            services.GetRequiredService<IAuditLog>(),
            services.GetRequiredService<PowerShellHistory>(),
            services.GetService<IDirectoryConnector>(),
            resolver,
            services.GetService<IRemoteComputerClient>(),
            ping);
    }
}
