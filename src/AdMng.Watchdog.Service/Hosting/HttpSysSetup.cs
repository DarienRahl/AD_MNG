using System.Reflection;
using System.Runtime.Versioning;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// The optional HTTP.sys server (spec 17.3, 19.2): kernel-mode Kerberos with the machine account (no SPN on the gMSA),
/// scheme <c>Kerberos</c> only when <c>RequireKerberos</c> (else Negotiate with channel-binding hardening), anonymous
/// requests allowed so <c>/healthz</c> works (every API endpoint still requires a role). URL ACL and certificate binding
/// are made with <c>netsh http</c> (install docs).
/// </summary>
/// <remarks>
/// The HTTP.sys server API is bound by reflection: the reference assembly of the shared framework exposes it only to
/// Windows builds, while the service is built and tested on Linux as well. The members used are stable public API
/// (<c>WebHostBuilderHttpSysExtensions.UseHttpSys</c>, <c>HttpSysOptions</c>).
/// </remarks>
[SupportedOSPlatform("windows")]
public static class HttpSysSetup
{
    /// <summary>AppContext switch that turns on extended protection (channel binding) for Negotiate on HTTP.sys.</summary>
    public const string CbtHardeningSwitch = "Microsoft.AspNetCore.Server.HttpSys.EnableCBTHardening";

    private const int NegotiateScheme = 8;
    private const int KerberosScheme = 16;

    public static void Configure(WebApplicationBuilder builder, HostingOptions hosting, IReadOnlyList<ListenerEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(hosting);
        ArgumentNullException.ThrowIfNull(endpoints);
        if (!hosting.RequireKerberos)
        {
            AppContext.SetSwitch(CbtHardeningSwitch, true);
        }

        var assembly = Assembly.Load("Microsoft.AspNetCore.Server.HttpSys");
        var optionsType = assembly.GetType("Microsoft.AspNetCore.Server.HttpSys.HttpSysOptions", throwOnError: true)!;
        var extensions = assembly.GetType("Microsoft.AspNetCore.Hosting.WebHostBuilderHttpSysExtensions", throwOnError: true)!;
        var useHttpSys = extensions.GetMethod("UseHttpSys", [typeof(IWebHostBuilder), typeof(Action<>).MakeGenericType(optionsType)])
            ?? throw new MissingMethodException("WebHostBuilderHttpSysExtensions", "UseHttpSys");
        var configure = typeof(HttpSysSetup).GetMethod(nameof(Typed), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(optionsType)
            .Invoke(null, [(Action<object>)(options => Apply(options, hosting, endpoints))]);
        useHttpSys.Invoke(null, [builder.WebHost, configure]);
    }

    private static Action<T> Typed<T>(Action<object> configure) => options => configure(options!);

    private static void Apply(object options, HostingOptions hosting, IReadOnlyList<ListenerEndpoint> endpoints)
    {
        var type = options.GetType();
        var authentication = type.GetProperty("Authentication")!.GetValue(options)!;
        var schemes = authentication.GetType().GetProperty("Schemes")!;
        schemes.SetValue(authentication, Enum.ToObject(schemes.PropertyType, hosting.RequireKerberos ? KerberosScheme : NegotiateScheme));
        authentication.GetType().GetProperty("AllowAnonymous")!.SetValue(authentication, true);
        type.GetProperty("MaxRequestBodySize")!.SetValue(options, (long?)ApiLimits.MaxRequestBodyBytes);
        var prefixes = type.GetProperty("UrlPrefixes")!.GetValue(options)!;
        var clear = prefixes.GetType().GetMethod("Clear")!;
        var add = prefixes.GetType().GetMethod("Add", [typeof(string)])!;
        clear.Invoke(prefixes, null);
        foreach (var endpoint in endpoints)
        {
            add.Invoke(prefixes, [endpoint.Url]);
        }
    }
}
