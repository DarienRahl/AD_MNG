using AdMng.Core.Batch;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Connection;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Presentation.Views;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Presentation.Tests;

public sealed class ServiceRegistrationTests : IDisposable
{
    private readonly TempAppPaths _paths = new();

    public void Dispose() => _paths.Dispose();

    [Fact]
    public void AddAdMngPresentation_ResolvesAllViewModels_WhenTheHostProvidesUiAndCoreServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IUiDispatcher, SynchronousUiDispatcher>();
        services.AddSingleton<IDialogService, ScriptedDialogService>();
        services.AddSingleton<IFileDialogService, FakeFileDialogService>();
        services.AddSingleton<IClipboardService, FakeClipboard>();
        services.AddSingleton<INotificationService, RecordingNotificationService>();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddSingleton<IThemeService, Dialogs.FakeThemeService>();
        services.AddSingleton<IDirectoryConnector>(new FakeDirectoryConnector(TestDirectory.Build(TestHarness.Now)));
        services.AddSingleton<IChangePlanner, RecordingChangePlanner>();
        services.AddSingleton<IBatchExecutor, RecordingBatchExecutor>();
        services.AddSingleton<ICsvCodec, SimpleCsvCodec>();
        services.AddAttributeFormatter(_ => new PassThroughAttributeFormatter());

        services.AddAdMngPresentation();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.NotNull(provider.GetRequiredService<ConnectViewModel>());
        var shell = provider.GetRequiredService<ShellViewModel>();
        Assert.NotSame(shell, provider.GetRequiredService<ShellViewModel>());
        Assert.NotNull(provider.GetRequiredService<NavigationTreeViewModel>());
        Assert.NotNull(provider.GetRequiredService<ObjectListViewModel>());
        Assert.NotNull(provider.GetRequiredService<SavedViewEditorViewModel>());
        Assert.Same(provider.GetRequiredService<ObjectListServices>(), provider.GetRequiredService<ObjectListServices>());

        // B2: window view models are created by the factory; parameterless ones also resolve directly.
        var factory = provider.GetRequiredService<IDialogViewModelFactory>();
        Assert.IsType<DialogViewModelFactory>(factory);
        Assert.NotNull(provider.GetRequiredService<SessionLogViewModel>());
        Assert.NotNull(provider.GetRequiredService<PowerShellHistoryViewModel>());
        Assert.NotNull(provider.GetRequiredService<SettingsViewModel>());
        Assert.NotNull(provider.GetRequiredService<CsvImportViewModel>());
        Assert.NotNull(provider.GetRequiredService<IRollbackPlanner>());
        Assert.NotNull(provider.GetRequiredService<ICsvImportPlanner>());
        Assert.NotNull(provider.GetRequiredService<PowerShellHistory>());
        Assert.IsType<RecordingChangePlanner>(provider.GetRequiredService<IChangePlanner>());
        using var sheet = factory.CreatePropertySheet("CN=Jan Nowak,OU=IT,OU=Firma,DC=contoso,DC=test");
        using var editor = factory.CreateBatchEditor(new BatchEditorRequest(["CN=Jan Nowak,OU=IT,OU=Firma,DC=contoso,DC=test"]));
        using var reset = factory.CreateResetPassword(["CN=Jan Nowak,OU=IT,OU=Firma,DC=contoso,DC=test"]);
        using var newUser = factory.CreateNewObject(Core.Model.ObjectKind.User, "OU=IT,OU=Firma,DC=contoso,DC=test");
        using var find = factory.CreateFind(null);
        using var settings = factory.CreateSettings();
        using var recycleBin = factory.CreateRecycleBin();
        Assert.NotNull(provider.GetRequiredService<RecycleBinViewModel>());
        Assert.All<IWindowViewModel>([sheet, editor, reset, newUser, find, settings, recycleBin], vm => Assert.False(string.IsNullOrEmpty(vm.WindowTitle)));
    }
}
