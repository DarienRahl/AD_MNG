using AdMng.Core.Ldap;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Tests.Detection;

/// <summary>Container rewrites of one cycle (section 10.7): deepest match, store order, reverse mapping.</summary>
public class DnRewriterTests
{
    private const string Domain = "DC=contoso,DC=test";

    private static readonly string[] Stored =
    [
        $"CN=Anna Alfowska,OU=Alfa,{Domain}",
        $"CN=Bogdan Betowski,OU=Beta,{Domain}",
        $"CN=Celina Głęboka,OU=Zespół,OU=Alfa,{Domain}",
        $"CN=Dariusz Obok,OU=Gamma Abroad,{Domain}",
        $"CN=Ewa Żurek,{Domain}",
    ];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShiftOfNames_RewritesEveryDnOnceByItsDeepestAncestor(bool alfaFirst)
    {
        var rewriter = new DnRewriter();
        (string Old, string New)[] moves = [($"OU=Alfa,{Domain}", $"OU=Beta,{Domain}"), ($"OU=Beta,{Domain}", $"OU=Gamma,{Domain}")];
        foreach (var (old, @new) in alfaFirst ? moves : moves.Reverse())
        {
            rewriter.Add(old, @new);
        }

        string[] expected =
        [
            $"CN=Anna Alfowska,OU=Beta,{Domain}",
            $"CN=Bogdan Betowski,OU=Gamma,{Domain}",
            $"CN=Celina Głęboka,OU=Zespół,OU=Beta,{Domain}",
            $"CN=Dariusz Obok,OU=Gamma Abroad,{Domain}",
            $"CN=Ewa Żurek,{Domain}",
        ];
        Assert.Equal(expected, Stored.Select(rewriter.Rewrite));
        Assert.Equal(expected, ApplySequentially(rewriter.StoreRewrites(0), Stored));
        Assert.Equal(Stored, expected.Select(rewriter.Original));
    }

    [Fact]
    public void SwapOfNames_UsesTemporaryDnsOutsideTheDirectory()
    {
        var rewriter = new DnRewriter();
        rewriter.Add($"OU=Alfa,{Domain}", $"OU=Beta,{Domain}");
        rewriter.Add($"OU=Beta,{Domain}", $"OU=Alfa,{Domain}");

        var store = rewriter.StoreRewrites(0);

        Assert.Equal(4, store.Count);
        Assert.All(store.Take(2), r => Assert.EndsWith(",DC=admng-rewrite,DC=invalid", r.NewSuffix, StringComparison.Ordinal));
        Assert.Equal(
            [$"CN=Anna Alfowska,OU=Beta,{Domain}", $"CN=Bogdan Betowski,OU=Alfa,{Domain}"],
            ApplySequentially(store, Stored.Take(2)));
    }

    [Fact]
    public void NestedMoves_WithoutConflict_AreOneRewriteEachDeepestFirst()
    {
        var rewriter = new DnRewriter();
        rewriter.Add($"OU=Alfa,{Domain}", $"OU=Alfa2,{Domain}");
        rewriter.Add($"OU=Zespół,OU=Alfa,{Domain}", $"OU=Zespół B,OU=Alfa2,{Domain}");

        var store = rewriter.StoreRewrites(0);

        Assert.Equal([$"OU=Zespół,OU=Alfa,{Domain}", $"OU=Alfa,{Domain}"], store.Select(r => r.OldSuffix));
        Assert.Equal($"CN=Celina Głęboka,OU=Zespół B,OU=Alfa2,{Domain}", ApplySequentially(store, [Stored[2]]).Single());
        Assert.Equal($"CN=Celina Głęboka,OU=Zespół B,OU=Alfa2,{Domain}", rewriter.Rewrite(Stored[2]));
        Assert.Equal(Stored[2], rewriter.Original($"CN=Celina Głęboka,OU=Zespół B,OU=Alfa2,{Domain}"));
    }

    [Fact]
    public void OccupiedNewDn_IsNotMappedBack()
    {
        var rewriter = new DnRewriter();
        rewriter.Add($"OU=Alfa,{Domain}", $"OU=Beta,{Domain}");

        rewriter.MarkOccupied($"OU=Beta,{Domain}");

        Assert.Equal(Stored[1], rewriter.Original(Stored[1]));
        Assert.Equal($"CN=Anna Alfowska,OU=Beta,{Domain}", rewriter.Rewrite(Stored[0]));
    }

    [Fact]
    public void MoveOntoTheNameOfARemovedOuWhoseChildMovedAway_RewritesEveryDnOnce()
    {
        // OU=Zespół left OU=Beta (→ OU=Wydział), OU=Beta was deleted, and the deeper OU=Alfa,OU=Dział,OU=Firma took its
        // name – OU=Alfa has a child of its own called OU=Zespół. All in one poll.
        var rewriter = new DnRewriter();
        rewriter.Add($"OU=Zespół,OU=Beta,{Domain}", $"OU=Wydział,{Domain}");
        rewriter.Add($"OU=Alfa,OU=Dział,OU=Firma,{Domain}", $"OU=Beta,{Domain}");
        string[] stored = [$"CN=Celina Głęboka,OU=Zespół,OU=Alfa,OU=Dział,OU=Firma,{Domain}", $"CN=Zofia Zespołowa,OU=Zespół,OU=Beta,{Domain}"];

        string[] expected = [$"CN=Celina Głęboka,OU=Zespół,OU=Beta,{Domain}", $"CN=Zofia Zespołowa,OU=Wydział,{Domain}"];
        Assert.Equal(expected, stored.Select(rewriter.Rewrite));
        Assert.Equal(expected, ApplySequentially(rewriter.StoreRewrites(0), stored));
    }

    [Fact]
    public void AnySetOfRewrites_TheStoreSequenceEqualsTheDeepestAncestorRule()
    {
        // Containers up to three levels deep with clashing names, random moves/renames among them: whatever the
        // recording order, applying StoreRewrites one after the other must equal Rewrite() for every stored DN.
        string[] names = ["Alfa", "Beta", "Zespół"];
        var containers = new List<string>();
        foreach (var a in names)
        {
            containers.Add($"OU={a},{Domain}");
            foreach (var b in names)
            {
                containers.Add($"OU={b},OU={a},{Domain}");
                containers.AddRange(names.Select(c => $"OU={c},OU={b},OU={a},{Domain}"));
            }
        }

        var stored = containers.Select(c => $"CN=Łucja Żółw,{c}").Concat(containers).ToList();
        var random = new Random(20261019);
        for (var run = 0; run < 2000; run++)
        {
            var rewriter = new DnRewriter();
            foreach (var old in containers.OrderBy(_ => random.Next()).Take(random.Next(1, 5)))
            {
                rewriter.Add(old, containers[random.Next(containers.Count)]);
            }

            Assert.Equal(stored.Select(rewriter.Rewrite), ApplySequentially(rewriter.StoreRewrites(0), stored));
        }
    }

    /// <summary>What the stores do: each rewrite in order, every strict descendant of its old suffix re-based.</summary>
    private static List<string> ApplySequentially(IReadOnlyList<DnRewrite> rewrites, IEnumerable<string> dns)
    {
        var result = dns.ToList();
        foreach (var rewrite in rewrites)
        {
            var oldBase = DistinguishedName.Parse(rewrite.OldSuffix);
            var newBase = DistinguishedName.Parse(rewrite.NewSuffix);
            for (var i = 0; i < result.Count; i++)
            {
                var dn = DistinguishedName.Parse(result[i]);
                if (dn.IsDescendantOf(oldBase))
                {
                    result[i] = DnRewriter.Rebase(dn, oldBase, newBase).ToString();
                }
            }
        }

        return result;
    }
}
