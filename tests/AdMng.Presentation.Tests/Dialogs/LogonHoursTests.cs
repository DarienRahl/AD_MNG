using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PropertySheets;
using AdMng.Presentation.Services;
using ManualTimeProvider = AdMng.Testing.ManualTimeProvider;

namespace AdMng.Presentation.Tests.Dialogs;

/// <summary>
/// The Logon Hours dialog (grid, selection, keyboard, read-only), the Account tab field that opens it and applies the
/// result through the sheet's change plan, and the batch editor operation "Set logon hours".
/// </summary>
public class LogonHoursTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly DateTimeOffset Winter = new(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Summer = new(2026, 7, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly DayOfWeek[] Workdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
    private static readonly ResxLocalizer Polish = new(CultureInfo.GetCultureInfo("pl-PL"));
    private static readonly ResxLocalizer English = new(CultureInfo.GetCultureInfo("en-US"));

    /// <summary>Monday–Friday 07:00–15:00 UTC: 08:00–16:00 in Warsaw in winter.</summary>
    private static LogonHours OfficeUtc { get; } = Utc((day, hour) => Workdays.Contains(day) && hour is >= 7 and < 15);

    private static LogonHours Utc(Func<DayOfWeek, int, bool> permitted) => LogonHours.FromLocalGrid(LogonHoursGrid.Create(permitted), 0);

    private static LogonHoursViewModel Dialog(LogonHours? hours, ResxLocalizer? localizer = null, DateTimeOffset? now = null, string? readOnlyReason = null) =>
        new(localizer ?? Polish, hours, Warsaw, now ?? Winter, "Jan Kowalski", readOnlyReason);

    private static LogonHoursCellViewModel Cell(LogonHoursViewModel dialog, DayOfWeek day, int hour) =>
        dialog.Days.Single(d => d.Day == day).Cells[hour];

    private static int Selected(LogonHoursViewModel dialog) => dialog.Days.Sum(d => d.Cells.Count(c => c.IsSelected));

    // ------------------------------------------------------------------ dialog

    [Fact]
    public void Polish_StartsOnMonday_English_OnSunday()
    {
        var polish = Dialog(LogonHours.All);
        var english = Dialog(LogonHours.All, English);

        Assert.Equal(["Poniedziałek", "Wtorek", "Środa", "Czwartek", "Piątek", "Sobota", "Niedziela"], polish.Days.Select(d => d.Name));
        Assert.Equal(DayOfWeek.Sunday, english.Days[0].Day);
        Assert.Equal("Sunday", english.Days[0].Name);
        Assert.Equal(24, polish.Hours.Count);
        Assert.All(polish.Days, d => Assert.Equal(24, d.Cells.Count));
        Assert.Equal("Godziny logowania dla: Jan Kowalski", polish.WindowTitle);
        Assert.Equal("Logon Hours for Jan Kowalski", english.WindowTitle);
        Assert.Equal("Godziny logowania", new LogonHoursViewModel(Polish, LogonHours.All, Warsaw, Winter).WindowTitle);
    }

    [Fact]
    public void StoredUtcHours_AreShownInTheLocalTimeOfTheOperator_WithTheCurrentOffset()
    {
        var winter = Dialog(OfficeUtc);
        var summer = Dialog(OfficeUtc, now: Summer);

        Assert.False(Cell(winter, DayOfWeek.Monday, 7).IsPermitted);
        Assert.True(Cell(winter, DayOfWeek.Monday, 8).IsPermitted);
        Assert.True(Cell(winter, DayOfWeek.Friday, 15).IsPermitted);
        Assert.False(Cell(winter, DayOfWeek.Friday, 16).IsPermitted);
        Assert.Equal("pon–pt 8–16", winter.Summary);
        Assert.Contains("UTC+01:00", winter.TimeZoneNote, StringComparison.Ordinal);

        // The DC applies UTC: in summer (UTC+2) the same value is 09:00–17:00 local time, as in ADUC.
        Assert.False(Cell(summer, DayOfWeek.Monday, 8).IsPermitted);
        Assert.True(Cell(summer, DayOfWeek.Monday, 16).IsPermitted);
        Assert.Equal("pon–pt 9–17", summer.Summary);
        Assert.Contains("UTC+02:00", summer.TimeZoneNote, StringComparison.Ordinal);
        Assert.False(winter.HasChanges);
        Assert.Equal(OfficeUtc, winter.Result);
    }

    [Fact]
    public void Dragging_SelectsARectangle_AndDenyAppliesToIt()
    {
        var dialog = Dialog(LogonHours.All);

        dialog.BeginSelection(Cell(dialog, DayOfWeek.Monday, 8), extend: false);
        dialog.ExtendSelection(Cell(dialog, DayOfWeek.Friday, 15));

        Assert.Equal(40, Selected(dialog));
        Assert.Equal("Zaznaczone: Poniedziałek – Piątek, 08:00–16:00 (logowanie dozwolone)", dialog.SelectionText);
        Assert.True(dialog.IsSelectionPermitted);
        Assert.False(dialog.IsSelectionDenied);
        Assert.Same(Cell(dialog, DayOfWeek.Friday, 15), dialog.FocusedCell);

        dialog.DenyCommand.Execute(null);

        Assert.False(Cell(dialog, DayOfWeek.Wednesday, 12).IsPermitted);
        Assert.True(Cell(dialog, DayOfWeek.Wednesday, 16).IsPermitted);
        Assert.True(dialog.IsSelectionDenied);
        Assert.False(dialog.IsSelectionPermitted);
        Assert.True(dialog.HasChanges);
        Assert.Equal("Poniedziałek 08:00–09:00: logowanie zabronione", Cell(dialog, DayOfWeek.Monday, 8).AutomationName);

        // Local 08:00 in winter is 07:00 UTC.
        var result = dialog.Result;
        Assert.False(result.IsPermittedUtc(DayOfWeek.Monday, 7));
        Assert.True(result.IsPermittedUtc(DayOfWeek.Monday, 6));
        Assert.True(result.IsPermittedUtc(DayOfWeek.Monday, 15));
        Assert.Equal(LogonHours.HoursPerWeek - 40, result.PermittedCount);
    }

    [Fact]
    public void Headers_SelectADay_AnHourOnEveryDay_OrEverything()
    {
        var dialog = Dialog(LogonHours.None);

        dialog.SelectDayCommand.Execute(dialog.Days.Single(d => d.Day == DayOfWeek.Saturday));
        Assert.Equal(24, Selected(dialog));
        Assert.Equal("Zaznaczone: Sobota, 00:00–24:00 (logowanie zabronione)", dialog.SelectionText);
        dialog.PermitCommand.Execute(null);
        Assert.Equal("sob 0–24", dialog.Summary);

        dialog.SelectHourCommand.Execute(dialog.Hours[22]);
        Assert.Equal(7, Selected(dialog));
        Assert.All(dialog.Days, d => Assert.True(d.Cells[22].IsSelected));
        Assert.False(dialog.IsSelectionPermitted);
        Assert.False(dialog.IsSelectionDenied);
        Assert.Equal("Zaznaczone: Poniedziałek – Niedziela, 22:00–23:00 (częściowo dozwolone)", dialog.SelectionText);
        dialog.IsSelectionPermitted = true;
        Assert.True(Cell(dialog, DayOfWeek.Tuesday, 22).IsPermitted);

        dialog.SelectAllCommand.Execute(null);
        Assert.Equal(LogonHours.HoursPerWeek, Selected(dialog));
        dialog.IsSelectionDenied = true;
        Assert.True(dialog.IsNonePermitted);
        Assert.Equal("Brak dozwolonych godzin (logowanie zabronione)", dialog.Summary);
        Assert.Equal(LogonHours.None, dialog.Result);
        Assert.False(dialog.HasChanges);
    }

    [Fact]
    public void Keyboard_MovesTheFocus_ExtendsWithShift_AndSpaceSwitches()
    {
        var dialog = Dialog(LogonHours.All);
        Assert.Same(dialog.Days[0].Cells[0], dialog.FocusedCell);
        Assert.True(dialog.FocusedCell.IsFocused);

        Assert.True(dialog.MoveFocus(rows: 0, hours: 1, extend: false));
        Assert.True(dialog.MoveFocus(rows: 1, hours: 0, extend: true));
        Assert.True(dialog.MoveFocus(rows: 0, hours: 1, extend: true));

        Assert.Same(Cell(dialog, DayOfWeek.Tuesday, 2), dialog.FocusedCell);
        Assert.False(dialog.Days[0].Cells[0].IsFocused);
        Assert.Equal(4, Selected(dialog));
        dialog.ToggleCommand.Execute(null);
        Assert.False(Cell(dialog, DayOfWeek.Monday, 1).IsPermitted);
        Assert.False(Cell(dialog, DayOfWeek.Tuesday, 2).IsPermitted);
        dialog.ToggleCommand.Execute(null);
        Assert.True(Cell(dialog, DayOfWeek.Monday, 1).IsPermitted);

        // Edges: the focus stays inside the grid; Home/End jump within the row.
        Assert.False(dialog.MoveFocus(rows: -1, hours: 0, extend: false) && dialog.MoveFocus(rows: -1, hours: 0, extend: false));
        Assert.Same(Cell(dialog, DayOfWeek.Monday, 2), dialog.FocusedCell);
        dialog.MoveFocusToEdge(end: true, extend: false);
        Assert.Same(Cell(dialog, DayOfWeek.Monday, 23), dialog.FocusedCell);
        Assert.False(dialog.MoveFocus(rows: 0, hours: 1, extend: false));
        dialog.MoveFocusToEdge(end: false, extend: true);
        Assert.Equal(24, Selected(dialog));
        Assert.Same(Cell(dialog, DayOfWeek.Monday, 0), dialog.FocusedCell);
    }

    [Fact]
    public void ReadOnly_ShowsTheHours_ButChangesNothing()
    {
        var dialog = Dialog(OfficeUtc, readOnlyReason: "Brak uprawnień.");

        Assert.True(dialog.IsReadOnly);
        Assert.False(dialog.IsEditable);
        Assert.Equal("Brak uprawnień.", dialog.ReadOnlyReason);
        dialog.SelectAllCommand.Execute(null);
        Assert.False(dialog.PermitCommand.CanExecute(null));
        Assert.False(dialog.DenyCommand.CanExecute(null));
        Assert.False(dialog.ToggleCommand.CanExecute(null));
        dialog.IsSelectionPermitted = true;
        dialog.ToggleCommand.Execute(null);

        Assert.False(dialog.HasChanges);
        Assert.Equal(OfficeUtc, dialog.Result);
    }

    [Fact]
    public void InvalidStoredValue_StartsWithAllHours_AndCountsAsAChange()
    {
        var dialog = Dialog(null);

        Assert.All(dialog.Days.SelectMany(d => d.Cells), c => Assert.True(c.IsPermitted));
        Assert.True(dialog.HasChanges);
        Assert.Equal(LogonHours.All, dialog.Result);
        Assert.Equal("Wszystkie godziny", dialog.Summary);

        // The grid cannot show a broken value: the dialog says so (a read-only one would otherwise present it as "all
        // hours"); a valid value - also an absent one - has no note.
        Assert.Equal(
            "Zapisana wartość jest nieprawidłowa (nie ma 21 bajtów), więc siatka nie może jej pokazać – wszystkie godziny są w niej oznaczone jako dozwolone.",
            dialog.InvalidValueNote);
        Assert.NotNull(Dialog(null, readOnlyReason: "Brak uprawnień.").InvalidValueNote);
        Assert.Null(Dialog(LogonHours.All).InvalidValueNote);
        Assert.Null(Dialog(OfficeUtc).InvalidValueNote);
    }

    [Fact]
    public void Summary_GroupsDaysWithTheSameHours_InTheCultureWeekOrder()
    {
        var hours = LogonHours.FromLocalGrid(LogonHoursGrid.Create((day, hour) =>
            (Workdays.Contains(day) && hour is >= 8 and < 16) || (day == DayOfWeek.Saturday && hour is >= 9 and < 13) || (day == DayOfWeek.Sunday && hour == 23)), 1);

        // Sunday is "ndz" (as in the Watchdog summaries), never "nie" ("no").
        Assert.Equal("pon–pt 8–16; sob 9–13; ndz 23–24", LogonHoursText.Describe(hours, Polish, 1));
        Assert.Equal("Sun 23–24; Mon–Fri 8–16; Sat 9–13", LogonHoursText.Describe(hours, English, 1));
        Assert.Equal("All hours", LogonHoursText.Describe(LogonHours.All, English, 1));
        Assert.Equal("UTC+02:00", "UTC" + LogonHoursText.Offset(2));
        Assert.Equal("UTC-05:00", "UTC" + LogonHoursText.Offset(-5));
    }

    // ------------------------------------------------------------------ property sheet field

    [Fact]
    public async Task Field_ShowsTheSummary_AndStagesTheDialogResult()
    {
        var harness = await B2Harness.CreateAsync();
        var jan = harness.Company.JanKowalski;
        harness.Session.SetValues(jan, Attr.LogonHours, OfficeUtc.ToBytes());
        var sheet = harness.Factory.CreatePropertySheet(jan);
        await sheet.InitializeAsync();
        var account = Assert.IsType<FieldsPageViewModel>(Assert.Single(sheet.Pages, p => p.Key == L.Pages.Account));
        var field = Assert.Single(account.Fields.OfType<LogonHoursFieldViewModel>());

        // ADUC order: right after the pre-Windows 2000 logon name (the first section below the account state).
        var first = account.Sections[1].Fields;
        Assert.Equal(first.ToList().FindIndex(f => f.Attribute == Attr.SamAccountName) + 1, first.ToList().IndexOf(field));
        Assert.Equal("Logon hours", field.Label);
        Assert.Equal("Mon–Fri 7–15", field.Summary);
        Assert.False(field.IsReadOnly);
        Assert.False(field.IsDirty);

        // Cancel changes nothing.
        harness.Dialogs.LogonHoursHandler = dialog =>
        {
            dialog.SelectAllCommand.Execute(null);
            dialog.DenyCommand.Execute(null);
            return false;
        };
        await field.EditCommand.ExecuteAsync(null);
        Assert.False(field.IsDirty);

        LogonHoursViewModel? shown = null;
        harness.Dialogs.LogonHoursHandler = dialog =>
        {
            shown = dialog;
            dialog.SelectDayCommand.Execute(dialog.Days.Single(d => d.Day == DayOfWeek.Saturday));
            dialog.PermitCommand.Execute(null);
            return true;
        };
        await field.EditCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.Equal("Logon Hours for Jan Kowalski", shown.WindowTitle);
        Assert.False(shown.IsReadOnly);
        Assert.True(field.IsDirty);
        Assert.True(sheet.HasChanges);
        Assert.Equal("Mon–Fri 7–15; Sat 0–24", field.Summary);

        await sheet.ApplyCommand.ExecuteAsync(null);

        Assert.Null(sheet.ApplyError);
        Assert.False(sheet.HasChanges);
        var expected = Utc((day, hour) => (Workdays.Contains(day) && hour is >= 7 and < 15) || day == DayOfWeek.Saturday);
        Assert.Equal([expected.ToHex().ToUpperInvariant()], harness.Session.GetValues(jan, Attr.LogonHours));
        Assert.Contains(harness.AuditLog.Entries, e => e.Attribute == Attr.LogonHours && e.OldValue == OfficeUtc.ToHex() && e.NewValue == expected.ToHex());
        Assert.DoesNotContain(harness.Dialogs.Shown, s => s.StartsWith("confirm:", StringComparison.Ordinal));
        Assert.False(field.IsDirty);
    }

    [Fact]
    public async Task Field_OkWithoutChanges_OnAnAccountWithoutRestrictions_WritesNothing()
    {
        var harness = await B2Harness.CreateAsync();
        var sheet = harness.Factory.CreatePropertySheet(harness.Company.LukaszWisniewski);
        await sheet.InitializeAsync();
        var field = Assert.Single(sheet.Pages.OfType<FieldsPageViewModel>().SelectMany(p => p.Fields).OfType<LogonHoursFieldViewModel>());
        Assert.Equal("All hours", field.Summary);

        harness.Dialogs.LogonHoursHandler = _ => true;
        await field.EditCommand.ExecuteAsync(null);

        Assert.False(field.IsDirty);
        Assert.False(sheet.HasChanges);
        Assert.Empty(harness.Session.GetValues(harness.Company.LukaszWisniewski, Attr.LogonHours));
    }

    [Fact]
    public async Task Field_IsReadOnly_WithoutThePermission_AndOpensTheDialogReadOnly()
    {
        var harness = await B2Harness.CreateAsync();
        harness.Session.SetPermissions(harness.Company.JanKowalski, new EffectivePermissions
        {
            IsKnown = true,
            WritableAttributes = new HashSet<string>([Attr.Description], StringComparer.OrdinalIgnoreCase),
        });
        var sheet = harness.Factory.CreatePropertySheet(harness.Company.JanKowalski);
        await sheet.InitializeAsync();
        var field = Assert.Single(sheet.Pages.OfType<FieldsPageViewModel>().SelectMany(p => p.Fields).OfType<LogonHoursFieldViewModel>());
        LogonHoursViewModel? shown = null;
        harness.Dialogs.LogonHoursHandler = dialog =>
        {
            shown = dialog;
            return true;
        };

        Assert.True(field.IsReadOnly);
        Assert.True(field.EditCommand.CanExecute(null));
        await field.EditCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.True(shown.IsReadOnly);
        Assert.Equal("You do not have permission to change this value.", shown.ReadOnlyReason);
        Assert.False(field.IsDirty);
        Assert.Empty(field.GetOperations());
    }

    [Fact]
    public async Task Field_ValueChangedByAnotherAdministrator_AsksBeforeOverwriting()
    {
        var harness = await B2Harness.CreateAsync();
        var jan = harness.Company.JanKowalski;
        harness.Session.SetValues(jan, Attr.LogonHours, OfficeUtc.ToBytes());
        var sheet = harness.Factory.CreatePropertySheet(jan);
        await sheet.InitializeAsync();
        var field = Assert.Single(sheet.Pages.OfType<FieldsPageViewModel>().SelectMany(p => p.Fields).OfType<LogonHoursFieldViewModel>());
        harness.Dialogs.LogonHoursHandler = dialog =>
        {
            dialog.SelectAllCommand.Execute(null);
            dialog.PermitCommand.Execute(null);
            return true;
        };
        await field.EditCommand.ExecuteAsync(null);
        harness.Session.SetValues(jan, Attr.LogonHours, LogonHours.None.ToBytes());

        await sheet.ApplyCommand.ExecuteAsync(null);

        Assert.Contains(harness.Dialogs.Shown, s => s.StartsWith("confirm:", StringComparison.Ordinal) && s.Contains("Logon hours", StringComparison.Ordinal));
        Assert.NotNull(sheet.ApplyError);
        Assert.Equal([LogonHours.None.ToHex().ToUpperInvariant()], harness.Session.GetValues(jan, Attr.LogonHours));
    }

    [Fact]
    public async Task Field_InvalidStoredValue_IsReplacedThroughTheSheet_WithoutAConflict()
    {
        var harness = await B2Harness.CreateAsync();
        var jan = harness.Company.JanKowalski;
        harness.Session.SetValues(jan, Attr.LogonHours, new byte[] { 1, 2, 3 });
        var sheet = harness.Factory.CreatePropertySheet(jan);
        await sheet.InitializeAsync();
        var field = Assert.Single(sheet.Pages.OfType<FieldsPageViewModel>().SelectMany(p => p.Fields).OfType<LogonHoursFieldViewModel>());
        Assert.Equal("Invalid value (3 bytes)", field.Summary);
        Assert.False(sheet.HasChanges);

        // OK on the dialog (which starts with every hour permitted) replaces the broken value with a valid one.
        harness.Dialogs.LogonHoursHandler = dialog => dialog.HasChanges;
        await field.EditCommand.ExecuteAsync(null);
        Assert.True(field.IsDirty);
        await sheet.ApplyCommand.ExecuteAsync(null);

        // The sheet compares the plan's old value (hex) with its own read in the same form: no false conflict.
        Assert.Null(sheet.ApplyError);
        Assert.DoesNotContain(harness.Dialogs.Shown, s => s.StartsWith("confirm:", StringComparison.Ordinal));
        Assert.Equal([LogonHours.All.ToHex().ToUpperInvariant()], harness.Session.GetValues(jan, Attr.LogonHours));
        Assert.Contains(harness.AuditLog.Entries, e => e.Attribute == Attr.LogonHours && e.OldValue == "010203" && e.NewValue == LogonHours.All.ToHex());
        Assert.Equal("All hours", field.Summary);
        Assert.False(field.IsDirty);
    }

    [Fact]
    public async Task Field_UsesTheOperatorsTimeZone_AndShowsAnInvalidValue()
    {
        var harness = await B2Harness.CreateAsync();
        var services = new DialogServices(
            harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes,
            new ManualTimeProvider(Summer, Warsaw), harness.Dialogs, harness.Files, harness.Clipboard, harness.Notifications,
            harness.Coordinator, harness.Activities, harness.Lists, harness.Regional);
        var field = new LogonHoursFieldViewModel(new FieldContext(services, harness.Session));

        field.Load(new DirectoryObject("CN=Jan,DC=contoso,DC=test", [new AttributeValues(Attr.LogonHours, [OfficeUtc.ToBytes()])]));
        Assert.Equal("Mon–Fri 9–17", field.Summary);

        field.Load(new DirectoryObject("CN=Jan,DC=contoso,DC=test", [new AttributeValues(Attr.LogonHours, [new byte[] { 1, 2, 3, 4, 5 }])]));
        Assert.Equal("Invalid value (5 bytes)", field.Summary);

        // Read again with another invalid value: the shown text follows although the schedule stays "invalid" (null).
        var summaryChanged = false;
        field.PropertyChanged += (_, e) => summaryChanged |= e.PropertyName == nameof(LogonHoursFieldViewModel.Summary);
        field.Load(new DirectoryObject("CN=Jan,DC=contoso,DC=test", [new AttributeValues(Attr.LogonHours, [new byte[] { 1, 2, 3 }])]));
        Assert.True(summaryChanged);
        Assert.Equal("Invalid value (3 bytes)", field.Summary);
        Assert.False(field.IsDirty);
        Assert.Empty(field.GetOperations());
        LogonHoursViewModel? shown = null;
        harness.Dialogs.LogonHoursHandler = dialog =>
        {
            shown = dialog;
            return true;
        };
        await field.EditCommand.ExecuteAsync(null);
        Assert.Equal("The stored value is invalid (it is not 21 bytes long), so the grid cannot show it – every hour is marked as permitted.", shown?.InvalidValueNote);
        Assert.True(field.IsDirty);
        Assert.Equal(LogonHours.All, Assert.IsType<SetLogonHoursOperation>(Assert.Single(field.GetOperations())).Hours);
    }

    // ------------------------------------------------------------------ batch editor

    [Fact]
    public async Task BatchOperation_PreviewsSummaries_ExecutesAndUndoes()
    {
        var harness = await B2Harness.CreateAsync();
        var company = harness.Company;
        harness.Session.SetValues(company.LukaszWisniewski, Attr.LogonHours, OfficeUtc.ToBytes());
        var editor = harness.Factory.CreateBatchEditor(new BatchEditorRequest([company.JanKowalski, company.LukaszWisniewski, company.Computer1]));
        await editor.InitializeAsync();

        editor.AddOperationCommand.Execute(OperationType.LogonHours);
        var operation = Assert.IsType<LogonHoursEditorViewModel>(editor.Operations[^1]);
        Assert.Equal("Set logon hours", operation.Title);
        Assert.Equal("All hours", operation.Summary);
        harness.Dialogs.LogonHoursHandler = dialog =>
        {
            Assert.Equal("Logon Hours", dialog.WindowTitle);
            dialog.SelectAllCommand.Execute(null);
            dialog.DenyCommand.Execute(null);
            dialog.SelectDayCommand.Execute(dialog.Days.Single(d => d.Day == DayOfWeek.Monday));
            dialog.PermitCommand.Execute(null);
            return true;
        };
        await operation.EditCommand.ExecuteAsync(null);
        Assert.Equal("Mon 0–24", operation.Summary);
        Assert.True(operation.IsValid);

        await editor.PreviewCommand.ExecuteAsync(null);

        // The attribute column names the attribute like the Account tab does, never by its LDAP name.
        Assert.All(editor.PreviewRows.Where(r => r.Before.Length > 0 || r.After.Length > 0), r => Assert.Equal("Logon hours", r.Attribute));

        // An absent value is "no restriction": an empty cell would read like "no hours".
        Assert.Contains(editor.PreviewRows, r => r.Name == "Jan Kowalski" && r.Before == "All hours" && r.After == "Mon 0–24");
        Assert.Contains(editor.PreviewRows, r => r.Name == "Łukasz Wiśniewski" && r.Before == "Mon–Fri 7–15" && r.After == "Mon 0–24");
        await editor.ExecuteCommand.ExecuteAsync(null);

        var monday = Utc((day, _) => day == DayOfWeek.Monday);
        Assert.Equal(2, editor.Result!.SucceededCount);
        Assert.Equal([monday.ToHex().ToUpperInvariant()], harness.Session.GetValues(company.JanKowalski, Attr.LogonHours));
        Assert.Empty(harness.Session.GetValues(company.Computer1, Attr.LogonHours));

        editor.UndoCommand.Execute(null);
        Assert.Contains(editor.PreviewRows, r => r.Name == "Jan Kowalski" && r.Before == "Mon 0–24" && r.After == "All hours");
        await editor.ExecuteCommand.ExecuteAsync(null);
        Assert.Empty(harness.Session.GetValues(company.JanKowalski, Attr.LogonHours));
        Assert.Equal([OfficeUtc.ToHex().ToUpperInvariant()], harness.Session.GetValues(company.LukaszWisniewski, Attr.LogonHours));
    }

    [Fact]
    public async Task BatchOperation_PrefilledFromAnOperation()
    {
        var harness = await B2Harness.CreateAsync();
        var editor = harness.Factory.CreateBatchEditor(new BatchEditorRequest([harness.Company.JanKowalski])
        {
            Operations = [new SetLogonHoursOperation(OfficeUtc)],
        });
        await editor.InitializeAsync();

        var operation = Assert.IsType<LogonHoursEditorViewModel>(Assert.Single(editor.Operations));
        Assert.Equal(OfficeUtc, operation.Hours);
        Assert.Equal("Mon–Fri 7–15", operation.Summary);
    }

    [Fact]
    public void LocalizationKeys_ExistInBothLanguages()
    {
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            Assert.True(English.Contains(DisplayNames.LogonHoursDayKey(day)));
            Assert.True(English.Contains(DisplayNames.LogonHoursShortDayKey(day)));
            Assert.DoesNotContain("[", Polish[DisplayNames.LogonHoursDayKey(day)], StringComparison.Ordinal);
            Assert.DoesNotContain("[", Polish[DisplayNames.LogonHoursShortDayKey(day)], StringComparison.Ordinal);
        }

        Assert.Equal("Ustaw godziny logowania", Polish[DisplayNames.OperationTypeKey(OperationType.LogonHours)]);
        Assert.Equal("Godziny logowania…", Polish[L.Labels.LogonHoursButton]);
    }
}
