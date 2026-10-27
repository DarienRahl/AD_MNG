using AdMng.Core.ActiveDirectory;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Salutations;
using AdMng.Core.Model;
using static AdMng.Core.Tests.Messaging.MessageTestData;

namespace AdMng.Core.Tests.Messaging.Salutations;

/// <summary>Salutations in the plan (U1d): attributes read, derived values, findings, overrides and manager mode.</summary>
public sealed class UserMessageSalutationPlannerTests
{
    private const string ProfessorDn = "CN=Stanisław Kowalczyk,OU=Wykładowcy,DC=contoso,DC=test";
    private const string SaszaDn = "CN=Sasza Kim,OU=Wykładowcy,DC=contoso,DC=test";
    private const string EwaDn = "CN=Ewa Zielińska,OU=Wykładowcy,DC=contoso,DC=test";

    private readonly UserMessagePlanner _planner = new(new UserMessageFields());

    private static MessageTemplate Template(string polishBody, string englishBody = "", TemplateSalutationOptions? options = null) => new()
    {
        Id = "salutations",
        Name = "Zwroty",
        Polish = new MessageTemplateText { Subject = "Hasło – {$salutation:vocativeNoTitle}", Body = polishBody },
        English = new MessageTemplateText { Subject = englishBody.Length == 0 ? string.Empty : "Password", Body = englishBody },
        Salutation = options,
    };

    private static MessageTemplate BodyOnly(string polishBody) => new()
    {
        Id = "plain",
        Name = "Bez zwrotów w temacie",
        Polish = new MessageTemplateText { Subject = "Hasło", Body = polishBody },
    };

    private static DirectoryObject Professor(params (string Attribute, object Value)[] extra) => User(ProfessorDn,
    [
        (Attr.GivenName, "Stanisław"), (Attr.Sn, "Kowalczyk"), (Attr.DisplayName, "Stanisław Kowalczyk"), ("personalTitle", "prof. dr hab."),
        (Attr.Mail, "stanislaw.kowalczyk@contoso.test"), (Attr.SamAccountName, "stanislaw.kowalczyk"), .. extra,
    ]);

    private static DirectoryObject Sasza(params (string Attribute, object Value)[] extra) => User(SaszaDn,
    [
        (Attr.GivenName, "Sasza"), (Attr.Sn, "Kim"), (Attr.DisplayName, "Sasza Kim"), (Attr.Mail, "sasza.kim@contoso.test"),
        (Attr.Manager, ProfessorDn), .. extra,
    ]);

    private static DirectoryObject Ewa(params (string Attribute, object Value)[] extra) => User(EwaDn,
    [
        (Attr.GivenName, "Ewa"), (Attr.Sn, "Zielińska"), (Attr.DisplayName, "dr hab. Ewa Zielińska, prof. UW"), (Attr.Mail, "ewa.zielinska@contoso.test"),
        ("preferredLanguage", "en-US"), .. extra,
    ]);

    private UserMessagePlan Plan(MessageTemplate template, IEnumerable<UserMessageAccount> accounts, Func<UserMessagePlanRequest, UserMessagePlanRequest>? change = null, SalutationOptions? options = null)
    {
        var request = Request(template, accounts, options);
        return _planner.Plan(change?.Invoke(request) ?? request);
    }

    private static UserMessagePlanRequest Request(MessageTemplate template, IEnumerable<UserMessageAccount> accounts, SalutationOptions? options = null) => new()
    {
        Template = template,
        Sender = Sender() with { Salutations = options ?? SalutationOptions.Default },
        Accounts = [.. accounts],
        Now = Now,
        Domain = "contoso.test",
    };

    [Fact]
    public void Attributes_AreReadOnlyWhenTheTemplateUsesSalutationFields()
    {
        var options = SalutationOptions.Default with { GenderAttribute = "extensionAttribute5" };
        var plain = BodyOnly("Dzień dobry {givenName},");
        var account = BodyOnly("{$salutation},");
        var manager = BodyOnly("{$managerSalutation},");

        Assert.DoesNotContain("personalTitle", _planner.AccountAttributesFor(plain, options));
        Assert.Contains("personalTitle", _planner.AccountAttributesFor(account, options));
        Assert.Contains("extensionAttribute5", _planner.AccountAttributesFor(account, options));
        Assert.DoesNotContain("personalTitle", _planner.ManagerAttributesFor(account, options));
        Assert.Contains("personalTitle", _planner.ManagerAttributesFor(manager, options));
        Assert.Contains("extensionAttribute5", _planner.ManagerAttributesFor(manager, options));
        Assert.DoesNotContain("extensionAttribute5", _planner.AccountAttributesFor(manager, options));

        // Unusable attributes are never read: the job title, secrets, invalid names.
        foreach (var bad in new[] { "title", "unicodePwd", "userPassword", "a b", "1abc" })
        {
            Assert.DoesNotContain(bad, _planner.AccountAttributesFor(account, SalutationOptions.Default with { TitleAttribute = bad, GenderAttribute = bad }));
        }
    }

    [Fact]
    public void Plan_RendersSalutations_InTheRecipientsLanguage_WithDerivedValues()
    {
        var template = Template("{$salutation},\nprosimy o zmianę hasła.", "{$salutation},\nplease change your password.");

        var plan = Plan(template, [new(ProfessorDn, Professor()), new(EwaDn, Ewa())]);

        var professor = plan.Messages[0];
        Assert.Equal(PlannedMessageStatus.Ready, professor.Status);
        Assert.Equal("Hasło – Panie", professor.Message!.Subject);
        Assert.StartsWith("Szanowny Panie Profesorze,\nprosimy o zmianę hasła.", professor.Message.TextBody, StringComparison.Ordinal);
        Assert.Equal(
            [
                new DerivedFieldValue("title", "prof. dr hab.", DerivedValueSource.TitleAttribute, false),
                new DerivedFieldValue("gender", "Masculine", DerivedValueSource.NameRule, true, "Stanisław") { Issue = SalutationIssueCodes.GenderGuessed },
                new DerivedFieldValue("salutation", "Szanowny Panie Profesorze", DerivedValueSource.NameRule, true) { Issue = SalutationIssueCodes.GenderGuessed },
            ],
            professor.Derived);
        Assert.Equal([new MessageIssue(SalutationIssueCodes.GenderGuessed, MessageIssueSeverity.Info, "Stanisław")], professor.Issues);

        // Ewa: English, title from the display name, "Professor" needs no gender.
        var ewa = plan.Messages[1];
        Assert.Equal(MessageLanguage.English, ewa.Language);
        Assert.StartsWith("Dear Professor Zielińska,\nplease change your password.", ewa.Message!.TextBody, StringComparison.Ordinal);
        Assert.Contains(ewa.Derived, d => d is { Field: "salutation", Value: "Dear Professor Zielińska", Source: DerivedValueSource.DisplayName, IsGuess: true });
        Assert.Contains(new MessageIssue(SalutationIssueCodes.TitleFromDisplayName, MessageIssueSeverity.Info, "dr hab., prof. UW"), ewa.Issues);
        Assert.DoesNotContain(ewa.Issues, i => i.Code == SalutationIssueCodes.GenderGuessed);
    }

    [Fact]
    public void UnknownGender_IsAWarning_AndAnOverrideReplansThatMessageOnly()
    {
        var template = Template("{$salutation},\nprosimy, aby {$honorific} {$byGender:zmienił/zmieniła} hasło.");
        var request = Request(template, [new(ProfessorDn, Professor()), new(SaszaDn, Sasza())]);

        var plan = _planner.Plan(request);

        var sasza = plan.Messages[1];
        Assert.Equal(PlannedMessageStatus.Warning, sasza.Status);
        Assert.Contains(new MessageIssue(SalutationIssueCodes.GenderUnknown, MessageIssueSeverity.Warning, "Sasza Kim"), sasza.Issues);
        Assert.StartsWith("Szanowna Pani / Szanowny Panie,\nprosimy, aby Pan/Pani zmienił/zmieniła hasło.", sasza.Message!.TextBody, StringComparison.Ordinal);
        Assert.Contains(sasza.Derived, d => d is { Field: "salutation", Source: DerivedValueSource.Fallback, Issue: SalutationIssueCodes.GenderUnknown });

        var replanned = _planner.Replan(request with { GenderOverrides = new Dictionary<string, Gender> { [SaszaDn.ToUpperInvariant()] = Gender.Feminine } }, 1, sasza);

        Assert.Equal(1, replanned.Index);
        Assert.Equal(PlannedMessageStatus.Ready, replanned.Status);
        Assert.Empty(replanned.Issues);
        Assert.StartsWith("Szanowna Pani,\nprosimy, aby Pani zmieniła hasło.", replanned.Message!.TextBody, StringComparison.Ordinal);
        Assert.Contains(replanned.Derived, d => d is { Field: "gender", Value: "Feminine", Source: DerivedValueSource.Override, IsGuess: false });

        // A neutral override is the operator's choice: no warning.
        var neutral = _planner.Replan(request with { GenderOverrides = new Dictionary<string, Gender> { [SaszaDn] = Gender.Unknown } }, 1);
        Assert.Equal(PlannedMessageStatus.Ready, neutral.Status);
        Assert.StartsWith("Szanowna Pani / Szanowny Panie,", neutral.Message!.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void DerivedSalutation_IsTheVariantTheTemplateRendered_MarkedByTheWorstOne()
    {
        // English, title "prof.", unknown gender: "noTitle" renders the neutral text – the column must not show "Dear Professor Kim".
        var english = Plan(Template("{$salutation},", "{$salutation:noTitle},"), [new(SaszaDn, Sasza(("preferredLanguage", "en-US"), ("personalTitle", "prof.")))]).Messages[0];
        Assert.StartsWith("Dear Sasza Kim,", english.Message!.TextBody, StringComparison.Ordinal);
        Assert.Contains(english.Derived, d => d is { Field: "salutation", Value: "Dear Sasza Kim", Source: DerivedValueSource.Fallback, Issue: SalutationIssueCodes.GenderUnknown });

        // Only the vocative: the column shows what the mail says.
        var vocative = Plan(BodyOnly("{$salutation:vocative},"), [new(ProfessorDn, Professor())]).Messages[0];
        Assert.StartsWith("Panie Profesorze,", vocative.Message!.TextBody, StringComparison.Ordinal);
        Assert.Contains(vocative.Derived, d => d is { Field: "salutation", Value: "Panie Profesorze" });

        // Several variants: the fullest one is shown, a fallback of another one marks it.
        var mixed = Plan(Template("{$salutation},", "{$salutation:noTitle} / {$salutation}"), [new(SaszaDn, Sasza(("personalTitle", "prof."), ("preferredLanguage", "en")))]).Messages[0];
        Assert.StartsWith("Dear Sasza Kim / Dear Professor Kim", mixed.Message!.TextBody, StringComparison.Ordinal);
        var salutation = Assert.Single(mixed.Derived, d => d.Field == "salutation");
        Assert.Equal(("Dear Professor Kim", DerivedValueSource.Fallback, SalutationIssueCodes.GenderUnknown), (salutation.Value, salutation.Source, salutation.Issue));
    }

    [Fact]
    public void DerivedTitleAndGender_AreMarkedOnlyWhenAFieldNeededThem()
    {
        // Only {$titledName}: nothing depends on the gender, so Sasza Kim's unknown gender is neither a guess nor a finding.
        var titled = Plan(BodyOnly("{$titledName},"), [new(SaszaDn, Sasza())]).Messages[0];
        Assert.Equal(PlannedMessageStatus.Ready, titled.Status);
        Assert.Empty(titled.Issues);
        Assert.Equal(["title", "gender"], titled.Derived.Select(d => d.Field));
        Assert.All(titled.Derived, d => Assert.False(d.IsGuess || d.IsFallback || d.Issue is not null, d.Field));

        // Only {$byGender}: the display-name title was not looked at – only the gender is a guess.
        var ewa = Plan(BodyOnly("{$byGender:zmienił/zmieniła}"), [new(EwaDn, Ewa())]).Messages[0];
        Assert.StartsWith("zmieniła", ewa.Message!.TextBody, StringComparison.Ordinal);
        Assert.Contains(ewa.Derived, d => d is { Field: "title", IsGuess: false, Source: DerivedValueSource.DisplayName });
        Assert.Contains(ewa.Derived, d => d is { Field: "gender", IsGuess: true, Issue: SalutationIssueCodes.GenderGuessed });
        Assert.DoesNotContain(ewa.Issues, i => i.Code == SalutationIssueCodes.TitleFromDisplayName);

        // Only {$honorific}: the neutral "Pan/Pani" marks the gender as a fallback.
        var honorific = Plan(BodyOnly("{$honorific}"), [new(SaszaDn, Sasza())]).Messages[0];
        Assert.Contains(honorific.Derived, d => d is { Field: "gender", Source: DerivedValueSource.Fallback, Issue: SalutationIssueCodes.GenderUnknown });
    }

    [Fact]
    public void EnglishMessages_DoNotGuessByThePolishNameRule()
    {
        const string JohnDn = "CN=John Smith,OU=Wykładowcy,DC=contoso,DC=test";
        const string JenniferDn = "CN=Jennifer Smith,OU=Wykładowcy,DC=contoso,DC=test";
        DirectoryObject Account(string dn, string givenName, string language) => User(dn,
            [(Attr.GivenName, givenName), (Attr.Sn, "Smith"), (Attr.DisplayName, $"{givenName} Smith"), (Attr.Mail, $"{givenName.ToLowerInvariant()}@contoso.test"), ("preferredLanguage", language)]);
        var template = Template("{$salutation},", "{$salutation},");

        var plan = Plan(template, [new(JohnDn, Account(JohnDn, "John", "en-GB")), new(JenniferDn, Account(JenniferDn, "Jennifer", "en-GB"))]);

        // John: the rule does not apply to English recipients – neutral and a warning, never a wrong "Mr"/"Ms".
        var john = plan.Messages[0];
        Assert.StartsWith("Dear John Smith,", john.Message!.TextBody, StringComparison.Ordinal);
        Assert.Contains(new MessageIssue(SalutationIssueCodes.GenderUnknown, MessageIssueSeverity.Warning, "John Smith"), john.Issues);

        // Jennifer is on the list: "Ms", not "Mr" by the rule.
        Assert.StartsWith("Dear Ms Smith,", plan.Messages[1].Message!.TextBody, StringComparison.Ordinal);

        // The same John in a Polish message: the Polish rule applies.
        var polish = Plan(template, [new(JohnDn, Account(JohnDn, "John", "pl-PL"))]).Messages[0];
        Assert.StartsWith("Szanowny Panie,", polish.Message!.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void EnglishMessages_GuessPolishNamesByTheRule()
    {
        const string JanDn = "CN=Jan Nowak,OU=Wykładowcy,DC=contoso,DC=test";
        var jan = User(JanDn,
            [(Attr.GivenName, "Jan"), (Attr.Sn, "Nowak"), (Attr.DisplayName, "Jan Nowak"), (Attr.Mail, "jan.nowak@contoso.test"), ("preferredLanguage", "en-GB")]);

        var message = Plan(Template("{$salutation},", "{$salutation},"), [new(JanDn, jan)]).Messages[0];

        Assert.StartsWith("Dear Mr Nowak,", message.Message!.TextBody, StringComparison.Ordinal);
        Assert.Contains(new MessageIssue(SalutationIssueCodes.GenderGuessed, MessageIssueSeverity.Info, "Jan"), message.Issues);
        Assert.DoesNotContain(message.Issues, i => i.Code == SalutationIssueCodes.GenderUnknown);
    }

    [Fact]
    public void Findings_NameTheAccount_EvenWithoutAnyName()
    {
        const string ScannerDn = "CN=Skaner Sekretariat,OU=Urządzenia,DC=contoso,DC=test";
        const string KioskDn = "CN=Kiosk Łódź,OU=Urządzenia,DC=contoso,DC=test";
        var scanner = User(ScannerDn, [(Attr.SamAccountName, "skaner.sekretariat"), (Attr.Mail, "skaner@contoso.test")]);
        var kiosk = User(KioskDn, [(Attr.Mail, "kiosk@contoso.test")]);

        var plan = Plan(BodyOnly("{$salutation},"), [new(ScannerDn, scanner), new(KioskDn, kiosk)]);

        Assert.Contains(new MessageIssue(SalutationIssueCodes.GenderUnknown, MessageIssueSeverity.Warning, "skaner.sekretariat"), plan.Messages[0].Issues);
        Assert.Contains(new MessageIssue(SalutationIssueCodes.GenderUnknown, MessageIssueSeverity.Warning, "Kiosk Łódź"), plan.Messages[1].Issues);
        Assert.DoesNotContain(plan.Messages.SelectMany(m => m.Issues), i => string.IsNullOrEmpty(i.Argument));
    }

    [Fact]
    public void Replan_KeepsADuplicateAddressWarning()
    {
        var template = Template("{$salutation},");
        var request = Request(template,
        [
            new(SaszaDn, Sasza()),
            new(ProfessorDn, Professor((Attr.Mail, "sasza.kim@contoso.test"))),
        ]);
        var plan = _planner.Plan(request);
        Assert.Contains(plan.Messages[0].Issues, i => i.Code == MessageIssueCodes.DuplicateAddress);

        var replanned = _planner.Replan(request with { GenderOverrides = new Dictionary<string, Gender> { [SaszaDn] = Gender.Masculine } }, 0, plan.Messages[0]);

        Assert.Contains(replanned.Issues, i => i.Code == MessageIssueCodes.DuplicateAddress);
        Assert.Equal(PlannedMessageStatus.Warning, replanned.Status);
        Assert.Throws<ArgumentOutOfRangeException>(() => _planner.Replan(request, 2));
    }

    [Fact]
    public void ManagerMode_UsesTheManagersSalutation()
    {
        var template = Template("{$managerSalutation},\n{$titledName} {$byGender:nie zmienił/nie zmieniła/nie zmienił(a)} hasła.") with
        {
            Recipients = MessageRecipientMode.Manager,
        };

        var plan = Plan(template, [new(SaszaDn, Sasza(), Professor())]);

        var message = Assert.Single(plan.Messages);
        Assert.Equal("stanislaw.kowalczyk@contoso.test", message.Recipient);
        Assert.StartsWith("Szanowny Panie Profesorze,\nSasza Kim nie zmienił(a) hasła.", message.Message!.TextBody, StringComparison.Ordinal);
        Assert.Equal(["title", "gender", "salutation", "managerTitle", "managerGender", "managerSalutation"], message.Derived.Select(d => d.Field));
        Assert.Contains(new MessageIssue(SalutationIssueCodes.GenderGuessed, MessageIssueSeverity.Info, "Stanisław"), message.Issues);
        Assert.Contains(new MessageIssue(SalutationIssueCodes.GenderUnknown, MessageIssueSeverity.Warning, "Sasza Kim"), message.Issues);
    }

    [Fact]
    public void ManagerOverride_ChangesTheManagersFields_NotTheAccounts()
    {
        // The professor's manager is Sasza Kim (unknown gender); the message goes to the manager.
        var template = BodyOnly("{$managerSalutation},\n{$byGender:zmienił/zmieniła} hasło.") with { Recipients = MessageRecipientMode.Manager };
        var request = Request(template, [new(ProfessorDn, Professor((Attr.Manager, SaszaDn)), Sasza())]);
        var planned = _planner.Plan(request).Messages[0];
        Assert.StartsWith("Szanowna Pani / Szanowny Panie,\nzmienił hasło.", planned.Message!.TextBody, StringComparison.Ordinal);

        var manager = _planner.Replan(request with { ManagerGenderOverrides = new Dictionary<string, Gender> { [ProfessorDn] = Gender.Feminine } }, 0);
        Assert.StartsWith("Szanowna Pani,\nzmienił hasło.", manager.Message!.TextBody, StringComparison.Ordinal);
        Assert.Contains(manager.Derived, d => d is { Field: "managerGender", Value: "Feminine", Source: DerivedValueSource.Override });
        Assert.Contains(manager.Derived, d => d is { Field: "gender", Value: "Masculine", Source: DerivedValueSource.NameRule });
        Assert.DoesNotContain(manager.Issues, i => i.Code == SalutationIssueCodes.GenderUnknown);

        // The account's override changes the account only.
        var account = _planner.Replan(request with { GenderOverrides = new Dictionary<string, Gender> { [ProfessorDn] = Gender.Feminine } }, 0);
        Assert.StartsWith("Szanowna Pani / Szanowny Panie,\nzmieniła hasło.", account.Message!.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void GenderAttribute_TemplateOptionsAndTitleFindings()
    {
        var options = SalutationOptions.Default with { GenderAttribute = "extensionAttribute5" };
        var template = Template("{$salutation},", options: new TemplateSalutationOptions { UseTitles = false, UnknownGender = UnknownGenderMode.Feminine });

        var plan = Plan(template,
        [
            new(ProfessorDn, Professor(("extensionAttribute5", "M"))),
            new(SaszaDn, Sasza(("extensionAttribute5", "?"))),
        ], options: options);

        var professor = plan.Messages[0];
        Assert.StartsWith("Szanowny Panie,", professor.Message!.TextBody, StringComparison.Ordinal);
        Assert.Empty(professor.Issues);
        Assert.Contains(professor.Derived, d => d is { Field: "gender", Source: DerivedValueSource.Attribute, IsGuess: false });

        var sasza = plan.Messages[1];
        Assert.StartsWith("Szanowna Pani,", sasza.Message!.TextBody, StringComparison.Ordinal);

        // The default gender of the template stands in for an unknown one: still a warning.
        Assert.Equal(PlannedMessageStatus.Warning, sasza.Status);
        Assert.Contains(new MessageIssue(SalutationIssueCodes.GenderDefaulted, MessageIssueSeverity.Warning, "Sasza Kim"), sasza.Issues);
        Assert.DoesNotContain(sasza.Issues, i => i.Code == SalutationIssueCodes.GenderGuessed);
        Assert.Contains(sasza.Derived, d => d is { Field: "gender", Value: "Feminine", Source: DerivedValueSource.Fallback, Issue: SalutationIssueCodes.GenderDefaulted });
        Assert.Contains(new MessageIssue(SalutationIssueCodes.GenderValueUnknown, MessageIssueSeverity.Info, "?"), sasza.Issues);

        var unrecognized = Plan(Template("{$salutation},"), [new(ProfessorDn, Professor(("personalTitle", "dr MBA")))]).Messages[0];
        Assert.StartsWith("Szanowny Panie Doktorze,", unrecognized.Message!.TextBody, StringComparison.Ordinal);
        Assert.Contains(new MessageIssue(SalutationIssueCodes.TitleUnrecognized, MessageIssueSeverity.Warning, "MBA"), unrecognized.Issues);
    }

    [Theory]
    [InlineData("PhD")]
    [InlineData("mec.")]
    public void TitleAttributeWithoutAnyKnownTitle_IsAFindingOfTheSalutation(string personalTitle)
    {
        // Only {$salutation} uses the title: the plain "Szanowny Panie" must not hide that the title was not understood.
        var message = Plan(BodyOnly("{$salutation},"), [new(ProfessorDn, Professor(("personalTitle", personalTitle)))]).Messages[0];

        Assert.StartsWith("Szanowny Panie,", message.Message!.TextBody, StringComparison.Ordinal);
        Assert.Contains(new MessageIssue(SalutationIssueCodes.TitleUnrecognized, MessageIssueSeverity.Warning, personalTitle), message.Issues);
        Assert.Contains(message.Derived, d => d is { Field: "title", Value: "", Issue: SalutationIssueCodes.TitleUnrecognized });
        Assert.Equal(PlannedMessageStatus.Warning, message.Status);

        // "noTitle" does not look at the title.
        var noTitle = Plan(BodyOnly("{$salutation:noTitle},"), [new(ProfessorDn, Professor(("personalTitle", personalTitle)))]).Messages[0];
        Assert.DoesNotContain(noTitle.Issues, i => i.Code == SalutationIssueCodes.TitleUnrecognized);
    }

    [Fact]
    public void TemplateWithoutSalutationFields_HasNoDerivedValuesAndNoFindings()
    {
        var plan = Plan(BodyOnly("Dzień dobry {givenName},"), [new(SaszaDn, Sasza())]);

        var message = Assert.Single(plan.Messages);
        Assert.Empty(message.Derived);
        Assert.Empty(message.Issues);
        Assert.Equal(PlannedMessageStatus.Ready, message.Status);
    }

    [Fact]
    public void BuiltInTemplates_StayWithoutSalutations()
    {
        foreach (var template in BuiltInMessageTemplates.All(MessageLanguage.Polish))
        {
            Assert.Null(template.Salutation);
            Assert.DoesNotContain(
                template.Texts().SelectMany(t => new[] { t.Subject, t.Body, t.Html ?? string.Empty }),
                text => text.Contains("{$salutation", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TemplateOptions_Normalize()
    {
        Assert.Null(new TemplateSalutationOptions().Normalized());
        Assert.Null(new TemplateSalutationOptions { NeutralPolish = "  ", UnknownGender = (UnknownGenderMode)42 }.Normalized());
        var normalized = new TemplateSalutationOptions { UseTitles = false, NeutralEnglish = " Dear\r\n{name} " }.Normalized()!;
        Assert.Equal((false, "Dear  {name}"), (normalized.UseTitles, normalized.NeutralEnglish));
        Assert.Equal(TemplateSalutationOptions.MaxNeutralLength, new TemplateSalutationOptions { NeutralPolish = new string('ż', 500) }.Normalized()!.NeutralPolish!.Length);
    }
}
