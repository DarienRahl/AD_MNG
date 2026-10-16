using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Formatting.Webhooks;

namespace AdMng.Watchdog.Core.Tests.Formatting.Webhooks;

public class TeamsUrlValidatorTests
{
    private const string Host = "default72f988bf86f141af91ab2d7cd011db.47.environment.api.powerplatform.com";
    private const string Query = "?api-version=1&sp=%2Ftriggers%2Fmanual%2Frun&sv=1.0&sig=TajnyPodpisSAS_ŻółćAbc123";
    private const string Standard = "https://" + Host + ":443/powerautomate/automations/direct/workflows/4f0b9a1c2d3e4f5a6b7c8d9e0f1a2b3c/triggers/manual/paths/invoke" + Query;

    [Theory]
    [InlineData(Standard)]
    [InlineData("https://" + Host + "/powerautomate/automations/direct/workflows/4f0b9a1c/triggers/manual/paths/invoke" + Query)]
    [InlineData("https://" + Host + ":443/powerautomate/automations/direct/cu/12/workflows/4f0b9a1c/triggers/manual/paths/invoke" + Query)]
    [InlineData("HTTPS://DEFAULT72F988BF86F141AF91AB2D7CD011DB.47.ENVIRONMENT.API.POWERPLATFORM.COM/powerautomate/automations/direct/workflows/x")]
    public void StandardWorkflowUrls_AreValid_ForBothAuthModes(string url)
    {
        foreach (var mode in new[] { TeamsAuthMode.Sas, TeamsAuthMode.EntraClientCredentials })
        {
            var result = TeamsUrlValidator.Validate(url, mode);

            Assert.True(result.IsValid, result.ToString());
            Assert.True(result.IsAccepted);
            Assert.True(result.IsPowerPlatformHost);
            Assert.Equal(TeamsUrlProblem.None, result.Problem);
            Assert.Null(result.Code);
            Assert.Null(result.Severity);
            Assert.Null(result.Hint);
        }
    }

    [Fact]
    public void UrlsLongerThan4KB_AreValid()
    {
        var url = Standard + "&state=" + new string('A', 5000);

        Assert.True(url.Length > 4096);
        Assert.True(TeamsUrlValidator.Validate(url, TeamsAuthMode.EntraClientCredentials).IsValid);
    }

    [Theory]
    [InlineData("https://contoso.webhook.office.com/webhookb2/1a2b3c4d@72f988bf/IncomingWebhook/5e6f/0a1b")]
    [InlineData("https://webhook.office.com/webhookb2/x")]
    [InlineData("http://contoso.webhook.office.com/webhookb2/x")]
    [InlineData("https://prod-12.westeurope.logic.azure.com:443/workflows/4f0b9a1c/triggers/manual/paths/invoke?api-version=2016-06-01&sig=abc")]
    [InlineData("https://outlook.office.com/webhook/1a2b3c4d@72f988bf/IncomingWebhook/5e6f/0a1b")]
    [InlineData("https://outlook.office365.com/webhook/1a2b3c4d@72f988bf/IncomingWebhook/5e6f/0a1b")]
    public void RetiredConnectorAndLogicAppsUrls_HaveTheirOwnCodeAndHint(string url)
    {
        var result = TeamsUrlValidator.Validate(url, TeamsAuthMode.Sas);

        Assert.Equal(TeamsUrlProblem.Retired, result.Problem);
        Assert.Equal("retiredTeamsUrl", result.Code);
        Assert.Equal(IssueSeverity.Error, result.Severity);
        Assert.Equal(DeliveryHints.RetiredTeamsUrl, result.Hint);
        Assert.False(result.IsAccepted);
    }

    [Theory]
    [InlineData("https://contoso.webhook.office.com./webhookb2/1a2b3c4d@72f988bf/IncomingWebhook/5e6f/0a1b")]
    [InlineData("https://CONTOSO.WEBHOOK.OFFICE.COM./webhookb2/x")]
    [InlineData("https://prod-12.westeurope.logic.azure.com.:443/workflows/4f0b9a1c/triggers/manual/paths/invoke?sig=abc")]
    [InlineData("https://outlook.office.com./webhook/1a2b3c4d@72f988bf/IncomingWebhook/5e6f/0a1b")]
    [InlineData("https://outlook.office365.com。/webhook/1a2b3c4d@72f988bf/IncomingWebhook/5e6f/0a1b")]
    public void RetiredUrls_WithATrailingDot_AreStillRetired(string url)
    {
        // "host." is the same DNS name as "host": a trailing dot must not turn a retired URL into an accepted warning.
        foreach (var mode in new[] { TeamsAuthMode.Sas, TeamsAuthMode.EntraClientCredentials })
        {
            var result = TeamsUrlValidator.Validate(url, mode);

            Assert.Equal(TeamsUrlProblem.Retired, result.Problem);
            Assert.Equal("retiredTeamsUrl", result.Code);
            Assert.False(result.IsAccepted);
        }
    }

    [Fact]
    public void Http_IsInsecure_EvenForAPowerPlatformHost()
    {
        var result = TeamsUrlValidator.Validate("http://" + Host + "/powerautomate/automations/direct/workflows/x", TeamsAuthMode.Sas, allowInsecureLoopback: true);

        Assert.Equal(TeamsUrlProblem.Insecure, result.Problem);
        Assert.Equal("insecureUrl", result.Code);
        Assert.Equal(IssueSeverity.Error, result.Severity);
        Assert.Equal("insecureUrl", TeamsUrlValidator.Validate("ftp://" + Host + "/powerautomate", TeamsAuthMode.Sas).Code);
    }

    [Theory]
    [InlineData("http://localhost:5080/teams")]
    [InlineData("http://127.0.0.1:5080/teams")]
    [InlineData("http://[::1]:5080/teams")]
    public void LoopbackHttp_OnlyWithTheDevelopmentFlag_AndNeverAsAPowerPlatformHost(string url)
    {
        Assert.Equal(TeamsUrlProblem.Insecure, TeamsUrlValidator.Validate(url, TeamsAuthMode.Sas).Problem);

        var sas = TeamsUrlValidator.Validate(url, TeamsAuthMode.Sas, allowInsecureLoopback: true);
        Assert.Equal(TeamsUrlProblem.NonStandard, sas.Problem);
        Assert.Equal(IssueSeverity.Warning, sas.Severity);
        Assert.True(sas.IsAccepted);

        var oauth = TeamsUrlValidator.Validate(url, TeamsAuthMode.EntraClientCredentials, allowInsecureLoopback: true);
        Assert.Equal(IssueSeverity.Error, oauth.Severity);
        Assert.False(oauth.IsPowerPlatformHost);
    }

    [Theory]
    [InlineData("https://hooks.contoso.test/teams/łódź")]
    [InlineData("https://x.environment.api.powerplatform.com.evil.test/powerautomate/automations/direct/workflows/x")]
    [InlineData("https://evil.test/powerautomate/automations/direct/workflows/x?h=.environment.api.powerplatform.com")]
    [InlineData("https://xenvironment.api.powerplatform.com/powerautomate/automations/direct/workflows/x")]
    [InlineData("https://environment.api.powerplatform.com/powerautomate/automations/direct/workflows/x")]
    [InlineData("https://" + Host + ":8443/powerautomate/automations/direct/workflows/x")]
    public void CustomOrLookalikeHosts_AreAWarningForSas_AndAnErrorForOAuth(string url)
    {
        var sas = TeamsUrlValidator.Validate(url, TeamsAuthMode.Sas);
        var oauth = TeamsUrlValidator.Validate(url, TeamsAuthMode.EntraClientCredentials);

        Assert.Equal(TeamsUrlProblem.NonStandard, sas.Problem);
        Assert.Equal("teamsUrlFormat", sas.Code);
        Assert.Equal(IssueSeverity.Warning, sas.Severity);
        Assert.True(sas.IsAccepted);
        Assert.False(sas.IsValid);
        Assert.Equal("teamsUrlFormat", oauth.Code);
        Assert.Equal(IssueSeverity.Error, oauth.Severity);
        Assert.False(oauth.IsAccepted);
        Assert.False(oauth.IsPowerPlatformHost);
    }

    [Theory]
    [InlineData("/powerautomate/automations/workflows/x")]
    [InlineData("/powerautomate/automations/direct/x")]
    [InlineData("/workflows/x/powerautomate/automations/direct/")]
    [InlineData("/")]
    public void PowerPlatformHostWithAnotherPath_IsNonStandard_ButStillAPowerPlatformHost(string path)
    {
        var result = TeamsUrlValidator.Validate("https://" + Host + path, TeamsAuthMode.EntraClientCredentials);

        Assert.Equal(TeamsUrlProblem.NonStandard, result.Problem);
        Assert.Equal(IssueSeverity.Error, result.Severity);
        Assert.True(result.IsPowerPlatformHost);
        Assert.Equal(IssueSeverity.Warning, TeamsUrlValidator.Validate("https://" + Host + path, TeamsAuthMode.Sas).Severity);
    }

    [Theory]
    [InlineData(null, TeamsUrlProblem.Missing, "required")]
    [InlineData("", TeamsUrlProblem.Missing, "required")]
    [InlineData("   ", TeamsUrlProblem.Missing, "required")]
    [InlineData("nie jest adresem", TeamsUrlProblem.Malformed, "teamsUrlFormat")]
    [InlineData("powerautomate/automations/direct/workflows/x", TeamsUrlProblem.Malformed, "teamsUrlFormat")]
    [InlineData("https://user:hasło@" + Host + "/powerautomate/automations/direct/workflows/x", TeamsUrlProblem.Malformed, "teamsUrlFormat")]
    [InlineData("https://" + Host + ".evil.test@evil.test/powerautomate/automations/direct/workflows/x", TeamsUrlProblem.Malformed, "teamsUrlFormat")]
    // .NET's Uri (which HttpClient uses) reads "evil.test\" as user info and connects to the Power Platform host –
    // unlike browsers; either way a URL with user info is rejected.
    [InlineData("https://evil.test\\@" + Host + "/powerautomate/automations/direct/workflows/x", TeamsUrlProblem.Malformed, "teamsUrlFormat")]
    [InlineData("mailto:zespół.it@contoso.test", TeamsUrlProblem.Insecure, "insecureUrl")]
    public void MissingMalformedAndNonHttpUrls_AreErrors(string? url, TeamsUrlProblem problem, string code)
    {
        foreach (var mode in new[] { TeamsAuthMode.Sas, TeamsAuthMode.EntraClientCredentials })
        {
            var result = TeamsUrlValidator.Validate(url, mode);

            Assert.Equal(problem, result.Problem);
            Assert.Equal(code, result.Code);
            Assert.Equal(IssueSeverity.Error, result.Severity);
            Assert.False(result.IsAccepted);
        }
    }

    [Fact]
    public void Results_NeverContainTheUrl()
    {
        string[] urls =
        [
            Standard,
            "https://hooks.contoso.test/teams" + Query,
            "https://contoso.webhook.office.com/webhookb2/x" + Query,
            "http://" + Host + "/x" + Query,
            "https://user:TajnyPodpisSAS@" + Host + "/x",
        ];

        foreach (var url in urls)
        {
            foreach (var mode in new[] { TeamsAuthMode.Sas, TeamsAuthMode.EntraClientCredentials })
            {
                var result = TeamsUrlValidator.Validate(url, mode);
                var text = $"{result} {result.Message} {result.Code} {result.Hint}";
                Assert.DoesNotContain("TajnyPodpis", text, StringComparison.Ordinal);
                Assert.DoesNotContain("default72f988bf", text, StringComparison.Ordinal);
                Assert.DoesNotContain("contoso", text, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData(Host, true)]
    [InlineData("DEFAULT1.12.Environment.Api.PowerPlatform.com", true)]
    [InlineData("environment.api.powerplatform.com", false)]
    [InlineData(".environment.api.powerplatform.com", false)]
    [InlineData("x..environment.api.powerplatform.com", false)]
    [InlineData("x.environment.api.powerplatform.com.evil.test", false)]
    [InlineData("api.powerplatform.com", false)]
    [InlineData("x.environment.api.powerplatform.com.", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    // Not a host name: user info, a path, a port, a fragment, white space or non-ASCII (pass Uri.IdnHost) never qualify.
    [InlineData("evil.test@x.environment.api.powerplatform.com", false)]
    [InlineData("evil.test:443@x.environment.api.powerplatform.com", false)]
    [InlineData("evil.test/x.environment.api.powerplatform.com", false)]
    [InlineData("evil.test#x.environment.api.powerplatform.com", false)]
    [InlineData("evil.test?x.environment.api.powerplatform.com", false)]
    [InlineData("evil.test x.environment.api.powerplatform.com", false)]
    [InlineData("evil.test\\x.environment.api.powerplatform.com", false)]
    [InlineData("zażółć.environment.api.powerplatform.com", false)]
    public void IsPowerPlatformHost(string? host, bool expected)
    {
        Assert.Equal(expected, TeamsUrlValidator.IsPowerPlatformHost(host));
    }
}
