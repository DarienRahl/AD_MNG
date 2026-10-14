using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AdMng.Watchdog.Service.Tests.Fakes;

/// <summary>An <see cref="IHostEnvironment"/> with a settable environment name.</summary>
public sealed class FakeHostEnvironment(string environmentName = "Production") : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;

    public string ApplicationName { get; set; } = "AdMng.Watchdog";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
