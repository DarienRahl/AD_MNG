using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Formatting;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Presentation.Comparison;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Tests.Connections;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Presentation.Tests.Comparison;

/// <summary>
/// contoso.test and fabrikam.test with the same sample company, except: in fabrikam Łukasz Wiśniewski is "Starszy
/// programista" in Wrocław, a member of GG_Sprzedaz instead of GG_IT, and the group of RID 513 is "Użytkownicy domeny"
/// (a Polish DC). The objectGUIDs of the two domains differ.
/// </summary>
internal sealed class ComparisonDomains
{
    public const string Fabrikam = "fabrikam.test";

    private ComparisonDomains(SampleDomain contoso, SampleDomain fabrikam, string fabrikamDomainUsers)
    {
        Contoso = contoso;
        FabrikamDomain = fabrikam;
        FabrikamDomainUsersDn = fabrikamDomainUsers;
    }

    public SampleDomain Contoso { get; }

    public SampleDomain FabrikamDomain { get; }

    public string FabrikamDomainUsersDn { get; }

    public static async Task<ComparisonDomains> CreateAsync(ISchemaCatalog? fabrikamSchema = null, Action<FakeDirectoryBuilder>? contosoExtra = null, Action<FakeDirectoryBuilder>? fabrikamExtra = null)
    {
        var contosoBuilder = new FakeDirectoryBuilder();
        var contosoCompany = contosoBuilder.AddSampleCompany();
        contosoExtra?.Invoke(contosoBuilder);

        var fabrikamBuilder = new FakeDirectoryBuilder(Fabrikam, schema: fabrikamSchema, guidNamespace: 2);
        var company = fabrikamBuilder.AddSampleCompany();
        var directory = fabrikamBuilder.Session;
        directory.SetValues(company.LukaszWisniewski, Attr.Title, "Starszy programista");
        directory.SetValues(company.LukaszWisniewski, Attr.City, "Wrocław");
        directory.SetValues(company.GroupIt, Attr.Member, company.JanKowalski);
        fabrikamBuilder.AddMembers(company.GroupSales, company.LukaszWisniewski);
        var domainUsers = await directory.MoveAsync(fabrikamBuilder.DomainUsersDn, null, "CN=Użytkownicy domeny", TestContext.Current.CancellationToken);
        directory.SetValues(domainUsers, Attr.SamAccountName, "Użytkownicy domeny");
        fabrikamExtra?.Invoke(fabrikamBuilder);

        contosoBuilder.Session.ClearOperations();
        directory.ClearOperations();
        return new ComparisonDomains(
            new SampleDomain(contosoBuilder.Session, contosoCompany),
            new SampleDomain(directory, company),
            domainUsers);
    }

    /// <summary>Two connection windows: A on contoso.test as jan.kowalski, B on fabrikam.test as anna.zielinska.</summary>
    public async Task<(ConnectionScopeHost Host, TestConnection A, TestConnection B)> OpenAsync(Action<IServiceCollection>? configure = null)
    {
        var host = new ConnectionScopeHost(configure, [Contoso.Session, FabrikamDomain.Session]);
        var a = host.CreateConnection();
        var b = host.CreateConnection();
        await a.ConnectAsync(user: "jan.kowalski");
        await b.ConnectAsync(Fabrikam, "anna.zielinska");
        return (host, a, b);
    }
}

/// <summary>Helpers of the comparison tests.</summary>
internal static class ComparisonTestSupport
{
    /// <summary>The comparison window of <paramref name="dn"/> in window <paramref name="connection"/>, through its factory.</summary>
    public static ObjectComparisonViewModel Compare(this TestConnection connection, string dn) =>
        connection.Get<IDialogViewModelFactory>().CreateObjectComparison(dn);

    /// <summary>Runs the comparison the window starts by itself and waits for the background reads.</summary>
    public static async Task<ObjectComparisonViewModel> RunAsync(this ObjectComparisonViewModel comparison)
    {
        await comparison.InitializeAsync();
        await comparison.Pending;
        return comparison;
    }

    public static AttributeComparisonRowViewModel Row(this ObjectComparisonViewModel comparison, string ldapName) =>
        Assert.Single(comparison.AttributeRows, r => string.Equals(r.LdapName, ldapName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A formatter that shows the syntax its OWN schema gives an attribute ("UnicodeString:Wrocław"): which side's formatter
/// and schema formatted a cell is visible in the text.
/// </summary>
internal sealed class SchemaTaggingFormatter(ISchemaCatalog schema) : IAttributeFormatter
{
    public string Format(string attribute, AttributeValues? values, CultureInfo culture) =>
        values is null ? string.Empty : string.Join("; ", values.Select(v => FormatValue(attribute, v, culture)));

    public string FormatValue(string attribute, object value, CultureInfo culture) =>
        $"{schema.FindAttribute(attribute)?.Syntax.ToString() ?? "?"}:{value as string ?? Convert.ToHexString((byte[])value)}";

    public bool TryParse(string attribute, string input, CultureInfo culture, out object? value, out string? error)
    {
        value = input;
        error = null;
        return true;
    }
}
