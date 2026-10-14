using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AdMng.Watchdog.Contracts;

namespace AdMng.Watchdog.Service.Tests.Infrastructure;

/// <summary>Contract (de)serialization for API tests – always through <see cref="WatchdogJson.Options"/>.</summary>
public static class ApiJson
{
    public static StringContent Content<T>(T value)
    {
        var content = new StringContent(JsonSerializer.Serialize(value, WatchdogJson.Options), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonSerializer.Deserialize<T>(text, WatchdogJson.Options)
               ?? throw new InvalidOperationException("Empty JSON response.");
    }

    /// <summary>The problem details of an error response.</summary>
    public static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.Clone();
    }

    /// <summary>The issue codes of a validation problem.</summary>
    public static async Task<IReadOnlyList<string>> IssueCodesAsync(HttpResponseMessage response)
    {
        var problem = await ProblemAsync(response);
        return problem.TryGetProperty("issues", out var issues)
            ? [.. issues.EnumerateArray().Select(i => i.GetProperty("code").GetString()!)]
            : [];
    }
}
