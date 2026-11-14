using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Batch.Creation;
using AdMng.Presentation.BulkCreate;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.BulkCreate;

/// <summary>
/// The batch editor in create mode (docs/dev/phase3-bulk-create.md 2.7, 2.11 Presentation B4): the plan with frozen values,
/// the typed confirmation, the canary, the accumulated results with the passwords, copy and export, the close guard, the retry
/// and the undo – on the bulk-create sample through <see cref="B4Wizard"/>.
/// </summary>
public class BulkCreateBatchEditorTests
{
    /// <summary>Another HR sheet of five people – row numbers 1–5 again, other names.</summary>
    private static readonly string[][] SecondSheet =
    [
        ["+48 600 100 206", "Anna", "Kowalczyk", "Kadry", "anna.kowalczyk@firma.pl"],
        ["+48 600 100 207", "Paweł", "Wiśniewski", "IT", "pawel.wisniewski@firma.pl"],
        ["+48 600 100 208", "Katarzyna", "Zielińska", "Sprzedaż", "katarzyna.zielinska@firma.pl"],
        ["+48 600 100 209", "Tomasz", "Szymański", "Logistyka", "tomasz.szymanski@firma.pl"],
        ["+48 600 100 210", "Agnieszka", "Woźniak", "Księgowość", "agnieszka.wozniak@firma.pl"],
    ];

    [Fact]
    public async Task Preview_BuildsThePlanOfTheCheckedValues_AndTheChangeTitleCountsThem()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        var editor = wizard.Editor;
        Assert.Equal("Zakładanie wielu kont – Nowi pracownicy", editor.WindowTitle);

        await editor.PreviewCommand.ExecuteAsync(null);

        Assert.True(editor.IsPreviewStep, editor.Error);
        Assert.Equal("Utworzenie kont (5) w jednostce „Nowi pracownicy”", editor.ChangeTitle);
        Assert.Equal(5, editor.ChangeCount);
        Assert.Equal(0, editor.ErrorCount);
        var creates = editor.PreviewObjects.Select(o => o.Plan).ToList();
        Assert.All(creates, o => Assert.Equal(ChangeAction.Create, o.Changes[0].Action));
        Assert.Equal(["kd208", "kd209", "kd210", "kd211", "kd212"], creates.Select(o => Value(o, Attr.SamAccountName)));
        Assert.All(creates, o => Assert.Contains(o.Changes, c => c.Action == ChangeAction.AddMember));
        Assert.All(creates, o => Assert.Contains(o.Changes, c => c.Action == ChangeAction.ResetPassword && c.Password is { Length: 16 }));
        Assert.Null(editor.UndoNote);

        // The PowerShell equivalent asks for the password, it never contains one.
        editor.CopyPowerShellCommand.Execute(null);
        var script = wizard.Harness.Clipboard.Text!;
        Assert.Contains("New-ADUser", script, StringComparison.Ordinal);
        Assert.Contains("Read-Host -AsSecureString", script, StringComparison.Ordinal);
        Assert.All(creates, o => Assert.DoesNotContain(o.Changes.First(c => c.Action == ChangeAction.ResetPassword).Password!, script, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DefaultSchema_TheOperatorSheetIsCreatedToo()
    {
        // The UI tests' context has the default schema (no bulk-create attributes): the wizard works there as well.
        using var wizard = await B4Wizard.CreateAsync(bulkCreateSchema: false);
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        Assert.Null(wizard.Check.Error);
        Assert.False(wizard.Check.Computation!.HasRunErrors, string.Join(" ", wizard.Check.RunIssues));

        await wizard.PreviewAndExecuteAsync();

        Assert.Equal(5, wizard.Editor.CreatedAccounts.Count(a => a.HasPassword));
    }

    [Fact]
    public async Task Preview_AValueTakenSinceTheCheck_IsAnErrorOfItsRow_NotRenumbered()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        wizard.Harness.Builder.AddUser("Ktoś", "Inny", wizard.Harness.Company.SalesOu, new Dictionary<string, object?> { [Attr.SamAccountName] = "kd209" });

        await wizard.Editor.PreviewCommand.ExecuteAsync(null);

        var editor = wizard.Editor;
        Assert.True(editor.IsPreviewStep, editor.Error);
        Assert.Equal(1, editor.ErrorCount);
        Assert.Equal(4, editor.ChangeCount);
        Assert.Equal(["kd208", "kd209", "kd210", "kd211", "kd212"], editor.PreviewObjects.Select(o => Value(o.Plan, Attr.SamAccountName)));
        // Back in the check step the row shows the error.
        editor.BackToEditCommand.Execute(null);
        Assert.Equal(BulkRowStatus.Error, wizard.Check.Rows[1].Status);
    }

    [Fact]
    public async Task Execute_IsConfirmedByTyping_RunsACanaryThenTheRest_AndListsThePasswordsMasked()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        var activities = 0;
        wizard.Harness.Activities.Activities.CollectionChanged += (_, e) => activities += e.NewItems?.Count ?? 0;

        await wizard.PreviewAndExecuteAsync();

        var editor = wizard.Editor;
        var (_, message, expected) = Assert.Single(wizard.Harness.Dialogs.TypedConfirmations);
        Assert.Equal("5", expected);
        Assert.Contains("obiekty do utworzenia w domenie", message, StringComparison.Ordinal);
        Assert.Contains("Hasła początkowe będą dostępne tylko w tym oknie", message, StringComparison.Ordinal);
        Assert.DoesNotContain(wizard.Harness.Dialogs.Shown, s => s.Contains(wizard.Harness.Localizer[L.Batch.ReplacePasswords], StringComparison.Ordinal));
        Assert.Equal(2, activities);
        Assert.Equal(5, editor.Result!.SucceededCount);

        // The accounts exist with their values, disabled until the password, then enabled with must-change.
        var dn = wizard.Bulk.CreatedRows[1];
        Assert.Equal("kd208", wizard.Harness.Value(dn, Attr.SamAccountName));
        Assert.Equal("kd208@contoso.com", wizard.Harness.Value(dn, Attr.UserPrincipalName));
        Assert.Equal("+48 600 100 201", wizard.Harness.Value(dn, Attr.Mobile));
        Assert.Equal("Łukasz", wizard.Harness.Value(dn, Attr.GivenName));
        Assert.Equal("0", wizard.Harness.Value(dn, Attr.PwdLastSet));
        Assert.Equal("512", wizard.Harness.Value(dn, Attr.UserAccountControl));
        Assert.Contains(wizard.Harness.Values(wizard.Sample.GroupNew, Attr.Member), m => Core.Ldap.DistinguishedName.AreEqual(m, dn));

        Assert.Equal(5, editor.CreatedAccounts.Count);
        var first = editor.CreatedAccounts[0];
        Assert.Equal("kd208", first.Login);
        Assert.Equal("kd208@contoso.com", first.Upn);
        Assert.Equal("lukasz.wojcik@firma.pl", first.Mail);
        Assert.Equal("Łukasz Wójcik", first.Name);
        Assert.Equal("Utworzono", first.Status);
        Assert.Equal(CreatedAccountViewModel.Mask, first.PasswordText);
        Assert.DoesNotContain(first.Password!, first.ToString(), StringComparison.Ordinal);
        Assert.True(editor.HasUnsavedGeneratedPasswords);
        Assert.Contains(wizard.Bulk.ResultNotes, n => n.StartsWith("Konta utworzono na kontrolerze", StringComparison.Ordinal));

        // Created rows are marked in the check step.
        await wizard.BackToCheckAsync();
        Assert.All(wizard.Check.Rows, r => Assert.Equal(BulkRowStatus.Created, r.Status));
        Assert.Contains("utworzone: 5", wizard.Check.Summary, StringComparison.Ordinal);
        Assert.False(wizard.Bulk.IsReadyToPlan);

        // Audit has the creations, never a password.
        var passwords = editor.CreatedAccounts.Select(a => a.Password!).ToList();
        Assert.NotEmpty(wizard.Harness.AuditLog.Entries);
        Assert.All(wizard.Harness.AuditLog.Entries, e => Assert.All(passwords, p => Assert.DoesNotContain(p, e.ToString() ?? string.Empty, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Canary_ASystematicFailure_StopsAfterOneAccount_AndTheOtherRowsStayNotCreated()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        await wizard.Editor.PreviewCommand.ExecuteAsync(null);

        // The channel is no longer encrypted when the plan runs: the first reset is refused (StrongAuthRequired).
        wizard.Harness.Session.IsChannelEncrypted = false;
        wizard.Harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await wizard.Editor.ExecuteCommand.ExecuteAsync(null);

        var editor = wizard.Editor;
        Assert.True(editor.IsResultsStep, editor.Error);
        Assert.True(wizard.Bulk.WasStoppedAfterCanary);
        Assert.Equal(wizard.Harness.Localizer[L.BulkCreateCheck.CanaryStopped], wizard.Bulk.ResultNotes[0]);
        Assert.Equal([1], wizard.Bulk.CreatedRows.Keys);
        Assert.Equal(ObjectResultStatus.PartiallySucceeded, editor.Result!.Objects[0].Status);
        Assert.All(editor.Result.Objects.Skip(1), o => Assert.Equal(ObjectResultStatus.Cancelled, o.Status));
        var account = Assert.Single(editor.CreatedAccounts);
        Assert.False(account.HasPassword);
        Assert.Contains(wizard.Harness.Localizer[L.BulkCreateCheck.DisabledWithoutPassword], account.Message, StringComparison.Ordinal);
        Assert.Equal("514", wizard.Harness.Value(wizard.Bulk.CreatedRows[1], Attr.UserAccountControl));
        Assert.False(editor.HasUnsavedGeneratedPasswords);

        // Fixed: the retry runs the canary's password and the rows that never ran – no second Create for the canary.
        wizard.Harness.Session.IsChannelEncrypted = true;
        await editor.RetryFailedCommand.ExecuteAsync(null);
        Assert.True(editor.IsPreviewStep, editor.Error);
        var retry = editor.PreviewObjects.Select(o => o.Plan).ToList();
        Assert.Equal(5, retry.Count);
        Assert.DoesNotContain(retry[0].Changes, c => c.Action == ChangeAction.Create);
        Assert.All(retry.Skip(1), o => Assert.Equal(ChangeAction.Create, o.Changes[0].Action));
        wizard.Harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await editor.ExecuteCommand.ExecuteAsync(null);

        Assert.True(editor.IsResultsStep, editor.Error);
        Assert.Equal(5, editor.Result!.SucceededCount);
        Assert.Equal(5, editor.CreatedAccounts.Count);
        Assert.All(editor.CreatedAccounts, a => Assert.True(a.HasPassword));
        Assert.Equal("512", wizard.Harness.Value(wizard.Bulk.CreatedRows[1], Attr.UserAccountControl));
        Assert.False(wizard.Bulk.WasStoppedAfterCanary);
    }

    [Fact]
    public async Task Canary_AValueTakenMeanwhile_HandsOverToTheNextRow()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        await wizard.Editor.PreviewCommand.ExecuteAsync(null);
        wizard.Harness.Builder.AddUser("Ktoś", "Szybszy", wizard.Harness.Company.SalesOu, new Dictionary<string, object?> { [Attr.SamAccountName] = "kd208" });

        wizard.Harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await wizard.Editor.ExecuteCommand.ExecuteAsync(null);

        var result = wizard.Editor.Result!;
        Assert.False(wizard.Bulk.WasStoppedAfterCanary);
        Assert.Equal(4, result.SucceededCount);
        Assert.Equal(ObjectResultStatus.Failed, result.Objects.Single(o => o.Plan.SourceRow == 1).Status);
        Assert.Equal([2, 3, 4, 5], wizard.Bulk.CreatedRows.Keys.Order());
    }

    [Fact]
    public async Task PasswordCopy_UsesTheProtectedClipboard_AndHandsThePasswordOut()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        await wizard.PreviewAndExecuteAsync();
        var accounts = wizard.Editor.CreatedAccounts;

        accounts[0].CopyPasswordCommand.Execute(null);
        Assert.True(wizard.Harness.Clipboard.IsSecret);
        Assert.Equal(accounts[0].Password, wizard.Harness.Clipboard.Text);
        Assert.True(accounts[0].WasHandedOut);

        accounts[1].IsPasswordVisible = true;
        Assert.Equal(accounts[1].Password, accounts[1].PasswordText);
        Assert.True(accounts[1].WasHandedOut);
        Assert.True(wizard.Editor.HasUnsavedGeneratedPasswords);

        // A busy clipboard hands nothing out.
        wizard.Harness.Clipboard.IsBusy = true;
        accounts[2].CopyPasswordCommand.Execute(null);
        Assert.False(accounts[2].WasHandedOut);
    }

    [Fact]
    public async Task CopyTableWithPasswords_AsksFirst_ProtectsFormulas_AndAddsThePhoneColumn()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        await wizard.PreviewAndExecuteAsync();
        var bulk = wizard.Bulk;
        var clipboard = wizard.Harness.Clipboard;

        wizard.Harness.Dialogs.ConfirmAnswers.Enqueue(false);
        await bulk.CopyTableWithPasswordsCommand.ExecuteAsync(null);
        Assert.Null(clipboard.Text);
        Assert.True(wizard.Editor.HasUnsavedGeneratedPasswords);

        Assert.Equal(["Telefon komórkowy"], bulk.CopyColumns.Where(c => c.IsSelected).Select(c => c.Header));
        wizard.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await bulk.CopyTableWithPasswordsCommand.ExecuteAsync(null);

        Assert.True(clipboard.IsSecret);
        var lines = clipboard.Text!.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Nr\tNazwa\tNazwa logowania\tUPN\tAdres e-mail\tTelefon komórkowy\tHasło początkowe", lines[0]);
        var accounts = wizard.Editor.CreatedAccounts;
        Assert.Equal($"2\tŁukasz Wójcik\tkd208\tkd208@contoso.com\tlukasz.wojcik@firma.pl\t'+48 600 100 201\t{Protected(accounts[0].Password!)}", lines[1]);
        Assert.Equal(6, lines.Length);
        Assert.All(accounts, a => Assert.True(a.WasHandedOut));
        Assert.False(wizard.Editor.HasUnsavedGeneratedPasswords);
    }

    [Fact]
    public async Task CopyTableWithoutPasswords_UsesPlainText_AndHandsNothingOut()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        await wizard.PreviewAndExecuteAsync();

        wizard.Bulk.CopyTableWithoutPasswordsCommand.Execute(null);

        var text = wizard.Harness.Clipboard.Text!;
        Assert.False(wizard.Harness.Clipboard.IsSecret);
        Assert.Contains("kd212@contoso.com", text, StringComparison.Ordinal);
        Assert.All(wizard.Editor.CreatedAccounts, a => Assert.DoesNotContain(a.Password!, text, StringComparison.Ordinal));
        Assert.True(wizard.Editor.HasUnsavedGeneratedPasswords);
    }

    [Fact]
    public async Task Export_WritesThePasswordsOnlyAfterTheWarning_AndThenTheWindowClosesWithoutAsking()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        await wizard.PreviewAndExecuteAsync();
        var editor = wizard.Editor;
        var path = Path.Combine(Path.GetTempPath(), $"admng-b4-{Guid.NewGuid():N}.csv");
        try
        {
            wizard.Harness.Files.NextSavePath = path;
            wizard.Harness.Dialogs.ConfirmAnswers.Enqueue(false);
            await editor.ExportResultsCommand.ExecuteAsync(null);
            var withoutPasswords = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.All(editor.CreatedAccounts, a => Assert.DoesNotContain(a.Password!, withoutPasswords, StringComparison.Ordinal));
            Assert.True(editor.HasUnsavedGeneratedPasswords);

            // Closing now asks; declining keeps the window.
            wizard.Harness.Dialogs.ConfirmAnswers.Enqueue(false);
            Assert.False(await editor.CanCloseAsync());

            wizard.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
            await editor.ExportResultsCommand.ExecuteAsync(null);
            var withPasswords = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.All(editor.CreatedAccounts, a => Assert.Contains(a.Password!, withPasswords, StringComparison.Ordinal));
            Assert.False(editor.HasUnsavedGeneratedPasswords);
            var questions = wizard.Harness.Dialogs.Shown.Count;
            Assert.True(await editor.CanCloseAsync());
            Assert.Equal(questions, wizard.Harness.Dialogs.Shown.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Retry_APasswordPolicyFailure_ReadsTheNewPasswordBoxes_AndRunsOnlyThePasswordAndTheRest()
    {
        const string Refused = "Wiosna2026!x";
        const string Accepted = "Lato#2026xyz";
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput(new BulkAccountOptions { PasswordMode = InitialPasswordMode.SameForAll, GroupDns = [wizard.Sample.GroupNew] }));
        var typed = Refused;
        wizard.Bulk.SharedPasswordReader = () => typed;
        wizard.Harness.Session.PasswordPolicy = p => p != Refused;
        await wizard.EnterCheckAsync();
        await wizard.Editor.PreviewCommand.ExecuteAsync(null);
        var editor = wizard.Editor;
        Assert.True(editor.IsPreviewStep, editor.Error);
        wizard.Harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await editor.ExecuteCommand.ExecuteAsync(null);

        // The canary's password was refused: stopped after one account, which stays disabled without its groups.
        Assert.True(wizard.Bulk.WasStoppedAfterCanary);
        Assert.True(wizard.Bulk.NeedsRetryPassword);
        Assert.False(editor.HasUnsavedGeneratedPasswords);
        var canaryDn = wizard.Bulk.CreatedRows[1];
        Assert.Equal("514", wizard.Harness.Value(canaryDn, Attr.UserAccountControl));

        // No new password typed: the retry says what is missing; a mismatch too.
        await editor.RetryFailedCommand.ExecuteAsync(null);
        Assert.Equal(wizard.Harness.Localizer[L.BulkCreateCheck.RetryPasswordRequired], editor.Error);
        wizard.Bulk.RetryPasswordInput = new TestPasswordInput(Accepted);
        wizard.Bulk.RetryConfirmPasswordInput = new TestPasswordInput(Accepted + "!");
        await editor.RetryFailedCommand.ExecuteAsync(null);
        Assert.Equal(wizard.Harness.Localizer[L.Batch.PasswordMismatch], editor.Error);
        Assert.True(editor.IsResultsStep);

        var first = new TestPasswordInput(Accepted);
        var second = new TestPasswordInput(Accepted);
        wizard.Bulk.RetryPasswordInput = first;
        wizard.Bulk.RetryConfirmPasswordInput = second;
        await editor.RetryFailedCommand.ExecuteAsync(null);

        Assert.True(editor.IsPreviewStep, editor.Error);
        Assert.True(first.Cleared && second.Cleared);
        var plans = editor.PreviewObjects.Select(o => o.Plan).ToList();
        Assert.Equal([ChangeAction.ResetPassword, ChangeAction.Modify, ChangeAction.AddMember], plans[0].Changes.Select(c => c.Action));
        Assert.All(plans.SelectMany(o => o.Changes).Where(c => c.Action == ChangeAction.ResetPassword), c => Assert.Equal(Accepted, c.Password));
        Assert.Null(plans[0].PowerShell);

        wizard.Harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await editor.ExecuteCommand.ExecuteAsync(null);

        Assert.True(editor.IsResultsStep, editor.Error);
        Assert.Equal(5, editor.Result!.SucceededCount);
        Assert.False(wizard.Bulk.NeedsRetryPassword);
        Assert.Equal("512", wizard.Harness.Value(canaryDn, Attr.UserAccountControl));
        Assert.Equal(1, wizard.Harness.Session.Operations.Count(o => o.Kind == FakeOperationKind.Add && Core.Ldap.DistinguishedName.AreEqual(o.DistinguishedName, canaryDn)));
        // A shared password the operator typed does not keep the window open.
        Assert.False(editor.HasUnsavedGeneratedPasswords);
    }

    [Fact]
    public async Task SharedPassword_IsReadAfterTheComputation_AndEmptyBoxesAskToTypeItAgain()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput(new BulkAccountOptions { PasswordMode = InitialPasswordMode.SameForAll }));
        var reads = 0;
        string? typed = null;
        wizard.Bulk.SharedPasswordReader = () =>
        {
            reads++;
            Assert.False(wizard.Check.IsComputing);
            return typed;
        };
        await wizard.EnterCheckAsync();

        await wizard.Editor.PreviewCommand.ExecuteAsync(null);

        Assert.Equal(1, reads);
        Assert.True(wizard.Editor.IsEditStep);
        Assert.Equal(wizard.Harness.Localizer[L.BulkCreateCheck.ReenterPassword], wizard.Editor.Error);

        typed = "Jesień#2026zz";
        await wizard.Editor.PreviewCommand.ExecuteAsync(null);
        Assert.True(wizard.Editor.IsPreviewStep, wizard.Editor.Error);
        Assert.All(wizard.Editor.PreviewObjects.SelectMany(o => o.Plan.Changes).Where(c => c.Action == ChangeAction.ResetPassword), c => Assert.Equal(typed, c.Password));
        Assert.Equal(InitialPasswordMode.SameForAll, wizard.Bulk.PasswordModeAtPlan);
    }

    [Fact]
    public async Task Undo_DeletesEverythingThisWindowCreated_OverAllExecutions_AndARerunGivesTheSameNumbers()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        wizard.Check.Rows[4].IsIncluded = false;
        await B2Harness.WaitUntilAsync(() => !wizard.Check.IsComputing && wizard.Check.Computation!.Rows[4].Status == BulkRowStatus.Excluded);
        await wizard.PreviewAndExecuteAsync();
        Assert.Equal(4, wizard.Bulk.CreatedRows.Count);

        // A second plan after Wstecz: the excluded row is included again and created by a second execution.
        await wizard.BackToCheckAsync();
        wizard.Check.Rows[4].IsIncluded = true;
        await B2Harness.WaitUntilAsync(() => !wizard.Check.IsComputing && wizard.Check.Computation!.Rows[4].Status == BulkRowStatus.Ok);
        Assert.Equal("kd212", wizard.Check.Rows[4].Cell(Attr.SamAccountName)!.Value);
        await wizard.PreviewAndExecuteAsync();
        var editor = wizard.Editor;
        Assert.Equal(5, wizard.Bulk.CreatedRows.Count);
        Assert.Equal(5, editor.Result!.SucceededCount);
        Assert.Equal(5, editor.CreatedAccounts.Count);
        var created = wizard.Bulk.CreatedRows.Values.ToList();
        await B2Harness.WaitUntilAsync(() => wizard.Bulk.CreatedObjects.Count == 5 && wizard.Bulk.CreatedObjects.All(o => o.ObjectGuid is not null));

        editor.UndoCommand.Execute(null);
        await B2Harness.WaitUntilAsync(() => editor.IsPreviewStep && editor.UndoNote is not null);

        Assert.True(editor.IsRollbackPreview);
        Assert.Equal(wizard.Harness.Localizer.Format(L.BulkCreateCheck.UndoNote, 5), editor.UndoNote);
        Assert.All(editor.PreviewObjects, o => Assert.Equal([ChangeAction.Delete], o.Plan.Changes.Select(c => c.Action)));
        wizard.Harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await editor.ExecuteCommand.ExecuteAsync(null);

        Assert.True(editor.IsResultsStep, editor.Error);
        Assert.All(created, dn => Assert.Null(wizard.Harness.Session.GetValues(dn, Attr.SamAccountName).FirstOrDefault()));
        Assert.Empty(wizard.Bulk.CreatedRows);
        Assert.All(editor.CreatedAccounts, a => Assert.True(a.IsUndone));
        Assert.All(editor.CreatedAccounts, a => Assert.Equal("Usunięte (cofnięte)", a.Status));
        Assert.All(editor.CreatedAccounts, a => Assert.False(a.HasPassword));
        Assert.False(editor.HasUnsavedGeneratedPasswords);
        Assert.Null(wizard.Bulk.Accumulated);
        Assert.False(editor.RetryFailedCommand.CanExecute(null));

        // A re-run after the undo gives the same numbers (the window's own deleted objects do not block them).
        await wizard.BackToCheckAsync();
        await wizard.Check.RecheckCommand.ExecuteAsync(null);
        Assert.Equal(["kd208", "kd209", "kd210", "kd211", "kd212"], wizard.Check.Rows.Select(r => r.Cell(Attr.SamAccountName)!.Value));
        Assert.All(wizard.Check.Rows, r => Assert.NotEqual(BulkRowStatus.Created, r.Status));
    }

    [Fact]
    public async Task Execute_ADeclinedConfirmation_CreatesNothing()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        await wizard.Editor.PreviewCommand.ExecuteAsync(null);
        wizard.Harness.Dialogs.TypedConfirmAnswers.Enqueue(false);
        await wizard.Editor.ExecuteCommand.ExecuteAsync(null);
        Assert.True(wizard.Editor.IsPreviewStep);
        Assert.Empty(wizard.Bulk.CreatedRows);
        Assert.DoesNotContain(wizard.Harness.Session.Operations, o => o.Kind == FakeOperationKind.Add);
    }

    [Fact]
    public async Task Canary_ARefusedCreate_StopsWithNothingCreated_AndTheUndoSaysSo()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        await wizard.Editor.PreviewCommand.ExecuteAsync(null);
        wizard.Harness.Session.FailNext(FakeOperationKind.Add, null, Core.Directory.DirectoryErrorKind.AccessDenied);

        wizard.Harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await wizard.Editor.ExecuteCommand.ExecuteAsync(null);

        Assert.True(wizard.Bulk.WasStoppedAfterCanary);
        Assert.Empty(wizard.Bulk.CreatedRows);
        Assert.Empty(wizard.Editor.CreatedAccounts);
        Assert.Equal(1, wizard.Harness.Session.Operations.Count(o => o.Kind == FakeOperationKind.Add));
        wizard.Editor.UndoCommand.Execute(null);
        Assert.Contains(wizard.Harness.Notifications.Items, n => n.Message == wizard.Harness.Localizer[L.Batch.NothingToUndo]);
        Assert.True(wizard.Editor.IsResultsStep);
    }

    [Fact]
    public async Task Dispose_DropsThePasswordsAndTheResults()
    {
        var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        await wizard.PreviewAndExecuteAsync();
        var accounts = wizard.Editor.CreatedAccounts.ToList();
        var bulk = wizard.Bulk;

        wizard.Dispose();

        Assert.All(accounts, a => Assert.False(a.HasPassword));
        Assert.Null(bulk.Accumulated);
        Assert.Empty(bulk.CreatedObjects);
        Assert.Empty(wizard.Editor.CreatedAccounts);
        Assert.Null(wizard.Editor.Result);
    }

    [Fact]
    public async Task SessionEnded_StopsPlanning_AndKeepsTheResultsReadable()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        await wizard.PreviewAndExecuteAsync();

        await wizard.Harness.Sessions.DisconnectAsync();
        await B2Harness.WaitUntilAsync(() => wizard.Editor.IsSessionEnded);

        Assert.False(wizard.Editor.RetryFailedCommand.CanExecute(null));
        Assert.Equal(5, wizard.Editor.CreatedAccounts.Count);
        Assert.True(wizard.Editor.HasUnsavedGeneratedPasswords);
        await wizard.BackToCheckAsync();
        Assert.False(wizard.Editor.PreviewCommand.CanExecute(null));
    }

    [Fact]
    public async Task Undo_ThenARerun_ThenARetry_ListsEveryAccountOnce()
    {
        using var wizard = await B4Wizard.CreateAsync();
        var editor = wizard.Editor;
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        await wizard.PreviewAndExecuteAsync();
        await B2Harness.WaitUntilAsync(() => wizard.Bulk.CreatedObjects.Count == 5 && wizard.Bulk.CreatedObjects.All(o => o.ObjectGuid is not null));
        editor.UndoCommand.Execute(null);
        await B2Harness.WaitUntilAsync(() => editor.IsPreviewStep && editor.UndoNote is not null);
        wizard.Harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await editor.ExecuteCommand.ExecuteAsync(null);
        Assert.Empty(wizard.Bulk.CreatedRows);

        // The same rows created again (one of them partially): the undone accounts and the new ones share their row numbers.
        await wizard.BackToCheckAsync();
        await wizard.Check.RecheckCommand.ExecuteAsync(null);
        await editor.PreviewCommand.ExecuteAsync(null);
        Assert.True(editor.IsPreviewStep, editor.Error);
        wizard.Harness.Session.FailNext(FakeOperationKind.Modify, wizard.Check.Computation!.Rows[2].DistinguishedName!, Core.Directory.DirectoryErrorKind.AccessDenied);
        wizard.Harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await editor.ExecuteCommand.ExecuteAsync(null);
        Assert.True(editor.IsResultsStep, editor.Error);
        Assert.Equal(5, editor.CreatedAccounts.Count(a => !a.IsUndone && a.HasPassword));

        // A third execution of the window (the retry) merges into that list again – each row's live account once.
        await editor.RetryFailedCommand.ExecuteAsync(null);
        Assert.True(editor.IsPreviewStep, editor.Error);
        wizard.Harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await editor.ExecuteCommand.ExecuteAsync(null);

        Assert.True(editor.IsResultsStep, editor.Error);
        Assert.Equal(5, editor.Result!.SucceededCount);
        Assert.Equal([1, 2, 3, 4, 5], editor.CreatedAccounts.Where(a => !a.IsUndone).Select(a => a.Row));
        Assert.Equal(5, editor.CreatedAccounts.Count(a => a.IsUndone));
    }

    [Fact]
    public async Task ANewTable_AfterTheFirstWasCreated_IsCheckedAndCreatedOnItsOwn_AndTheUndoCoversBoth()
    {
        using var wizard = await B4Wizard.CreateAsync();
        var editor = wizard.Editor;
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        await wizard.PreviewAndExecuteAsync();
        var first = editor.CreatedAccounts.ToDictionary(a => a.Login!, a => a.Password);
        Assert.Equal(["kd208", "kd209", "kd210", "kd211", "kd212"], first.Keys);

        // Wstecz → Wklej i zastąp: the next sheet uses the row numbers 1–5 again.
        editor.BackToEditCommand.Execute(null);
        wizard.Check.OnInputReset();
        wizard.Use(wizard.OperatorInput() with { Table = B4Wizard.TableWithHeaders(B4Wizard.OperatorHeaders, SecondSheet) });
        await wizard.Check.OnEnteredAsync();

        var check = wizard.Check;
        Assert.Null(check.Error);
        Assert.Empty(wizard.Bulk.CreatedRows);
        Assert.All(check.Rows, r => Assert.NotEqual(BulkRowStatus.Created, r.Status));
        Assert.Equal(["kd213", "kd214", "kd215", "kd216", "kd217"], check.Rows.Select(r => r.Cell(Attr.SamAccountName)!.Value));

        await wizard.PreviewAndExecuteAsync();
        Assert.Equal(10, editor.Result!.SucceededCount);
        Assert.Equal(10, editor.CreatedAccounts.Count);
        Assert.Equal(10, editor.CreatedAccounts.Select(a => a.Login).Distinct().Count());
        Assert.All(first, p => Assert.Equal(p.Value, Assert.Single(editor.CreatedAccounts, a => a.Login == p.Key).Password));
        Assert.Equal(5, wizard.Bulk.CreatedRows.Count);
        Assert.All(check.Rows, r => Assert.Equal(BulkRowStatus.Created, r.Status));

        // The undo deletes both tables' accounts.
        await B2Harness.WaitUntilAsync(() => wizard.Bulk.CreatedObjects.Count == 10 && wizard.Bulk.CreatedObjects.All(o => o.ObjectGuid is not null));
        var dns = wizard.Bulk.CreatedObjects.Select(o => o.DistinguishedName).ToList();
        editor.UndoCommand.Execute(null);
        await B2Harness.WaitUntilAsync(() => editor.IsPreviewStep && editor.UndoNote is not null);
        Assert.Equal(wizard.Harness.Localizer.Format(L.BulkCreateCheck.UndoNote, 10), editor.UndoNote);
        wizard.Harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await editor.ExecuteCommand.ExecuteAsync(null);

        Assert.True(editor.IsResultsStep, editor.Error);
        Assert.All(dns, dn => Assert.Empty(wizard.Harness.Session.GetValues(dn, Attr.ObjectClass)));
        Assert.All(editor.CreatedAccounts, a => Assert.True(a.IsUndone));
        Assert.Equal(0, wizard.Bulk.CreatedCount);
    }

    [Fact]
    public async Task Undo_AfterTheGuidReadFailed_ReadsThemAgain_AndDeletesEveryCreatedObject()
    {
        using var wizard = await B4Wizard.CreateAsync();
        var editor = wizard.Editor;
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();

        // The read of objectGUID and whenCreated right after the execution fails as a whole (the DC was briefly unreachable).
        var failed = 0;
        wizard.Harness.Session.BeforeOperation = operation =>
        {
            if (operation is { Kind: FakeOperationKind.Search, Search: { Scope: Core.Directory.SearchScope.OneLevel } search }
                && search.Attributes.Contains(Attr.WhenCreated, StringComparer.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref failed, 1) == 0)
            {
                throw new Core.Directory.DirectoryException(Core.Directory.DirectoryErrorKind.Timeout, "The domain controller did not answer.");
            }
        };
        await wizard.PreviewAndExecuteAsync();
        Assert.Equal(5, wizard.Bulk.CreatedRows.Count);
        var created = wizard.Bulk.CreatedRows.Values.ToList();

        editor.UndoCommand.Execute(null);
        await B2Harness.WaitUntilAsync(() => editor.IsPreviewStep && editor.UndoNote is not null);

        Assert.Equal(1, failed);
        Assert.Equal(5, editor.PreviewObjects.Count);
        Assert.Equal(wizard.Harness.Localizer.Format(L.BulkCreateCheck.UndoNote, 5), editor.UndoNote);
        wizard.Harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await editor.ExecuteCommand.ExecuteAsync(null);
        Assert.All(created, dn => Assert.Empty(wizard.Harness.Session.GetValues(dn, Attr.ObjectClass)));
    }

    private static string? Value(ObjectPlan plan, string attribute) =>
        plan.Changes.First(c => c.Action == ChangeAction.Create).Attributes
            .FirstOrDefault(a => string.Equals(a.Attribute, attribute, StringComparison.OrdinalIgnoreCase))?.NewValues.FirstOrDefault();

    private static string Protected(string value) => Core.Csv.CsvCodec.FormatField(value, '\t', protectFormulas: true);
}
