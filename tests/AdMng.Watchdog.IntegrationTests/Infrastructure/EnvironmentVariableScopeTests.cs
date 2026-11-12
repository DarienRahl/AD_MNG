namespace AdMng.Watchdog.IntegrationTests.Infrastructure;

/// <summary>
/// The scope of a process environment variable when tests that run side by side set the same one: the lab tests that
/// host the service give it the bind password this way, and the one that ended first must not take the password away
/// from the one that still runs. These tests need no lab.
/// </summary>
public sealed class EnvironmentVariableScopeTests
{
    [Fact]
    public void ScopesThatOverlap_KeepTheVariableUntilTheLastOneEnds()
    {
        var name = NewName();
        Assert.Null(Environment.GetEnvironmentVariable(name));

        var first = new EnvironmentVariableScope(name, "Hasło-ż");
        var second = new EnvironmentVariableScope(name, "Hasło-ż");
        Assert.Equal("Hasło-ż", Environment.GetEnvironmentVariable(name));

        // The one that started first ends first: the other still has the variable.
        first.Dispose();
        Assert.Equal("Hasło-ż", Environment.GetEnvironmentVariable(name));

        // Ending a scope twice ends it once.
        first.Dispose();
        Assert.Equal("Hasło-ż", Environment.GetEnvironmentVariable(name));

        second.Dispose();
        Assert.Null(Environment.GetEnvironmentVariable(name));
    }

    [Fact]
    public void TheLastScopeToEnd_RestoresWhatWasThereBeforeTheFirst()
    {
        var name = NewName();
        Environment.SetEnvironmentVariable(name, "przedtem");
        try
        {
            var outer = new EnvironmentVariableScope(name, "w trakcie");
            var inner = new EnvironmentVariableScope(name, "w trakcie");
            Assert.Equal("w trakcie", Environment.GetEnvironmentVariable(name));

            // The one that started last ends last here: what it found when it started is not what is restored.
            outer.Dispose();
            Assert.Equal("w trakcie", Environment.GetEnvironmentVariable(name));
            inner.Dispose();
            Assert.Equal("przedtem", Environment.GetEnvironmentVariable(name));

            // A scope after that starts from the beginning.
            using (new EnvironmentVariableScope(name, "potem"))
            {
                Assert.Equal("potem", Environment.GetEnvironmentVariable(name));
            }

            Assert.Equal("przedtem", Environment.GetEnvironmentVariable(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void AScopeThatWantsAnotherValueWhileOneRuns_IsRefused_AndChangesNothing()
    {
        var name = NewName();
        var running = new EnvironmentVariableScope(name, "jedna");

        var refused = Assert.Throws<InvalidOperationException>(() => new EnvironmentVariableScope(name, "druga"));
        Assert.Contains(name, refused.Message, StringComparison.Ordinal);
        Assert.Equal("jedna", Environment.GetEnvironmentVariable(name));

        // The refusal did not count as a user: the one scope that runs ends the variable.
        running.Dispose();
        Assert.Null(Environment.GetEnvironmentVariable(name));
    }

    private static string NewName() => "ADMNG_TEST_SCOPE_" + Guid.NewGuid().ToString("N");
}
