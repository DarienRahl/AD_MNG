using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Services;
using AdMng.Presentation.Watchdog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>DI registration of the Group Policy (K2) presentation services.</summary>
public static class GroupPolicyServiceCollectionExtensions
{
    /// <summary>
    /// The reader and the planner (singletons: pure, they take the session per call), the window's services, operations
    /// and document factory (scoped: the session, coordinator, dialogs and shell of one connection window). The SYSVOL
    /// writer comes from the host (the WPF app registers <c>WindowsSysvolFileWriter</c>); without it every SYSVOL write is
    /// disabled as not supported. The Group Policy windows come from the window's dialog service when it implements
    /// <see cref="IGroupPolicyDialogs"/> (or a host registration made before this call).
    /// </summary>
    public static IServiceCollection AddAdMngGroupPolicy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<GroupPolicyReader>();
        services.TryAddSingleton(sp => new GroupPolicyPlanner(sp.GetRequiredService<GroupPolicyReader>(), sp.GetRequiredService<TimeProvider>()));
        services.TryAddScoped<IGroupPolicyDialogs>(sp => sp.GetRequiredService<IDialogService>() as IGroupPolicyDialogs ?? new UnavailableGroupPolicyDialogs());
        services.TryAddScoped(sp => new GroupPolicyServices(
            sp.GetRequiredService<DialogServices>(),
            sp.GetRequiredService<BatchEngines>(),
            sp.GetRequiredService<GroupPolicyPlanner>(),
            sp.GetRequiredService<IGroupPolicyDialogs>(),
            sp.GetService<ISysvolFileWriter>(),
            sp.GetService<WatchdogObjectHistoryLauncher>()));
        services.TryAddScoped<GroupPolicyActions>();
        services.TryAddScoped<GroupPolicyViewModelFactory>();
        return services;
    }

    /// <summary>A host without Group Policy windows: nothing is shown, every window answers "cancelled".</summary>
    private sealed class UnavailableGroupPolicyDialogs : IGroupPolicyDialogs
    {
        public Task<bool> ShowPlanPreviewAsync(ChangePlanPreviewViewModel preview) => Task.FromResult(false);

        public Task<bool> ShowNewGpoAsync(NewGpoViewModel dialog) => Task.FromResult(false);

        public Task<bool> ShowGpoPickerAsync(GpoPickerViewModel picker) => Task.FromResult(false);

        public Task<bool> ShowBackupAsync(GpoBackupViewModel dialog) => Task.FromResult(false);

        public Task ShowComparisonAsync(GpoComparisonViewModel comparison)
        {
            ArgumentNullException.ThrowIfNull(comparison);
            comparison.Dispose();
            return Task.CompletedTask;
        }

        public Task<bool> ShowWmiFilterEditorAsync(WmiFilterEditorViewModel editor) => Task.FromResult(false);

        public Task<string?> PickFolderAsync(string title, string? initialFolder) => Task.FromResult<string?>(null);
    }
}
