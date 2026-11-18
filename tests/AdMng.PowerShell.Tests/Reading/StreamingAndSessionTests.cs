using System.Management.Automation;
using System.Runtime.CompilerServices;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Core.PowerShell;
using AdMng.Core.Schema;
using AdMng.PowerShell.Commands;
using AdMng.Testing;

namespace AdMng.PowerShell.Tests.Reading;

/// <summary>
/// A directory that makes its objects only when they are asked for and counts them: what a 100,000-object search looks like to
/// a command that buffers (it produces them all) and to one that streams (it produces what its consumer takes).
/// </summary>
internal sealed class LazyDirectoryReader(IDirectoryReader template, int count) : IDirectoryReader
{
    private int _produced;
    private int _searches;
    private readonly Lock _gate = new();
    private CancellationToken _lastToken;

    public int Produced => Volatile.Read(ref _produced);

    public int Searches => Volatile.Read(ref _searches);

    /// <summary>The token of the last search: the pipeline's stop token reaches the read when it is cancelled with the run.</summary>
    public CancellationToken LastToken
    {
        get
        {
            lock (_gate)
            {
                return _lastToken;
            }
        }
    }

    public SessionInfo Info => template.Info;

    public ISchemaCatalog Schema => template.Schema;

    public async IAsyncEnumerable<DirectoryObject> SearchAsync(DirectorySearch search, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _searches);
        lock (_gate)
        {
            _lastToken = cancellationToken;
        }

        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _produced);
            yield return new DirectoryObject(
                $"CN=Lazy {i},OU=Lazy,{template.Info.DefaultNamingContext}",
                [
                    new AttributeValues(Attr.ObjectClass, ["top", "person", "organizationalPerson", "user"]),
                    new AttributeValues(Attr.SamAccountName, [$"lazy{i}"]),
                    new AttributeValues(Attr.UserAccountControl, ["512"]),
                ]);
            if (i % 256 == 0)
            {
                await Task.Yield();
            }
        }
    }

    public Task<DirectoryObject?> GetAsync(string distinguishedName, IReadOnlyList<string> attributes, CancellationToken cancellationToken) =>
        template.GetAsync(distinguishedName, attributes, cancellationToken);

    public IAsyncEnumerable<string> GetAllValuesAsync(string distinguishedName, string attribute, CancellationToken cancellationToken) =>
        template.GetAllValuesAsync(distinguishedName, attribute, cancellationToken);
}

/// <summary>Streaming and stop (CLAUDE.md rule 6), and the reads going to the window's session only (rule 4) - R1 part 2.</summary>
public sealed class StreamingAndSessionTests
{
    /// <summary>The bounded output channel of the session plus what one read page holds: nothing like the whole result set.</summary>
    private const int FarFewerThanAll = 4096 + 512;

    private static async Task<(PowerShellSession Session, LazyDirectoryReader Reader)> CreateAsync(int count)
    {
        var template = new FakeDirectorySession();
        var reader = new LazyDirectoryReader(template, count);
        var session = await TestSessions.CreateAsync(new AdmWindowContextOver(reader));
        return (session, reader);
    }

    [Fact]
    public async Task A_consumer_that_takes_fifty_of_a_hundred_thousand_results_stops_the_read_after_fifty()
    {
        var (session, reader) = await CreateAsync(100_000);
        await using var _ = session;
        var objects = new List<PSObject>();

        await foreach (var item in session.RunObjectsAsync("Get-AdmUser | Select-Object -First 50", TestContext.Current.CancellationToken))
        {
            if (item is PowerShellObjectItem { Value: PSObject value })
            {
                objects.Add(value);
            }
        }

        Assert.Equal(50, objects.Count);
        Assert.Equal(1, reader.Searches);
        Assert.True(reader.Produced <= 60, $"the read produced {reader.Produced} objects for 50 results");
    }

    [Fact]
    public async Task The_results_are_not_buffered_the_read_runs_no_further_ahead_than_the_window_takes()
    {
        var (session, reader) = await CreateAsync(100_000);
        await using var _ = session;
        var taken = 0;

        await foreach (var item in session.RunObjectsAsync("Get-AdmUser", TestContext.Current.CancellationToken))
        {
            if (item is PowerShellObjectItem && ++taken == 100)
            {
                await session.StopAsync();
            }
        }

        Assert.True(taken >= 100);
        Assert.True(reader.Produced < FarFewerThanAll + 100, $"{reader.Produced} of 100000 objects were produced for {taken} taken");
    }

    [Fact]
    public async Task Stop_cancels_the_read_at_the_directory_with_the_token_of_the_pipeline()
    {
        var (session, reader) = await CreateAsync(100_000);
        await using var _ = session;
        PowerShellRunEndItem? end = null;
        var seen = 0;

        await foreach (var item in session.RunObjectsAsync("Get-AdmUser", TestContext.Current.CancellationToken))
        {
            if (item is PowerShellObjectItem && ++seen == 10)
            {
                await session.StopAsync();
            }

            if (item is PowerShellRunEndItem finished)
            {
                end = finished;
            }
        }

        Assert.NotNull(end);
        Assert.Equal(PowerShellRunState.Stopped, end.State);
        Assert.True(reader.LastToken.IsCancellationRequested, "the token that reached the directory was not cancelled by Stop");
        Assert.True(reader.Produced < 100_000);
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task The_formatted_window_run_streams_too_and_a_whole_small_result_set_arrives_complete()
    {
        var (session, reader) = await CreateAsync(2500);
        await using var _ = session;

        var items = await TestSessions.RunAsync(session, "Get-AdmUser | Measure-Object | ForEach-Object { $_.Count }");

        Assert.Equal("2500\n", TestSessions.Text(items));
        Assert.Equal(2500, reader.Produced);
    }

    [Fact]
    public async Task The_paging_and_size_parameters_reach_the_search()
    {
        await using var harness = await ReadHarness.CreateAsync();
        harness.Directory.ClearOperations();

        await harness.RunAsync("Get-AdmUser -ResultPageSize 25 -ResultSetSize 3");
        await harness.RunAsync("Get-AdmGroup");

        var searches = harness.Directory.Operations.Where(o => o.Kind == FakeOperationKind.Search).Select(o => o.Search!).ToList();
        Assert.Equal(25, searches[0].PageSize);
        Assert.Equal(3, searches[0].SizeLimit);
        Assert.Equal(500, searches[1].PageSize);
        Assert.Equal(0, searches[1].SizeLimit);
    }

    [Fact]
    public async Task Every_command_reads_through_the_windows_session_only_and_never_touches_another_one_or_writes()
    {
        await using var harness = await ReadHarness.CreateAsync();
        var other = new FakeDirectoryBuilder("other.test");
        other.AddSampleCompany();
        harness.Directory.ClearOperations();

        var scripts = new[]
        {
            "Get-AdmObject -Identity jan.kowalski",
            "Get-AdmUser -Department IT",
            "Get-AdmGroup -Name GG_*",
            "Get-AdmComputer",
            "Get-AdmGroupMember -Identity DL_Wszyscy -Recursive",
            "Get-AdmGroupMember -Identity GG_IT",
            "Search-AdmObject -Name 'Jan Kowalski'",
            "Get-AdmUser -SearchBase 'OU=Nope,DC=contoso,DC=test'",
        };
        foreach (var script in scripts)
        {
            await harness.RunAsync(script);
        }

        Assert.Empty(other.Session.Operations);
        Assert.NotEmpty(harness.Directory.Operations);
        Assert.Empty(harness.Directory.Writes);
        Assert.All(harness.Directory.Operations, o => Assert.True(o.Kind is FakeOperationKind.Search or FakeOperationKind.Get or FakeOperationKind.GetAllValues, o.Kind.ToString()));
    }

    [Fact]
    public async Task The_commands_have_no_server_and_no_credential_parameter_and_cannot_be_given_one()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var parameters = await harness.RunAsync(
            "foreach ($name in 'Get-AdmObject','Get-AdmUser','Get-AdmGroup','Get-AdmComputer','Get-AdmGroupMember','Search-AdmObject') { (Get-Command $name).Parameters.Keys }");
        var server = await harness.RunAsync("try { Get-AdmUser -Server dc02.contoso.test } catch { $_.FullyQualifiedErrorId.Split(',')[0] }");
        var credential = await harness.RunAsync("try { Get-AdmUser -Credential x } catch { $_.FullyQualifiedErrorId.Split(',')[0] }");

        var names = parameters.Objects.Select(o => o.ToString()).ToList();
        Assert.DoesNotContain("Server", names);
        Assert.DoesNotContain("Credential", names);
        Assert.DoesNotContain("ComputerName", names);
        Assert.Contains("SearchBase", names);
        Assert.Equal("NamedParameterNotFound", server.Single().ToString());
        Assert.Equal("NamedParameterNotFound", credential.Single().ToString());
    }

    [Fact]
    public async Task A_second_window_session_in_the_same_process_is_never_touched_by_the_first()
    {
        var first = await ReadHarness.CreateAsync();
        var second = await ReadHarness.CreateAsync();
        await using var _ = first;
        await using var __ = second;
        first.Directory.ClearOperations();
        second.Directory.ClearOperations();

        await first.RunAsync("Get-AdmUser -Identity jan.kowalski");
        var secondResult = await second.RunAsync("Get-AdmUser -Department IT");

        Assert.DoesNotContain(second.Directory.Operations, o => o.Search?.Filter.Contains("jan.kowalski", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Equal(2, secondResult.Objects.Count);
        Assert.NotEmpty(first.Directory.Operations);
        Assert.Equal(1, second.Directory.Operations.Count(o => o.Kind == FakeOperationKind.Search));
    }

    private sealed class AdmWindowContextOver(IDirectoryReader reader) : IAdmWindowContext
    {
        private readonly AdmWindowContext _inner = new(reader, "1.2.3-test");

        public IDirectoryReader Reader => _inner.Reader;

        public string ApplicationVersion => _inner.ApplicationVersion;

        public AdmKnownContainers KnownContainers => _inner.KnownContainers;

        public IAdmWindowServices Services => _inner.Services;
    }
}
