using System.Management.Automation;
using AdMng.Core.PowerShell;
using AdMng.PowerShell.Commands;
using AdMng.Testing;

namespace AdMng.PowerShell.Tests.Reading;

/// <summary>What a script produced in object mode: the objects, the error and warning text, how it ended.</summary>
internal sealed record ScriptResult(IReadOnlyList<PSObject> Objects, string Errors, string Warnings, PowerShellRunEndItem End)
{
    public PSObject Single()
    {
        Assert.True(Objects.Count == 1, $"{Objects.Count} objects instead of one; errors: {Errors}");
        return Objects[0];
    }

    public object? Property(string name) => Single().Properties[name]?.Value;

    /// <summary>The value of one property of every object, as text, in the order the objects arrived.</summary>
    public string[] Texts(string property) =>
        [.. Objects.Select(o => Convert.ToString(o.Properties[property]?.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)];

    /// <summary>Every value of one property of every object (a multi-valued attribute contributes each of its values).</summary>
    public string[] Flat(string property) =>
        [.. Objects.SelectMany(o => FlatValues(o.Properties[property]?.Value))];

    private static IEnumerable<string> FlatValues(object? value) => value switch
    {
        null => [],
        string text => [text],
        System.Collections.IEnumerable values => values.Cast<object?>().Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty),
        _ => [Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty],
    };

    /// <summary>The distinguished names of the objects, in the order they arrived.</summary>
    public string[] Dns() => Texts("DistinguishedName");
}

/// <summary>A window session over the sample company of the fake directory, and a way to run a script and read what it returned.</summary>
internal sealed class ReadHarness : IAsyncDisposable
{
    private ReadHarness(FakeDirectoryBuilder builder, SampleCompany company, FakeWindowContext context, PowerShellSession session)
    {
        Builder = builder;
        Company = company;
        Context = context;
        Session = session;
    }

    public FakeDirectoryBuilder Builder { get; }

    public SampleCompany Company { get; }

    public FakeDirectorySession Directory => Builder.Session;

    public FakeWindowContext Context { get; }

    public PowerShellSession Session { get; }

    public static async Task<ReadHarness> CreateAsync(
        Action<FakeDirectoryBuilder, SampleCompany>? seed = null,
        IAdmWindowServices? services = null,
        System.Globalization.CultureInfo? culture = null)
    {
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        seed?.Invoke(builder, company);
        return await CreateAsync(builder, company, services, culture);
    }

    /// <summary>A harness over a directory the test built itself (a schema of its own, extra objects); <paramref name="culture"/> is the window's language.</summary>
    public static async Task<ReadHarness> CreateAsync(FakeDirectoryBuilder builder, SampleCompany company, IAdmWindowServices? services = null, System.Globalization.CultureInfo? culture = null)
    {
        var context = new FakeWindowContext(builder.Session, services);
        var session = await TestSessions.CreateAsync(context, culture: culture);
        return new ReadHarness(builder, company, context, session);
    }

    /// <summary>Runs <paramref name="script"/> in object mode and collects everything up to the end item.</summary>
    public async Task<ScriptResult> RunAsync(string script)
    {
        var objects = new List<PSObject>();
        var errors = new System.Text.StringBuilder();
        var warnings = new System.Text.StringBuilder();
        PowerShellRunEndItem? end = null;
        await foreach (var item in Session.RunObjectsAsync(script, TestContext.Current.CancellationToken))
        {
            switch (item)
            {
                case PowerShellObjectItem { Value: PSObject value }:
                    objects.Add(value);
                    break;
                case PowerShellTextItem { Stream: PowerShellStream.Error } text:
                    errors.Append(text.Text);
                    break;
                case PowerShellTextItem { Stream: PowerShellStream.Warning } text:
                    warnings.Append(text.Text);
                    break;
                case PowerShellRunEndItem finished:
                    end = finished;
                    break;
            }
        }

        return new ScriptResult(objects, errors.ToString(), warnings.ToString(), end ?? throw new InvalidOperationException("The run did not end."));
    }

    /// <summary>Runs a script that ends in a string and returns it (errors of the script are returned as <c>ERR:</c> text).</summary>
    public async Task<string> TextAsync(string script)
    {
        var result = await RunAsync(script);
        return result.Errors.Length > 0 ? "ERR:" + result.Errors : string.Concat(result.Objects.Select(o => o.ToString())).Trim();
    }

    public ValueTask DisposeAsync() => Session.DisposeAsync();
}
