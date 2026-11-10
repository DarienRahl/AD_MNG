using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.BulkCreate;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.BulkCreate;

/// <summary>
/// Step 4 <em>Sprawdzenie</em> (docs/dev/phase3-bulk-create.md 2.7, 2.11 Presentation B4), pl-PL, on the bulk-create sample:
/// the check grid is driven through <see cref="B4Wizard"/> (the check's input replaced, so steps 1–3 are not needed).
/// </summary>
public class BulkCheckTests
{
    [Fact]
    public async Task Compute_ShowsEveryRowWithItsValues_StatusesAndPolishNotes()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());

        await wizard.EnterCheckAsync();

        var check = wizard.Check;
        Assert.Null(check.Error);
        Assert.Equal(5, check.Rows.Count);
        Assert.Equal(["kd208", "kd209", "kd210", "kd211", "kd212"], check.Rows.Select(r => r.Cell(Attr.SamAccountName)!.Value));
        Assert.Equal("kd208@contoso.com", check.Rows[0].Cell(Attr.UserPrincipalName)!.Value);
        Assert.Equal("lukasz.wojcik@firma.pl", check.Rows[0].Cell(Attr.Mail)!.Value);
        Assert.Equal("+48 600 100 201", check.Rows[0].Cell(Attr.Mobile)!.Value);
        Assert.Equal("Łukasz Wójcik", check.Rows[0].Cell(Attr.Cn)!.Value);

        // Nr, Imię, Nazwisko frozen on the left; Uwagi next; the computed attributes; the groups of the option.
        Assert.Equal([BulkCheckViewModel.NumberKey, Attr.GivenName, Attr.Sn], check.Columns.Where(c => c.IsFrozen).Select(c => c.Key));
        Assert.Equal("Nr", check.Columns[0].Header);
        Assert.Equal("Imię", check.Columns[1].Header);
        Assert.Contains(check.Columns, c => c.Key == BulkCheckViewModel.GroupsKey && c.Header == "Grupy");
        Assert.Equal("2", check.Rows[0].Cell(BulkCheckViewModel.NumberKey)!.Value);
        Assert.Equal(2, check.Rows[0].ExcelRow);
        Assert.Equal("1", check.Rows[0].Cell(BulkCheckViewModel.GroupsKey)!.Value);
        Assert.Contains("GG_Nowi", check.Rows[0].Cell(BulkCheckViewModel.GroupsKey)!.ToolTip);

        Assert.Equal("Wiersze: 5 – OK: 5, z ostrzeżeniami: 0, z błędami: 0, wykluczone: 0", check.Summary);
        Assert.Equal("Unikalność UPN i adresów: cały las (jedna domena)", check.ScopeNote);
        Assert.All(check.Rows, r => Assert.Equal("OK", r.StatusText));
        Assert.True(wizard.Bulk.IsReadyToPlan);
        Assert.True(wizard.Editor.PreviewCommand.CanExecute(null));
    }

    [Fact]
    public async Task WarningsAndErrors_HaveTheirReasons_AndTheFilterAndF8FindThem()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.Input(
            [["Jan", "Kowalski", ""], ["Anna", "Nowak", "anna.nowak@firma.pl"], ["Anna", "Nowak", "anna.nowak@firma.pl"], ["Ewa", "Wiśniewska", ""]],
            [new(0, ColumnTargetKind.Attribute, Attr.GivenName), new(1, ColumnTargetKind.Attribute, Attr.Sn), new(2, ColumnTargetKind.Attribute, Attr.Mail)],
            B4Wizard.StandardRules,
            options: new BulkAccountOptions()));

        await wizard.EnterCheckAsync();

        var check = wizard.Check;
        var jan = check.Rows[0];
        Assert.Equal(BulkRowStatus.Warning, jan.Status);
        Assert.Contains("Kosz", jan.Notes, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(BulkRowStatus.Error, check.Rows[1].Status);
        Assert.Equal(BulkRowStatus.Error, check.Rows[2].Status);
        Assert.NotEmpty(check.Rows[1].Notes);
        Assert.True(check.Rows[3].IsPossibleDuplicate);
        Assert.Contains("Wiśniewska", check.Rows[3].Notes, StringComparison.Ordinal);
        Assert.DoesNotContain("[", jan.Notes, StringComparison.Ordinal);

        check.Filter = BulkCheckFilter.Errors;
        Assert.Equal([2, 3], check.Rows.Select(r => r.Number));
        check.Filter = BulkCheckFilter.PossibleDuplicates;
        Assert.Contains(check.Rows, r => r.Number == 4);
        check.Filter = BulkCheckFilter.All;

        check.NextErrorCommand.Execute(null);
        Assert.Equal(2, check.SelectedRow!.Number);
        check.NextErrorCommand.Execute(null);
        Assert.Equal(3, check.SelectedRow!.Number);
        check.NextErrorCommand.Execute(null);
        Assert.Equal(2, check.SelectedRow!.Number);
    }

    [Fact]
    public async Task CopyErrorRows_CopiesTheSheetsCellsAndTheNotes_FormulaProtected()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.Input(
            [["=Anna", "Nowak", "anowak"], ["=Anna", "Nowak", "anowak"], ["Zofia", "Kamińska", "zkaminska"]],
            [.. B4Wizard.NameMappings, new(2, ColumnTargetKind.Attribute, Attr.SamAccountName)],
            B4Wizard.StandardRules,
            options: new BulkAccountOptions { MatchAttributes = [] }));
        await wizard.EnterCheckAsync();

        wizard.Check.CopyErrorRowsCommand.Execute(null);

        var text = wizard.Harness.Clipboard.Text!;
        Assert.False(wizard.Harness.Clipboard.IsSecret);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal("Nr\tA\tB\tC\tUwagi", lines[0]);
        // The Excel row (step 1's first data row) and the sheet's cells; "=Anna" stays text in Excel.
        var excelRow = wizard.Check.ExcelRow(1).ToString(CultureInfo.InvariantCulture);
        Assert.StartsWith(excelRow + "\t'=Anna\tNowak\tanowak\t", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("Kamińska", text, StringComparison.Ordinal);
        Assert.Contains(wizard.Harness.Notifications.Items, n => n.Message == "Skopiowano wiersze z błędami: 2.");
    }

    [Fact]
    public async Task Edit_IsRecomputedInPlace_StaysMarked_AndCanBeReverted()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        var check = wizard.Check;
        var row = check.Rows[1];
        var cell = row.Cell(Attr.SamAccountName)!;
        var rows = check.Rows.ToList();

        cell.EditText = "kd300";
        await B2Harness.WaitUntilAsync(() => !check.IsComputing && check.Computation!.Rows[1].Value(Attr.SamAccountName) == "kd300");

        Assert.Same(row, check.Rows[1]);
        Assert.Same(cell, row.Cell(Attr.SamAccountName));
        Assert.Equal(rows, check.Rows);
        Assert.True(cell.IsEdited);
        Assert.Equal(BulkValueSource.Edited, cell.Source);
        Assert.Equal("kd300", cell.Value);
        Assert.Equal("kd300@contoso.com", row.Cell(Attr.UserPrincipalName)!.Value);
        Assert.Equal("kd300", check.Edits[2][Attr.SamAccountName]);
        // The other rows keep their numbers (pins).
        Assert.Equal("kd208", check.Rows[0].Cell(Attr.SamAccountName)!.Value);
        Assert.Equal("kd210", check.Rows[2].Cell(Attr.SamAccountName)!.Value);

        check.RevertCellCommand.Execute(cell);
        await B2Harness.WaitUntilAsync(() => !check.IsComputing && !check.Edits.ContainsKey(2) && check.Computation!.Rows[1].Value(Attr.SamAccountName) != "kd300");

        Assert.False(cell.IsEdited);
        Assert.Equal("kd209", cell.Value);
    }

    [Fact]
    public async Task Recompute_LeavesTheCellBeingEditedAlone()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        var check = wizard.Check;
        var editing = check.Rows[3].Cell(Attr.DisplayName)!;
        editing.IsEditing = true;
        editing.EditText = "Małgorzata D";

        // Another cell is committed meanwhile: the recompute updates the grid but not the open editor.
        check.Rows[0].Cell(Attr.SamAccountName)!.EditText = "kd400";
        await B2Harness.WaitUntilAsync(() => !check.IsComputing && check.Computation!.Rows[0].Value(Attr.SamAccountName) == "kd400");

        Assert.Equal("Małgorzata D", editing.EditText);
    }

    [Fact]
    public async Task AmbiguousManager_OffersItsCandidates_AndAChoiceResolvesIt()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.Input(
            [["Łukasz", "Wójcik", "Piotr Zieliński"], ["Zofia", "Kamińska", "Ewa Wiśniewska"]],
            [.. B4Wizard.NameMappings, new(2, ColumnTargetKind.Manager)]));
        await wizard.EnterCheckAsync();
        var check = wizard.Check;
        var manager = check.Rows[0].Cell(BulkCheckViewModel.ManagerKey)!;

        Assert.Equal(BulkRowStatus.Warning, check.Rows[0].Status);
        Assert.True(manager.HasCandidates);
        Assert.Equal(2, manager.Candidates.Count);
        Assert.All(manager.Candidates, c => Assert.StartsWith("Piotr Zieliński (", c.Text, StringComparison.Ordinal));
        Assert.Equal("Ewa Wiśniewska", check.Rows[1].Cell(BulkCheckViewModel.ManagerKey)!.Value);
        Assert.True(manager.IsEditable);

        manager.ChosenCandidate = wizard.Sample.PiotrZielinski2;
        await B2Harness.WaitUntilAsync(() => !check.IsComputing && check.Computation!.Rows[0].ManagerDn is not null);

        Assert.True(DistinguishedNameEquals(wizard.Sample.PiotrZielinski2, check.Computation!.Rows[0].ManagerDn!));
        Assert.Equal("Piotr Zieliński", manager.Value);
        Assert.False(manager.HasCandidates);
        Assert.Equal(BulkRowStatus.Ok, check.Rows[0].Status);
    }

    [Fact]
    public async Task GroupsCell_IsEditable_AndResolvedAgain()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.Input([["Łukasz", "Wójcik", "GG_Nowi"]], [.. B4Wizard.NameMappings, new(2, ColumnTargetKind.Groups)]));
        await wizard.EnterCheckAsync();
        var check = wizard.Check;
        var groups = check.Rows[0].Cell(BulkCheckViewModel.GroupsKey)!;
        Assert.Equal("1", groups.Value);
        Assert.Equal("GG_Nowi", groups.EditText);

        groups.EditText = "GG_Nowi|UG_Projekty";
        await B2Harness.WaitUntilAsync(() => !check.IsComputing && check.Computation!.Rows[0].GroupDns.Count == 2);

        Assert.Equal("2", groups.Value);
        Assert.Contains("UG_Projekty", groups.ToolTip);
        Assert.True(groups.IsEdited);
    }

    [Fact]
    public async Task ExcludedRow_IsLeftOut_AndCountedInTheSummary()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        var check = wizard.Check;

        check.Rows[0].IsIncluded = false;
        await B2Harness.WaitUntilAsync(() => !check.IsComputing && check.Computation!.Rows[0].Status == BulkRowStatus.Excluded);

        Assert.Equal([1], check.ExcludedRows);
        Assert.Equal(BulkRowStatus.Excluded, check.Rows[0].Status);
        Assert.Equal("Wykluczony", check.Rows[0].StatusText);
        Assert.Contains("wykluczone: 1", check.Summary, StringComparison.Ordinal);
        // The pins keep the other rows' numbers.
        Assert.Equal("kd209", check.Rows[1].Cell(Attr.SamAccountName)!.Value);
        check.Filter = BulkCheckFilter.Excluded;
        Assert.Equal([1], check.Rows.Select(r => r.Number));
        check.Filter = BulkCheckFilter.All;

        check.Rows[0].IsIncluded = true;
        await B2Harness.WaitUntilAsync(() => !check.IsComputing && check.Computation!.Rows[0].Status == BulkRowStatus.Ok);
        Assert.Empty(check.ExcludedRows);
    }

    [Fact]
    public async Task Renumber_ForgetsThePins()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        var check = wizard.Check;
        check.Rows[0].IsIncluded = false;
        await B2Harness.WaitUntilAsync(() => !check.IsComputing && check.Computation!.Rows[0].Status == BulkRowStatus.Excluded);
        Assert.Equal("kd209", check.Rows[1].Cell(Attr.SamAccountName)!.Value);

        await check.RenumberCommand.ExecuteAsync(null);

        Assert.Equal("kd208", check.Rows[1].Cell(Attr.SamAccountName)!.Value);
        Assert.Equal(4, check.PinnedNumbers.Count);
    }

    [Fact]
    public async Task Recheck_ReadsTheDirectoryAgain()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        var check = wizard.Check;
        wizard.Harness.Builder.AddUser("Inny", "Użytkownik", wizard.Harness.Company.SalesOu, new Dictionary<string, object?> { [Attr.SamAccountName] = "kd210" });

        await check.RecheckCommand.ExecuteAsync(null);

        // kd210 is taken now: its row keeps no number that collides (pins are kept only while free).
        Assert.DoesNotContain("kd210", check.Rows.Select(r => r.Cell(Attr.SamAccountName)!.Value));
    }

    [Fact]
    public async Task IncompleteInput_ShowsWhatIsMissing()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Check.Input = () => null;

        await wizard.EnterCheckAsync();

        Assert.Equal(wizard.Harness.Localizer[L.BulkCreateCheck.InputIncomplete], wizard.Check.Error);
        Assert.False(wizard.Bulk.IsReadyToPlan);
    }

    [Fact]
    public async Task InputReset_DropsEditsExclusionsPinsAndTheGrid()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        var check = wizard.Check;
        check.Rows[0].IsIncluded = false;
        check.Rows[1].Cell(Attr.SamAccountName)!.EditText = "kd500";
        await B2Harness.WaitUntilAsync(() => !check.IsComputing && check.Computation!.Rows[1].Value(Attr.SamAccountName) == "kd500");

        check.OnInputReset();

        Assert.Empty(check.Edits);
        Assert.Empty(check.ExcludedRows);
        Assert.Empty(check.PinnedNumbers);
        Assert.Empty(check.Rows);
        Assert.Null(check.Computation);
    }

    [Fact]
    public async Task ManyRows_FillTheGridOnce_AndARecomputeKeepsEveryRowObject()
    {
        using var wizard = await B4Wizard.CreateAsync();
        var people = Enumerable.Range(0, 300).Select(i => new[] { "Osoba" + i.ToString(CultureInfo.InvariantCulture), "Żółć" }).ToArray();
        wizard.Use(wizard.Input(people, B4Wizard.NameMappings, options: new BulkAccountOptions { MatchAttributes = [] }));
        await wizard.EnterCheckAsync();
        var check = wizard.Check;
        var before = check.Rows.ToList();
        var resets = 0;
        check.Rows.CollectionChanged += (_, e) => resets += e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset ? 1 : 0;

        before[150].Cell(Attr.DisplayName)!.EditText = "Ktoś Inny";
        await B2Harness.WaitUntilAsync(() => !check.IsComputing && check.Computation!.Rows[150].Value(Attr.DisplayName) == "Ktoś Inny");

        Assert.Equal(300, check.Rows.Count);
        Assert.Equal(before, check.Rows);
        Assert.Equal(0, resets);
    }

    [Fact]
    public async Task Created_And_Undone_Rows_Are_Marked()
    {
        using var wizard = await B4Wizard.CreateAsync();
        wizard.Use(wizard.OperatorInput());
        await wizard.EnterCheckAsync();
        var check = wizard.Check;

        check.MarkCreated([1, 2]);

        Assert.Equal(BulkRowStatus.Created, check.Rows[0].Status);
        Assert.True(check.Rows[0].IsCreated);
        Assert.False(check.Rows[0].CanToggle);
        Assert.All(check.Rows[0].Cells, c => Assert.False(c.IsEditable));
        Assert.True(check.IsStale);

        check.OnUndone([1]);
        Assert.Contains(check.Rows[0].Cells, c => c.IsEditable);
    }

    [Fact]
    public async Task EveryCheckKey_HasBothLanguages_AndTheChangeTitlesExist()
    {
        using var wizard = await B4Wizard.CreateAsync();
        var en = new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"));
        var pl = new ResxLocalizer(B4Wizard.Polish);
        foreach (var kind in L.BulkCreateCheck.ChangeTitleKinds)
        {
            var key = L.BulkCreateCheck.ChangeTitleKey(kind);
            Assert.NotEqual($"[{key}]", en[key]);
            Assert.NotEqual($"[{key}]", pl[key]);
        }

        Assert.Equal(L.BulkCreateCheck.ChangeTitleKey(ObjectKind.User), L.BulkCreateCheck.ChangeTitleKey(ObjectKind.InetOrgPerson));
        Assert.Equal("Utworzenie kont (0) w jednostce „Nowi pracownicy”", wizard.Bulk.ChangeTitle);
    }

    [Fact]
    public void EveryF3IssueCode_RendersInPolish_WithoutTheEnglishFallback()
    {
        var pl = new ResxLocalizer(B4Wizard.Polish);
        var en = new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"));
        foreach (var code in BulkCreateIssueCodes.All)
        {
            var issue = new Core.Batch.ValidationIssue(Core.Batch.IssueSeverity.Warning, code, "English text of the planner.", Attr.SamAccountName)
            {
                Arguments = ["Jan Kowalski", "7", "1"],
            };
            var polish = pl.IssueText(issue);
            Assert.DoesNotContain("English text", polish, StringComparison.Ordinal);
            Assert.DoesNotContain("[Issue.", polish, StringComparison.Ordinal);
            Assert.NotEqual(en.IssueText(issue), polish);
        }
    }

    // ---- The read-only global-catalog session of a multi-domain forest (2.2, 2.7) ----

    [Fact]
    public async Task GlobalCatalog_IsOpenedForAMultiDomainForestWithAReadyGc_UsedForTheForestScope_AndClosedOnDispose()
    {
        GlobalCatalogSessionDouble? catalog = null;
        var wizard = await B4Wizard.CreateAsync((harness, _) => catalog = MultiDomainForest(harness));
        await B2Harness.WaitUntilAsync(() => wizard.Check.HasGlobalCatalog);
        wizard.Use(wizard.OperatorInput());

        await wizard.EnterCheckAsync();

        Assert.Null(wizard.Check.Error);
        Assert.Equal(1, wizard.Harness.Connector.GlobalCatalogConnectCount);
        Assert.Equal(UniquenessScope.Forest, wizard.Check.Computation!.Scope);
        Assert.Equal("Unikalność UPN i adresów: cały las (wykaz globalny na " + wizard.Harness.Session.Info.Server + ")", wizard.Check.ScopeNote);
        Assert.Same(wizard.Check.Lookups.ForestReader, wizard.Check.Lookups.ForestReader);
        Assert.IsNotAssignableFrom<IDirectorySession>(wizard.Check.Lookups.ForestReader);

        wizard.Dispose();
        await B2Harness.WaitUntilAsync(() => catalog!.IsDisposed);
    }

    [Fact]
    public async Task GlobalCatalog_IsNotOpened_ForASingleDomainForest_OrADcThatIsNoReadyGc()
    {
        using (var single = await B4Wizard.CreateAsync((harness, _) =>
               {
                   harness.Session.Info = harness.Session.Info with { IsGlobalCatalogReady = true };
                   harness.Connector.GlobalCatalogSession = new FakeDomainControllerSession(harness.Session, harness.Session.Info.Server);
               }))
        {
            Assert.Equal(0, single.Harness.Connector.GlobalCatalogConnectCount);
        }

        using var notReady = await B4Wizard.CreateAsync((harness, _) =>
        {
            MultiDomainForest(harness);
            harness.Session.Info = harness.Session.Info with { IsGlobalCatalogReady = false };
        });
        notReady.Use(notReady.OperatorInput());
        await notReady.EnterCheckAsync();

        Assert.Equal(0, notReady.Harness.Connector.GlobalCatalogConnectCount);
        Assert.Equal(UniquenessScope.Domain, notReady.Check.Computation!.Scope);
        Assert.Equal("Unikalność UPN i adresów: sprawdzone tylko w domenie", notReady.Check.ScopeNote);
    }

    [Fact]
    public async Task GlobalCatalog_IsAwaitedByTheComputation()
    {
        var gate = new TaskCompletionSource();
        using var wizard = await B4Wizard.CreateAsync((harness, _) =>
        {
            MultiDomainForest(harness);
            harness.Connector.GlobalCatalogGate = gate;
        });
        wizard.Use(wizard.OperatorInput());

        var entering = wizard.EnterCheckAsync();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(entering.IsCompleted);
        Assert.Null(wizard.Check.Computation);

        gate.SetResult();
        await entering;

        Assert.Equal(UniquenessScope.Forest, wizard.Check.Computation!.Scope);
    }

    [Fact]
    public async Task GlobalCatalog_AFailedConnect_ChecksInTheDomainOnly()
    {
        using var wizard = await B4Wizard.CreateAsync((harness, _) =>
        {
            MultiDomainForest(harness);
            harness.Connector.GlobalCatalogError = new DirectoryException(DirectoryErrorKind.ServerUnavailable, "No GC.");
        });
        wizard.Use(wizard.OperatorInput());

        await wizard.EnterCheckAsync();

        Assert.False(wizard.Check.HasGlobalCatalog);
        Assert.Equal(UniquenessScope.Domain, wizard.Check.Computation!.Scope);
        Assert.Contains(wizard.Check.RunIssues, i => i.Length > 0);
    }

    [Fact]
    public async Task GlobalCatalog_ALateConnect_AfterTheWindowClosed_IsDisposedAtOnce()
    {
        var gate = new TaskCompletionSource();
        GlobalCatalogSessionDouble? catalog = null;
        var wizard = await B4Wizard.CreateAsync((harness, _) =>
        {
            catalog = MultiDomainForest(harness);
            harness.Connector.GlobalCatalogGate = gate;
        });
        await B2Harness.WaitUntilAsync(() => wizard.Harness.Connector.GlobalCatalogConnectCount == 1);

        wizard.Dispose();
        gate.SetResult();

        await B2Harness.WaitUntilAsync(() => catalog!.IsDisposed);
    }

    [Fact]
    public async Task GlobalCatalog_IsClosed_WhenTheWindowsSessionEnds()
    {
        GlobalCatalogSessionDouble? catalog = null;
        using var wizard = await B4Wizard.CreateAsync((harness, _) => catalog = MultiDomainForest(harness));
        await B2Harness.WaitUntilAsync(() => wizard.Check.HasGlobalCatalog);

        await wizard.Harness.Sessions.DisconnectAsync();

        await B2Harness.WaitUntilAsync(() => catalog!.IsDisposed);
        Assert.False(wizard.Check.HasGlobalCatalog);
        Assert.Null(wizard.Check.Lookups.ForestReader);
        Assert.False(wizard.Editor.PreviewCommand.CanExecute(null));
    }

    /// <summary>A second domain in CN=Partitions, the window's DC a ready GC and a GC session of the connector.</summary>
    private static GlobalCatalogSessionDouble MultiDomainForest(B2Harness harness)
    {
        harness.Session.Seed($"CN=CHILD,{harness.Builder.PartitionsDn}", new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "crossRef" },
            ["systemFlags"] = 3,
            ["dnsRoot"] = "child.contoso.test",
            ["nCName"] = "DC=child,DC=contoso,DC=test",
        });
        harness.Session.Info = harness.Session.Info with { IsGlobalCatalogReady = true };
        var child = new FakeDirectoryBuilder("child.contoso.test", schema: FakeSchema.Create(bulkCreate: true), guidNamespace: 7, domainSid: "S-1-5-21-1-2-3");
        var catalog = new GlobalCatalogSessionDouble(new FakeGlobalCatalogReader(harness.Session, child.Session));
        harness.Connector.GlobalCatalogSession = catalog;
        return catalog;
    }

    private static bool DistinguishedNameEquals(string a, string b) => Core.Ldap.DistinguishedName.AreEqual(a, b);
}

/// <summary>
/// The fixture of the B4 tests (pl-PL): a bulk-create <see cref="B2Harness"/> with <see cref="FakeDirectoryBuilder.AddBulkCreateSample"/>,
/// the batch editor in create mode on <em>Nowi pracownicy</em> and the check step's input replaced by a hand-built
/// <see cref="BulkCreateInput"/> (with the window's edits, exclusions, pins and created rows), so the check, the plan and the
/// results run without steps 1–3 (B3) until the wave-2 merge.
/// </summary>
internal sealed class B4Wizard : IDisposable
{
    public static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    /// <summary>The operator's sheet of 2.11: <em>Telefon komórkowy, Imię, Nazwisko, Dział, E-mail</em> – five people with diacritics.</summary>
    public static readonly string[] OperatorHeaders = ["Telefon komórkowy", "Imię", "Nazwisko", "Dział", "E-mail"];

    public static readonly string[][] OperatorRows =
    [
        ["+48 600 100 201", "Łukasz", "Wójcik", "Księgowość", "lukasz.wojcik@firma.pl"],
        ["+48 600 100 202", "Zofia", "Kamińska", "Kadry", "zofia.kaminska@firma.pl"],
        ["+48 600 100 203", "Michał", "Lewandowski", "IT", "michal.lewandowski@firma.pl"],
        ["+48 600 100 204", "Małgorzata", "Dąbrowska", "Sprzedaż", "malgorzata.dabrowska@firma.pl"],
        ["+48 600 100 205", "Grzegorz", "Żółkiewski", "Logistyka", "grzegorz.zolkiewski@firma.pl"],
    ];

    /// <summary>What the auto-mapping of step 2 gives the operator's sheet.</summary>
    public static readonly IReadOnlyList<ColumnMapping> OperatorMappings =
    [
        new(0, ColumnTargetKind.Attribute, Attr.Mobile),
        new(1, ColumnTargetKind.Attribute, Attr.GivenName),
        new(2, ColumnTargetKind.Attribute, Attr.Sn),
        new(3, ColumnTargetKind.Attribute, Attr.Department),
        new(4, ColumnTargetKind.Attribute, Attr.Mail),
    ];

    /// <summary>Given name in column A, surname in column B.</summary>
    public static readonly IReadOnlyList<ColumnMapping> NameMappings =
    [
        new(0, ColumnTargetKind.Attribute, Attr.GivenName),
        new(1, ColumnTargetKind.Attribute, Attr.Sn),
    ];

    /// <summary>The built-in <em>Konta numerowane (kd201…)</em> profile of 2.6.</summary>
    public static readonly IReadOnlyList<AttributeRule> NumberedRules =
    [
        new(Attr.SamAccountName, "kd{$nr:201}"),
        new(Attr.UserPrincipalName, "{sAMAccountName}"),
        new(Attr.Mail, "{sAMAccountName}@{$mailDomain}"),
        new(Attr.Cn, "{givenName} {sn}"),
        new(Attr.DisplayName, "{givenName} {sn}"),
    ];

    /// <summary>The built-in <em>Standardowy</em> profile of 2.6.</summary>
    public static readonly IReadOnlyList<AttributeRule> StandardRules =
    [
        new(Attr.Cn, "{givenName} {sn}"),
        new(Attr.DisplayName, "{givenName} {sn}"),
        new(Attr.SamAccountName, "{givenName|sam|lower}.{sn|sam|lower}"),
        new(Attr.UserPrincipalName, "{sAMAccountName}"),
    ];

    private B4Wizard(B2Harness harness, BulkCreateSample sample, BatchEditorViewModel editor)
    {
        Harness = harness;
        Sample = sample;
        Editor = editor;
    }

    public B2Harness Harness { get; }

    public BulkCreateSample Sample { get; }

    public BatchEditorViewModel Editor { get; }

    public BulkCreateViewModel Bulk => Editor.BulkCreate!;

    public BulkCheckViewModel Check => Bulk.Check;

    /// <summary>The input the check reads (with the window's state added); null = an incomplete earlier step.</summary>
    public BulkCreateInput? Current { get; set; }

    /// <param name="before">Changes the directory or the harness before the window opens (e.g. a multi-domain forest).</param>
    /// <param name="kind">The kind of the request.</param>
    /// <param name="initialize">Calls the editor's <c>InitializeAsync</c> (the GC session starts there).</param>
    /// <param name="bulkCreateSchema">The bulk-create schema (false: the default schema the UI tests' context has).</param>
    public static async Task<B4Wizard> CreateAsync(Action<B2Harness, BulkCreateSample>? before = null, ObjectKind kind = ObjectKind.User, bool initialize = true, bool bulkCreateSchema = true)
    {
        var harness = await B2Harness.CreateAsync(Polish, null, bulkCreateSchema);
        var sample = harness.Builder.AddBulkCreateSample(harness.Company);
        before?.Invoke(harness, sample);
        var editor = harness.Factory.CreateBatchEditor(new BatchEditorRequest([]) { Create = new BulkCreateRequest(kind, sample.NewEmployeesOu) });
        var wizard = new B4Wizard(harness, sample, editor);
        wizard.Check.EditDelay = TimeSpan.Zero;
        if (initialize)
        {
            await editor.InitializeAsync();
        }

        return wizard;
    }

    /// <summary>The check computes from <paramref name="input"/> plus the window's edits, exclusions, pins, created rows and released values.</summary>
    public void Use(BulkCreateInput input)
    {
        Current = input;
        Check.Input = () => Current is { } current
            ? current with
            {
                Edits = Check.Edits,
                ExcludedRows = Check.ExcludedRows,
                PinnedNumbers = Check.PinnedNumbers,
                CreatedRows = Bulk.CreatedRows,
                ReleasedValues = Bulk.ReleasedValues,
            }
            : null;
    }

    /// <summary>Goes to <em>Sprawdzenie</em> and computes.</summary>
    public Task EnterCheckAsync() => Bulk.EnterCheckStepAsync();

    /// <summary>
    /// The operator scenario of 2.11 as steps 1–3 would hand it over: the sheet with its header, the auto-mapping, <em>Konta
    /// numerowane</em> (after the highest number, no reuse of Recycle Bin numbers), suffix <c>contoso.com</c>, the group
    /// <em>GG_Nowi</em>, generated passwords with must-change on.
    /// </summary>
    public BulkCreateInput OperatorInput(BulkAccountOptions? options = null) => new()
    {
        ContainerDn = Sample.NewEmployeesOu,
        Table = TableWithHeaders(OperatorHeaders, OperatorRows),
        Mappings = OperatorMappings,
        Rules = new BulkCreateRules
        {
            Kind = ObjectKind.User,
            Attributes = NumberedRules,
            UpnSuffix = "contoso.com",
            MailDomain = "contoso.com",
            CounterMode = CounterMode.AfterHighest,
            ReuseRecycleBinNumbers = false,
        },
        Options = options ?? new BulkAccountOptions { GroupDns = [Sample.GroupNew] },
        UpnSuffixes = FakeDirectoryBuilder.BulkCreateUpnSuffixes,
    };

    /// <summary>A headerless input (columns A, B …) into <em>Nowi pracownicy</em>.</summary>
    public BulkCreateInput Input(
        string[][] rows,
        IReadOnlyList<ColumnMapping> mappings,
        IReadOnlyList<AttributeRule>? rules = null,
        BulkAccountOptions? options = null,
        ObjectKind kind = ObjectKind.User) => new()
    {
        ContainerDn = Sample.NewEmployeesOu,
        Table = Table(rows),
        Mappings = mappings,
        Rules = new BulkCreateRules
        {
            Kind = kind,
            Attributes = rules ?? NumberedRules,
            UpnSuffix = "contoso.com",
            MailDomain = "contoso.com",
            CounterMode = CounterMode.AfterHighest,
        },
        Options = options ?? new BulkAccountOptions(),
        UpnSuffixes = FakeDirectoryBuilder.BulkCreateUpnSuffixes,
    };

    public static InputTable Table(string[][] rows)
    {
        var width = rows.Length == 0 ? 0 : rows.Max(r => r.Length);
        var columns = Enumerable.Range(0, width).Select(i => new TableColumn(i, TableTextReader.ColumnLetter(i), string.Empty)).ToList();
        return new InputTable(columns, [.. rows.Select((r, i) => new TableRow(i + 1, r))], HasHeader: false, TableSource.Typed, []);
    }

    public static InputTable TableWithHeaders(string[] headers, string[][] rows)
    {
        var columns = headers.Select((h, i) => new TableColumn(i, TableTextReader.ColumnLetter(i), h)).ToList();
        return new InputTable(columns, [.. rows.Select((r, i) => new TableRow(i + 1, r))], HasHeader: true, TableSource.ClipboardText, []);
    }

    /// <summary>Preview → typed confirmation (answered yes) → execution; returns when the results are shown.</summary>
    public async Task PreviewAndExecuteAsync()
    {
        await Editor.PreviewCommand.ExecuteAsync(null);
        Assert.True(Editor.IsPreviewStep, Editor.Error);
        Harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await Editor.ExecuteCommand.ExecuteAsync(null);
        Assert.True(Editor.IsResultsStep, Editor.Error);
    }

    /// <summary>Back from the results to the wizard's check step (the batch editor's <em>Wstecz</em>).</summary>
    public async Task BackToCheckAsync()
    {
        Editor.BackToEditCommand.Execute(null);
        await Check.OnEnteredAsync();
    }

    public void Dispose() => Editor.Dispose();
}

/// <summary>
/// The GC session the connector hands out in these tests: the reads of <see cref="FakeGlobalCatalogReader"/> (the forest as one
/// namespace), every write refused, <see cref="IsDisposed"/> once closed.
/// </summary>
internal sealed class GlobalCatalogSessionDouble(FakeGlobalCatalogReader reader) : IDirectorySession
{
    private int _disposed;

    public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    public SessionInfo Info => reader.Info;

    public Core.Schema.ISchemaCatalog Schema => reader.Schema;

    public IAsyncEnumerable<DirectoryObject> SearchAsync(DirectorySearch search, CancellationToken cancellationToken) => reader.SearchAsync(search, cancellationToken);

    public Task<DirectoryObject?> GetAsync(string distinguishedName, IReadOnlyList<string> attributes, CancellationToken cancellationToken) =>
        reader.GetAsync(distinguishedName, attributes, cancellationToken);

    public IAsyncEnumerable<string> GetAllValuesAsync(string distinguishedName, string attribute, CancellationToken cancellationToken) =>
        reader.GetAllValuesAsync(distinguishedName, attribute, cancellationToken);

    public Task<VirtualListPage> GetVirtualListPageAsync(DirectorySearch search, VirtualListRequest window, CancellationToken cancellationToken) => throw Refused();

    public Task<EffectivePermissions> GetEffectivePermissionsAsync(string distinguishedName, CancellationToken cancellationToken) => throw Refused();

    public Task ModifyAsync(string distinguishedName, IReadOnlyList<AttributeModification> modifications, ModifyOptions? options, CancellationToken cancellationToken) => throw Refused();

    public Task AddAsync(string distinguishedName, IReadOnlyDictionary<string, IReadOnlyList<object>> attributes, CancellationToken cancellationToken) => throw Refused();

    public Task DeleteAsync(string distinguishedName, bool subtree, CancellationToken cancellationToken) => throw Refused();

    public Task<string> MoveAsync(string distinguishedName, string? newParentDn, string? newRdn, CancellationToken cancellationToken) => throw Refused();

    public Task ResetPasswordAsync(string distinguishedName, string newPassword, CancellationToken cancellationToken) => throw Refused();

    public Task RestoreDeletedAsync(string deletedObjectDn, string targetDn, CancellationToken cancellationToken) => throw Refused();

    public Task<IAsyncDisposable?> WatchAsync(string containerDn, Action<string> onChanged, CancellationToken cancellationToken) => Task.FromResult<IAsyncDisposable?>(null);

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private static NotSupportedException Refused() => new("The global catalog session is read-only.");
}
