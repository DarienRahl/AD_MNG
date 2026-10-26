using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.AccountReview;

/// <summary>
/// Path U1 ("Wiadomości e-mail do użytkowników"): opens the message window for accounts with the template that fits
/// <c>reason</c> (<see cref="UserMessages.UserMessageRequest.For"/>). The WPF app implements it in its dialog service;
/// without an implementation every "Wyślij wiadomość…" command of the account review, the Account tab and the object
/// lists stays hidden or disabled (<c>CanSendMessages</c> is false).
/// </summary>
public interface IUserMessageLauncher
{
    /// <summary>Opens the composer for the accounts (one message per account).</summary>
    /// <param name="distinguishedNames">The recipients' accounts.</param>
    /// <param name="reason">The check the accounts come from (template suggestion), null from the property sheet.</param>
    Task ShowComposerAsync(IReadOnlyList<string> distinguishedNames, AccountCheck? reason);
}

/// <summary>Columns of the review lists (ids are the binding keys of <see cref="AccountReviewRowViewModel"/>).</summary>
public static class AccountReviewColumns
{
    public const string Name = "Name";
    public const string Login = "Login";
    public const string Location = "Location";
    public const string LockedSince = "LockedSince";
    public const string PasswordExpires = "PasswordExpires";
    public const string PasswordDays = "PasswordDays";
    public const string PasswordLastSet = "PasswordLastSet";
    public const string PasswordAge = "PasswordAge";
    public const string AccountExpires = "AccountExpires";
    public const string AccountDays = "AccountDays";
    public const string LastLogon = "LastLogon";
    public const string LogonDays = "LogonDays";
    public const string ExactLastLogon = "ExactLastLogon";
    public const string Created = "Created";
    public const string Mail = "Mail";
    public const string Manager = "Manager";
    public const string Department = "Department";
    public const string Groups = "Groups";
    public const string Problems = "Problems";
    public const string Description = "Description";
    public const string DistinguishedName = "DistinguishedName";

    /// <summary>Every column id (localization keys <c>AccountReview.Column.{id}</c>).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Name, Login, Location, LockedSince, PasswordExpires, PasswordDays, PasswordLastSet, PasswordAge, AccountExpires,
        AccountDays, LastLogon, LogonDays, ExactLastLogon, Created, Mail, Manager, Department, Groups, Problems, Description,
        DistinguishedName,
    ];

    /// <summary>The columns of a check's list: name, login, what the check is about, location.</summary>
    public static IReadOnlyList<string> For(AccountCheck check)
    {
        IReadOnlyList<string> middle = check switch
        {
            AccountCheck.Locked => [LockedSince, LastLogon, Mail],
            AccountCheck.PasswordExpired or AccountCheck.PasswordExpiring => [PasswordExpires, PasswordDays, PasswordLastSet, Mail],
            AccountCheck.MustChangePassword => [Created, LastLogon, Mail],
            AccountCheck.AccountExpired or AccountCheck.AccountExpiring => [AccountExpires, AccountDays, Manager, Mail],
            AccountCheck.Disabled => [LastLogon, Description],
            AccountCheck.Inactive => [LastLogon, LogonDays, ExactLastLogon, Created],
            AccountCheck.NeverLoggedOn => [Created, ExactLastLogon],
            AccountCheck.PasswordNeverExpires or AccountCheck.PasswordNotRequired or AccountCheck.ReversibleEncryption =>
                [PasswordLastSet, PasswordAge, LastLogon],
            AccountCheck.PasswordOld => [PasswordLastSet, PasswordAge, PasswordExpires],
            AccountCheck.NoMail => [Department, Manager],
            AccountCheck.NoManager => [Department, Mail],
            AccountCheck.ManagerDisabled => [Manager, Mail],
            AccountCheck.DisabledInGroups => [Groups, LastLogon],
            AccountCheck.DisabledInGal => [Mail, LastLogon],
            _ => [Problems, LastLogon, PasswordLastSet],
        };
        return [Name, Login, .. middle, Location];
    }
}

/// <summary>One column of the current list.</summary>
public sealed record AccountReviewColumnViewModel(string Id, string Header);

/// <summary>Texts and sort keys of the review cells in the UI language and the operator's time zone.</summary>
internal sealed class AccountReviewFormatter(ILocalizer localizer, TimeZoneInfo timeZone, AccountReviewParameters parameters)
{
    public ILocalizer Localizer { get; } = localizer;

    public AccountReviewParameters Parameters { get; } = parameters;

    private CultureInfo Culture => Localizer.Culture;

    public string Text(string column, AccountReviewFacts facts)
    {
        var s = facts.Status;
        var obj = facts.Object;
        return column switch
        {
            AccountReviewColumns.Name => obj.GetString(Attr.DisplayName) is { Length: > 0 } display ? display : obj.Name,
            AccountReviewColumns.Login => obj.GetString(Attr.SamAccountName) ?? string.Empty,
            AccountReviewColumns.Location => ParentPath(facts.DistinguishedName),
            AccountReviewColumns.LockedSince => Moment(s.LockedSince),
            AccountReviewColumns.PasswordExpires => PasswordExpires(s),
            AccountReviewColumns.PasswordDays => s.Password is PasswordState.Expires or PasswordState.Expired ? Days(s.PasswordDaysLeft) : string.Empty,
            AccountReviewColumns.PasswordLastSet => s.Password == PasswordState.MustChange ? Localizer[L.AccountReview.MustChangeValue] : Moment(s.PasswordLastSet),
            AccountReviewColumns.PasswordAge => Count(s.PasswordAgeDays),
            AccountReviewColumns.AccountExpires => s.AccountLastDay is { } day ? Localizer.Format(L.AccountReview.EndOfDay, Date(day)) : Localizer[L.AccountReview.Never],
            AccountReviewColumns.AccountDays => Days(s.AccountDaysLeft),
            AccountReviewColumns.LastLogon => s.LastLogon is null ? Localizer[L.AccountReview.Never] : Moment(s.LastLogon),
            AccountReviewColumns.LogonDays => s.DaysSinceLogon is { } d ? Days(-d) : string.Empty,
            AccountReviewColumns.ExactLastLogon => ExactLastLogon(facts),
            AccountReviewColumns.Created => Moment(s.Created),
            AccountReviewColumns.Mail => facts.NotificationAddress ?? string.Empty,
            AccountReviewColumns.Manager => Manager(facts),
            AccountReviewColumns.Department => obj.GetString(Attr.Department) ?? string.Empty,
            AccountReviewColumns.Groups => Groups(facts),
            AccountReviewColumns.Problems => string.Join(", ", AccountReviewQuery.PrivilegedProblemsOf(facts, Parameters).Select(c => Localizer[CheckKey(c)])),
            AccountReviewColumns.Description => obj.GetString(Attr.Description) ?? string.Empty,
            AccountReviewColumns.DistinguishedName => facts.DistinguishedName,
            _ => string.Empty,
        };
    }

    /// <summary>Sort key: numbers and instants sort by value, the rest by text (culture aware, case-insensitive).</summary>
    public IComparable SortKey(string column, AccountReviewFacts facts)
    {
        var s = facts.Status;
        return column switch
        {
            AccountReviewColumns.LockedSince => Ticks(s.LockedSince),
            AccountReviewColumns.PasswordExpires or AccountReviewColumns.PasswordDays => Ticks(s.PasswordExpiresAt),
            AccountReviewColumns.PasswordLastSet or AccountReviewColumns.PasswordAge => Ticks(s.PasswordLastSet),
            AccountReviewColumns.AccountExpires or AccountReviewColumns.AccountDays => s.AccountExpiresAt?.UtcTicks ?? long.MaxValue,
            AccountReviewColumns.LastLogon or AccountReviewColumns.LogonDays => Ticks(s.LastLogon),
            AccountReviewColumns.ExactLastLogon => Ticks(facts.ExactLastLogon),
            AccountReviewColumns.Created => Ticks(s.Created),
            AccountReviewColumns.Groups => facts.Groups.Count,
            _ => Text(column, facts),
        };
    }

    public static string CheckKey(AccountCheck check) => $"AccountReview.Check.{check}";

    public static string CheckHintKey(AccountCheck check) => $"AccountReview.Hint.{check}";

    public static string AreaKey(AccountCheckArea area) => $"AccountReview.Area.{area}";

    public static string ColumnKey(string column) => $"AccountReview.Column.{column}";

    /// <summary>"today", "tomorrow", "in 5 days", "3 days ago".</summary>
    public string Days(int? days) => days switch
    {
        null => string.Empty,
        0 => Localizer[L.AccountReview.Today],
        1 => Localizer[L.AccountReview.Tomorrow],
        -1 => Localizer[L.AccountReview.Yesterday],
        > 1 => Localizer.Format(L.AccountReview.InDays, days.Value.ToString("N0", Culture)),
        _ => Localizer.Format(L.AccountReview.DaysAgo, (-days.Value).ToString("N0", Culture)),
    };

    /// <summary>"1 day" / "120 days".</summary>
    public string Count(int? days) => days switch
    {
        null => string.Empty,
        1 => Localizer[L.AccountReview.OneDay],
        _ => Localizer.Format(L.AccountReview.DayCount, days.Value.ToString("N0", Culture)),
    };

    public string Moment(DateTimeOffset? instant) =>
        instant is { } value ? TimeZoneInfo.ConvertTime(value, timeZone).ToString("g", Culture) : string.Empty;

    public string Date(DateOnly day) => day.ToString("d", Culture);

    private static long Ticks(DateTimeOffset? instant) => instant?.UtcTicks ?? long.MinValue;

    private static string ParentPath(string dn) =>
        Core.Ldap.DistinguishedName.TryParse(dn, out var parsed) && parsed.Parent is { IsRoot: false } parent
            ? AddressResolver.ToDisplayPath(parent.ToString())
            : string.Empty;

    private static string NameOf(string dn) =>
        Core.Ldap.DistinguishedName.TryParse(dn, out var parsed) && !parsed.IsRoot ? parsed.Name : dn;

    private string PasswordExpires(AccountStatus s) => s.Password switch
    {
        PasswordState.Expires or PasswordState.Expired when s.PasswordExpiresAt is { } at => s.IsPasswordExpiryEstimated
            ? Localizer.Format(L.AccountReview.Estimated, Moment(at))
            : Moment(at),
        PasswordState.Expired => Localizer[L.AccountReview.PasswordExpiredValue],
        PasswordState.NeverExpires => Localizer[L.AccountReview.Never],
        PasswordState.MustChange => Localizer[L.AccountReview.MustChangeValue],
        _ => Localizer[L.Common.Unknown],
    };

    private string ExactLastLogon(AccountReviewFacts facts)
    {
        if (!facts.HasExactLastLogon)
        {
            return string.Empty;
        }

        return facts.ExactLastLogon is { } exact
            ? Localizer.Format(L.AccountReview.ExactOn, Moment(exact), facts.ExactLastLogonServer ?? string.Empty)
            : Localizer[L.AccountReview.Never];
    }

    private string Manager(AccountReviewFacts facts)
    {
        if (facts.ManagerDn is not { } manager)
        {
            return string.Empty;
        }

        var name = NameOf(manager);
        return facts.IsManagerDisabled == true ? Localizer.Format(L.AccountReview.ManagerDisabledValue, name) : name;
    }

    private string Groups(AccountReviewFacts facts)
    {
        if (facts.Groups.Count == 0)
        {
            return string.Empty;
        }

        var count = facts.HasMoreGroups ? $"{facts.Groups.Count.ToString("N0", Culture)}+" : facts.Groups.Count.ToString("N0", Culture);
        var names = string.Join(", ", facts.Groups.Take(3).Select(NameOf));
        return facts.Groups.Count > 3 ? $"{count}: {names}, …" : $"{count}: {names}";
    }
}

/// <summary>One account in the review lists (it may be a finding of several checks).</summary>
public sealed class AccountReviewRowViewModel : ObservableObject
{
    private AccountReviewFormatter _formatter;

    internal AccountReviewRowViewModel(AccountReviewFacts facts, IReadOnlySet<AccountCheck> checks, AccountReviewFormatter formatter)
    {
        Facts = facts;
        Checks = checks;
        _formatter = formatter;
    }

    public AccountReviewFacts Facts { get; private set; }

    /// <summary>The checks this account is a finding of.</summary>
    public IReadOnlySet<AccountCheck> Checks { get; private set; }

    public string Dn => Facts.DistinguishedName;

    public string Name => this[AccountReviewColumns.Name];

    public ObjectIcon Icon => ObjectIcons.For(Facts.Object.Kind, Facts.Status.IsDisabled, Facts.Status.IsLocked);

    /// <summary>Text of a column (<see cref="AccountReviewColumns"/>); bound as <c>[ColumnId]</c>.</summary>
    public string this[string column] => _formatter.Text(column, Facts);

    internal IComparable SortKey(string column) => _formatter.SortKey(column, Facts);

    internal void Update(AccountReviewFacts facts, IReadOnlySet<AccountCheck> checks)
    {
        Facts = facts;
        Checks = checks;
        RaiseAll();
    }

    internal void UseFormatter(AccountReviewFormatter formatter)
    {
        _formatter = formatter;
        RaiseAll();
    }

    private void RaiseAll()
    {
        OnPropertyChanged("Item[]");
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(Facts));
    }
}

/// <summary>A tile: one check with its number of findings; selecting it shows its list.</summary>
public sealed partial class AccountCheckTileViewModel(AccountCheck check) : ObservableObject
{
    public AccountCheck Check { get; } = check;

    public AccountCheckArea Area => AccountReviewQuery.AreaOf(Check);

    public AccountCheckSeverity Severity => AccountReviewQuery.SeverityOf(Check);

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AreaTitle { get; set; } = string.Empty;

    /// <summary>What the check looks at (tooltip).</summary>
    [ObservableProperty]
    public partial string Hint { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFindings))]
    public partial int Count { get; private set; }

    /// <summary>The count is final (false while the run streams or resolves managers).</summary>
    [ObservableProperty]
    public partial bool IsComplete { get; private set; }

    /// <summary>The count in the UI language, with "…" while it is not final.</summary>
    [ObservableProperty]
    public partial string CountText { get; private set; } = "0";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public bool HasFindings => Count > 0;

    internal void SetCount(int count, bool complete, CultureInfo culture)
    {
        Count = count;
        IsComplete = complete;
        var text = count.ToString("N0", culture);
        CountText = complete ? text : $"{text}…";
    }
}

/// <summary>A check in the parameters: run it or not.</summary>
public sealed partial class AccountCheckOptionViewModel(AccountCheck check) : ObservableObject
{
    public AccountCheck Check { get; } = check;

    public AccountCheckArea Area => AccountReviewQuery.AreaOf(Check);

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AreaTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;
}
