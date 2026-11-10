using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.BulkCreate;
using AdMng.Presentation.Settings;
using AdMng.Testing.BulkCreate;

namespace AdMng.Presentation.Tests.BulkCreate;

/// <summary>
/// The naming profiles of step 3 (docs/dev/phase3-bulk-create.md 2.6, 2.11 Presentation B3) – built-ins never stored, save,
/// <em>Zapisz</em> on a built-in, <em>(zmieniony)</em> and the switch question, delete, the default of a container and the
/// preselection order, <c>MaxProfiles</c>, header mappings that look like data, groups of another domain, settings free of
/// passwords and sample values – and the operator scenario of 2.6 through steps 1–3 and the real planner, executor and fake
/// directory (the check grid and the results are B4's).
/// </summary>
public sealed class BulkCreateProfileTests
{
    [Fact]
    public async Task OperatorScenario_PasteToCreatedAccounts_AndTheContainersDefaultProfile()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        var harness = wizard.Harness;
        var sample = wizard.Sample;

        // 1 Ctrl+V – the five columns map without a click.
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        Assert.Equal(["mobile", "givenName", "sn", "department", "mail"], wizard.Columns.ToMappings().Select(m => m.Attribute));

        // 2–3 Dalej, Dalej; 4 Konta numerowane; 5 the suffix contoso.com; 6 the group GG_Nowi (must-change stays on).
        await wizard.GoToRulesAsync();
        var rules = wizard.Rules;
        rules.Profile = rules.Profiles.Single(p => p.Value == BulkCreateProfileStore.NumberedId);
        rules.UpnSuffix = "contoso.com";
        harness.Dialogs.PickedObjects.Enqueue([sample.GroupNew]);
        await rules.AddGroupsCommand.ExecuteAsync(null);
        await wizard.SettleAsync();

        Assert.Equal("Pierwszy wolny numer: kd208 (najwyższy istniejący: kd207)", rules.FirstFreeText);
        Assert.StartsWith("kd208@contoso.com", wizard.Rule(Attr.UserPrincipalName).Sample, StringComparison.Ordinal);
        Assert.True(rules.MustChangeAtLogon);
        Assert.Null(rules.Error);

        // 7 Dalej to the check step; the planner's input comes from the three steps.
        await wizard.Wizard.NextStepCommand.ExecuteAsync(null);
        Assert.True(wizard.Wizard.IsCheckStep);
        var input = wizard.Wizard.BuildInput();
        Assert.NotNull(input);
        var planner = harness.Engines.BulkCreate;
        var computation = await planner.ComputeAsync(harness.Session, input!, new BulkCreateLookups(), null, TestContext.Current.CancellationToken);

        Assert.False(computation.HasRunErrors);
        Assert.Equal(["kd208", "kd209", "kd210", "kd211", "kd212"], computation.Rows.Select(r => r.Value(Attr.SamAccountName)));
        Assert.Equal("kd208@contoso.com", computation.Rows[0].Value(Attr.UserPrincipalName));
        Assert.Equal("lukasz.grzedzinski@firma.pl", computation.Rows[0].Value(Attr.Mail));
        Assert.Equal("+48 600 200 301", computation.Rows[0].Value(Attr.Mobile));
        Assert.Equal("Łukasz Grzędziński", computation.Rows[0].Value(Attr.Cn));
        Assert.All(computation.Rows, r => Assert.NotEqual(BulkRowStatus.Error, r.Status));

        // The plan: one create per row with a generated password, must-change and the group; executed through the pipeline.
        var plan = planner.BuildPlan(computation, rules.ReadSharedPassword(), "Utworzenie kont");
        Assert.True(plan.ContainsPasswords);
        var passwords = plan.Objects.SelectMany(o => o.Changes).Where(c => c.Action == ChangeAction.ResetPassword).Select(c => c.Password).ToList();
        Assert.Equal(5, passwords.Count);
        Assert.All(passwords, p => Assert.True(p is { Length: >= 16 }));
        var result = await harness.Coordinator.ExecutePlanAsync(plan, "Utworzenie kont", harness.Session, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(5, result.SucceededCount);
        var created = await harness.Session.GetAsync(
            computation.Rows[0].DistinguishedName!,
            [Attr.SamAccountName, Attr.UserPrincipalName, Attr.Mobile, Attr.Mail, Attr.Department, Attr.MemberOf, Attr.PwdLastSet],
            TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        Assert.Equal("kd208", created!.GetString(Attr.SamAccountName));
        Assert.Equal("kd208@contoso.com", created.GetString(Attr.UserPrincipalName));
        Assert.Equal("+48 600 200 301", created.GetString(Attr.Mobile));
        Assert.Equal("Księgowość", created.GetString(Attr.Department));
        Assert.Equal("0", created.GetString(Attr.PwdLastSet));
        Assert.Contains(created.GetStrings(Attr.MemberOf), dn => DistinguishedName.AreEqual(dn, sample.GroupNew));

        // The profile is saved as the default of "Nowi pracownicy" (Zapisz jako…, the default box, OK).
        await rules.SaveProfileAsCommand.ExecuteAsync(null);
        Assert.Equal("Konta numerowane (kd201…) (kopia)", rules.NewProfileName);
        Assert.Equal("Profil zapamięta też przypisanie kolumn: Telefon komórkowy, Imię, Nazwisko, Dział, E-mail.", rules.SaveHeadersText);
        rules.NewProfileName = "Nowi pracownicy – konta kd";
        rules.SaveAsDefaultForContainer = true;
        await rules.ConfirmSaveProfileAsCommand.ExecuteAsync(null);
        Assert.Equal("Nowi pracownicy – konta kd", rules.Profile?.Text);
        Assert.False(rules.IsBuiltInProfile);
        Assert.False(rules.IsModified);
        Assert.True(rules.IsDefaultForContainer);
        var saved = Assert.Single(harness.Settings.Current.BulkCreate.Profiles);
        Assert.Equal("contoso.com", saved.UpnSuffix);
        Assert.Equal([DistinguishedName.ToPortable(sample.NewEmployeesOu, harness.Session.Info.DefaultNamingContext)], saved.DefaultForContainers);
        Assert.Equal([DistinguishedName.ToPortable(sample.GroupNew, harness.Session.Info.DefaultNamingContext)], saved.GroupDns);
        Assert.Equal("contoso.test", saved.GroupDomain);
        Assert.Equal(5, saved.HeaderMappings.Count);
        AssertNoSampleValues(harness.Settings.Current.BulkCreate);

        // A new wizard on that container picks it up, and its first free number is kd213.
        using var next = await BulkWizardHarness.CreateAsync(harness: harness, sample: sample);
        Assert.Equal("Nowi pracownicy – konta kd", next.Rules.Profile?.Text);
        Assert.Equal("contoso.com", next.Rules.UpnSuffix);
        await next.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        Assert.Equal("jak zapamiętano w profilu", next.Mapping("E-mail").NoteText);
        await next.GoToRulesAsync();
        Assert.Equal("Pierwszy wolny numer: kd213 (najwyższy istniejący: kd212)", next.Rules.FirstFreeText);
        Assert.Equal(["GG_Nowi"], next.Rules.Groups.Select(g => g.Name));
        Assert.False(next.Rules.IsModified);
    }

    [Fact]
    public void BuiltIns_AreCode_WithTheDesignedRules()
    {
        var store = new BulkCreateProfileStore(new TestDoubles.InMemorySettingsStore(), new global::AdMng.Presentation.Localization.ResxLocalizer(BulkWizardHarness.Polish));

        var users = store.BuiltIns(ObjectKind.User);
        Assert.Equal(["Standardowy", "Konta numerowane (kd201…)"], users.Select(p => p.Name));
        Assert.All(users, p => Assert.True(BulkCreateProfileStore.IsBuiltIn(p.Id)));
        var numbered = users[1];
        Assert.Equal(CounterMode.AfterHighest, numbered.CounterMode);
        Assert.False(numbered.ReuseRecycleBinNumbers);
        Assert.Contains(numbered.Rules, r => r.Attribute == Attr.Cn && r.Pattern == "{givenName} {sn}");
        Assert.Contains(numbered.Rules, r => r.Attribute == Attr.Mail && r.Pattern == "{sAMAccountName}@{$mailDomain}");
        Assert.False(numbered.AddPrimarySmtp);
        Assert.Equal(CounterMode.FillGaps, users[0].CounterMode);
        Assert.Equal(["Standardowy"], store.BuiltIns(ObjectKind.Contact).Select(p => p.Name));
        Assert.Equal("{cn}", Assert.Single(store.BuiltIns(ObjectKind.Computer)[0].Rules).Pattern);
        Assert.Equal(store.BuiltIns(ObjectKind.Group)[0].Id, store.Find(BulkCreateProfileStore.StandardId(ObjectKind.Group))?.Id);
        Assert.Empty(new TestDoubles.InMemorySettingsStore().Current.BulkCreate.Profiles);
    }

    [Fact]
    public async Task SaveOnABuiltIn_BecomesSaveAs_AndBuiltInsAreNeverStored()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        await wizard.GoToRulesAsync();
        var rules = wizard.Rules;
        wizard.Rule(Attr.Description).Pattern = "Nowy pracownik – {department}";
        Assert.True(rules.IsModified);

        await rules.SaveProfileCommand.ExecuteAsync(null);

        Assert.True(rules.IsNamingProfile);
        Assert.Equal("Standardowy (kopia)", rules.NewProfileName);
        Assert.Empty(wizard.Harness.Settings.Current.BulkCreate.Profiles);
        rules.CancelSaveProfileAsCommand.Execute(null);
        Assert.False(rules.IsNamingProfile);

        await rules.SaveProfileCommand.ExecuteAsync(null);
        rules.NewProfileName = "  ";
        await rules.ConfirmSaveProfileAsCommand.ExecuteAsync(null);
        Assert.Equal("Wpisz nazwę profilu.", rules.ProfileStatus);
        rules.NewProfileName = "Standardowy";
        await rules.ConfirmSaveProfileAsCommand.ExecuteAsync(null);
        Assert.Equal("Profil o nazwie „Standardowy” już istnieje.", rules.ProfileStatus);

        rules.NewProfileName = "Kadry";
        await rules.ConfirmSaveProfileAsCommand.ExecuteAsync(null);
        var saved = Assert.Single(wizard.Harness.Settings.Current.BulkCreate.Profiles);
        Assert.Equal("Kadry", saved.Name);
        Assert.Contains(saved.Rules, r => r.Attribute == Attr.Description && r.Pattern == "Nowy pracownik – {department}");
        Assert.DoesNotContain(wizard.Harness.Settings.Current.BulkCreate.Profiles, p => BulkCreateProfileStore.IsBuiltIn(p.Id));
        Assert.Equal(saved.Id, wizard.Harness.Settings.Current.BulkCreate.LastProfileIds["User"]);
        Assert.Equal("Zapisano profil „Kadry”. Zapamiętane kolumny: Telefon komórkowy, Imię, Nazwisko, Dział, E-mail.", rules.ProfileStatus);

        // Zapisz on a saved profile overwrites it.
        wizard.Rule(Attr.Description).Pattern = "Kadry – {department}";
        Assert.True(rules.IsModified);
        await rules.SaveProfileCommand.ExecuteAsync(null);
        Assert.False(rules.IsModified);
        Assert.Equal("Kadry – {department}", Assert.Single(wizard.Harness.Settings.Current.BulkCreate.Profiles).Rules.Single(r => r.Attribute == Attr.Description).Pattern);
    }

    [Fact]
    public async Task SwitchingAChangedProfile_Asks()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        await wizard.GoToRulesAsync();
        var rules = wizard.Rules;
        var standard = rules.Profile;
        wizard.Rule(Attr.Description).Pattern = "Zmieniony opis";

        rules.Profile = rules.Profiles.Single(p => p.Value == BulkCreateProfileStore.NumberedId);
        await wizard.SettleAsync();

        Assert.Equal(standard, rules.Profile);
        Assert.Equal("Zmieniony opis", wizard.Rule(Attr.Description).Pattern);
        Assert.Contains(wizard.Harness.Dialogs.Shown, s => s == "confirm:Zmiany profilu „Standardowy” nie zostały zapisane. Odrzucić je i zmienić profil?");

        wizard.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        rules.Profile = rules.Profiles.Single(p => p.Value == BulkCreateProfileStore.NumberedId);
        await wizard.SettleAsync();
        Assert.Equal(BulkCreateProfileStore.NumberedId, rules.Profile?.Value);
        Assert.Equal(string.Empty, wizard.Rule(Attr.Description).Pattern);
        Assert.False(rules.IsModified);
        Assert.Equal(BulkCreateProfileStore.NumberedId, wizard.Harness.Settings.Current.BulkCreate.LastProfileIds["User"]);
    }

    [Fact]
    public async Task Preselection_ContainerDefault_ThenLastUsed_ThenStandard()
    {
        using var first = await BulkWizardHarness.CreateAsync();
        var harness = first.Harness;
        var settings = harness.Settings;
        var namingContext = harness.Session.Info.DefaultNamingContext;
        Assert.Equal("Standardowy", first.Rules.Profile?.Text);

        await settings.UpdateAsync(s =>
        {
            s.BulkCreate.Profiles.Add(new BulkCreateProfile { Id = "a", Name = "Księgowość", Kind = ObjectKind.User, DefaultForContainers = [DistinguishedName.ToPortable(first.Sample.AccountingChildOu, namingContext)] });
            s.BulkCreate.Profiles.Add(new BulkCreateProfile { Id = "b", Name = "Praktykanci", Kind = ObjectKind.User });
            s.BulkCreate.LastProfileIds["User"] = "b";
        }, TestContext.Current.CancellationToken);

        using var lastUsed = await BulkWizardHarness.CreateAsync(harness: harness, sample: first.Sample);
        Assert.Equal("Praktykanci", lastUsed.Rules.Profile?.Text);
        using var byContainer = await BulkWizardHarness.CreateAsync(harness: harness, sample: first.Sample, container: first.Sample.AccountingChildOu);
        Assert.Equal("Księgowość", byContainer.Rules.Profile?.Text);

        await settings.UpdateAsync(s => s.BulkCreate.LastProfileIds["User"] = "missing", TestContext.Current.CancellationToken);
        using var fallback = await BulkWizardHarness.CreateAsync(harness: harness, sample: first.Sample);
        Assert.Equal("Standardowy", fallback.Rules.Profile?.Text);
        Assert.Equal(["Standardowy", "Konta numerowane (kd201…)", "Księgowość", "Praktykanci"], fallback.Rules.Profiles.Select(p => p.Text));
    }

    [Fact]
    public async Task DefaultForContainer_Toggles_AndDeleteReturnsToStandard()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        await wizard.GoToRulesAsync();
        var rules = wizard.Rules;

        // A built-in cannot be a default: the command opens "Zapisz jako…" with the default box ticked.
        await rules.SetDefaultForContainerCommand.ExecuteAsync(null);
        Assert.True(rules.IsNamingProfile);
        Assert.True(rules.SaveAsDefaultForContainer);
        Assert.Equal("Profil wbudowany nie może być domyślny dla kontenera – najpierw zapisz go pod nową nazwą.", rules.ProfileStatus);
        rules.NewProfileName = "Nowi";
        await rules.ConfirmSaveProfileAsCommand.ExecuteAsync(null);
        Assert.True(rules.IsDefaultForContainer);

        await rules.SetDefaultForContainerCommand.ExecuteAsync(null);
        Assert.False(rules.IsDefaultForContainer);
        Assert.Empty(wizard.Harness.Settings.Current.BulkCreate.Profiles[0].DefaultForContainers);
        Assert.Equal("„Nowi” nie jest już domyślnym profilem tego kontenera.", rules.ProfileStatus);
        await rules.SetDefaultForContainerCommand.ExecuteAsync(null);
        Assert.True(rules.IsDefaultForContainer);

        Assert.True(rules.DeleteProfileCommand.CanExecute(null));
        await rules.DeleteProfileCommand.ExecuteAsync(null);
        Assert.Single(wizard.Harness.Settings.Current.BulkCreate.Profiles);
        wizard.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await rules.DeleteProfileCommand.ExecuteAsync(null);
        Assert.Empty(wizard.Harness.Settings.Current.BulkCreate.Profiles);
        Assert.Equal("Standardowy", rules.Profile?.Text);
        Assert.False(rules.DeleteProfileCommand.CanExecute(null));
        Assert.DoesNotContain(wizard.Harness.Settings.Current.BulkCreate.LastProfileIds.Values, id => !BulkCreateProfileStore.IsBuiltIn(id));
    }

    [Fact]
    public async Task MaxProfiles_IsEnforced()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.Harness.Settings.UpdateAsync(s =>
        {
            for (var i = 0; i < BulkCreateSettings.MaxProfiles; i++)
            {
                s.BulkCreate.Profiles.Add(new BulkCreateProfile { Name = $"Profil {i}", Kind = ObjectKind.User });
            }
        }, TestContext.Current.CancellationToken);
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        await wizard.GoToRulesAsync();

        await wizard.Rules.SaveProfileAsCommand.ExecuteAsync(null);
        wizard.Rules.NewProfileName = "Jeszcze jeden";
        await wizard.Rules.ConfirmSaveProfileAsCommand.ExecuteAsync(null);

        Assert.Equal("Nie można zapisać więcej profili (limit: 50). Usuń profil, który nie jest już potrzebny.", wizard.Rules.ProfileStatus);
        Assert.Equal(BulkCreateSettings.MaxProfiles, wizard.Harness.Settings.Current.BulkCreate.Profiles.Count);
        Assert.True(wizard.Rules.IsNamingProfile);
    }

    [Fact]
    public async Task HeadersThatLookLikeData_AreNeverRemembered()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        wizard.Source.HeaderMode = HeaderMode.FirstRowIsHeader;
        await wizard.PasteAsync(ExcelSamples.Tsv(
            ["Imię", "Nazwisko", "jan.kowalski@contoso.com", "600100200", "Hasło startowe"],
            ["Łucja", "Żak", "lucja.zak@contoso.com", "+48 600 100 201", "Start!2026"]));
        wizard.Mapping("600100200").Target = ColumnTargetKind.PatternOnly;
        await wizard.GoToRulesAsync();

        Assert.Equal(["imie", "nazwisko"], wizard.Columns.ToHeaderMappings().Select(m => m.Header));
        Assert.Equal(["Imię", "Nazwisko"], wizard.Columns.StorableHeaderNames());
        await wizard.Rules.SaveProfileAsCommand.ExecuteAsync(null);
        await wizard.Rules.ConfirmSaveProfileAsCommand.ExecuteAsync(null);

        var json = JsonSettingsStore.Serialize(wizard.Harness.Settings.Current);
        Assert.DoesNotContain("jan.kowalski", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("600100200", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Start!2026", json, StringComparison.Ordinal);
        AssertNoSampleValues(wizard.Harness.Settings.Current.BulkCreate);
    }

    [Fact]
    public async Task GroupsOfAnotherDomain_AreDropped_WithAWarning()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.Harness.Settings.UpdateAsync(s => s.BulkCreate.Profiles.Add(new BulkCreateProfile
        {
            Id = "other",
            Name = "Z innej domeny",
            Kind = ObjectKind.User,
            GroupDns = ["CN=GG_Nowi,OU=Grupy"],
            GroupDomain = "fabrikam.test",
            UpnSuffix = "fabrikam.com",
        }), TestContext.Current.CancellationToken);
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        await wizard.GoToRulesAsync();

        wizard.Rules.Profile = wizard.Rules.Profiles.Single(p => p.Value == "other");
        await wizard.SettleAsync();

        Assert.Empty(wizard.Rules.Groups);
        Assert.Equal("Grupy z profilu (liczba: 1) wybrano w domenie fabrikam.test i nie zostaną tu użyte – wybierz grupy ponownie.", wizard.Rules.GroupsWarning);
        Assert.Equal("Sufiksu UPN z profilu (fabrikam.com) nie ma w tym lesie – użyto contoso.test.", wizard.Rules.UpnSuffixWarning);
        Assert.Equal("contoso.test", wizard.Rules.UpnSuffix);
    }

    [Fact]
    public void Normalize_KeepsTheHeaderRule_AndClampsTheLength()
    {
        var settings = new BulkCreateSettings
        {
            Profiles =
            [
                new BulkCreateProfile
                {
                    Name = "Test",
                    InitialLength = 500,
                    HeaderMappings =
                    [
                        new BulkCreateHeaderMapping("telefonkomorkowy", ColumnTargetKind.Attribute, "mobile", NameOrder.GivenNameFirst, TextCase.AsIs),
                        new BulkCreateHeaderMapping("haslostartowe", ColumnTargetKind.Attribute, "description", NameOrder.GivenNameFirst, TextCase.AsIs),
                        new BulkCreateHeaderMapping("48600100200", ColumnTargetKind.Attribute, "mobile", NameOrder.GivenNameFirst, TextCase.AsIs),
                    ],
                },
                new BulkCreateProfile { Name = " " },
            ],
        };

        settings.Normalize();

        var profile = Assert.Single(settings.Profiles);
        Assert.Equal(Core.Security.PasswordGenerator.MaximumLength, profile.InitialLength);
        Assert.Equal(["telefonkomorkowy"], profile.HeaderMappings.Select(m => m.Header));
        foreach (var header in new[] { "haslo", "password", "pwd", "kennwort", "pin" })
        {
            Assert.Equal(ColumnSynonyms.IsPasswordLike(header), !BulkCreateSettings.IsStorableHeader(header));
        }
    }

    /// <summary>The profile settings hold patterns and options only: no sheet value, no password word.</summary>
    private static void AssertNoSampleValues(BulkCreateSettings settings)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        foreach (var value in new[] { "Łukasz", "Grzędziński", "lukasz", "600 200", "firma.pl", "kd208", "kd212", "Księgowość", "password", "pwd", "secret", "haslo" })
        {
            Assert.DoesNotContain(value, json, StringComparison.OrdinalIgnoreCase);
        }
    }
}
