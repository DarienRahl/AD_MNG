using System.Diagnostics;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Model;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Services;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;
using AdMng.Testing.BulkCreate;

namespace AdMng.Presentation.Tests.BulkCreate;

/// <summary>
/// The acceptance tests of the bulk creation wizard (docs/dev/phase3-bulk-create.md 2.6 operator scenario, 2.11): the
/// operator's sheet from Excel to created accounts with their passwords, the retry and the undo; 5 000 rows end to end.
/// </summary>
public class BulkCreateEndToEndTests
{
    /// <summary>The operator's sheet as Excel puts it on the clipboard (Unicode text, CRLF, a header row).</summary>
    private static string OperatorSheet =>
        string.Join("\r\n", new[] { B4Wizard.OperatorHeaders }.Concat(B4Wizard.OperatorRows).Select(r => string.Join('\t', r))) + "\r\n";

    /// <summary>
    /// The operator scenario through the four steps (2.6): Ctrl+V of <em>Telefon komórkowy, Imię, Nazwisko, Dział, E-mail</em>,
    /// the automatic mapping, <em>Konta numerowane</em> with the suffix contoso.com and <em>GG_Nowi</em>, the check
    /// (kd208–kd212), the plan, the execution and five passwords; the profile becomes the default of <em>Nowi pracownicy</em>
    /// and a new wizard there starts at kd213. At most 10 commands and choices besides typing the confirmation.
    /// </summary>
    [Fact]
    public async Task OperatorScenario()
    {
        var harness = await B2Harness.CreateAsync(B4Wizard.Polish, null, bulkCreateSchema: true);
        var sample = harness.Builder.AddBulkCreateSample(harness.Company);
        using var editor = harness.Factory.CreateBatchEditor(new BatchEditorRequest([]) { Create = new BulkCreateRequest(ObjectKind.User, sample.NewEmployeesOu) });
        await editor.InitializeAsync();
        var bulk = editor.BulkCreate!;
        var commands = 0;

        // 1 Dane: Ctrl+V.
        harness.Clipboard.Formatted = new ClipboardContent(null, null, null, OperatorSheet);
        await bulk.Source.PasteCommand.ExecuteAsync(null);
        commands++;
        Assert.Equal(5, bulk.Source.Table.Rows.Count);
        await bulk.NextStepCommand.ExecuteAsync(null);
        commands++;

        // 2 Kolumny: mapped without a click.
        Assert.Equal(
            [Attr.Mobile, Attr.GivenName, Attr.Sn, Attr.Department, Attr.Mail],
            bulk.Columns.Mappings.Select(m => m.Attribute));
        await bulk.NextStepCommand.ExecuteAsync(null);
        commands++;

        // 3 Nazwy i opcje: Konta numerowane, contoso.com, GG_Nowi; must-change stays on.
        bulk.Rules.Profile = bulk.Rules.Profiles.First(p => p.Text.StartsWith("Konta numerowane", StringComparison.Ordinal));
        commands++;
        bulk.Rules.UpnSuffix = "contoso.com";
        commands++;
        harness.Dialogs.PickedObjects.Enqueue([sample.GroupNew]);
        await bulk.Rules.AddGroupsCommand.ExecuteAsync(null);
        commands++;
        Assert.True(bulk.Rules.MustChangeAtLogon);
        await bulk.NextStepCommand.ExecuteAsync(null);
        commands++;

        // 4 Sprawdzenie.
        var check = bulk.Check;
        await B2Harness.WaitUntilAsync(() => check.Computation is not null && !check.IsComputing);
        Assert.Equal(["kd208", "kd209", "kd210", "kd211", "kd212"], check.Rows.Select(r => r.Cell(Attr.SamAccountName)!.Value));
        Assert.Equal("kd208@contoso.com", check.Rows[0].Cell(Attr.UserPrincipalName)!.Value);
        Assert.Equal("lukasz.wojcik@firma.pl", check.Rows[0].Cell(Attr.Mail)!.Value);
        Assert.Equal("+48 600 100 201", check.Rows[0].Cell(Attr.Mobile)!.Value);

        // Plan and execution.
        await editor.PreviewCommand.ExecuteAsync(null);
        commands++;
        Assert.True(editor.IsPreviewStep, editor.Error);
        harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await editor.ExecuteCommand.ExecuteAsync(null);
        commands++;
        Assert.True(editor.IsResultsStep, editor.Error);
        Assert.Equal(5, editor.CreatedAccounts.Count(a => a.HasPassword));
        var created = editor.BulkCreate!.CreatedRows.Values.ToList();
        Assert.All(created, dn => Assert.Equal("0", harness.Value(dn, Attr.PwdLastSet)));
        Assert.All(created, dn => Assert.Contains(harness.Values(sample.GroupNew, Attr.Member), m => Core.Ldap.DistinguishedName.AreEqual(m, dn)));

        // The profile becomes the default of the container (the 10th choice).
        await bulk.Rules.SetDefaultForContainerCommand.ExecuteAsync(null);
        commands++;
        Assert.True(commands <= 10, $"{commands} commands");

        using var next = harness.Factory.CreateBatchEditor(new BatchEditorRequest([]) { Create = new BulkCreateRequest(ObjectKind.User, sample.NewEmployeesOu) });
        await next.InitializeAsync();
        Assert.StartsWith("Konta numerowane", next.BulkCreate!.Rules.Profile!.Text, StringComparison.Ordinal);
        await next.BulkCreate.Rules.OnEnteredAsync();
        Assert.Contains("kd213", next.BulkCreate.Rules.FirstFreeText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The operator scenario from the input steps 1–3 hand over (runs before the wave-2 merge): the check shows kd208–kd212
    /// with the sheet's addresses and mobile numbers, the plan creates them with must-change and the group, the results list
    /// five passwords; a failed group step is retried without a second Create or a second password, and the undo deletes
    /// every object of both executions.
    /// </summary>
    [Fact]
    public async Task OperatorScenario_FromTheCheckToTheUndo()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        var check = wizard.Check;
        Assert.Equal(["kd208", "kd209", "kd210", "kd211", "kd212"], check.Rows.Select(r => r.Cell(Attr.SamAccountName)!.Value));
        Assert.Equal(
            ["kd208@contoso.com", "kd209@contoso.com", "kd210@contoso.com", "kd211@contoso.com", "kd212@contoso.com"],
            check.Rows.Select(r => r.Cell(Attr.UserPrincipalName)!.Value));
        Assert.Equal(B4Wizard.OperatorRows.Select(r => r[4]), check.Rows.Select(r => r.Cell(Attr.Mail)!.Value));
        Assert.Equal(B4Wizard.OperatorRows.Select(r => r[0]), check.Rows.Select(r => r.Cell(Attr.Mobile)!.Value));

        // The enable step of row 3 is refused once (not the canary): that account is created, but partially.
        var editor = wizard.Editor;
        await editor.PreviewCommand.ExecuteAsync(null);
        var row3 = check.Computation!.Rows[2].DistinguishedName!;
        wizard.Harness.Session.FailNext(FakeOperationKind.Modify, row3, Core.Directory.DirectoryErrorKind.AccessDenied);
        wizard.Harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await editor.ExecuteCommand.ExecuteAsync(null);
        Assert.True(editor.IsResultsStep, editor.Error);
        Assert.False(wizard.Bulk.WasStoppedAfterCanary);
        Assert.Equal(5, editor.CreatedAccounts.Count);
        Assert.All(editor.CreatedAccounts, a => Assert.True(a.HasPassword));
        Assert.Equal(1, editor.Result!.FailedCount);
        Assert.Equal("Utworzono, nie wszystkie kroki się udały", editor.CreatedAccounts[2].Status);
        Assert.NotNull(editor.CreatedAccounts[2].Message);
        var passwords = editor.CreatedAccounts.ToDictionary(a => a.Row, a => a.Password);

        // Retry: only row 3's steps that did not run – no second Create, no second password (the one handed out stays valid).
        await editor.RetryFailedCommand.ExecuteAsync(null);
        Assert.True(editor.IsPreviewStep, editor.Error);
        var retry = Assert.Single(editor.PreviewObjects).Plan;
        Assert.Equal(3, retry.SourceRow);
        Assert.DoesNotContain(retry.Changes, c => c.Action is ChangeAction.Create or ChangeAction.ResetPassword);
        Assert.Contains(retry.Changes, c => c.Action == ChangeAction.AddMember);
        wizard.Harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await editor.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(5, editor.Result!.SucceededCount);
        Assert.Equal(passwords, editor.CreatedAccounts.ToDictionary(a => a.Row, a => a.Password));
        Assert.Equal("512", wizard.Harness.Value(row3, Attr.UserAccountControl));
        Assert.Equal(5, wizard.Harness.Values(wizard.Sample.GroupNew, Attr.Member).Count(m => wizard.Bulk.CreatedRows.Values.Any(dn => Core.Ldap.DistinguishedName.AreEqual(dn, m))));

        // Undo: everything this window created.
        var created = wizard.Bulk.CreatedRows.Values.ToList();
        await B2Harness.WaitUntilAsync(() => wizard.Bulk.CreatedObjects.Count == 5 && wizard.Bulk.CreatedObjects.All(o => o.ObjectGuid is not null));
        editor.UndoCommand.Execute(null);
        await B2Harness.WaitUntilAsync(() => editor.IsPreviewStep && editor.UndoNote is not null);
        wizard.Harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await editor.ExecuteCommand.ExecuteAsync(null);
        Assert.All(created, dn => Assert.Empty(wizard.Harness.Session.GetValues(dn, Attr.ObjectClass)));
        Assert.All(editor.CreatedAccounts, a => Assert.True(a.IsUndone));

        // Audit: Create entries, never a password; the PowerShell history asks for the password.
        var all = passwords.Values.OfType<string>().ToList();
        Assert.All(wizard.Harness.AuditLog.Entries, e => Assert.All(all, p => Assert.DoesNotContain(p, e.ToString() ?? string.Empty, StringComparison.Ordinal)));
        var scripts = string.Join("\n", wizard.Harness.PowerShell.Entries.Select(e => e.Script));
        Assert.Contains("New-ADUser", scripts, StringComparison.Ordinal);
        Assert.All(all, p => Assert.DoesNotContain(p, scripts, StringComparison.Ordinal));
    }

    /// <summary>5 000 rows (2.11): compute, plan and preview within the budgets; the preview build of the plan measured on its own.</summary>
    [Fact]
    public async Task FiveThousandRows_ComputePlanAndPreview_StayWithinTheBudgets()
    {
        using var wizard = await B4Wizard.CreateAsync();
        var lines = ExcelSamples.Rows(5000).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var rows = lines.Skip(1).Select(l => l.Split('\t')).ToArray();
        wizard.Use(wizard.OperatorInput(new BulkAccountOptions { MatchAttributes = [] }) with
        {
            Table = B4Wizard.TableWithHeaders(lines[0].Split('\t'), rows),
            Mappings =
            [
                new(0, ColumnTargetKind.Attribute, Attr.GivenName),
                new(1, ColumnTargetKind.Attribute, Attr.Sn),
                new(2, ColumnTargetKind.Attribute, Attr.Mail),
                new(3, ColumnTargetKind.Attribute, Attr.Mobile),
                new(4, ColumnTargetKind.Attribute, Attr.Department),
            ],
        });

        var watch = Stopwatch.StartNew();
        await wizard.EnterCheckAsync();
        var compute = watch.Elapsed;
        Assert.Null(wizard.Check.Error);
        Assert.Equal(5000, wizard.Check.Rows.Count);

        watch.Restart();
        await wizard.Editor.PreviewCommand.ExecuteAsync(null);
        var plan = watch.Elapsed;
        Assert.True(wizard.Editor.IsPreviewStep, wizard.Editor.Error);
        Assert.Equal(5000, wizard.Editor.ChangeCount);

        var built = wizard.Editor.PreviewObjects.Select(o => o.Plan).ToList();
        var builder = new PreviewBuilder(wizard.Harness.Localizer, wizard.Harness.Errors, wizard.Harness.Sessions.Formatter, wizard.Harness.Session.Schema, wizard.Harness.Time);
        watch.Restart();
        var preview = builder.Build(new ChangePlan { Objects = built, Title = "5000", CreatedAt = wizard.Harness.Time.GetUtcNow(), Server = "dc01" });
        var previewBuild = watch.Elapsed;

        TestContext.Current.SendDiagnosticMessage($"5 000 rows: compute {compute.TotalSeconds:0.00} s, plan and preview {plan.TotalSeconds:0.00} s, preview build {previewBuild.TotalSeconds:0.00} s");
        Assert.Equal(5000, preview.Count);
        Assert.True(compute < TimeSpan.FromSeconds(20), $"compute {compute}");
        Assert.True(plan < TimeSpan.FromSeconds(30), $"plan and preview {plan}");
        Assert.True(previewBuild < TimeSpan.FromSeconds(3), $"preview build {previewBuild}");
    }
}
