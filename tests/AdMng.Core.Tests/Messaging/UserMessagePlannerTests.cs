using AdMng.Core.ActiveDirectory;
using AdMng.Core.Messaging;
using static AdMng.Core.Tests.Messaging.MessageTestData;

namespace AdMng.Core.Tests.Messaging;

/// <summary>Plan and validation of messages: languages, conditions, recipients, encoding, footer, security.</summary>
public class UserMessagePlannerTests
{
    private readonly UserMessagePlanner _planner = new(new UserMessageFields());

    private static MessageTemplate Template(string id) => BuiltInMessageTemplates.Get(id, MessageLanguage.Polish)!;

    private UserMessagePlan Plan(MessageTemplate template, IEnumerable<UserMessageAccount> accounts, Action<UserMessagePlanRequest>? check = null,
        MessageRecipientMode? mode = null, int? days = null, Dictionary<string, string>? inputs = null, UserMessageSender? sender = null)
    {
        var request = new UserMessagePlanRequest
        {
            Template = template,
            Recipients = mode,
            ConditionDays = days,
            Inputs = inputs ?? new Dictionary<string, string>(),
            Sender = sender ?? Sender(),
            Accounts = [.. accounts],
            Now = Now,
            Domain = "contoso.test",
        };
        check?.Invoke(request);
        return _planner.Plan(request);
    }

    [Fact]
    public void BuiltInTemplates_AreValid_InBothLanguages()
    {
        foreach (var template in BuiltInMessageTemplates.All(MessageLanguage.Polish))
        {
            Assert.NotEmpty(template.Name);
            Assert.NotEmpty(template.Polish.Body);
            Assert.NotEmpty(template.English.Body);
            Assert.DoesNotContain(_planner.CheckTemplate(template), i => i.Code != MessageIssueCodes.MissingInput);
            Assert.Equal(template.Id == BuiltInMessageTemplates.Custom, template.Polish.Subject.Length == 0);
        }

        Assert.Equal("Password expires in N days", BuiltInMessageTemplates.Get(BuiltInMessageTemplates.PasswordExpiring, MessageLanguage.English)!.Name);
        Assert.Equal(["termin"], _planner.InputsOf(Template(BuiltInMessageTemplates.InactiveDisable)));
    }

    [Fact]
    public void PasswordExpiring_RendersThePolishMessage_WithFooterHelpdeskCcAndReplyTo()
    {
        var plan = Plan(Template(BuiltInMessageTemplates.PasswordExpiring), [new(LukaszDn, Lukasz(), Jan())]);

        var planned = Assert.Single(plan.Messages);
        Assert.Equal(PlannedMessageStatus.Ready, planned.Status);
        Assert.True(planned.CanSend);
        Assert.Equal("Łukasz Wiśniewski", planned.Name);
        Assert.Equal("lukasz.wisniewski", planned.Login);
        var message = planned.Message!;
        Assert.Equal(MessageLanguage.Polish, message.Language);
        Assert.Equal("Hasło do konta lukasz.wisniewski wygaśnie 5 listopada 2026", message.Subject);
        Assert.Equal([new MessageAddress("lukasz.wisniewski@contoso.test", "Łukasz Wiśniewski")], message.To);
        Assert.Equal([new MessageAddress("helpdesk@contoso.test")], message.Cc);
        Assert.Equal("helpdesk@contoso.test", message.ReplyTo!.Address);
        Assert.Equal("noreply@contoso.test", message.From.Address);
        Assert.StartsWith("Dzień dobry Łukasz,\n\nhasło do Twojego konta lukasz.wisniewski wygaśnie 5 listopada 2026 (pozostało dni: 10).", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("Nigdy nie prosimy o podanie hasła", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("<p>Dzień dobry Łukasz,</p>", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Nigdy nie prosimy o podanie hasła", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain(message.Subject, message.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PreferredLanguage_ChoosesEnglish_AndTheProxyAddressIsTheFallback()
    {
        var template = Template(BuiltInMessageTemplates.AccountExpiring);

        var planned = Assert.Single(Plan(template, [new(AgnieszkaDn, Agnieszka())], mode: MessageRecipientMode.User).Messages);

        Assert.Equal(MessageLanguage.English, planned.Language);
        Assert.Equal("The account agnieszka.zolc expires on 30 October 2026", planned.Message!.Subject);
        Assert.Equal("agnieszka.zolc@contoso.test", planned.Recipient);
        Assert.Contains(planned.Issues, i => i.Code == MessageIssueCodes.ProxyAddressUsed && i.Severity == MessageIssueSeverity.Info);
        Assert.Contains("We never ask for your password", planned.Message.TextBody, StringComparison.Ordinal);
        Assert.Equal(PlannedMessageStatus.Ready, planned.Status);
    }

    [Fact]
    public void ATemplateWithOneLanguageOnly_SendsTheWholeMessageInThatLanguage()
    {
        // Agnieszka prefers English, the operator's template has Polish text only: the Polish text falls back, and its
        // dates and the footer must be Polish too – not "wygaśnie 30 October 2026" with the English footer.
        var template = new MessageTemplate
        {
            Name = "Tylko po polsku",
            Polish = new MessageTemplateText { Subject = "Konto {sAMAccountName}", Body = "Konto wygaśnie {$accountExpires}." },
        };

        var planned = Assert.Single(Plan(template, [new(AgnieszkaDn, Agnieszka())]).Messages);

        Assert.Equal(MessageLanguage.Polish, planned.Language);
        Assert.Equal(MessageLanguage.Polish, planned.Message!.Language);
        Assert.StartsWith("Konto wygaśnie 30 października 2026.", planned.Message.TextBody, StringComparison.Ordinal);
        Assert.Contains("Nigdy nie prosimy o podanie hasła", planned.Message.TextBody, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"pl\">", planned.Message.HtmlBody, StringComparison.Ordinal);
        Assert.Equal(MessageLanguage.Polish, template.TextLanguage(MessageLanguage.English));
        Assert.Equal(MessageLanguage.English, (template with { English = new MessageTemplateText { Body = "Hello" } }).TextLanguage(MessageLanguage.English));
    }

    [Fact]
    public void DefaultLanguage_AppliesToUnknownPreferredLanguages()
    {
        var sender = Sender() with { DefaultLanguage = MessageLanguage.English };

        var planned = Plan(Template(BuiltInMessageTemplates.PasswordExpiring), [new(LukaszDn, Lukasz(("preferredLanguage", "de-DE")))], sender: sender).Messages[0];

        Assert.Equal(MessageLanguage.English, planned.Language);
        Assert.StartsWith("Hello Łukasz,", planned.Message!.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Condition_SkipsAccountsThatNoLongerMeetIt_WithTheParameterOfTheRun()
    {
        var template = Template(BuiltInMessageTemplates.PasswordExpiring);
        var changed = Lukasz((Attr.PasswordExpiryComputed, FileTime(Now.AddDays(80))));

        var plan = Plan(template, [new(LukaszDn, changed), new(JanDn, Jan((Attr.PasswordExpiryComputed, FileTime(Now.AddDays(3)))))]);
        var wider = Plan(template, [new(LukaszDn, changed)], days: 90);

        Assert.Equal(PlannedMessageStatus.Skipped, plan.Messages[0].Status);
        Assert.False(plan.Messages[0].CanSend);
        Assert.Contains(plan.Messages[0].Issues, i => i.Code == MessageIssueCodes.ConditionNotMet && i.Argument == nameof(MessageConditionKind.PasswordExpiresWithin));
        Assert.Equal(PlannedMessageStatus.Ready, plan.Messages[1].Status);
        Assert.Equal(PlannedMessageStatus.Ready, wider.Messages[0].Status);
        Assert.Equal(1, plan.SendableCount);
    }

    [Theory]
    [InlineData(MessageConditionKind.PasswordExpired, false)]
    [InlineData(MessageConditionKind.MustChangePassword, true)]
    [InlineData(MessageConditionKind.Locked, false)]
    [InlineData(MessageConditionKind.NotLocked, true)]
    [InlineData(MessageConditionKind.AccountExpired, false)]
    [InlineData(MessageConditionKind.InactiveFor, false)]
    public void Conditions_AreEvaluatedFromTheAccountState(MessageConditionKind condition, bool met)
    {
        var status = AccountStatus.Evaluate(Lukasz((Attr.PwdLastSet, "0")), Now, Warsaw);

        Assert.Equal(met, MessageConditions.IsMet(condition, 90, status));
        Assert.Equal(condition == MessageConditionKind.InactiveFor, MessageConditions.UsesDays(condition));
    }

    [Fact]
    public void RecipientModes_UserManagerBothAndFallback()
    {
        var template = Template(BuiltInMessageTemplates.AccountExpiring);
        var noMail = Lukasz((Attr.Mail, string.Empty));

        var both = Plan(template, [new(LukaszDn, Lukasz(), Jan())], mode: MessageRecipientMode.UserAndManager).Messages[0].Message!;
        var manager = Plan(template, [new(LukaszDn, Lukasz(), Jan())], mode: MessageRecipientMode.Manager).Messages[0].Message!;
        var fallback = Plan(template, [new(LukaszDn, noMail, Jan())], mode: MessageRecipientMode.UserOrManager).Messages[0];
        var nobody = Plan(template, [new(LukaszDn, noMail, null)], mode: MessageRecipientMode.UserOrManager).Messages[0];

        Assert.Equal(["lukasz.wisniewski@contoso.test"], both.To.Select(a => a.Address));
        Assert.Equal(["jan.kowalski@contoso.test", "helpdesk@contoso.test"], both.Cc.Select(a => a.Address));
        Assert.Equal(["jan.kowalski@contoso.test"], manager.To.Select(a => a.Address));
        Assert.Equal("jan.kowalski@contoso.test", fallback.Recipient);
        Assert.Contains(fallback.Issues, i => i.Code == MessageIssueCodes.ManagerInstead);
        Assert.Equal(PlannedMessageStatus.Error, nobody.Status);
        Assert.Contains(nobody.Issues, i => i.Code == MessageIssueCodes.NoAddress);
        Assert.Null(nobody.Message);
    }

    [Fact]
    public void Warnings_DisabledAccount_DisabledManager_EmptyField_AndSharedAddresses()
    {
        var disabled = Lukasz((Attr.UserAccountControl, Uac(UserAccountControl.NormalAccount | UserAccountControl.AccountDisable)), (Attr.GivenName, string.Empty));
        var shared = Jan((Attr.Mail, "lukasz.wisniewski@contoso.test"), (Attr.PasswordExpiryComputed, FileTime(Now.AddDays(3))));
        var disabledManager = Jan((Attr.UserAccountControl, Uac(UserAccountControl.NormalAccount | UserAccountControl.AccountDisable)));

        var plan = Plan(Template(BuiltInMessageTemplates.PasswordExpiring), [new(LukaszDn, disabled, disabledManager), new(JanDn, shared)],
            mode: MessageRecipientMode.UserAndManager);

        var first = plan.Messages[0];
        Assert.Equal(PlannedMessageStatus.Warning, first.Status);
        Assert.True(first.CanSend);
        Assert.Contains(first.Issues, i => i.Code == MessageIssueCodes.AccountDisabled);
        Assert.Contains(first.Issues, i => i.Code == MessageIssueCodes.ManagerDisabled && i.Argument == "Jan Kowalski");
        Assert.Contains(first.Issues, i => i.Code == MessageIssueCodes.EmptyField && i.Argument == "{givenName}");
        Assert.Contains(first.Issues, i => i.Code == MessageIssueCodes.DuplicateAddress);
        Assert.Contains(plan.Messages[1].Issues, i => i.Code == MessageIssueCodes.DuplicateAddress);
    }

    [Fact]
    public void DirectoryValues_AreHtmlEncoded_AndStrippedOfLineBreaksInTheSubject()
    {
        var template = new MessageTemplate
        {
            Polish = new MessageTemplateText
            {
                Subject = "Dla {displayName}",
                Body = "Dział: {department}",
                Html = "<p style=\"color:#333\">Dział: <b>{department}</b></p>",
            },
        };
        var account = Lukasz((Attr.DisplayName, "Łukasz\r\nBcc: ofiara@example.com"), (Attr.Department, "<script>alert('x')</script> & Co"));

        var message = Plan(template, [new(LukaszDn, account)]).Messages[0].Message!;

        Assert.Equal("Dla Łukasz  Bcc: ofiara@example.com", message.Subject);
        Assert.DoesNotContain('\n', message.Subject);
        Assert.Equal("Łukasz  Bcc: ofiara@example.com", message.To[0].DisplayName);
        Assert.Contains("<b>&lt;script&gt;alert('x')&lt;/script&gt; &amp; Co</b>", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("<p style=\"color:#333\">", message.HtmlBody, StringComparison.Ordinal);
        Assert.StartsWith("Dział: <script>alert('x')</script> & Co", message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TemplateErrors_BlockThePlan_ForbiddenFieldsInputsHtmlAndMissingInputs()
    {
        var template = new MessageTemplate
        {
            Polish = new MessageTemplateText { Subject = "{ms-Mcs-AdmPwd}", Body = "{$input:hasło} {$manager:unicodePwd} {$input:termin}" },
            English = new MessageTemplateText { Subject = "x", Body = "y", Html = "<p onclick=\"x()\">{$nieznane}</p>" },
        };

        var plan = Plan(template, [new(LukaszDn, Lukasz())]);

        Assert.True(plan.IsBlocked);
        Assert.Empty(plan.Messages);
        var codes = plan.TemplateIssues.Select(i => (i.Code, i.Argument)).ToList();
        Assert.Contains((MessageIssueCodes.ForbiddenField, "{ms-Mcs-AdmPwd}"), codes);
        Assert.Contains((MessageIssueCodes.ForbiddenInput, "{$input:hasło}"), codes);
        Assert.Contains((MessageIssueCodes.ForbiddenField, "{$manager:unicodePwd}"), codes);
        Assert.Contains((MessageIssueCodes.MissingInput, "termin"), codes);
        Assert.Contains((MessageIssueCodes.HtmlNotAllowed, "onclick="), codes);
        Assert.Contains(plan.TemplateIssues, i => i.Code == MessageIssueCodes.TemplateSyntax);
        Assert.True(Plan(Template(BuiltInMessageTemplates.Custom), [new(LukaszDn, Lukasz())], sender: Sender() with { From = new MessageAddress("brak") }).IsBlocked);
    }

    [Fact]
    public void Links_OutsideTheAllowList_AreErrors_TheDomainAndTheSenderDomainAreAllowed()
    {
        var template = new MessageTemplate
        {
            Polish = new MessageTemplateText { Subject = "Zmiana hasła", Body = "Zmień hasło: https://sspr.contoso.test/ albo https://intranet.firma.pl/{sAMAccountName}" },
        };

        var blocked = Plan(template, [new(LukaszDn, Lukasz())]).Messages[0];
        var allowed = Plan(template, [new(LukaszDn, Lukasz())], sender: Sender("firma.pl")).Messages[0];
        var fromValue = Plan(template with { Polish = template.Polish with { Body = "{wWWHomePage}" } }, [new(LukaszDn, Lukasz(("wWWHomePage", "http://evil.example/x")))]).Messages[0];

        Assert.Equal(PlannedMessageStatus.Error, blocked.Status);
        Assert.Contains(blocked.Issues, i => i.Code == MessageIssueCodes.LinkNotAllowed && i.Argument == "intranet.firma.pl");
        Assert.DoesNotContain(blocked.Issues, i => i.Argument == "sspr.contoso.test");
        Assert.Equal(PlannedMessageStatus.Ready, allowed.Status);
        Assert.Contains(fromValue.Issues, i => i.Code == MessageIssueCodes.LinkNotAllowed && i.Argument == "evil.example");
    }

    [Fact]
    public void Subject_EmptyOrTooLong_AndMissingAccounts_AreErrors()
    {
        var custom = Template(BuiltInMessageTemplates.Custom);
        var tooLong = custom with { Polish = custom.Polish with { Subject = new string('x', UserMessageRules.MaxSubjectLength + 1) } };

        var plan = Plan(custom, [new(LukaszDn, Lukasz()), new(JanDn, null), new(LukaszDn, Lukasz())]);

        Assert.Equal(2, plan.Messages.Count);
        Assert.Contains(plan.Messages[0].Issues, i => i.Code == MessageIssueCodes.EmptySubject);
        Assert.Contains(plan.Messages[1].Issues, i => i.Code == MessageIssueCodes.NotFound);
        Assert.Equal("Jan Kowalski", plan.Messages[1].Name);
        Assert.Contains(Plan(tooLong, [new(LukaszDn, Lukasz())]).Messages[0].Issues, i => i.Code == MessageIssueCodes.SubjectTooLong);
    }

    [Fact]
    public void AttributesToRead_IncludeTheTemplateFields_AndTheManagerFields()
    {
        var template = new MessageTemplate { Polish = new MessageTemplateText { Subject = "{title}", Body = "{$manager:telephoneNumber} {department}" } };

        Assert.Contains("title", _planner.AccountAttributesFor(template));
        Assert.Contains("department", _planner.AccountAttributesFor(template));
        Assert.Contains(Attr.PasswordExpiryComputed, _planner.AccountAttributesFor(template));
        Assert.Contains("preferredLanguage", _planner.AccountAttributesFor(template));
        Assert.Contains("telephoneNumber", _planner.ManagerAttributesFor(template));
        Assert.Contains(Attr.Mail, _planner.ManagerAttributesFor(template));
    }
}
