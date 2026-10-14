using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace AdMng.Watchdog.Service.Tests.Infrastructure;

/// <summary>TestServer has no remote address; this filter sets <c>Connection.RemoteIpAddress</c> from the test-only header
/// <c>X-Test-Remote-Ip</c> (default loopback) before the service pipeline runs.</summary>
public sealed class RemoteAddressStartupFilter : IStartupFilter
{
    public const string Header = "X-Test-Remote-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.TryParse(context.Request.Headers[Header].ToString(), out var address)
                ? address
                : IPAddress.Loopback;
            return nextMiddleware(context);
        });
        next(app);
    };
}
