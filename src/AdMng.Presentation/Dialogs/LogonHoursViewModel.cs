using AdMng.Core.ActiveDirectory;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Dialogs;

/// <summary>One hour of one day in the logon hours grid (local time).</summary>
public sealed partial class LogonHoursCellViewModel : ObservableObject
{
    private readonly LogonHoursViewModel _owner;

    internal LogonHoursCellViewModel(LogonHoursViewModel owner, LogonHoursDayViewModel row, int hour)
    {
        _owner = owner;
        Row = row;
        Hour = hour;
    }

    public LogonHoursDayViewModel Row { get; }

    public DayOfWeek Day => Row.Day;

    /// <summary>Hour of the day, 0–23 (the cell covers Hour:00 to Hour+1:00).</summary>
    public int Hour { get; }

    /// <summary>Logon is permitted in this hour.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial bool IsPermitted { get; internal set; }

    [ObservableProperty]
    public partial bool IsSelected { get; internal set; }

    /// <summary>The keyboard focus of the grid is on this cell.</summary>
    [ObservableProperty]
    public partial bool IsFocused { get; internal set; }

    /// <summary>"Poniedziałek 08:00–09:00: logowanie dozwolone".</summary>
    public string AutomationName => _owner.Localizer.Format(
        L.LogonHours.Cell, Row.Name, LogonHoursText.Hour(Hour), LogonHoursText.Hour(Hour + 1),
        _owner.Localizer[IsPermitted ? L.LogonHours.StatePermitted : L.LogonHours.StateDenied]);

    public override string ToString() => AutomationName;
}

/// <summary>A day (row) of the logon hours grid; its header selects the whole day.</summary>
public sealed class LogonHoursDayViewModel
{
    internal LogonHoursDayViewModel(LogonHoursViewModel owner, int index, DayOfWeek day)
    {
        Index = index;
        Day = day;
        Name = LogonHoursText.DayName(owner.Localizer, day);
        SelectText = owner.Localizer.Format(L.LogonHours.SelectDay, Name);
        Cells = [.. Enumerable.Range(0, 24).Select(h => new LogonHoursCellViewModel(owner, this, h))];
    }

    /// <summary>Row number in display order.</summary>
    public int Index { get; }

    public DayOfWeek Day { get; }

    /// <summary>"Poniedziałek".</summary>
    public string Name { get; }

    /// <summary>Automation name of the row header ("Zaznacz cały dzień: Poniedziałek").</summary>
    public string SelectText { get; }

    public IReadOnlyList<LogonHoursCellViewModel> Cells { get; }

    public override string ToString() => Name;
}

/// <summary>An hour (column) of the logon hours grid; its header selects that hour on every day.</summary>
public sealed class LogonHoursHourViewModel
{
    internal LogonHoursHourViewModel(ILocalizer localizer, int hour)
    {
        Hour = hour;
        Label = hour.ToString(localizer.Culture);
        SelectText = localizer.Format(L.LogonHours.SelectHour, LogonHoursText.Hour(hour), LogonHoursText.Hour(hour + 1));
    }

    public int Hour { get; }

    /// <summary>Column header ("8").</summary>
    public string Label { get; }

    /// <summary>Automation name and tooltip of the column header.</summary>
    public string SelectText { get; }

    public override string ToString() => Label;
}

/// <summary>
/// ADUC's "Logon Hours" dialog: a 7 × 24 grid of the week in the operator machine's local time (rotated by the current
/// UTC offset like ADUC, see <see cref="LogonHours.OffsetHours"/>), days in the week order of the UI culture. A
/// rectangular selection is made by dragging (<see cref="BeginSelection"/> / <see cref="ExtendSelection"/>), by the
/// day and hour headers, by the corner (everything) or by the keyboard (<see cref="MoveFocus"/>: arrows, Shift
/// extends; <see cref="ToggleCommand"/>: Space). <see cref="PermitCommand"/> / <see cref="DenyCommand"/> (the
/// radio buttons <see cref="IsSelectionPermitted"/> / <see cref="IsSelectionDenied"/>) set the selected hours.
/// Pure editing: nothing is written here - the caller turns <see cref="Result"/> (UTC) into a change plan.
/// </summary>
public sealed partial class LogonHoursViewModel : ObservableObject
{
    private readonly LogonHours? _original;
    private readonly string? _subject;
    private (int Row, int Hour) _anchor;
    private (int Row, int Hour) _focus;

    /// <param name="localizer">UI texts and the week order.</param>
    /// <param name="hours">The stored schedule (UTC); null for an invalid stored value (the grid starts with all hours).</param>
    /// <param name="timeZone">The operator machine's time zone.</param>
    /// <param name="now">The current time (the offset is the zone's offset at this instant).</param>
    /// <param name="subject">Name of the account for the title; null for the batch editor.</param>
    /// <param name="readOnlyReason">Localized reason when the operator may not change the hours (the dialog only shows them).</param>
    public LogonHoursViewModel(ILocalizer localizer, LogonHours? hours, TimeZoneInfo timeZone, DateTimeOffset now, string? subject = null, string? readOnlyReason = null)
    {
        Localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        ArgumentNullException.ThrowIfNull(timeZone);
        _original = hours;
        _subject = subject;
        ReadOnlyReason = readOnlyReason;
        OffsetHours = LogonHours.OffsetHours(timeZone, now);
        Days = [.. LogonHoursText.Week(localizer.Culture).Select((day, index) => new LogonHoursDayViewModel(this, index, day))];
        Hours = [.. Enumerable.Range(0, 24).Select(h => new LogonHoursHourViewModel(localizer, h))];
        var local = (hours ?? LogonHours.All).ToLocalGrid(OffsetHours);
        foreach (var cell in AllCells)
        {
            cell.IsPermitted = local[cell.Day, cell.Hour];
        }

        Select((0, 0), (0, 0));
    }

    public string WindowTitle => _subject is null
        ? Localizer[L.LogonHours.WindowTitleBatch]
        : Localizer.Format(L.LogonHours.WindowTitle, _subject);

    /// <summary>Localized reason why the hours cannot be changed, or null.</summary>
    public string? ReadOnlyReason { get; }

    /// <summary>
    /// Localized note when the stored value is invalid (not 21 bytes): the grid cannot show it and starts with every hour
    /// permitted - without it a read-only dialog would present a broken value as "all hours". Null otherwise.
    /// </summary>
    public string? InvalidValueNote => _original is null ? Localizer[L.LogonHours.InvalidStored] : null;

    public bool IsReadOnly => ReadOnlyReason is not null;

    public bool IsEditable => !IsReadOnly;

    /// <summary>The whole-hour UTC offset the grid is shown in.</summary>
    public int OffsetHours { get; }

    /// <summary>"Godziny w czasie lokalnym tego komputera (UTC+01:00). …"</summary>
    public string TimeZoneNote => Localizer.Format(L.LogonHours.TimeZoneNote, LogonHoursText.Offset(OffsetHours));

    /// <summary>Rows in the culture's week order.</summary>
    public IReadOnlyList<LogonHoursDayViewModel> Days { get; }

    /// <summary>Columns 0–23.</summary>
    public IReadOnlyList<LogonHoursHourViewModel> Hours { get; }

    /// <summary>The cell with the keyboard focus.</summary>
    public LogonHoursCellViewModel FocusedCell => Days[_focus.Row].Cells[_focus.Hour];

    /// <summary>"Zaznaczone: Poniedziałek – Piątek, 08:00–16:00 (logowanie dozwolone)".</summary>
    [ObservableProperty]
    public partial string SelectionText { get; private set; } = string.Empty;

    /// <summary>One-line summary of the whole schedule ("pon–pt 8–16").</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>No hour is permitted (the account cannot log on at all).</summary>
    [ObservableProperty]
    public partial bool IsNonePermitted { get; private set; }

    /// <summary>The schedule differs from the stored one (always true when the stored value was invalid).</summary>
    [ObservableProperty]
    public partial bool HasChanges { get; private set; }

    /// <summary>Radio button "Logon Permitted": every selected hour is permitted; setting it permits the selection.</summary>
    public bool IsSelectionPermitted
    {
        get => SelectedCells.All(c => c.IsPermitted);
        set
        {
            if (value)
            {
                SetSelection(true);
            }
        }
    }

    /// <summary>Radio button "Logon Denied": every selected hour is denied; setting it denies the selection.</summary>
    public bool IsSelectionDenied
    {
        get => SelectedCells.All(c => !c.IsPermitted);
        set
        {
            if (value)
            {
                SetSelection(false);
            }
        }
    }

    /// <summary>The schedule as stored (UTC).</summary>
    public LogonHours Result
    {
        get
        {
            var grid = new LogonHoursGrid();
            foreach (var cell in AllCells)
            {
                grid[cell.Day, cell.Hour] = cell.IsPermitted;
            }

            return LogonHours.FromLocalGrid(grid, OffsetHours);
        }
    }

    internal ILocalizer Localizer { get; }

    private IEnumerable<LogonHoursCellViewModel> AllCells => Days.SelectMany(d => d.Cells);

    private IEnumerable<LogonHoursCellViewModel> SelectedCells => AllCells.Where(c => c.IsSelected);

    /// <summary>Mouse down on a cell: starts a selection there (or extends it from the anchor with Shift).</summary>
    public void BeginSelection(LogonHoursCellViewModel cell, bool extend)
    {
        ArgumentNullException.ThrowIfNull(cell);
        var position = (cell.Row.Index, cell.Hour);
        Select(extend ? _anchor : position, position);
    }

    /// <summary>The mouse moved over <paramref name="cell"/> with the button pressed: the selection spans anchor to cell.</summary>
    public void ExtendSelection(LogonHoursCellViewModel cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        Select(_anchor, (cell.Row.Index, cell.Hour));
    }

    /// <summary>Arrow keys: moves the focus (Shift extends the selection from the anchor). False at the edge of the grid.</summary>
    public bool MoveFocus(int rows, int hours, bool extend)
    {
        var target = (Row: Math.Clamp(_focus.Row + rows, 0, Days.Count - 1), Hour: Math.Clamp(_focus.Hour + hours, 0, 23));
        if (target == _focus)
        {
            return false;
        }

        Select(extend ? _anchor : target, target);
        return true;
    }

    /// <summary>Home / End: the first or last hour of the focused day.</summary>
    public void MoveFocusToEdge(bool end, bool extend)
    {
        var target = (_focus.Row, end ? 23 : 0);
        Select(extend ? _anchor : target, target);
    }

    /// <summary>Row header: selects the whole day.</summary>
    [RelayCommand]
    private void SelectDay(LogonHoursDayViewModel? day)
    {
        if (day is not null)
        {
            Select((day.Index, 0), (day.Index, 23));
        }
    }

    /// <summary>Column header: selects the hour on every day.</summary>
    [RelayCommand]
    private void SelectHour(LogonHoursHourViewModel? hour)
    {
        if (hour is not null)
        {
            Select((0, hour.Hour), (Days.Count - 1, hour.Hour));
        }
    }

    /// <summary>Corner button / Ctrl+A: selects the whole week.</summary>
    [RelayCommand]
    private void SelectAll() => Select((0, 0), (Days.Count - 1, 23));

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void Permit() => SetSelection(true);

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void Deny() => SetSelection(false);

    /// <summary>Space: the selection gets the opposite state of the focused cell.</summary>
    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void Toggle() => SetSelection(!FocusedCell.IsPermitted);

    private void Select((int Row, int Hour) anchor, (int Row, int Hour) focus)
    {
        FocusedCell.IsFocused = false;
        _anchor = anchor;
        _focus = focus;
        var (rowFrom, rowTo) = (Math.Min(anchor.Row, focus.Row), Math.Max(anchor.Row, focus.Row));
        var (hourFrom, hourTo) = (Math.Min(anchor.Hour, focus.Hour), Math.Max(anchor.Hour, focus.Hour));
        foreach (var cell in AllCells)
        {
            cell.IsSelected = cell.Row.Index >= rowFrom && cell.Row.Index <= rowTo && cell.Hour >= hourFrom && cell.Hour <= hourTo;
        }

        FocusedCell.IsFocused = true;
        OnPropertyChanged(nameof(FocusedCell));
        Refresh();
    }

    private void SetSelection(bool permitted)
    {
        if (IsReadOnly)
        {
            return;
        }

        foreach (var cell in SelectedCells)
        {
            cell.IsPermitted = permitted;
        }

        Refresh();
    }

    /// <summary>Recomputes the texts and states that depend on the cells or the selection.</summary>
    private void Refresh()
    {
        var result = Result;
        Summary = LogonHoursText.Describe(result, Localizer, OffsetHours);
        IsNonePermitted = result.IsNonePermitted;
        HasChanges = _original is null || !result.Equals(_original);
        SelectionText = DescribeSelection();
        OnPropertyChanged(nameof(IsSelectionPermitted));
        OnPropertyChanged(nameof(IsSelectionDenied));
    }

    private string DescribeSelection()
    {
        var (rowFrom, rowTo) = (Math.Min(_anchor.Row, _focus.Row), Math.Max(_anchor.Row, _focus.Row));
        var (hourFrom, hourTo) = (Math.Min(_anchor.Hour, _focus.Hour), Math.Max(_anchor.Hour, _focus.Hour));
        var days = rowFrom == rowTo
            ? Days[rowFrom].Name
            : Localizer.Format(L.LogonHours.DayRange, Days[rowFrom].Name, Days[rowTo].Name);
        var state = IsSelectionPermitted ? L.LogonHours.StatePermitted
            : IsSelectionDenied ? L.LogonHours.StateDenied
            : L.LogonHours.StateMixed;
        return Localizer.Format(L.LogonHours.Selection, days, LogonHoursText.Hour(hourFrom), LogonHoursText.Hour(hourTo + 1), Localizer[state]);
    }
}
