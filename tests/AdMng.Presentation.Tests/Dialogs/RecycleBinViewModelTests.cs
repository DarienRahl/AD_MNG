using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Dialogs;

/// <summary>The AD Recycle Bin window end to end: the real planner and executor over the fake DC with the sample company.</summary>
public class RecycleBinViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_DeletedObjects_WithOriginalLocationAndDeletionTime_NewestFirst()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Session.DeleteAsync(harness.Company.JanKowalski, subtree: false, Ct);
        harness.Time.Advance(TimeSpan.FromHours(1));
        await harness.Session.DeleteAsync(harness.Company.SalesOu, subtree: true, Ct);
        using var bin = harness.Factory.CreateRecycleBin();

        await bin.InitializeAsync();

        Assert.Equal("AD Recycle Bin – contoso.test", bin.WindowTitle);
        Assert.True(bin.IsConnected);
        Assert.True(bin.IsRecycleBinEnabled);
        Assert.False(bin.IsRecycleBinDisabled);
        Assert.StartsWith("The AD Recycle Bin is enabled", bin.BannerText, StringComparison.Ordinal);
        Assert.Null(bin.Error);
        Assert.False(bin.IsLoading);
        Assert.False(bin.IsEmpty);
        Assert.Equal(5, bin.Items.Count);
        Assert.Equal("Jan Kowalski", bin.Items[^1].Name);
        var jan = bin.Items[^1];
        Assert.Equal("User", jan.TypeText);
        Assert.Equal("contoso.test/Firma/Pracownicy/IT", jan.OriginalLocation);
        Assert.Equal(harness.Company.ItOu, jan.LastKnownParent);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero).ToString("g", CultureInfo.GetCultureInfo("en-US")), jan.DeletedText);
        var pawel = bin.Items.Single(r => r.Name == "Paweł Lewandowski");
        Assert.Equal("contoso.test/Firma/Pracownicy/Sprzedaż (deleted)", pawel.OriginalLocation);
        Assert.Equal("Organizational Unit", bin.Items.Single(r => r.Name == "Sprzedaż").TypeText);
        Assert.StartsWith("5 objects · ", bin.StatusText, StringComparison.Ordinal);
        Assert.All(bin.Items, r => Assert.Contains("CN=Deleted Objects", r.Dn, StringComparison.Ordinal));
        Assert.All(harness.Session.Operations.Where(o => o.Kind == FakeOperationKind.Search), o => Assert.True(o.Search!.IncludeDeleted));

        bin.SortCommand.Execute(DeletedObjectSort.Name);
        Assert.Equal(["Bartłomiej Kozłowski", "Jan Kowalski", "Paweł Lewandowski", "Sprzedaż", "Żaneta Szymańska"], bin.Items.Select(r => r.Name));
        bin.SortCommand.Execute(DeletedObjectSort.Name);
        Assert.Equal("Żaneta Szymańska", bin.Items[0].Name);
    }

    [Fact]
    public async Task OriginalLocation_FollowsDeletedContainersThatAreNotListed()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Session.DeleteAsync(harness.Company.EmployeesOu, subtree: true, Ct);
        using var bin = harness.Factory.CreateRecycleBin();
        await bin.InitializeAsync();

        bin.Kind = bin.Kinds.Single(k => k.Value == ObjectKind.User);
        await bin.Loading;

        // Neither IT nor Pracownicy is listed (users only): both are read to show the whole original path.
        Assert.Equal(8, bin.Items.Count);
        Assert.Equal("contoso.test/Firma/Pracownicy/IT (deleted)", bin.Items.Single(r => r.Name == "Jan Kowalski").OriginalLocation);
        Assert.Equal("contoso.test/Firma/Pracownicy/Sprzedaż (deleted)", bin.Items.Single(r => r.Name == "Żaneta Szymańska").OriginalLocation);
    }

    [Fact]
    public async Task ServerSideFilters_ReloadTheList()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Session.DeleteAsync(harness.Company.JanKowalski, subtree: false, Ct);
        await harness.Session.DeleteAsync(harness.Company.Laptop, subtree: false, Ct);
        harness.Time.Advance(TimeSpan.FromDays(3));
        await harness.Session.DeleteAsync(harness.Company.LukaszWisniewski, subtree: false, Ct);
        using var bin = harness.Factory.CreateRecycleBin();
        await bin.InitializeAsync();
        Assert.Equal(3, bin.Items.Count);

        bin.Kind = bin.Kinds.Single(k => k.Value == ObjectKind.Computer);
        await bin.Loading;
        Assert.Equal(["KRK-LT001"], bin.Items.Select(r => r.Name));
        Assert.Contains("(objectClass=computer)", LastFilter(harness), StringComparison.Ordinal);

        bin.Kind = bin.Kinds[0];
        bin.DeletedAfter = harness.Time.GetUtcNow().UtcDateTime.Date;
        await bin.Loading;
        Assert.Equal(["Łukasz Wiśniewski"], bin.Items.Select(r => r.Name));
        Assert.Contains("(whenChanged>=20261016000000.0Z)", LastFilter(harness), StringComparison.Ordinal);

        bin.DeletedAfter = null;
        await bin.Loading;
        bin.NameFilter = "kowal";
        await bin.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(["Jan Kowalski"], bin.Items.Select(r => r.Name));

        bin.DeletedFromDn = harness.Company.ItOu;
        await bin.Loading;
        Assert.Equal("contoso.test/Firma/Pracownicy/IT", bin.DeletedFromText);
        Assert.Equal(["Jan Kowalski"], bin.Items.Select(r => r.Name));

        await bin.ClearFiltersCommand.ExecuteAsync(null);
        Assert.Equal(3, bin.Items.Count);
        Assert.Equal(string.Empty, bin.NameFilter);
        Assert.Equal("Any location", bin.DeletedFromText);

        bin.NameFilter = "nikt-taki";
        await B2Harness.WaitUntilAsync(() => bin.IsEmpty);
        Assert.Empty(bin.Items);
    }

    [Fact]
    public async Task Restore_ToTheOriginalLocation_ThroughThePipeline()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Session.DeleteAsync(harness.Company.JanKowalski, subtree: false, Ct);
        await harness.Session.DeleteAsync(harness.Company.Laptop, subtree: false, Ct);
        var published = new List<DirectoryChange>();
        harness.Changes.Changed += (_, e) => published.AddRange(e.Changes);
        using var bin = harness.Factory.CreateRecycleBin();
        await bin.InitializeAsync();
        Assert.False(bin.RestoreCommand.CanExecute(null));

        bin.SelectedItem = bin.Items.Single(r => r.Name == "Jan Kowalski");
        Assert.Equal("1 selected", bin.SelectionText);
        Assert.True(bin.RestoreCommand.CanExecute(null));
        await bin.RestoreCommand.ExecuteAsync(null);

        Assert.True(bin.IsPreviewing);
        var item = Assert.Single(bin.PreviewItems);
        Assert.Equal("Jan Kowalski", item.Name);
        Assert.Equal("contoso.test/Firma/Pracownicy/IT", item.TargetText);
        Assert.Equal(harness.Company.JanKowalski, item.TargetDn);
        Assert.Equal(RowSeverity.None, item.Severity);
        Assert.True(item.CanRestore);
        Assert.Equal("1 of 1 objects will be restored.", bin.PreviewSummary);
        Assert.Equal(string.Empty, bin.PreviewErrorsText);
        Assert.StartsWith("To the original location", bin.PreviewTargetText, StringComparison.Ordinal);
        Assert.Equal("Restore (1)", bin.ConfirmText);
        Assert.False(bin.RestoreCommand.CanExecute(null));
        Assert.True(bin.ConfirmRestoreCommand.CanExecute(null));

        await bin.ConfirmRestoreCommand.ExecuteAsync(null);

        Assert.False(bin.IsPreviewing);
        Assert.True(harness.Session.Exists(harness.Company.JanKowalski));
        Assert.Equal("IT", harness.Value(harness.Company.JanKowalski, Attr.Department));
        Assert.Equal(["KRK-LT001"], bin.Items.Select(r => r.Name));
        Assert.Contains(harness.Notifications.Items, n => n.Severity == NotificationSeverity.Success && n.Message == "Restored from the AD Recycle Bin: 1.");
        var created = Assert.Single(published);
        Assert.Equal(DirectoryChangeKind.Created, created.Kind);
        Assert.Equal(harness.Company.JanKowalski, created.DistinguishedName);
        var audit = Assert.Single(harness.AuditLog.Entries);
        Assert.Equal("Restore", audit.Action);
        Assert.Equal(harness.Company.JanKowalski, audit.DistinguishedName);
        var history = Assert.Single(harness.PowerShell.Entries);
        Assert.Contains("Restore-ADObject -Identity '", history.Script, StringComparison.Ordinal);
        Assert.Equal("Restore from the AD Recycle Bin (1)", history.Title);
        Assert.Single(harness.Activities.Activities);
    }

    [Fact]
    public async Task Restore_AnOuWithItsChildren_ParentFirst()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Session.DeleteAsync(harness.Company.SalesOu, subtree: true, Ct);
        harness.Session.Latency = TimeSpan.FromMilliseconds(10);
        using var bin = harness.Factory.CreateRecycleBin();
        await bin.InitializeAsync();

        // Newest first puts the children before the container; the plan restores the container first anyway.
        bin.SortCommand.Execute(DeletedObjectSort.Type);
        Assert.Equal("Organizational Unit", bin.Items[0].TypeText);
        bin.SortCommand.Execute(DeletedObjectSort.Type);
        Assert.Equal("Organizational Unit", bin.Items[^1].TypeText);
        bin.SelectedItems = bin.Items.ToList();
        Assert.Equal("4 selected", bin.SelectionText);
        await bin.RestoreCommand.ExecuteAsync(null);

        Assert.Equal("Sprzedaż", bin.PreviewItems[0].Name);
        Assert.Equal(0, bin.PreviewItems[0].Level);
        Assert.Equal("contoso.test/Firma/Pracownicy", bin.PreviewItems[0].TargetText);
        Assert.All(bin.PreviewItems.Skip(1), i => Assert.Equal(1, i.Level));
        Assert.All(bin.PreviewItems.Skip(1), i => Assert.Equal(18, i.Indent));
        Assert.All(bin.PreviewItems.Skip(1), i => Assert.Equal("contoso.test/Firma/Pracownicy/Sprzedaż", i.TargetText));
        Assert.Equal("Restore (4)", bin.ConfirmText);

        await bin.ConfirmRestoreCommand.ExecuteAsync(null);

        Assert.True(harness.Session.Exists(harness.Company.SalesOu));
        Assert.True(harness.Session.Exists(harness.Company.PawelLewandowski));
        Assert.True(harness.Session.Exists(harness.Company.ZanetaSzymanska));
        Assert.True(harness.Session.Exists(harness.Company.BartlomiejKozlowski));
        Assert.Contains(harness.Company.ZanetaSzymanska, harness.Values(harness.Company.GroupSales, Attr.Member));
        Assert.Empty(bin.Items);
        Assert.True(bin.IsEmpty);
        Assert.Empty(harness.Dialogs.Errors);
    }

    [Fact]
    public async Task RestoreTo_AnotherContainer_AsksForTheContainer()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Session.DeleteAsync(harness.Company.SalesOu, subtree: true, Ct);
        using var bin = harness.Factory.CreateRecycleBin();
        await bin.InitializeAsync();
        bin.SelectedItem = bin.Items.Single(r => r.Name == "Żaneta Szymańska");

        await bin.RestoreToCommand.ExecuteAsync(null);
        Assert.False(bin.IsPreviewing);

        harness.Dialogs.PickedContainers.Enqueue(harness.Company.AccountingOu);
        await bin.RestoreToCommand.ExecuteAsync(null);
        Assert.Equal("To contoso.test/Firma/Pracownicy/Księgowość. Objects whose container is restored with them go back into it.", bin.PreviewTargetText);
        Assert.Equal("contoso.test/Firma/Pracownicy/Księgowość", Assert.Single(bin.PreviewItems).TargetText);
        await bin.ConfirmRestoreCommand.ExecuteAsync(null);

        Assert.True(harness.Session.Exists("CN=Żaneta Szymańska," + harness.Company.AccountingOu));
        Assert.False(harness.Session.Exists(harness.Company.SalesOu));
    }

    [Fact]
    public async Task ChildOfADeletedContainer_CannotBeRestoredAlone_AndBackReturnsToTheList()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Session.DeleteAsync(harness.Company.SalesOu, subtree: true, Ct);
        using var bin = harness.Factory.CreateRecycleBin();
        await bin.InitializeAsync();
        bin.SelectedItem = bin.Items.Single(r => r.Name == "Paweł Lewandowski");

        await bin.RestoreCommand.ExecuteAsync(null);

        var item = Assert.Single(bin.PreviewItems);
        Assert.Equal(RowSeverity.Error, item.Severity);
        Assert.False(item.CanRestore);
        Assert.Equal(string.Empty, item.TargetText);
        Assert.StartsWith("The original container is deleted as well.", item.Findings, StringComparison.Ordinal);
        Assert.Equal("1 objects cannot be restored – see the findings.", bin.PreviewErrorsText);
        Assert.False(bin.ConfirmRestoreCommand.CanExecute(null));
        Assert.True(bin.BackCommand.CanExecute(null));

        bin.BackCommand.Execute(null);

        Assert.False(bin.IsPreviewing);
        Assert.Empty(bin.PreviewItems);
        Assert.Equal(4, bin.Items.Count);
        Assert.False(harness.Session.Exists(harness.Company.PawelLewandowski));
    }

    [Fact]
    public async Task WithoutRecycleBin_WarnsAndAsksBeforeReanimatingTombstones()
    {
        var harness = await B2Harness.CreateAsync();
        harness.Session.Info = harness.Session.Info with { RecycleBinEnabled = false };
        await harness.Session.DeleteAsync(harness.Company.JanKowalski, subtree: false, Ct);
        using var bin = harness.Factory.CreateRecycleBin();
        await bin.InitializeAsync();

        Assert.False(bin.IsRecycleBinEnabled);
        Assert.True(bin.IsRecycleBinDisabled);
        Assert.StartsWith("The AD Recycle Bin is not enabled in this forest.", bin.BannerText, StringComparison.Ordinal);
        var jan = Assert.Single(bin.Items);
        Assert.Equal("Jan Kowalski", jan.Name);
        bin.SelectedItem = jan;
        await bin.RestoreCommand.ExecuteAsync(null);
        var item = Assert.Single(bin.PreviewItems);
        Assert.Equal(RowSeverity.Warning, item.Severity);
        Assert.Contains("tombstone", item.Findings, StringComparison.Ordinal);

        harness.Dialogs.ConfirmAnswers.Enqueue(false);
        await bin.ConfirmRestoreCommand.ExecuteAsync(null);
        Assert.True(bin.IsPreviewing);
        Assert.False(harness.Session.Exists(harness.Company.JanKowalski));
        Assert.Contains(harness.Dialogs.Shown, s => s.StartsWith("confirm:The AD Recycle Bin is not enabled.", StringComparison.Ordinal));

        harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await bin.ConfirmRestoreCommand.ExecuteAsync(null);
        Assert.True(harness.Session.Exists(harness.Company.JanKowalski));
        Assert.Empty(harness.Values(harness.Company.JanKowalski, Attr.Department));
        Assert.Empty(bin.Items);
    }

    [Fact]
    public async Task FailedRestores_AreReported_AndTheRestStaysInTheBin()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Session.DeleteAsync(harness.Company.SalesOu, subtree: true, Ct);
        using var bin = harness.Factory.CreateRecycleBin();
        await bin.InitializeAsync();
        var sales = bin.Items.Single(r => r.Name == "Sprzedaż");
        bin.SelectedItems = bin.Items.Where(r => r.Name is "Sprzedaż" or "Paweł Lewandowski").ToList();
        await bin.RestoreCommand.ExecuteAsync(null);
        harness.Session.FailNext(FakeOperationKind.RestoreDeleted, sales.Dn, DirectoryErrorKind.AccessDenied);

        await bin.ConfirmRestoreCommand.ExecuteAsync(null);

        var (title, message) = Assert.Single(harness.Dialogs.Errors);
        Assert.Equal("Restore", title);
        Assert.Contains("Failed: 2, succeeded: 0.", message, StringComparison.Ordinal);
        Assert.Contains("• Paweł Lewandowski: Not restored, because its parent container could not be restored.", message, StringComparison.Ordinal);
        Assert.Equal(4, bin.Items.Count);
        Assert.False(bin.IsPreviewing);
    }

    [Fact]
    public async Task NotConnected_ShowsWhy_AndOffersNothing()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Sessions.DisconnectAsync();
        using var bin = harness.Factory.CreateRecycleBin();

        await bin.InitializeAsync();

        Assert.False(bin.IsConnected);
        Assert.Equal("AD Recycle Bin", bin.WindowTitle);
        Assert.Equal("Not connected to a domain.", bin.Error);
        Assert.Equal("Not connected to a domain.", bin.BannerText);
        Assert.False(bin.RefreshCommand.CanExecute(null));
        Assert.False(bin.RestoreCommand.CanExecute(null));
        Assert.False(bin.BrowseDeletedFromCommand.CanExecute(null));
    }

    [Fact]
    public async Task ObjectsDeletedInTheApplication_AppearWithoutRefresh()
    {
        var harness = await B2Harness.CreateAsync();
        using var bin = harness.Factory.CreateRecycleBin();
        await bin.InitializeAsync();
        Assert.True(bin.IsEmpty);

        var outcome = await harness.Coordinator.ApplyAsync(new ChangeRequest([harness.Company.GrazynaDabrowska], [new DeleteOperation()], "Usuń"), Ct);

        Assert.Equal(ChangeOutcome.Executed, outcome);
        await B2Harness.WaitUntilAsync(() => bin.Items.Count == 1);
        Assert.Equal("Grażyna Dąbrowska", bin.Items[0].Name);
    }

    [Fact]
    public async Task Banner_ShowsTheDeletedObjectLifetime()
    {
        var harness = await B2Harness.CreateAsync();
        var configuration = harness.Builder.AddContainer("Configuration", harness.Builder.DomainDn);
        var windowsNt = harness.Builder.AddContainer("Windows NT", harness.Builder.AddContainer("Services", configuration));
        harness.Builder.AddContainer("Directory Service", windowsNt, new Dictionary<string, object?>
        {
            [Attr.TombstoneLifetime] = 180,
            [Attr.MsDsDeletedObjectLifetime] = 365,
        });
        using var bin = harness.Factory.CreateRecycleBin();

        await bin.InitializeAsync();

        Assert.EndsWith("Deleted objects can be restored for 365 days.", bin.BannerText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyDn_AndCulture_AreHandled()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Session.DeleteAsync(harness.Company.SalesOu, subtree: true, Ct);
        using var bin = harness.Factory.CreateRecycleBin();
        await bin.InitializeAsync();
        bin.SelectedItem = bin.Items.Single(r => r.Name == "Sprzedaż");

        bin.CopyDnCommand.Execute(null);
        Assert.Equal(bin.SelectedItem.Dn, harness.Clipboard.Text);

        harness.Localizer.SetCulture(CultureInfo.GetCultureInfo("pl-PL"));

        Assert.Equal("Kosz Active Directory – contoso.test", bin.WindowTitle);
        Assert.Equal("Wszystkie obiekty", bin.Kind.Text);
        Assert.Equal("Jednostka organizacyjna", bin.Items.Single(r => r.Name == "Sprzedaż").TypeText);
        Assert.Equal("contoso.test/Firma/Pracownicy/Sprzedaż (w koszu)", bin.Items.Single(r => r.Name == "Paweł Lewandowski").OriginalLocation);
        Assert.StartsWith("Kosz Active Directory jest włączony", bin.BannerText, StringComparison.Ordinal);
        Assert.Equal("Usunięte obiekty: 4", bin.StatusText);
    }

    [Fact]
    public async Task Dispose_StopsListeningToChanges()
    {
        var harness = await B2Harness.CreateAsync();
        var bin = harness.Factory.CreateRecycleBin();
        await bin.InitializeAsync();
        bin.Dispose();
        var searches = harness.Session.Operations.Count(o => o.Kind == FakeOperationKind.Search);

        harness.Changes.Publish([new DirectoryChange(DirectoryChangeKind.Deleted, harness.Company.JanKowalski)]);
        await Task.Delay(600, Ct);

        Assert.True(bin.IsDisposed);
        Assert.Equal(searches, harness.Session.Operations.Count(o => o.Kind == FakeOperationKind.Search));
    }

    [Fact]
    public void ParentNotRestored_IsTranslated()
    {
        var errors = new ErrorTranslator(new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL")));

        Assert.Equal(
            "Nie przywrócono, ponieważ nie udało się przywrócić kontenera nadrzędnego.",
            errors.TranslateFailure(DirectoryErrorKind.NoSuchObject, BatchExecutor.ParentNotRestoredMessage));
    }

    [Fact]
    public void Polish_UsesNaturalRecycleBinTerms()
    {
        var l = new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"));

        Assert.Equal("Kosz Active Directory", l[L.RecycleBin.WindowTitle]);
        Assert.Equal("Kosz Active Directory...", l[L.Commands.RecycleBin]);
        Assert.Equal("Przywróć", l[L.RecycleBin.Restore]);
        Assert.Equal("Przywróć do...", l[L.RecycleBin.RestoreTo]);
        Assert.Equal("Pierwotna lokalizacja", l[L.RecycleBin.OriginalLocation]);
        Assert.Equal("Usunięto", l[L.RecycleBin.Deleted]);
        Assert.Equal("Przywrócenie", l[DisplayNames.ChangeActionKey(ChangeAction.Restore)]);
        Assert.Equal("W kontenerze docelowym istnieje już obiekt o tej samej nazwie.", l.IssueText(new ValidationIssue(IssueSeverity.Error, IssueCodes.AlreadyExists, "x")));
    }

    private static string LastFilter(B2Harness harness) =>
        harness.Session.Operations.Last(o => o.Kind == FakeOperationKind.Search && o.Search!.Scope == SearchScope.OneLevel).Search!.Filter;
}
