using AdMng.Core.Directory;
using AdMng.Testing;
using AdMng.Watchdog.Core.Rules;
using AdMng.Watchdog.Core.Tests.Rules.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdMng.Watchdog.Core.Tests.Rules;

public class DirectoryConfigTokenSourceTests
{
    [Fact]
    public async Task GetAsync_ReadsTheDomainHeadOncePerSession()
    {
        var builder = new FakeDirectoryBuilder("łódź.contoso.test");
        var reads = 0;
        builder.Session.BeforeOperation = _ => Interlocked.Increment(ref reads);
        var provider = new FakeSessionProvider(builder.Session);
        using var source = new DirectoryConfigTokenSource(provider, NullLogger<DirectoryConfigTokenSource>.Instance);

        Assert.Same(ConfigTokens.Unknown, source.Current);
        var tokens = await source.GetAsync(TestContext.Current.CancellationToken);
        var again = await source.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(builder.DomainDn, tokens.DomainDn);
        Assert.Equal(builder.DomainSid, tokens.DomainSid);
        Assert.Equal(builder.DomainSid, tokens.RootDomainSid);
        Assert.Same(tokens, again);
        Assert.Same(tokens, source.Current);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task GetAsync_ReadsAgainForANewSession()
    {
        var builder = new FakeDirectoryBuilder();
        var provider = new FakeSessionProvider(builder.Session);
        using var source = new DirectoryConfigTokenSource(provider, NullLogger<DirectoryConfigTokenSource>.Instance);
        await source.GetAsync(TestContext.Current.CancellationToken);

        var failover = new FakeDirectoryBuilder();
        failover.Session.Info = failover.Session.Info with { Server = "dc02.contoso.test" };
        var reads = 0;
        failover.Session.BeforeOperation = _ => Interlocked.Increment(ref reads);
        provider.Session = failover.Session;

        var tokens = await source.GetAsync(TestContext.Current.CancellationToken);
        await source.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(failover.DomainSid, tokens.DomainSid);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task GetAsync_KeepsTheLastValues_WhenTheDirectoryIsUnavailable()
    {
        var builder = new FakeDirectoryBuilder();
        var provider = new FakeSessionProvider(builder.Session);
        using var source = new DirectoryConfigTokenSource(provider, NullLogger<DirectoryConfigTokenSource>.Instance);
        var known = await source.GetAsync(TestContext.Current.CancellationToken);

        provider.Failure = new DirectoryException(DirectoryErrorKind.ServerUnavailable, "DC niedostępny");

        Assert.Same(known, await source.GetAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAsync_WithoutAReadableDomainHead_KeepsTheDomainDnOnly()
    {
        var session = new FakeDirectorySession();
        var provider = new FakeSessionProvider(session);
        using var source = new DirectoryConfigTokenSource(provider, NullLogger<DirectoryConfigTokenSource>.Instance);

        var tokens = await source.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(session.Info.DefaultNamingContext, tokens.DomainDn);
        Assert.Null(tokens.DomainSid);
    }
}
