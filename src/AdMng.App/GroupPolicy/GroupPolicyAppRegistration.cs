using AdMng.App.Services;
using AdMng.App.Views;
using AdMng.Core.GroupPolicy;
using AdMng.Directory;
using AdMng.Presentation.Changes;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.GroupPolicy.Copy;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.GroupPolicy;

/// <summary>DI registration of the Group Policy (K2) part of the WPF app.</summary>
public static class GroupPolicyAppRegistration
{
    /// <summary>
    /// The SYSVOL writer of the session's DC (singleton, stateless: every connection logs on with the window's own
    /// credential, design note 3.2 - the batch executor gets it through its optional constructor parameter), the Group
    /// Policy windows of the connection window's dialog service (scoped) and the windows of the view models.
    /// </summary>
    public static IServiceCollection AddAdMngGroupPolicyApp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ISysvolFileWriter, WindowsSysvolFileWriter>();
        services.AddScoped<IGroupPolicyDialogs>(sp => sp.GetRequiredService<DialogService>());
        services.AddWindow<ChangePlanPreviewViewModel, ChangePlanPreviewWindow>();
        services.AddWindow<NewGpoViewModel, NewGpoWindow>();
        services.AddWindow<GpoPickerViewModel, GpoPickerWindow>();
        services.AddWindow<GpoBackupViewModel, GpoBackupWindow>();
        services.AddWindow<GpoComparisonViewModel, GpoComparisonWindow>();
        services.AddWindow<WmiFilterEditorViewModel, WmiFilterEditorWindow>();

        // K3: the installers and CAB archives of template packages are unpacked by Windows tools; the update window.
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IPolicyPackageExtractor, WindowsPolicyPackageExtractor>();
        }

        services.AddWindow<CentralStoreUpdateViewModel, CentralStoreUpdateWindow>();

        // K7: "Kopiuj ustawienia" / "Scal obiekty GPO".
        services.AddWindow<GpoCopySettingsViewModel, GpoCopySettingsWindow>();
        return services;
    }
}
