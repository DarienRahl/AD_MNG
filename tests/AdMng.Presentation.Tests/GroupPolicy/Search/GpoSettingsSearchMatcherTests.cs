using System.Diagnostics;
using System.Globalization;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Presentation.GroupPolicy.Search;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Tests.GroupPolicy.Search;

/// <summary>
/// The matching of "Wyszukiwarka ustawień GPO" (K7, 21.3 budgets, 21.4) on hand-built indexes: folding of case and Polish
/// letters, the agreement of values, the 2 000-group cap and a pass over 100 000 records within a generous time bound.
/// </summary>
public sealed class GpoSettingsSearchMatcherTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Długość HASŁA", "dlugosc hasla")]
    [InlineData("Zażółć gęślą jaźń", "zazolc gesla jazn")]
    [InlineData("K:", "k:")]
    [InlineData("", "")]
    public void Fold_IgnoresCaseAndPolishLetters(string text, string folded) => Assert.Equal(folded, GpoSettingsMatcher.Fold(text));

    [Fact]
    public void Agreement_ComparesOnlyTheApplyingOccurrences()
    {
        GpoSettingUnit Unit(string value, bool disabled = false, GpoUnitComparability comparability = GpoUnitComparability.Comparable) =>
            SyntheticIndex.Unit("K|Computer/Security/System Access/LockoutBadCount", "LockoutBadCount", value) with
            {
                Disabled = disabled,
                Comparability = comparability,
            };

        Assert.Equal(GpoValueAgreement.Single, GpoSettingsMatcher.Agreement([Unit("5")]));
        Assert.Equal(GpoValueAgreement.Single, GpoSettingsMatcher.Agreement([Unit("5"), Unit("3", disabled: true)]));
        Assert.Equal(GpoValueAgreement.Same, GpoSettingsMatcher.Agreement([Unit("5"), Unit("5")]));
        Assert.Equal(GpoValueAgreement.Different, GpoSettingsMatcher.Agreement([Unit("5"), Unit("3")]));
        Assert.Equal(GpoValueAgreement.NotComparable, GpoSettingsMatcher.Agreement([Unit("5"), Unit(string.Empty, comparability: GpoUnitComparability.Masked)]));
    }

    [Fact]
    public void AreaOf_PutsEveryUnitKindIntoOneArea()
    {
        Assert.All(Enum.GetValues<GpoSettingUnitKind>(), k => Assert.True(Enum.IsDefined(GpoSettingsMatcher.AreaOf(k))));
        Assert.Equal(GpoSearchArea.AdministrativeTemplates, GpoSettingsMatcher.AreaOf(GpoSettingUnitKind.RegistryValue));
        Assert.Equal(GpoSearchArea.Security, GpoSettingsMatcher.AreaOf(GpoSettingUnitKind.RestrictedGroup));
        Assert.Equal(GpoSearchArea.Audit, GpoSettingsMatcher.AreaOf(GpoSettingUnitKind.AuditSubcategory));
        Assert.Equal(GpoSearchArea.Preferences, GpoSettingsMatcher.AreaOf(GpoSettingUnitKind.Preference));
        Assert.Equal(GpoSearchArea.Scripts, GpoSettingsMatcher.AreaOf(GpoSettingUnitKind.ScriptFile));
        Assert.Equal(GpoSearchArea.Other, GpoSettingsMatcher.AreaOf(GpoSettingUnitKind.Comment));
    }

    [Fact]
    public void AMatchPass_Over100000Records_StaysWellUnderHalfASecond_AndTheResultIsCapped()
    {
        var view = SyntheticIndex.View(gpos: 500, unitsPerGpo: 200, distinct: 2600);
        var corpus = GpoSearchCorpus.Build(view, GpoIndexTexts.For(new ResxLocalizer(Polish), view), Ct);
        Assert.Equal(100_000, corpus.Records.Count);
        Assert.Equal(2600, corpus.Groups.Count);

        // A query that matches nothing scans every field of every record (the worst case).
        var none = new GpoSearchQuery("żadne-takie-ustawienie");
        GpoSettingsMatcher.Match(corpus, none, Ct);
        var best = TimeSpan.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var watch = Stopwatch.StartNew();
            var result = GpoSettingsMatcher.Match(corpus, none, Ct);
            watch.Stop();
            Assert.Empty(result.Groups);
            best = watch.Elapsed < best ? watch.Elapsed : best;
        }

        Assert.True(best < TimeSpan.FromMilliseconds(500), $"best pass {best.TotalMilliseconds} ms");

        // Everything matches: the first 2 000 groups in GPMC's order, the total kept for the note.
        var all = GpoSettingsMatcher.Match(corpus, new GpoSearchQuery("ustawienie testowe"), Ct);
        Assert.Equal(GpoSettingsMatcher.MaxGroups, all.Groups.Count);
        Assert.Equal(2600, all.TotalGroups);
        Assert.True(all.IsCapped);
        Assert.Equal(100_000, all.MatchedOccurrences);
        Assert.Equal(all.Groups.OrderBy(g => corpus.Groups.ToList().IndexOf(g)), all.Groups);

        // Empty and one-character queries: the summary only.
        Assert.True(GpoSettingsMatcher.Match(corpus, new GpoSearchQuery(string.Empty), Ct).IsSummary);
        Assert.True(GpoSettingsMatcher.Match(corpus, new GpoSearchQuery(" u "), Ct).IsSummary);
    }

    [Fact]
    public void Match_IsCancellable()
    {
        var view = SyntheticIndex.View(gpos: 20, unitsPerGpo: 300, distinct: 300);
        var corpus = GpoSearchCorpus.Build(view, GpoIndexTexts.For(new ResxLocalizer(Polish), view), Ct);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => GpoSettingsMatcher.Match(corpus, new GpoSearchQuery("ustawienie"), cancelled.Token));
    }

    [Fact]
    public async Task TheDocument_ShowsAtMost2000Groups_WithANoteToNarrowTheSearch()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        var index = new FixedIndexService(SyntheticIndex.View(gpos: 50, unitsPerGpo: 50, distinct: 2500));
        using var document = new GpoSettingsSearchDocumentViewModel(h.Services, h.Actions, index, "ustawienie");
        await document.LoadAsync();

        Assert.Equal(1, index.Calls);
        Assert.Equal(2000, document.Groups.Count);
        Assert.Equal(2000, document.Rows.Count);
        Assert.True(document.IsCapped);
        Assert.Equal($"Zawęź wyszukiwanie – pokazano {2000.ToString("N0", Polish)} z {2500.ToString("N0", Polish)} grup", document.CapNote);
        Assert.StartsWith("Znalezione ustawienia: " + 2500.ToString("N0", Polish), document.ResultText, StringComparison.Ordinal);

        document.SearchText = "ustawienie testowe 00042";
        await document.SearchCommand.ExecuteAsync(null);
        Assert.False(document.IsCapped);
        Assert.Null(document.CapNote);
    }

    /// <summary>An index service that always returns one view (counts the calls).</summary>
    private sealed class FixedIndexService(GpoIndexView view) : IGpoSettingsIndexService
    {
        public int Calls { get; private set; }

        public GpoIndexView? Current => view;

        public bool IsStale => false;

        public bool IsBuilding => false;

        public GpoIndexProgress? Progress => null;

        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }

        public Task<GpoIndexView> GetAsync(GpoIndexRefresh refresh, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(view);
        }

        public void CancelBuild()
        {
        }

        public void Invalidate()
        {
        }
    }
}

/// <summary>Hand-built indexes of security values ("Ustawienie testowe n"), as many GPOs and units as a test needs.</summary>
internal static class SyntheticIndex
{
    public static GpoSettingUnit Unit(string baseId, string name, string value) => new()
    {
        Id = baseId,
        BaseId = baseId,
        Kind = GpoSettingUnitKind.SecurityValue,
        Scope = GpoScope.Computer,
        Area = GpoSettingArea.SystemAccess,
        Source = SecurityTemplate.RelativePath,
        Settings = [new GpoSetting { Key = baseId[2..], Value = value, Source = SecurityTemplate.RelativePath }],
        ValueFingerprint = name + "=" + value,
    };

    /// <summary><paramref name="gpos"/> GPOs with <paramref name="unitsPerGpo"/> units each, spread over <paramref name="distinct"/> base ids.</summary>
    public static GpoIndexView View(int gpos, int unitsPerGpo, int distinct)
    {
        var entries = new List<GpoIndexEntry>(gpos);
        var next = 0;
        for (var g = 0; g < gpos; g++)
        {
            var id = new Guid(g + 1, 0x4b37, 0x4000, [0x80, 0, 0, 0, 0, 0, 0, 7]);
            var units = new List<GpoSettingUnit>(unitsPerGpo);
            for (var u = 0; u < unitsPerGpo; u++)
            {
                var n = next++ % distinct;
                var name = "Ustawienie testowe " + n.ToString("D5", CultureInfo.InvariantCulture);
                units.Add(Unit("K|Computer/Security/System Access/" + name, name, (g % 7).ToString(CultureInfo.InvariantCulture)));
            }

            entries.Add(new GpoIndexEntry
            {
                Gpo = new GpoInfo { Id = id, Dn = GpLinkValue.GpoDn(id, "DC=contoso,DC=test"), DisplayName = "Syntetyczny GPO " + g.ToString("D3", CultureInfo.InvariantCulture) },
                Status = GpoIndexStatus.Read,
                Units = units,
                Settings = [.. units.SelectMany(u => u.Settings)],
            });
        }

        var index = new GpoSettingsIndex
        {
            Server = "dc01.contoso.test",
            DomainDn = "DC=contoso,DC=test",
            DomainDnsName = "contoso.test",
            SysvolRead = true,
            Entries = entries,
            BuiltAt = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero),
        };
        return new GpoIndexView(index, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), CultureInfo.GetCultureInfo("pl-PL"));
    }
}
