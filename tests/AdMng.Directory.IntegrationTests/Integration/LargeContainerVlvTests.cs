using System.Diagnostics;
using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Formatting;
using AdMng.Core.Ldap;
using AdMng.Directory.IntegrationTests.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using SortKey = AdMng.Core.Directory.SortKey;

namespace AdMng.Directory.IntegrationTests.Integration;

/// <summary>
/// VLV over an OU with a few thousand objects (created once per run in the run OU, removed with it by the lab cleanup):
/// the server behaviour the virtual list of the console relies on (docs/dev/phase1-navigation.md) and
/// <see cref="VirtualRowList"/> over the real session.
/// </summary>
public sealed class LargeContainerVlvTests(LabFixture lab)
{
    private const int Count = 3000;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _ou;

    private static string Name(int i) => $"Kontakt {i:D4} Żółw";

    [Fact]
    public async Task Windows_CoverTheWholeList_InBothDirections()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var search = Search(await LargeOuAsync());

        var first = await lab.Admin.GetVirtualListPageAsync(search, new VirtualListRequest { Count = 20 }, ct);
        var middle = await lab.Admin.GetVirtualListPageAsync(search, new VirtualListRequest { Offset = 1500, Count = 20, ContextId = first.ContextId }, ct);
        var last = await lab.Admin.GetVirtualListPageAsync(search, new VirtualListRequest { Offset = Count - 5, Count = 20, ContextId = middle.ContextId }, ct);
        var descending = await lab.Admin.GetVirtualListPageAsync(search with { Sort = new SortKey(Attr.Name, Descending: true) }, new VirtualListRequest { Offset = 10, Count = 3 }, ct);

        Assert.Equal(Count, first.TotalCount);
        Assert.Equal(Enumerable.Range(0, 20).Select(Name), first.Items.Select(i => i.Name));
        Assert.Equal(1500, middle.Offset);
        Assert.Equal(Enumerable.Range(1500, 20).Select(Name), middle.Items.Select(i => i.Name));
        Assert.Equal(Count - 5, last.Offset);
        Assert.Equal(Enumerable.Range(Count - 5, 5).Select(Name), last.Items.Select(i => i.Name));
        Assert.Equal([Name(Count - 11), Name(Count - 12), Name(Count - 13)], descending.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Seek_LandsAtOrAfterTheValue_InTheListOrder()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var search = Search(await LargeOuAsync());
        var descendingSearch = search with { Sort = new SortKey(Attr.Name, Descending: true) };

        var ascending = await lab.Admin.GetVirtualListPageAsync(search, new VirtualListRequest { SeekValue = "kontakt 123", Count = 2 }, ct);
        var descending = await lab.Admin.GetVirtualListPageAsync(descendingSearch, new VirtualListRequest { SeekValue = "Kontakt 123", Count = 2 }, ct);
        var beyond = await lab.Admin.GetVirtualListPageAsync(search, new VirtualListRequest { SeekValue = "Zenon", Count = 2 }, ct);

        // Ascending: the first entry >= the value, case-insensitive ("Kontakt 1230 Żółw").
        Assert.Equal(1230, ascending.Offset);
        Assert.Equal(Name(1230), ascending.Items[0].Name);

        // Descending: the first entry <= the value in the reversed order - "Kontakt 1239 Żółw" … "Kontakt 1230 Żółw" sort
        // above "Kontakt 123", so the target is "Kontakt 1229 Żółw" and the matching block lies just before it.
        Assert.Equal(Count - 1 - 1229, descending.Offset);
        Assert.Equal(Name(1229), descending.Items[0].Name);

        // No entry after the value: the target is past the end (no row to select).
        Assert.True(beyond.Offset >= Count - 1, $"offset {beyond.Offset}");
        Assert.True(beyond.Items.Count == 0 || beyond.Offset == Count, $"{beyond.Items.Count} items at {beyond.Offset}");
    }

    [Fact]
    public async Task VirtualRowList_ReadsPagesOnDemand_SeeksAndRefreshes()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var ou = await LargeOuAsync();
        var search = Search(ou);
        var context = new RowContext(
            new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL")),
            new AttributeFormatter(lab.Admin.Schema),
            DomainPolicy.Unknown,
            DateTimeOffset.UtcNow);
        var first = await lab.Admin.GetVirtualListPageAsync(search, new VirtualListRequest { Count = 100 }, ct);
        var requests = 0;
        using var rows = new VirtualRowList(
            first,
            100,
            (request, token) =>
            {
                Interlocked.Increment(ref requests);
                return lab.Admin.GetVirtualListPageAsync(search, request, token);
            },
            obj => new ObjectRowViewModel(obj, context),
            () => ObjectRowViewModel.CreatePlaceholder(context),
            new InlineDispatcher(),
            maxCachedPages: 4);

        Assert.Equal(Count, rows.Count);
        Assert.True(rows[2500].IsPlaceholder);
        await rows.Idle;
        Assert.Equal(Name(2500), rows[2500].Name);
        Assert.Equal(1, requests);

        Assert.Equal(2220, await rows.SeekAsync("Kontakt 222"));
        Assert.Equal(Name(2220), rows.GetLoaded(2220)?.Name);

        // Type-to-find. Samba compares a value that continues with a non-ASCII letter as smaller than the typed text
        // ("Kontakt 2222 Żółw" < "Kontakt 2222"), so its seek lands one row late - the walk back finds the row anyway.
        Assert.Equal(2222, await rows.FindPrefixAsync("Kontakt 2222", r => r.Name));
        Assert.Equal(2000, await rows.FindPrefixAsync("kontakt 2", r => r.Name));
        Assert.Null(await rows.FindPrefixAsync("Zenon", r => r.Name));

        var descendingSearch = search with { Sort = new SortKey(Attr.Name, Descending: true) };
        using (var descending = new VirtualRowList(
            await lab.Admin.GetVirtualListPageAsync(descendingSearch, new VirtualListRequest { Count = 100 }, ct),
            100,
            (request, token) => lab.Admin.GetVirtualListPageAsync(descendingSearch, request, token),
            obj => new ObjectRowViewModel(obj, context),
            () => ObjectRowViewModel.CreatePlaceholder(context),
            new InlineDispatcher()))
        {
            Assert.Equal(Count - 1 - 1239, await descending.FindPrefixAsync("Kontakt 123", r => r.Name));
            Assert.Equal(Count - 1 - 1234, await descending.FindPrefixAsync("Kontakt 1234", r => r.Name));
        }

        // A new object sorts in at the top: a refresh re-reads the shown pages and the count.
        var added = await lab.CreateContactAsync(ou, "Kontakt 0000a Żółw");
        try
        {
            _ = rows[1];
            await rows.RefreshAsync();
            Assert.Equal(Count + 1, rows.Count);
            Assert.Equal("Kontakt 0000a Żółw", rows.GetLoaded(1)?.Name);
            Assert.True(rows.CachedPageCount <= 4);
        }
        finally
        {
            await lab.Admin.DeleteAsync(added, subtree: false, CancellationToken.None);
        }
    }

    private static DirectorySearch Search(string ou) => new()
    {
        BaseDn = ou,
        Scope = SearchScope.OneLevel,
        Filter = LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.Contact),
        Attributes = [Attr.Name, Attr.ObjectClass, Attr.Description],
        Sort = new SortKey(Attr.Name),
    };

    /// <summary>The OU with <see cref="Count"/> contacts, created by the first test of the run that needs it.</summary>
    private async Task<string> LargeOuAsync()
    {
        await Gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            if (_ou is not null)
            {
                return _ou;
            }

            var stopwatch = Stopwatch.StartNew();
            var ou = await lab.CreateOuAsync(TestNames.Unique("Duża OU"));
            await Parallel.ForEachAsync(
                Enumerable.Range(0, Count),
                new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = TestContext.Current.CancellationToken },
                async (i, _) => await lab.CreateContactAsync(ou, Name(i)));
            TestContext.Current.SendDiagnosticMessage($"Created {Count} contacts in {stopwatch.Elapsed.TotalSeconds:F0} s.");
            _ou = ou;
            return ou;
        }
        finally
        {
            Gate.Release();
        }
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }
}
