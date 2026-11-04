using AdMng.Presentation.Diagnostics;
using AdMng.Testing;
using Microsoft.Extensions.Logging;

namespace AdMng.Presentation.Tests.Diagnostics;

/// <summary>
/// The in-memory diagnostic buffer of the problem report (design note s-pilot-readiness.md 3): it keeps level, time,
/// category, event id, the message template and exception types – never an argument value, a formatted message, a scope
/// or an exception message, so no directory value or password can enter it (CLAUDE.md rules 1 and 2).
/// </summary>
public sealed class DiagnosticLogBufferTests
{
    internal const string Dn = "CN=Łukasz Wiśniewski,OU=Księgowość,OU=Łódź,DC=contoso,DC=test";
    internal const string Account = @"CONTOSO\łukasz.wiśniewski";
    internal const string Password = "Zażółć-gęślą-jaźń!2026";

    /// <summary>Fragments of the values above that must never show up anywhere.</summary>
    internal static readonly string[] Values = [Dn, Account, Password, "Wiśniewski", "Księgowość", "contoso", "Zażółć"];

    private static readonly Action<ILogger, string, string, Exception?> ResetFailed =
        LoggerMessage.Define<string, string>(LogLevel.Error, new EventId(7, "ResetFailed"), "Reset of {Dn} for {Account} failed");

    [Fact]
    public void ArgumentValues_NeverEnterTheBuffer_OnlyTheTemplate()
    {
        var (buffer, logger, time) = Create("AdMng.Directory.LdapDirectorySession");

        logger.LogWarning(new EventId(12, "BindFailed"), "Bind of {Account} to {Dn} failed with {Password}", Account, Dn, Password);

        var entry = Assert.Single(buffer.Snapshot());
        Assert.Equal("Bind of {Account} to {Dn} failed with {Password}", entry.Template);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("AdMng.Directory.LdapDirectorySession", entry.Category);
        Assert.Equal(12, entry.EventId);
        Assert.Equal("BindFailed", entry.EventName);
        Assert.Equal(time.GetUtcNow(), entry.Time);
        Assert.Empty(entry.Exceptions);
        AssertValueFree(entry);
    }

    [Fact]
    public void Exceptions_AreKeptAsTypeAndHResult_NeverTheirMessages()
    {
        var (buffer, logger, _) = Create();
        var inner = new IOException($"Hasło {Password} odrzucone dla {Account}");
        var exception = new InvalidOperationException($"Nie można zmienić {Dn}", inner);

        logger.LogError(exception, "The change of {Dn} failed", Dn);

        var entry = Assert.Single(buffer.Snapshot());
        Assert.Equal("The change of {Dn} failed", entry.Template);
        Assert.Equal(
            [new DiagnosticException("System.InvalidOperationException", exception.HResult), new DiagnosticException("System.IO.IOException", inner.HResult)],
            entry.Exceptions);
        AssertValueFree(entry);
    }

    [Fact]
    public void ExceptionChains_AreCutAtTheMaximumDepth()
    {
        var (buffer, logger, _) = Create();
        Exception exception = new FormatException(Dn);
        for (var i = 0; i < 6; i++)
        {
            exception = new InvalidOperationException(Password, exception);
        }

        logger.LogError(exception, "Nested failure");

        var entry = Assert.Single(buffer.Snapshot());
        Assert.Equal(DiagnosticLogBuffer.MaxExceptionDepth, entry.Exceptions.Count);
        AssertValueFree(entry);
    }

    [Fact]
    public void LoggerMessageDefinitions_KeepTheirTemplateAndEventId()
    {
        var (buffer, logger, _) = Create();

        ResetFailed(logger, Dn, Account, new UnauthorizedAccessException(Password));

        var entry = Assert.Single(buffer.Snapshot());
        Assert.Equal("Reset of {Dn} for {Account} failed", entry.Template);
        Assert.Equal((7, "ResetFailed"), (entry.EventId, entry.EventName));
        Assert.Equal("System.UnauthorizedAccessException", Assert.Single(entry.Exceptions).Type);
        AssertValueFree(entry);
    }

    [Fact]
    public void UnstructuredState_IsNeitherFormattedNorKept()
    {
        var (buffer, logger, _) = Create();
        var formatted = false;

        logger.Log(LogLevel.Information, new EventId(3), new LeakyState(Dn), null, (state, _) =>
        {
            formatted = true;
            return state.ToString();
        });
        logger.Log(LogLevel.Information, default, Password, null, (state, _) =>
        {
            formatted = true;
            return state;
        });

        Assert.False(formatted);
        Assert.All(buffer.Snapshot(), entry =>
        {
            Assert.Null(entry.Template);
            AssertValueFree(entry);
        });
        Assert.Equal(2, buffer.Snapshot().Count);
    }

    [Fact]
    public void Scopes_AreIgnored()
    {
        var (buffer, logger, _) = Create();

        using (logger.BeginScope("Object {Dn} of {Account}", Dn, Account))
        {
            logger.LogInformation("Loaded the object");
        }

        var entry = Assert.Single(buffer.Snapshot());
        Assert.Equal("Loaded the object", entry.Template);
        AssertValueFree(entry);
    }

    [Fact]
    public void KeepsTheLastEntries_OldestFirst()
    {
        var time = new ManualTimeProvider();
        var buffer = new DiagnosticLogBuffer(time, capacity: 3);
        var logger = new DiagnosticLoggerProvider(buffer).CreateLogger("AdMng.Tests");

        foreach (var template in new[] { "Entry 1", "Entry 2", "Entry 3", "Entry 4", "Entry 5" })
        {
            time.Advance(TimeSpan.FromSeconds(1));
            logger.LogInformation(template);
        }

        Assert.Equal(["Entry 3", "Entry 4", "Entry 5"], buffer.Snapshot().Select(e => e.Template));
        Assert.Equal(5, buffer.Total);
        Assert.Equal(3, buffer.Capacity);
        Assert.True(buffer.Snapshot()[0].Time < buffer.Snapshot()[2].Time);
        Assert.Equal(DiagnosticLogBuffer.DefaultCapacity, new DiagnosticLogBuffer(time).Capacity);
    }

    [Fact]
    public void LevelNone_IsNotRecorded()
    {
        var (buffer, logger, _) = Create();

        Assert.False(logger.IsEnabled(LogLevel.None));
        Assert.True(logger.IsEnabled(LogLevel.Trace));
        logger.Log(LogLevel.None, "Nothing");

        Assert.Empty(buffer.Snapshot());
    }

    /// <summary>No field of the entry, nor its text, carries one of <see cref="Values"/>.</summary>
    internal static void AssertValueFree(DiagnosticLogEntry entry)
    {
        var texts = new List<string?> { entry.ToString(), entry.Category, entry.EventName, entry.Template };
        texts.AddRange(entry.Exceptions.Select(e => e.ToString()));
        foreach (var text in texts.OfType<string>())
        {
            foreach (var value in Values)
            {
                Assert.DoesNotContain(value, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static (DiagnosticLogBuffer Buffer, ILogger Logger, ManualTimeProvider Time) Create(string category = "AdMng.Tests.Diagnostics")
    {
        var time = new ManualTimeProvider();
        var buffer = new DiagnosticLogBuffer(time);
        return (buffer, new DiagnosticLoggerProvider(buffer).CreateLogger(category), time);
    }

    /// <summary>A state that is not a list of pairs and would leak its value through <see cref="ToString"/>.</summary>
    private sealed class LeakyState(string value)
    {
        public override string ToString() => value;
    }
}
