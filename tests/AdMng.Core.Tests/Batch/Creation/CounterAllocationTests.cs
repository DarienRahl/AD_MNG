using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch.Creation;

namespace AdMng.Core.Tests.Batch.Creation;

/// <summary>
/// Counters (docs/dev/phase3-bulk-create.md 2.4) on the bulk-create sample: kd201, kd203 and kd205 live, kd207 deleted (in the
/// AD Recycle Bin). FillGaps and AfterHighest with and without the reuse of Recycle Bin numbers, pins, reservations,
/// sequences per prefix, width and step, windowed lookups, CounterTooLong, CounterExhausted, one query per prefix and the
/// counter preview.
/// </summary>
public sealed class CounterAllocationTests
{
    private readonly BulkCreateTestBed _bed = new();

    private static readonly (string, string)[] FourPeople =
        [("Łucja", "Żak"), ("Grzegorz", "Brzęczyszczykiewicz"), ("Zofia", "Wiśniewska"), ("Paweł", "Kaźmierczak")];

    private static IReadOnlyList<string?> Sam(BulkCreateComputation computation) => BulkCreateTestBed.Values(computation, Attr.SamAccountName);

    [Fact]
    public async Task FillGaps_TakesTheLowestFreeNumbers_TheRecycleBinNumberCountsAsTaken()
    {
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(FourPeople)));

        Assert.False(computation.HasRunErrors, string.Join(", ", computation.Issues.Select(i => i.Code)));
        Assert.Equal(["kd202", "kd204", "kd206", "kd208"], Sam(computation));
        Assert.Equal(["kd202@contoso.com", "kd204@contoso.com", "kd206@contoso.com", "kd208@contoso.com"], BulkCreateTestBed.Values(computation, Attr.UserPrincipalName));
        Assert.Equal(["kd202@contoso.com", "kd204@contoso.com", "kd206@contoso.com", "kd208@contoso.com"], BulkCreateTestBed.Values(computation, Attr.Mail));
        Assert.Equal([202, 204, 206, 208], computation.Rows.Select(r => r.CounterNumber!.Value));
        Assert.All(computation.Rows, r => Assert.Equal(BulkRowStatus.Ok, r.Status));
        Assert.Equal("Łucja Żak", computation.Rows[0].Value(Attr.Cn));
    }

    [Fact]
    public async Task FillGaps_WithRecycleBinReuse_GivesTheDeletedNumber_AndWarns()
    {
        var input = _bed.Input(BulkCreateTestBed.People(FourPeople), configure: r => r with { ReuseRecycleBinNumbers = true });

        var computation = await _bed.ComputeAsync(input);

        Assert.Equal(["kd202", "kd204", "kd206", "kd207"], Sam(computation));
        var reused = computation.Rows[3];
        Assert.Equal(BulkRowStatus.Warning, reused.Status);
        Assert.Equal(
            [Attr.SamAccountName, Attr.UserPrincipalName, Attr.Mail],
            reused.Issues.Where(i => i.Code == BulkCreateIssueCodes.NameInRecycleBin).Select(i => i.Attribute));
    }

    [Fact]
    public async Task AfterHighest_ContinuesAfterTheHighestNumber_IncludingTheRecycleBin()
    {
        var input = _bed.Input(BulkCreateTestBed.People(FourPeople[..3]), configure: r => r with { CounterMode = CounterMode.AfterHighest });

        var computation = await _bed.ComputeAsync(input);

        Assert.Equal(["kd208", "kd209", "kd210"], Sam(computation));
    }

    [Fact]
    public async Task AfterHighest_WithRecycleBinReuse_ContinuesAfterTheHighestLiveNumber()
    {
        var input = _bed.Input(BulkCreateTestBed.People(FourPeople[..3]),
            configure: r => r with { CounterMode = CounterMode.AfterHighest, ReuseRecycleBinNumbers = true });

        var computation = await _bed.ComputeAsync(input);

        Assert.Equal(["kd206", "kd207", "kd208"], Sam(computation));
        Assert.NotNull(BulkCreateTestBed.IssueOf(computation.Rows[1], BulkCreateIssueCodes.NameInRecycleBin, Attr.SamAccountName));
        Assert.Null(BulkCreateTestBed.IssueOf(computation.Rows[0], BulkCreateIssueCodes.NameInRecycleBin));
    }

    [Fact]
    public async Task Pins_AreKeptAfterAnExclusion_AndANewRowGetsANumberAfterThem()
    {
        var first = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(FourPeople[..3])));
        Assert.Equal(["kd202", "kd204", "kd206"], Sam(first));

        var input = _bed.Input(BulkCreateTestBed.People(FourPeople)) with { ExcludedRows = [1], PinnedNumbers = first.Numbers };
        var second = await _bed.ComputeAsync(input);

        Assert.Equal(BulkRowStatus.Excluded, second.Rows[0].Status);
        Assert.Equal([204, 206, 208], second.Rows.Skip(1).Select(r => r.CounterNumber!.Value));
        Assert.Equal(["kd204", "kd206", "kd208"], Sam(second).Skip(1));
    }

    [Fact]
    public async Task Pins_ATakenOrOffGridPinIsReplaced()
    {
        var input = _bed.Input(BulkCreateTestBed.People(FourPeople[..2])) with { PinnedNumbers = new Dictionary<int, int> { [1] = 203, [2] = 250 } };

        var computation = await _bed.ComputeAsync(input);

        Assert.Equal(["kd202", "kd250"], Sam(computation));
    }

    [Fact(Skip = BulkCreateTestBed.Wave1)]
    public async Task Pins_AreKeptAfterAnEdit()
    {
        var first = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(FourPeople[..3])));
        var edits = new Dictionary<int, IReadOnlyDictionary<string, string>> { [2] = new Dictionary<string, string> { [Attr.GivenName] = "Grażyna" } };

        var second = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(FourPeople[..3])) with { Edits = edits, PinnedNumbers = first.Numbers });

        Assert.Equal(["kd202", "kd204", "kd206"], Sam(second));
        Assert.Equal("Grażyna Brzęczyszczykiewicz", second.Rows[1].Value(Attr.Cn));
        Assert.Equal(BulkValueSource.Edited, second.Rows[1].Values.Single(v => v.Attribute == Attr.GivenName).Source);
    }

    [Fact]
    public async Task ReleasedValues_KeepThePinsAfterAnUndo()
    {
        // kd202 was created by this window and deleted by its own undo: its deleted copy is in the Recycle Bin.
        var created = _bed.Builder.AddUser("Łucja", "Żak", _bed.Sample.NewEmployeesOu, new Dictionary<string, object?>
        {
            [Attr.SamAccountName] = "kd202",
            [Attr.UserPrincipalName] = "kd202@contoso.com",
            [Attr.Mail] = "kd202@contoso.com",
        });
        await _bed.Session.DeleteAsync(created, subtree: false, BulkCreateTestBed.Ct);
        var pins = new Dictionary<int, int> { [1] = 202 };

        var withoutRelease = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(FourPeople[..1])) with { PinnedNumbers = pins });
        _bed.Refresh();
        var released = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(FourPeople[..1])) with
        {
            PinnedNumbers = pins,
            ReleasedValues = ["kd202", "kd202@contoso.com", "Łucja Żak"],
        });

        Assert.Equal(["kd204"], Sam(withoutRelease));
        Assert.Equal(["kd202"], Sam(released));
        Assert.Equal(BulkRowStatus.Ok, released.Rows[0].Status);
    }

    [Fact]
    public async Task ColumnValues_AreReservedBeforeAnyNumberIsGiven()
    {
        var table = BulkCreateTestBed.Table(["Łucja", "Żak", string.Empty], ["Jan", "Nowak", "kd202"], ["Ewa", "Bąk", string.Empty]);
        var mappings = new List<ColumnMapping>(BulkCreateTestBed.NameMappings) { new(2, ColumnTargetKind.Attribute, Attr.SamAccountName) };

        var computation = await _bed.ComputeAsync(_bed.Input(table, mappings: mappings));

        Assert.Equal(["kd204", "kd202", "kd206"], Sam(computation));
        Assert.Equal([204, (int?)null, 206], computation.Rows.Select(r => r.CounterNumber));
        Assert.Equal(BulkValueSource.Column, computation.Rows[1].Values.Single(v => v.Attribute == Attr.SamAccountName).Source);
        Assert.Equal("kd202@contoso.com", computation.Rows[1].Value(Attr.UserPrincipalName));
    }

    [Fact(Skip = BulkCreateTestBed.Wave1)]
    public async Task EditedValues_AreReservedBeforeAnyNumberIsGiven()
    {
        var edits = new Dictionary<int, IReadOnlyDictionary<string, string>> { [3] = new Dictionary<string, string> { [Attr.SamAccountName] = "kd202" } };

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(FourPeople[..3])) with { Edits = edits });

        Assert.Equal(["kd204", "kd206", "kd202"], Sam(computation));
        Assert.Equal(BulkValueSource.Edited, computation.Rows[2].Values.Single(v => v.Attribute == Attr.SamAccountName).Source);
    }

    [Fact]
    public async Task Sequences_OnePerPrefix()
    {
        var rules = new AttributeRule[]
        {
            new(Attr.SamAccountName, "{givenName|first:1|sam|lower}{sn|first:1|sam|lower}{$nr:1:3}"),
            new(Attr.UserPrincipalName, "{sAMAccountName}"),
            new(Attr.Cn, "{givenName} {sn}"),
        };

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(("Jan", "Kowalski"), ("Joanna", "Kot"), ("Anna", "Nowak"), ("Jerzy", "Kłos")), rules));

        Assert.Equal(["jk001", "jk002", "an001", "jk003"], Sam(computation));
    }

    [Fact]
    public async Task WidthAndStep_AndAnAttributeThatTakesTheSpec()
    {
        var rules = new AttributeRule[] { new(Attr.SamAccountName, "kd{$nr:200:0:10}"), new(Attr.EmployeeNumber, "{$nr}"), new(Attr.Cn, "{givenName} {sn}") };

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(FourPeople[..3]), rules));

        Assert.Equal(["kd200", "kd210", "kd220"], Sam(computation));
        Assert.Equal(["200", "210", "220"], BulkCreateTestBed.Values(computation, Attr.EmployeeNumber));
    }

    [Fact]
    public async Task ShortPrefix_UsesWindowedEqualityLookups()
    {
        _bed.AddUser("Ula", "Pierwsza", new Dictionary<string, object?> { [Attr.SamAccountName] = "u0001" });
        var rules = new AttributeRule[] { new(Attr.SamAccountName, "u{$nr:1:4}"), new(Attr.Cn, "{givenName} {sn}") };

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(FourPeople[..2]), rules));

        Assert.Equal(["u0002", "u0003"], Sam(computation));
        Assert.DoesNotContain(_bed.Searches, s => s.Search!.Filter.Contains("(sAMAccountName=u*)", StringComparison.Ordinal));
        Assert.Contains(_bed.Searches, s => s.Search!.Filter.Contains("(sAMAccountName=u0050)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CounterOnlyInTheUpn_TheColumnLoginStays()
    {
        var table = BulkCreateTestBed.Table(["Łucja", "Żak", "lucja.zak"], ["Jan", "Nowak", "jan.nowak"]);
        var mappings = new List<ColumnMapping>(BulkCreateTestBed.NameMappings) { new(2, ColumnTargetKind.Attribute, Attr.SamAccountName) };
        var rules = new AttributeRule[] { new(Attr.UserPrincipalName, "kd{$nr:201}"), new(Attr.Cn, "{givenName} {sn}") };

        var computation = await _bed.ComputeAsync(_bed.Input(table, rules, mappings));

        Assert.Equal(["lucja.zak", "jan.nowak"], Sam(computation));
        Assert.Equal(["kd202@contoso.com", "kd204@contoso.com"], BulkCreateTestBed.Values(computation, Attr.UserPrincipalName));
    }

    [Fact]
    public async Task ATakenAddress_SkipsTheNumber()
    {
        _bed.Builder.AddContact("Kontakt", "Zewnętrzny", _bed.Company.CompanyOu, new Dictionary<string, object?> { [Attr.Mail] = "kd202@contoso.com" });

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(FourPeople[..1])));

        Assert.Equal(["kd204"], Sam(computation));
    }

    [Fact]
    public async Task AnAddressInProxyAddresses_SkipsTheNumber()
    {
        _bed.Builder.AddContact("Kontakt", "Zewnętrzny", _bed.Company.CompanyOu, new Dictionary<string, object?>
        {
            [Attr.ProxyAddresses] = new[] { "SMTP:inny@contoso.com", "smtp:KD202@contoso.com" },
        });

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(FourPeople[..1])));

        Assert.Equal(["kd204"], Sam(computation));
    }

    [Fact]
    public async Task AnImplicitUpn_SkipsTheNumber()
    {
        _bed.AddUser("Inny", "Użytkownik", new Dictionary<string, object?> { [Attr.SamAccountName] = "inny", [Attr.UserPrincipalName] = "kd202@contoso.test" });

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(FourPeople[..1])));

        Assert.Equal(["kd204"], Sam(computation));
    }

    [Fact]
    public async Task CounterTooLong_IsAnError_TheValueIsNeverCut()
    {
        var rules = new AttributeRule[] { new(Attr.SamAccountName, "abcdefghijklmnopqr{$nr:201}"), new(Attr.Cn, "{givenName} {sn}") };

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(FourPeople[..1]), rules));

        var row = Assert.Single(computation.Rows);
        Assert.Equal("abcdefghijklmnopqr201", row.Value(Attr.SamAccountName));
        Assert.Equal(BulkRowStatus.Error, row.Status);
        var issue = BulkCreateTestBed.IssueOf(row, BulkCreateIssueCodes.CounterTooLong, Attr.SamAccountName)!;
        Assert.Equal(["20"], issue.Arguments);
    }

    [Fact]
    public async Task CounterExhausted_AboveTheLastNumber()
    {
        _bed.AddUser("Ostatni", "Numer", new Dictionary<string, object?> { [Attr.SamAccountName] = "kd999999999" });
        var rules = new AttributeRule[] { new(Attr.SamAccountName, "kd{$nr:999999999}"), new(Attr.Cn, "{givenName} {sn}") };

        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(FourPeople[..1]), rules));

        var row = Assert.Single(computation.Rows);
        Assert.Equal(BulkRowStatus.Error, row.Status);
        Assert.NotNull(BulkCreateTestBed.IssueOf(row, BulkCreateIssueCodes.CounterExhausted));
        Assert.Null(row.CounterNumber);
        Assert.DoesNotContain(CreationPatternTokens.NumberSentinel, row.Value(Attr.SamAccountName) ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnePrefixSearch_PerAttribute_ForTheWholeBatch()
    {
        var computation = await _bed.ComputeAsync(_bed.Input(BulkCreateTestBed.People(FourPeople)));

        Assert.Equal(4, computation.Rows.Count(r => r.CounterNumber is not null));
        Assert.Single(_bed.Searches, s => s.Search!.Filter == "(sAMAccountName=kd*)");
        Assert.Single(_bed.Searches, s => s.Search!.Filter == "(&(userPrincipalName=kd*)(userPrincipalName=*@contoso.com))");
        Assert.Single(_bed.Searches, s => s.Search!.Filter == "(&(userPrincipalName=kd*)(userPrincipalName=*@contoso.test))");
        Assert.Single(_bed.Searches, s => s.Search!.Filter.StartsWith("(|(&(mail=kd*)(mail=*@contoso.com))", StringComparison.Ordinal));
        Assert.DoesNotContain(_bed.Searches, s => s.Search!.Filter.Contains("(sAMAccountName=kd202)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recompute_ReadsNothingNew_FromTheCachedLookups()
    {
        var input = _bed.Input(BulkCreateTestBed.People(FourPeople));
        var first = await _bed.ComputeAsync(input);
        var reads = _bed.Session.Operations.Count;

        var second = await _bed.ComputeAsync(input with { PinnedNumbers = first.Numbers });

        Assert.Equal(Sam(first), Sam(second));
        Assert.Equal(reads, _bed.Session.Operations.Count);
    }

    [Fact]
    public async Task Preview_TheFirstFreeNumber_AndTheHighest()
    {
        var fillGaps = await _bed.Planner.PreviewCounterAsync(_bed.Session, _bed.Input(BulkCreateTestBed.People()), _bed.Lookups, BulkCreateTestBed.Ct);
        var afterHighest = await _bed.Planner.PreviewCounterAsync(_bed.Session,
            _bed.Input(BulkCreateTestBed.People(), configure: r => r with { CounterMode = CounterMode.AfterHighest }), _bed.Lookups, BulkCreateTestBed.Ct);
        var reuse = await _bed.Planner.PreviewCounterAsync(_bed.Session,
            _bed.Input(BulkCreateTestBed.People(), configure: r => r with { CounterMode = CounterMode.AfterHighest, ReuseRecycleBinNumbers = true }),
            _bed.Lookups, BulkCreateTestBed.Ct);

        Assert.Equal(new CounterPreview("kd", 202, 207), fillGaps);
        Assert.Equal(new CounterPreview("kd", 208, 207), afterHighest);
        Assert.Equal(new CounterPreview("kd", 206, 205), reuse);
    }

    [Fact]
    public async Task Preview_NothingForAPrefixThatIsNotConstant_OrWithoutACounter()
    {
        var perPerson = _bed.Input(BulkCreateTestBed.People(), [new AttributeRule(Attr.SamAccountName, "{givenName|first:1}{$nr}")]);
        var noCounter = _bed.Input(BulkCreateTestBed.People(), BulkCreateTestBed.StandardRules);

        Assert.Equal(new CounterPreview(null, null, null), await _bed.Planner.PreviewCounterAsync(_bed.Session, perPerson, _bed.Lookups, BulkCreateTestBed.Ct));
        Assert.Equal(new CounterPreview(null, null, null), await _bed.Planner.PreviewCounterAsync(_bed.Session, noCounter, _bed.Lookups, BulkCreateTestBed.Ct));
    }
}
