using System.Globalization;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Dialogs;

public class BatchEditorTests
{
    private static async Task<(BatchEditorViewModel Editor, B2Harness Harness)> OpenAsync(Func<SampleCompany, IReadOnlyList<string>> targets, params BatchOperation[] operations)
    {
        var harness = await B2Harness.CreateAsync();
        var editor = harness.Factory.CreateBatchEditor(new BatchEditorRequest(targets(harness.Company)) { Operations = operations });
        await editor.InitializeAsync();
        return (editor, harness);
    }

    private static T Add<T>(BatchEditorViewModel editor, OperationType type)
        where T : OperationEditorViewModel
    {
        editor.AddOperationCommand.Execute(type);
        return Assert.IsType<T>(editor.Operations[^1]);
    }

    [Fact]
    public async Task Targets_AreResolved_AndAttributeOptionsComeFromTheSchema()
    {
        var (editor, _) = await OpenAsync(c => [c.JanKowalski, c.LukaszWisniewski, c.GroupIt]);

        Assert.Equal("Batch edit - 3 objects", editor.WindowTitle);
        Assert.Equal(["Jan Kowalski", "Łukasz Wiśniewski", "GG_IT"], editor.Targets.Select(t => t.Name));
        Assert.Equal("User: 2, Group: 1", editor.TargetSummary);
        Assert.Contains(editor.AttributeOptions, o => o.LdapName == Attr.Department);
        Assert.Contains(editor.AttributeOptions, o => o.LdapName == Attr.Member);
        Assert.DoesNotContain(editor.AttributeOptions, o => o.LdapName == Attr.ObjectGuid);
        Assert.DoesNotContain(editor.AttributeOptions, o => o.LdapName == Attr.Cn);
        Assert.Equal(BatchEditorStep.Edit, editor.Step);
        Assert.False(editor.PreviewCommand.CanExecute(null));
    }

    [Fact]
    public async Task SetAttribute_PreviewExecuteResults_EndToEnd()
    {
        var (editor, harness) = await OpenAsync(c => [c.JanKowalski, c.LukaszWisniewski, c.MalgorzataWojcik]);
        var set = Add<SetAttributeEditorViewModel>(editor, OperationType.SetAttribute);
        set.Attribute = "Department [department]";
        set.ValueTemplate = "IT";
        Assert.True(set.IsValid);

        await editor.PreviewCommand.ExecuteAsync(null);

        Assert.Equal(BatchEditorStep.Preview, editor.Step);
        Assert.Equal(3, editor.ObjectCount);
        Assert.Equal(1, editor.ChangeCount);
        Assert.Equal(2, editor.NoChangeCount);
        var row = Assert.Single(editor.PreviewRows, r => r.IsChange);
        Assert.Equal("Małgorzata Wójcik", row.Name);
        Assert.Equal("Księgowość", row.Before);
        Assert.Equal("IT", row.After);
        Assert.Equal("Modify", row.Action);
        editor.PreviewFilter = PreviewFilter.NoChange;
        Assert.Equal(2, editor.PreviewRows.Count);
        Assert.All(editor.PreviewRows, r => Assert.Equal("No change", r.Action));

        await editor.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal(BatchEditorStep.Results, editor.Step);
        Assert.Empty(harness.Dialogs.TypedConfirmations);
        Assert.Equal("IT", harness.Value(harness.Company.MalgorzataWojcik, Attr.Department));
        Assert.Equal(1, editor.Result!.SucceededCount);
        Assert.Equal("Succeeded: 1, failed: 0, skipped: 2", editor.ResultSummary);
        Assert.NotNull(editor.CurrentActivity);
        Assert.False(editor.CurrentActivity!.IsRunning);
        Assert.Contains(harness.AuditLog.Entries, e => e.Attribute == Attr.Department && e.NewValue == "IT");
        var history = Assert.Single(harness.PowerShell.Entries);
        Assert.Contains("Set-ADObject", history.Script, StringComparison.Ordinal);
        Assert.Contains(harness.Notifications.Items, n => n.Severity == NotificationSeverity.Success);
    }

    [Fact]
    public async Task TemplateSample_IsRenderedForTheFirstTarget()
    {
        var (editor, harness) = await OpenAsync(c => [c.LukaszWisniewski, c.JanKowalski]);
        var set = Add<SetAttributeEditorViewModel>(editor, OperationType.SetAttribute);
        set.Attribute = Attr.Mail;

        set.ValueTemplate = "{givenName|nodiacritics|lower}.{sn|nodiacritics|lower}@firma.pl";
        await B2Harness.WaitUntilAsync(() => set.Sample == "lukasz.wisniewski@firma.pl");

        Assert.Equal("lukasz.wisniewski@firma.pl", set.Sample);
        set.ValueTemplate = "{givenName|";
        Assert.Equal(harness.Localizer[L.Batch.InvalidTemplate], set.Error);
        Assert.False(editor.PreviewCommand.CanExecute(null));
    }

    [Fact]
    public async Task LargeOrRiskyBatches_NeedTypedConfirmation()
    {
        var (editor, harness) = await OpenAsync(c => [c.JanKowalski, c.LukaszWisniewski]);
        var move = Add<MoveEditorViewModel>(editor, OperationType.Move);
        harness.Dialogs.PickedContainers.Enqueue(harness.Company.SalesOu);
        await move.BrowseCommand.ExecuteAsync(null);
        Assert.Equal("contoso.test/Firma/Pracownicy/Sprzedaż", move.TargetPath);
        await editor.PreviewCommand.ExecuteAsync(null);

        harness.Dialogs.TypedConfirmAnswers.Enqueue(false);
        await editor.ExecuteCommand.ExecuteAsync(null);

        var confirmation = Assert.Single(harness.Dialogs.TypedConfirmations);
        Assert.Equal("2", confirmation.Expected);
        Assert.Equal(BatchEditorStep.Preview, editor.Step);
        Assert.True(harness.Session.Exists(harness.Company.JanKowalski));

        harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await editor.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(BatchEditorStep.Results, editor.Step);
        Assert.False(harness.Session.Exists(harness.Company.JanKowalski));
        Assert.True(harness.Session.Exists($"CN=Jan Kowalski,{harness.Company.SalesOu}"));
    }

    [Fact]
    public async Task ThresholdAppliesToSimpleChangesToo()
    {
        var (editor, harness) = await OpenAsync(c => [c.JanKowalski, c.LukaszWisniewski]);
        await harness.Settings.UpdateAsync(s => s.ConfirmationThreshold = 1, TestContext.Current.CancellationToken);
        Add<SetAttributeEditorViewModel>(editor, OperationType.SetAttribute).Also(o => { o.Attribute = Attr.Company; o.ValueTemplate = "Fabrikam"; });
        await editor.PreviewCommand.ExecuteAsync(null);

        harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await editor.ExecuteCommand.ExecuteAsync(null);

        Assert.Single(harness.Dialogs.TypedConfirmations);
        Assert.Equal("Fabrikam", harness.Value(harness.Company.LukaszWisniewski, Attr.Company));
    }

    [Fact]
    public async Task ExcludedObjects_AreNotExecuted()
    {
        var (editor, harness) = await OpenAsync(c => [c.JanKowalski, c.LukaszWisniewski], new SetAttributeOperation(Attr.Company, "Fabrikam"));
        await editor.PreviewCommand.ExecuteAsync(null);
        Assert.Equal(2, editor.ChangeCount);

        editor.PreviewObjects.Single(o => o.Name == "Jan Kowalski").IsIncluded = false;
        Assert.Equal(1, editor.ChangeCount);
        Assert.Equal(1, editor.ExcludedCount);
        await editor.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal("Contoso Sp. z o.o.", harness.Value(harness.Company.JanKowalski, Attr.Company));
        Assert.Equal("Fabrikam", harness.Value(harness.Company.LukaszWisniewski, Attr.Company));
        Assert.Single(editor.Result!.Objects);
    }

    [Fact]
    public async Task Undo_ShowsTheRollbackPlan_AndRestoresValues()
    {
        var (editor, harness) = await OpenAsync(c => [c.JanKowalski, c.LukaszWisniewski], new SetAttributeOperation(Attr.Title, "Konsultant"));
        await editor.PreviewCommand.ExecuteAsync(null);
        await editor.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal("Konsultant", harness.Value(harness.Company.JanKowalski, Attr.Title));

        editor.UndoCommand.Execute(null);

        Assert.Equal(BatchEditorStep.Preview, editor.Step);
        Assert.True(editor.IsRollbackPreview);
        Assert.Contains(editor.PreviewRows, r => r.Before == "Konsultant" && r.After == "Administrator systemów");
        await editor.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal("Administrator systemów", harness.Value(harness.Company.JanKowalski, Attr.Title));
        Assert.Equal("Programista", harness.Value(harness.Company.LukaszWisniewski, Attr.Title));
        Assert.False(editor.RetryFailedCommand.CanExecute(null));
    }

    [Fact]
    public async Task RetryFailed_PlansOnlyTheFailedObjectsAgain()
    {
        var (editor, harness) = await OpenAsync(c => [c.JanKowalski, c.LukaszWisniewski], new SetAttributeOperation(Attr.Title, "Konsultant"));
        harness.Session.FailNext(FakeOperationKind.Modify, harness.Company.LukaszWisniewski, DirectoryErrorKind.AccessDenied);
        await editor.PreviewCommand.ExecuteAsync(null);
        await editor.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal(1, editor.Result!.FailedCount);
        Assert.Equal(ResultFilter.Failed, editor.ResultFilter);
        var failed = Assert.Single(editor.ResultRows);
        Assert.Equal("Łukasz Wiśniewski", failed.Name);
        Assert.Equal(harness.Errors.Translate(DirectoryErrorKind.AccessDenied), failed.Message);
        Assert.True(editor.RetryFailedCommand.CanExecute(null));

        await editor.RetryFailedCommand.ExecuteAsync(null);

        Assert.Equal(BatchEditorStep.Preview, editor.Step);
        Assert.Equal(1, editor.ObjectCount);
        await editor.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal("Konsultant", harness.Value(harness.Company.LukaszWisniewski, Attr.Title));
    }

    [Fact]
    public async Task ValueChangedAfterThePreview_IsReportedAsAConflict()
    {
        var harness = await B2Harness.CreateAsync();
        harness.Session.SetValues(harness.Company.JanKowalski, Attr.Title, "Przed");
        var editor = harness.Factory.CreateBatchEditor(new BatchEditorRequest([harness.Company.JanKowalski])
        {
            Operations = [new SetAttributeOperation(Attr.Title, "Architekt")],
        });
        await editor.InitializeAsync();
        await editor.PreviewCommand.ExecuteAsync(null);
        Assert.Equal(1, editor.ChangeCount);

        harness.Session.SetValues(harness.Company.JanKowalski, Attr.Title, "Zmienione w międzyczasie");
        await editor.ExecuteCommand.ExecuteAsync(null);

        var row = Assert.Single(editor.ResultRows);
        Assert.Equal(ObjectResultStatus.Failed, row.Status);
        Assert.Equal(harness.Localizer[L.Errors.Conflict], row.Message);
        Assert.Equal("Zmienione w międzyczasie", harness.Value(harness.Company.JanKowalski, Attr.Title));
    }

    [Fact]
    public async Task ResultsExport_IncludesPasswordsOnlyAfterConfirmation()
    {
        var (editor, harness) = await OpenAsync(c => [c.JanKowalski, c.LukaszWisniewski], new ResetPasswordOperation(PasswordMode.GenerateUnique));
        Assert.IsType<ResetPasswordEditorViewModel>(Assert.Single(editor.Operations));
        await editor.PreviewCommand.ExecuteAsync(null);
        Assert.Contains(editor.PreviewRows, r => r.After == "••••••••");
        harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await editor.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(2, harness.Session.PasswordResets.Count);

        // The generated passwords exist only in this window: closing asks until they were exported.
        bool? closed = null;
        editor.CloseRequested += (_, result) => closed = result;
        Assert.True(editor.HasUnsavedGeneratedPasswords);
        harness.Dialogs.ConfirmAnswers.Enqueue(false);
        await editor.CloseCommand.ExecuteAsync(null);
        Assert.Null(closed);
        Assert.Contains($"confirm:{harness.Localizer[L.Batch.DiscardPasswords]}", harness.Dialogs.Shown);

        var path = Path.Combine(Path.GetTempPath(), $"admng-test-{Guid.NewGuid():N}.csv");
        try
        {
            harness.Files.NextSavePath = path;
            harness.Dialogs.ConfirmAnswers.Enqueue(false);
            await editor.ExportResultsCommand.ExecuteAsync(null);
            var withoutPasswords = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.DoesNotContain("Password", withoutPasswords.Split('\n')[0], StringComparison.Ordinal);
            Assert.True(editor.HasUnsavedGeneratedPasswords);

            harness.Dialogs.ConfirmAnswers.Enqueue(true);
            await editor.ExportResultsCommand.ExecuteAsync(null);
            var withPasswords = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains("Password", withPasswords.Split('\n')[0], StringComparison.Ordinal);
            Assert.Contains(harness.Session.PasswordResets[0].Password, withPasswords, StringComparison.Ordinal);
            Assert.Equal(0xEF, (await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken))[0]);
            Assert.False(editor.HasUnsavedGeneratedPasswords);
            Assert.True(await editor.CanCloseAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task GeneratedPasswords_StayProtected_WhileAnUndoIsPreviewed_AndBeforeTheResultsAreReplaced()
    {
        var (editor, harness) = await OpenAsync(
            c => [c.JanKowalski],
            new ResetPasswordOperation(PasswordMode.GenerateUnique),
            new SetAttributeOperation(Attr.Title, "Konsultant"));
        await editor.PreviewCommand.ExecuteAsync(null);
        harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await editor.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(BatchEditorStep.Results, editor.Step);
        Assert.True(editor.HasUnsavedGeneratedPasswords);

        // The undo plan is only previewed: the results and their passwords are still in the window.
        editor.UndoCommand.Execute(null);
        Assert.True(editor.IsRollbackPreview);
        Assert.True(editor.HasUnsavedGeneratedPasswords);
        Assert.False(await editor.CanCloseAsync());
        Assert.Contains($"confirm:{harness.Localizer[L.Batch.DiscardPasswords]}", harness.Dialogs.Shown);

        // Executing the undo replaces the results: declined, nothing runs.
        harness.Dialogs.ConfirmAnswers.Enqueue(false);
        await editor.ExecuteCommand.ExecuteAsync(null);
        Assert.Contains($"confirm:{harness.Localizer[L.Batch.ReplacePasswords]}", harness.Dialogs.Shown);
        Assert.Equal(BatchEditorStep.Preview, editor.Step);
        Assert.Equal("Konsultant", harness.Value(harness.Company.JanKowalski, Attr.Title));

        // Accepted: the undo runs; its results hold no generated passwords.
        harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await editor.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(BatchEditorStep.Results, editor.Step);
        Assert.Equal("Administrator systemów", harness.Value(harness.Company.JanKowalski, Attr.Title));
        Assert.False(editor.HasUnsavedGeneratedPasswords);
        Assert.True(await editor.CanCloseAsync());
    }

    [Fact]
    public async Task GeneratedPasswords_StayProtected_WhenOtherOperationsArePreviewedAndExecuted()
    {
        var (editor, harness) = await OpenAsync(c => [c.JanKowalski], new ResetPasswordOperation(PasswordMode.GenerateUnique));
        await editor.PreviewCommand.ExecuteAsync(null);
        harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await editor.ExecuteCommand.ExecuteAsync(null);
        Assert.True(editor.HasUnsavedGeneratedPasswords);

        editor.BackToEditCommand.Execute(null);
        editor.RemoveOperationCommand.Execute(editor.Operations[0]);
        var set = Add<SetAttributeEditorViewModel>(editor, OperationType.SetAttribute);
        set.Attribute = Attr.Title;
        set.ValueTemplate = "Konsultant";
        await editor.PreviewCommand.ExecuteAsync(null);

        Assert.Equal(BatchEditorStep.Preview, editor.Step);
        Assert.True(editor.HasUnsavedGeneratedPasswords);
        await editor.ExecuteCommand.ExecuteAsync(null);
        Assert.Contains($"confirm:{harness.Localizer[L.Batch.ReplacePasswords]}", harness.Dialogs.Shown);
        Assert.Equal(BatchEditorStep.Preview, editor.Step);
        Assert.Equal("Administrator systemów", harness.Value(harness.Company.JanKowalski, Attr.Title));
    }

    [Fact]
    public async Task PreviewExport_AndPowerShell()
    {
        var (editor, harness) = await OpenAsync(c => [c.JanKowalski], new SetAttributeOperation(Attr.Title, "Konsultant"));
        await editor.PreviewCommand.ExecuteAsync(null);
        var path = Path.Combine(Path.GetTempPath(), $"admng-test-{Guid.NewGuid():N}.csv");
        try
        {
            harness.Files.NextSavePath = path;
            await editor.ExportPreviewCommand.ExecuteAsync(null);
            var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains("Konsultant", text, StringComparison.Ordinal);
            Assert.StartsWith("preview-", harness.Files.LastDefaultFileName, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }

        editor.CopyPowerShellCommand.Execute(null);

        Assert.Contains("Set-ADObject", harness.Clipboard.Text, StringComparison.Ordinal);
        Assert.Contains("dc01.contoso.test", harness.Clipboard.Text, StringComparison.Ordinal);
        Assert.Contains("-Server $server", harness.Clipboard.Text, StringComparison.Ordinal);
        Assert.Empty(harness.Session.Writes);
    }

    [Fact]
    public async Task PrefilledRequestWithOpenPreview_PlansImmediately()
    {
        var harness = await B2Harness.CreateAsync();
        var editor = harness.Factory.CreateBatchEditor(new BatchEditorRequest([harness.Company.JanKowalski, harness.Company.ZbigniewKazmierczak])
        {
            Operations = [new SetAccountEnabledOperation(true)],
            OpenPreview = true,
            Title = "Enable 2 accounts",
        });

        await editor.InitializeAsync();

        Assert.Equal(BatchEditorStep.Preview, editor.Step);
        Assert.Equal("Enable 2 accounts", editor.ChangeTitle);
        Assert.Equal(1, editor.ChangeCount);
        Assert.IsType<EnableDisableEditorViewModel>(Assert.Single(editor.Operations));
    }

    [Fact]
    public async Task OperationListRules_AreValidatedBeforePlanning()
    {
        var (editor, harness) = await OpenAsync(c => [c.JanKowalski]);
        Add<DeleteEditorViewModel>(editor, OperationType.Delete);
        Add<UnlockEditorViewModel>(editor, OperationType.Unlock);

        Assert.Equal(harness.Localizer[L.Batch.DeleteAlone], editor.OperationsError);
        Assert.False(editor.PreviewCommand.CanExecute(null));

        editor.RemoveOperationCommand.Execute(editor.Operations[0]);
        Assert.Null(editor.OperationsError);
        Assert.True(editor.PreviewCommand.CanExecute(null));

        var groups = Add<GroupsEditorViewModel>(editor, OperationType.AddToGroups);
        Assert.NotNull(groups.Error);
        harness.Dialogs.PickedObjects.Enqueue([harness.Company.GroupSales]);
        await groups.AddGroupsCommand.ExecuteAsync(null);
        Assert.Null(groups.Error);
        Assert.Equal("GG_Sprzedaz", Assert.Single(groups.Groups).Name);
        editor.MoveOperationUpCommand.Execute(groups);
        Assert.Same(groups, editor.Operations[0]);
    }

    [Fact]
    public async Task FixedPassword_IsReadFromThePasswordBoxesOnlyWhenPlanning()
    {
        var (editor, harness) = await OpenAsync(c => [c.JanKowalski]);
        var reset = Add<ResetPasswordEditorViewModel>(editor, OperationType.ResetPassword);
        reset.PasswordInput = new TestPasswordInput(string.Empty);
        reset.ConfirmPasswordInput = new TestPasswordInput(string.Empty);
        reset.UseFixedPassword = true;
        reset.MustChangeAtNextLogon = false;

        // Typing in a PasswordBox raises no notification: the boxes must not make the editor invalid, otherwise
        // Preview would stay disabled after the operator typed the password.
        ((TestPasswordInput)reset.PasswordInput).Password = "Haslo!2026xyz";
        ((TestPasswordInput)reset.ConfirmPasswordInput).Password = "Inne!2026xyz";
        Assert.True(reset.IsValid);
        Assert.True(editor.PreviewCommand.CanExecute(null));

        await editor.PreviewCommand.ExecuteAsync(null);

        Assert.Equal(BatchEditorStep.Edit, editor.Step);
        Assert.Equal(harness.Localizer[L.Batch.PasswordMismatch], reset.PasswordError);
        Assert.True(editor.PreviewCommand.CanExecute(null));
        ((TestPasswordInput)reset.ConfirmPasswordInput).Password = "Haslo!2026xyz";
        reset.NotifyPasswordChanged();
        Assert.Null(reset.PasswordError);
        await editor.PreviewCommand.ExecuteAsync(null);
        Assert.Equal(BatchEditorStep.Preview, editor.Step);
        harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await editor.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal("Haslo!2026xyz", Assert.Single(harness.Session.PasswordResets).Password);
        Assert.DoesNotContain("Haslo", reset.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CsvMode_ImportsAPolishExcelFile()
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo("pl-PL"));
        var editor = harness.Factory.CreateBatchEditor(new BatchEditorRequest([]));
        await editor.InitializeAsync();
        Assert.True(editor.IsCsvMode);
        Assert.Equal("Import zmian z pliku CSV", editor.WindowTitle);
        var csv = Assert.IsType<CsvImportViewModel>(editor.CsvImport);

        var path = Path.Combine(Path.GetTempPath(), $"admng-test-{Guid.NewGuid():N}.csv");
        try
        {
            // Polish Excel: ';' delimiter and Windows-1250 encoding.
            var content = "sAMAccountName;Dział;Stanowisko [title];Nieznana kolumna\r\n"
                + "jan.kowalski;IT;Kierownik zespołu;x\r\n"
                + "lukasz.wisniewski;Rozwój;Programista;y\r\n"
                + "nie.ma;IT;Ktoś;z\r\n";
            await File.WriteAllBytesAsync(path, Encoding.GetEncoding(1250).GetBytes(content), TestContext.Current.CancellationToken);
            harness.Files.NextOpenPath = path;
            await csv.BrowseCommand.ExecuteAsync(null);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Null(csv.Error);
        Assert.Equal(3, csv.RowCount);
        Assert.Equal(";", csv.DelimiterText);
        Assert.Equal("sAMAccountName", csv.KeyColumn);
        Assert.Equal(Attr.SamAccountName, csv.KeyAttribute.Value);
        Assert.Equal(Attr.Department, csv.Mappings.Single(m => m.Column == "Dział").Attribute);
        Assert.Equal(Attr.Title, csv.Mappings.Single(m => m.Column == "Stanowisko [title]").Attribute);
        Assert.False(csv.Mappings.Single(m => m.Column == "Nieznana kolumna").IsMapped);
        Assert.Equal("Kierownik zespołu", csv.PreviewRows[0].Cells[2]);
        Assert.True(editor.PreviewCommand.CanExecute(null));

        await editor.PreviewCommand.ExecuteAsync(null);

        Assert.Equal(BatchEditorStep.Preview, editor.Step);
        Assert.Equal(3, editor.ObjectCount);
        Assert.Equal(1, editor.ErrorCount);
        Assert.Equal(3, editor.PreviewObjects.Single(o => o.SourceRow == 3).SourceRow);
        await editor.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal("Kierownik zespołu", harness.Value(harness.Company.JanKowalski, Attr.Title));
        Assert.Equal("Rozwój", harness.Value(harness.Company.LukaszWisniewski, Attr.Department));
    }

    [Fact]
    public async Task CsvMode_RetryKeepsTheOriginalRowNumbers()
    {
        var harness = await B2Harness.CreateAsync();
        var editor = harness.Factory.CreateBatchEditor(new BatchEditorRequest([]));
        var csv = editor.CsvImport!;
        csv.Load(new CsvTable(
            ["sAMAccountName", "title"],
            [["jan.kowalski", "A"], ["lukasz.wisniewski", "B"], ["pawel.lewandowski", "C"]],
            ';'));
        harness.Session.FailNext(FakeOperationKind.Modify, harness.Company.PawelLewandowski, DirectoryErrorKind.Busy, times: 10);
        await editor.PreviewCommand.ExecuteAsync(null);
        await editor.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(1, editor.Result!.FailedCount);

        await editor.RetryFailedCommand.ExecuteAsync(null);

        var retried = Assert.Single(editor.PreviewObjects);
        Assert.Equal(3, retried.SourceRow);
        Assert.Equal("Paweł Lewandowski", retried.Name);
    }

    [Fact]
    public async Task CsvMapping_IsValidated()
    {
        var harness = await B2Harness.CreateAsync();
        var csv = harness.Factory.CreateBatchEditor(new BatchEditorRequest([])).CsvImport!;
        Assert.False(csv.IsValid);

        csv.Load(new CsvTable(["login", "Department", "department"], [["jan.kowalski", "A", "B"]], ','));
        Assert.Equal("login", csv.KeyColumn);
        Assert.Equal(harness.Localizer.Format(L.Csv.DuplicateMapping, Attr.Department), csv.MappingError);

        csv.Mappings[2].Attribute = string.Empty;
        Assert.True(csv.IsValid);
        var mapping = csv.BuildMapping();
        Assert.Equal(Attr.SamAccountName, mapping.KeyAttribute);
        Assert.Equal(Attr.Department, mapping.Columns["Department"]);
        Assert.Equal(EmptyCellBehavior.Ignore, mapping.EmptyCells);
        Assert.Equal(Core.Model.ObjectKind.User, mapping.Kind);
    }

    [Fact]
    public async Task CsvTemplate_WritesTheHeader()
    {
        var harness = await B2Harness.CreateAsync();
        var csv = harness.Factory.CreateBatchEditor(new BatchEditorRequest([])).CsvImport!;
        var path = Path.Combine(Path.GetTempPath(), $"admng-test-{Guid.NewGuid():N}.csv");
        try
        {
            harness.Files.NextSavePath = path;
            await csv.DownloadTemplateCommand.ExecuteAsync(null);

            var header = (await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken))[0];
            Assert.StartsWith("sAMAccountName,givenName,sn,displayName", header, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Dispose_DropsPlansAndResults()
    {
        var (editor, _) = await OpenAsync(c => [c.JanKowalski], new SetAttributeOperation(Attr.Title, "X"));
        await editor.PreviewCommand.ExecuteAsync(null);

        editor.Dispose();

        Assert.Empty(editor.PreviewObjects);
        Assert.True(editor.IsDisposed);
    }
}

/// <summary>Small helper for configuring view models inline.</summary>
internal static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> configure)
    {
        configure(value);
        return value;
    }
}
