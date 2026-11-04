using AdMng.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.Presentation.Correspondence;

/// <summary>DI registration of the Korespondencja (U5) presentation services.</summary>
public static class CorrespondenceServiceCollectionExtensions
{
    /// <summary>
    /// The template library and the journal (singletons: one settings file, one journal for every window) and, per
    /// connection window (scoped), <see cref="CorrespondenceServices"/> and <see cref="CorrespondenceViewModelFactory"/>.
    /// The host provides the Outlook client, the signature source, the key protector, the platform, the browser preview,
    /// the dialogs and the launcher (the WPF app: <c>AddAdMngCorrespondenceApp</c>); without a key protector the journal
    /// cannot be opened, without the rest the window disables what needs them and the shell hides its commands.
    /// </summary>
    public static IServiceCollection AddAdMngCorrespondence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<CorrespondenceTemplateLibrary>();
        services.TryAddSingleton(sp => new CorrespondenceJournal(
            sp.GetRequiredService<IAppPaths>(),
            sp.GetService<ICorrespondenceKeyProtector>() ?? new UnavailableKeyProtector(),
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddScoped<CorrespondenceServices>();
        services.TryAddScoped<CorrespondenceViewModelFactory>();
        return services;
    }

    /// <summary>A host without a key protector: the journal key can be neither made nor read.</summary>
    private sealed class UnavailableKeyProtector : ICorrespondenceKeyProtector
    {
        public byte[] Protect(byte[] data) => throw new PlatformNotSupportedException("The correspondence journal needs a key protector.");

        public byte[] Unprotect(byte[] data) => throw new PlatformNotSupportedException("The correspondence journal needs a key protector.");
    }
}
