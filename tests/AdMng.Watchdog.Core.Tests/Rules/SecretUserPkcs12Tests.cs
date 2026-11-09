using AdMng.Core.Schema;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Rules;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Core.Tests.Rules;

/// <summary>
/// P1 decision 7 (docs/dev/p-certificate-services.md): <c>userPKCS12</c> – the PKCS #12 blob of a user's certificates and
/// private keys – is a secret, so the Watchdog can never watch it: a configuration naming it fails validation and the
/// effective watched sets drop it. This is the only Watchdog-visible change of the P1 contract.
/// </summary>
public sealed class SecretUserPkcs12Tests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    private static ConfigValidationResult Validate(params string[] additional)
    {
        var defaults = new DefaultConfigurationFactory().Create();
        var configuration = defaults with { Directory = defaults.Directory with { AdditionalWatchedAttributes = additional, IgnoredAttributes = additional } };
        return new ConfigurationValidator(new FakeTimeProvider(Now)).Validate(configuration, new ConfigValidationContext());
    }

    [Fact]
    public void UserPkcs12_IsASecret()
    {
        Assert.True(SecretAttributes.IsSecret("userPKCS12"));
        Assert.Contains("userPKCS12", SecretAttributes.WellKnown, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AConfigurationWatchingUserPkcs12_FailsWithInvalidAttribute()
    {
        var result = Validate("userPKCS12");

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == ConfigValidationCodes.InvalidAttribute && i.Path == "directory.additionalWatchedAttributes[0]");
        Assert.Contains(result.Issues, i => i.Code == ConfigValidationCodes.InvalidAttribute && i.Path == "directory.ignoredAttributes[0]");

        // The same configuration with an ordinary attribute has no attribute issue.
        Assert.DoesNotContain(Validate("employeeType").Issues, i => i.Code == ConfigValidationCodes.InvalidAttribute);
    }

    [Fact]
    public void WatchedAttributes_DropUserPkcs12()
    {
        var settings = new DirectorySettings { AdditionalWatchedAttributes = ["userPKCS12", "employeeType"] };

        foreach (var kind in new[] { WatchObjectKind.User, WatchObjectKind.Computer, WatchObjectKind.ServiceAccount })
        {
            var watched = WatchedAttributes.For(kind, settings);
            Assert.DoesNotContain("userPKCS12", watched);
            Assert.Contains("employeeType", watched);
        }

        Assert.False(WatchedAttributes.IsWatched("userPKCS12", settings));
        Assert.DoesNotContain("userPKCS12", WatchedAttributes.RequestSet(settings), StringComparer.OrdinalIgnoreCase);
    }
}
