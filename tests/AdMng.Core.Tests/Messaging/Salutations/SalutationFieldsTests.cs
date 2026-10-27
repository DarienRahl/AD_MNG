using System.Net;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Salutations;
using AdMng.Core.Model;
using static AdMng.Core.Tests.Messaging.MessageTestData;

namespace AdMng.Core.Tests.Messaging.Salutations;

/// <summary>The salutation fields of the template engine (U1d, design note section 14) in Polish and English.</summary>
public sealed class SalutationFieldsTests
{
    private const string ProfessorDn = "CN=Jan Kowalski,OU=Wykładowcy,DC=contoso,DC=test";
    private const string DoctorDn = "CN=Anna Nowak,OU=Wykładowcy,DC=contoso,DC=test";
    private const string SaszaDn = "CN=Sasza Kim,OU=Wykładowcy,DC=contoso,DC=test";

    private readonly UserMessageFields _fields = new();

    private static DirectoryObject Professor() => User(ProfessorDn,
        [(Attr.GivenName, "Jan"), (Attr.Sn, "Kowalski"), (Attr.DisplayName, "Jan Kowalski"), ("personalTitle", "prof. dr hab.")]);

    private static DirectoryObject Doctor() => User(DoctorDn,
        [(Attr.GivenName, "Anna"), (Attr.Sn, "Nowak"), (Attr.DisplayName, "dr Anna Nowak")]);

    private static DirectoryObject Sasza() => User(SaszaDn,
        [(Attr.GivenName, "Sasza"), (Attr.Sn, "Kim"), (Attr.DisplayName, "Sasza Kim")]);

    private static UserMessageFieldData Data(
        DirectoryObject account,
        MessageLanguage language = MessageLanguage.Polish,
        DirectoryObject? manager = null,
        SalutationOptions? options = null,
        List<MessageIssue>? issues = null) => new()
    {
        Account = account,
        Status = AccountStatus.Evaluate(account, Now, Warsaw),
        Manager = manager,
        Language = language,
        TimeZone = Warsaw,
        Domain = "contoso.test",
        Salutation = new MessageSalutations(account, manager, options ?? SalutationOptions.Default, issues: issues, language: language),
    };

    [Fact]
    public void GrammarExample_RendersThePolishLetter()
    {
        const string template = "{$salutation},\nuprzejmie informujemy, że hasło do {$honorific:dopełniacz} konta wygaśnie. "
            + "Prosimy, aby {$honorific} {$byGender:zmienił/zmieniła} je przed tym terminem.";

        Assert.Equal(
            "Szanowny Panie Profesorze,\nuprzejmie informujemy, że hasło do Pana konta wygaśnie. Prosimy, aby Pan zmienił je przed tym terminem.",
            _fields.Render(template, Data(Professor())));
        Assert.Equal(
            "Szanowna Pani Doktor,\nuprzejmie informujemy, że hasło do Pani konta wygaśnie. Prosimy, aby Pani zmieniła je przed tym terminem.",
            _fields.Render(template, Data(Doctor())));
        Assert.Equal(
            "Szanowna Pani / Szanowny Panie,\nuprzejmie informujemy, że hasło do Pana/Pani konta wygaśnie. Prosimy, aby Pan/Pani zmienił/zmieniła je przed tym terminem.",
            _fields.Render(template, Data(Sasza())));
    }

    [Theory]
    [InlineData("{$salutation}", "Szanowny Panie Profesorze", "Dear Professor Kowalski")]
    [InlineData("{$salutation:noTitle}", "Szanowny Panie", "Dear Mr Kowalski")]
    [InlineData("{$salutation:vocative}", "Panie Profesorze", "Professor Kowalski")]
    [InlineData("{$salutation:vocativeNoTitle}", "Panie", "Mr Kowalski")]
    [InlineData("{$salutation:NOTITLE}", "Szanowny Panie", "Dear Mr Kowalski")]
    [InlineData("{$title}", "prof. dr hab.", "prof. dr hab.")]
    [InlineData("{$title:highest}", "prof.", "prof.")]
    [InlineData("{$title|capitalize}", "Prof. dr hab.", "Prof. dr hab.")]
    [InlineData("{$titledName}", "prof. dr hab. Jan Kowalski", "prof. dr hab. Jan Kowalski")]
    [InlineData("{$honorific}", "Pan", "Mr")]
    [InlineData("{$honorific:dopełniacz}", "Pana", "Mr")]
    [InlineData("{$honorific:celownik}", "Panu", "Mr")]
    [InlineData("{$honorific:biernik}", "Pana", "Mr")]
    [InlineData("{$honorific:narzędnik}", "Panem", "Mr")]
    [InlineData("{$honorific:narzednik}", "Panem", "Mr")]
    [InlineData("{$honorific:miejscownik}", "Panu", "Mr")]
    [InlineData("{$honorific:wołacz}", "Panie", "Mr")]
    [InlineData("{$honorific:acc}", "Pana", "Mr")]
    [InlineData("{$byGender:zmienił/zmieniła}", "zmienił", "zmienił")]
    [InlineData("{$byGender:his/her/their}", "his", "his")]
    public void EveryFieldAndArgument_InBothLanguages(string template, string polish, string english)
    {
        Assert.Equal(polish, _fields.Render(template, Data(Professor())));
        Assert.Equal(english, _fields.Render(template, Data(Professor(), MessageLanguage.English)));
    }

    [Fact]
    public void ManagerTwins_EvaluateForTheManager_AndAreEmptyWithoutOne()
    {
        var data = Data(Sasza(), manager: Doctor());

        Assert.Equal("Szanowna Pani Doktor", _fields.Render("{$managerSalutation}", data));
        Assert.Equal("dr", _fields.Render("{$managerTitle}", data));
        Assert.Equal("dr Anna Nowak", _fields.Render("{$managerTitledName}", data));
        Assert.Equal("Panią", _fields.Render("{$managerHonorific:biernik}", data));
        Assert.Equal("zmieniła", _fields.Render("{$managerByGender:zmienił/zmieniła}", data));
        Assert.Equal("Dear Dr Nowak", _fields.Render("{$managerSalutation}", Data(Sasza(), MessageLanguage.English, Doctor())));

        var empty = new List<string>();
        Assert.Equal(string.Empty, _fields.Render("{$managerSalutation}", Data(Sasza()), emptyField: empty.Add));
        Assert.Equal(["{$managerSalutation}"], empty);
    }

    [Fact]
    public void EmptyTitle_IsNotAnEmptyField_ButAnEmptySalutationOfAMissingManagerIs()
    {
        var empty = new List<string>();

        var text = _fields.Render("{$title}|{$managerTitle}|{$byGender:a/b/}|{givenName}", Data(Sasza()), emptyField: empty.Add);

        Assert.Equal("|||Sasza", text);
        Assert.Empty(empty);
    }

    [Fact]
    public void Values_AreEncoded_InHtml()
    {
        var account = User(SaszaDn, [(Attr.GivenName, "Sasza"), (Attr.Sn, "<Kim>"), (Attr.DisplayName, "Sasza <Kim>")]);

        Assert.Equal("Dear Sasza &lt;Kim&gt;", _fields.Render("{$salutation}", Data(account, MessageLanguage.English), WebUtility.HtmlEncode));
    }

    [Theory]
    [InlineData("{$salutation:formal}", "Allowed: noTitle, vocative, vocativeNoTitle")]
    [InlineData("{$title:lowest}", "Allowed: highest")]
    [InlineData("{$honorific:przypadek}", "Allowed: nom, gen, dat, acc, ins, loc, voc, mianownik, dopełniacz")]
    [InlineData("{$titledName:x}", "does not take an argument")]
    [InlineData("{$byGender}", "requires an argument")]
    [InlineData("{$byGender:zmienił}", "masculine/feminine[/neutral]")]
    [InlineData("{$byGender:a/b/c/d}", "masculine/feminine[/neutral]")]
    public void InvalidArguments_AreTemplateErrors(string template, string message)
    {
        var parsed = _fields.Engine.Parse(template);

        Assert.False(parsed.IsValid);
        Assert.Contains(message, Assert.Single(parsed.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void ValidArguments_AreAccepted_DiacriticsInsensitively()
    {
        foreach (var template in new[] { "{$honorific:dopelniacz}", "{$honorific:Wołacz}", "{$salutation:vocativenotitle}", "{$title:HIGHEST}", "{$byGender:his/her/their}" })
        {
            Assert.True(_fields.Engine.Parse(template).IsValid, template);
        }
    }

    [Fact]
    public void Capitalize_UpperCasesTheFirstLetterOnly_InTheBatchEngineToo()
    {
        var engine = new AdMng.Core.Templates.TemplateEngine();
        var context = new AdMng.Core.Templates.TemplateContext { Attribute = a => a == "personalTitle" ? ["prof. dr hab."] : ["(łódź)"] };

        Assert.Equal("Prof. dr hab.", engine.Render("{personalTitle|capitalize}", context));
        Assert.Equal("(Łódź)", engine.Render("{l|capitalize}", context));
        Assert.False(engine.Parse("{l|capitalize:1}").IsValid);
    }

    [Fact]
    public void Findings_AreAddedOnlyForTheFieldsUsed()
    {
        var issues = new List<MessageIssue>();
        _fields.Render("{givenName}, {$title}", Data(Sasza(), issues: issues));
        Assert.Empty(issues);

        _fields.Render("{$salutation}", Data(Sasza(), issues: issues));
        Assert.Equal([new MessageIssue(SalutationIssueCodes.GenderUnknown, MessageIssueSeverity.Warning, "Sasza Kim")], issues);

        // An English titled salutation needs no gender, so no gender warning.
        issues.Clear();
        var titled = User(SaszaDn, [(Attr.GivenName, "Sasza"), (Attr.Sn, "Kim"), (Attr.DisplayName, "Sasza Kim"), ("personalTitle", "prof.")]);
        Assert.Equal("Dear Professor Kim", _fields.Render("{$salutation}", Data(titled, MessageLanguage.English, issues: issues)));
        Assert.Empty(issues);

        // The doctor's title came from the display name, the gender from her name.
        issues.Clear();
        _fields.Render("{$salutation} {$salutation:vocative}", Data(Doctor(), issues: issues));
        Assert.Equal(
            [
                new MessageIssue(SalutationIssueCodes.GenderGuessed, MessageIssueSeverity.Info, "Anna"),
                new MessageIssue(SalutationIssueCodes.TitleFromDisplayName, MessageIssueSeverity.Info, "dr"),
            ],
            issues);
    }

    [Fact]
    public void Tokens_AndExamples()
    {
        foreach (var example in UserMessageFields.Examples)
        {
            Assert.True(_fields.Engine.Parse(example).IsValid, example);
        }

        Assert.Contains("{$byGender:zmienił/zmieniła}", UserMessageFields.Examples);
        Assert.Contains("{$managerSalutation}", UserMessageFields.Examples);
        Assert.Equal(10, UserMessageFields.Tokens.Count(t => SalutationFields.IsSalutationField(t.Name)));
        Assert.False(UserMessageFields.Tokens.Single(t => t.Name == UserMessageFields.Title).ReportsEmpty);
        Assert.False(UserMessageFields.Tokens.Single(t => t.Name == UserMessageFields.ManagerTitle).ReportsEmpty);
        Assert.True(UserMessageFields.Tokens.Single(t => t.Name == UserMessageFields.Salutation).ReportsEmpty);
    }
}
