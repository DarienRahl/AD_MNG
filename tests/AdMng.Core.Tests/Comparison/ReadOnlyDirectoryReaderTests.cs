using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Testing;

namespace AdMng.Core.Tests.Comparison;

public class ReadOnlyDirectoryReaderTests
{
    [Fact]
    public async Task ForwardsReads_ButIsNoSession()
    {
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        var session = builder.Build();
        var reader = ReadOnlyDirectoryReader.Of(session);
        var ct = TestContext.Current.CancellationToken;

        var lukasz = await reader.GetAsync(company.LukaszWisniewski, [Attr.Title], ct);
        var found = new List<string>();
        await foreach (var obj in reader.SearchAsync(new DirectorySearch { BaseDn = company.ItOu, Filter = "(objectClass=user)", Attributes = [Attr.Name] }, ct))
        {
            found.Add(obj.Name);
        }

        var members = new List<string>();
        await foreach (var member in reader.GetAllValuesAsync(company.GroupIt, Attr.Member, ct))
        {
            members.Add(member);
        }

        Assert.Equal("Programista", lukasz?.GetString(Attr.Title));
        Assert.Contains("Łukasz Wiśniewski", found);
        Assert.Equal(2, members.Count);
        Assert.Same(session.Schema, reader.Schema);
        Assert.Equal(session.Info, reader.Info);
        Assert.False(typeof(IDirectorySession).IsAssignableFrom(reader.GetType()));
        Assert.False(typeof(IAsyncDisposable).IsAssignableFrom(reader.GetType()));
        Assert.Empty(session.Writes);
    }

    [Fact]
    public async Task Detach_DropsTheSession_AndRefusesLaterReads()
    {
        var session = new FakeDirectoryBuilder().Build();
        var reader = ReadOnlyDirectoryReader.Of(session);

        reader.Detach();
        reader.Detach();

        Assert.True(reader.IsDetached);
        Assert.Equal("contoso.test", reader.Info.DomainDnsName);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.GetAsync(session.Info.DefaultNamingContext, [Attr.Name], TestContext.Current.CancellationToken));
        Assert.Throws<ObjectDisposedException>(() => reader.SearchAsync(new DirectorySearch { BaseDn = session.Info.DefaultNamingContext }, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("contoso", reader.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
