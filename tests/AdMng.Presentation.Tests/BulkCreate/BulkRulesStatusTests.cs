using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.BulkCreate;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;
using AdMng.Testing;
using AdMng.Testing.BulkCreate;

namespace AdMng.Presentation.Tests.BulkCreate;

/// <summary>
/// What step 3 <em>Nazwy i opcje</em> of the bulk creation wizard says in the status line of the batch editor (track V,
/// stage V2b-4; docs/dev/v-interface-renewal.md "V2b-4 as built"): the answers to the presses of the step - a profile saved,
/// deleted or made the default, or why not, and why the account that can join computers was not set - and nothing else:
/// why <em>Dalej</em> is not possible is a note of the step. The profile commands themselves are covered, unchanged, by
/// <see cref="BulkCreateProfileTests"/>.
/// </summary>
public sealed class BulkRulesStatusTests
{
    [Fact]
    public async Task TheStep_SaysNothingAtRest_AndWhyDalejIsNotPossibleIsNotStatus()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        await wizard.GoToRulesAsync();
        var line = wizard.Editor.Status;

        // The page of step 3 is in front of the line, and it has no summary.
        Assert.Same(wizard.Rules.Status, wizard.Wizard.Status.Front);
        Assert.Equal((PageStatusKind.Idle, string.Empty), (line.Current.Kind, line.Current.Message));

        // A rule without an attribute: the step says why Dalej is not possible - in the step, as a note.
        wizard.Rules.AddRuleCommand.Execute(null);
        wizard.Rules.Rules[^1].Pattern = "Nowy pracownik";
        Assert.Equal("Wybierz atrybut reguły.", wizard.Rules.Error);
        Assert.Equal((PageStatusKind.Idle, string.Empty), (line.Current.Kind, line.Current.Message));
        Assert.Null(wizard.Rules.Status.Lasting);
    }

    [Fact]
    public async Task AProfileThatCannotBeSaved_IsWhatWasSkippedAndWhy_AndCancelClosesIt()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        await wizard.GoToRulesAsync();
        var rules = wizard.Rules;
        var line = wizard.Editor.Status;
        await rules.SaveProfileCommand.ExecuteAsync(null);

        rules.NewProfileName = "  ";
        await rules.ConfirmSaveProfileAsCommand.ExecuteAsync(null);

        var skipped = line.Current;
        Assert.Equal(PageStatusKind.Attention, skipped.Kind);
        Assert.Equal("Nie zapisano profilu: Wpisz nazwę profilu.", skipped.Message);
        Assert.Equal("BulkCreate.Status.ProfileNotSaved", skipped.Log.ToString());
        Assert.True(skipped.CanClose);

        // The name is on screen only.
        rules.NewProfileName = "Standardowy";
        await rules.ConfirmSaveProfileAsCommand.ExecuteAsync(null);
        Assert.Equal("Nie zapisano profilu: Profil o nazwie „Standardowy” już istnieje.", line.Current.Message);
        Assert.Equal("BulkCreate.Status.ProfileNotSaved", line.Current.Log.ToString());

        // Cancel closes the question and what was said about it.
        rules.CancelSaveProfileAsCommand.Execute(null);
        Assert.Equal((PageStatusKind.Idle, string.Empty), (line.Current.Kind, line.Current.Message));
    }

    [Fact]
    public async Task AProfileThatWasSaved_SetAsTheDefault_AndDeleted_IsDone_WithItsSentenceAndNoName()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        await wizard.GoToRulesAsync();
        var rules = wizard.Rules;
        var line = wizard.Editor.Status;
        await rules.SaveProfileCommand.ExecuteAsync(null);
        rules.NewProfileName = "Kadry";

        await rules.ConfirmSaveProfileAsCommand.ExecuteAsync(null);

        // The sentence the step always had, as a result; the log copy has the key and how many columns were remembered.
        var saved = line.Current;
        Assert.Equal(PageStatusKind.Done, saved.Kind);
        Assert.Equal("Zapisano profil „Kadry”. Zapamiętane kolumny: Telefon komórkowy, Imię, Nazwisko, Dział, E-mail.", saved.Message);
        Assert.Equal("BulkCreate.ProfileSavedWithHeaders (5)", saved.Log.ToString());
        Assert.DoesNotContain("Kadry", saved.Log.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Telefon", saved.Log.ToString(), StringComparison.Ordinal);
        Assert.True(saved.CanClose);

        // The default profile of the container.
        await rules.SetDefaultForContainerCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Done, line.Current.Kind);
        Assert.Equal("BulkCreate.ProfileIsDefault", line.Current.Log.ToString());
        await rules.SetDefaultForContainerCommand.ExecuteAsync(null);
        Assert.Equal("„Kadry” nie jest już domyślnym profilem tego kontenera.", line.Current.Message);
        Assert.Equal("BulkCreate.ProfileNotDefault", line.Current.Log.ToString());

        // Deleted (after the question).
        wizard.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await rules.DeleteProfileCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Done, line.Current.Kind);
        Assert.Contains("Kadry", line.Current.Message, StringComparison.Ordinal);
        Assert.Equal("BulkCreate.ProfileDeleted", line.Current.Log.ToString());

        // The next thing the operator does - another profile - ends what was said about the last one.
        rules.Profile = rules.Profiles.Single(p => p.Value == BulkCreateProfileStore.NumberedId);
        await wizard.SettleAsync();
        Assert.Equal((PageStatusKind.Idle, string.Empty), (line.Current.Kind, line.Current.Message));
    }

    [Fact]
    public async Task ABuiltInProfile_CannotBeTheDefault_AndTheStepSaysWhy()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        await wizard.GoToRulesAsync();

        await wizard.Rules.SetDefaultForContainerCommand.ExecuteAsync(null);

        var skipped = wizard.Editor.Status.Current;
        Assert.Equal(PageStatusKind.Attention, skipped.Kind);
        Assert.Equal(
            "Nie zmieniono profilu domyślnego: Profil wbudowany nie może być domyślny dla kontenera – najpierw zapisz go pod nową nazwą.",
            skipped.Message);
        Assert.Equal("BulkCreate.Status.DefaultNotChanged", skipped.Log.ToString());
        Assert.True(wizard.Rules.IsNamingProfile);
    }

    [Fact]
    public async Task TheMaximumOfProfiles_IsWhatWasNotSaved_WithTheLimitAndWhatToDo()
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

        Assert.Equal(
            "Nie zapisano profilu: Nie można zapisać więcej profili (limit: 50). Usuń profil, który nie jest już potrzebny.",
            wizard.Editor.Status.Current.Message);
    }

    [Fact]
    public async Task TheAccountThatCanJoinComputers_ThatCannotBeRead_IsSaidAtOnce_AndAnotherPickClosesIt()
    {
        using var wizard = await BulkWizardHarness.CreateAsync(ObjectKind.Computer);
        await wizard.PasteAsync(ExcelSamples.Tsv(["Nazwa komputera"], ["PC-KSIEG-01"], ["PC-KSIEG-02"]));
        await wizard.GoToRulesAsync();
        var rules = wizard.Rules;
        var line = wizard.Editor.Status;
        var loc = wizard.Harness.Localizer;

        // An object that is not there: nothing was set, and the line says why.
        wizard.Harness.Dialogs.PickedObjects.Enqueue(["CN=Nie ma,OU=Nigdzie," + wizard.Harness.Session.Info.DefaultNamingContext]);
        await rules.PickJoinerCommand.ExecuteAsync(null);
        var notFound = line.Current;
        Assert.Equal(PageStatusKind.Attention, notFound.Kind);
        Assert.Equal($"{loc[L.BulkCreateStatus.JoinerNotSet]}: {loc[L.BulkCreate.JoinerNotFound]}", notFound.Message);
        Assert.Equal("BulkCreate.Status.JoinerNotSet", notFound.Log.ToString());
        Assert.Equal(loc[L.BulkCreate.JoinerNotFound], rules.OptionsError);

        // The directory does not answer: an error with the reason and the code - never the text of the server.
        wizard.Harness.Session.BeforeOperation = operation =>
        {
            if (operation.Kind == FakeOperationKind.Get)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "81: Server Down, CN=Łukasz Żółć") { LdapResultCode = 81 };
            }
        };
        wizard.Harness.Dialogs.PickedObjects.Enqueue([wizard.Harness.Company.JanKowalski]);
        await rules.PickJoinerCommand.ExecuteAsync(null);
        wizard.Harness.Session.BeforeOperation = null;
        var failed = line.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.Equal(
            $"{loc[L.BulkCreateStatus.JoinerNotRead]}: {wizard.Harness.Errors.Translate(DirectoryErrorKind.ServerUnavailable)} (LDAP 81)",
            failed.Message);
        Assert.Equal("BulkCreate.Status.JoinerNotRead [LDAP 81]", failed.Log.ToString());
        Assert.DoesNotContain("Żółć", failed.ToString(), StringComparison.Ordinal);
        Assert.True(failed.CanClose);

        // A pick that works: the account is set and what was said about the last one is over.
        wizard.Harness.Dialogs.PickedObjects.Enqueue([wizard.Harness.Company.JanKowalski]);
        await rules.PickJoinerCommand.ExecuteAsync(null);
        Assert.NotNull(rules.Joiner);
        Assert.Null(rules.OptionsError);
        Assert.Equal((PageStatusKind.Idle, string.Empty), (line.Current.Kind, line.Current.Message));
    }

    [Fact]
    public async Task WhatTheStepSaid_ComesBackWhenTheStepIsShownAgain()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        await wizard.GoToRulesAsync();
        await wizard.Rules.SaveProfileCommand.ExecuteAsync(null);
        wizard.Rules.NewProfileName = " ";
        await wizard.Rules.ConfirmSaveProfileAsCommand.ExecuteAsync(null);
        var said = wizard.Editor.Status.Current.Message;
        Assert.StartsWith("Nie zapisano profilu: ", said, StringComparison.Ordinal);

        // The columns step has nothing to say: the line is empty there, and the sentence is back with the step.
        wizard.Wizard.PreviousStepCommand.Execute(null);
        Assert.Null(wizard.Wizard.Status.Front);
        Assert.Equal((PageStatusKind.Idle, string.Empty), (wizard.Editor.Status.Current.Kind, wizard.Editor.Status.Current.Message));
        await wizard.Wizard.NextStepCommand.ExecuteAsync(null);
        Assert.Same(wizard.Rules.Status, wizard.Wizard.Status.Front);
        Assert.Equal(said, wizard.Editor.Status.Current.Message);
    }
}
