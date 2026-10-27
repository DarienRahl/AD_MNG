using AdMng.Core.ActiveDirectory;
using AdMng.Presentation.Services;
using AdMng.Testing;

namespace AdMng.App.UiTests.Infrastructure;

/// <summary>
/// fabrikam.test for the comparison of objects between two connection windows: the sample company, but Łukasz
/// Wiśniewski is "Starszy programista" in Wrocław and in GG_Sprzedaz instead of GG_IT, and the group of RID 513 is
/// "Użytkownicy domeny" (a Polish DC). Its objectGUIDs differ from contoso.test's.
/// </summary>
internal static class ComparisonFixture
{
    public const string Fabrikam = "fabrikam.test";

    public static async Task<SampleCompany> AddFabrikamAsync(UiTestContext context)
    {
        var builder = new FakeDirectoryBuilder(Fabrikam, guidNamespace: 2);
        var company = builder.AddSampleCompany();
        var directory = builder.Session;
        directory.SetValues(company.LukaszWisniewski, Attr.Title, "Starszy programista");
        directory.SetValues(company.LukaszWisniewski, Attr.City, "Wrocław");
        directory.SetValues(company.GroupIt, Attr.Member, company.JanKowalski);
        builder.AddMembers(company.GroupSales, company.LukaszWisniewski);
        var domainUsers = await directory.MoveAsync(builder.DomainUsersDn, null, "CN=Użytkownicy domeny", CancellationToken.None);
        directory.SetValues(domainUsers, Attr.SamAccountName, "Użytkownicy domeny");
        directory.ClearOperations();
        context.Connector.AddDomain(directory);
        context.Connector.NonOwning = true;
        return company;
    }
}

/// <summary>A save dialog that answers with a prepared path (null = cancelled) and records the suggested file name.</summary>
internal sealed class PreparedSaveDialog : IFileDialogService
{
    public string? NextSavePath { get; set; }

    public string? LastSuggestedName { get; private set; }

    public Task<string?> OpenFileAsync(string title, IReadOnlyList<FileFilter> filters) => Task.FromResult<string?>(null);

    public Task<string?> SaveFileAsync(string title, string defaultFileName, IReadOnlyList<FileFilter> filters)
    {
        LastSuggestedName = defaultFileName;
        return Task.FromResult(NextSavePath);
    }
}
