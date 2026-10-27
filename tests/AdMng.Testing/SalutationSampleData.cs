using AdMng.Core.ActiveDirectory;

namespace AdMng.Testing;

/// <summary>The accounts of <see cref="FakeDirectoryBuilder.AddSalutationSamples"/> (U1d), by the example they show.</summary>
public sealed record SalutationSample(
    string Ou,
    string Professor,
    string Doctor,
    string Engineer,
    string Associate,
    string Priest,
    string Kuba,
    string SaszaKaminska,
    string SaszaKim,
    string Nikola,
    string Emily)
{
    /// <summary>Every account, in the order of the design note's grammar examples.</summary>
    public IReadOnlyList<string> All => [Professor, Doctor, Engineer, Associate, Priest, Kuba, SaszaKaminska, SaszaKim, Nikola, Emily];
}

/// <summary>Accounts for title-aware salutations (U1d), in their own OU so <see cref="AddSampleCompany"/> counts stay.</summary>
public sealed partial class FakeDirectoryBuilder
{
    /// <summary>
    /// OU "Zwroty grzecznościowe" under the company with the grammar examples of the design note (section 20): a professor
    /// (personalTitle "prof. dr hab."), a doctor whose title is only in the display name, an engineer ("mgr inż."), a
    /// professor's suffix title ("dr hab. Ewa Zielińska, prof. UW"), a priest ("ks. dr"), Kuba (masculine exception),
    /// Sasza Kamińska (unisex name, feminine surname), Sasza Kim (unknown gender – Sasza Kim's manager is the professor),
    /// Nikola Lewandowski with personalTitle "Pani" (the honorific wins over the surname) and Emily Brown (English).
    /// </summary>
    public SalutationSample AddSalutationSamples(SampleCompany company)
    {
        ArgumentNullException.ThrowIfNull(company);
        var ou = AddOu("Zwroty grzecznościowe", company.CompanyOu);
        string Add(string givenName, string surname, string mailbox, params (string Attribute, object? Value)[] extra) =>
            AddUser(givenName, surname, ou, Person("Uczelnia", "Pracownik naukowy", "Kraków", mailbox, extra));

        var professor = Add("Stanisław", "Kowalczyk", "stanislaw.kowalczyk", ("personalTitle", "prof. dr hab."));
        var doctor = Add("Anna", "Nowak", "anna.nowak", (Attr.DisplayName, "dr Anna Nowak"));
        var engineer = Add("Piotr", "Wiśniewski", "piotr.wisniewski", ("personalTitle", "mgr inż."));
        var associate = Add("Ewa", "Zielińska", "ewa.zielinska", (Attr.DisplayName, "dr hab. Ewa Zielińska, prof. UW"));
        var priest = Add("Tomasz", "Wójcik", "tomasz.wojcik", ("personalTitle", "ks. dr"));
        var kuba = Add("Kuba", "Mazur", "kuba.mazur");
        var kaminska = Add("Sasza", "Kamińska", "sasza.kaminska");
        var kim = Add("Sasza", "Kim", "sasza.kim", (Attr.Manager, professor));
        var nikola = Add("Nikola", "Lewandowski", "nikola.lewandowski", ("personalTitle", "Pani"));
        var emily = Add("Emily", "Brown", "emily.brown", ("preferredLanguage", "en-GB"));
        return new SalutationSample(ou, professor, doctor, engineer, associate, priest, kuba, kaminska, kim, nikola, emily);
    }
}
