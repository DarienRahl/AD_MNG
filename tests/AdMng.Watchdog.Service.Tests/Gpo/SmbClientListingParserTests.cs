using AdMng.Watchdog.Core.Gpo;
using AdMng.Watchdog.Service.Gpo;

namespace AdMng.Watchdog.Service.Tests.Gpo;

/// <summary>
/// The smbclient listing parser (stage B design 8.3) with output captured on the Samba lab (Ubuntu, Samba 4.19.5 – the
/// Debian 12/13 versions 4.17/4.22 print the same <c>display_finfo</c> format) and hostile listings: names with spaces,
/// multi-byte characters (the column padding counts bytes), names that look like sizes, dates or errors, forged lines
/// and folder headers.
/// </summary>
public sealed class SmbClientListingParserTests
{
    private static readonly Guid Explore = Guid.Parse("36F97CB4-4935-42E4-9950-A93B706A5C11");
    private static readonly Guid DefaultDomainPolicy = Guid.Parse("31B2F340-016D-11D2-945F-00C04FB984F9");

    private static SysvolGpoFolder Folder(Guid gpo) => SysvolGpoFolder.Create("vm.contoso.test", "contoso.test", gpo);

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Gpo", "Fixtures", name));

    [Fact]
    public void Samba419_GpoFolder_IsParsedRecursively()
    {
        var result = SmbClientListingParser.Parse(Fixture("smbclient-4.19-ls-gpo.txt"), Folder(Explore));

        Assert.Empty(result.Errors);
        Assert.Equal(0, result.Listing.RejectedEntries);
        var entries = result.Listing.Entries.ToDictionary(e => e.RelativePath);
        Assert.Equal(
            [
                "GPT.INI", "Machine", "Machine/Microsoft", "Machine/Microsoft/Windows NT", "Machine/Microsoft/Windows NT/SecEdit",
                "Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf", "Machine/Preferences", "Machine/Preferences/Groups",
                "Machine/Preferences/Groups/Groups.xml", "Machine/Registry.pol", "Machine/Scripts", "Machine/Scripts/Startup",
                "Machine/Scripts/Startup/-l.txt", "Machine/Scripts/Startup/a b.txt", "Machine/Scripts/Startup/zażółć  x", "User",
            ],
            entries.Keys.Order(StringComparer.Ordinal));
        Assert.True(entries["Machine/Preferences"].IsDirectory);
        Assert.False(entries["GPT.INI"].IsDirectory);
        Assert.Equal(22, entries["GPT.INI"].Size);
        Assert.Equal(0, entries["Machine"].Size);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 18, 10, 12, TimeSpan.Zero), entries["Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf"].ModifiedAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 8, 5, 9, TimeSpan.Zero), entries["Machine/Preferences/Groups/Groups.xml"].ModifiedAt);
        Assert.Equal(12, entries["Machine/Scripts/Startup/zażółć  x"].Size);

        // The planner accepts all of them (a name starting with '-' is refused by the reader, not the inventory).
        var plan = GpoReadPlanner.Plan(result.Listing);
        Assert.Empty(plan.Problems);
        Assert.Contains(plan.Read, f => f.Path == "Machine/Registry.pol");
    }

    [Fact]
    public void Samba419_DefaultDomainPolicy_UpperCaseFoldersAndNormalAttribute()
    {
        var result = SmbClientListingParser.Parse(Fixture("smbclient-4.19-ls.txt"), Folder(DefaultDomainPolicy));

        Assert.Equal(["GPT.INI", "MACHINE", "USER"], result.Listing.Entries.Select(e => e.RelativePath).Order(StringComparer.Ordinal));
        Assert.Equal(20, result.Listing.Entries.Single(e => e.RelativePath == "GPT.INI").Size);
        Assert.Equal(0, result.Listing.RejectedEntries);
    }

    [Fact]
    public void ErrorLines_AreReturnedAsCodesOnly()
    {
        var output = "cd \\contoso.test\\Policies\\{36F97CB4-4935-42E4-9950-A93B706A5C11}\\: NT_STATUS_OBJECT_NAME_NOT_FOUND\n";

        var result = SmbClientListingParser.Parse(output, Folder(Explore));

        Assert.Equal(["NT_STATUS_OBJECT_NAME_NOT_FOUND"], result.Errors);
        Assert.Empty(result.Listing.Entries);
    }

    [Fact]
    public void HostileNames_AreEntriesOrRejected_NeverPathsOutsideTheFolder()
    {
        var root = @"\contoso.test\Policies\{36F97CB4-4935-42E4-9950-A93B706A5C11}";
        var output = string.Join('\n',
            "  .                                   D        0  Tue Oct 20 18:01:15 2026",
            // A name that looks like an attribute, a size and a date: the real tail ends the line.
            "  evil  A       22  Tue Oct 20 18:01:15 2026   A       10  Tue Oct 20 18:01:16 2026",
            // A name carrying an NT_STATUS code is an entry, not an error.
            "  NT_STATUS_ACCESS_DENIED.txt        A        5  Tue Oct 20 18:01:15 2026",
            "  Machine                             D        0  Tue Oct 20 18:01:21 2026",
            // A line break in a name forges a line: rejected (no attributes/size/date).
            "  first-half-of-a-name",
            // Separators inside a name: rejected.
            @"  ..\..\x                             A        5  Tue Oct 20 18:01:15 2026",
            "  bad date                            A        5  Mon Oct 20 18:01:15 2026",
            "",
            // A header outside the GPO folder and one for a folder never listed: their entries are rejected.
            @"\contoso.test\Policies\{6AC1786C-016F-11D2-945F-00C04FB984F9}\Machine",
            "  stolen.txt                          A        5  Tue Oct 20 18:01:15 2026",
            root + @"\Secret",
            "  hidden.txt                          A        5  Tue Oct 20 18:01:15 2026",
            "",
            root + @"\Machine",
            "  Registry.pol                        A      126  Tue Oct 20 18:12:40 2026",
            "",
            "\t\t264212084 blocks of size 1024. 14151244 blocks available");

        var result = SmbClientListingParser.Parse(output, Folder(Explore));

        Assert.Empty(result.Errors);
        Assert.Equal(
            ["Machine", "Machine/Registry.pol", "NT_STATUS_ACCESS_DENIED.txt", "evil  A       22  Tue Oct 20 18:01:15 2026"],
            result.Listing.Entries.Select(e => e.RelativePath).Order(StringComparer.Ordinal));
        Assert.Equal(10, result.Listing.Entries.Single(e => e.RelativePath.StartsWith("evil", StringComparison.Ordinal)).Size);
        Assert.Equal(7, result.Listing.RejectedEntries);
        Assert.Contains(GpoReadProblems.InvalidPath, GpoReadPlanner.Plan(result.Listing).Problems);
    }
}
