using AdMng.Core.GroupPolicy.Content;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Gpo;

namespace AdMng.Watchdog.Core.Tests.Gpo;

/// <summary>"Setting: was → is" rows of stage B design 8.6 with the 200-row limit.</summary>
public sealed class GpoSettingsDifferTests
{
    [Fact]
    public void IdenticalSettingsGiveNoChange()
    {
        var settings = new[] { Text("Computer/A", "1"), Text("Computer/B", "2") };

        Assert.True(GpoSettingsDiffer.Diff(settings, [.. settings]).IsEmpty);
    }

    [Fact]
    public void KeysCompareCaseInsensitively()
    {
        var diff = GpoSettingsDiffer.Diff([Text(@"Computer/Registry/HKLM\SOFTWARE\X!V", "REG_DWORD:1")], [Text(@"Computer/Registry/HKLM\Software\x!v", "REG_DWORD:1")]);

        Assert.True(diff.IsEmpty);
    }

    [Fact]
    public void ModifiedAddedRemoved()
    {
        var diff = GpoSettingsDiffer.Diff(
            [Text("Computer/Security/System Access/MinimumPasswordLength", "14", GpoSettingCategory.AccountPolicy), Text("Computer/Old", "x")],
            [Text("Computer/Security/System Access/MinimumPasswordLength", "8", GpoSettingCategory.AccountPolicy), Text("Computer/New", "Łódź")]);

        Assert.Equal((1, 1, 1), (diff.Added, diff.Removed, diff.Modified));
        var modified = Row(diff, "Computer/Security/System Access/MinimumPasswordLength");
        Assert.Equal(["14"], modified.OldValues);
        Assert.Equal(["8"], modified.NewValues);
        Assert.Equal(AttributeValueKind.Text, modified.ValueKind);
        Assert.Empty(Row(diff, "Computer/New").OldValues);
        Assert.Equal(["Łódź"], Row(diff, "Computer/New").NewValues);
        Assert.Equal(["x"], Row(diff, "Computer/Old").OldValues);
        Assert.Empty(Row(diff, "Computer/Old").NewValues);
        Assert.Equal(modified, diff.Changes[0]); // sensitive first
        Assert.True(diff.SensitiveChanged);
        Assert.Contains(GpoSettingCategory.AccountPolicy, diff.ChangedCategories);
    }

    [Fact]
    public void ListsReportAddedAndRemovedElements()
    {
        var diff = GpoSettingsDiffer.Diff(
            [List("Computer/Security/Privilege Rights/SeDebugPrivilege", "S-1-5-32-544", "S-1-5-21-1-2-3-1105")],
            [List("Computer/Security/Privilege Rights/SeDebugPrivilege", "S-1-5-32-544", "S-1-5-21-1-2-3-4242")]);

        var row = Assert.Single(diff.Changes);
        Assert.Equal(["S-1-5-21-1-2-3-4242"], row.Added);
        Assert.Equal(["S-1-5-21-1-2-3-1105"], row.Removed);
        Assert.Equal((2, 2), (row.OldCount, row.NewCount));
    }

    [Fact]
    public void MaskedSettingsShowOnlyPresence()
    {
        var masked = new GpoSetting { Key = "Computer/Preferences/Groups/{1}/cpassword", Kind = GpoSettingKind.Masked, Category = GpoSettingCategory.PreferenceGroups };

        var added = Assert.Single(GpoSettingsDiffer.Diff([], [masked]).Changes);
        Assert.True(added.Redacted);
        Assert.Empty(added.OldValues);
        Assert.Empty(added.NewValues);
        Assert.Equal((0, 1), (added.OldCount, added.NewCount));
        Assert.True(GpoSettingsDiffer.Diff([], [masked]).GppPasswordPresent);
        Assert.True(GpoSettingsDiffer.Diff([masked], [masked]).IsEmpty); // a new secret value is not detectable – only presence

        var becameSecret = Assert.Single(GpoSettingsDiffer.Diff([Text(masked.Key, "jawne")], [masked]).Changes);
        Assert.True(becameSecret.Redacted);
        Assert.Empty(becameSecret.OldValues);
    }

    [Fact]
    public void HashedValuesAreMarked()
    {
        var row = Assert.Single(GpoSettingsDiffer.Diff([Text("Computer/A", "short")], [new GpoSetting { Key = "Computer/A", Value = GpoValues.Hash("long"), Kind = GpoSettingKind.Hash }]).Changes);

        Assert.Equal(AttributeValueKind.Hash, row.ValueKind);
    }

    [Fact]
    public void AtMost200RowsSensitiveFirst()
    {
        var before = Enumerable.Range(0, 240).Select(i => Text("Computer/Preferences/X/" + GpoFixtures.Invariant(i), "a")).ToList();
        var after = before.Select(s => s with { Value = "b" }).ToList();
        after.AddRange(Enumerable.Range(0, 10).Select(i => Text("Computer/Security/Privilege Rights/Se" + GpoFixtures.Invariant(i), "S-1-5-32-544", GpoSettingCategory.PrivilegeRights)));

        var diff = GpoSettingsDiffer.Diff(before, after);

        Assert.True(diff.Truncated);
        Assert.Equal(250, diff.Total);
        Assert.Equal(GpoSettingsDiffer.MaxChanges, diff.Changes.Count);
        Assert.All(diff.Changes.Take(10), c => Assert.Contains("Privilege Rights", c.Attribute, StringComparison.Ordinal));
        var summary = diff.Changes[^1];
        Assert.Equal(GpoSettingsDiffer.SummaryAttribute, summary.Attribute);
        Assert.True(summary.Truncated);
        Assert.Equal((51, 250), (summary.OldCount, summary.NewCount));
    }

    [Fact]
    public void ExactlyTwoHundredRowsAreNotTruncated()
    {
        var after = Enumerable.Range(0, 200).Select(i => Text("Computer/X/" + GpoFixtures.Invariant(i), "a")).ToList();

        var diff = GpoSettingsDiffer.Diff([], after);

        Assert.False(diff.Truncated);
        Assert.Equal(200, diff.Changes.Count);
        Assert.False(diff.SensitiveChanged);
    }

    private static GpoSetting Text(string key, string value, GpoSettingCategory category = GpoSettingCategory.Preferences) =>
        new() { Key = key, Value = value, Category = category };

    private static GpoSetting List(string key, params string[] elements)
    {
        var sink = new GpoSettingSink("test");
        sink.List(key, elements, GpoSettingCategory.PrivilegeRights);
        return sink.Settings[0];
    }

    private static AttributeChange Row(GpoSettingsDiff diff, string key) => Assert.Single(diff.Changes, c => c.Attribute == key);
}
