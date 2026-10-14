using Microsoft.AspNetCore.Authentication;

namespace AdMng.Watchdog.Service.Tests.Infrastructure;

/// <summary>Options of <see cref="TestAuthenticationHandler"/>: an identity for requests without test headers (the
/// client tests, whose requests carry Negotiate credentials only).</summary>
public sealed class TestAuthenticationOptions : AuthenticationSchemeOptions
{
    public string? DefaultAccount { get; set; }

    public IReadOnlyList<string> DefaultSids { get; set; } = [];
}
